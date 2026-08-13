using Patchthrough.Core;

namespace Patchthrough.Core.Tests;

/// <summary>
/// The session list is what a user sees, so a wrong status is a wrong screen.
/// Each test builds the exact files that produce one status, because the states
/// exist to tell apart directories that look alike: a live recording, a session
/// waiting for transcription, and a finished session that found no speech all
/// lack a usable transcript.
/// </summary>
public sealed class SessionIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pt-index-" + Guid.NewGuid().ToString("N")[..8]);

    public SessionIndexTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A session directory with the files a caller asks for.</summary>
    private string Session(
        string id,
        string? meta = null,
        string? transcriptMarkdown = null,
        bool transcriptJson = false)
    {
        var directory = Path.Combine(_root, id);
        Directory.CreateDirectory(directory);
        if (meta is not null) File.WriteAllText(Path.Combine(directory, "meta.json"), meta);
        if (transcriptMarkdown is not null)
        {
            File.WriteAllText(Path.Combine(directory, "transcript.md"), transcriptMarkdown);
        }
        if (transcriptJson) File.WriteAllText(Path.Combine(directory, "transcript.json"), "{}");
        return directory;
    }

    /// <summary>meta.json as the recorder writes it at stop.</summary>
    private static string Meta(int durationSeconds = 92, bool cleanStop = true, string? name = null) =>
        $$"""
        {
          "clean_stop": {{(cleanStop ? "true" : "false")}},
          "duration_seconds": {{durationSeconds}},
          "files": { "mic": "mic.m4a", "system": "system.m4a" },{{(name is null ? "" : $"\n  \"name\": \"{name}\",")}}
          "start_offset_ms": { "mic": 0, "system": 12 },
          "started": "2026-08-03T14:00:00Z"
        }
        """;

    /// <summary>What Transcript.Render writes, which is what the reader has to match.</summary>
    private static string Rendered(params string[] spoken)
    {
        var lines = new List<string> { "# 2026.08.03-1400", "", "engine: parakeet (parakeet-tdt-0.6b-v2)", "" };
        foreach (var line in spoken)
        {
            lines.Add(line);
            lines.Add("");
        }
        return string.Join("\n", lines);
    }

    [Fact]
    public void ATranscribedSessionIsReadyAndCarriesItsCountsAndFirstLine()
    {
        Session("2026.08.03-1400", Meta(), Rendered(
            "**[0:01] me:** Ship the Windows recorder.",
            "**[0:05] them:** Agreed, after the CLI lands."));

        var listing = Assert.Single(SessionIndex.Scan(_root));

        Assert.Equal(SessionStatus.Ready, listing.Status);
        Assert.Equal("2026.08.03-1400", listing.Id);
        Assert.Equal(92, listing.DurationSeconds);
        Assert.True(listing.CleanStop);
        // Spoken words only. The title and engine lines are not speech.
        Assert.Equal(9, listing.Words);
        Assert.Equal("Ship the Windows recorder.", listing.FirstLine);
    }

    [Fact]
    public void ASessionWaitingForTranscriptionIsPending()
    {
        Session("2026.08.03-1400", Meta());

        var listing = Assert.Single(SessionIndex.Scan(_root));

        Assert.Equal(SessionStatus.Pending, listing.Status);
        // A pending session has no transcript to count, so it claims nothing.
        Assert.Equal(0, listing.Words);
        Assert.Null(listing.FirstLine);
    }

    [Fact]
    public void TheLiveSessionOutranksWhatIsOnDiskBecauseTheFilesAreStillBeingWritten()
    {
        Session("2026.08.03-1400", Meta());

        var listing = Assert.Single(SessionIndex.Scan(_root, liveSessionId: "2026.08.03-1400"));

        // Without the live id this is Pending: the two are identical on disk.
        Assert.Equal(SessionStatus.Recording, listing.Status);
    }

    [Fact]
    public void TranscriptJsonWithNoSpeechIsFinishedWorkNotPendingWork()
    {
        // Transcription ran and wrote its completion marker, but the audio held
        // no speech. Calling this pending leaves a row reading "Transcribing"
        // forever with nothing behind it.
        Session("2026.08.03-1400", Meta(), Rendered(), transcriptJson: true);

        Assert.Equal(SessionStatus.Empty, Assert.Single(SessionIndex.Scan(_root)).Status);
    }

    [Fact]
    public void ATranscriptWithNoSpokenLineIsNotATranscript()
    {
        // The header alone, with no completion marker. The macOS app reaches the
        // same verdict through Handoff.resolveSession, which refuses an empty one.
        Session("2026.08.03-1400", Meta(), Rendered());

        Assert.Equal(SessionStatus.Pending, Assert.Single(SessionIndex.Scan(_root)).Status);
    }

    [Fact]
    public void ADirectoryWithNoMetaIsBroken()
    {
        // The recording was interrupted before it wrote a marker.
        Session("2026.08.03-1400");

        Assert.Equal(SessionStatus.Broken, Assert.Single(SessionIndex.Scan(_root)).Status);
    }

    [Fact]
    public void AMalformedMetaStillListsTheSession()
    {
        // One corrupt directory must not empty the list, and the session is
        // still recorded audio the user can see.
        Session("2026.08.03-1400", "{ not json");

        var listing = Assert.Single(SessionIndex.Scan(_root));

        Assert.Equal(SessionStatus.Pending, listing.Status);
        Assert.Equal(0, listing.DurationSeconds);
        Assert.Null(listing.Name);
    }

    [Fact]
    public void AMalformedMetaDoesNotStopAReadableTranscriptFromBeingReady()
    {
        Session("2026.08.03-1400", "{ not json", Rendered("**[0:01] me:** hello"));

        var listing = Assert.Single(SessionIndex.Scan(_root));

        // The transcript is what a handoff needs; the duration is cosmetic.
        Assert.Equal(SessionStatus.Ready, listing.Status);
        Assert.Equal(1, listing.Words);
        Assert.Equal(0, listing.DurationSeconds);
    }

    [Fact]
    public void AnUncleanStopSurvivesIntoTheListing()
    {
        Session("2026.08.03-1400", Meta(cleanStop: false), Rendered("**[0:01] me:** cut short"));

        Assert.False(Assert.Single(SessionIndex.Scan(_root)).CleanStop);
    }

    [Fact]
    public void TheNameFromMetaBecomesTheTitleAndTheFolderStaysTheIdentity()
    {
        Session("2026.08.03-1400", Meta(name: "Windows port kickoff"), Rendered("**[0:01] me:** hello"));

        var listing = Assert.Single(SessionIndex.Scan(_root));

        Assert.Equal("Windows port kickoff", listing.Name);
        Assert.Equal("Windows port kickoff", listing.DisplayTitle);
        Assert.Equal("2026.08.03-1400", listing.Id);
    }

    [Fact]
    public void AnUnnamedSessionShowsItsFolderName()
    {
        Session("2026.08.03-1400", Meta());

        Assert.Equal("2026.08.03-1400", Assert.Single(SessionIndex.Scan(_root)).DisplayTitle);
    }

    [Fact]
    public void SessionsComeBackNewestFirst()
    {
        Session("2026.08.01-0900", Meta());
        Session("2026.08.03-1400", Meta());
        Session("2026.08.02-1000", Meta());

        // The folder format sorts chronologically as text, which is why it was
        // chosen. The newest session is the one a user wants first.
        Assert.Equal(
            ["2026.08.03-1400", "2026.08.02-1000", "2026.08.01-0900"],
            SessionIndex.Scan(_root).Select(listing => listing.Id));
    }

    [Fact]
    public void TheFolderNameSuppliesTheDateBecauseAListGroupsByLocalDay()
    {
        Session("2026.08.03-1400", Meta());

        var listing = Assert.Single(SessionIndex.Scan(_root));

        Assert.NotNull(listing.StartedAt);
        // Local time, as the folder name records it. meta.json's `started` is
        // UTC and would group a late-evening meeting under the wrong day.
        Assert.Equal(new DateTime(2026, 8, 3, 14, 0, 0), listing.StartedAt!.Value.DateTime);
    }

    [Fact]
    public void ACollisionSuffixStillParsesItsDate()
    {
        // SessionWriter appends -2 for a second recording inside one minute.
        Session("2026.08.03-1400-2", Meta());

        var listing = Assert.Single(SessionIndex.Scan(_root));

        Assert.Equal("2026.08.03-1400-2", listing.Id);
        Assert.Equal(new DateTime(2026, 8, 3, 14, 0, 0), listing.StartedAt!.Value.DateTime);
    }

    [Fact]
    public void AFolderThatIsNotASessionHasNoDateAndStillLists()
    {
        Session("scratch", Meta());

        var listing = Assert.Single(SessionIndex.Scan(_root));

        // meta.json carries `started`, so a folder with a hand-written name
        // still sorts somewhere sensible.
        Assert.Equal(new DateTimeOffset(2026, 8, 3, 14, 0, 0, TimeSpan.Zero), listing.StartedAt);
    }

    [Fact]
    public void AMissingRootIsAnEmptyListRatherThanAFailure()
    {
        // Nothing has been recorded yet. That is a first run, not an error.
        Assert.Empty(SessionIndex.Scan(Path.Combine(_root, "never-recorded")));
    }

    [Fact]
    public void LooseFilesInTheRootAreNotSessions()
    {
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not a session");
        Session("2026.08.03-1400", Meta());

        Assert.Single(SessionIndex.Scan(_root));
    }

    [Fact]
    public void TheWordCountReadsWhatTheRendererWrites()
    {
        // Ties the reader to Transcript.Render rather than to a copy of its
        // format: if the renderer changes, this test fails here.
        var transcript = new Transcript
        {
            Engine = "parakeet",
            Model = "parakeet-tdt-0.6b-v2",
            CreatedAt = DateTimeOffset.UnixEpoch,
            Segments =
            [
                new Segment("me", 1000, 2500, "Ship the Windows recorder."),
                new Segment("them", 5000, 7000, "Agreed, after the CLI lands."),
            ],
        };
        Session("2026.08.03-1400", Meta(), transcript.Render("2026.08.03-1400"));

        var listing = Assert.Single(SessionIndex.Scan(_root));

        Assert.Equal(SessionStatus.Ready, listing.Status);
        Assert.Equal(9, listing.Words);
        Assert.Equal("Ship the Windows recorder.", listing.FirstLine);
    }
}
