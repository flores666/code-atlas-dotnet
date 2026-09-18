using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Trace;
using Microsoft.Data.Sqlite;

namespace CodeAtlas.Core.Tests;

/// <summary>
/// Traces are built against a hand-written index rather than a compilation, so each
/// case can state exactly which edges exist.
/// </summary>
public class EndpointTraceBuilderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codeatlas-trace").FullName;

    private string DatabasePath => Path.Combine(_root, "index.db");

    private static IndexedSymbol Method(string fullyQualifiedName, string container, string name) => new()
    {
        Kind = IndexedSymbolKind.Method,
        Name = name,
        FullyQualifiedName = fullyQualifiedName,
        Display = $"{name}()",
        Namespace = "App",
        ContainerFullyQualifiedName = container,
        FilePath = "/repo/App.cs",
        Line = 1,
    };

    private static IndexedSymbol Type(string fullyQualifiedName, string name, IndexedSymbolKind kind) => new()
    {
        Kind = kind,
        Name = name,
        FullyQualifiedName = fullyQualifiedName,
        Display = name,
        Namespace = "App",
        FilePath = "/repo/App.cs",
        Line = 1,
    };

    /// <summary>
    /// Writes an index and returns a reader over it. Endpoints name their handler and
    /// inline dependencies the way the collector does — by fully qualified name, resolved
    /// to row ids on completion.
    /// </summary>
    private SymbolIndexDatabase Write(
        IEnumerable<IndexedSymbol> symbols,
        IEnumerable<PendingRelation> relations,
        params HttpEndpoint[] endpoints)
    {
        using (var database = SymbolIndexDatabase.Open(DatabasePath))
        using (var session = database.BeginRebuild())
        {
            var projectId = session.AddProject(new IndexedProject { Name = "App" });
            session.AddSymbols(projectId, symbols);
            session.AddRelations(projectId, relations);
            session.AddEndpoints(projectId, endpoints);
            session.Complete("/repo/App.sln");
        }

        return SymbolIndexDatabase.Open(DatabasePath);
    }

    private static HttpEndpoint Endpoint(string? handler, params string[] dependencies) => new()
    {
        HttpMethod = "GET",
        Route = "api/widgets",
        HandlerDisplay = handler ?? "inline",
        HandlerFullyQualifiedName = handler,
        Kind = handler is null ? EndpointKind.MinimalApi : EndpointKind.ControllerAction,
        Dependencies = [.. dependencies.Select(d => new SymbolLink(null, d, d))],
    };

    [Fact]
    public void Follows_calls_from_a_controller_action_and_dispatch_past_an_interface()
    {
        using var database = Write(
            [
                Type("App.IService", "IService", IndexedSymbolKind.Interface),
                Method("App.Controller.Get()", "App.Controller", "Get"),
                Method("App.IService.Load()", "App.IService", "Load"),
                Method("App.Service.Load()", "App.Service", "Load"),
                Method("App.Repository.Query()", "App.Repository", "Query"),
            ],
            [
                new PendingRelation("App.Controller.Get()", RelationKind.Calls, "App.IService.Load()", "Load()"),
                new PendingRelation("App.Service.Load()", RelationKind.Implements, "App.IService.Load()", "Load()"),
                new PendingRelation("App.Service.Load()", RelationKind.Calls, "App.Repository.Query()", "Query()"),
            ],
            Endpoint("App.Controller.Get()"));

        var trace = EndpointTraceBuilder.Build(database, database.GetEndpoints().Single());

        // In reading order: the action, the interface member it calls, what runs in its
        // place, and what that in turn calls.
        Assert.Equal(
            [
                ("App.Controller.Get()", 0, TraceStepKind.Entry),
                ("App.IService.Load()", 1, TraceStepKind.Calls),
                ("App.Service.Load()", 2, TraceStepKind.Implements),
                ("App.Repository.Query()", 3, TraceStepKind.Calls),
            ],
            trace.Steps.Select(s => (s.Symbol.FullyQualifiedName, s.Depth, s.Kind)));

        Assert.False(trace.Truncated);
        Assert.Equal(TraceStepEnd.Terminal, trace.Steps[^1].End);
    }

    [Fact]
    public void Starts_an_inline_handler_from_what_the_lambda_was_read_to_reach()
    {
        using var database = Write(
            [
                Type("App.IService", "IService", IndexedSymbolKind.Interface),
                Method("App.IService.Load()", "App.IService", "Load"),
            ],
            [],
            // A Minimal API lambda declares nothing: the collector records the service it
            // is handed and the method it calls.
            Endpoint(handler: null, "App.IService", "App.IService.Load()"));

        var trace = EndpointTraceBuilder.Build(database, database.GetEndpoints().Single());

        Assert.Equal(
            ["App.IService", "App.IService.Load()"],
            trace.Steps.Select(s => s.Symbol.FullyQualifiedName));
        Assert.All(trace.Steps, step => Assert.Equal(TraceStepKind.Entry, step.Kind));
        Assert.All(trace.Steps, step => Assert.Equal(0, step.Depth));
    }

    [Fact]
    public void Lists_every_implementation_rather_than_guessing_which_one_is_registered()
    {
        using var database = Write(
            [
                Method("App.Controller.Get()", "App.Controller", "Get"),
                Method("App.IService.Load()", "App.IService", "Load"),
                Method("App.Fast.Load()", "App.Fast", "Load"),
                Method("App.Slow.Load()", "App.Slow", "Load"),
            ],
            [
                new PendingRelation("App.Controller.Get()", RelationKind.Calls, "App.IService.Load()", "Load()"),
                new PendingRelation("App.Fast.Load()", RelationKind.Implements, "App.IService.Load()", "Load()"),
                new PendingRelation("App.Slow.Load()", RelationKind.Implements, "App.IService.Load()", "Load()"),
            ],
            Endpoint("App.Controller.Get()"));

        var trace = EndpointTraceBuilder.Build(database, database.GetEndpoints().Single());

        Assert.Equal(
            ["App.Fast.Load()", "App.Slow.Load()"],
            trace.Steps.Where(s => s.Kind == TraceStepKind.Implements).Select(s => s.Symbol.FullyQualifiedName));
    }

    [Fact]
    public void Terminates_on_recursion_and_says_where_the_rest_of_the_path_is()
    {
        using var database = Write(
            [
                Method("App.A.One()", "App.A", "One"),
                Method("App.A.Two()", "App.A", "Two"),
            ],
            [
                new PendingRelation("App.A.One()", RelationKind.Calls, "App.A.Two()", "Two()"),
                new PendingRelation("App.A.Two()", RelationKind.Calls, "App.A.One()", "One()"),
            ],
            Endpoint("App.A.One()"));

        var trace = EndpointTraceBuilder.Build(database, database.GetEndpoints().Single());

        Assert.Equal(
            [
                ("App.A.One()", TraceStepEnd.Expanded),
                ("App.A.Two()", TraceStepEnd.Expanded),
                ("App.A.One()", TraceStepEnd.Repeated),
            ],
            trace.Steps.Select(s => (s.Symbol.FullyQualifiedName, s.End)));
    }

    [Fact]
    public void Reports_a_path_that_runs_past_the_depth_it_walks()
    {
        var symbols = Enumerable
            .Range(0, EndpointTraceBuilder.MaxDepth + 2)
            .Select(i => Method($"App.A.M{i}()", "App.A", $"M{i}"))
            .ToList();

        var relations = Enumerable
            .Range(0, symbols.Count - 1)
            .Select(i => new PendingRelation(
                $"App.A.M{i}()", RelationKind.Calls, $"App.A.M{i + 1}()", $"M{i + 1}()"))
            .ToList();

        using var database = Write(symbols, relations, Endpoint("App.A.M0()"));

        var trace = EndpointTraceBuilder.Build(database, database.GetEndpoints().Single());

        Assert.Equal(EndpointTraceBuilder.MaxDepth + 1, trace.Steps.Count);
        Assert.Equal(TraceStepEnd.Deeper, trace.Steps[^1].End);
    }

    [Fact]
    public void Produces_nothing_for_an_endpoint_whose_handler_is_not_indexed()
    {
        using var database = Write([], [], Endpoint("App.Missing.Get()"));

        var trace = EndpointTraceBuilder.Build(database, database.GetEndpoints().Single());

        Assert.Empty(trace.Steps);
        Assert.False(trace.Truncated);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
