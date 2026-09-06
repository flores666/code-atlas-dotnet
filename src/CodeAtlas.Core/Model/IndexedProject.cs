namespace CodeAtlas.Core.Model;

public sealed record IndexedProject
{
    public long Id { get; init; }

    public required string Name { get; init; }

    public string? FilePath { get; init; }

    public string? AssemblyName { get; init; }

    /// <summary>False when the project failed to load; its symbols are absent from the index.</summary>
    public bool Loaded { get; init; } = true;
}
