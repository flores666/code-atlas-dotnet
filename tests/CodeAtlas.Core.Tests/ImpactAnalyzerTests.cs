using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Impact;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests;

/// <summary>
/// The MVP 5 acceptance path: selecting a shared service says what a change to it can
/// affect, in counts a reader can then open.
/// </summary>
/// <remarks>
/// One application shared by every test, wired the way a real one is: an interface behind
/// two implementations, a service depending on it, controllers and a background worker
/// above that, and a repository writing an entity and calling out to an HTTP client
/// configured from options. Every number asserted below is a consequence of that wiring
/// rather than of the analyser's own bookkeeping.
/// </remarks>
public class ImpactAnalyzerTests : IDisposable
{
    private const string Source = """
        using System.Linq;
        using System.Net.Http;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Authorization;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;
        using Microsoft.Extensions.Options;

        namespace Shop
        {
            public class Order { public int Id { get; set; } }

            public class Invoice { public int Id { get; set; } }

            public class ShopContext : DbContext
            {
                public DbSet<Order> Orders => Set<Order>();
                public DbSet<Invoice> Invoices => Set<Invoice>();
            }

            public class CrmOptions { public string Url { get; set; } = ""; }

            // ---- the shared abstraction under test -------------------------

            public interface IPricingService
            {
                decimal Price(int orderId);
            }

            public class PricingService : IPricingService
            {
                private readonly ShopContext _context;
                private readonly ICrmClient _crm;

                public PricingService(ShopContext context, ICrmClient crm)
                {
                    _context = context;
                    _crm = crm;
                }

                public decimal Price(int orderId)
                {
                    var order = _context.Orders.FirstOrDefault(o => o.Id == orderId);
                    _crm.Notify(orderId);
                    return 1m;
                }
            }

            public class CachedPricingService : IPricingService
            {
                public decimal Price(int orderId) => 2m;
            }

            // ---- the boundary out of the application -----------------------

            public interface ICrmClient { void Notify(int id); }

            public class CrmClient : ICrmClient
            {
                private readonly HttpClient _http;
                private readonly IOptions<CrmOptions> _options;

                public CrmClient(HttpClient http, IOptions<CrmOptions> options)
                {
                    _http = http;
                    _options = options;
                }

                public void Notify(int id) => _http.CancelPendingRequests();
            }

            // ---- direct consumers of IPricingService -----------------------

            public class CheckoutService
            {
                private readonly IPricingService _pricing;
                public CheckoutService(IPricingService pricing) => _pricing = pricing;
                public decimal Total(int id) => _pricing.Price(id);
            }

            public class QuoteService
            {
                private readonly IPricingService _pricing;
                public QuoteService(IPricingService pricing) => _pricing = pricing;
                public decimal Quote(int id) => _pricing.Price(id);
            }

            public class ReportService
            {
                private readonly IPricingService _pricing;
                public ReportService(IPricingService pricing) => _pricing = pricing;
                public decimal Summary(int id) => _pricing.Price(id);
            }

            // ---- indirect consumers ----------------------------------------

            public class BillingService
            {
                private readonly CheckoutService _checkout;
                private readonly ShopContext _context;

                public BillingService(CheckoutService checkout, ShopContext context)
                {
                    _checkout = checkout;
                    _context = context;
                }

                public void Bill(int id)
                {
                    _context.Invoices.Add(new Invoice());
                    _checkout.Total(id);
                }
            }

            // ---- HTTP entry points -----------------------------------------

            [Authorize]
            [Route("orders")]
            public class OrdersController : ControllerBase
            {
                private readonly CheckoutService _checkout;
                public OrdersController(CheckoutService checkout) => _checkout = checkout;

                [HttpGet("{id}")]
                public IActionResult Get(int id) => Ok(_checkout.Total(id));
            }

            [Route("quotes")]
            public class QuotesController : ControllerBase
            {
                private readonly QuoteService _quotes;
                public QuotesController(QuoteService quotes) => _quotes = quotes;

                [HttpGet("{id}")]
                [AllowAnonymous]
                public IActionResult Get(int id) => Ok(_quotes.Quote(id));
            }

            // ---- a background worker ---------------------------------------

            public class NightlyRepricer : BackgroundService
            {
                private readonly ReportService _reports;
                public NightlyRepricer(ReportService reports) => _reports = reports;

                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _reports.Summary(1);
                    return Task.CompletedTask;
                }
            }

            public static class Registration
            {
                public static void Add(IServiceCollection services)
                {
                    services.AddScoped<IPricingService, PricingService>();
                    services.AddHttpClient<ICrmClient, CrmClient>();
                    services.AddScoped<CheckoutService>();
                }
            }
        }
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("codeatlas-impact").FullName;
    private SymbolIndexDatabase? _database;

    private async Task<SymbolIndexDatabase> IndexAsync()
    {
        if (_database is not null)
        {
            return _database;
        }

        var data = await SymbolCollector.CollectAsync(
            TestProjectFactory.Create("Shop", ("Shop.cs", Source)),
            TestContext.Current.CancellationToken);

        var path = Path.Combine(_root, "index.db");

        using (var writer = SymbolIndexDatabase.Open(path))
        using (var session = writer.BeginRebuild())
        {
            var projectId = session.AddProject(data.Project);
            session.AddSymbols(projectId, data.Symbols);
            session.AddRelations(projectId, data.Relations);
            session.AddRegistrations(data.Registrations);
            session.AddEndpoints(projectId, data.Endpoints);
            session.AddEntities(projectId, data.Entities);
            session.AddMigrations(projectId, data.Migrations);
            session.AddConfiguration(projectId, data.Configuration);
            session.AddExternalDependencies(projectId, data.ExternalDependencies);
            session.Complete("/repo/Shop.sln");
        }

        return _database = SymbolIndexDatabase.Open(path);
    }

    private async Task<ImpactReport> AnalyseAsync(string fullyQualifiedName)
    {
        var database = await IndexAsync();
        var symbol = database.Search(fullyQualifiedName.Split('.')[^1], limit: 200)
            .First(candidate => candidate.FullyQualifiedName == fullyQualifiedName);

        var report = ImpactAnalyzer.Analyze(database, symbol.Id);

        Assert.NotNull(report);
        return report;
    }

    // ---- the acceptance summary ---------------------------------------------

    /// <summary>
    /// The completion criterion: a shared service produces a concise summary of what a
    /// change to it can affect.
    /// </summary>
    [Fact]
    public async Task Summarises_what_a_change_to_a_shared_service_can_affect()
    {
        var report = await AnalyseAsync("Shop.IPricingService");

        var summary = report.Summary.ToDictionary(entry => entry.Label, entry => entry.Count);

        // Three services take IPricingService and call Price directly.
        Assert.Equal(3, summary["Direct callers"]);

        // Billing, the two controllers and the worker reach it through them.
        Assert.True(summary["Indirect callers"] >= 4, $"indirect was {summary["Indirect callers"]}");

        Assert.Equal(2, summary["Potentially affected endpoints"]);
        Assert.Equal(2, summary["Database entities"]);
        Assert.Equal(1, summary["External integrations"]);
    }

    [Fact]
    public async Task Keeps_direct_and_indirect_callers_apart()
    {
        var report = await AnalyseAsync("Shop.IPricingService");

        Assert.All(report.DirectCallers, caller => Assert.Equal(1, caller.Distance));
        Assert.All(report.IndirectCallers, caller => Assert.True(caller.Distance > 1));

        var direct = report.DirectCallers.Select(caller => caller.Symbol.ContainerFullyQualifiedName);
        Assert.Contains("Shop.CheckoutService", direct);
        Assert.Contains("Shop.QuoteService", direct);

        // Reached only through CheckoutService, so it must not be reported as direct.
        Assert.DoesNotContain(
            "Shop.BillingService",
            report.DirectCallers.Select(caller => caller.Symbol.ContainerFullyQualifiedName));
        Assert.Contains(
            "Shop.BillingService",
            report.IndirectCallers.Select(caller => caller.Symbol.ContainerFullyQualifiedName));
    }

    /// <summary>Distance is the shortest path, which is what makes it comparable at all.</summary>
    [Fact]
    public async Task Records_the_shortest_distance_to_each_impacted_symbol()
    {
        var report = await AnalyseAsync("Shop.IPricingService");

        Assert.All(report.Impacted, symbol => Assert.True(symbol.Distance >= 1));
        Assert.True(report.MaxDistance >= 2);
        Assert.False(report.Truncated);

        // Grouped rather than flat: the report has to be able to say how indirect an
        // effect is, not only that it exists.
        Assert.True(report.Impacted.Select(symbol => symbol.Distance).Distinct().Count() > 1);
    }

    // ---- what it reaches ----------------------------------------------------

    [Fact]
    public async Task Names_the_endpoints_a_change_can_reach()
    {
        var report = await AnalyseAsync("Shop.IPricingService");

        Assert.Equal(
            ["GET /orders/{id}", "GET /quotes/{id}"],
            report.Endpoints.Select(e => $"{e.HttpMethod} {e.Route}").Order().ToList());
    }

    [Fact]
    public async Task Names_the_implementations_that_would_have_to_change_with_it()
    {
        var report = await AnalyseAsync("Shop.IPricingService");

        Assert.Contains(report.Implementations, link => link.Display == "PricingService");
        Assert.Contains(report.Implementations, link => link.Display == "CachedPricingService");
    }

    [Fact]
    public async Task Names_the_entities_and_integrations_in_the_closure()
    {
        var report = await AnalyseAsync("Shop.IPricingService");

        Assert.Equal(
            ["Invoice", "Order"],
            report.Entities.Select(entity => entity.EntityDisplay).Order().ToList());

        var external = Assert.Single(report.ExternalIntegrations);
        Assert.Equal(ExternalTechnology.HttpApi, external.Technology);
    }

    /// <summary>
    /// A worker is found by the hosting type it derives from, never by its name. It is
    /// worth separating because a change reaching one is not visible in any endpoint list.
    /// </summary>
    [Fact]
    public async Task Finds_a_background_worker_the_change_reaches()
    {
        var report = await AnalyseAsync("Shop.IPricingService");

        Assert.Contains(
            report.Workers,
            worker => worker.Symbol.FullyQualifiedName.StartsWith("Shop.NightlyRepricer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reports_configuration_the_closure_depends_on()
    {
        var report = await AnalyseAsync("Shop.IPricingService");

        Assert.Contains(report.Configuration, usage => usage.OptionsDisplay == "CrmOptions");
    }

    // ---- risk surface -------------------------------------------------------

    /// <summary>
    /// Every signal is a fact with its evidence, and the level is a stated count of them —
    /// not a weighting anyone has to take on trust.
    /// </summary>
    [Fact]
    public async Task Explains_the_risk_surface_signal_by_signal()
    {
        var report = await AnalyseAsync("Shop.IPricingService");

        Assert.All(report.Risk.Signals, signal => Assert.False(string.IsNullOrWhiteSpace(signal.Evidence)));
        Assert.Equal(report.Risk.PresentCount, report.Risk.Present.Count);
        Assert.Contains(report.Risk.PresentCount.ToString(), report.Risk.Rationale);

        var present = report.Risk.Present.Select(signal => signal.Kind).ToList();
        Assert.Contains(RiskSignalKind.ReachableEndpoints, present);
        Assert.Contains(RiskSignalKind.Authorization, present);
        Assert.Contains(RiskSignalKind.Persistence, present);
        Assert.Contains(RiskSignalKind.ExternalIntegration, present);
        Assert.Contains(RiskSignalKind.SharedAbstraction, present);

        Assert.Equal(RiskLevel.High, report.Risk.Level);
    }

    /// <summary>The authorization signal keys off a protected endpoint, not off any endpoint.</summary>
    [Fact]
    public async Task Reads_authorization_from_the_endpoints_it_reaches()
    {
        var report = await AnalyseAsync("Shop.QuoteService");

        // QuoteService reaches only the anonymous endpoint.
        Assert.Equal(["GET /quotes/{id}"], report.Endpoints.Select(e => $"{e.HttpMethod} {e.Route}").ToList());
        Assert.DoesNotContain(
            RiskSignalKind.Authorization,
            report.Risk.Present.Select(signal => signal.Kind));
    }

    /// <summary>
    /// A leaf with nothing above it is genuinely low risk, which the surface has to be able
    /// to say — otherwise the level means nothing.
    /// </summary>
    [Fact]
    public async Task Reports_a_low_surface_for_something_nothing_depends_on()
    {
        var report = await AnalyseAsync("Shop.CachedPricingService");

        Assert.Empty(report.DirectCallers);
        Assert.Empty(report.Endpoints);
        Assert.Equal(RiskLevel.Low, report.Risk.Level);
    }

    // ---- starting points ----------------------------------------------------

    /// <summary>A concrete class is a valid starting point, seeded through its members.</summary>
    [Fact]
    public async Task Analyses_a_class_through_its_members()
    {
        var report = await AnalyseAsync("Shop.CheckoutService");

        Assert.NotEmpty(report.DirectCallers);
        Assert.Contains(
            report.Endpoints,
            endpoint => endpoint.Route == "/orders/{id}");
    }

    /// <summary>An entity is a starting point too: what reads and writes it is impact.</summary>
    [Fact]
    public async Task Analyses_an_entity()
    {
        var report = await AnalyseAsync("Shop.Order");

        Assert.NotEmpty(report.Impacted);
        Assert.Contains(report.Entities, entity => entity.EntityDisplay == "Order");
    }

    [Fact]
    public async Task Returns_nothing_for_a_symbol_the_index_does_not_hold()
    {
        var database = await IndexAsync();

        Assert.Null(ImpactAnalyzer.Analyze(database, 987654));
    }

    /// <summary>
    /// A cap has to be reported, because silently describing a smaller blast radius than
    /// the real one is the one outcome that would mislead.
    /// </summary>
    [Fact]
    public async Task Says_when_a_cap_stopped_the_walk()
    {
        var database = await IndexAsync();
        var symbol = database.Search("IPricingService", limit: 50)
            .First(candidate => candidate.FullyQualifiedName == "Shop.IPricingService");

        var report = ImpactAnalyzer.Analyze(database, symbol.Id, new ImpactOptions { MaxNodes = 3 });

        Assert.NotNull(report);
        Assert.True(report.Truncated);
    }

    /// <summary>Depth is a bound on the answer, so a shallower walk reports less.</summary>
    [Fact]
    public async Task Respects_the_depth_bound()
    {
        var database = await IndexAsync();
        var symbol = database.Search("IPricingService", limit: 50)
            .First(candidate => candidate.FullyQualifiedName == "Shop.IPricingService");

        var shallow = ImpactAnalyzer.Analyze(database, symbol.Id, new ImpactOptions { MaxDepth = 1 });
        var deep = ImpactAnalyzer.Analyze(database, symbol.Id, new ImpactOptions { MaxDepth = 6 });

        Assert.NotNull(shallow);
        Assert.NotNull(deep);
        Assert.Empty(shallow.IndirectCallers);
        Assert.True(deep.Impacted.Count > shallow.Impacted.Count);
    }

    /// <summary>Deterministic: the same index and symbol give the same answer every time.</summary>
    [Fact]
    public async Task Is_deterministic()
    {
        var database = await IndexAsync();
        var symbol = database.Search("IPricingService", limit: 50)
            .First(candidate => candidate.FullyQualifiedName == "Shop.IPricingService");

        var first = ImpactAnalyzer.Analyze(database, symbol.Id);
        var second = ImpactAnalyzer.Analyze(database, symbol.Id);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(
            first.Impacted.Select(s => (s.Symbol.Id, s.Distance)),
            second.Impacted.Select(s => (s.Symbol.Id, s.Distance)));
        Assert.Equal(first.Risk.PresentCount, second.Risk.PresentCount);
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
