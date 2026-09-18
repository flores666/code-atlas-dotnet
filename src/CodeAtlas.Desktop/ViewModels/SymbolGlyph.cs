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

    /// <summary>File name and line, e.g. <c>SymbolCollector.cs:184</c>; empty without a location.</summary>
    public static string Origin(string? filePath, int? line) => filePath is { Length: > 0 }
        ? $"{Path.GetFileName(filePath)}:{line?.ToString() ?? "?"}"
        : string.Empty;
}
