using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Tests;

public class EndpointCollectorTests
{
    private static async Task<IReadOnlyList<HttpEndpoint>> CollectAsync(string source) =>
        (await SymbolCollector.CollectAsync(
            TestProjectFactory.Create("Api", ("Api.cs", $$"""
                using System;
                using Microsoft.AspNetCore.Authorization;
                using Microsoft.AspNetCore.Builder;
                using Microsoft.AspNetCore.Http;
                using Microsoft.AspNetCore.Mvc;
                using Microsoft.AspNetCore.Routing;

                namespace Api
                {
                    public interface IUserService { string Find(int id); }

                {{source}}
                }
                """)),
            TestContext.Current.CancellationToken)).Endpoints;

    private static HttpEndpoint Route(IReadOnlyList<HttpEndpoint> endpoints, string method, string route) =>
        endpoints.Single(endpoint => endpoint.HttpMethod == method && endpoint.Route == route);

    [Fact]
    public async Task Combines_the_controller_route_with_the_action_route()
    {
        var endpoints = await CollectAsync("""
                [Route("api/[controller]")]
                public class UsersController : ControllerBase
                {
                    [HttpGet("{id}")]
                    public IActionResult Get(int id) => Ok();

                    [HttpPost]
                    public IActionResult Create() => Ok();
                }
            """);

        // [controller] expands the way the framework expands it: the type name minus its suffix.
        Assert.Equal("UsersController.Get", Route(endpoints, "GET", "/api/Users/{id}").HandlerDisplay);
        Assert.Equal("UsersController.Create", Route(endpoints, "POST", "/api/Users").HandlerDisplay);
    }

    [Fact]
    public async Task Reads_every_verb()
    {
        var endpoints = await CollectAsync("""
                [Route("things")]
                public class ThingsController : ControllerBase
                {
                    [HttpGet] public IActionResult G() => Ok();
                    [HttpPost] public IActionResult P() => Ok();
                    [HttpPut] public IActionResult U() => Ok();
                    [HttpPatch] public IActionResult A() => Ok();
                    [HttpDelete] public IActionResult D() => Ok();
                }
            """);

        Assert.Equal(
            ["DELETE", "GET", "PATCH", "POST", "PUT"],
            endpoints.Select(endpoint => endpoint.HttpMethod).Order().ToList());
    }

    [Fact]
    public async Task An_absolute_action_route_replaces_the_controller_prefix()
    {
        var endpoints = await CollectAsync("""
                [Route("api/[controller]")]
                public class HealthController : ControllerBase
                {
                    [HttpGet("/healthz")]
                    public IActionResult Check() => Ok();
                }
            """);

        Assert.Equal("/healthz", Assert.Single(endpoints).Route);
    }

    [Fact]
    public async Task Layers_action_authorization_over_the_controller()
    {
        var endpoints = await CollectAsync("""
                [Authorize]
                [Route("admin")]
                public class AdminController : ControllerBase
                {
                    [HttpGet("stats")]
                    [Authorize(Policy = "Elevated", Roles = "ops,sre")]
                    public IActionResult Stats() => Ok();

                    [HttpGet("ping")]
                    [AllowAnonymous]
                    public IActionResult Ping() => Ok();
                }
            """);

        var stats = Route(endpoints, "GET", "/admin/stats");
        Assert.True(stats.RequiresAuthorization);
        Assert.Equal(["Elevated"], stats.Policies);
        Assert.Equal(["ops", "sre"], stats.Roles);

        // The controller requires authorization; the action opts back out, as it does at run time.
        var ping = Route(endpoints, "GET", "/admin/ping");
        Assert.True(ping.AllowsAnonymous);
        Assert.Equal("Anonymous", ping.AuthorizationSummary);
    }

    [Fact]
    public async Task Reads_a_positional_authorization_policy()
    {
        var endpoints = await CollectAsync("""
                [Route("reports")]
                public class ReportsController : ControllerBase
                {
                    [HttpGet]
                    [Authorize("Managers")]
                    public IActionResult List() => Ok();
                }
            """);

        Assert.Equal(["Managers"], Assert.Single(endpoints).Policies);
    }

