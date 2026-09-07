namespace CodeAtlas.Core.Model;

/// <summary>A single declaration captured from a Roslyn compilation.</summary>
public sealed record IndexedSymbol
{
    /// <summary>Row id assigned by the index database; 0 before persistence.</summary>
    public long Id { get; init; }

    public required IndexedSymbolKind Kind { get; init; }

    /// <summary>Short name as written in source, e.g. <c>Parse</c>.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Namespace-qualified, parameter-qualified name. Unique per symbol within a
    /// solution and used to resolve relation targets.
    /// </summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>Human-readable signature for display, e.g. <c>Parse(string, int)</c>.</summary>
    public required string Display { get; init; }

    public string? ProjectName { get; init; }

    public string? Namespace { get; init; }

    /// <summary>Fully qualified name of the declaring type, or <c>null</c> for types and namespaces.</summary>
    public string? ContainerFullyQualifiedName { get; init; }

    public string? FilePath { get; init; }

    /// <summary>1-based line of the declaration, or <c>null</c> when it has no source location.</summary>
    public int? Line { get; init; }

    /// <summary>1-based column of the declaration.</summary>
    public int? Column { get; init; }

    /// <summary>
    /// 1-based last line of the whole declaration, or <c>null</c> when it has no source
    /// location.
    /// </summary>
    /// <remarks>
    /// <see cref="Line"/> alone locates a declaration; the span is what lets a changed
    /// line be attributed to the symbol it falls inside. A type's span covers its members,
    /// so an edit inside a method lands on both the method and the type, which is exactly
    /// how a reader thinks about it.
    /// </remarks>
    public int? EndLine { get; init; }

    /// <summary>True when <paramref name="line"/> falls inside this declaration.</summary>
    public bool Contains(int line) =>
        Line is { } start && line >= start && line <= (EndLine ?? start);

    public string? Accessibility { get; init; }

    public IReadOnlyList<string> Attributes { get; init; } = [];
}
