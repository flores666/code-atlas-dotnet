namespace CodeAtlas.Core.Model;

/// <summary>
/// What one EF Core model declaration says about an entity type.
/// </summary>
/// <remarks>
/// One row per declaration site, the way a DI registration is one row per call: an entity
/// exposed by a <c>DbSet</c> and mapped by an <c>IEntityTypeConfiguration</c> in the same
/// project is one merged row, but a second <c>DbContext</c> that also declares it is its
/// own. Only statically available mapping is captured — a table name is recorded when it
/// was written as a literal, never reconstructed from EF's conventions at run time.
/// </remarks>
public sealed record EntityMapping
{
    public long Id { get; init; }

    public required string EntityFullyQualifiedName { get; init; }

    public required string EntityDisplay { get; init; }

    public long? EntitySymbolId { get; init; }

    /// <summary>The <c>DbContext</c> that exposes it, when one does.</summary>
    public string? ContextFullyQualifiedName { get; init; }

    public string? ContextDisplay { get; init; }

    public long? ContextSymbolId { get; init; }

    /// <summary>Name of the <c>DbSet</c> property, e.g. <c>Users</c>.</summary>
    public string? SetName { get; init; }

    /// <summary>Table name where it was written out; <c>null</c> when EF's convention decides it.</summary>
    public string? TableName { get; init; }

    public string? Schema { get; init; }

    /// <summary>The <c>IEntityTypeConfiguration</c> implementation that maps it, when there is one.</summary>
    public string? ConfigurationFullyQualifiedName { get; init; }

    public string? ConfigurationDisplay { get; init; }

    public long? ConfigurationSymbolId { get; init; }

    public string? ProjectName { get; init; }

    public string? FilePath { get; init; }

    public int? Line { get; init; }

    /// <summary>Schema-qualified table, or an empty string when the mapping does not name one.</summary>
    public string QualifiedTable => (Schema, TableName) switch
    {
        (null or "", null or "") => string.Empty,
        (null or "", var table) => table!,
        (var schema, var table) => $"{schema}.{table}",
    };

    public bool HasTable => QualifiedTable.Length > 0;
}

/// <summary>
/// One EF Core migration, and the tables its <c>Up</c> and <c>Down</c> methods name.
/// </summary>
/// <remarks>
/// Tables come from the arguments bound to the <c>table</c> parameter of a
/// <c>MigrationBuilder</c> call — and to <c>name</c> for the table-level operations — so a
/// migration built out of raw <c>Sql()</c> reports no tables rather than a guess.
/// </remarks>
public sealed record DataMigration
{
    public long Id { get; init; }

    /// <summary>The <c>[Migration]</c> identifier, falling back to the type name.</summary>
    public required string Name { get; init; }

    public required string TypeFullyQualifiedName { get; init; }

    public required string TypeDisplay { get; init; }

    public long? TypeSymbolId { get; init; }

    /// <summary>The context named by <c>[DbContext(typeof(T))]</c>.</summary>
    public string? ContextFullyQualifiedName { get; init; }

    public string? ContextDisplay { get; init; }

    public long? ContextSymbolId { get; init; }

    public IReadOnlyList<string> Tables { get; init; } = [];

    public string? ProjectName { get; init; }

    public string? FilePath { get; init; }

    public int? Line { get; init; }

    public string TableSummary => Tables.Count == 0 ? "no tables named" : string.Join(", ", Tables);
}
