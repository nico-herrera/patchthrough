using System.Text.Json;
using System.Text.Json.Nodes;

namespace Patchthrough.Core;

/// <summary>
/// One speaker's uninterrupted stretch of a conversation, as the transcript view
/// draws it: a timestamp, a speaker label, and the lines they said before the
/// other side spoke.
/// </summary>
public sealed record Turn(string Speaker, int StartMs, IReadOnlyList<string> Lines)
{
    /// <summary>`me` is the microphone, so the local speaker.</summary>
    public bool IsMe => string.Equals(Speaker, "me", StringComparison.Ordinal);

    /// <summary>The timestamp label, on the same clock transcript.md prints.</summary>
    public string Clock => Transcript.Clock(StartMs);

    /// <summary>The turn as one block of text, one line per segment.</summary>
    public string Text => string.Join("\n", Lines);
}

/// <summary>
/// Reads a session's transcript and groups it into turns.
///
/// It reads transcript.json rather than parsing transcript.md. The json file is
/// the canonical output and the markdown is a rendering of it, so going to the
/// source avoids re-deriving speakers and times from formatted text. The macOS
/// window parses the markdown, which is a difference in method and not in result.
/// </summary>
public static class TranscriptTurns
{
    /// <summary>
    /// The segments of a session, in transcript order. An unreadable or absent
    /// transcript is an empty list: a session whose transcript cannot be parsed
    /// still has to render as a row rather than take the window down.
    /// </summary>
    public static IReadOnlyList<Segment> ReadSegments(string sessionDirectory)
    {
        var path = Path.Combine(sessionDirectory, "transcript.json");
        JsonArray? segments;
        try
        {
            if (!File.Exists(path)) return [];
            segments = (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?["segments"] as JsonArray;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
        if (segments is null) return [];

        var result = new List<Segment>();
        foreach (var node in segments)
        {
            if (node is not JsonObject segment) continue;
            var speaker = segment["speaker"] is JsonValue speakerValue
                && speakerValue.TryGetValue(out string? name) ? name : null;
            var text = segment["text"] is JsonValue textValue
                && textValue.TryGetValue(out string? spoken) ? spoken : null;
            if (speaker is null || text is null) continue;

            var startMs = segment["start_ms"] is JsonValue startValue
                && startValue.TryGetValue(out int start) ? start : 0;
            var endMs = segment["end_ms"] is JsonValue endValue
                && endValue.TryGetValue(out int end) ? end : startMs;
            result.Add(new Segment(speaker, startMs, endMs, text));
        }
        return result;
    }

    /// <summary>
    /// Collapse consecutive segments from one speaker into a single turn.
    ///
    /// The turn, not the segment, is what the view lays out: it is the block that
    /// sits right for `me` and left for `them`, and it carries the timestamp of
    /// the moment that speaker started. Grouping per segment instead would put a
    /// label and a timestamp on every sentence.
    /// </summary>
    public static IReadOnlyList<Turn> Group(IEnumerable<Segment> segments)
    {
        var turns = new List<(string Speaker, int StartMs, List<string> Lines)>();
        foreach (var segment in segments)
        {
            if (turns.Count > 0 && string.Equals(turns[^1].Speaker, segment.Speaker, StringComparison.Ordinal))
            {
                turns[^1].Lines.Add(segment.Text);
            }
            else
            {
                turns.Add((segment.Speaker, segment.StartMs, [segment.Text]));
            }
        }
        return turns.Select(turn => new Turn(turn.Speaker, turn.StartMs, turn.Lines)).ToList();
    }

    /// <summary>The turns of a session, read and grouped.</summary>
    public static IReadOnlyList<Turn> Read(string sessionDirectory) => Group(ReadSegments(sessionDirectory));
}
