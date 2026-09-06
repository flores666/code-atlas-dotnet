using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Core.Tests;

public class GitRepositoryDetectorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codeatlas-git").FullName;

    [Fact]
    public void Finds_the_root_from_a_nested_file()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var nested = Path.Combine(_root, "src", "App");
        Directory.CreateDirectory(nested);
        var project = Path.Combine(nested, "App.csproj");
        File.WriteAllText(project, string.Empty);

        Assert.Equal(_root, GitRepositoryDetector.FindRepositoryRoot(project));
    }

    [Fact]
    public void Recognises_a_worktree_where_dot_git_is_a_file()
    {
        File.WriteAllText(Path.Combine(_root, ".git"), "gitdir: /elsewhere/.git/worktrees/wt");

        Assert.Equal(_root, GitRepositoryDetector.FindRepositoryRoot(_root));
    }

    [Fact]
    public void Returns_null_outside_a_repository()
    {
        Assert.Null(GitRepositoryDetector.FindRepositoryRoot(_root));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
