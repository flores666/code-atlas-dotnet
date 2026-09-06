namespace CodeAtlas.Core.Model;

/// <summary>The shape of a configuration read, which is what decides how much of it is knowable.</summary>
public enum ConfigurationAccess
{
    /// <summary>The consumer takes <c>IConfiguration</c> itself, so the keys it reads are its own business.</summary>
    Provider,

    /// <summary>A <c>GetSection</c> or <c>GetChildren</c> read of a subtree.</summary>
    Section,

    /// <summary>One key, read through the indexer or <c>GetValue</c>.</summary>
    Value,

    /// <summary>A named connection string.</summary>
    ConnectionString,

    /// <summary>An options type bound to configuration, or injected as <c>IOptions&lt;T&gt;</c>.</summary>
    Options,

    /// <summary>An environment variable read by name.</summary>
    Environment,
}

/// <summary>
/// One statically discoverable configuration read.
/// </summary>
/// <remarks>
/// Keys are recorded only when they were written as constants, and section paths only as
/// far back as the receiver chain is literal, so a key that is listed is one that exists
/// in source. Either end may be unknown: an options type bound in <c>Program.cs</c> has no
/// consumer symbol worth naming, and an indexer read names a key but no type.
/// </remarks>
public sealed record ConfigurationUsage
{
    public long Id { get; init; }

    public required ConfigurationAccess Access { get; init; }

    /// <summary>The configuration key or section path, colon-separated as the provider writes it.</summary>
    public string? Key { get; init; }

    /// <summary>The options type bound to this configuration, when there is one.</summary>
    public string? OptionsFullyQualifiedName { get; init; }

    public string? OptionsDisplay { get; init; }

    public long? OptionsSymbolId { get; init; }

    /// <summary>The type that reads it.</summary>
    public string? ConsumerFullyQualifiedName { get; init; }

    public string? ConsumerDisplay { get; init; }

    public long? ConsumerSymbolId { get; init; }

    public string? ProjectName { get; init; }

    public string? FilePath { get; init; }

    public int? Line { get; init; }

    public RelationProvenance Provenance { get; init; } = RelationProvenance.Exact;
}
