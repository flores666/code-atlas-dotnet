using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Graph;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests;

/// <summary>
/// The MVP 3 acceptance paths, end to end through the index: an endpoint reaches an EF
/// entity and the table it maps to, and a service reaches a typed HTTP client and the
/// configuration that client is pointed at.
/// </summary>
public class InfrastructureFlowTests : IDisposable
{
    private const string Source = """
        using System.Linq;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.Extensions.Configuration;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Options;
        using System.Net.Http;

        namespace Shop
        {
            public class Order { public int Id { get; set; } }

            public class ShopContext : DbContext
            {
                public DbSet<Order> Orders => Set<Order>();

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>().ToTable("orders", "sales");
                }
            }

            public interface IOrderRepository { Order? Get(int id); }

            public class SqlOrderRepository : IOrderRepository
            {
                private readonly ShopContext _context;

                public SqlOrderRepository(ShopContext context) => _context = context;

                public Order? Get(int id) => _context.Orders.FirstOrDefault(order => order.Id == id);
            }

            public class CrmOptions { public string BaseUrl { get; set; } = ""; }

            public interface ICrmClient { string Describe(int id); }

            public class CrmWebServicesClient : ICrmClient
            {
                private readonly HttpClient _http;
                private readonly CrmOptions _options;

                public CrmWebServicesClient(HttpClient http, IOptions<CrmOptions> options)
                {
                    _http = http;
                    _options = options.Value;
                }

                public string Describe(int id) => _options.BaseUrl + id;
            }

            public interface IOrderService { Order? Get(int id); }

            public class OrderService : IOrderService
            {
                private readonly IOrderRepository _orders;
                private readonly ICrmClient _crm;

                public OrderService(IOrderRepository orders, ICrmClient crm)
                {
                    _orders = orders;
                    _crm = crm;
                }

                public Order? Get(int id) => _orders.Get(id);
            }

            [Route("orders")]
            public class OrdersController : ControllerBase
            {
                private readonly IOrderService _orders;

                public OrdersController(IOrderService orders) => _orders = orders;

                [HttpGet("{id}")]
                public IActionResult Get(int id) => Ok(_orders.Get(id));
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services, IConfiguration configuration)
                {
                    services.AddScoped<IOrderService, OrderService>();
                    services.AddScoped<IOrderRepository, SqlOrderRepository>();
                    services.AddHttpClient<ICrmClient, CrmWebServicesClient>("crm");
                    services.Configure<CrmOptions>(configuration.GetSection("Crm"));
                }
            }
        }
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("codeatlas-infra").FullName;

    private async Task<SymbolIndexDatabase> IndexAsync()
    {
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

        return SymbolIndexDatabase.Open(path);
    }

    private static long Id(SymbolIndexDatabase database, string fullyQualifiedName) =>
        database.Search(fullyQualifiedName.Split('.')[^1].Split('(')[0])
            .Single(symbol => symbol.FullyQualifiedName == fullyQualifiedName)
            .Id;

    [Fact]
    public async Task Maps_an_entity_to_its_context_and_table()
    {
        using var database = await IndexAsync();
        var entity = Assert.Single(database.GetEntities());

        Assert.Equal("Order", entity.EntityDisplay);
        Assert.Equal("ShopContext", entity.ContextDisplay);
        Assert.Equal("Orders", entity.SetName);
        Assert.Equal("sales.orders", entity.QualifiedTable);
        Assert.NotNull(entity.EntitySymbolId);
        Assert.NotNull(entity.ContextSymbolId);
    }

    [Fact]
    public async Task Shows_an_entity_its_readers_and_the_context_that_declares_it()
    {
        using var database = await IndexAsync();
        var details = database.GetDetails(Id(database, "Shop.Order"));

        Assert.NotNull(details);
        // The context's own DbSet property reads the set it hands back, and is listed as
        // the reader it is.
        Assert.Equal(["Get(int id)", "Orders"], details.Readers.Select(link => link.Display).Order().ToList());
        Assert.Equal(["ShopContext"], details.DeclaredBy.Select(link => link.Display));
        Assert.Equal("sales.orders", Assert.Single(details.EntityMappings).QualifiedTable);
    }

    [Fact]
    public async Task Reaches_the_endpoint_and_the_services_above_an_entity()
    {
        using var database = await IndexAsync();
        var details = database.GetDetails(Id(database, "Shop.Order"));

        Assert.NotNull(details);
        Assert.Equal("/orders/{id}", Assert.Single(details.RelatedEndpoints).Route);

        Assert.Equal(
            ["IOrderRepository", "IOrderService", "OrderService", "SqlOrderRepository"],
            details.RelatedServices.Select(link => link.Display).Order().ToList());
    }

    [Fact]
    public async Task Walks_an_endpoint_down_to_the_entity_in_one_graph()
    {
        using var database = await IndexAsync();
        var endpoint = Assert.Single(database.GetEndpoints());

        var graph = NeighborhoodBuilder.Build(database, endpoint.HandlerSymbolId!.Value, new GraphOptions
        {
            Depth = 4,
            Seeds = [endpoint.DeclaringTypeSymbolId!.Value],
        });

        var names = graph.Nodes.Select(node => node.Symbol.Display).ToList();

        // GET /orders/{id} -> OrdersController -> IOrderService -> OrderService
        //                  -> IOrderRepository -> SqlOrderRepository -> ShopContext -> Order
        Assert.Contains("OrdersController", names);
        Assert.Contains("IOrderService", names);
        Assert.Contains("SqlOrderRepository", names);
        Assert.Contains("ShopContext", names);
        Assert.Contains("Order", names);

        Assert.Contains(graph.Edges, edge => edge.Kind == RelationKind.DeclaresEntity);
    }

    [Fact]
    public async Task Walks_a_service_out_to_its_typed_client_and_that_client_configuration()
    {
        using var database = await IndexAsync();
        var details = database.GetDetails(Id(database, "Shop.CrmWebServicesClient"));

        Assert.NotNull(details);

        // OrderService -> ICrmClient -> CrmWebServicesClient -> HttpClient, and CrmOptions.
        Assert.Equal(["ICrmClient"], details.ResolvedBy.Select(link => link.Display));
        Assert.Equal(["HttpClient"], details.UsesExternal.Select(link => link.Display));
        Assert.Equal(["CrmOptions"], details.ReadsConfiguration.Select(link => link.Display));

        var dependency = Assert.Single(details.ExternalDependencies);
        Assert.Equal(ExternalTechnology.HttpApi, dependency.Technology);
        Assert.Equal("HTTP · crm", dependency.Resource);
    }

    [Fact]
    public async Task Names_the_section_an_options_type_is_bound_to()
    {
        using var database = await IndexAsync();
        var details = database.GetDetails(Id(database, "Shop.CrmOptions"));

        Assert.NotNull(details);
        Assert.Contains(details.Configuration, usage =>
            usage.Access == ConfigurationAccess.Options && usage.Key == "Crm");
        // The client that takes the options, and the composition root that bound them.
        Assert.Equal(
            ["CrmWebServicesClient", "Startup"],
            details.ConfigurationReaders.Select(link => link.Display).Order().ToList());
    }

    [Fact]
    public async Task Labels_the_infrastructure_a_graph_node_stands_on()
    {
        using var database = await IndexAsync();
        var order = Id(database, "Shop.Order");
        var client = Id(database, "Shop.CrmWebServicesClient");
        var options = Id(database, "Shop.CrmOptions");

        var labels = database.GetInfrastructureLabels([order, client, options]);

        Assert.Equal("sales.orders", labels[order]);
        Assert.Equal("HTTP · crm", labels[client]);
        Assert.Equal("options", labels[options]);
    }

    [Fact]
    public async Task Keeps_a_boundary_that_is_outside_the_solution_navigable_by_name_only()
    {
        using var database = await IndexAsync();
        var link = Assert.Single(database.GetDetails(Id(database, "Shop.CrmWebServicesClient"))!.UsesExternal);

        Assert.False(link.IsNavigable);
        Assert.Equal("System.Net.Http.HttpClient", link.FullyQualifiedName);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
