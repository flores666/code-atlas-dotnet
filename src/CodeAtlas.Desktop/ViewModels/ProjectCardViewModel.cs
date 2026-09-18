using CodeAtlas.Core.Model;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>
/// One card of the strip under the title bar: the open solution, then each project in
/// it. Selecting a card scopes the explorer and the endpoint list to it.
/// </summary>
/// <remarks>
/// The solution's own card is what "everything" is, rather than a separate all-projects
/// pseudo-entry: a solution is the thing that was opened, and its projects are what it
/// turned out to contain.
/// </remarks>
public sealed class ProjectCardViewModel
{
    private ProjectCardViewModel(string name, string detail, string metric, bool isLoaded, string? projectName)
    {
        Name = name;
        Detail = detail;
        Metric = metric;
        IsLoaded = isLoaded;
        ProjectName = projectName;
    }

    public string Name { get; }

    /// <summary>Where it lives, or what it holds for the solution card.</summary>
    public string Detail { get; }

    public string Metric { get; }

    /// <summary>False on a project that failed to load, whose symbols are absent.</summary>
    public bool IsLoaded { get; }

    /// <summary>The project this card scopes to; <c>null</c> on the solution card.</summary>
    public string? ProjectName { get; }

    public bool IsSolution => ProjectName is null;

    public static ProjectCardViewModel ForSolution(WorkspaceTarget target, int projectCount, int symbolCount) =>
        new(
            target.DisplayName,
            Shorten(Path.GetDirectoryName(target.Path)),
            $"{projectCount} project{(projectCount == 1 ? "" : "s")} · {symbolCount:N0} symbols",
            isLoaded: true,
            projectName: null);

    public static ProjectCardViewModel ForProject(IndexedProject project) =>
        new(
            project.Name,
            Shorten(Path.GetDirectoryName(project.FilePath)),
            $"{project.SymbolCount:N0} symbols",
            project.Loaded,
            project.Name);

    /// <summary>Home-relative, because an absolute path is all prefix on a card this size.</summary>
    private static string Shorten(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return string.Empty;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return home.Length > 0 && directory.StartsWith(home, StringComparison.Ordinal)
            ? string.Concat("~", directory.AsSpan(home.Length))
            : directory;
    }
}
