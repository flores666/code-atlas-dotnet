namespace CodeAtlas.Core.Model;

public enum WorkspaceTargetKind
{
    Solution,
    Project,
}

/// <summary>The .sln/.slnx/.csproj CodeAtlas was pointed at.</summary>
public sealed record WorkspaceTarget
{
    public required WorkspaceTargetKind Kind { get; init; }

    /// <summary>Absolute path to the solution or project file.</summary>
    public required string Path { get; init; }

    public required string DisplayName { get; init; }
}
