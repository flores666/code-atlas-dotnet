using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Trace;

/// <summary>
/// Walks the index forwards from an HTTP endpoint to produce what running it executes.
/// </summary>
/// <remarks>
/// <para>
/// Two hops make a trace, and both come from the compiler. A call is what the code does;
/// an implementation or an override is what a call against an interface or an abstract
/// member actually reaches. Following both is what carries a trace past the interface a
/// controller was handed, without any guess about which implementation the container
/// binds — every candidate is listed.
/// </para>
/// <para>
/// Bounded on three axes, because none of them bounds the walk alone: depth, total steps,
/// and the rule that a symbol is expanded at most once. The last is what makes a
/// recursive or mutually recursive chain terminate, and it also keeps a helper that
/// twenty methods call from being walked twenty times.
/// </para>
/// </remarks>
public static class EndpointTraceBuilder
{
    /// <summary>
    /// Hops to follow. Deep enough for controller → service → repository → client and a
    /// few internal calls at each level; past that a trace is no longer readable.
    /// </summary>
    public const int MaxDepth = 8;

    /// <summary>Steps to emit. A trace this long has stopped answering the question.</summary>
    public const int MaxSteps = 400;

    public static EndpointTrace Build(SymbolIndexDatabase database, HttpEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(endpoint);

        var steps = new List<TraceStep>();
        var expanded = new HashSet<long>();
        var truncated = false;

        foreach (var entry in Entries(database, endpoint))
        {
            Walk(entry, 0, TraceStepKind.Entry);
        }

        return new EndpointTrace(steps, truncated);

        void Walk(IndexedSymbol symbol, int depth, TraceStepKind kind)
        {
            if (steps.Count >= MaxSteps)
            {
                truncated = true;
                return;
            }

            // Its continuation is already in the trace, above this point.
            if (!expanded.Add(symbol.Id))
            {
                steps.Add(new TraceStep(symbol, depth, kind, TraceStepEnd.Repeated));
                return;
            }

            var reached = Reached(database, symbol);

            if (reached.Count == 0)
            {
                steps.Add(new TraceStep(symbol, depth, kind, TraceStepEnd.Terminal));
                return;
            }

            if (depth >= MaxDepth)
            {
                steps.Add(new TraceStep(symbol, depth, kind, TraceStepEnd.Deeper));
                return;
            }

            // Written before its continuations, so the list stays in reading order.
            steps.Add(new TraceStep(symbol, depth, kind, TraceStepEnd.Expanded));

            foreach (var (next, nextKind) in reached)
            {
                Walk(next, depth + 1, nextKind);
            }
        }
    }

    /// <summary>
    /// Where the trace starts. A controller action is its own entry point. An inline
    /// Minimal API handler declares nothing, so its entry points are what the collector
    /// read off the lambda: the services it is handed, then the methods it calls.
    /// </summary>
    private static IReadOnlyList<IndexedSymbol> Entries(SymbolIndexDatabase database, HttpEndpoint endpoint)
    {
        var ids = endpoint.HandlerSymbolId is { } handler ? [handler] : endpoint.Dependencies.Select(dependency => dependency.SymbolId).OfType<long>().Distinct().ToList();

        return [.. ids.Select(database.GetSymbol).OfType<IndexedSymbol>()];
    }

    /// <summary>
    /// What executing <paramref name="symbol"/> reaches in one hop: first what runs in its
    /// place, then what it calls. Dispatch comes first because it is the continuation of
    /// the same call, where a call is a step further along.
    /// </summary>
    private static List<(IndexedSymbol Symbol, TraceStepKind Kind)> Reached(SymbolIndexDatabase database, IndexedSymbol symbol)
    {
        var reached = database.GetImplementations(symbol.Id).Select(target => (target, TraceStepKind.Implements)).ToList();

        reached.AddRange(database.GetCallees(symbol.Id).Select(target => (target, TraceStepKind.Calls)));

        return reached;
    }
}
