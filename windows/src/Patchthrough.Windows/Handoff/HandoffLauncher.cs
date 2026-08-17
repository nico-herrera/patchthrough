using System.Diagnostics;
using Patchthrough.Core;

namespace Patchthrough.Windows.Handoff;

/// <summary>What happened, and what the user needs to be told about it.</summary>
/// <param name="Message">
/// A sentence for the status line. It is never empty: a handoff that appeared to do
/// nothing is the outcome this exists to prevent.
/// </param>
/// <param name="NeedsManualPaste">
/// The prompt or the file is on the clipboard and the user has to paste it. Windows
/// never synthesizes the keystroke, so this is a real instruction rather than a
/// fallback notice.
/// </param>
public sealed record HandoffResult(string Message, bool NeedsManualPaste = false);

/// <summary>
/// Starts an agent, or opens a chat site, with a meeting's transcript.
///
/// **The invariant, carried from both reference implementations: transcript-derived
/// text never reaches a command interpreter.** A transcript is speech from a
/// meeting, so it can contain anything, including quotes, semicolons, backticks,
/// and newlines. Every path below either writes the text to a file and has the
/// agent read the file, or puts it on the clipboard. Nothing interpolates it into a
/// command line. The npm CLI states the same rule; see `launchAgent` and
/// `handToWeb` in cli/src/patchthrough.js.
/// </summary>
public static class HandoffLauncher
{
    /// <summary>
    /// Stage the transcript into a repository and start an agent there.
    /// </summary>
    public static HandoffResult ToAgent(
        InstalledAgent agent,
        string repository,
        string sessionId,
        string document,
        string? configuredTerminal)
    {
        HandoffStaging.Stage(repository, sessionId, document);
        var prompt = HandoffPrompt.ForStagedFile(HandoffStaging.RelativePathFor(sessionId));
        var terminal = TerminalCatalog.Resolve(configuredTerminal);

        // The prompt goes to a file, and the agent is told to read the file. The
        // alternative, passing it as an argument, would put meeting speech on a
        // command line.
        var promptFile = Path.Combine(Path.GetTempPath(), $"patchthrough-prompt-{Guid.NewGuid():N}.txt");
        File.WriteAllText(promptFile, prompt, new System.Text.UTF8Encoding(false));

        var style = agent.EffectiveStyle;
        if (style == AgentPromptStyle.Clipboard)
        {
            // An npm shim, or an agent that takes no opening prompt. cmd.exe parses
            // a shim's argument line and ends a command at a newline, so no quoting
            // makes an argument work here.
            var copied = Clipboard.SetText(prompt);
            Start(terminal, repository, ShellCommand(agent.ExecutablePath, null, promptFile));
            return new HandoffResult(
                copied
                    ? $"Started {agent.Agent.Label}. The prompt is on your clipboard, so paste it once the agent is ready."
                    : $"Started {agent.Agent.Label}. Read the prompt from {promptFile}.",
                NeedsManualPaste: copied);
        }

        Start(terminal, repository, ShellCommand(agent.ExecutablePath, style, promptFile));
        return new HandoffResult($"Started {agent.Agent.Label} in {Path.GetFileName(repository)}");
    }

    /// <summary>
    /// Open a chat site with the handoff document attached to the clipboard.
    ///
    /// Windows never auto-pastes. A synthesized keystroke has no reliable focus
    /// guarantee here: the browser may still be loading, and the keystroke would
    /// land in whatever window happened to be in front. The npm CLI refuses for the
    /// same reason. The user pastes, and the message says so.
    /// </summary>
    public static HandoffResult ToChatSite(ChatSite site, string handoffFilePath, int durationSeconds, string document)
    {
        // The file reference first, so a paste attaches a file rather than a wall of
        // text. Text is the fallback when the clipboard will not take a file, which
        // happens without an interactive desktop.
        var attached = File.Exists(handoffFilePath) && Clipboard.SetFile(handoffFilePath);
        var copiedText = !attached && Clipboard.SetText(document);

        // The prefilled prompt names an attachment, so a text fallback opens a plain
        // chat instead: telling a model to read an attached file that was never
        // attached reads as a missing upload.
        var prompt = attached ? HandoffPrompt.ForAttachedDocument(durationSeconds) : null;
        var url = DestinationCatalog.UrlFor(site, prompt);

        OpenUrl(url);

        if (attached)
        {
            return new HandoffResult(
                $"Opened {site.Label}. The transcript is on your clipboard, so paste it into the composer.",
                NeedsManualPaste: true);
        }
        return new HandoffResult(
            copiedText
                ? $"Opened {site.Label}. The transcript text is on your clipboard, so paste it into the composer."
                : $"Opened {site.Label}. The clipboard could not be used, so attach {handoffFilePath} by hand.",
            NeedsManualPaste: copiedText);
    }

    /// <summary>
    /// The command the terminal runs. PowerShell reads the prompt out of the file at
    /// run time, so the text is data to the shell rather than part of the command.
    /// </summary>
    private static string ShellCommand(string executable, AgentPromptStyle? style, string promptFile)
    {
        // Single-quoted PowerShell literals, with any embedded quote doubled. Both
        // values are paths this code produced, but quoting them correctly is what
        // keeps a space in a user's profile path from splitting the command.
        var agent = PowerShellLiteral(executable);
        var file = PowerShellLiteral(promptFile);

        return style switch
        {
            AgentPromptStyle.Argument => $"& {agent} (Get-Content -Raw -LiteralPath {file})",
            AgentPromptStyle.RunSubcommand => $"& {agent} run (Get-Content -Raw -LiteralPath {file})",
            // No prompt on the command line at all. The agent starts and the user
            // pastes.
            _ => $"& {agent}",
        };
    }

    private static string PowerShellLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// Start the terminal in the repository, running the command.
    ///
    /// Arguments go through ArgumentList, so the runtime quotes each one. Building a
    /// single argument string by hand is the mistake this avoids.
    /// </summary>
    private static void Start(TerminalChoice terminal, string workingDirectory, string command)
    {
        var executable = terminal.Locate() ?? terminal.Executable;
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };

        if (terminal.Id == "wt")
        {
            // Windows Terminal takes the profile and directory itself, then the
            // command to run inside the new tab.
            start.ArgumentList.Add("-d");
            start.ArgumentList.Add(workingDirectory);
            start.ArgumentList.Add("powershell.exe");
            start.ArgumentList.Add("-NoExit");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(command);
        }
        else if (terminal.Id == "cmd")
        {
            // cmd cannot run this form, so PowerShell runs inside it. NoExit keeps
            // the agent's session open after it starts.
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("start");
            start.ArgumentList.Add("");
            start.ArgumentList.Add("powershell.exe");
            start.ArgumentList.Add("-NoExit");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(command);
        }
        else
        {
            start.ArgumentList.Add("-NoExit");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(command);
        }

        Process.Start(start)?.Dispose();
    }

    /// <summary>
    /// Open a URL in the user's browser.
    ///
    /// UseShellExecute hands the URL to the shell as a URL. It never reaches
    /// cmd.exe, which would expand the percent escapes the prompt is full of.
    /// </summary>
    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"refusing to open a non-web URL: {url}");
        }
        Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }
}
