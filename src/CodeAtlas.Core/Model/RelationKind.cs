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

    /// <summary>
    /// Source type takes the target type as a constructor parameter. Redundant with the
    /// constructor's own parameter edges, and stored anyway because it is the edge the
    /// composition graph walks: a type reaches the services it depends on in one hop,
    /// without a constructor node in between.
    /// </summary>
    Injects,

    /// <summary>
    /// The target type is registered in the DI container as an implementation of the
    /// source service type. Derived from registrations, so it may be
    /// <see cref="RelationProvenance.Inferred"/>.
    /// </summary>
    Resolves,

    /// <summary>Source <c>DbContext</c> exposes the target entity type as a <c>DbSet</c>.</summary>
    DeclaresEntity,

    /// <summary>Source type maps the target entity: an <c>IEntityTypeConfiguration</c>, or <c>OnModelCreating</c>.</summary>
    ConfiguresEntity,

    /// <summary>Two entity types are related, by a navigation property or a configured relationship.</summary>
    RelatesToEntity,

    /// <summary>Source member queries the target entity.</summary>
    ReadsEntity,

    /// <summary>Source member adds instances of the target entity.</summary>
    CreatesEntity,

    /// <summary>Source member updates instances of the target entity.</summary>
    ModifiesEntity,

    /// <summary>Source member removes instances of the target entity.</summary>
    DeletesEntity,

    /// <summary>Source type reads the target options type out of configuration.</summary>
    ReadsConfiguration,

    /// <summary>
    /// Source reaches the target infrastructure client type, which is a boundary out of
    /// the application. The target is frequently a framework type outside the solution,
    /// so this edge — like <see cref="Inherits"/> — keeps an unindexed target rather than
    /// dropping it.
    /// </summary>
    UsesExternal,
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

    /// <summary>How the application is wired: what a type injects, and what satisfies it.</summary>
    Composition,

    /// <summary>Persistence: what declares, maps, relates and uses an EF Core entity.</summary>
    Database,

    /// <summary>What binds and reads configuration.</summary>
    Configuration,

    /// <summary>Where the application crosses out to infrastructure it does not own.</summary>
    ExternalServices,
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
        RelationGroupKind.Composition => [RelationKind.Injects, RelationKind.Resolves],
        RelationGroupKind.Database =>
        [
            RelationKind.DeclaresEntity, RelationKind.ConfiguresEntity, RelationKind.RelatesToEntity,
            RelationKind.ReadsEntity, RelationKind.CreatesEntity,
            RelationKind.ModifiesEntity, RelationKind.DeletesEntity,
        ],
        RelationGroupKind.Configuration => [RelationKind.ReadsConfiguration],
        RelationGroupKind.ExternalServices => [RelationKind.UsesExternal],
        _ => [],
    };

    public static RelationGroupKind GroupOf(RelationKind kind) => kind switch
    {
        RelationKind.Calls => RelationGroupKind.Calls,
        RelationKind.References => RelationGroupKind.References,
        RelationKind.Inherits => RelationGroupKind.Inheritance,
        RelationKind.Implements or RelationKind.Overrides => RelationGroupKind.Implementations,
        RelationKind.Injects or RelationKind.Resolves => RelationGroupKind.Composition,
        RelationKind.DeclaresEntity or RelationKind.ConfiguresEntity or RelationKind.RelatesToEntity
            or RelationKind.ReadsEntity or RelationKind.CreatesEntity
            or RelationKind.ModifiesEntity or RelationKind.DeletesEntity => RelationGroupKind.Database,
        RelationKind.ReadsConfiguration => RelationGroupKind.Configuration,
        RelationKind.UsesExternal => RelationGroupKind.ExternalServices,
        _ => RelationGroupKind.TypeDependencies,
    };
}
