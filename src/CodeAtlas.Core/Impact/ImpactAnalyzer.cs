using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Impact;

/// <summary>
/// Answers "what can this change affect" from the persisted semantic graph.
/// </summary>
/// <remarks>
/// <para>
/// Deterministic and reproducible: every entry in a report is an edge the compiler
/// recorded, so the same index and the same symbol always give the same answer, and a
/// reader can check any line of it. Nothing is predicted, ranked by a model, or scored
/// opaquely.
/// </para>
/// <para>
/// Two closures are walked, because they answer different questions and blurring them
/// would overstate both. The call closure follows <see cref="RelationKind.Calls"/> alone
/// and is what "direct" and "indirect callers" mean. The dependency closure follows
/// everything that constitutes a dependency, and is the set whose endpoints, entities,
/// integrations and configuration the report describes.
/// </para>
/// <para>
/// Both are bounded on depth and total nodes, for the reason the graph is: a shared
/// abstraction in a large solution has a dependency closure the size of the solution.
/// Hitting a cap sets <see cref="ImpactReport.Truncated"/> — reporting a smaller blast
/// radius than the real one without saying so would be the one genuinely misleading
/// outcome.
/// </para>
/// </remarks>
public static class ImpactAnalyzer
{
    /// <summary>
    /// The edges that mean "depends on". Reversed, they are the blast radius.
    /// </summary>
    /// <remarks>
    /// <see cref="RelationKind.Resolves"/> is here so a change to an implementation can
    /// reach the consumers of the interface it is registered as — without it the closure
    /// stops at the concrete type, which is exactly where the interesting callers are not.
    /// Signature edges are included because a change to a type is felt by every member
    /// that mentions it in a signature, not only by what calls into it.
    /// </remarks>
    private static readonly RelationKind[] DependencyKinds =
    [
        RelationKind.Calls,
        RelationKind.References,
        RelationKind.Implements,
        RelationKind.Overrides,
        RelationKind.Inherits,
        RelationKind.Injects,
        RelationKind.Resolves,
        RelationKind.ParameterType,
        RelationKind.ReturnType,
    ];

    private static readonly RelationKind[] CallKinds = [RelationKind.Calls];

    /// <summary>
    /// The edges followed <em>forwards</em>, to find what the affected code talks to.
    /// </summary>
    /// <remarks>
    /// A boundary out of the application, a configuration read and an entity write are all
    /// recorded against the component that performs them, which sits below a change rather
    /// than above it. Walking only backwards therefore finds every caller of a service and
    /// none of the infrastructure that service touches — which is half the answer to "what
    /// can this affect". These are the flow edges: what runs what, how it is wired, and
    /// what the container puts behind an interface.
    /// </remarks>
    private static readonly RelationKind[] ResourceKinds =
    [
        RelationKind.Calls,
        RelationKind.Injects,
        RelationKind.Resolves,
    ];

    /// <summary>
    /// Direct callers at or above which fan-in is a risk property in its own right.
    /// </summary>
    /// <remarks>
    /// A threshold has to be a number, and this one is stated rather than tuned: it is
    /// the point past which a reader stops being able to hold every call site in mind.
    /// </remarks>
    public const int FanInThreshold = 5;

    /// <summary>Implementations at or above which an abstraction counts as shared.</summary>
    public const int SharedImplementationThreshold = 2;

    /// <summary>
    /// How many ids one dependents query may carry, so a wide frontier is asked for in
    /// batches rather than as one statement with thousands of parameters.
    /// </summary>
    private const int BatchSize = 400;

