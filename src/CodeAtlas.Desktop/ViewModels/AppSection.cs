namespace CodeAtlas.Desktop.ViewModels;

/// <summary>The main-content sections reachable from the sidebar.</summary>
public enum AppSection
{
    Overview,
    Explorer,
    Search,
    Endpoints,
    Infrastructure,
    Graph,
    Diagnostics,
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
