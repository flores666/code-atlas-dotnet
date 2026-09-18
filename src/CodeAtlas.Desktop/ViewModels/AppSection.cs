namespace CodeAtlas.Desktop.ViewModels;

/// <summary>The three things the sidebar switches between.</summary>
public enum AppSection
{
    Indexing,
    Explorer,
    Endpoints,
}

/// <summary>What the endpoint details panel is showing.</summary>
public enum DetailsTab
{
    ExecutionTrace,
    Overview,
}

/// <summary>
/// How much of the workspace the current index covers, which is what the status
/// indicator reports. <see cref="Warnings"/> is the normal outcome for a solution
/// where some projects failed to load: the rest is still indexed and usable.
/// </summary>
public enum IndexHealth
{
    None,
    Indexing,
    Ready,
    Warnings,
    Failed,
}
