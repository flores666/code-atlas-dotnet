using CodeAtlas.Core.Indexing;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;
using Microsoft.Data.Sqlite;

namespace CodeAtlas.Core.Tests;

/// <summary>
/// Exercises the whole MVP path against a real solution on disk: load with MSBuild,
/// index, persist, reopen, search, inspect and navigate.
/// </summary>
/// <remarks>
/// Runs in its own collection because MSBuildLocator registers process-wide state.
/// </remarks>
[Collection(nameof(MSBuildCollection))]
public class IndexingServiceTests : IClassFixture<SampleRepository>, IDisposable
{
    private readonly SampleRepository _repository;
    private readonly string _cacheRoot = Directory.CreateTempSubdirectory("codeatlas-cache").FullName;

    public IndexingServiceTests(SampleRepository repository) => _repository = repository;

    private string CachePath => Path.Combine(_cacheRoot, "index.db");

    private async Task<IndexingResult> IndexAsync(string workspacePath)
    {
        var target = WorkspaceLocator.Resolve(workspacePath);
        Assert.NotNull(target);

        return await new IndexingService().BuildAsync(
            target,
            CachePath,
            progress: null,
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("sln")]
    [InlineData("slnx")]
    public async Task Indexes_every_project_a_solution_can_load(string format)
    {
        var path = format == "sln" ? _repository.SolutionPath : _repository.SolutionXmlPath;

        var result = await IndexAsync(path);

        using var database = SymbolIndexDatabase.Open(CachePath);
        var projects = database.GetProjects().Select(p => p.Name).ToList();

        Assert.Contains("Core", projects);
        Assert.Contains("App", projects);
        Assert.Equal(path, result.Metadata.SourcePath);
        Assert.True(result.Metadata.SymbolCount > 0);
    }

    [Fact]
    public async Task Keeps_indexing_after_a_project_fails_to_load()
    {
        var result = await IndexAsync(_repository.SolutionPath);

        // The unloadable project is reported rather than swallowed...
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == Model.DiagnosticSeverity.Error &&
            d.Message.Contains("Broken.csproj", StringComparison.OrdinalIgnoreCase));

        using var database = SymbolIndexDatabase.Open(CachePath);

        // ...and the healthy projects are still fully indexed.
        Assert.NotEmpty(database.Search("Calculator"));
        Assert.NotEmpty(database.Search("Runner"));
        Assert.Contains(database.GetDiagnostics(), d =>
            d.Message.Contains("Broken.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Finds_a_type_by_name_and_reports_where_it_is_declared()
    {
        await IndexAsync(_repository.SolutionPath);

        using var database = SymbolIndexDatabase.Open(CachePath);
        var calculator = Assert.Single(database.Search("Calculator"), s => s.Kind == IndexedSymbolKind.Class);

        Assert.Equal("Sample.Core.Calculator", calculator.FullyQualifiedName);
        Assert.Equal("Sample.Core", calculator.Namespace);
        Assert.Equal("Core", calculator.ProjectName);
        Assert.Equal("Public", calculator.Accessibility);
        Assert.Equal(_repository.CalculatorSourcePath, calculator.FilePath);
        Assert.True(File.Exists(calculator.FilePath));

        // The recorded line really is the declaration.
        var line = File.ReadAllLines(calculator.FilePath)[calculator.Line!.Value - 1];
        Assert.Contains("class Calculator", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Links_relations_across_project_boundaries()
    {
        await IndexAsync(_repository.SolutionPath);

        using var database = SymbolIndexDatabase.Open(CachePath);
        var @interface = Assert.Single(database.Search("ICalculator"));
        var details = database.GetDetails(@interface.Id);

        Assert.NotNull(details);

        // Implemented in the same project...
        Assert.Contains(details.Implementors, l => l.FullyQualifiedName == "Sample.Core.Calculator");

        // ...and referenced from another one, resolved to a navigable symbol.
        Assert.Contains(details.ReferencedBy, l => l.IsNavigable && l.FullyQualifiedName.StartsWith("Sample.App.Runner", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reuses_a_cached_index_when_the_workspace_is_reopened()
    {
        var first = await IndexAsync(_repository.SolutionPath);

        SqliteConnection.ClearAllPools();

        // Reopening is what the application does on startup: no indexing, just a read.
        using var reopened = SymbolIndexDatabase.Open(CachePath);

        Assert.True(reopened.HasUsableIndexFor(_repository.SolutionPath));
        Assert.Equal(first.Metadata.SymbolCount, reopened.ReadMetadata()?.SymbolCount);
        Assert.NotEmpty(reopened.Search("Calculator"));
    }

    [Fact]
    public async Task Indexes_a_single_project_without_a_solution()
    {
        var result = await IndexAsync(_repository.AppProjectPath);

        using var database = SymbolIndexDatabase.Open(CachePath);

        // Opening App pulls in the project it references.
        Assert.Contains(database.GetProjects(), p => p.Name == "App");
        Assert.NotEmpty(database.Search("Runner"));
        Assert.True(result.Metadata.SymbolCount > 0);
    }

    [Fact]
    public async Task Reports_progress_while_indexing()
    {
        var messages = new List<string>();
        var target = WorkspaceLocator.Resolve(_repository.SolutionPath);
        Assert.NotNull(target);

        await new IndexingService().BuildAsync(
            target,
            CachePath,
            new Progress<IndexingProgress>(p => { lock (messages) { messages.Add(p.Message); } }),
            TestContext.Current.CancellationToken);

        lock (messages)
        {
            Assert.NotEmpty(messages);
        }
    }

    [Fact]
    public void Detects_that_the_workspace_is_not_under_git()
    {
        var target = WorkspaceLocator.Resolve(_repository.SolutionPath);

        Assert.NotNull(target);
        Assert.False(target.IsGitRepository);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_cacheRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

[CollectionDefinition(nameof(MSBuildCollection), DisableParallelization = true)]
public sealed class MSBuildCollection;
