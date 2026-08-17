using System.Text.Json.Nodes;
using Patchthrough.Core;

namespace Patchthrough.Core.Tests;

/// <summary>
/// Notes and the recording clock.
///
/// A note's whole value is that it points at the moment a human reacted to
/// something. A timestamp that is plausible but wrong is the worst outcome here,
/// because the note still looks right and quietly sends the reader to a different
/// sentence. These tests are almost entirely about that.
///
/// docs/notes-and-the-recording-clock.md is the reasoning behind them.
/// </summary>
public sealed class SessionNotesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pt-notes-" + Guid.NewGuid().ToString("N")[..8]);

    public SessionNotesTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>The session start, before the capture devices are opened.</summary>
    private static readonly DateTimeOffset Started = new(2026, 8, 3, 14, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The first audio buffer, 1.64 seconds later. That is a real measurement from
    /// the macOS build, and it is the latency this key exists to cancel.
    /// </summary>
    private static readonly DateTimeOffset AudioStart = Started.AddMilliseconds(1640);

    private string Session(bool withAudioStart = true, bool withMeta = true)
    {
        var directory = Path.Combine(_root, "2026.08.03-1400");
        Directory.CreateDirectory(directory);
        if (!withMeta) return directory;

        var writer = SessionWriter.Create(_root, Started);
        writer.AddTrack("mic", "mic.m4a");
        writer.AddTrack("system", "system.m4a", offsetMs: 12);
        writer.WriteFinalMeta(
            Started.AddSeconds(92), name: null, audioStart: withAudioStart ? AudioStart : null);
        return writer.Directory;
    }

    // ----------------------------------------------------------------- storage

    [Fact]
    public void ANoteRoundTripsThroughTheFile()
    {
        var directory = Session();

        SessionNotes.Append(directory, "Flag this exact line.", AudioStart.AddSeconds(30));

        var note = Assert.Single(SessionNotes.Read(directory));
        Assert.Equal("Flag this exact line.", note.Text);
        Assert.Equal(AudioStart.AddSeconds(30), note.At);
    }

    [Fact]
    public void TheFileStoresAnInstantAndNotAnOffset()
    {
        // The transcript's zero moves during recording: a track that never delivers
        // a buffer redefines it. An offset written at typing time would bake in
        // whichever zero happened to be current; an instant re-resolves.
        var directory = Session();
        SessionNotes.Append(directory, "note", AudioStart.AddSeconds(30));

        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "notes.json")))!.AsObject();

        Assert.Equal(1, (int?)root["schema_version"]);
        var stored = (string?)root["notes"]![0]!["at"];
        Assert.Equal("2026-08-03T14:00:31.640Z", stored);
        // Milliseconds are kept. Device startup latency has been measured between
        // 0.19 and 1.64 seconds, so a value rounded to the second would throw away
        // most of what the anchor is for.
        Assert.Contains(".640", stored);
    }

    [Fact]
    public void EachNoteIsFlushedRatherThanHeldUntilStop()
    {
        // A recording can run for an hour, and a crash in that hour must not take
        // the user's typing with it.
        var directory = Session();

        SessionNotes.Append(directory, "first", AudioStart.AddSeconds(10));
        Assert.Single(SessionNotes.Read(directory));

        SessionNotes.Append(directory, "second", AudioStart.AddSeconds(20));
        Assert.Equal(2, SessionNotes.Read(directory).Count);
    }

    [Fact]
    public void AnEmptyNoteIsNotStored()
    {
        var directory = Session();

        SessionNotes.Append(directory, "   ");

        Assert.Empty(SessionNotes.Read(directory));
    }

    [Fact]
    public void ANoteIsTrimmed()
    {
        var directory = Session();

        SessionNotes.Append(directory, "  spaced  ", AudioStart);

        Assert.Equal("spaced", Assert.Single(SessionNotes.Read(directory)).Text);
    }

    [Fact]
    public void ASessionWithNoNotesFileHasNoNotes() =>
        Assert.Empty(SessionNotes.Read(Session()));

    [Fact]
    public void ADamagedNotesFileCostsTheNotesAndNothingElse()
    {
        var directory = Session();
        File.WriteAllText(Path.Combine(directory, "notes.json"), "{ not json");

        // Losing the handoff because a side file got corrupted would be a worse
        // failure than losing the notes.
        Assert.Empty(SessionNotes.Read(directory));
        Assert.Empty(SessionNotes.Resolved(directory));
    }

    [Fact]
    public void AnUnreadableEntryIsSkippedAndTheRestSurvive()
    {
        var directory = Session();
        File.WriteAllText(Path.Combine(directory, "notes.json"), """
        {
          "schema_version": 1,
          "notes": [
            { "text": "no timestamp" },
            { "at": "not a date", "text": "bad timestamp" },
            { "at": "2026-08-03T14:00:31.640Z" },
            { "at": "2026-08-03T14:00:41.640Z", "text": "readable" }
          ]
        }
        """);

        Assert.Equal("readable", Assert.Single(SessionNotes.Read(directory)).Text);
    }

    // ------------------------------------------------------------ the clock

    [Fact]
    public void ANoteIsPlacedAgainstTheAudioAnchorAndNotTheSessionStart()
    {
        var directory = Session();
        // Typed 30 seconds after the first audio buffer, which is where the
        // transcript's own 0:30 is.
        SessionNotes.Append(directory, "Flag this exact line.", AudioStart.AddSeconds(30));

        var note = Assert.Single(SessionNotes.Resolved(directory));

        Assert.Equal(30_000, note.OffsetMs);
        Assert.Equal("0:30", note.Clock);
    }

    [Fact]
    public void UsingTheSessionStartWouldLandOverASecondLate()
    {
        // The failure this anchor exists to prevent. Subtracting `started` instead
        // adds the device startup latency, so a note lands past the line it was
        // about. At 1.64 seconds that is a different sentence in a dense
        // conversation.
        var directory = Session();
        SessionNotes.Append(directory, "note", AudioStart.AddSeconds(30));

        var resolved = Assert.Single(SessionNotes.Resolved(directory));
        var againstStarted = (int)(AudioStart.AddSeconds(30) - Started).TotalMilliseconds;

        Assert.Equal(30_000, resolved.OffsetMs);
        Assert.Equal(31_640, againstStarted);
        Assert.Equal("0:31", Transcript.Clock(againstStarted));
        Assert.NotEqual(Transcript.Clock(againstStarted), resolved.Clock);
    }

    [Fact]
    public void AnOlderSessionFallsBackToItsStartAndSaysNothingAboutIt()
    {
        // Sessions recorded before the anchor was persisted. Overshooting is
        // documented as approximate; refusing to place the note at all would be
        // worse, because the note is still real.
        var directory = Session(withAudioStart: false);
        SessionNotes.Append(directory, "note", Started.AddSeconds(30));

        var note = Assert.Single(SessionNotes.Resolved(directory));

        Assert.Equal(30_000, note.OffsetMs);
    }

    [Fact]
    public void ANoteTypedBeforeTheFirstBufferIsClampedToZero()
    {
        // The window is live before the audio devices finish opening, so this
        // happens. It belongs at the start of the transcript, not before it.
        var directory = Session();
        SessionNotes.Append(directory, "typed while starting up", Started.AddMilliseconds(200));

        var note = Assert.Single(SessionNotes.Resolved(directory));

        Assert.Equal(0, note.OffsetMs);
        Assert.Equal("0:00", note.Clock);
    }

    [Fact]
    public void WithNoAnchorAtAllANoteKeepsItsTextAndLosesItsPosition()
    {
        // Null is not zero. A note claiming 0:00 when its position is unknown would
        // send a reader to the opening line of a meeting it has nothing to do with.
        var directory = Session(withMeta: false);
        SessionNotes.Append(directory, "orphaned note", AudioStart.AddSeconds(30));

        var note = Assert.Single(SessionNotes.Resolved(directory));

        Assert.Null(note.OffsetMs);
        Assert.Null(note.Clock);
        Assert.Equal("orphaned note", note.Text);
    }

    [Fact]
    public void TheLabelTruncatesTheWayTheTranscriptDoes()
    {
        // transcript.md floors to the second. A renderer that rounded would point
        // one line off, which is the hardest kind of error to notice because the
        // note still looks right.
        var directory = Session();
        SessionNotes.Append(directory, "note", AudioStart.AddMilliseconds(30_900));

        var note = Assert.Single(SessionNotes.Resolved(directory));

        Assert.Equal(30_900, note.OffsetMs);
        Assert.Equal("0:30", note.Clock);
    }

    [Fact]
    public void NotesComeBackInTranscriptOrderRatherThanTypedOrder()
    {
        var directory = Session();
        SessionNotes.Append(directory, "later", AudioStart.AddSeconds(90));
        SessionNotes.Append(directory, "earlier", AudioStart.AddSeconds(30));

        Assert.Equal(["earlier", "later"], SessionNotes.Resolved(directory).Select(note => note.Text));
    }

    // ---------------------------------------------------------- the handoff

    [Fact]
    public void TheHandoffCarriesTheNotesAboveTheTranscript()
    {
        var document = HandoffDocument.Build(
            _root, "**[0:31] me:** the line itself", 92, cleanStop: true, name: null,
            notes: [new ResolvedNote(30_000, "Flag this exact line.")]);

        var notesAt = document.IndexOf("## Notes", StringComparison.Ordinal);
        var transcriptAt = document.IndexOf("## Transcript", StringComparison.Ordinal);
        // What a human flagged, then the record it points at.
        Assert.True(notesAt > 0 && notesAt < transcriptAt);
        Assert.Contains("- **[0:30]** Flag this exact line.", document);
        Assert.Contains("Nothing here was generated or summarized.", document);
    }

    [Fact]
    public void TheInstructionsGainANotesClauseOnlyWhenThereAreNotes()
    {
        const string transcript = "**[0:01] me:** hello";

        var withNotes = HandoffDocument.Build(_root, transcript, 92, true, null,
            [new ResolvedNote(1000, "note")]);
        var withoutNotes = HandoffDocument.Build(_root, transcript, 92, true, null);

        Assert.Contains("My own notes are above the transcript.", withNotes);
        // Telling an agent to weigh something that is not there reads as a missing
        // attachment.
        Assert.DoesNotContain("My own notes", withoutNotes);
        Assert.DoesNotContain("## Notes", withoutNotes);
    }

    [Fact]
    public void ASessionWithNoNotesProducesNoHeadingAndNoBlankGap()
    {
        var document = HandoffDocument.Build(_root, "**[0:01] me:** hello", 92, true, null);

        // An empty "## Notes" would claim the user wrote nothing worth saying rather
        // than that the session has no notes at all.
        Assert.Contains("- Source: `" + _root + "`\n\n## Transcript", document);
    }

    [Fact]
    public void ANoteWithNoPositionRendersWithoutATimestamp()
    {
        var document = HandoffDocument.Build(_root, "**[0:01] me:** hello", 92, true, null,
            [new ResolvedNote(null, "unanchored")]);

        Assert.Contains("- unanchored", document);
        Assert.DoesNotContain("**[", document.Split("## Transcript")[0]);
    }

    [Fact]
    public void WritingTheHandoffPicksUpTheNotesOnDisk()
    {
        // Every writer of this document gets the notes without having to know they
        // exist, which is what keeps the transcription pipeline from needing to.
        var directory = Session();
        File.WriteAllText(Path.Combine(directory, "transcript.md"),
            "# 2026.08.03-1400\n\nengine: parakeet (x)\n\n**[0:31] me:** the line itself\n");
        SessionNotes.Append(directory, "Flag this exact line.", AudioStart.AddSeconds(30));

        HandoffDocument.Write(directory, 92, cleanStop: true, name: null);

        var document = File.ReadAllText(Path.Combine(directory, "handoff.md"));
        Assert.Contains("- **[0:30]** Flag this exact line.", document);
    }

    [Fact]
    public void TheDocumentStaysLineFeedOnly()
    {
        // handoff.md is a byte-level contract with the macOS app and the npm CLI.
        var document = HandoffDocument.Build(_root, "**[0:01] me:** hello", 92, true, null,
            [new ResolvedNote(1000, "note")]);

        Assert.DoesNotContain('\r', document);
    }
}
