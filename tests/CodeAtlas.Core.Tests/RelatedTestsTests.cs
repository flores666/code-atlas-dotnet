using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Testing;

namespace CodeAtlas.Core.Tests;

/// <summary>
/// The MVP 6 acceptance path: from a production symbol to the tests that exercise it,
/// across the three frameworks, with each relationship claiming only what it can back up.
/// </summary>
public class RelatedTestsTests : IDisposable
{
    /// <summary>
    /// The attributes are declared here rather than referenced, the way the other
    /// framework samples are: a source declaration shadows the imported one, and what is
    /// being tested is that the fully qualified name is what CodeAtlas matches on.
    /// </summary>
    private const string Frameworks = """
        namespace Xunit
        {
            public sealed class FactAttribute : System.Attribute { }
        }

        namespace NUnit.Framework
        {
            public sealed class TestAttribute : System.Attribute { }
        }

        namespace Microsoft.VisualStudio.TestTools.UnitTesting
        {
            public sealed class TestMethodAttribute : System.Attribute { }
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

        public class UserService
        {
            private readonly IUserRepository _repository;

            public UserService(IUserRepository repository) => _repository = repository;

            public User Get(int id) => _repository.Load(id);

            public void Archive(int id)
            {
            }
        }
        """;

    private const string ReportServiceSource = """
        namespace Shop;

        public class ReportService
        {
            public int Total() => 0;
        }
        """;

    private const string InvoiceSource = """
        namespace Shop.Billing;

        public class InvoiceCalculator
        {
            public decimal Compute(decimal net) => net;
        }
        """;

    private const string PricingSource = """
        namespace Shop.Legacy;

        public class PricingEngine
        {
            public decimal Quote(decimal net) => net;
        }
        """;

    /// <summary>Constructs the subject once and calls one of its methods: the exact tiers.</summary>
    private const string UserServiceTestsSource = """
        using Shop;
        using Xunit;

        namespace Shop.Tests;

        public class UserServiceTests
        {
            private readonly UserService _service = new UserService(null!);

            [Fact]
            public void Get_returns_the_user() => _service.Get(1);

            [Fact]
            public void Archive_is_quiet()
            {
            }
        }
        """;

    /// <summary>Named after its subject and touching nothing: naming alone, and never exact.</summary>
    private const string ReportServiceTestsSource = """
        namespace Shop.Tests;

        public class ReportServiceTests
        {
            [NUnit.Framework.Test]
            public void Totals_are_produced()
            {
            }
        }
        """;

