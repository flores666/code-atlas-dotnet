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

    /// <summary>
    /// The depth an endpoint's flow needs to land on an implementation.
    /// </summary>
    /// <remarks>
    /// A composition hop costs two: <see cref="RelationKind.Injects"/> reaches the
    /// interface a type depends on, and <see cref="RelationKind.Resolves"/> crosses from
    /// there to what the container was told satisfies it. An odd depth therefore stops on
    /// an interface — the hop before the answer — so the flow behind a controller with a
    /// service and a repository needs four to reach the repository implementation rather
    /// than only its interface.
    /// </remarks>
    public const int EndpointFlowDepth = 4;

    /// <summary>
    /// A resource sits at the far end of the same flow, so reaching back up to the
    /// endpoint takes the hops the other direction spent getting down to it, plus the one
    /// that crossed into the resource itself.
    /// </summary>
    public const int ResourceFlowDepth = EndpointFlowDepth + 1;

    /// <summary>
    /// The edges a flow is made of, whichever end it is read from: what calls what, how it
    /// is wired, and where it lands. References and signature types answer a different
    /// question and would bury this one.
    /// </summary>
    public static readonly IReadOnlyList<RelationGroupKind> FlowGroups =
    [
        RelationGroupKind.Calls,
        RelationGroupKind.Composition,
        RelationGroupKind.Implementations,
        RelationGroupKind.Database,
        RelationGroupKind.Configuration,
        RelationGroupKind.ExternalServices,
    ];

    /// <summary><see cref="FlowGroups"/> flattened, for a walk built without filter state.</summary>
    public static readonly IReadOnlySet<RelationKind> FlowKinds =
        FlowGroups.SelectMany(RelationKinds.InGroup).ToHashSet();

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
