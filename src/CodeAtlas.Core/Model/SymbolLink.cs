namespace CodeAtlas.Core.Model;

/// <summary>
/// One end of a relation. <see cref="SymbolId"/> is <c>null</c> when the symbol lives
/// outside the indexed solution, in which case only its name is known.
/// </summary>
public sealed record SymbolLink(long? SymbolId, string FullyQualifiedName, string Display);
