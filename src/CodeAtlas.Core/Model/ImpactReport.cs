namespace CodeAtlas.Core.Model;

/// <summary>
/// One symbol a change can reach, and how far away it is.
/// </summary>
/// <param name="Distance">
/// Hops through the dependency graph, 1 being a direct dependent. It is the honest measure
/// of how indirect an effect is, and the reason nothing here is presented as a flat list:
/// a caller two hops away is not the same claim as one twenty hops away.
/// </param>
public sealed record ImpactedSymbol(IndexedSymbol Symbol, int Distance)
{
    public bool IsDirect => Distance <= 1;
}

/// <summary>
/// The factual properties that make a change risky.
/// </summary>
/// <remarks>
/// Every one of these is a property the index already records, so each can be shown with
/// the evidence behind it. There is deliberately no learned or weighted score: a reader
/// has to be able to disagree with a risk assessment by checking the facts under it.
/// </remarks>
public enum RiskSignalKind
{
    /// <summary>HTTP endpoints can be reached from the change.</summary>
    ReachableEndpoints,

    /// <summary>The symbol is visible outside its assembly, so its callers are not all in view.</summary>
    PublicBoundary,

    /// <summary>An endpoint the change reaches is behind authorization.</summary>
    Authorization,

    /// <summary>The change reaches code that reads or writes persisted entities.</summary>
    Persistence,

    /// <summary>The change reaches a boundary out of the application.</summary>
    ExternalIntegration,

    /// <summary>Many call sites depend on it directly.</summary>
    FanIn,

    /// <summary>It is an abstraction several things are built on, or one the container resolves.</summary>
    SharedAbstraction,
}

/// <summary>
/// One risk property, present or not, with the fact that decided it.
/// </summary>
/// <param name="Evidence">
/// Why this signal reads the way it does, in terms the reader can check against the rest
/// of the report. Populated whether or not the signal is present, so its absence is also
/// accounted for.
/// </param>
public sealed record RiskSignal(RiskSignalKind Kind, bool IsPresent, string Title, string Evidence);

/// <summary>How much of the risk surface a change touches.</summary>
public enum RiskLevel
{
    Low,
    Moderate,
    High,
}

/// <summary>
/// The risk surface of a change: every signal, and the level that follows from them.
/// </summary>
/// <remarks>
/// <see cref="Level"/> is a count of present signals against stated thresholds, not a
/// weighting anyone has to take on trust. Both thresholds are public constants so the UI
/// can state the rule it is applying rather than only its outcome.
/// </remarks>
public sealed record RiskSurface(IReadOnlyList<RiskSignal> Signals)
{
    /// <summary>Signals present at or above which the surface reads as <see cref="RiskLevel.High"/>.</summary>
    public const int HighThreshold = 5;

    public const int ModerateThreshold = 3;

    public IReadOnlyList<RiskSignal> Present =>
        Signals.Where(signal => signal.IsPresent).ToList();

    public int PresentCount => Signals.Count(signal => signal.IsPresent);

    public RiskLevel Level => PresentCount switch
    {
        >= HighThreshold => RiskLevel.High,
        >= ModerateThreshold => RiskLevel.Moderate,
        _ => RiskLevel.Low,
    };

    /// <summary>The rule being applied, so the level is never a bare verdict.</summary>
    public string Rationale =>
        $"{PresentCount} of {Signals.Count} risk properties present " +
        $"({HighThreshold}+ reads as high, {ModerateThreshold}+ as moderate).";
}

/// <summary>What to walk, and the limits that keep the walk from running away.</summary>
/// <remarks>
/// The same three-axis discipline the graph uses. A shared abstraction in a large solution
/// has a dependency closure the size of the solution, so the answer has to be bounded and
/// has to say when it was.
/// </remarks>
public sealed record ImpactOptions
{
    public const int DefaultMaxDepth = 6;

    public const int DefaultMaxNodes = 1500;

    /// <summary>Hops to follow outwards from the change.</summary>
    public int MaxDepth { get; init; } = DefaultMaxDepth;

    /// <summary>
    /// Total symbols the closure may hold. Reaching it sets
    /// <see cref="ImpactReport.Truncated"/> rather than silently reporting a smaller blast
    /// radius than the real one.
    /// </summary>
    public int MaxNodes { get; init; } = DefaultMaxNodes;
}

/// <summary>
/// What a change to one symbol can affect.
/// </summary>
/// <remarks>
/// <para>
/// Two closures, kept apart because they answer different questions. The call closure is
/// who invokes this, and splits into <see cref="DirectCallers"/> and
/// <see cref="IndirectCallers"/> because "eight places call this" and "thirty-seven more
/// reach it eventually" are not the same fact. The dependency closure is everything that
/// depends on it by any means, and the endpoints, entities, integrations and configuration
/// below are properties <em>of that set</em> rather than of the root alone.
/// </para>
/// <para>
/// Nothing here is a prediction. Every entry is an edge the compiler recorded, which is
/// what makes the answer reproducible and what keeps a reader able to check it.
/// </para>
/// </remarks>
public sealed record ImpactReport
{
    public required IndexedSymbol Root { get; init; }

    /// <summary>Members that invoke the root, or a member of it, directly.</summary>
    public IReadOnlyList<ImpactedSymbol> DirectCallers { get; init; } = [];

    /// <summary>Members that reach it through at least one other call.</summary>
    public IReadOnlyList<ImpactedSymbol> IndirectCallers { get; init; } = [];

    /// <summary>Types and members implementing or overriding it.</summary>
    public IReadOnlyList<SymbolLink> Implementations { get; init; } = [];

    /// <summary>Types deriving from it. Empty unless the root is a type.</summary>
    public IReadOnlyList<SymbolLink> DerivedTypes { get; init; } = [];

    /// <summary>HTTP endpoints whose handler or declaring type is in the dependency closure.</summary>
    public IReadOnlyList<HttpEndpoint> Endpoints { get; init; } = [];

    /// <summary>
    /// Background services in the closure, found by their hosting base type. Only what is
    /// discoverable from the framework's own types: nothing is inferred from a name.
    /// </summary>
    public IReadOnlyList<ImpactedSymbol> Workers { get; init; } = [];

    public IReadOnlyList<EntityMapping> Entities { get; init; } = [];

    public IReadOnlyList<ExternalDependency> ExternalIntegrations { get; init; } = [];

    public IReadOnlyList<ConfigurationUsage> Configuration { get; init; } = [];

    /// <summary>Everything that depends on the root, by any edge, at any distance.</summary>
    public IReadOnlyList<ImpactedSymbol> Impacted { get; init; } = [];

    /// <summary>The furthest anything in the closure sits from the root.</summary>
    public int MaxDistance => Impacted.Count == 0 ? 0 : Impacted.Max(symbol => symbol.Distance);

    /// <summary>True when a cap stopped the walk, so the real blast radius is larger.</summary>
    public bool Truncated { get; init; }

    public required RiskSurface Risk { get; init; }

    /// <summary>
    /// The report in one line, which is the question "what can this change affect" answered
    /// before any of it is expanded.
    /// </summary>
    public IReadOnlyList<(string Label, int Count)> Summary =>
    [
        ("Direct callers", DirectCallers.Count),
        ("Indirect callers", IndirectCallers.Count),
        ("Potentially affected endpoints", Endpoints.Count),
        ("Database entities", Entities.Count),
        ("External integrations", ExternalIntegrations.Count),
    ];
}
