using Patchthrough.Core;

namespace Patchthrough.Core.Tests;

/// <summary>
/// The state file decides which handoff destination the primary button points
/// at, so its ordering is user-visible. It also has to survive being damaged:
/// none of it is worth failing a launch over.
/// </summary>
public sealed class AppStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pt-state-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _path;

    public AppStateTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "state.json");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ValuesSurviveAReload()
    {
        var state = AppState.Load(_path);
        state.HandoffRepository = @"D:\code\app";
        state.WindowFrame = "200,300,952,721";

        var reloaded = AppState.Load(_path);

        Assert.Equal(@"D:\code\app", reloaded.HandoffRepository);
        Assert.Equal("200,300,952,721", reloaded.WindowFrame);
    }

    [Fact]
    public void AMissingFileIsAnEmptyStateNotAFailure()
    {
        // First launch. Nothing has been saved yet.
        var state = AppState.Load(Path.Combine(_root, "never-written.json"));

        Assert.Null(state.HandoffRepository);
        Assert.Null(state.WindowFrame);
        Assert.Empty(state.RankedDestinations());
    }

    [Fact]
    public void ADamagedFileIsAnEmptyStateNotAFailure()
    {
        File.WriteAllText(_path, "{ not json");

        // Losing a window position must never stop the app from starting.
        var state = AppState.Load(_path);

        Assert.Null(state.WindowFrame);
        // And it recovers: the next save replaces the damaged file.
        state.WindowFrame = "0,0,940,720";
        Assert.Equal("0,0,940,720", AppState.Load(_path).WindowFrame);
    }

    [Fact]
    public void UseCountsRankDestinationsMostUsedFirst()
    {
        var state = AppState.Load(_path);
        state.RecordUse("cli:claude");
        state.RecordUse("gui:chatgpt");
        state.RecordUse("cli:claude");
        state.RecordUse("cli:codex");
        state.RecordUse("cli:claude");

        Assert.Equal(3, state.UseCount("cli:claude"));
        Assert.Equal(1, state.UseCount("gui:chatgpt"));
        Assert.Equal(0, state.UseCount("cli:opencode"));
        // This order is what fills the "Most used" section and picks the
        // promoted one-click row.
        Assert.Equal(["cli:claude", "cli:codex", "gui:chatgpt"], state.RankedDestinations());
    }

    [Fact]
    public void TiedDestinationsKeepAStableOrder()
    {
        var state = AppState.Load(_path);
        state.RecordUse("gui:chatgpt");
        state.RecordUse("cli:claude");

        // Both used once. A menu that reordered itself between openings would be
        // unusable, so ties break on the id.
        Assert.Equal(["cli:claude", "gui:chatgpt"], state.RankedDestinations());
    }

    [Fact]
    public void RecordingAUseAlsoRetargetsThePrimaryButton()
    {
        var state = AppState.Load(_path);
        state.RecordUse("cli:claude");
        state.RecordUse("gui:chatgpt");

        // The split button follows the last destination used, not the most used.
        Assert.Equal("gui:chatgpt", AppState.Load(_path).LastDestination);
    }

    [Fact]
    public void EachSuppressedWarningIsIndependent()
    {
        var state = AppState.Load(_path);
        state.Suppress("cloud-upload.m365");

        var reloaded = AppState.Load(_path);
        Assert.True(reloaded.IsSuppressed("cloud-upload.m365"));
        // Agreeing that one site may hold a transcript says nothing about
        // another site.
        Assert.False(reloaded.IsSuppressed("cloud-upload.chatgpt"));
        Assert.False(reloaded.IsSuppressed("manual-paste.claude"));
    }

    [Fact]
    public void ClearingAValueRemovesItRatherThanStoringABlank()
    {
        var state = AppState.Load(_path);
        state.HandoffRepository = @"D:\code\app";

        state.HandoffRepository = null;

        Assert.Null(AppState.Load(_path).HandoffRepository);
        Assert.DoesNotContain("handoff.repo", File.ReadAllText(_path));
    }

    [Fact]
    public void SavingIsAtomicAndLeavesNoTemporaryFile()
    {
        var state = AppState.Load(_path);
        state.RecordUse("cli:claude");

        // A half-written file reads as damaged, which silently forgets every
        // ranking the user built up.
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void UnknownKeysInTheFileSurviveASave()
    {
        // A newer build may have written keys this one does not model. Dropping
        // them would reset that build's state on every launch of this one.
        File.WriteAllText(_path, """{ "future.setting": "keep me", "window.frame": "0,0,10,10" }""");

        AppState.Load(_path).WindowFrame = "1,1,20,20";

        Assert.Contains("future.setting", File.ReadAllText(_path));
    }
}
