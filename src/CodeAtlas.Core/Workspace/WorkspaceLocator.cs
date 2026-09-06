using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Workspace;

/// <summary>
/// Resolves a user-picked path — a solution file, a project file, or a directory —
/// to the workspace CodeAtlas should open.
/// </summary>
public static class WorkspaceLocator
{
    public const string SolutionExtension = ".sln";
    public const string SolutionXmlExtension = ".slnx";
    public const string ProjectExtension = ".csproj";

    private static readonly string[] IgnoredDirectories = ["bin", "obj", ".git", "node_modules", ".vs", ".idea"];

    /// <summary>Directories below the picked one are searched this deep for a solution.</summary>
    private const int MaxSearchDepth = 3;

    public static bool IsSupportedFile(string path) =>
        Path.GetExtension(path) is var ext &&
        (ext.Equals(SolutionExtension, StringComparison.OrdinalIgnoreCase) ||
         ext.Equals(SolutionXmlExtension, StringComparison.OrdinalIgnoreCase) ||
         ext.Equals(ProjectExtension, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves <paramref name="path"/> to a single workspace target, or <c>null</c>
    /// when nothing openable was found. When a directory holds several candidates the
    /// best-ranked one wins; use <see cref="FindCandidates"/> to offer a choice.
    /// </summary>
    public static WorkspaceTarget? Resolve(string path) => FindCandidates(path).FirstOrDefault();

    /// <summary>
    /// All openable targets for <paramref name="path"/>, best first: solutions before
    /// projects, shallower before deeper, then by name.
    /// </summary>
    public static IReadOnlyList<WorkspaceTarget> FindCandidates(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (File.Exists(path))
        {
            return IsSupportedFile(path) ? [CreateTarget(Path.GetFullPath(path))] : [];
        }

        if (!Directory.Exists(path))
        {
            return [];
        }

        var root = Path.GetFullPath(path);

        // Prefer anything sitting directly in the picked directory before descending.
        var found = Collect(root, depth: 0);
        if (found.Count == 0)
        {
            return [];
        }

        return found
            .OrderBy(f => Rank(f.Path))
            .ThenBy(f => f.Depth)
            .ThenBy(f => Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase)
            .Select(f => CreateTarget(f.Path))
            .ToList();
    }

    private static List<(string Path, int Depth)> Collect(string directory, int depth)
    {
        var results = new List<(string, int)>();

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return results;
        }

        foreach (var file in files)
        {
            if (IsSupportedFile(file))
            {
                results.Add((file, depth));
            }
        }

        // A solution at this level is enough; do not descend past it.
        if (results.Any(r => IsSolution(r.Item1)) || depth >= MaxSearchDepth)
        {
            return results;
        }

        IEnumerable<string> subdirectories;
        try
        {
            subdirectories = Directory.EnumerateDirectories(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return results;
        }

        foreach (var subdirectory in subdirectories)
        {
            var name = Path.GetFileName(subdirectory);
            if (IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith('.'))
            {
                continue;
            }

            results.AddRange(Collect(subdirectory, depth + 1));
        }

        return results;
    }

    private static bool IsSolution(string path) => Rank(path) < 2;

    private static int Rank(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        SolutionXmlExtension => 0,
        SolutionExtension => 1,
        _ => 2,
    };

    private static WorkspaceTarget CreateTarget(string fullPath)
    {
        var extension = Path.GetExtension(fullPath);
        var isProject = extension.Equals(ProjectExtension, StringComparison.OrdinalIgnoreCase);

        return new WorkspaceTarget
        {
            Kind = isProject ? WorkspaceTargetKind.Project : WorkspaceTargetKind.Solution,
            Path = fullPath,
            DisplayName = Path.GetFileNameWithoutExtension(fullPath),
            GitRoot = GitRepositoryDetector.FindRepositoryRoot(fullPath),
        };
    }
}
