using Patchthrough.Core;

namespace Patchthrough.Core.Tests;

/// <summary>
/// Turns are what the transcript view lays out, so the grouping decides the shape
/// of the whole pane: one block per stretch of speech, sitting right for the local
/// speaker and left for the other side.
/// </summary>
public sealed class TranscriptTurnsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pt-turns-" + Guid.NewGuid().ToString("N")[..8]);

    public TranscriptTurnsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Segment Spoken(string speaker, int startMs, string text) =>
        new(speaker, startMs, startMs + 1000, text);

    [Fact]
    public void ConsecutiveSegmentsFromOneSpeakerBecomeOneTurn()
    {
        var turns = TranscriptTurns.Group([
            Spoken("me", 1000, "Ship the Windows recorder."),
            Spoken("me", 2500, "Tray first."),
            Spoken("them", 5000, "Agreed."),
        ]);

        // Two turns, not three. A label and a timestamp on every sentence is what
        // grouping exists to prevent.
        Assert.Equal(2, turns.Count);
        Assert.Equal(["Ship the Windows recorder.", "Tray first."], turns[0].Lines);
        Assert.Equal("Ship the Windows recorder.\nTray first.", turns[0].Text);
    }

    [Fact]
    public void ATurnKeepsTheTimeItsSpeakerStarted()
    {
        var turns = TranscriptTurns.Group([
            Spoken("me", 1000, "first"),
            Spoken("me", 65_000, "still me"),
        ]);

        // The start of the stretch, not the last thing said in it.
        Assert.Equal(1000, turns[0].StartMs);
        Assert.Equal("0:01", turns[0].Clock);
    }

    [Fact]
    public void SpeakersAlternatingProduceOneTurnEach()
    {
        var turns = TranscriptTurns.Group([
            Spoken("me", 0, "a"),
            Spoken("them", 1000, "b"),
            Spoken("me", 2000, "c"),
        ]);

        Assert.Equal(["me", "them", "me"], turns.Select(turn => turn.Speaker));
        Assert.Equal([true, false, true], turns.Select(turn => turn.IsMe));
    }

    [Fact]
    public void TheClockMatchesWhatTheMarkdownPrints()
    {
        // A note or a handoff points a reader at a timestamp, so this label and
        // transcript.md have to agree exactly.
        var turns = TranscriptTurns.Group([Spoken("them", 3_725_000, "past an hour")]);
        Assert.Equal(Transcript.Clock(3_725_000), turns[0].Clock);
        Assert.Equal("1:02:05", turns[0].Clock);
    }

    [Fact]
    public void SegmentsAreReadBackFromTheCanonicalTranscript()
    {
        // Written by the real writer, so this ties the reader to the format the
        // pipeline emits rather than to a copy of it.
        var directory = Path.Combine(_root, "2026.08.03-1400");
        Directory.CreateDirectory(directory);
        new Transcript
        {
            Engine = "parakeet",
            Model = "parakeet-tdt-0.6b-v2",
            CreatedAt = DateTimeOffset.UnixEpoch,
            Segments =
            [
                new Segment("me", 1000, 2500, "Ship the Windows recorder."),
                new Segment("them", 5000, 7000, "Agreed, after the CLI lands."),
            ],
        }.Write(directory);

        var turns = TranscriptTurns.Read(directory);

        Assert.Equal(2, turns.Count);
        Assert.Equal("me", turns[0].Speaker);
        Assert.Equal("Ship the Windows recorder.", turns[0].Text);
        Assert.Equal("0:05", turns[1].Clock);
    }

    [Fact]
    public void AMissingTranscriptIsNoTurnsRatherThanAFailure()
    {
        var directory = Path.Combine(_root, "2026.08.03-1400");
        Directory.CreateDirectory(directory);

        // A pending session. The row still has to render.
        Assert.Empty(TranscriptTurns.Read(directory));
    }

    [Fact]
    public void ADamagedTranscriptIsNoTurnsRatherThanAFailure()
    {
        var directory = Path.Combine(_root, "2026.08.03-1400");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "transcript.json"), "{ not json");

        Assert.Empty(TranscriptTurns.Read(directory));
    }

    [Fact]
    public void ASegmentMissingItsSpeakerOrTextIsSkippedAndTheRestSurvive()
    {
        var directory = Path.Combine(_root, "2026.08.03-1400");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "transcript.json"), """
        {
          "segments": [
            { "start_ms": 0, "text": "no speaker" },
            { "speaker": "me", "start_ms": 1000 },
            { "speaker": "them", "start_ms": 2000, "end_ms": 3000, "text": "readable" }
          ]
        }
        """);

        // One bad segment must not cost the reader the whole transcript.
        var turns = TranscriptTurns.Read(directory);
        Assert.Single(turns);
        Assert.Equal("readable", turns[0].Text);
    }

    [Fact]
    public void NoSegmentsMeansNoTurns() => Assert.Empty(TranscriptTurns.Group([]));
}
