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

/// <summary>
/// A symbol and everything the index records about it: the exact relations in both
/// directions, and the infrastructure facts — container registrations, entity mapping,
/// configuration and external dependencies — that are stored beside them.
/// </summary>
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
    /// What this type's constructors take — the services an instance of it cannot be built
    /// without. Empty for members, which declare no constructors of their own.
    /// </summary>
    public IReadOnlyList<SymbolLink> Injects { get; init; } = [];

    /// <summary>Implementations registered in the DI container for this service type.</summary>
    public IReadOnlyList<SymbolLink> Resolves { get; init; } = [];

    /// <summary>Types constructed with this one.</summary>
    public IReadOnlyList<SymbolLink> InjectedBy { get; init; } = [];

    /// <summary>Service types this one is registered as an implementation of.</summary>
    public IReadOnlyList<SymbolLink> ResolvedBy { get; init; } = [];

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

    // ---- persistence --------------------------------------------------------

    /// <summary>Entity types this <c>DbContext</c> exposes as a <c>DbSet</c>.</summary>
    public IReadOnlyList<SymbolLink> DeclaredEntities { get; init; } = [];

    /// <summary>Entity types this configuration class maps.</summary>
    public IReadOnlyList<SymbolLink> ConfiguredEntities { get; init; } = [];

    /// <summary>The contexts and configuration classes that declare or map this entity.</summary>
    public IReadOnlyList<SymbolLink> DeclaredBy { get; init; } = [];

    /// <summary>Entities related to this one, by navigation property or configured relationship.</summary>
    public IReadOnlyList<SymbolLink> RelatedEntities { get; init; } = [];

    /// <summary>Entities this member queries.</summary>
    public IReadOnlyList<SymbolLink> ReadsEntities { get; init; } = [];

    /// <summary>Entities this member adds, updates or removes.</summary>
    public IReadOnlyList<SymbolLink> WritesEntities { get; init; } = [];

    /// <summary>Members that query this entity.</summary>
    public IReadOnlyList<SymbolLink> Readers { get; init; } = [];

    /// <summary>Members that add, update or remove this entity.</summary>
    public IReadOnlyList<SymbolLink> Writers { get; init; } = [];

    // ---- configuration and infrastructure -----------------------------------

    /// <summary>Options types this one reads out of configuration.</summary>
    public IReadOnlyList<SymbolLink> ReadsConfiguration { get; init; } = [];

    /// <summary>Types that read this options type.</summary>
    public IReadOnlyList<SymbolLink> ConfigurationReaders { get; init; } = [];

    /// <summary>Infrastructure client types this one reaches.</summary>
    public IReadOnlyList<SymbolLink> UsesExternal { get; init; } = [];

    /// <summary>Types reaching this one as an infrastructure boundary.</summary>
    public IReadOnlyList<SymbolLink> ExternalConsumers { get; init; } = [];

    // ---- facts stored beside the relations ----------------------------------

    /// <summary>
    /// The container registrations this symbol takes part in, as the service or as the
    /// implementation. More than one is normal: the container keeps them all.
    /// </summary>
    public IReadOnlyList<ServiceRegistration> Registrations { get; init; } = [];

    /// <summary>How this entity is mapped, or how this context maps the entities it owns.</summary>
    public IReadOnlyList<EntityMapping> EntityMappings { get; init; } = [];

    /// <summary>Migrations touching this context.</summary>
    public IReadOnlyList<DataMigration> Migrations { get; init; } = [];

    /// <summary>Configuration this symbol reads, or that binds to it when it is an options type.</summary>
    public IReadOnlyList<ConfigurationUsage> Configuration { get; init; } = [];

    /// <summary>Infrastructure this symbol reaches.</summary>
    public IReadOnlyList<ExternalDependency> ExternalDependencies { get; init; } = [];

    /// <summary>Endpoints whose flow reaches this symbol, found by a bounded reverse walk.</summary>
    public IReadOnlyList<HttpEndpoint> RelatedEndpoints { get; init; } = [];

    /// <summary>Registered services on the path from an endpoint to this symbol.</summary>
    public IReadOnlyList<SymbolLink> RelatedServices { get; init; } = [];

    /// <summary>
    /// The tests that exercise this symbol, best evidence first. Exact entries are edges
    /// the compiler recorded; the rest are read off names and project structure.
    /// </summary>
    public IReadOnlyList<RelatedTest> RelatedTests { get; init; } = [];
}
