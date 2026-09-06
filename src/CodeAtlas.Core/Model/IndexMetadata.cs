namespace CodeAtlas.Core.Model;

/// <summary>Describes the index currently held in a cache file.</summary>
public sealed record IndexMetadata(
    int SchemaVersion,
    string SourcePath,
    DateTimeOffset IndexedAtUtc,
    int ProjectCount,
    int SymbolCount);
