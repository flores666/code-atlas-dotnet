using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Graph;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests;

/// <summary>
/// The MVP 2 acceptance path, end to end through the index: an HTTP route resolves to a
/// handler, the handler to the services it depends on, and each service to what the
/// container was told satisfies it. Both endpoint shapes share one application, because
/// both have to reach the same flow from what the compiler recorded about them.
/// </summary>
public class CompositionFlowTests : IDisposable
{
    private const string Source = """
        using Microsoft.AspNetCore.Authorization;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.AspNetCore.Routing;
        using Microsoft.Extensions.DependencyInjection;

        namespace Shop
        {
            public interface IUserRepository { User Load(int id); }

            public interface IUserService { User Get(int id); }

            public class User { public int Id { get; set; } }

            public class SqlUserRepository : IUserRepository
            {
                public User Load(int id) => new User { Id = id };
            }

            public class UserService : IUserService
            {
                private readonly IUserRepository _repository;

                public UserService(IUserRepository repository) => _repository = repository;

                public User Get(int id) => _repository.Load(id);
            }

            [Authorize]
            [Route("users")]
            public class UsersController : ControllerBase
            {
                private readonly IUserService _users;

                public UsersController(IUserService users) => _users = users;

                [HttpGet("{id}")]
                public IActionResult Get(int id) => Ok(_users.Get(id));
            }

            public static class InlineEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("/inline/users/{id}", (IUserService users, int id) => users.Get(id));
                }
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services)
                {
                    services.AddScoped<IUserService, UserService>();
                    services.AddScoped<IUserRepository, SqlUserRepository>();
                }
            }
        }
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("codeatlas-flow").FullName;

    private string IndexPath => Path.Combine(_root, "index.db");

    private async Task<SymbolIndexDatabase> IndexAsync()
    {
        var data = await SymbolCollector.CollectAsync(
            TestProjectFactory.Create("Shop", ("Shop.cs", Source)),
            TestContext.Current.CancellationToken);

        using (var writer = SymbolIndexDatabase.Open(IndexPath))
        using (var session = writer.BeginRebuild())
        {
            var projectId = session.AddProject(data.Project);
            session.AddSymbols(projectId, data.Symbols);
            session.AddRelations(projectId, data.Relations);
            session.AddRegistrations(data.Registrations);
            session.AddEndpoints(projectId, data.Endpoints);
            session.Complete(SourcePath);
        }

        return SymbolIndexDatabase.Open(IndexPath);
    }

    private const string SourcePath = "/repo/Shop.sln";

    /// <summary>
    /// The walk the endpoints section runs when a row is picked, over the flow edges only:
    /// rooted at the action and seeded with the type carrying its dependencies, or — for an
    /// inline handler, which is declared nowhere — rooted at the first thing it reaches and
    /// seeded with the rest.
    /// </summary>
    private static SymbolGraph FlowOf(SymbolIndexDatabase database, HttpEndpoint endpoint)
    {
        List<long> reached = endpoint.HandlerSymbolId is not null
            ? [.. endpoint.FlowSeeds]
            : [.. database.GetEndpointDependencies(endpoint.Id).Select(link => link.SymbolId).OfType<long>()];

        return NeighborhoodBuilder.Build(database, reached[0], new GraphOptions
        {
            Depth = GraphOptions.EndpointFlowDepth,
            Kinds = GraphOptions.FlowKinds,
            Seeds = reached[1..],
        });
    }

    private static HttpEndpoint Controller(SymbolIndexDatabase database) =>
        database.GetEndpoints().Single(endpoint => endpoint.Kind == EndpointKind.ControllerAction);

    private static HttpEndpoint Inline(SymbolIndexDatabase database) =>
        database.GetEndpoints().Single(endpoint => endpoint.Kind == EndpointKind.MinimalApi);

    private static long Id(SymbolIndexDatabase database, string fullyQualifiedName) =>
        database.Search(fullyQualifiedName.Split('.')[^1].Split('(')[0])
            .Single(symbol => symbol.FullyQualifiedName == fullyQualifiedName)
            .Id;

    [Fact]
    public async Task Lists_the_endpoint_with_its_route_handler_and_authorization()
    {
        using var database = await IndexAsync();
        var endpoint = Controller(database);

        Assert.Equal("GET", endpoint.HttpMethod);
        Assert.Equal("/users/{id}", endpoint.Route);
        Assert.Equal("UsersController.Get", endpoint.HandlerDisplay);
        Assert.True(endpoint.RequiresAuthorization);
        Assert.NotNull(endpoint.Line);

        // The attribute-routed action resolves to the indexed method itself, which is what
        // makes the endpoint a usable graph root rather than only a row of text.
        var handler = database.GetSymbol(endpoint.HandlerSymbolId ?? 0);
        Assert.NotNull(handler);
        Assert.Equal("Shop.UsersController.Get(System.Int32)", handler.FullyQualifiedName);
        Assert.Equal(IndexedSymbolKind.Method, handler.Kind);

        Assert.Equal(Id(database, "Shop.UsersController"), endpoint.DeclaringTypeSymbolId);
    }

    [Fact]
    public async Task Reports_the_dependencies_an_endpoint_reaches_directly()
    {
        using var database = await IndexAsync();
        var endpoint = Controller(database);

        Assert.Equal(
            ["IUserService"],
            database.GetEndpointDependencies(endpoint.Id).Select(link => link.Display));
    }

    [Fact]
    public async Task Traverses_from_an_injected_interface_to_its_registered_implementation()
    {
        using var database = await IndexAsync();
        var details = database.GetDetails(Id(database, "Shop.IUserService"));

        Assert.NotNull(details);
        Assert.Equal(["UserService"], details.Resolves.Select(link => link.Display));
        Assert.Equal(["UsersController"], details.InjectedBy.Select(link => link.Display));

        var registration = Assert.Single(database.GetRegistrationsForService(Id(database, "Shop.IUserService")));
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
        Assert.Equal("Shop.UserService", registration.ImplementationFullyQualifiedName);
    }

    [Fact]
    public async Task Walks_the_whole_flow_in_one_graph()
    {
        using var database = await IndexAsync();
        var graph = FlowOf(database, Controller(database));

        var names = graph.Nodes.Select(node => node.Symbol.Display).ToList();

        // GET /users/{id} -> UsersController.Get -> IUserService -> UserService
        //                 -> IUserRepository -> SqlUserRepository
        Assert.Contains("Get(int id)", names);
        Assert.Contains("UsersController", names);
        Assert.Contains("IUserService", names);
        Assert.Contains("UserService", names);
        Assert.Contains("IUserRepository", names);

        // The far end of the flow is the implementation the container resolves, not the
        // interface one hop before it: a composition hop costs an Injects and a Resolves,
        // so a depth that stops on an odd hop stops on a question rather than an answer.
        Assert.Contains("SqlUserRepository", names);

        Assert.Contains(graph.Edges, edge => edge.Kind == RelationKind.Injects);
        Assert.Contains(graph.Edges, edge => edge.Kind == RelationKind.Resolves);

        // The work the action itself does, which is the other half of a flow.
        Assert.Contains(graph.Edges, edge => edge.Kind == RelationKind.Calls);
    }

    /// <summary>
    /// An inline Minimal API handler is declared nowhere, so the compiler records nothing
    /// about it under a name. What it is handed and what it calls is read off the lambda
    /// and stored on the endpoint, which is the only place its flow can come from.
    /// </summary>
    [Fact]
    public async Task Records_what_an_inline_handler_is_handed_and_calls()
    {
        using var database = await IndexAsync();
        var endpoint = Inline(database);

        Assert.Equal("/inline/users/{id}", endpoint.Route);
        Assert.Null(endpoint.HandlerSymbolId);

        var reached = database.GetEndpointDependencies(endpoint.Id);

        // The service it takes comes before the method it calls, because the service is
        // where the flow starts and the first entry is what the graph is rooted at.
        Assert.Equal(
            ["IUserService", "int", "Get(int id)"],
            reached.Select(link => link.Display));

        // A route value is collected like anything else and simply never matches an
        // indexed symbol, so it costs no list of framework names to exclude.
        Assert.Equal(
            ["IUserService", "Get(int id)"],
            reached.Where(link => link.IsNavigable).Select(link => link.Display));
    }

    [Fact]
    public async Task Walks_an_inline_handler_down_the_same_flow()
    {
        using var database = await IndexAsync();
        var graph = FlowOf(database, Inline(database));

        var names = graph.Nodes.Select(node => node.Symbol.Display).ToList();

        // GET /inline/users/{id} -> IUserService -> UserService -> IUserRepository
        //                        -> SqlUserRepository
        Assert.Contains("IUserService", names);
        Assert.Contains("UserService", names);
        Assert.Contains("IUserRepository", names);
        Assert.Contains("SqlUserRepository", names);

        Assert.Contains(graph.Edges, edge => edge.Kind == RelationKind.Injects);
        Assert.Contains(graph.Edges, edge => edge.Kind == RelationKind.Resolves);

        // The method the lambda calls is walked too, so the flow reads at member level
        // exactly as a controller action's does.
        Assert.Contains("Get(int id)", names);
        Assert.Contains("Load(int id)", names);
    }

    /// <summary>
    /// The second open of a workspace reuses the cache rather than reindexing, so the
    /// handler a flow is rooted at has to survive the round trip through SQLite.
    /// </summary>
    [Fact]
    public async Task Maps_the_same_flow_from_the_cached_index()
    {
        long handlerId;
        long declaringId;

        using (var fresh = await IndexAsync())
        {
            var endpoint = Controller(fresh);
            handlerId = endpoint.HandlerSymbolId!.Value;
            declaringId = endpoint.DeclaringTypeSymbolId!.Value;
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Reopened exactly as a restart would: the file on disk, with nothing rebuilt.
        using var cached = SymbolIndexDatabase.Open(IndexPath);
        Assert.True(cached.HasUsableIndexFor(SourcePath));

        var reloaded = Controller(cached);
        Assert.Equal(handlerId, reloaded.HandlerSymbolId);
        Assert.Equal(declaringId, reloaded.DeclaringTypeSymbolId);
        Assert.NotNull(cached.GetDetails(handlerId));

        var graph = FlowOf(cached, reloaded);

        Assert.Equal(handlerId, graph.RootId);
        Assert.Contains("SqlUserRepository", graph.Nodes.Select(node => node.Symbol.Display));

        // The inline handler's own flow is stored, not recomputed, so it has to come back
        // from the cache too.
        Assert.Contains(
            "SqlUserRepository",
            FlowOf(cached, Inline(cached)).Nodes.Select(node => node.Symbol.Display));
    }

    [Fact]
    public async Task Marks_a_registration_derived_edge_with_the_registration_provenance()
    {
        using var database = await IndexAsync();
        var resolves = database.GetDetails(Id(database, "Shop.IUserService"))!.Resolves;

        Assert.Equal(RelationProvenance.Exact, Assert.Single(resolves).Provenance);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
