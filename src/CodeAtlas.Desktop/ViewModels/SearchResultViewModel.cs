using CodeAtlas.Core.Model;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>
/// The one-or-two character badge that stands in for a symbol kind. Kinds share the
/// badge shape and differ only by glyph, which keeps rows scannable without adding a
/// colour per kind.
/// </summary>
public static class SymbolGlyph
{
    public const string Project = "Pr";

    public const string Namespace = "{}";

    public static string For(IndexedSymbolKind kind) => kind switch
    {
        IndexedSymbolKind.Namespace => Namespace,
        IndexedSymbolKind.Class => "C",
        IndexedSymbolKind.Interface => "I",
        IndexedSymbolKind.Record => "R",
        IndexedSymbolKind.Struct => "S",
        IndexedSymbolKind.Enum => "E",
        IndexedSymbolKind.Delegate => "D",
        IndexedSymbolKind.Method => "M",
        IndexedSymbolKind.Constructor => "Ct",
        IndexedSymbolKind.Property => "P",
        IndexedSymbolKind.Field => "F",
        IndexedSymbolKind.Event => "Ev",
        _ => "?",
    };

    /// <summary>True for the kinds that contain other symbols; they carry the filled badge.</summary>
    public static bool IsContainer(IndexedSymbolKind kind) =>
        kind == IndexedSymbolKind.Namespace || IndexedSymbolKinds.IsType(kind);

    /// <summary>The C# keyword for a kind, used as its human-readable label.</summary>
    public static string Keyword(IndexedSymbolKind kind) => kind switch
    {
        IndexedSymbolKind.Namespace => "namespace",
        IndexedSymbolKind.Class => "class",
        IndexedSymbolKind.Interface => "interface",
        IndexedSymbolKind.Record => "record",
        IndexedSymbolKind.Struct => "struct",
        IndexedSymbolKind.Enum => "enum",
        IndexedSymbolKind.Delegate => "delegate",
        IndexedSymbolKind.Constructor => "constructor",
        IndexedSymbolKind.Method => "method",
        IndexedSymbolKind.Property => "property",
        IndexedSymbolKind.Field => "field",
        IndexedSymbolKind.Event => "event",
        _ => string.Empty,
    };
}

/// <summary>
/// One search hit, formatted for the result row. The row shows where a symbol lives
/// rather than its fully qualified name, which is what tells two same-named hits apart.
/// </summary>
public sealed class SearchResultViewModel(IndexedSymbol symbol)
{
    public IndexedSymbol Symbol { get; } = symbol;

    public string Glyph { get; } = SymbolGlyph.For(symbol.Kind);

    public bool IsContainer { get; } = SymbolGlyph.IsContainer(symbol.Kind);

    public string Display { get; } = symbol.Display;

    /// <summary>The declaring type, falling back to the namespace for types themselves.</summary>
    public string Container { get; } =
        symbol.ContainerFullyQualifiedName
        ?? (string.IsNullOrEmpty(symbol.Namespace) ? "<global namespace>" : symbol.Namespace);

    public string Project { get; } = symbol.ProjectName ?? "-";

    /// <summary>File name and line, e.g. <c>SymbolCollector.cs:184</c>; empty without a location.</summary>
    public string Location { get; } = symbol.FilePath is { } path
        ? $"{System.IO.Path.GetFileName(path)}:{symbol.Line?.ToString() ?? "?"}"
        : string.Empty;

    public bool HasLocation => Location.Length > 0;
}
