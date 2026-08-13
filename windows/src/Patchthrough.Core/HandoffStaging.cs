namespace Patchthrough.Core;

/// <summary>
/// Puts a meeting's handoff document inside the repository it is about, so an
/// agent working in that repository can read it.
///
/// The document goes in `.meeting/`, and that directory is excluded through
/// `.git/info/exclude` rather than through `.gitignore`. The distinction matters:
/// `.gitignore` is tracked, so writing to it would show up as a change to the
/// user's repository and could end up in a commit or a pull request. `info/exclude`
/// is local to the clone and belongs to the person using it.
/// </summary>
public static class HandoffStaging
{
    /// <summary>The directory inside the repository, and the exclude entry.</summary>
    public const string MeetingDirectory = ".meeting";

    private const string ExcludeEntry = ".meeting/";

    private const string ExcludeComment = "# patchthrough meeting transcripts: local only, never commit";

    /// <summary>
    /// Write the handoff document into the repository and return the path written.
    /// </summary>
    /// <param name="sessionId">
    /// The session's folder name, which becomes the file name. Anything outside
    /// letters, digits, dot, underscore and hyphen is replaced, so a meeting the
    /// user named cannot produce a path with a separator or a quote in it.
    /// </param>
    public static string Stage(string repository, string sessionId, string document)
    {
        var resolved = Path.GetFullPath(repository);
        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"repository directory does not exist: {resolved}");
        }

        var meeting = Path.Combine(resolved, MeetingDirectory);
        Directory.CreateDirectory(meeting);
        var target = Path.Combine(meeting, FileNameFor(sessionId));
        AtomicFile.WriteText(target, document);
        ExcludeMeetingDirectory(resolved);
        return target;
    }

    /// <summary>
    /// The file name a session gets inside `.meeting`. Sanitised, because the
    /// session's name can come from a user and this value becomes a path.
    /// </summary>
    public static string FileNameFor(string sessionId)
    {
        var safe = new string(sessionId
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'
                ? character
                : '-')
            .ToArray());
        return safe + ".md";
    }

    /// <summary>
    /// The path an agent is told to read, which is relative to the repository root
    /// and uses forward slashes. A backslash would be read as an escape by some
    /// agents and is not what a repository-relative path looks like anywhere else.
    /// </summary>
    public static string RelativePathFor(string sessionId) =>
        $"{MeetingDirectory}/{FileNameFor(sessionId)}";

    /// <summary>
    /// Add `.meeting/` to the repository's local excludes, once.
    ///
    /// The git directory is resolved by reading the filesystem rather than by
    /// running git. There is no git to run in a graphical app's environment as
    /// reliably as there is in a shell, and the two cases that matter are both
    /// readable: a normal clone has a `.git` directory, and a worktree or submodule
    /// has a `.git` file holding `gitdir: <path>`.
    ///
    /// A repository this cannot make sense of is left alone. Failing the handoff
    /// over an exclude entry would be worse than the user seeing an untracked
    /// directory.
    /// </summary>
    public static void ExcludeMeetingDirectory(string repository)
    {
        var gitDirectory = ResolveGitDirectory(repository);
        if (gitDirectory is null) return;

        try
        {
            var exclude = Path.Combine(gitDirectory, "info", "exclude");
            var existing = File.Exists(exclude) ? File.ReadAllText(exclude) : "";
            if (existing.Split('\n').Any(line => line.TrimEnd('\r') == ExcludeEntry)) return;

            var separator = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
            Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
            File.WriteAllText(exclude, $"{existing}{separator}\n{ExcludeComment}\n{ExcludeEntry}\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A read-only or unusual repository still gets its transcript.
        }
    }

    /// <summary>
    /// The repository's git directory, or null when this is not a repository.
    /// </summary>
    public static string? ResolveGitDirectory(string repository)
    {
        try
        {
            var candidate = Path.Combine(Path.GetFullPath(repository), ".git");
            if (Directory.Exists(candidate)) return candidate;
            if (!File.Exists(candidate)) return null;

            // A worktree or submodule: the file holds "gitdir: <path>", which may
            // be relative to the repository.
            var pointer = File.ReadAllText(candidate).Trim();
            const string prefix = "gitdir:";
            if (!pointer.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var target = pointer[prefix.Length..].Trim();
            if (target.Length == 0) return null;
            var absolute = Path.IsPathRooted(target)
                ? target
                : Path.GetFullPath(Path.Combine(Path.GetFullPath(repository), target));
            return Directory.Exists(absolute) ? absolute : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
