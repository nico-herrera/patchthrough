using System.Text.Json;
using System.Text.Json.Nodes;

namespace Patchthrough.Core;

/// <summary>One audio track of a session, and who it represents.</summary>
public sealed record Track(string Key, string File, string Speaker, int OffsetMs);

/// <summary>
/// meta.json. The macOS app writes the same keys from RecordingSession.swift,
/// and `files` is what names the audio, so a Windows recorder can write any
/// container. See schemas/session-v1.md.
/// </summary>
public sealed class SessionMeta
{
    public required DateTimeOffset Started { get; init; }
    public DateTimeOffset? Ended { get; init; }
    public required int DurationSeconds { get; init; }
    public required bool CleanStop { get; init; }
    public required Dictionary<string, string> Files { get; init; }
    public required Dictionary<string, int> StartOffsetMs { get; init; }
    public string? Name { get; init; }

    /// <summary>
    /// The instant every transcript timestamp is measured from: the first audio
    /// buffer of whichever track delivered one first.
    ///
    /// This is not the same as <see cref="Started"/>, and the difference is not
    /// small. `Started` is stamped before the capture devices are opened, and
    /// opening them takes a variable amount of time. Anything converting a
    /// wall-clock instant into transcript time has to subtract this value, not
    /// `Started`, or it lands late by that latency. Notes are the thing that
    /// converts, so this is what makes a note point at the line it was about.
    ///
    /// Null on a session recorded before this key existed. Falling back to
    /// `Started` is documented as approximate in schemas/session-v1.md.
    /// </summary>
    public DateTimeOffset? AudioStart { get; init; }

    /// <summary>
    /// The tracks in the order the coordinator transcribes them. The mic is
    /// `me` and the system track is `them`, exactly as on macOS.
    /// </summary>
    public IEnumerable<Track> Tracks()
    {
        if (Files.TryGetValue("mic", out var mic))
        {
            yield return new Track("mic", mic, "me", StartOffsetMs.GetValueOrDefault("mic"));
        }
        if (Files.TryGetValue("system", out var system))
        {
            yield return new Track("system", system, "them", StartOffsetMs.GetValueOrDefault("system"));
        }
    }

    /// <summary>
    /// Serialize with sorted keys and two-space indentation, which is what
    /// Swift's `[.prettyPrinted, .sortedKeys]` produces. The order costs
    /// nothing and keeps a Windows session diffable against a macOS one.
    /// </summary>
    public string ToJson()
    {
        var files = new JsonObject();
        foreach (var pair in Files.OrderBy(p => p.Key, StringComparer.Ordinal)) files[pair.Key] = pair.Value;

        var offsets = new JsonObject();
        foreach (var pair in StartOffsetMs.OrderBy(p => p.Key, StringComparer.Ordinal)) offsets[pair.Key] = pair.Value;

        // Added in alphabetical order, because System.Text.Json writes
        // properties in insertion order and has no sorting option.
        var root = new JsonObject();
        if (AudioStart is not null) root["audio_start"] = Iso8601Millis(AudioStart.Value);
        root["clean_stop"] = CleanStop;
        root["duration_seconds"] = DurationSeconds;
        if (Ended is not null) root["ended"] = Iso8601(Ended.Value);
        root["files"] = files;
        if (!string.IsNullOrWhiteSpace(Name)) root["name"] = Name;
        root["start_offset_ms"] = offsets;
        root["started"] = Iso8601(Started);

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public void Write(string sessionDirectory) =>
        AtomicFile.WriteText(Path.Combine(sessionDirectory, "meta.json"), ToJson());

    public static SessionMeta Read(string sessionDirectory)
    {
        var path = Path.Combine(sessionDirectory, "meta.json");
        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
        {
            throw new JsonException($"can't parse {path}");
        }

        var files = new Dictionary<string, string>();
        if (root["files"] is JsonObject fileMap)
        {
            foreach (var pair in fileMap)
            {
                if (pair.Value is JsonValue value && value.TryGetValue(out string? track) && track is not null)
                {
                    files[pair.Key] = track;
                }
            }
        }
        if (files.Count == 0) throw new JsonException($"can't parse {path}: no \"files\" map");

        // Sessions recorded before offsets existed default to zero. The tracks
        // start within tens of milliseconds of each other anyway.
        var offsets = new Dictionary<string, int>();
        if (root["start_offset_ms"] is JsonObject offsetMap)
        {
            foreach (var pair in offsetMap)
            {
                if (pair.Value is JsonValue value && value.TryGetValue(out int ms)) offsets[pair.Key] = ms;
            }
        }

        var name = root["name"] is JsonValue nameValue && nameValue.TryGetValue(out string? title)
            ? title?.Trim()
            : null;

        return new SessionMeta
        {
            Started = ReadDate(root["started"]) ?? DateTimeOffset.UnixEpoch,
            Ended = ReadDate(root["ended"]),
            DurationSeconds = root["duration_seconds"] is JsonValue d && d.TryGetValue(out int secs) ? secs : 0,
            CleanStop = root["clean_stop"] is not JsonValue c || !c.TryGetValue(out bool clean) || clean,
            Files = files,
            StartOffsetMs = offsets,
            Name = string.IsNullOrEmpty(name) ? null : name,
            AudioStart = ReadDate(root["audio_start"]),
        };
    }

    /// <summary>
    /// Name or rename a session, or remove the name with null.
    ///
    /// This edits the file in place instead of reading a
    /// <see cref="SessionMeta"/> and writing it back. A round trip through this
    /// class keeps only the keys it models, and a macOS-written meta.json can
    /// carry keys a Windows build has never heard of, `audio_start` among them.
    /// Renaming a meeting must not silently drop them.
    /// </summary>
    public static void UpdateName(string sessionDirectory, string? name)
    {
        var path = Path.Combine(sessionDirectory, "meta.json");
        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
        {
            throw new JsonException($"can't parse {path}");
        }

        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) root.Remove("name");
        else root["name"] = trimmed;

        // Rebuilt in alphabetical order for the same reason ToJson inserts in
        // that order: System.Text.Json writes insertion order, and a Windows
        // session should stay diffable against a macOS one.
        var sorted = new JsonObject();
        foreach (var pair in root.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sorted[pair.Key] = pair.Value?.DeepClone();
        }

        AtomicFile.WriteText(path, sorted.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static DateTimeOffset? ReadDate(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text)
            && DateTimeOffset.TryParse(text, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Second precision in UTC, which is what Swift's ISO8601DateFormatter
    /// writes by default.
    /// </summary>
    internal static string Iso8601(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Millisecond precision, in UTC. The audio anchor needs it: device startup
    /// latency has been measured between 0.19 and 1.64 seconds, so a value rounded
    /// to the second would throw away most of what this key exists to record.
    /// </summary>
    internal static string Iso8601Millis(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
