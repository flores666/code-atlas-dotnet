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
