namespace CodeAtlas.Core.Model;

/// <summary>An edge of the semantic graph, with both endpoints resolved to indexed symbols.</summary>
public sealed record RelationEdge(
    long SourceId,
    long TargetId,
    RelationKind Kind,
    RelationProvenance Provenance);

/// <summary>
/// A symbol shown in the graph.
/// </summary>
/// <param name="Depth">Hops from the root; 0 is the root itself.</param>
/// <param name="HiddenNeighbours">
/// Neighbours this node has that the current graph does not show, which is what makes a
/// node worth expanding.
/// </param>
public sealed record GraphNode(IndexedSymbol Symbol, int Depth, int HiddenNeighbours);

/// <summary>
/// The neighbourhood around one symbol. Never the whole solution: it is always bounded
/// by <see cref="GraphOptions.Depth"/> and <see cref="GraphOptions.MaxNodes"/>.
/// </summary>
/// <param name="Truncated">
/// True when a cap stopped the walk, so the neighbourhood shown is incomplete.
/// </param>
public sealed record SymbolGraph(
    long RootId,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<RelationEdge> Edges,
    bool Truncated);

/// <summary>
/// What to walk, and the limits that keep the walk from running away.
/// </summary>
/// <remarks>
/// Both caps matter and guard different failure modes: <see cref="MaxNeighboursPerNode"/>
/// stops a single hub symbol from filling the budget on its own, and
/// <see cref="MaxNodes"/> bounds the graph overall.
/// </remarks>
public sealed record GraphOptions
{
    public const int DefaultMaxNodes = 120;

    /// <summary>
    /// Kept low on purpose. A first look at a hub symbol is more useful as a readable
    /// sample with the rest offered behind an expand affordance than as a complete ring
    /// of eighty nodes nobody can read.
    /// </summary>
    public const int DefaultMaxNeighboursPerNode = 18;

    public const int MaxDepth = 6;

    /// <summary>Hops to walk from the root. Clamped to <see cref="MaxDepth"/>.</summary>
    public int Depth { get; init; } = 1;

    public IReadOnlySet<RelationKind> Kinds { get; init; } = RelationKinds.All.ToHashSet();

    /// <summary>
    /// Nodes the user expanded by hand. Each contributes its own immediate neighbours,
    /// so expansion is a property of the request rather than hidden view state.
    /// </summary>
    public IReadOnlyList<long> Expanded { get; init; } = [];

    /// <summary>
    /// Extra symbols to walk from as though they were the root.
    /// </summary>
    /// <remarks>
    /// An endpoint's flow needs two: the action, whose calls are the work it does, and the
    /// controller, which is what carries the injected dependencies. Neither reaches the
    /// other in one hop, so the request names both rather than the graph guessing.
    /// </remarks>
    public IReadOnlyList<long> Seeds { get; init; } = [];

    public int MaxNodes { get; init; } = DefaultMaxNodes;

    public int MaxNeighboursPerNode { get; init; } = DefaultMaxNeighboursPerNode;
}
