using System.Globalization;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Git;

/// <summary>
/// Read-only access to the working tree the analysed workspace sits in.
/// </summary>
/// <remarks>
/// <para>
/// Everything here goes through <see cref="GitCommandRunner"/>, so nothing this type can
/// be asked to do writes to the repository. There is deliberately no counterpart for
/// committing, pushing, pulling, resetting, checking out, stashing or cleaning: CodeAtlas
/// reports what Git says and never changes it.
/// </para>
/// <para>
/// A repository with no commits yet is a normal state rather than an error: <c>HEAD</c>
/// does not resolve, so there is no baseline, and every path is simply reported by status.
/// </para>
/// </remarks>
public sealed class GitRepository
{
    /// <summary>
    /// Field separator for the log format: a unit separator, which cannot occur in a commit
    /// subject or an author name and so needs no escaping on the way back. Git writes it
    /// from the <c>%x1f</c> placeholder, so the format string stays printable.
    /// </summary>
    private const char Separator = (char)0x1f;

    private const string LogFormat = "--pretty=format:%H%x1f%h%x1f%an%x1f%aI%x1f%s";

    private GitRepository(string root) => Root = root;

    /// <summary>Absolute path of the working tree root.</summary>
    public string Root { get; }

    /// <summary>
    /// Opens the working tree containing <paramref name="path"/>, or returns <c>null</c>
    /// when the path is not under Git or Git is not installed.
    /// </summary>
    public static GitRepository? Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (Workspace.GitRepositoryDetector.FindRepositoryRoot(path) is not { } root)
        {
            return null;
        }

        var repository = new GitRepository(root);

