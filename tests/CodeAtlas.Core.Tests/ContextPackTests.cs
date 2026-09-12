using System.IO.Compression;
using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Context;
using CodeAtlas.Core.Git;
using CodeAtlas.Core.Impact;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests;

/// <summary>
/// The MVP 7 acceptance path: a developer picks a service, assembles a focused pack about
/// it, inspects exactly what is in it, and takes it away.
/// </summary>
/// <remarks>
/// The sources are written to a real directory and indexed from there, because a pack
/// carries file content: the paths a pack files things under, and the bytes in them, are
/// what is being asserted rather than the index's own bookkeeping.
/// </remarks>
public class ContextPackTests : IDisposable
{
    private const string Frameworks = """
        namespace Xunit
        {
            public sealed class FactAttribute : System.Attribute { }
        }
        """;

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

        public interface IUserService
        {
            User Get(int id);
        }

        public class UserService : IUserService
        {
            private readonly IUserRepository _repository;

            public UserService(IUserRepository repository) => _repository = repository;

            public User Get(int id) => _repository.Load(id);
        }
        """;

    private const string ControllerSource = """
        using Microsoft.AspNetCore.Mvc;

        namespace Shop;

        [Route("users")]
        public class UsersController : ControllerBase
        {
            private readonly IUserService _service;

            public UsersController(IUserService service) => _service = service;

            [HttpGet("{id}")]
            public User Get(int id) => _service.Get(id);
        }
        """;

    private const string StartupSource = """
        using Microsoft.Extensions.DependencyInjection;

        namespace Shop;

        public static class Startup
        {
            public static void Register(IServiceCollection services)
            {
                services.AddScoped<IUserService, UserService>();
            }
        }
        """;

    private const string UserServiceTestsSource = """
        using Shop;
        using Xunit;

        namespace Shop.Tests;

        public class UserServiceTests
        {
            private readonly UserService _service = new UserService(null!);

            [Fact]
            public void Get_returns_the_user() => _service.Get(1);
        }
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("codeatlas-context").FullName;
    private SymbolIndexDatabase? _database;

    private string SourceRoot => Path.Combine(_root, "src");

    // ---- the pack itself ----------------------------------------------------

    /// <summary>
    /// An empty pack is still a pack: it says what the task is and that nothing has been
    /// chosen, rather than exporting nothing at all.
    /// </summary>
    [Fact]
    public async Task Always_carries_a_task_document()
    {
        var builder = await BuilderAsync();
        builder.TaskText = "Add paging to the user list.";

        var files = builder.Render();

        var task = Assert.Single(files, file => file.Path == "Task.md");
        Assert.Contains("Add paging to the user list.", task.Text, StringComparison.Ordinal);
        Assert.Contains("Nothing has been added yet", task.Text, StringComparison.Ordinal);
    }

