using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Graph;

/// <summary>
/// Walks the persisted relations outwards from one symbol.
/// </summary>
/// <remarks>
/// <para>
/// This is where "never render the whole repository" is enforced. The walk is a
/// breadth-first expansion bounded on three axes — depth, total nodes, and neighbours
/// per node — and every one of them is needed: depth alone does not bound a graph in
/// which one symbol has ten thousand callers.
/// </para>
/// <para>
/// It reads the index and never Roslyn, so a graph query costs a handful of indexed
/// SQLite lookups rather than a recompilation.
/// </para>
/// </remarks>
public static class NeighborhoodBuilder
{
    /// <summary>
    /// How much further an explicitly expanded node may reach than the default per-node
    /// allowance. Without this, expanding a node the cap had trimmed would add nothing.
    /// </summary>
    private const int ExpansionAllowance = 6;

    public static SymbolGraph Build(SymbolIndexDatabase database, long rootId, GraphOptions options)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);

        if (database.GetSymbol(rootId) is null)
        {
            return new SymbolGraph(rootId, [], [], Truncated: false);
        }

        var kinds = options.Kinds;
        var maxNodes = Math.Max(1, options.MaxNodes);
        var depthLimit = Math.Clamp(options.Depth, 0, GraphOptions.MaxDepth);

        var depths = new Dictionary<long, int> { [rootId] = 0 };
        var truncated = false;

        var frontier = new List<long> { rootId };
        foreach (var seed in options.Seeds)
        {
            if (seed != rootId && depths.TryAdd(seed, 0))
            {
                frontier.Add(seed);
            }
        }

        for (var depth = 1; depth <= depthLimit && frontier.Count > 0 && !truncated; depth++)
        {
            frontier = Expand(database, frontier, depth, kinds, options, maxNodes, depths, ref truncated);
        }

        // Nodes the user opened by hand each contribute one more hop. Repeated until it
        // settles, so expanding a node that itself arrived from an earlier expansion works.
        var expanded = options.Expanded.Distinct().ToList();
        for (var pass = 0; pass < expanded.Count && !truncated; pass++)
        {
            var added = false;

            foreach (var id in expanded)
            {
                if (!depths.TryGetValue(id, out var depth))
                {
                    continue;
                }

                added |= Expand(
                    database,
                    [id],
                    depth + 1,
                    kinds,
                    options,
                    maxNodes,
                    depths,
                    ref truncated,
                    ExpansionAllowance).Count > 0;
            }

            if (!added)
            {
                break;
            }
        }

        var ids = depths.Keys.ToList();
        var edges = database.GetInternalEdges(ids, kinds);
        var totalNeighbours = database.CountNeighbours(ids, kinds);

        var shown = new Dictionary<long, HashSet<long>>();
        foreach (var edge in edges)
        {
            Neighbours(shown, edge.SourceId).Add(edge.TargetId);
            Neighbours(shown, edge.TargetId).Add(edge.SourceId);
        }

        var nodes = database.GetSymbols(ids)
            .Select(symbol => new GraphNode(
                symbol,
                depths[symbol.Id],
                Math.Max(0, Total(totalNeighbours, symbol.Id) - Shown(shown, symbol.Id))))
            .OrderBy(node => node.Depth)
            .ThenBy(node => node.Symbol.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SymbolGraph(rootId, nodes, edges, truncated);

        static HashSet<long> Neighbours(Dictionary<long, HashSet<long>> map, long id) =>
            map.TryGetValue(id, out var set) ? set : map[id] = [];

        static int Total(IReadOnlyDictionary<long, int> counts, long id) =>
            counts.TryGetValue(id, out var count) ? count : 0;

        static int Shown(Dictionary<long, HashSet<long>> map, long id) =>
            map.TryGetValue(id, out var set) ? set.Count : 0;
    }

    /// <summary>
    /// Adds the unseen neighbours of <paramref name="frontier"/> at <paramref name="depth"/>
    /// and returns them, stopping the moment the node budget is spent.
    /// </summary>
    private static List<long> Expand(
        SymbolIndexDatabase database,
        IReadOnlyCollection<long> frontier,
        int depth,
        IReadOnlyCollection<RelationKind> kinds,
        GraphOptions options,
        int maxNodes,
        Dictionary<long, int> depths,
        ref bool truncated,
        int allowance = 1)
    {
        var next = new List<long>();

        var edges = database
            .GetIncidentEdges(frontier, kinds, Math.Max(1, options.MaxNeighboursPerNode) * allowance)
            .OrderBy(edge => edge.Kind)
            .ThenBy(edge => edge.SourceId)
            .ThenBy(edge => edge.TargetId);

        var restriction = options.RestrictTo;

        foreach (var edge in edges)
        {
            foreach (var id in (ReadOnlySpan<long>)[edge.SourceId, edge.TargetId])
            {
                if (depths.ContainsKey(id))
                {
                    continue;
                }

                // A restricted walk still traverses the whole neighbourhood; it simply
                // does not admit what falls outside the set. Skipping rather than stopping
                // is what lets two changed symbols joined through unchanged code still
                // both appear.
                if (restriction is not null && !restriction.Contains(id))
                {
                    continue;
                }

                if (depths.Count >= maxNodes)
                {
                    truncated = true;
                    return next;
                }

                depths[id] = depth;
                next.Add(id);
            }
        }

        return next;
    }
}
