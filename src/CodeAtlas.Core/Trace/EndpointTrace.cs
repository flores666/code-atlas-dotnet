using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Trace;

/// <summary>Why a step is part of the trace.</summary>
public enum TraceStepKind
{
    /// <summary>Where the request enters: the handler, or what an inline handler is handed.</summary>
    Entry,

    /// <summary>The step above invokes it.</summary>
    Calls,

    /// <summary>It runs in place of the step above, which is an interface or abstract member.</summary>
    Implements,
}

/// <summary>What stops the trace at a step.</summary>
public enum TraceStepEnd
{
    /// <summary>Its continuation is the steps below it.</summary>
    Expanded,

    /// <summary>Nothing indexed runs after it.</summary>
    Terminal,

    /// <summary>It appears earlier in the trace, where its continuation is already shown.</summary>
    Repeated,

    /// <summary>It continues past the depth this trace walks.</summary>
    Deeper,
}

/// <summary>
/// One symbol on the path a request takes.
/// </summary>
/// <param name="Depth">Hops from the entry point; 0 is the entry point itself.</param>
public sealed record TraceStep(IndexedSymbol Symbol, int Depth, TraceStepKind Kind, TraceStepEnd End);

/// <summary>
/// The execution trace of one endpoint, in the order it reads: pre-order, so a step is
/// immediately followed by what it reaches.
/// </summary>
/// <param name="Truncated">True when the step budget stopped the walk short.</param>
public sealed record EndpointTrace(IReadOnlyList<TraceStep> Steps, bool Truncated);
