using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Git;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CodeAtlas.Core.Tests;

/// <summary>
/// What a working-tree change means for the tests over it: every changed declaration
/// carries the tests that exercise it, and a changed method of production code that
/// nothing covers is the one thing warned about.
/// </summary>
/// <remarks>
/// Run against a real Git working tree and a real index, because the join depends on both
/// being in the same line numbering — the same reason
/// <see cref="RepositoryChangeTests"/> works this way.
/// </remarks>
public class ChangedSymbolCoverageTests : IDisposable
{
    private const string ShopSource = """
        namespace Shop;

        public interface IUserRepository
        {
            User Load(int id);
        }

        public class User
        {
            public int Id { get; set; }
        }

        public class UserService
        {
            private readonly IUserRepository _repository;

            public UserService(IUserRepository repository) => _repository = repository;

            public User Get(int id)
            {
                return _repository.Load(id);
            }
        }
        """;

    private const string PricingSource = """
        namespace Shop.Legacy;

        public sealed record PriceRequest(decimal Net);

        public class PricingEngine
        {
            public decimal Quote(decimal net)
            {
                return net;
            }
        }
        """;

    private const string FrameworkSource = """
        namespace Xunit
        {
            public sealed class FactAttribute : System.Attribute { }
        }
        """;

    private const string TestSource = """
        using Shop;
        using Xunit;

        namespace Shop.Tests;

        public class UserServiceTests
        {
            [Fact]
            public void Get_returns_the_user() => new UserService(null!).Get(1);
        }
        """;

    private readonly GitWorkingTree _tree = new();
    private readonly string _cacheRoot = Directory.CreateTempSubdirectory("codeatlas-coverage").FullName;

    private string IndexPath => Path.Combine(_cacheRoot, "index.db");

    public ChangedSymbolCoverageTests()
    {
        _tree.Write("src/Shop/UserService.cs", ShopSource);
        _tree.Write("src/Shop/Legacy/PricingEngine.cs", PricingSource);
        _tree.Write("tests/Frameworks.cs", FrameworkSource);
        _tree.Write("tests/UserServiceTests.cs", TestSource);
        _tree.Commit("baseline");
    }

    /// <summary>
    /// Indexes the tree as it now stands — production project and the test project built
    /// against it — then reads Git and maps it on.
    /// </summary>
    private async Task<(SymbolIndexDatabase Database, RepositoryChanges Changes)> AnalyseAsync()
    {
        var projects = TestProjectFactory.CreateSolution(
            _tree.Root,
            new TestProjectSpec(
                "Shop",
                [
                    ("src/Shop/UserService.cs", File.ReadAllText(_tree.PathOf("src/Shop/UserService.cs"))),
                    ("src/Shop/Legacy/PricingEngine.cs", File.ReadAllText(_tree.PathOf("src/Shop/Legacy/PricingEngine.cs"))),
                ],
                []),
            new TestProjectSpec(
                "Shop.Tests",
                [
                    ("tests/Frameworks.cs", FrameworkSource),
                    ("tests/UserServiceTests.cs", TestSource),
                ],
                ["Shop"]));

        using (var writer = SymbolIndexDatabase.Open(IndexPath))
        using (var session = writer.BeginRebuild())
        {
            foreach (var project in projects)
            {
                var data = await SymbolCollector.CollectAsync(project, TestContext.Current.CancellationToken);
                var projectId = session.AddProject(data.Project);

                session.AddSymbols(projectId, data.Symbols);
                session.AddProjectReferences(projectId, data.ProjectReferences);
                session.AddRelations(projectId, data.Relations);
            }

            session.Complete(Path.Combine(_tree.Root, "Shop.sln"));
        }

        var database = SymbolIndexDatabase.Open(IndexPath);
        var repository = GitRepository.Open(_tree.Root);
        Assert.NotNull(repository);

        return (database, RepositoryChangeAnalyzer.Analyze(
            repository,
            database,
            TestContext.Current.CancellationToken));
    }

    /// <summary>Edits within a line, so the declarations around it keep their positions.</summary>
    private void Replace(string relativePath, string original, string replacement)
    {
        var content = File.ReadAllText(_tree.PathOf(relativePath));

        Assert.Contains(original, content, StringComparison.Ordinal);
        _tree.Write(relativePath, content.Replace(original, replacement, StringComparison.Ordinal));
    }

    private static ChangedSymbol Named(RepositoryChanges changes, string display, string container) =>
        Assert.Single(changes.Symbols.Where(symbol =>
            symbol.Display == display && symbol.Container == container));

    [Fact]
    public async Task A_changed_method_carries_the_tests_that_exercise_it()
    {
        // An edit inside a body, on neither declaration line: only the span says which
        // method this is.
        Replace("src/Shop/UserService.cs", "return _repository.Load(id);", "return _repository.Load(id + 0);");

        var (database, changes) = await AnalyseAsync();
        using var _ = database;

        var changed = Named(changes, "Get(int id)", "Shop.UserService");

        Assert.True(changed.IsMeaningful);
        Assert.False(changed.IsUntested);

        var test = Assert.Single(changed.Tests);
        Assert.Equal("Get_returns_the_user()", test.Test.Display);
        Assert.True(test.IsExact);
    }

    [Fact]
    public async Task A_changed_method_no_test_reaches_is_warned_about()
    {
        Replace("src/Shop/Legacy/PricingEngine.cs", "return net;", "return net * 1m;");

        var (database, changes) = await AnalyseAsync();
        using var _ = database;

        var untested = Assert.Single(changes.Symbols.Where(symbol => symbol.IsUntested));

        Assert.Equal("Quote(decimal net)", untested.Display);
        Assert.Equal("Shop.Legacy.PricingEngine", untested.Container);
        Assert.Empty(untested.Tests);
    }

    [Fact]
    public async Task A_changed_type_is_listed_without_being_warned_about()
    {
        Replace(
            "src/Shop/Legacy/PricingEngine.cs",
            "public class PricingEngine",
            "public sealed class PricingEngine");

        var (database, changes) = await AnalyseAsync();
        using var _ = database;

        var changed = Named(changes, "PricingEngine", "Shop.Legacy");

        // A type is not something a test is expected to cover on its own.
        Assert.False(changed.IsMeaningful);
        Assert.DoesNotContain(changes.Symbols, symbol => symbol.IsUntested);
    }

    [Fact]
    public async Task A_records_data_shape_is_not_warned_about()
    {
        Replace(
            "src/Shop/Legacy/PricingEngine.cs",
            "record PriceRequest(decimal Net)",
            "record PriceRequest(decimal Amount)");

        var (database, changes) = await AnalyseAsync();
        using var _ = database;


        // The primary constructor is the record's shape, not behaviour a test could catch
        // going wrong, so the change is listed and never warned about.
        Assert.NotEmpty(changes.Symbols);
        Assert.DoesNotContain(changes.Symbols, symbol => symbol.IsUntested);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SqliteConnection.ClearAllPools();
        _tree.Dispose();

        try
        {
            Directory.Delete(_cacheRoot, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
