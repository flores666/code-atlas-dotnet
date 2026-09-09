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

    /// <summary>
    /// <c>[Route]</c> is inherited, so a controller carrying none of its own is routed by
    /// its base. Reading only directly-applied attributes left every action with an empty
    /// prefix, which collapsed the whole controller to <c>/</c>.
    /// </summary>
    [Fact]
    public async Task Routes_a_controller_by_the_route_its_base_class_declares()
    {
        var endpoints = await CollectAsync("""
                [ApiController]
                [Route("api/v1/[controller]")]
                public abstract class ApiControllerBase : ControllerBase
                {
                }

                public class OrdersController : ApiControllerBase
                {
                    [HttpGet]
                    public IActionResult List() => Ok();

                    [HttpGet("{id}")]
                    public IActionResult Get(int id) => Ok();
                }
            """);

        // [controller] names the type that inherited the template, not the base that
        // declared it.
        Assert.Equal("OrdersController.List", Route(endpoints, "GET", "/api/v1/Orders").HandlerDisplay);
        Assert.Equal("OrdersController.Get", Route(endpoints, "GET", "/api/v1/Orders/{id}").HandlerDisplay);
    }

    /// <summary>
    /// The template is taken from the nearest declaration, so a derived controller that
    /// states its own route is not also listed under its base's.
    /// </summary>
    [Fact]
    public async Task Prefers_the_controllers_own_route_over_its_bases()
    {
        var endpoints = await CollectAsync("""
                [Route("api/v1/[controller]")]
                public abstract class ApiControllerBase : ControllerBase
                {
                }

                [Route("api/v2/[controller]")]
                public class OrdersController : ApiControllerBase
                {
                    [HttpGet]
                    public IActionResult List() => Ok();
                }
            """);

        Assert.Equal("OrdersController.List", Route(endpoints, "GET", "/api/v2/Orders").HandlerDisplay);
        Assert.DoesNotContain(endpoints, endpoint => endpoint.Route == "/api/v1/Orders");
    }

    /// <summary>
    /// Inheritance is followed through however many levels sit between the controller and
    /// the base that carries the route.
    /// </summary>
    [Fact]
    public async Task Follows_the_route_through_an_intermediate_base_class()
    {
        var endpoints = await CollectAsync("""
                [Route("api/[controller]")]
                public abstract class RootController : ControllerBase
                {
                }

                public abstract class SecuredController : RootController
                {
                }

                public class ReportsController : SecuredController
                {
                    [HttpGet("summary")]
                    public IActionResult Summary() => Ok();
                }
            """);

        Assert.Equal(
            "ReportsController.Summary",
            Route(endpoints, "GET", "/api/Reports/summary").HandlerDisplay);
    }

    /// <summary>
    /// <c>[area]</c> is filled from <c>[Area]</c>, which is inherited the way
    /// <c>[Route]</c> is. Left unexpanded it produced a literal <c>/[area]/...</c> that
    /// matches no request and no filter the reader would type.
    /// </summary>
    [Fact]
    public async Task Expands_the_area_token_from_the_area_attribute()
    {
        var endpoints = await CollectAsync("""
                [Area("cms")]
                [Route("[area]/npa/{api:guid}/[controller]")]
                public class ReportsController : Controller
                {
                    [HttpGet]
                    public IActionResult Index() => Ok();
                }
            """);

        Assert.Equal(
            "ReportsController.Index",
            Route(endpoints, "GET", "/cms/npa/{api:guid}/Reports").HandlerDisplay);
    }

    [Fact]
    public async Task Expands_the_area_route_parameter()
    {
        var endpoints = await CollectAsync("""
                [Area("lk")]
                [Route("{area}/appeals")]
                public class AppealsController : Controller
                {
                    [HttpGet]
                    public IActionResult Index() => Ok();
                }
            """);

        Assert.Equal("/lk/appeals", Assert.Single(endpoints).Route);
    }

    /// <summary>
    /// A controller with no <c>[Area]</c> keeps the token as written: an unexpanded token
    /// is visibly unresolved, whereas dropping it would produce a route that looks real.
    /// </summary>
    [Fact]
    public async Task Leaves_the_area_token_alone_without_an_area_attribute()
    {
        var endpoints = await CollectAsync("""
                [Route("[area]/orphan")]
                public class OrphanController : Controller
                {
                    [HttpGet]
                    public IActionResult Index() => Ok();
                }
            """);

        Assert.Equal("/[area]/orphan", Assert.Single(endpoints).Route);
    }

    /// <summary>
    /// A filter hook or <c>Dispose</c> is not an endpoint. It matters most now that an
    /// action needs no verb attribute to be listed: by shape alone an override of
    /// <c>OnActionExecuting</c> is indistinguishable from a plain MVC action.
    /// </summary>
    [Fact]
    public async Task Does_not_treat_a_controller_lifecycle_override_as_an_action()
    {
        var endpoints = await CollectAsync("""
                [Route("[controller]")]
                public class ThingsController : Controller
                {
                    public IActionResult Index() => Ok();

                    public override void OnActionExecuting(
                        Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context)
                    {
                    }

                    public override string ToString() => "things";
                }
            """);

        Assert.Equal("ThingsController.Index", Assert.Single(endpoints).HandlerDisplay);
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
    public async Task Routes_a_verbless_action_by_its_controllers_template_and_skips_non_actions()
    {
        var endpoints = await CollectAsync("""
                [Route("mixed")]
                public class MixedController : ControllerBase
                {
                    [HttpGet("ok")]
                    public IActionResult Mapped() => Ok();

                    // Placing a route on the controller makes its actions attribute-routed,
                    // so this one is reachable at the controller's own template and accepts
                    // any verb. It is deliberately not reachable conventionally.
                    public IActionResult Conventional() => Ok();

                    [HttpGet("hidden")]
                    [NonAction]
                    public IActionResult Hidden() => Ok();
                }
            """);

        Assert.Equal("MixedController.Mapped", Route(endpoints, "GET", "/mixed/ok").HandlerDisplay);
        Assert.Equal("MixedController.Conventional", Route(endpoints, "ANY", "/mixed").HandlerDisplay);

        Assert.Equal(2, endpoints.Count);
        Assert.DoesNotContain(endpoints, endpoint => endpoint.Route.Contains("hidden", StringComparison.Ordinal));
    }

    /// <summary>
    /// A controller with no route template anywhere is reached only through the
    /// conventional route table, which is assembled at start-up. Listing its actions at
    /// <c>/</c> would name endpoints that do not exist.
    /// </summary>
    [Fact]
    public async Task Claims_nothing_for_a_controller_with_no_route_template()
    {
        var endpoints = await CollectAsync("""
                public class ConventionalController : Controller
                {
                    public IActionResult Index() => Ok();

                    [HttpGet]
                    public IActionResult Bare() => Ok();
                }
            """);

        Assert.Empty(endpoints);
    }

    /// <summary>
    /// The classic MVC site shape: a controller carrying <c>[Route]</c> and plain action
    /// methods, with no verb attribute in sight.
    /// </summary>
    [Fact]
    public async Task Lists_a_plain_mvc_action_under_its_controller_route()
    {
        var endpoints = await CollectAsync("""
                [Route("search")]
                public class SearchController : Controller
                {
                    public IActionResult Index() => Ok();
                }
            """);

        Assert.Equal("SearchController.Index", Route(endpoints, "ANY", "/search").HandlerDisplay);
    }

    /// <summary>
    /// <c>{action}</c> is a route parameter the framework matches against the action name,
    /// so each action gets the URL that actually reaches it rather than every action on the
    /// controller sharing one indistinguishable template.
    /// </summary>
    [Fact]
    public async Task Resolves_the_action_route_parameter_per_action()
    {
        var endpoints = await CollectAsync("""
                [Route("auth/{action=Index}/{id?}")]
                public class AuthController : Controller
                {
                    public IActionResult Index() => Ok();

                    public IActionResult Login() => Ok();
                }
            """);

        Assert.Equal("AuthController.Index", Route(endpoints, "ANY", "/auth/Index/{id?}").HandlerDisplay);
        Assert.Equal("AuthController.Login", Route(endpoints, "ANY", "/auth/Login/{id?}").HandlerDisplay);
    }

    /// <summary>
    /// A constraint is left as written rather than half-resolved: substituting a name into
    /// <c>{action:regex(...)}</c> would discard the constraint the route actually carries.
    /// </summary>
    [Fact]
    public async Task Leaves_a_constrained_action_parameter_alone()
    {
        var endpoints = await CollectAsync("""
                [Route("x/{action:regex(^[a-z]+$)}")]
                public class ThingController : Controller
                {
                    public IActionResult Index() => Ok();
                }
            """);

        Assert.Equal("/x/{action:regex(^[a-z]+$)}", Assert.Single(endpoints).Route);
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
