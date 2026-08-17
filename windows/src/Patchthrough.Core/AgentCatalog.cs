namespace Patchthrough.Core;

/// <summary>How an agent takes its opening prompt.</summary>
public enum AgentPromptStyle
{
    /// <summary>The prompt is the first argument.</summary>
    Argument,

    /// <summary>The prompt follows a `run` subcommand.</summary>
    RunSubcommand,

    /// <summary>
    /// The agent takes no opening prompt. The prompt goes on the clipboard and the
    /// user pastes it once the agent has started.
    /// </summary>
    Clipboard,
}

/// <summary>A coding agent that can be handed a transcript.</summary>
public sealed record Agent(string Id, string Label, AgentPromptStyle Style)
{
    /// <summary>The destination id used in menus and in the use counts.</summary>
    public string DestinationId => $"cli:{Id}";
}

/// <summary>An agent that was found on this machine, and where.</summary>
public sealed record InstalledAgent(Agent Agent, string ExecutablePath)
{
    /// <summary>
    /// True when the executable is a shim script rather than a real program.
    ///
    /// npm installs a Windows agent as a `.cmd` shim, and cmd.exe parses that
    /// shim's argument line. cmd.exe also treats a newline as the end of a command,
    /// and every prompt here has newlines, so a shim agent cannot take its prompt
    /// as an argument no matter how it is quoted. It takes it from the clipboard
    /// instead. The npm CLI reaches the same conclusion in `launchAgent`.
    /// </summary>
    public bool IsShim =>
        ExecutablePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
        || ExecutablePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>How the prompt actually has to be delivered on this machine.</summary>
    public AgentPromptStyle EffectiveStyle => IsShim ? AgentPromptStyle.Clipboard : Agent.Style;
}

/// <summary>
/// The agents Patchthrough knows how to start, and which of them this machine has.
///
/// The table mirrors KNOWN_AGENTS in cli/src/patchthrough.js. Keep the two in step:
/// the CLI and the app are two doors onto the same handoff, and an agent that only
/// one of them knows about is a door that appears and disappears depending on how
/// the user got there.
/// </summary>
public static class AgentCatalog
{
    public static readonly IReadOnlyList<Agent> Known =
    [
        new("claude", "claude", AgentPromptStyle.Argument),
        new("copilot", "copilot", AgentPromptStyle.Argument),
        new("codex", "codex", AgentPromptStyle.Argument),
        new("cursor-agent", "cursor-agent", AgentPromptStyle.Argument),
        new("opencode", "opencode", AgentPromptStyle.RunSubcommand),
        new("kimi", "kimi", AgentPromptStyle.Clipboard),
    ];

    /// <summary>
    /// The agents on this machine's PATH.
    /// </summary>
    /// <param name="path">
    /// The PATH value. Injected so this is testable, and so a caller can pass the
    /// PATH a terminal would see rather than the one the app inherited.
    /// </param>
    /// <param name="pathExtensions">
    /// The PATHEXT value. On Windows an executable is found by appending one of
    /// these, and an agent installed by npm is usually a `.cmd`, so a probe that
    /// only looked for an extensionless name would find nothing.
    /// </param>
    /// <param name="exists">
    /// Whether a candidate path is a file. Injected for the same reason as PATH.
    /// </param>
    public static IReadOnlyList<InstalledAgent> Installed(
        string? path,
        string? pathExtensions = null,
        Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var found = new List<InstalledAgent>();
        foreach (var agent in Known)
        {
            var executable = Locate(agent.Id, path, pathExtensions, exists);
            if (executable is not null) found.Add(new InstalledAgent(agent, executable));
        }
        return found;
    }

    /// <summary>
    /// Where an executable named <paramref name="name"/> is, or null.
    ///
    /// The order of the extensions is the order in PATHEXT, because that is the
    /// order the shell itself resolves in: a directory holding both `claude` and
    /// `claude.cmd` has to pick the same one a terminal would.
    /// </summary>
    public static string? Locate(
        string name,
        string? path,
        string? pathExtensions = null,
        Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var directories = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var extensions = Extensions(pathExtensions);

        foreach (var directory in directories)
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0) continue;
            foreach (var extension in extensions)
            {
                string candidate;
                try { candidate = Path.Combine(trimmed, name + extension); }
                catch (ArgumentException) { continue; }   // a PATH entry with invalid characters
                if (exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static IReadOnlyList<string> Extensions(string? pathExtensions)
    {
        var configured = (pathExtensions ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(extension => extension.Trim())
            .Where(extension => extension.StartsWith('.'))
            .ToList();
        // The bare name first, then each extension. A file with no extension is
        // how a Unix-style installer or a WSL shim lands, and it is also how this
        // resolves when PATHEXT is absent.
        return ["", .. configured.Count > 0 ? configured : [".EXE", ".CMD", ".BAT"]];
    }
}
