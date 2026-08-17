using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Patchthrough.Core;

/// <summary>
/// What a session directory holds right now.
///
/// The five states mirror SessionStore.Item.Status in
/// Sources/patchthrough/UI/PatchthroughWindow.swift. Two of them exist because
/// a directory alone cannot tell them apart: a live recording and a session
/// queued for transcription both hold meta.json and no transcript, and a
/// finished session that found no speech looks like unfinished work. Read
/// <see cref="SessionIndex.Scan"/> for how each one is decided.
/// </summary>
public enum SessionStatus
{
    /// <summary>The caller says this directory is being recorded into now.</summary>
    Recording,

    /// <summary>Transcribed, with at least one spoken line. The only state a handoff can use.</summary>
    Ready,

    /// <summary>Recorded, not transcribed yet. Transcription can still run.</summary>
    Pending,

    /// <summary>Transcription finished and found no speech. Finished work, not work in progress.</summary>
    Empty,

    /// <summary>No meta.json. The recording was interrupted before it wrote one.</summary>
    Broken,
}

/// <summary>
/// One session, as a list needs it. Fields a status cannot supply stay at their
/// zero value: a <see cref="SessionStatus.Pending"/> session has no word count,
/// and reporting 0 words for it would be a claim rather than an absence.
/// </summary>
public sealed record SessionListing(
    string Directory,
    string Id,
    SessionStatus Status,
    string? Name,
    DateTimeOffset? StartedAt,
    int DurationSeconds,
    bool CleanStop,
    int Words,
    string? FirstLine)
{
    /// <summary>The name the user gave the meeting, or the folder timestamp.</summary>
    public string DisplayTitle => string.IsNullOrEmpty(Name) ? Id : Name;
}

/// <summary>
/// Reads a recordings root into a list of sessions.
///
/// This is the read model behind any session list. It replaces neither
/// <see cref="TranscriptionPipeline.Pending"/> nor
/// <see cref="TranscriptionPipeline.MissingHandoffs"/>: both of those filter for
/// work to do, and a list has to show sessions no worker will ever touch again.
///
/// Nothing here throws on a damaged session. A session that cannot be read is a
/// row that says so, because one corrupt directory must not empty the list.
/// </summary>
public static class SessionIndex
{
    /// <summary>The folder name format, shared with the macOS recorder.</summary>
    private const string FolderFormat = "yyyy.MM.dd-HHmm";

