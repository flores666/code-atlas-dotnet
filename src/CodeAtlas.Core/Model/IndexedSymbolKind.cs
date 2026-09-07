namespace CodeAtlas.Core.Model;

/// <summary>
/// The symbol kinds CodeAtlas indexes. Deliberately narrower than Roslyn's
/// <see cref="Microsoft.CodeAnalysis.SymbolKind"/>: it distinguishes the C# type
/// declarations (record/struct/enum/delegate) that Roslyn folds into NamedType.
/// </summary>
public enum IndexedSymbolKind
{
    Namespace,
    Class,
    Interface,
    Record,
    Struct,
    Enum,
    Delegate,
    Method,
    Constructor,
    Property,
    Field,
    Event,
}

/// <summary>Facts about a kind that are properties of the language, not of any display.</summary>
public static class IndexedSymbolKinds
{
    /// <summary>
    /// True for the kinds that declare members of their own.
    /// </summary>
    /// <remarks>
    /// Excludes <see cref="IndexedSymbolKind.Namespace"/> on purpose. A namespace contains
    /// types rather than declaring members, and treating it as one would make "the members
    /// of this symbol" mean an arbitrary share of the solution.
    /// </remarks>
    public static bool IsType(IndexedSymbolKind kind) => kind
        is IndexedSymbolKind.Class
        or IndexedSymbolKind.Interface
        or IndexedSymbolKind.Record
        or IndexedSymbolKind.Struct
        or IndexedSymbolKind.Enum
        or IndexedSymbolKind.Delegate;
}