    /// <summary>
    /// Builds the report for one symbol, or <c>null</c> when the index does not hold it.
    /// </summary>
    public static ImpactReport? Analyze(
        SymbolIndexDatabase database,
        long rootId,
        ImpactOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        if (database.GetSymbol(rootId) is not { } root)
        {
            return null;
        }

        options ??= new ImpactOptions();

        // A type is changed through its members, so they seed the walk alongside it.
        // Without this, "who calls this service" finds nothing: calls land on methods.
        var seeds = new List<long> { rootId };
        if (IndexedSymbolKinds.IsType(root.Kind))
        {
            seeds.AddRange(database
                .GetMembers(root.FullyQualifiedName)
                .Select(member => member.Id));
        }

        var calls = Walk(database, seeds, CallKinds, options, cancellationToken);
        var dependencies = Walk(database, seeds, DependencyKinds, options, cancellationToken);

        var callers = Materialise(database, calls.Distances, seeds);
        var impacted = Materialise(database, dependencies.Distances, seeds);

        var closure = dependencies.Distances.Keys.ToList();

        // Endpoints and workers are entry points, so they can only be above a change:
        // the backwards closure is the whole of that answer.
        var endpoints = database.GetEndpointsFor(closure);
        var workerIds = database.GetWorkersAmong(closure).ToHashSet();

        // Resources are the other direction. What a change can affect includes the
        // persistence, configuration and boundaries reached by the code on the affected
        // paths, and those are recorded against components below it — the implementation
        // behind the interface, the client that interface is wired to. So the resource
        // question is asked over the backwards closure and what it runs, together.
        var resources = Walk(database, closure, ResourceKinds, options, cancellationToken, forwards: true)
            .Distances.Keys
            .ToList();

        var entities = database.GetEntitiesFor(resources);
        var external = database.GetExternalDependenciesFor(resources);
        var configuration = database.GetConfigurationFor(resources);
        var workers = impacted
            .Where(symbol => workerIds.Contains(symbol.Symbol.Id))
            .ToList();

        var implementations = database.FindImplementations(rootId);
        var derivedTypes = database.FindDerivedTypes(rootId);

        var directCallers = callers.Where(caller => caller.IsDirect).ToList();
        var indirectCallers = callers.Where(caller => !caller.IsDirect).ToList();

        return new ImpactReport
        {
            Root = root,
            DirectCallers = directCallers,
            IndirectCallers = indirectCallers,
            Implementations = implementations,
            DerivedTypes = derivedTypes,
            Endpoints = endpoints,
            Workers = workers,
            Entities = entities,
            ExternalIntegrations = external,
            Configuration = configuration,
            Impacted = impacted,
            Truncated = calls.Truncated || dependencies.Truncated,
            Risk = Assess(
                database,
                root,
                directCallers,
                implementations,
                endpoints,
                entities,
                external),
        };
    }

    /// <summary>
    /// Breadth-first outwards from the seeds, recording the shortest distance to each
    /// symbol and stopping at the caps.
    /// </summary>
    /// <remarks>
    /// Breadth-first is what makes the distance meaningful: a symbol is recorded the first
    /// time it is reached, which is by definition its shortest path.
    /// </remarks>
    /// <param name="forwards">
    /// False to follow dependents, which is the impact question; true to follow
    /// dependencies, which is what the affected code in turn relies on.
    /// </param>
    private static (Dictionary<long, int> Distances, bool Truncated) Walk(
        SymbolIndexDatabase database,
        IReadOnlyList<long> seeds,
        IReadOnlyCollection<RelationKind> kinds,
        ImpactOptions options,
        CancellationToken cancellationToken,
        bool forwards = false)
    {
        var distances = new Dictionary<long, int>();
        foreach (var seed in seeds)
        {
            distances[seed] = 0;
        }

        var frontier = seeds.Distinct().ToList();
        var truncated = false;
        var maxNodes = Math.Max(1, options.MaxNodes);

        for (var depth = 1; depth <= options.MaxDepth && frontier.Count > 0 && !truncated; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var next = new List<long>();

            foreach (var batch in Batches(frontier))
            {
                var hop = forwards
                    ? database.GetDependencies(batch, kinds)
                    : database.GetDependents(batch, kinds);

                foreach (var id in hop)
                {
                    if (distances.ContainsKey(id))
                    {
                        continue;
                    }

                    if (distances.Count >= maxNodes)
                    {
                        truncated = true;
                        break;
                    }

                    distances[id] = depth;
                    next.Add(id);
                }

                if (truncated)
                {
                    break;
                }
            }

            frontier = next;
        }

        return (distances, truncated);
    }

    private static IEnumerable<List<long>> Batches(List<long> ids)
    {
        for (var start = 0; start < ids.Count; start += BatchSize)
        {
            yield return ids.GetRange(start, Math.Min(BatchSize, ids.Count - start));
        }
    }