    /// <summary>
    /// A spoken line in transcript.md. The same expression lives in
    /// cli/src/patchthrough.js (`segmentLines`), and it has to keep matching
    /// what <see cref="Transcript.Render"/> writes.
    /// </summary>
    private static readonly Regex SpokenLine = new(
        @"^\*\*\[[^\]]+\]\s+[^:]+:\*\*\s*(?<text>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Every session under <paramref name="root"/>, newest first.
    ///
    /// <paramref name="liveSessionId"/> is the folder name currently being
    /// recorded into, which only the caller knows. Without it a live recording
    /// reads as <see cref="SessionStatus.Pending"/>, because that is what it
    /// looks like on disk.
    /// </summary>
    public static IReadOnlyList<SessionListing> Scan(string root, string? liveSessionId = null)
    {
        if (!System.IO.Directory.Exists(root)) return [];

        return System.IO.Directory.GetDirectories(root)
            .Select(dir => Read(dir, liveSessionId))
            // Descending by folder name. The format sorts chronologically as
            // text, which is why it was chosen, and Ordinal keeps a machine's
            // locale out of the order.
            .OrderByDescending(listing => listing.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// One session directory. The order of the checks is the whole contract:
    /// the live session first, then a usable transcript, then the completion
    /// marker, then any recording at all.
    /// </summary>
    public static SessionListing Read(string directory, string? liveSessionId = null)
    {
        var id = new DirectoryInfo(directory).Name;
        var meta = ReadMetaLoosely(directory);
        var startedAt = ParseFolderStamp(id) ?? meta.Started;

        // The live session is checked before the files, because its files are
        // still being written and say nothing useful yet.
        if (id == liveSessionId)
        {
            return new SessionListing(
                directory, id, SessionStatus.Recording, meta.Name, startedAt,
                meta.DurationSeconds, CleanStop: true, Words: 0, FirstLine: null);
        }

        var transcript = Path.Combine(directory, "transcript.md");
        if (File.Exists(transcript))
        {
            var (words, firstLine) = ReadSpoken(transcript);
            // A transcript.md with no spoken line is not a transcript. The
            // macOS app reaches the same conclusion through
            // Handoff.resolveSession, which refuses an empty one.
            if (firstLine is not null)
            {
                return new SessionListing(
                    directory, id, SessionStatus.Ready, meta.Name, startedAt,
                    meta.DurationSeconds, meta.CleanStop, words, firstLine);
            }
        }

        // transcript.json is the completion marker. Present, with no usable
        // transcript beside it, means transcription ran and found no speech.
        // Calling that pending leaves a row reading "Transcribing" forever.
        var status = File.Exists(Path.Combine(directory, "transcript.json"))
            ? SessionStatus.Empty
            : File.Exists(Path.Combine(directory, "meta.json"))
                ? SessionStatus.Pending
                : SessionStatus.Broken;

        return new SessionListing(
            directory, id, status, meta.Name, startedAt,
            meta.DurationSeconds, meta.CleanStop, Words: 0, FirstLine: null);
    }

    /// <summary>
    /// The word count and the first spoken line of a transcript.
    /// A null first line means the file holds no spoken line at all.
    /// </summary>
    private static (int Words, string? FirstLine) ReadSpoken(string transcriptPath)
    {
        string[] lines;
        try { lines = File.ReadAllLines(transcriptPath); }
        catch (IOException) { return (0, null); }
        catch (UnauthorizedAccessException) { return (0, null); }

        var words = 0;
        string? first = null;
        foreach (var line in lines)
        {
            var match = SpokenLine.Match(line);
            if (!match.Success) continue;
            var text = match.Groups["text"].Value;
            words += text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            first ??= text;
        }
        // A spoken line with no words after it still proves the file is a
        // transcript, so an empty string is a result and null is not.
        return (words, first);
    }

    /// <summary>
    /// meta.json, read for display and never for correctness. Missing keys and
    /// malformed JSON both degrade to defaults, because a list row that says
    /// "no duration" is better than a list that refuses to load.
    /// <see cref="SessionMeta.Read"/> stays the strict reader for the pipeline.
    /// </summary>
    private static (string? Name, DateTimeOffset? Started, int DurationSeconds, bool CleanStop)
        ReadMetaLoosely(string directory)
    {
        JsonObject? root = null;
        try
        {
            var path = Path.Combine(directory, "meta.json");
            if (File.Exists(path)) root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception) { root = null; }
        if (root is null) return (null, null, 0, true);

        var name = root["name"] is JsonValue nameValue && nameValue.TryGetValue(out string? title)
            ? title?.Trim()
            : null;

        var started = root["started"] is JsonValue startedValue
            && startedValue.TryGetValue(out string? stamp)
            && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : (DateTimeOffset?)null;

        var duration = root["duration_seconds"] is JsonValue durationValue
            && durationValue.TryGetValue(out int seconds)
            ? seconds
            : 0;

        // Absent means a clean stop, matching SessionMeta.Read and the macOS app.
        var cleanStop = root["clean_stop"] is not JsonValue cleanValue
            || !cleanValue.TryGetValue(out bool clean)
            || clean;

        return (string.IsNullOrEmpty(name) ? null : name, started, duration, cleanStop);
    }

    /// <summary>
    /// The instant in the folder name, which is local time on the machine that
    /// recorded it. This is preferred over meta.json's `started` because a
    /// list groups by calendar day, and the folder name is what the user sees.
    /// Anything that is not a session folder returns null.
    /// </summary>
    private static DateTimeOffset? ParseFolderStamp(string id)
    {
        // Only the first two dash-separated parts are the timestamp, so a
        // folder that grew a suffix still parses. Same slice as the macOS app.
        var parts = id.Split('-');
        if (parts.Length < 2) return null;
        var stamp = $"{parts[0]}-{parts[1]}";
        return DateTime.TryParseExact(
            stamp, FolderFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed))
            : null;
    }
}
