using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Patchthrough.Core;

/// <summary>One note, as it was typed.</summary>
/// <param name="At">
/// When the user committed it, on the same wall clock as `audio_start` in
/// meta.json. An absolute instant, never an offset. See the class summary.
/// </param>
public sealed record Note(DateTimeOffset At, string Text);

/// <summary>
/// One note placed on the transcript's clock.
/// </summary>
/// <param name="OffsetMs">
/// Milliseconds from the transcript's zero, or null when the session carries no
/// usable anchor. Null means "render this without a timestamp", never "render it at
/// zero": a note claiming 0:00 when its position is unknown would send a reader to
/// the opening line of a meeting it has nothing to do with.
/// </param>
public sealed record ResolvedNote(int? OffsetMs, string Text)
{
    /// <summary>The label, or null when this note has no position.</summary>
    public string? Clock => OffsetMs is null ? null : Transcript.Clock(OffsetMs.Value);
}

/// <summary>
/// Notes the user typed while a meeting was recording, stored beside the audio as
/// `notes.json`.
///
/// These are the user's own words. Nothing generates, rewrites, or summarizes them:
/// they ride into handoff.md verbatim, above the transcript, so the receiving agent
/// knows which minutes a human thought mattered. That is the whole feature. The
/// transcript says what was said; the notes say what landed.
///
/// **Timestamps are absolute wall clock, never offsets.** The transcript's zero is
/// `audio_start`, and that value is not final until the recording stops: a track
/// that never delivers a buffer falls back to the session start, which moves the
/// zero after notes may already exist. A note that stored "2:14" at typing time
/// would bake in whichever zero happened to be current. Storing the instant lets
/// every note re-resolve against the final anchor, however many times it moves.
///
/// Read docs/notes-and-the-recording-clock.md before changing anything here. The
/// macOS counterpart is Sources/patchthrough/SessionNotes.swift, and the two write
/// the same file.
/// </summary>
public static class SessionNotes
{
    public const int SchemaVersion = 1;

    public const string FileName = "notes.json";

    /// <summary>
    /// Read the notes. An empty list is the normal state: it covers every session
    /// recorded before this shipped and every meeting where the user typed nothing.
    ///
    /// A damaged file reads as empty rather than throwing. Notes are an addition to
    /// a session, and losing the handoff because a side file got corrupted would be
    /// a worse failure than losing the notes.
    /// </summary>
    public static IReadOnlyList<Note> Read(string sessionDirectory)
    {
        JsonArray? entries;
        try
        {
            var path = Path.Combine(sessionDirectory, FileName);
            if (!File.Exists(path)) return [];
            entries = (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?["notes"] as JsonArray;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
        if (entries is null) return [];

        var notes = new List<Note>();
        foreach (var node in entries)
        {
            if (node is not JsonObject entry) continue;
            var text = entry["text"] is JsonValue textValue && textValue.TryGetValue(out string? typed)
                ? typed
                : null;
            var stamp = entry["at"] is JsonValue atValue && atValue.TryGetValue(out string? at) ? at : null;
            if (text is null || stamp is null) continue;
            if (!DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
            {
                continue;
            }
            notes.Add(new Note(when, text));
        }
        return notes;
    }

    /// <summary>
    /// Append one note and flush.
    ///
    /// Read-modify-write is safe here in a way it is not for meta.json: this file has
    /// exactly one writer, the interface thread, and nothing in the record path
    /// touches it.
    ///
    /// Flushing on every note rather than at stop is deliberate. A recording can run
    /// for an hour, and a crash in that hour must not take the user's typing with it.
    /// </summary>
    public static void Append(string sessionDirectory, string text, DateTimeOffset? at = null)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return;

        var notes = Read(sessionDirectory).ToList();
        notes.Add(new Note(at ?? DateTimeOffset.Now, trimmed));
        Write(sessionDirectory, notes);
    }

    public static void Write(string sessionDirectory, IReadOnlyList<Note> notes)
    {
        var entries = new JsonArray();
        foreach (var note in notes)
        {
            // Keys in alphabetical order, matching every other file in a session so
            // a Windows-written one stays diffable against a macOS-written one.
            entries.Add(new JsonObject
            {
                ["at"] = SessionMeta.Iso8601Millis(note.At),
                ["text"] = note.Text,
            });
        }

        var root = new JsonObject
        {
            ["notes"] = entries,
            ["schema_version"] = SchemaVersion,
        };
        AtomicFile.WriteText(
            Path.Combine(sessionDirectory, FileName),
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Read the notes and place them on the same clock transcript.md uses.
    ///
    /// The anchor is `audio_start`, the first audio buffer of whichever track
    /// delivered one first, which is exactly what every transcript timestamp is
    /// measured from. `started` is the fallback for sessions written before that key
    /// existed. It is stamped before the capture devices are opened, so it is the
    /// earlier instant and subtracting it overshoots: those notes read later in the
    /// transcript than the moment they refer to, by the device startup latency.
    /// </summary>
    public static IReadOnlyList<ResolvedNote> Resolved(string sessionDirectory)
    {
        var notes = Read(sessionDirectory);
        if (notes.Count == 0) return [];

        var anchor = Anchor(sessionDirectory);
        return notes
            .Select(note =>
            {
                if (anchor is null) return new ResolvedNote(null, note.Text);
                // Clamped. The window is live before the audio devices finish
                // opening, so a note genuinely can predate the first buffer. It
                // belongs at the start of the transcript, not before it.
                var offset = (int)Math.Round((note.At - anchor.Value).TotalMilliseconds);
                return new ResolvedNote(Math.Max(0, offset), note.Text);
            })
            .OrderBy(note => note.OffsetMs ?? 0)
            .ToList();
    }

    /// <summary>The transcript's zero, read from meta.json.</summary>
    private static DateTimeOffset? Anchor(string sessionDirectory)
    {
        JsonObject? root;
        try
        {
            var path = Path.Combine(sessionDirectory, "meta.json");
            if (!File.Exists(path)) return null;
            root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
        if (root is null) return null;

        return ReadInstant(root["audio_start"]) ?? ReadInstant(root["started"]);
    }

    private static DateTimeOffset? ReadInstant(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}
