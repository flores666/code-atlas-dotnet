namespace CodeAtlas.Core.Model;

/// <summary>
/// One end of a relation. <see cref="SymbolId"/> is <c>null</c> when the symbol lives
/// outside the indexed solution, in which case only its name is known.
/// </summary>
public sealed record SymbolLink(
    long? SymbolId,
    string FullyQualifiedName,
    string Display,
    RelationProvenance Provenance = RelationProvenance.Exact)
{
    public bool IsNavigable => SymbolId is not null;

    public bool IsExact => Provenance == RelationProvenance.Exact;
}

/// <summary>A symbol plus the exact relations recorded for it in both directions.</summary>
public sealed record SymbolDetails
{
    public required IndexedSymbol Symbol { get; init; }

    /// <summary>The declared base type. At most one entry for a C# type.</summary>
    public IReadOnlyList<SymbolLink> BaseTypes { get; init; } = [];

    /// <summary>Interfaces this type implements, or interface members this member implements.</summary>
    public IReadOnlyList<SymbolLink> Interfaces { get; init; } = [];

    /// <summary>The base member this one overrides.</summary>
    public IReadOnlyList<SymbolLink> Overrides { get; init; } = [];

    /// <summary>Methods and constructors this member invokes.</summary>
    public IReadOnlyList<SymbolLink> Calls { get; init; } = [];

    /// <summary>Symbols this one mentions without calling.</summary>
    public IReadOnlyList<SymbolLink> References { get; init; } = [];

    /// <summary>Types this member takes as parameters.</summary>
    public IReadOnlyList<SymbolLink> ParameterTypes { get; init; } = [];

    /// <summary>The member's return, field, property or event type.</summary>
    public IReadOnlyList<SymbolLink> ReturnTypes { get; init; } = [];

    /// <summary>
    /// For a type, the parameter types of its declared constructors — what an instance
    /// cannot be built without. Empty for members.
    /// </summary>
    public IReadOnlyList<SymbolLink> ConstructorDependencies { get; init; } = [];

    public IReadOnlyList<SymbolLink> DerivedTypes { get; init; } = [];

    /// <summary>Types or members implementing this one.</summary>
    public IReadOnlyList<SymbolLink> Implementors { get; init; } = [];

    /// <summary>Members overriding this one.</summary>
    public IReadOnlyList<SymbolLink> OverriddenBy { get; init; } = [];

    /// <summary>Members invoking this one, truncated to <see cref="CalledByTotal"/>.</summary>
    public IReadOnlyList<SymbolLink> CalledBy { get; init; } = [];

    public int CalledByTotal { get; init; }

    /// <summary>Symbols referencing this one, truncated to <see cref="ReferencedByTotal"/>.</summary>
    public IReadOnlyList<SymbolLink> ReferencedBy { get; init; } = [];

    /// <summary>Total incoming references, which may exceed <see cref="ReferencedBy"/>.</summary>
    public int ReferencedByTotal { get; init; }
}
