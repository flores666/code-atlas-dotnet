namespace CodeAtlas.Core.Model;

public enum WorkspaceTargetKind
{
    Solution,
    Project,
}

/// <summary>The .sln/.slnx/.csproj CodeAtlas was pointed at, plus its Git context.</summary>
public sealed record WorkspaceTarget
{
    public required WorkspaceTargetKind Kind { get; init; }

    /// <summary>Absolute path to the solution or project file.</summary>
    public required string Path { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Absolute path of the Git working tree root, or <c>null</c> when not under Git.</summary>
    public string? GitRoot { get; init; }

    public bool IsGitRepository => GitRoot is not null;
}
