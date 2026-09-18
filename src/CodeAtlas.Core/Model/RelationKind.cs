namespace CodeAtlas.Core.Model;

/// <summary>
/// Compiler-derived edges between symbols.
/// </summary>
/// <remarks>
/// Only the edges an execution trace is made of. A call is what runs; an implementation
/// or an override is what runs when the call was made against an interface or an abstract
/// member. Nothing else is recorded, because nothing else is read.
/// </remarks>
public enum RelationKind
{
    /// <summary>Source member invokes the target method or constructor.</summary>
    Calls,

    /// <summary>Source member implements the target interface member.</summary>
    Implements,

    /// <summary>Source member overrides the target member declared in a base type.</summary>
    Overrides,
}

/// <summary>
/// How an edge was established.
/// </summary>
/// <remarks>
/// Only a binding the compiler resolved to exactly one symbol is
/// <see cref="Exact"/>. Anything recovered from an unresolved binding is
/// <see cref="Inferred"/> and must never be presented as compiler truth.
/// </remarks>
public enum RelationProvenance
{
    Exact,
    Inferred,
}
