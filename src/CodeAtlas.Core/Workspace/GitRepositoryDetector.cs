namespace CodeAtlas.Core.Workspace;

/// <summary>
/// Detects whether a path sits inside a Git working tree by walking up for a
/// <c>.git</c> entry. Read-only and dependency-free: CodeAtlas never runs Git.
/// </summary>
public static class GitRepositoryDetector
{
    /// <summary>Returns the working tree root, or <c>null</c> when the path is not under Git.</summary>
    public static string? FindRepositoryRoot(string startPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startPath);

        var directory = Directory.Exists(startPath)
            ? new DirectoryInfo(startPath)
            : new FileInfo(startPath).Directory;

        while (directory is not null)
        {
            var git = Path.Combine(directory.FullName, ".git");

            // A directory in a normal clone; a file containing "gitdir:" in a
            // worktree or submodule. Both mean we are inside a working tree.
            if (Directory.Exists(git) || File.Exists(git))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
