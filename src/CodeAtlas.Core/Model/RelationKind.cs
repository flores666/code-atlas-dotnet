namespace CodeAtlas.Core.Model;

/// <summary>Compiler-derived edges between symbols.</summary>
public enum RelationKind
{
    /// <summary>Source type derives from the target type.</summary>
    Inherits,

    /// <summary>
    /// Source directly implements the target: a type implementing an interface, or a
    /// member implementing an interface member.
    /// </summary>
    Implements,

    /// <summary>Source member overrides the target member declared in a base type.</summary>
    Overrides,

    /// <summary>Source member invokes the target method or constructor.</summary>
    Calls,

    /// <summary>Source member mentions the target symbol somewhere in its body, other than by calling it.</summary>
    References,

    /// <summary>Source member declares a parameter of the target type.</summary>
    ParameterType,

    /// <summary>Source member's return, field, property or event type is the target type.</summary>
    ReturnType,
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

/// <summary>
/// The edge groups the graph filters by. One group can cover several
/// <see cref="RelationKind"/> values that mean the same thing to a reader.
/// </summary>
public enum RelationGroupKind
{
    Calls,
    References,
    Inheritance,
    Implementations,
    TypeDependencies,
}

public static class RelationKinds
{
    public static readonly IReadOnlyList<RelationKind> All = Enum.GetValues<RelationKind>();

    /// <summary>The kinds each filter group switches on.</summary>
    public static IReadOnlyList<RelationKind> InGroup(RelationGroupKind group) => group switch
    {
        RelationGroupKind.Calls => [RelationKind.Calls],
        RelationGroupKind.References => [RelationKind.References],
        RelationGroupKind.Inheritance => [RelationKind.Inherits],
        RelationGroupKind.Implementations => [RelationKind.Implements, RelationKind.Overrides],
        RelationGroupKind.TypeDependencies => [RelationKind.ParameterType, RelationKind.ReturnType],
        _ => [],
    };

    public static RelationGroupKind GroupOf(RelationKind kind) => kind switch
    {
        RelationKind.Calls => RelationGroupKind.Calls,
        RelationKind.References => RelationGroupKind.References,
        RelationKind.Inherits => RelationGroupKind.Inheritance,
        RelationKind.Implements or RelationKind.Overrides => RelationGroupKind.Implementations,
        _ => RelationGroupKind.TypeDependencies,
    };
}