    private const string InvoiceTestsSource = """
        using Shop.Billing;

        namespace Shop.Billing.Tests;

        public class InvoiceChecks
        {
            [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
            public void Computes()
            {
                new InvoiceCalculator().Compute(1m);
            }
        }
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("codeatlas-tests").FullName;
    private SymbolIndexDatabase? _database;

    private async Task<SymbolIndexDatabase> IndexAsync()
    {
        if (_database is not null)
        {
            return _database;
        }

        var projects = TestProjectFactory.CreateSolution(
            "/repo/src",
            new TestProjectSpec(
                "Shop",
                [
                    ("Shop/UserService.cs", UserServiceSource),
                    ("Shop/ReportService.cs", ReportServiceSource),
                    ("Shop/Billing/InvoiceCalculator.cs", InvoiceSource),
                    ("Shop/Legacy/PricingEngine.cs", PricingSource),
                ],
                []),
            new TestProjectSpec(
                "Shop.Tests",
                [
                    ("Tests/Frameworks.cs", Frameworks),
                    ("Tests/UserServiceTests.cs", UserServiceTestsSource),
                    ("Tests/ReportServiceTests.cs", ReportServiceTestsSource),
                    ("Tests/Billing/InvoiceChecks.cs", InvoiceTestsSource),
                ],
                ["Shop"]));

        using (var writer = SymbolIndexDatabase.Open(Path.Combine(_root, "index.db")))
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

            session.Complete("/repo/Shop.sln");
        }

        return _database = SymbolIndexDatabase.Open(Path.Combine(_root, "index.db"));
    }

    private async Task<IReadOnlyList<RelatedTest>> TestsForAsync(string name, string fullyQualifiedName)
    {
        var database = await IndexAsync();
        var symbol = Assert.Single(
            database.Search(name),
            found => found.FullyQualifiedName == fullyQualifiedName);

        return database.FindRelatedTests(symbol.Id);
    }

    [Fact]
    public async Task Finds_the_test_that_calls_the_method()
    {
        var tests = await TestsForAsync("Get", "Shop.UserService.Get(System.Int32)");

        var direct = Assert.Single(tests, test => test.Strategy == TestRelationStrategy.DirectReference);

        Assert.Equal("Get_returns_the_user()", direct.Test.Display);
        Assert.Equal("UserServiceTests", direct.Test.ClassDisplay);
        Assert.Equal(TestFramework.XUnit, direct.Test.Framework);
        Assert.Equal(TestConfidence.Exact, direct.Confidence);
        Assert.Equal("calls Get(int id)", direct.Reason);
    }

    [Fact]
    public async Task Relates_a_fixture_to_every_member_of_the_class_it_instantiates()
    {
        var tests = await TestsForAsync("Archive", "Shop.UserService.Archive(System.Int32)");

        // Nothing names Archive, but the fixture builds a UserService, so both of its
        // tests exercise one — and that is compiler-derived, not a guess.
        Assert.Equal(2, tests.Count);
        Assert.All(tests, test => Assert.True(test.IsExact));
        Assert.All(tests, test => Assert.Equal(TestRelationStrategy.ClassUnderTest, test.Strategy));
        Assert.Contains(tests, test => test.Test.Display == "Archive_is_quiet()");
    }

    [Fact]
    public async Task Never_presents_a_naming_match_as_exact()
    {
        var tests = await TestsForAsync("Total", "Shop.ReportService.Total()");

        var named = Assert.Single(tests);

        Assert.Equal(TestRelationStrategy.ProjectDependency, named.Strategy);
        Assert.Equal(TestConfidence.Probable, named.Confidence);
        Assert.False(named.IsExact);
        Assert.Equal(TestFramework.NUnit, named.Test.Framework);
        Assert.Equal("named after ReportService, in a project that depends on it", named.Reason);
    }

    [Fact]
    public async Task Reads_all_three_frameworks_and_the_projects_that_declare_them()
    {
        var database = await IndexAsync();

        Assert.Equal(["Shop.Tests"], database.GetTestProjects());

        var invoice = await TestsForAsync("Compute", "Shop.Billing.InvoiceCalculator.Compute(System.Decimal)");
        var mstest = Assert.Single(invoice, test => test.Strategy == TestRelationStrategy.DirectReference);

        Assert.Equal(TestFramework.MSTest, mstest.Test.Framework);
        Assert.Equal("InvoiceChecks", mstest.Test.ClassDisplay);
    }

    [Fact]
    public async Task Reports_nothing_for_a_symbol_no_test_comes_near()
    {
        Assert.Empty(await TestsForAsync("Quote", "Shop.Legacy.PricingEngine.Quote(System.Decimal)"));
    }

    [Theory]
    [InlineData("UserServiceTests", "UserService", true)]
    [InlineData("UserServiceTest", "UserService", true)]
    [InlineData("UserServiceUnitTests", "UserService", true)]
    [InlineData("UserService_Tests", "UserService", true)]
    [InlineData("TestUserService", "UserService", true)]
    [InlineData("UserServiceShould", "UserService", true)]
    [InlineData("UserServiceFixture", "UserService", true)]
    [InlineData("UserRepositoryTests", "UserService", false)]
    [InlineData("Tests", "UserService", false)]
    public void Reads_a_fixture_name_as_the_type_it_covers(string fixture, string type, bool expected) =>
        Assert.Equal(expected, TestNaming.NamesType(fixture, type));

    [Theory]
    [InlineData("Shop.Billing.Tests", "Shop.Billing", true)]
    [InlineData("Shop.Tests.Billing", "Shop.Billing", true)]
    [InlineData("Shop.Billing", "Shop.Billing", true)]
    [InlineData("Shop.Tests", "Shop.Billing", false)]
    [InlineData("Shop.Tests", null, false)]
    public void Reads_a_namespace_through_the_segments_that_only_mark_it_as_tests(
        string fixture, string? type, bool expected) =>
        Assert.Equal(expected, TestNaming.SharesNamespace(fixture, type));

    public void Dispose()
    {
        _database?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }
}