    /// <summary>
    /// Turns distances into symbols, dropping the seeds: the root and its own members are
    /// what changed, not what the change affects.
    /// </summary>
    private static List<ImpactedSymbol> Materialise(
        SymbolIndexDatabase database,
        Dictionary<long, int> distances,
        IReadOnlyList<long> seeds)
    {
        var seedSet = seeds.ToHashSet();
        var ids = distances.Keys.Where(id => !seedSet.Contains(id)).ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        return database
            .GetSymbols(ids)
            .Select(symbol => new ImpactedSymbol(symbol, distances[symbol.Id]))
            .OrderBy(symbol => symbol.Distance)
            .ThenBy(symbol => symbol.Symbol.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Reads the risk surface off the facts already gathered.
    /// </summary>
    /// <remarks>
    /// Each signal is a property with its evidence attached, present or not, so the UI can
    /// show the whole surface rather than a verdict. The level follows from how many are
    /// present, against thresholds stated on <see cref="RiskSurface"/> — which is a rule a
    /// reader can apply themselves and disagree with, unlike a number from a model.
    /// </remarks>
    private static RiskSurface Assess(
        SymbolIndexDatabase database,
        IndexedSymbol root,
        IReadOnlyList<ImpactedSymbol> directCallers,
        IReadOnlyList<SymbolLink> implementations,
        IReadOnlyList<HttpEndpoint> endpoints,
        IReadOnlyList<EntityMapping> entities,
        IReadOnlyList<ExternalDependency> external)
    {
        var protectedEndpoints = endpoints
            .Where(endpoint => endpoint.RequiresAuthorization && !endpoint.AllowsAnonymous)
            .ToList();

        var registrations = database
            .GetRegistrationsForService(root.Id)
            .Concat(database.GetRegistrationsForImplementation(root.Id))
            .ToList();

        var isPublic = root.Accessibility is "Public" or "Protected" or "ProtectedOrInternal";

        return new RiskSurface(
        [
            new RiskSignal(
                RiskSignalKind.ReachableEndpoints,
                endpoints.Count > 0,
                "HTTP endpoints reachable",
                endpoints.Count == 0
                    ? "No indexed endpoint's flow reaches this symbol."
                    : $"{endpoints.Count} endpoint(s), including " +
                      $"{string.Join(", ", endpoints.Take(3).Select(e => $"{e.HttpMethod} {e.Route}"))}."),

            new RiskSignal(
                RiskSignalKind.PublicBoundary,
                isPublic,
                "Public boundary",
                isPublic
                    ? $"Declared {root.Accessibility?.ToLowerInvariant()}, so callers outside this " +
                      "assembly are not necessarily in the index."
                    : $"Declared {root.Accessibility?.ToLowerInvariant() ?? "with no recorded accessibility"}, " +
                      "so every caller is inside this solution."),

            new RiskSignal(
                RiskSignalKind.Authorization,
                protectedEndpoints.Count > 0,
                "Authorization involved",
                protectedEndpoints.Count == 0
                    ? "No reachable endpoint is behind authorization."
                    : $"{protectedEndpoints.Count} reachable endpoint(s) require authorization, including " +
                      $"{string.Join(", ", protectedEndpoints.Take(2).Select(e => $"{e.HttpMethod} {e.Route}"))}."),

            new RiskSignal(
                RiskSignalKind.Persistence,
                entities.Count > 0,
                "Persistence involved",
                entities.Count == 0
                    ? "Nothing in the closure reads or writes an indexed entity."
                    : $"{entities.Count} entity/entities, including " +
                      $"{string.Join(", ", entities.Take(3).Select(entity => entity.EntityDisplay))}."),

            new RiskSignal(
                RiskSignalKind.ExternalIntegration,
                external.Count > 0,
                "External integration involved",
                external.Count == 0
                    ? "Nothing in the closure crosses out of the application."
                    : $"{external.Count} boundary/boundaries, including " +
                      $"{string.Join(", ", external.Take(3).Select(dependency => dependency.Resource))}."),

            new RiskSignal(
                RiskSignalKind.FanIn,
                directCallers.Count >= FanInThreshold,
                "High fan-in",
                $"{directCallers.Count} direct caller(s); {FanInThreshold} or more counts as high."),

            new RiskSignal(
                RiskSignalKind.SharedAbstraction,
                root.Kind == IndexedSymbolKind.Interface ||
                implementations.Count >= SharedImplementationThreshold ||
                registrations.Count > 0,
                "Shared abstraction",
                Describe(root, implementations, registrations)),
        ]);
    }

    private static string Describe(
        IndexedSymbol root,
        IReadOnlyList<SymbolLink> implementations,
        IReadOnlyList<ServiceRegistration> registrations)
    {
        var facts = new List<string>();

        if (root.Kind == IndexedSymbolKind.Interface)
        {
            facts.Add("it is an interface");
        }

        if (implementations.Count > 0)
        {
            facts.Add($"{implementations.Count} implementation(s)");
        }

        if (registrations.Count > 0)
        {
            facts.Add($"{registrations.Count} container registration(s)");
        }

        if (facts.Count == 0)
        {
            return "Not an interface, with no implementations and no container registration.";
        }

        var joined = string.Join(", ", facts);
        return char.ToUpperInvariant(joined[0]) + joined[1..] + ".";
    }
}