    /// <summary>A symbol brings the file it is declared in, filed under its own path.</summary>
    [Fact]
    public async Task Adding_a_symbol_carries_its_file()
    {
        var builder = await BuilderAsync();

        Assert.True(builder.Add(builder.CreateSymbol(await IdOfAsync("Shop.UserService"))).IsAdded);

        var files = builder.Render();

        var source = Assert.Single(files, file => file.Path == "RelevantFiles/Shop/UserService.cs");
        Assert.Contains("public class UserService", source.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same file asked for twice is carried once. Source code duplicated across a pack
    /// is the one cost a context pack exists to avoid.
    /// </summary>
    [Fact]
    public async Task Carries_each_file_once_however_many_entries_want_it()
    {
        var builder = await BuilderAsync();
        var id = await IdOfAsync("Shop.UserService");

        builder.Add(builder.CreateSymbol(id));
        builder.Add(builder.CreateSourceFile(Path.Combine(SourceRoot, "Shop", "UserService.cs")));
        builder.Add(builder.CreateDependencies(id));

        var files = builder.Render();

        Assert.Equal(3, builder.Items.Count);
        Assert.Single(files, file => file.Path == "RelevantFiles/Shop/UserService.cs");
    }

    /// <summary>
    /// A relation is described in names and locations, never in quoted code: the code is
    /// already in the pack once, under its own path.
    /// </summary>
    [Fact]
    public async Task Describes_callers_without_quoting_them()
    {
        var builder = await BuilderAsync();

        // The controller calls through the interface, which is where the call is recorded.
        Assert.True(builder.Add(builder.CreateCallers(await IdOfAsync("Shop.IUserService.Get(System.Int32)"))).IsAdded);

        var flow = Assert.Single(builder.Render(), file => file.Path == "ExecutionFlow.md");

        Assert.Contains("Get(int id)", flow.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("_repository.Load", flow.Text, StringComparison.Ordinal);
        Assert.Single(builder.Render(), file => file.Path == "RelevantFiles/Shop/UsersController.cs");
    }

    /// <summary>Tests are filed apart from the code under test, as the structure says.</summary>
    [Fact]
    public async Task Files_related_tests_under_their_own_folder()
    {
        var builder = await BuilderAsync();

        Assert.True(builder.Add(builder.CreateRelatedTests(await IdOfAsync("Shop.UserService"))).IsAdded);

        var files = builder.Render();

        Assert.Single(files, file => file.Path == "RelatedTests/Tests/UserServiceTests.cs");
        Assert.Contains(
            "UserServiceTests.Get_returns_the_user()",
            Assert.Single(files, file => file.Path == "Task.md").Text,
            StringComparison.Ordinal);
    }

    /// <summary>The endpoint's flow is the wiring behind it, which is what an agent needs.</summary>
    [Fact]
    public async Task Adds_the_flow_behind_an_endpoint()
    {
        var builder = await BuilderAsync();
        var database = await IndexAsync();
        var endpoint = Assert.Single(database.GetEndpoints());

        Assert.True(builder.Add(builder.CreateEndpointFlow(endpoint)).IsAdded);

        var flow = Assert.Single(builder.Render(), file => file.Path == "ExecutionFlow.md");

        Assert.Contains("GET /users/{id}", flow.Text, StringComparison.Ordinal);
        Assert.Contains("UsersController", flow.Text, StringComparison.Ordinal);
        Assert.Contains("IUserService", flow.Text, StringComparison.Ordinal);
    }

    /// <summary>The wiring CodeAtlas already knows, rather than anything it works out now.</summary>
    [Fact]
    public async Task Summarises_the_architecture()
    {
        var builder = await BuilderAsync();

        Assert.True(builder.Add(builder.CreateArchitecture()).IsAdded);

        var architecture = Assert.Single(builder.Render(), file => file.Path == "Architecture.md");

        Assert.Contains("Shop", architecture.Text, StringComparison.Ordinal);
        Assert.Contains("IUserService", architecture.Text, StringComparison.Ordinal);
        Assert.Contains("scoped", architecture.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Carries_an_impact_report_it_was_given()
    {
        var builder = await BuilderAsync();
        var database = await IndexAsync();
        var report = ImpactAnalyzer.Analyze(database, await IdOfAsync("Shop.IUserService"));

        Assert.NotNull(report);
        Assert.True(builder.Add(builder.CreateImpact(report)).IsAdded);

        var impact = Assert.Single(builder.Render(), file => file.Path == "Impact.md");

        Assert.Contains("Direct callers", impact.Text, StringComparison.Ordinal);
        Assert.Contains("Risk surface", impact.Text, StringComparison.Ordinal);
    }

    /// <summary>The patch is a patch: no Markdown wrapper, so it still applies.</summary>
    [Fact]
    public async Task Writes_the_diff_as_a_patch()
    {
        var builder = await BuilderAsync();
        var changes = new RepositoryChanges(
            new GitStatus("main", IsDetached: false, "abc1234", []),
            [new FileDiff { Path = "src/Shop/UserService.cs", Text = "@@ -1 +1 @@\n-old\n+new\n" }],
            []);

        Assert.True(builder.Add(builder.CreateGitDiff(changes)).IsAdded);

        var patch = Assert.Single(builder.Render(), file => file.Path == "GitDiff.patch");

        Assert.StartsWith("@@", patch.Text, StringComparison.Ordinal);
        Assert.Contains("+new", patch.Text, StringComparison.Ordinal);
    }

    // ---- the budget ---------------------------------------------------------

    /// <summary>
    /// A budget is a refusal, not a trim: the entry does not go in, the pack is unchanged,
    /// and the reader is told by how much.
    /// </summary>
    [Fact]
    public async Task Refuses_an_entry_that_would_exceed_the_budget()
    {
        var builder = await BuilderAsync();
        builder.BudgetTokens = 40;

        var result = builder.Add(builder.CreateSymbol(await IdOfAsync("Shop.UserService")));

        Assert.Equal(ContextAddOutcome.ExceedsBudget, result.Outcome);
        Assert.Contains("budget", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(builder.Items);
    }

    /// <summary>No budget means no ceiling, which is a choice the reader can make.</summary>
    [Fact]
    public async Task Adds_without_limit_when_no_budget_is_set()
    {
        var builder = await BuilderAsync();
        builder.BudgetTokens = 0;

        Assert.True(builder.Add(builder.CreateSymbol(await IdOfAsync("Shop.UserService"))).IsAdded);
        Assert.False(builder.HasBudget);
    }

    /// <summary>The size is measured on what would actually be written, not on an estimate of it.</summary>
    [Fact]
    public async Task Measures_what_it_would_export()
    {
        var builder = await BuilderAsync();
        builder.Add(builder.CreateSymbol(await IdOfAsync("Shop.UserService")));

        var rendered = builder.Render();

        Assert.Equal(
            rendered.Aggregate(ContextSize.Zero, (total, file) => total + file.Size),
            builder.Size);
        Assert.Equal(rendered.Sum(file => file.Text.Length), builder.Size.Characters);
        Assert.True(builder.Size.Lines > 0);
    }

    /// <summary>
    /// The token figure is an estimate and is stated as one: four characters to a token,
    /// with no tokenizer behind it and nothing asked of any service.
    /// </summary>
    [Fact]
    public void Estimates_tokens_at_four_characters_each()
    {
        Assert.Equal(new ContextSize(0, 0, 0), ContextSize.Of(string.Empty));
        Assert.Equal(new ContextSize(8, 2, 2), ContextSize.Of("abcd\nfgh"));
        Assert.Equal(3, ContextSize.EstimateTokens(9));
    }

    [Fact]
    public async Task Reports_an_entry_that_is_already_in_the_pack()
    {
        var builder = await BuilderAsync();
        var id = await IdOfAsync("Shop.UserService");

        builder.Add(builder.CreateSymbol(id));
        var second = builder.Add(builder.CreateSymbol(id));

        Assert.Equal(ContextAddOutcome.AlreadyPresent, second.Outcome);
        Assert.Single(builder.Items);
    }

    [Fact]
    public async Task Reports_a_relation_the_index_does_not_hold()
    {
        var builder = await BuilderAsync();

        var result = builder.Add(builder.CreateCallers(await IdOfAsync("Shop.User")));

        Assert.Equal(ContextAddOutcome.Empty, result.Outcome);
        Assert.Empty(builder.Items);
    }

    [Fact]
    public async Task Removes_an_entry_and_the_file_it_alone_carried()
    {
        var builder = await BuilderAsync();
        var item = builder.CreateSymbol(await IdOfAsync("Shop.UserService"));

        builder.Add(item);
        Assert.True(builder.Remove(item!.Key));

        Assert.Empty(builder.Items);
        Assert.DoesNotContain(builder.Render(), file => file.Path.StartsWith("RelevantFiles", StringComparison.Ordinal));
    }

    // ---- suggestions --------------------------------------------------------

    /// <summary>
    /// Every suggestion says why it is relevant, because a pack nobody can audit is not one
    /// worth handing to an agent.
    /// </summary>
    [Fact]
    public async Task Suggests_with_a_reason_on_every_entry()
    {
        var builder = await BuilderAsync();

        var suggestions = builder.Suggest(await IdOfAsync("Shop.UserService"));

        Assert.All(suggestions, item => Assert.False(string.IsNullOrWhiteSpace(item.Reason)));
        Assert.Contains(suggestions, item => item.Kind == ContextItemKind.Symbol);
        Assert.Contains(suggestions, item => item.Kind == ContextItemKind.RelatedTests);
        Assert.Contains(suggestions, item => item.Kind == ContextItemKind.Architecture);
    }

    /// <summary>What is already in the pack is not offered again.</summary>
    [Fact]
    public async Task Stops_suggesting_what_has_been_taken()
    {
        var builder = await BuilderAsync();
        var id = await IdOfAsync("Shop.UserService");

        builder.Add(builder.CreateSymbol(id));

        Assert.DoesNotContain(builder.Suggest(id), item => item.Kind == ContextItemKind.Symbol);
    }

    // ---- export -------------------------------------------------------------

    [Fact]
    public async Task Exports_the_folder_structure()
    {
        var builder = await BuilderAsync();
        builder.TaskText = "Fix the user lookup.";
        builder.Add(builder.CreateSymbol(await IdOfAsync("Shop.UserService")));
        builder.Add(builder.CreateRelatedTests(await IdOfAsync("Shop.UserService")));

        var destination = Path.Combine(_root, "export");
        var folder = ContextPackWriter.WriteDirectory(builder.Render(), destination);

        Assert.Equal(Path.Combine(destination, "ContextPack"), folder);
        Assert.True(File.Exists(Path.Combine(folder, "Task.md")));
        Assert.True(File.Exists(Path.Combine(folder, "RelevantFiles", "Shop", "UserService.cs")));
        Assert.True(File.Exists(Path.Combine(folder, "RelatedTests", "Tests", "UserServiceTests.cs")));
        Assert.Contains("Fix the user lookup.", File.ReadAllText(Path.Combine(folder, "Task.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exports_a_zip_holding_the_same_files()
    {
        var builder = await BuilderAsync();
        builder.Add(builder.CreateSymbol(await IdOfAsync("Shop.UserService")));

        var zipPath = Path.Combine(_root, "pack.zip");
        ContextPackWriter.WriteZip(builder.Render(), zipPath);

        using var archive = ZipFile.OpenRead(zipPath);

        Assert.Equal(
            builder.Render().Select(file => $"ContextPack/{file.Path}").Order(),
            archive.Entries.Select(entry => entry.FullName).Order());
    }

    /// <summary>
    /// The clipboard form keeps the paths, because an agent told which file it is reading
    /// can quote a location back.
    /// </summary>
    [Fact]
    public async Task Copies_as_one_block_that_names_every_file()
    {
        var builder = await BuilderAsync();
        builder.Add(builder.CreateSymbol(await IdOfAsync("Shop.UserService")));

        var text = ContextPackWriter.ToText(builder.Render());

        Assert.Contains("ContextPack/Task.md", text, StringComparison.Ordinal);
        Assert.Contains("ContextPack/RelevantFiles/Shop/UserService.cs", text, StringComparison.Ordinal);
        Assert.Contains("public class UserService", text, StringComparison.Ordinal);
    }

    /// <summary>The same pack renders identically every time, so an export is reproducible.</summary>
    [Fact]
    public async Task Is_deterministic()
    {
        var builder = await BuilderAsync();
        var id = await IdOfAsync("Shop.UserService");

        builder.Add(builder.CreateSymbol(id));
        builder.Add(builder.CreateRelatedTests(id));
        builder.Add(builder.CreateArchitecture());

        Assert.Equal(
            ContextPackWriter.ToText(builder.Render()),
            ContextPackWriter.ToText(builder.Render()));
    }

    // ---- harness ------------------------------------------------------------

    private async Task<ContextPackBuilder> BuilderAsync() => new(await IndexAsync(), SourceRoot);

    private async Task<long> IdOfAsync(string fullyQualifiedName)
    {
        var database = await IndexAsync();
        var bare = fullyQualifiedName.Split('(')[0];
        var name = bare[(bare.LastIndexOf('.') + 1)..];

        return Assert.Single(
            database.Search(name, limit: 200),
            symbol => symbol.FullyQualifiedName == fullyQualifiedName).Id;
    }

    private async Task<SymbolIndexDatabase> IndexAsync()
    {
        if (_database is not null)
        {
            return _database;
        }

        var sources = new (string Path, string Source)[]
        {
            ("Shop/UserService.cs", UserServiceSource),
            ("Shop/UsersController.cs", ControllerSource),
            ("Shop/Startup.cs", StartupSource),
            ("Tests/Frameworks.cs", Frameworks),
            ("Tests/UserServiceTests.cs", UserServiceTestsSource),
        };

        foreach (var (path, source) in sources)
        {
            var full = Path.Combine(SourceRoot, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, source, TestContext.Current.CancellationToken);
        }

        var projects = TestProjectFactory.CreateSolution(
            SourceRoot,
            new TestProjectSpec(
                "Shop",
                [.. sources.Take(3).Select(entry => (entry.Path, entry.Source))],
                []),
            new TestProjectSpec(
                "Shop.Tests",
                [.. sources.Skip(3).Select(entry => (entry.Path, entry.Source))],
                ["Shop"]));

        var databasePath = Path.Combine(_root, "index.db");

        using (var writer = SymbolIndexDatabase.Open(databasePath))
        using (var session = writer.BeginRebuild())
        {
            foreach (var project in projects)
            {
                var data = await SymbolCollector.CollectAsync(project, TestContext.Current.CancellationToken);
                var projectId = session.AddProject(data.Project);

                session.AddSymbols(projectId, data.Symbols);
                session.AddProjectReferences(projectId, data.ProjectReferences);
                session.AddRelations(projectId, data.Relations);
                session.AddRegistrations(data.Registrations);
                session.AddEndpoints(projectId, data.Endpoints);
            }

            session.Complete(Path.Combine(_root, "Shop.sln"));
        }

        return _database = SymbolIndexDatabase.Open(databasePath);
    }

    public void Dispose()
    {
        _database?.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Not worth failing a test over.
        }
    }
}
