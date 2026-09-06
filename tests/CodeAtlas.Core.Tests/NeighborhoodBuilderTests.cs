using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Graph;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests;

public class NeighborhoodBuilderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codeatlas-graph").FullName;

    private string DatabasePath => Path.Combine(_root, "index.db");

    /// <summary>Writes a graph described by name, so a test reads as the shape it exercises.</summary>
    private SymbolIndexDatabase Seed(
        IEnumerable<string> names,
        params (string Source, RelationKind Kind, string Target)[] edges)
    {
        using (var writer = SymbolIndexDatabase.Open(DatabasePath))
        using (var session = writer.BeginRebuild())
        {
            var projectId = session.AddProject(new IndexedProject { Name = "App" });

            session.AddSymbols(projectId, names.Select(name => new IndexedSymbol
            {
                Kind = IndexedSymbolKind.Method,
                Name = name,
                FullyQualifiedName = $"App.{name}",
                Display = name,
                Namespace = "App",
                FilePath = "/repo/App.cs",
                Line = 1,
            }));

            session.AddRelations(projectId, edges.Select(edge =>
                new PendingRelation($"App.{edge.Source}", edge.Kind, $"App.{edge.Target}", edge.Target)));

            session.Complete("/repo/App.sln");
        }

        return SymbolIndexDatabase.Open(DatabasePath);
    }

    private static long Id(SymbolIndexDatabase database, string name) =>
        database.Search(name).Single(symbol => symbol.Name == name).Id;

    private static IReadOnlyList<string> Names(SymbolGraph graph) =>
        graph.Nodes.Select(node => node.Symbol.Name).Order().ToList();

    [Fact]
    public void Walks_only_as_far_as_the_requested_depth()
    {
        using var database = Seed(
            ["Root", "A", "B", "C"],
            ("Root", RelationKind.Calls, "A"),
            ("A", RelationKind.Calls, "B"),
            ("B", RelationKind.Calls, "C"));

        var depth1 = NeighborhoodBuilder.Build(database, Id(database, "Root"), new GraphOptions { Depth = 1 });
        var depth2 = NeighborhoodBuilder.Build(database, Id(database, "Root"), new GraphOptions { Depth = 2 });

        Assert.Equal(["A", "Root"], Names(depth1));
        Assert.Equal(["A", "B", "Root"], Names(depth2));
    }

    [Fact]
    public void Walks_incoming_edges_as_well_as_outgoing()
    {
        using var database = Seed(
            ["Root", "Callee", "Caller"],
            ("Root", RelationKind.Calls, "Callee"),
            ("Caller", RelationKind.Calls, "Root"));

        var graph = NeighborhoodBuilder.Build(database, Id(database, "Root"), new GraphOptions { Depth = 1 });

        Assert.Equal(["Callee", "Caller", "Root"], Names(graph));
    }

    [Fact]
    public void Draws_edges_between_every_pair_of_shown_nodes()
    {
        using var database = Seed(
            ["Root", "A", "B"],
            ("Root", RelationKind.Calls, "A"),
            ("Root", RelationKind.Calls, "B"),
            ("A", RelationKind.Calls, "B"));

        var graph = NeighborhoodBuilder.Build(database, Id(database, "Root"), new GraphOptions { Depth = 1 });

        // The A -> B link is not on the path from the root but both ends are shown, so
        // hiding it would misdescribe the neighbourhood.
        Assert.Equal(3, graph.Edges.Count);
    }

    [Fact]
    public void Follows_only_the_selected_edge_kinds()
    {
        using var database = Seed(
            ["Root", "Called", "Base"],
            ("Root", RelationKind.Calls, "Called"),
            ("Root", RelationKind.Inherits, "Base"));

        var graph = NeighborhoodBuilder.Build(database, Id(database, "Root"), new GraphOptions
        {
            Depth = 1,
            Kinds = new HashSet<RelationKind> { RelationKind.Inherits },
        });

        Assert.Equal(["Base", "Root"], Names(graph));
    }

    [Fact]
    public void Stops_at_the_node_budget_and_says_so()
    {
        var names = new[] { "Root" }.Concat(Enumerable.Range(0, 50).Select(i => $"N{i}")).ToList();
        using var database = Seed(
            names,
            [.. Enumerable.Range(0, 50).Select(i => ("Root", RelationKind.Calls, $"N{i}"))]);

        var graph = NeighborhoodBuilder.Build(database, Id(database, "Root"), new GraphOptions
        {
            Depth = 1,
            MaxNodes = 10,
        });

        Assert.Equal(10, graph.Nodes.Count);
        Assert.True(graph.Truncated);
    }

    [Fact]
    public void Caps_the_fan_out_of_one_hub_and_reports_what_it_hid()
    {
        var names = new[] { "Hub" }.Concat(Enumerable.Range(0, 30).Select(i => $"C{i}")).ToList();
        using var database = Seed(
            names,
            [.. Enumerable.Range(0, 30).Select(i => ($"C{i}", RelationKind.Calls, "Hub"))]);

        var graph = NeighborhoodBuilder.Build(database, Id(database, "Hub"), new GraphOptions
        {
            Depth = 1,
            MaxNeighboursPerNode = 5,
        });

        var hub = graph.Nodes.Single(node => node.Symbol.Name == "Hub");

        Assert.Equal(6, graph.Nodes.Count);
        Assert.Equal(25, hub.HiddenNeighbours);
    }

    [Fact]
    public void Expanding_a_node_reaches_one_hop_past_the_depth_limit()
    {
        using var database = Seed(
            ["Root", "A", "B"],
            ("Root", RelationKind.Calls, "A"),
            ("A", RelationKind.Calls, "B"));

        var rootId = Id(database, "Root");
        var collapsed = NeighborhoodBuilder.Build(database, rootId, new GraphOptions { Depth = 1 });
        var expanded = NeighborhoodBuilder.Build(database, rootId, new GraphOptions
        {
            Depth = 1,
            Expanded = [Id(database, "A")],
        });

        Assert.Equal(["A", "Root"], Names(collapsed));
        Assert.Equal(["A", "B", "Root"], Names(expanded));
        Assert.Equal(0, expanded.Nodes.Single(node => node.Symbol.Name == "A").HiddenNeighbours);
    }

    [Fact]
    public void Expanding_a_capped_hub_reaches_past_the_per_node_cap()
    {
        var names = new[] { "Hub" }.Concat(Enumerable.Range(0, 30).Select(i => $"C{i}")).ToList();
        using var database = Seed(
            names,
            [.. Enumerable.Range(0, 30).Select(i => ($"C{i}", RelationKind.Calls, "Hub"))]);

        var hubId = Id(database, "Hub");
        var expanded = NeighborhoodBuilder.Build(database, hubId, new GraphOptions
        {
            Depth = 1,
            MaxNeighboursPerNode = 5,
            Expanded = [hubId],
        });

        // Expanding the very node the cap trimmed has to be worth doing.
        Assert.Equal(31, expanded.Nodes.Count);
        Assert.Equal(0, expanded.Nodes.Single(node => node.Symbol.Name == "Hub").HiddenNeighbours);
    }

    [Fact]
    public void Reports_an_unknown_root_as_an_empty_graph()
    {
        using var database = Seed(["Root"]);

        var graph = NeighborhoodBuilder.Build(database, 9999, new GraphOptions());

        Assert.Empty(graph.Nodes);
        Assert.False(graph.Truncated);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