    [Fact]
    public async Task Records_the_declaring_type_that_carries_the_dependencies()
    {
        var endpoints = await CollectAsync("""
                [Route("users")]
                public class UsersController : ControllerBase
                {
                    public UsersController(IUserService users) { }

                    [HttpGet]
                    public IActionResult List() => Ok();
                }
            """);

        var endpoint = Assert.Single(endpoints);
        Assert.Equal("Api.UsersController", endpoint.DeclaringTypeFullyQualifiedName);
        Assert.Equal("Api.UsersController.List()", endpoint.HandlerFullyQualifiedName);
        Assert.Equal(EndpointKind.ControllerAction, endpoint.Kind);
    }

    [Fact]
    public async Task Skips_actions_with_no_attribute_route_and_non_actions()
    {
        var endpoints = await CollectAsync("""
                [Route("mixed")]
                public class MixedController : ControllerBase
                {
                    [HttpGet("ok")]
                    public IActionResult Mapped() => Ok();

                    // Conventional routing depends on the route table built at start-up.
                    public IActionResult Conventional() => Ok();

                    [HttpGet("hidden")]
                    [NonAction]
                    public IActionResult Hidden() => Ok();
                }
            """);

        Assert.Equal("/mixed/ok", Assert.Single(endpoints).Route);
    }

    [Fact]
    public async Task Ignores_a_class_that_is_not_a_controller()
    {
        var endpoints = await CollectAsync("""
                [Route("nope")]
                public class NotAController
                {
                    [HttpGet]
                    public string Get() => "x";
                }
            """);

        Assert.Empty(endpoints);
    }

    [Fact]
    public async Task Reads_minimal_api_registrations()
    {
        var endpoints = await CollectAsync("""
                public static class Routes
                {
                    public static string Handle() => "ok";

                    public static void Map(WebApplication app)
                    {
                        app.MapGet("/ping", Handle);
                        app.MapPost("/echo", () => "echo").RequireAuthorization("Writers");
                    }
                }
            """);

        var ping = Route(endpoints, "GET", "/ping");
        Assert.Equal(EndpointKind.MinimalApi, ping.Kind);
        Assert.Equal("Api.Routes.Handle()", ping.HandlerFullyQualifiedName);

        // A named handler carries its own flow, so nothing is read off the call site.
        Assert.Empty(ping.Dependencies);

        // A lambda handler has no declaration; the endpoint is still real.
        var echo = Route(endpoints, "POST", "/echo");
        Assert.Null(echo.HandlerSymbolId);
        Assert.Equal(RelationProvenance.Inferred, echo.Provenance);
        Assert.True(echo.RequiresAuthorization);
        Assert.Equal(["Writers"], echo.Policies);
    }

    /// <summary>
    /// A lambda declares nothing, so what it is handed and what it calls is the only
    /// record of its flow, and it has to be read off the registration itself.
    /// </summary>
    [Fact]
    public async Task Reads_what_an_inline_handler_depends_on()
    {
        var endpoints = await CollectAsync("""
                public static class Routes
                {
                    public static void Map(WebApplication app)
                    {
                        app.MapGet("/users/{id}", (IUserService users, int id) => users.Find(id));
                    }
                }
            """);

        // Parameters before calls: the service is where the flow starts.
        Assert.Equal(
            ["Api.IUserService", "System.Int32", "Api.IUserService.Find(System.Int32)"],
            Route(endpoints, "GET", "/users/{id}").Dependencies.Select(link => link.FullyQualifiedName));
    }

    [Fact]
    public async Task Reads_nothing_from_a_handler_that_touches_nothing()
    {
        var endpoints = await CollectAsync("""
                public static class Routes
                {
                    public static void Map(WebApplication app) => app.MapGet("/ping", () => "pong");
                }
            """);

        Assert.Empty(Route(endpoints, "GET", "/ping").Dependencies);
    }

    [Fact]
    public async Task Prefixes_minimal_api_routes_with_their_groups()
    {
        var endpoints = await CollectAsync("""
                public static class Routes
                {
                    public static void Map(WebApplication app)
                    {
                        var api = app.MapGroup("/api").MapGroup("/v1");
                        api.MapGet("/users", () => "users");
                    }
                }
            """);

        Assert.Equal("/api/v1/users", Assert.Single(endpoints).Route);
    }

    [Fact]
    public async Task Skips_a_minimal_api_route_that_is_not_a_literal()
    {
        var endpoints = await CollectAsync("""
                public static class Routes
                {
                    public static void Map(WebApplication app, string prefix)
                    {
                        app.MapGet(prefix + "/dynamic", () => "x");
                    }
                }
            """);

        Assert.Empty(endpoints);
    }
}
