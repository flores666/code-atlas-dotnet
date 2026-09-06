using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Graph;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests;

/// <summary>
/// The MVP 2 acceptance path, end to end through the index: an HTTP route resolves to an
/// action, the action's controller to the services it injects, and each service to what
/// the container was told satisfies it.
/// </summary>
public class CompositionFlowTests : IDisposable
{
    private const string Source = """
        using Microsoft.AspNetCore.Authorization;
        using Microsoft.AspNetCore.Mvc;
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
            session.Complete("/repo/Shop.sln");
        }

        return SymbolIndexDatabase.Open(path);
    }

    private static long Id(SymbolIndexDatabase database, string fullyQualifiedName) =>
        database.Search(fullyQualifiedName.Split('.')[^1].Split('(')[0])
            .Single(symbol => symbol.FullyQualifiedName == fullyQualifiedName)
            .Id;

    [Fact]
    public async Task Lists_the_endpoint_with_its_route_handler_and_authorization()
    {
        using var database = await IndexAsync();
        var endpoint = Assert.Single(database.GetEndpoints());

        Assert.Equal("GET", endpoint.HttpMethod);
        Assert.Equal("/users/{id}", endpoint.Route);
        Assert.Equal("UsersController.Get", endpoint.HandlerDisplay);
        Assert.True(endpoint.RequiresAuthorization);
        Assert.NotNull(endpoint.HandlerSymbolId);
        Assert.NotNull(endpoint.DeclaringTypeSymbolId);
        Assert.NotNull(endpoint.Line);
    }

    [Fact]
    public async Task Reports_the_dependencies_an_endpoint_reaches_directly()
    {
        using var database = await IndexAsync();
        var endpoint = Assert.Single(database.GetEndpoints());

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
        var endpoint = Assert.Single(database.GetEndpoints());

        // What the UI asks for when an endpoint is selected: rooted at the action, seeded
        // with the controller that carries the dependencies.
        var graph = NeighborhoodBuilder.Build(database, endpoint.HandlerSymbolId!.Value, new GraphOptions
        {
            Depth = 3,
            Seeds = [endpoint.DeclaringTypeSymbolId!.Value],
        });

        var names = graph.Nodes.Select(node => node.Symbol.Display).ToList();

        // GET /users/{id} -> UsersController.Get -> IUserService -> UserService -> IUserRepository
        Assert.Contains("Get(int id)", names);
        Assert.Contains("UsersController", names);
        Assert.Contains("IUserService", names);
        Assert.Contains("UserService", names);
        Assert.Contains("IUserRepository", names);

        Assert.Contains(graph.Edges, edge => edge.Kind == RelationKind.Injects);
        Assert.Contains(graph.Edges, edge => edge.Kind == RelationKind.Resolves);
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
