using CodeAtlas.Core.Git;

namespace CodeAtlas.Core.Tests;

/// <summary>
/// Guards the promise that CodeAtlas never writes to the analysed repository.
/// </summary>
/// <remarks>
/// The allowlist is the enforcement, so these are the tests that would fail if someone
/// widened it: a verb that can mutate a repository must not be reachable, whatever
/// arguments a caller pairs it with.
/// </remarks>
public class GitCommandRunnerTests
{
    private readonly string _root = Directory.CreateTempSubdirectory("codeatlas-git-guard").FullName;

    [Theory]
    [InlineData("commit")]
    [InlineData("push")]
    [InlineData("pull")]
    [InlineData("reset")]
    [InlineData("checkout")]
    [InlineData("stash")]
    [InlineData("clean")]
    [InlineData("merge")]
    [InlineData("rebase")]
    [InlineData("apply")]
    [InlineData("add")]
    [InlineData("rm")]
    [InlineData("switch")]
    [InlineData("restore")]
    [InlineData("branch")]
    [InlineData("tag")]
    [InlineData("gc")]
    [InlineData("fetch")]
    [InlineData("init")]
    [InlineData("config")]
    public void Refuses_every_command_that_could_change_a_repository(string verb)
    {
        Assert.False(GitCommandRunner.IsReadOnly(verb));

        var exception = Assert.Throws<ArgumentException>(
            () => GitCommandRunner.Run(_root, [verb], TestContext.Current.CancellationToken));

        Assert.Contains("read-only", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("rev-parse")]
    [InlineData("status")]
    [InlineData("diff")]
    [InlineData("log")]
    [InlineData("show")]
    public void Allows_the_read_only_commands_the_repository_reader_needs(string verb) =>
        Assert.True(GitCommandRunner.IsReadOnly(verb));

    [Fact]
    public void Refuses_an_empty_command() =>
        Assert.Throws<ArgumentException>(
            () => GitCommandRunner.Run(_root, [], TestContext.Current.CancellationToken));

    /// <summary>
    /// A forbidden verb is refused before a process is started, so nothing reaches Git even
    /// when the working tree does not exist.
    /// </summary>
    [Fact]
    public void Refuses_a_forbidden_command_without_running_anything() =>
        Assert.Throws<ArgumentException>(() =>
            GitCommandRunner.Run(
                Path.Combine(_root, "does-not-exist"),
                ["commit", "-m", "nope"],
                TestContext.Current.CancellationToken));
}
