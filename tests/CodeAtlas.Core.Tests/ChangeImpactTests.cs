using System.Diagnostics;
using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Diff;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CodeAtlas.Core.Tests;

/// <summary>
/// The other half of MVP 6, over a real Git working tree: an edit becomes a set of
/// changed declarations, each carrying the tests that exercise it, and a changed method
/// nothing covers is reported as such.
/// </summary>
public class ChangeImpactTests : IDisposable
{
    private const string UserServiceSource = """
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

    private readonly string _root = Directory.CreateTempSubdirectory("codeatlas-diff").FullName;
    private readonly string _cache = Directory.CreateTempSubdirectory("codeatlas-diff-cache").FullName;

    private string DatabasePath => Path.Combine(_cache, "index.db");

    public ChangeImpactTests()
    {
        Write("src/Shop/UserService.cs", UserServiceSource);
        Write("src/Shop/Legacy/PricingEngine.cs", PricingSource);
        Write("tests/Frameworks.cs", FrameworkSource);
        Write("tests/UserServiceTests.cs", TestSource);

        Git("init", "-q");
        Git("add", ".");
        Git("-c", "user.email=test@codeatlas", "-c", "user.name=CodeAtlas", "commit", "-q", "-m", "initial");
    }

    /// <summary>Indexes the committed tree, the way a workspace is indexed before it is edited.</summary>
    private async Task<SymbolIndexDatabase> IndexAsync()
    {
        var projects = TestProjectFactory.CreateSolution(
            _root,
            new TestProjectSpec(
                "Shop",
                [
                    ("src/Shop/UserService.cs", UserServiceSource),
                    ("src/Shop/Legacy/PricingEngine.cs", PricingSource),
                ],
                []),
            new TestProjectSpec(
                "Shop.Tests",
                [
                    ("tests/Frameworks.cs", FrameworkSource),
                    ("tests/UserServiceTests.cs", TestSource),
                ],
                ["Shop"]));

        using (var writer = SymbolIndexDatabase.Open(DatabasePath))
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

            session.Complete(Path.Combine(_root, "Shop.sln"));
        }

        return SymbolIndexDatabase.Open(DatabasePath);
    }

    [Fact]
    public async Task Maps_an_edited_body_to_the_method_it_belongs_to_and_the_tests_over_it()
    {
        using var database = await IndexAsync();

        // An edit inside a body, on neither declaration line: only the span says which
        // method this is.
        Replace("src/Shop/UserService.cs", "return _repository.Load(id);", "return _repository.Load(id + 0);");

        var impact = await new ChangeImpactService(database)
            .AnalyseAsync(_root, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(impact.Error);

        var changed = Assert.Single(impact.Changes);

        Assert.Equal("Shop.UserService.Get(System.Int32)", changed.Symbol.FullyQualifiedName);
        Assert.True(changed.IsMeaningful);
        Assert.False(changed.IsUntested);

        var test = Assert.Single(changed.Tests);
        Assert.Equal("Get_returns_the_user()", test.Test.Display);
        Assert.True(test.IsExact);
    }

    [Fact]
    public async Task Warns_about_a_changed_method_no_test_reaches()
    {
        using var database = await IndexAsync();

        Replace("src/Shop/Legacy/PricingEngine.cs", "return net;", "return net * 1m;");

        var impact = await new ChangeImpactService(database)
            .AnalyseAsync(_root, cancellationToken: TestContext.Current.CancellationToken);

        var untested = Assert.Single(impact.Untested);

        Assert.Equal("Shop.Legacy.PricingEngine.Quote(System.Decimal)", untested.Symbol.FullyQualifiedName);
        Assert.Empty(untested.Tests);
    }

    [Fact]
    public async Task Reports_the_type_when_the_change_is_outside_every_member()
    {
        using var database = await IndexAsync();

        Replace("src/Shop/Legacy/PricingEngine.cs", "public class PricingEngine", "public sealed class PricingEngine");

        var impact = await new ChangeImpactService(database)
            .AnalyseAsync(_root, cancellationToken: TestContext.Current.CancellationToken);

        var changed = Assert.Single(impact.Changes);

        Assert.Equal("Shop.Legacy.PricingEngine", changed.Symbol.FullyQualifiedName);

        // A type is not something a test is expected to cover on its own, so it is listed
        // without being warned about.
        Assert.False(changed.IsMeaningful);
        Assert.Empty(impact.Untested);
    }

    [Fact]
    public async Task Does_not_warn_about_a_records_data_shape()
    {
        using var database = await IndexAsync();

        Replace("src/Shop/Legacy/PricingEngine.cs", "record PriceRequest(decimal Net)", "record PriceRequest(decimal Amount)");

        var impact = await new ChangeImpactService(database)
            .AnalyseAsync(_root, cancellationToken: TestContext.Current.CancellationToken);

        // The primary constructor is the record's shape, not behaviour a test could catch
        // going wrong, so the change is listed and never warned about.
        Assert.NotEmpty(impact.Changes);
        Assert.Empty(impact.Untested);
    }

    [Fact]
    public async Task Reports_a_changed_file_the_index_has_never_seen()
    {
        using var database = await IndexAsync();

        Write("src/Shop/Added.cs", "namespace Shop; public class Added { }");
        Git("add", "src/Shop/Added.cs");

        var impact = await new ChangeImpactService(database)
            .AnalyseAsync(_root, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["src/Shop/Added.cs"], impact.UnmappedFiles);
        Assert.Empty(impact.Changes);
    }

    [Fact]
    public async Task Says_why_a_diff_could_not_be_read()
    {
        using var database = await IndexAsync();

        var impact = await new ChangeImpactService(database)
            .AnalyseAsync(_root, "no-such-ref", TestContext.Current.CancellationToken);

        Assert.NotNull(impact.Error);
        Assert.Empty(impact.Changes);
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>Edits within a line, so the declarations around it keep their positions.</summary>
    private void Replace(string relativePath, string original, string replacement)
    {
        var path = Path.Combine(_root, relativePath);
        var content = File.ReadAllText(path);

        Assert.Contains(original, content, StringComparison.Ordinal);
        File.WriteAllText(path, content.Replace(original, replacement, StringComparison.Ordinal));
    }

    private void Git(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var directory in (string[])[_root, _cache])
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        GC.SuppressFinalize(this);
    }
}
