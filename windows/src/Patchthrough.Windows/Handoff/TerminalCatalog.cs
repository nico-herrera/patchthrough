namespace Patchthrough.Windows.Handoff;

/// <summary>A terminal an agent can be started in.</summary>
public sealed record TerminalChoice(string Id, string Label, string Executable)
{
    /// <summary>Whether this terminal is on the machine.</summary>
    public bool IsAvailable => Locate() is not null;

    /// <summary>
    /// The executable's full path, or null. Windows Terminal installs as an app
    /// execution alias, which is a zero-length reparse point on PATH rather than a
    /// real file, so PATH is the only reliable way to find it.
    /// </summary>
    public string? Locate() => Patchthrough.Core.AgentCatalog.Locate(
        Executable,
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetEnvironmentVariable("PATHEXT"));
}

/// <summary>
/// The terminals Patchthrough can start a CLI agent in.
///
/// This is the Windows counterpart to the macOS terminal setting, and it uses the
/// same `terminal` config key with values that make sense on this platform. The
/// choice matters for the same reason it does on macOS: the shell profile the agent
/// inherits comes from the terminal it is started in, not from the system default.
/// </summary>
public static class TerminalCatalog
{
    /// <summary>
    /// Windows Terminal first. It is the default on Windows 11 and the one that
    /// handles a long prompt and Unicode without configuration, and it falls back
    /// on machines that do not have it.
    /// </summary>
    public static readonly IReadOnlyList<TerminalChoice> Known =
    [
        new("wt", "Windows Terminal", "wt.exe"),
        new("powershell", "Windows PowerShell", "powershell.exe"),
        new("pwsh", "PowerShell 7", "pwsh.exe"),
        new("cmd", "Command Prompt", "cmd.exe"),
    ];

    /// <summary>The terminals this machine actually has.</summary>
    public static IReadOnlyList<TerminalChoice> Available() =>
        Known.Where(choice => choice.IsAvailable).ToList();

    /// <summary>
    /// The terminal to use: the configured one when it is present, otherwise the
    /// best one that is. A configured terminal that has been uninstalled falls back
    /// rather than failing the handoff.
    /// </summary>
    public static TerminalChoice Resolve(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var chosen = Known.FirstOrDefault(choice =>
                string.Equals(choice.Id, configured, StringComparison.OrdinalIgnoreCase)
                && choice.IsAvailable);
            if (chosen is not null) return chosen;
        }
        // PowerShell is the guaranteed floor: it ships with every supported Windows.
        return Available().FirstOrDefault() ?? Known.Single(choice => choice.Id == "powershell");
    }
}