        // Proves the executable is present and the directory really is a working tree, so
        // every later call can treat a failure as something worth reporting rather than as
        // "there is no Git here".
        try
        {
            return repository.Run("rev-parse", "--is-inside-work-tree").Succeeded ? repository : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The current branch, the short <c>HEAD</c>, and every path that differs from it.</summary>
    public GitStatus GetStatus(CancellationToken cancellationToken = default)
    {
        var head = Run(cancellationToken, "rev-parse", "--short", "HEAD");
        var branch = Run(cancellationToken, "rev-parse", "--abbrev-ref", "HEAD");

        // "HEAD" is what --abbrev-ref reports for a detached head; the commit is then the
        // only name the checkout has.
        var detached = branch.Text is "HEAD";
        var name = detached
            ? head.Succeeded ? head.Text : "(detached)"
            : branch.Succeeded && branch.Text.Length > 0 ? branch.Text : "(no commits)";

        return new GitStatus(
            name,
            detached,
            head.Succeeded ? head.Text : null,
            GetFileStatuses(cancellationToken));
    }

    /// <summary>
    /// Every path differing from the index or from <c>HEAD</c>, untracked files included.
    /// </summary>
    /// <remarks>
    /// <c>-z</c> is what makes this safe to parse: it separates entries with NUL and
    /// suppresses the quoting Git otherwise applies to unusual path characters, so a path
    /// arrives exactly as it is on disk.
    /// </remarks>
    private IReadOnlyList<GitFileStatus> GetFileStatuses(CancellationToken cancellationToken)
    {
        var result = Run(cancellationToken, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        if (!result.Succeeded)
        {
            return [];
        }

        var entries = result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var files = new List<GitFileStatus>();

        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 4)
            {
                continue;
            }

            var index = entry[0];
            var tree = entry[1];
            var path = entry[3..];

            // A rename or copy is followed by its source path as a separate NUL-terminated
            // entry, which is why this loop can consume two.
            string? oldPath = null;
            if ((index is 'R' or 'C' || tree is 'R' or 'C') && i + 1 < entries.Length)
            {
                oldPath = entries[++i];
            }

            files.Add(new GitFileStatus
            {
                Path = path,
                FullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, path)),
                Change = Classify(index, tree),
                OldPath = oldPath,
                IsStaged = index is not (' ' or '?'),
                IsUnstaged = tree is not ' ',
            });
        }

        return files
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Maps a porcelain XY pair onto one change. The index side is read first, because a
    /// file added to the index and then edited is still an addition.
    /// </summary>
    private static GitFileChange Classify(char index, char tree) => (index, tree) switch
    {
        ('?', _) or (_, '?') => GitFileChange.Untracked,
        ('U', _) or (_, 'U') or ('A', 'A') or ('D', 'D') => GitFileChange.Conflicted,
        ('R', _) => GitFileChange.Renamed,
        ('C', _) => GitFileChange.Copied,
        ('A', _) => GitFileChange.Added,
        ('D', _) or (_, 'D') => GitFileChange.Deleted,
        ('T', _) or (_, 'T') => GitFileChange.TypeChanged,
        _ => GitFileChange.Modified,
    };

    /// <summary>
    /// The whole difference between the working tree and <c>HEAD</c>, staged and unstaged
    /// together.
    /// </summary>
    /// <remarks>
    /// <c>HEAD</c> is the baseline the reader means by "what have I changed", and it is
    /// also the only one that lines up with the index: the index was built from the files
    /// on disk, so the new side of a diff against <c>HEAD</c> is in the same coordinate
    /// space as a recorded declaration span. Untracked files have no baseline and so never
    /// appear here — they are reported through status instead.
    /// </remarks>
    public IReadOnlyList<FileDiff> GetDiff(CancellationToken cancellationToken = default)
    {
        var result = Run(cancellationToken, "diff", "--no-color", "--find-renames", "HEAD", "--");

        return result.Succeeded ? DiffParser.Parse(result.StandardOutput) : [];
    }

    /// <summary>The most recent commits reachable from <c>HEAD</c>.</summary>
    public IReadOnlyList<GitCommit> GetRecentCommits(int count = 20, CancellationToken cancellationToken = default) =>
        ReadCommits(cancellationToken, "log", Limit(count), LogFormat, "--no-color");

    /// <summary>
    /// The commits that touched one path, which is the history of a file the reader has
    /// selected. Renames are followed, so the history does not stop where the file moved.
    /// </summary>
    public IReadOnlyList<GitCommit> GetFileHistory(
        string relativePath,
        int count = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        return ReadCommits(
            cancellationToken,
            "log", Limit(count), LogFormat, "--no-color", "--follow", "--", relativePath);
    }

    /// <summary>
    /// The baseline content of a path as <c>HEAD</c> has it, or <c>null</c> when it has
    /// none — a new file, or a repository with no commits.
    /// </summary>
    public string? GetBaselineText(string relativePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        // Git spells paths with forward slashes in a revision specifier on every platform.
        var result = Run(cancellationToken, "show", $"HEAD:{relativePath.Replace('\\', '/')}");

        return result.Succeeded ? result.StandardOutput : null;
    }

    private static string Limit(int count) =>
        $"--max-count={Math.Clamp(count, 1, 500).ToString(CultureInfo.InvariantCulture)}";

    private IReadOnlyList<GitCommit> ReadCommits(CancellationToken cancellationToken, params string[] arguments)
    {
        var result = Run(cancellationToken, arguments);
        if (!result.Succeeded)
        {
            return [];
        }

        var commits = new List<GitCommit>();

        foreach (var line in result.Lines)
        {
            var fields = line.Split(Separator);
            if (fields.Length < 5)
            {
                continue;
            }

            commits.Add(new GitCommit(
                fields[0],
                fields[1],
                fields[2],
                DateTimeOffset.TryParse(fields[3], CultureInfo.InvariantCulture, out var when) ? when : default,
                fields[4]));
        }

        return commits;
    }

    private GitCommandResult Run(params string[] arguments) =>
        GitCommandRunner.Run(Root, arguments);

    private GitCommandResult Run(CancellationToken cancellationToken, params string[] arguments) =>
        GitCommandRunner.Run(Root, arguments, cancellationToken);
}
