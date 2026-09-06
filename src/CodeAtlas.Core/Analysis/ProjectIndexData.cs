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
    IReadOnlyList<IndexDiagnostic> Diagnostics);
