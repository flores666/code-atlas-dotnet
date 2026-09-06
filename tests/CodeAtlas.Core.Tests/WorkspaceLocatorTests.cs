using CodeAtlas.Core.Model;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Core.Tests;

public class WorkspaceLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codeatlas-locator").FullName;

    private string Touch(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void Resolves_a_file_that_was_picked_directly()
    {
        var project = Touch("App/App.csproj");

        var target = WorkspaceLocator.Resolve(project);

        Assert.NotNull(target);
        Assert.Equal(WorkspaceTargetKind.Project, target.Kind);
        Assert.Equal("App", target.DisplayName);
        Assert.Equal(project, target.Path);
    }

    [Fact]
    public void Ignores_a_file_that_is_not_a_solution_or_project()
    {
        Assert.Null(WorkspaceLocator.Resolve(Touch("readme.md")));
    }

    [Fact]
    public void Prefers_a_solution_over_a_project_in_the_same_directory()
    {
        Touch("App.csproj");
        var solution = Touch("App.sln");

        var target = WorkspaceLocator.Resolve(_root);

        Assert.Equal(solution, target?.Path);
        Assert.Equal(WorkspaceTargetKind.Solution, target?.Kind);
    }

    [Fact]
    public void Prefers_slnx_over_sln()
    {
        Touch("App.sln");
        var modern = Touch("App.slnx");

        Assert.Equal(modern, WorkspaceLocator.Resolve(_root)?.Path);
    }

    [Fact]
    public void Descends_into_subdirectories_when_the_root_holds_nothing()
    {
        var project = Touch("src/App/App.csproj");

        Assert.Equal(project, WorkspaceLocator.Resolve(_root)?.Path);
    }

    [Fact]
    public void Does_not_descend_past_a_solution_found_higher_up()
    {
        var solution = Touch("App.sln");
        Touch("src/App/App.csproj");

        var candidates = WorkspaceLocator.FindCandidates(_root);

        Assert.Equal([solution], candidates.Select(c => c.Path));
    }

    [Fact]
    public void Skips_build_output_directories()
    {
        Touch("src/App/bin/Debug/Generated.csproj");
        var real = Touch("src/App/App.csproj");

        Assert.Equal([real], WorkspaceLocator.FindCandidates(_root).Select(c => c.Path));
    }

    [Fact]
    public void Returns_nothing_for_a_missing_path()
    {
        Assert.Empty(WorkspaceLocator.FindCandidates(Path.Combine(_root, "absent")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
