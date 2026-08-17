using Patchthrough.Core;

namespace Patchthrough.Core.Tests;

/// <summary>
/// The handoff is the product: it is what takes a meeting from "we agreed on it" to
/// an agent working on it. These tests cover the parts that are easy to get subtly
/// wrong and expensive to notice, which are the paths written into a user's own
/// repository and the encoding of a prompt that travels through a URL.
/// </summary>
public sealed class HandoffTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pt-handoff-" + Guid.NewGuid().ToString("N")[..8]);

    public HandoffTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Repository(bool git = true)
    {
        var repository = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repository);
        if (git) Directory.CreateDirectory(Path.Combine(repository, ".git"));
        return repository;
    }

    // -------------------------------------------------------------- staging

    [Fact]
    public void StagingWritesTheDocumentInsideTheRepository()
    {
        var repository = Repository();

        var staged = HandoffStaging.Stage(repository, "2026.08.03-1400", "# Meeting handoff");

        Assert.Equal(Path.Combine(repository, ".meeting", "2026.08.03-1400.md"), staged);
        Assert.Equal("# Meeting handoff", File.ReadAllText(staged));
    }

    [Theory]
    [InlineData("2026.08.03-1400", "2026.08.03-1400.md")]
    [InlineData("Windows port kickoff", "Windows-port-kickoff.md")]
    public void AFileNameKeepsWhatIsSafeToKeep(string sessionId, string expected) =>
        Assert.Equal(expected, HandoffStaging.FileNameFor(sessionId));

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\Windows\\System32")]
    [InlineData("a\"; rm -rf /")]
    [InlineData("sub/dir")]
    [InlineData("C:\\absolute")]
    [InlineData("..")]
    [InlineData("na$me`with|pipes")]
    public void AFileNameCannotEscapeTheMeetingDirectory(string sessionId)
    {
        // A meeting name comes from the user and this value becomes a path. The
        // invariant is what matters rather than the exact substitution: whatever
        // comes out is one path segment, inside .meeting, and nothing else.
        var name = HandoffStaging.FileNameFor(sessionId);

        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain(':', name);
        Assert.EndsWith(".md", name);
        Assert.Equal(name, Path.GetFileName(name));
        // The decisive check: joined to the meeting directory it stays inside it.
        var meeting = Path.GetFullPath(Path.Combine(_root, HandoffStaging.MeetingDirectory));
        var resolved = Path.GetFullPath(Path.Combine(meeting, name));
        Assert.StartsWith(meeting + Path.DirectorySeparatorChar, resolved);
    }

    [Fact]
    public void StagingRefusesADirectoryThatIsNotThere()
    {
        // A repository the user moved or mistyped. Creating it would put a
        // transcript somewhere nobody is working.
        Assert.Throws<DirectoryNotFoundException>(() =>
            HandoffStaging.Stage(Path.Combine(_root, "missing"), "2026.08.03-1400", "x"));
    }

    [Fact]
    public void TheAgentIsPointedAtARepositoryRelativePathWithForwardSlashes()
    {
        // The agent runs with the repository as its working directory, so this is
        // the only path it can resolve. Forward slashes because a backslash reads
        // as an escape to some agents.
        Assert.Equal(".meeting/2026.08.03-1400.md", HandoffStaging.RelativePathFor("2026.08.03-1400"));
    }

    [Fact]
    public void StagingExcludesTheMeetingDirectoryLocally()
    {
        var repository = Repository();

        HandoffStaging.Stage(repository, "2026.08.03-1400", "x");

        var exclude = Path.Combine(repository, ".git", "info", "exclude");
        var contents = File.ReadAllText(exclude);
        Assert.Contains(".meeting/", contents);
        // info/exclude and not .gitignore: .gitignore is tracked, so writing to it
        // would show up as a change to the user's repository.
        Assert.False(File.Exists(Path.Combine(repository, ".gitignore")));
    }

    [Fact]
    public void ExcludingIsNotRepeatedOnASecondHandoff()
    {
        var repository = Repository();

        HandoffStaging.Stage(repository, "2026.08.03-1400", "x");
        HandoffStaging.Stage(repository, "2026.08.03-1500", "y");

        var contents = File.ReadAllText(Path.Combine(repository, ".git", "info", "exclude"));
        Assert.Equal(1, contents.Split(".meeting/").Length - 1);
    }

    [Fact]
    public void AnExistingExcludeFileKeepsItsContent()
    {
        var repository = Repository();
        var info = Path.Combine(repository, ".git", "info");
        Directory.CreateDirectory(info);
        File.WriteAllText(Path.Combine(info, "exclude"), "*.local\nscratch/\n");

        HandoffStaging.Stage(repository, "2026.08.03-1400", "x");

        var contents = File.ReadAllText(Path.Combine(info, "exclude"));
        Assert.Contains("*.local", contents);
        Assert.Contains("scratch/", contents);
        Assert.Contains(".meeting/", contents);
    }

    [Fact]
    public void ADirectoryThatIsNotARepositoryStillGetsItsTranscript()
    {
        var plain = Repository(git: false);

        var staged = HandoffStaging.Stage(plain, "2026.08.03-1400", "x");

        // Failing the handoff over a missing exclude entry would be worse than the
        // user seeing an untracked directory.
        Assert.True(File.Exists(staged));
        Assert.Null(HandoffStaging.ResolveGitDirectory(plain));
    }

    [Fact]
    public void AWorktreeGitFileIsFollowedToItsRealGitDirectory()
    {
        // A worktree and a submodule both have a .git file rather than a directory,
        // holding "gitdir: <path>". Reading it is what makes the exclude land in
        // the right place for those clones.
        var real = Path.Combine(_root, "actual-git-dir");
        Directory.CreateDirectory(real);
        var worktree = Path.Combine(_root, "worktree");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {real}\n");

        Assert.Equal(real, HandoffStaging.ResolveGitDirectory(worktree));

        HandoffStaging.Stage(worktree, "2026.08.03-1400", "x");
        Assert.Contains(".meeting/", File.ReadAllText(Path.Combine(real, "info", "exclude")));
    }

    [Fact]
    public void ARelativeGitdirPointerIsResolvedAgainstTheRepository()
    {
        var worktree = Path.Combine(_root, "worktree");
        Directory.CreateDirectory(Path.Combine(worktree, "nested-git"));
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: nested-git");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(worktree, "nested-git")),
            HandoffStaging.ResolveGitDirectory(worktree));
    }

    // --------------------------------------------------------------- prompts

    [Fact]
    public void TheAgentPromptNamesTheStagedFileAndCarriesTheSharedCaveat()
    {
        var prompt = HandoffPrompt.ForStagedFile(".meeting/2026.08.03-1400.md");

        Assert.StartsWith("Read .meeting/2026.08.03-1400.md.", prompt);
        // The caveat is one wording shared by the app, the CLI, and the document.
        Assert.Contains(HandoffDocument.AsrCaveat, prompt);
        Assert.Contains("Don't edit anything until we've agreed the list.", prompt);
    }

    [Fact]
    public void TheChatPromptNeverNamesTheAttachedFile()
    {
        // One site renames a pasted file and strips its extension, so naming it
        // would point the model at something it cannot see.
        var prompt = HandoffPrompt.ForAttachedDocument(1832);

        Assert.DoesNotContain("handoff.md", prompt);
        Assert.DoesNotContain(".meeting", prompt);
        Assert.Contains("30m32s", prompt);
        Assert.Contains(HandoffDocument.AsrCaveat, prompt);
    }

    // ---------------------------------------------------------- destinations

    [Fact]
    public void PercentEncodingLeavesOnlyLettersAndDigitsRaw()
    {
        // Stricter than a standard encoder, which leaves '()*~!.-_ alone. The same
        // bytes have to reach the site from the app, the CLI, and the macOS build.
        Assert.Equal("a-b", DestinationCatalog.PercentEncode("a-b").Replace("%2D", "-"));
        Assert.Equal("%2D", DestinationCatalog.PercentEncode("-"));
        Assert.Equal("%27%28%29%2A%7E%21%2E%5F", DestinationCatalog.PercentEncode("'()*~!._"));
        Assert.Equal("hello%20world", DestinationCatalog.PercentEncode("hello world"));
    }

    [Fact]
    public void PercentEncodingSendsNonLatinTextAsUtf8Bytes()
    {
        // Two bytes for an accented character, four for an emoji. A site reading the
        // query as UTF-8 gets the text back intact.
        Assert.Equal("%C3%A9", DestinationCatalog.PercentEncode("é"));
        Assert.Equal("%F0%9F%8E%A7", DestinationCatalog.PercentEncode("🎧"));
    }

    [Fact]
    public void APromptIsAddedToASiteThatReadsOne()
    {
        var site = DestinationCatalog.Shipped.Single(s => s.Id == "web-claude");

        var url = DestinationCatalog.UrlFor(site, "read this");

        Assert.Equal("https://claude.ai/new?q=read%20this", url);
    }

    [Fact]
    public void ASiteThatIgnoresAPromptGetsAPlainChat()
    {
        // Sending a query it will not read would show the whole prompt as literal
        // text in the page.
        var site = DestinationCatalog.Shipped.Single(s => s.Id == "web-m365");

        Assert.Equal(site.NewChatUrl, DestinationCatalog.UrlFor(site, "read this"));
    }

    [Fact]
    public void APromptIsAppendedToAUrlThatAlreadyHasAQuery()
    {
        // Built through a UriBuilder, not by concatenation: appending "?q=" by hand
        // to a URL with a fragment puts the query inside the fragment, where the
        // page never reads it.
        var site = new ChatSite("custom", "Custom", "https://chat.example.com/new?team=eng",
            PrefillsPrompt: true, UploadsToCloud: false, IsCustom: true);

        var url = DestinationCatalog.UrlFor(site, "hi");

        Assert.Equal("https://chat.example.com/new?team=eng&q=hi", url);
    }

    [Fact]
    public void ACustomDestinationIsReadFromTheConfig()
    {
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, """
        {
          "custom_destinations": [
            { "id": "internal", "label": "Internal chat", "url": "https://chat.example.com/new",
              "uploads_to_cloud": true }
          ]
        }
        """);

        var sites = DestinationCatalog.Resolve(Config.Load(path), TextWriter.Null);

        var custom = sites.Single(site => site.Id == "internal");
        Assert.Equal("Internal chat", custom.Label);
        Assert.True(custom.UploadsToCloud);
        Assert.True(custom.PrefillsPrompt);   // the default when the key is absent
        Assert.True(custom.IsCustom);
        Assert.Equal("gui:internal", custom.DestinationId);
    }

    [Theory]
    [InlineData("""{ "custom_destinations": [ { "url": "https://a.example" } ] }""", "missing")]
    [InlineData("""{ "custom_destinations": [ { "id": "bad id", "url": "https://a.example" } ] }""", "bad")]
    [InlineData("""{ "custom_destinations": [ { "id": "a", "url": "file:///etc/passwd" } ] }""", "scheme")]
    [InlineData("""{ "custom_destinations": [ { "id": "a", "url": "javascript:alert(1)" } ] }""", "scheme")]
    [InlineData("""{ "custom_destinations": [ { "id": "a" } ] }""", "no url")]
    public void ABadCustomDestinationIsDroppedAndReported(string json, string why)
    {
        var path = Path.Combine(_root, $"config-{why.Replace(' ', '-')}.json");
        File.WriteAllText(path, json);
        var warnings = new StringWriter();

        var sites = DestinationCatalog.Resolve(Config.Load(path), warnings);

        // The URL is handed to the shell, which gives any scheme to whatever claims
        // it, so anything that is not http or https never becomes a door.
        Assert.Equal(DestinationCatalog.Shipped.Count, sites.Count);
        Assert.Contains("warning:", warnings.ToString());
    }

    [Fact]
    public void ACustomEntryCanReplaceAShippedSite()
    {
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, """
        {
          "custom_destinations": [
            { "id": "web-claude", "label": "Claude (self-hosted)", "url": "https://claude.internal/new" }
          ]
        }
        """);

        var sites = DestinationCatalog.Resolve(Config.Load(path), TextWriter.Null);

        var claude = Assert.Single(sites, site => site.Id == "web-claude");
        Assert.Equal("Claude (self-hosted)", claude.Label);
        Assert.Equal(DestinationCatalog.Shipped.Count, sites.Count);
    }

    // ---------------------------------------------------------------- agents

    [Fact]
    public void AnAgentIsFoundOnThePathThroughItsWindowsExtension()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(bin, "claude.cmd"),
        };

        var found = AgentCatalog.Installed(bin, ".COM;.EXE;.CMD;.BAT", installed.Contains);

        var claude = Assert.Single(found);
        Assert.Equal("claude", claude.Agent.Id);
        // The extension carries PATHEXT's own case, which is what the npm CLI
        // produces too. Windows paths are case-insensitive, so this resolves; the
        // shim check is case-insensitive for the same reason.
        Assert.Equal(Path.Combine(bin, "claude.CMD"), claude.ExecutablePath);
        Assert.True(claude.IsShim);
    }

    [Fact]
    public void AnNpmShimTakesItsPromptFromTheClipboard()
    {
        // cmd.exe parses a .cmd shim's argument line and treats a newline as the end
        // of the command. Every prompt here has newlines, so no amount of quoting
        // makes an argument work.
        var bin = Path.Combine(_root, "bin");
        var shim = new InstalledAgent(
            AgentCatalog.Known.Single(agent => agent.Id == "claude"),
            Path.Combine(bin, "claude.cmd"));

        Assert.True(shim.IsShim);
        Assert.Equal(AgentPromptStyle.Clipboard, shim.EffectiveStyle);
    }

    [Fact]
    public void ARealExecutableKeepsItsOwnPromptStyle()
    {
        var bin = Path.Combine(_root, "bin");
        var direct = new InstalledAgent(
            AgentCatalog.Known.Single(agent => agent.Id == "opencode"),
            Path.Combine(bin, "opencode.exe"));

        Assert.False(direct.IsShim);
        Assert.Equal(AgentPromptStyle.RunSubcommand, direct.EffectiveStyle);
    }

    [Fact]
    public void TheBareNameIsPreferredOverAnExtension()
    {
        // A directory holding both has to resolve the way a shell would.
        var bin = Path.Combine(_root, "bin");
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(bin, "claude"),
            Path.Combine(bin, "claude.cmd"),
        };

        Assert.Equal(
            Path.Combine(bin, "claude"),
            AgentCatalog.Locate("claude", bin, ".EXE;.CMD", installed.Contains));
    }

    [Fact]
    public void AnEarlierPathDirectoryWins()
    {
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(first, "codex.exe"),
            Path.Combine(second, "codex.exe"),
        };

        Assert.Equal(
            Path.Combine(first, "codex.EXE"),
            AgentCatalog.Locate("codex", $"{first}{Path.PathSeparator}{second}", ".EXE", installed.Contains));
    }

    [Fact]
    public void NoPathMeansNoAgents() =>
        Assert.Empty(AgentCatalog.Installed(null, ".EXE", _ => true));

    [Fact]
    public void AnUnusablePathEntryDoesNotStopTheSearch()
    {
        // A PATH with a quoted entry and an empty one is common on Windows.
        var bin = Path.Combine(_root, "bin");
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(bin, "kimi.exe"),
        };
        var path = $"\"{bin}\"{Path.PathSeparator}{Path.PathSeparator}   ";

        Assert.Single(AgentCatalog.Installed(path, ".EXE", installed.Contains));
    }

    [Fact]
    public void AShimIsDetectedWhateverCaseItsExtensionHas()
    {
        var agent = AgentCatalog.Known.Single(a => a.Id == "claude");
        Assert.True(new InstalledAgent(agent, @"C:\bin\claude.CMD").IsShim);
        Assert.True(new InstalledAgent(agent, @"C:\bin\claude.Bat").IsShim);
        Assert.False(new InstalledAgent(agent, @"C:\bin\claude.exe").IsShim);
    }

    [Fact]
    public void EveryKnownAgentHasADistinctDestinationId()
    {
        var ids = AgentCatalog.Known.Select(agent => agent.DestinationId).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.StartsWith("cli:", id));
    }
}
