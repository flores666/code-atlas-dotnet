using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Analysis;

/// <summary>
/// A relation whose target is still identified by name. Targets are resolved to symbol
/// ids once every project has been written, so an edge may point forward at a project
/// that has not been indexed yet.
/// </summary>
public sealed record PendingRelation(
    string SourceFullyQualifiedName,
    RelationKind Kind,
    string TargetFullyQualifiedName,
    string TargetDisplay,
    RelationProvenance Provenance = RelationProvenance.Exact);

/// <summary>Everything one project contributes to the index.</summary>
public sealed record ProjectIndexData(
    IndexedProject Project,
    IReadOnlyList<IndexedSymbol> Symbols,
    IReadOnlyList<PendingRelation> Relations,
    IReadOnlyList<IndexDiagnostic> Diagnostics)
{
    /// <summary>
    /// The projects this one was built against, by name. What a test project can see is
    /// the difference between a fixture named after a type and one that could actually
    /// have exercised it.
    /// </summary>
    public IReadOnlyList<string> ProjectReferences { get; init; } = [];

    /// <summary>DI registrations found in this project's source.</summary>
    public IReadOnlyList<ServiceRegistration> Registrations { get; init; } = [];

    /// <summary>HTTP entry points declared by this project.</summary>
    public IReadOnlyList<HttpEndpoint> Endpoints { get; init; } = [];

    /// <summary>EF Core entities this project declares or maps.</summary>
    public IReadOnlyList<EntityMapping> Entities { get; init; } = [];

    /// <summary>EF Core migrations declared by this project.</summary>
    public IReadOnlyList<DataMigration> Migrations { get; init; } = [];

    /// <summary>Configuration keys, sections and options bindings found in this project.</summary>
    public IReadOnlyList<ConfigurationUsage> Configuration { get; init; } = [];

    /// <summary>Infrastructure boundaries this project's types sit on.</summary>
    public IReadOnlyList<ExternalDependency> ExternalDependencies { get; init; } = [];
}
