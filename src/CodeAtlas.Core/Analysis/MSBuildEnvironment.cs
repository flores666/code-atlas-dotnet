using Microsoft.Build.Locator;

namespace CodeAtlas.Core.Analysis;

/// <summary>
/// Registers the .NET SDK's MSBuild with the process.
/// </summary>
/// <remarks>
/// <see cref="MSBuildWorkspaceLoader"/> cannot resolve MSBuild assemblies until this has
/// run. Registration is process-wide and one-way, so it is done once and guarded.
/// </remarks>
public static class MSBuildEnvironment
{
    private static readonly Lock Gate = new();
    private static bool _registered;

    /// <summary>Registers the newest installed SDK, once per process. Safe to call repeatedly.</summary>
    /// <exception cref="InvalidOperationException">No .NET SDK is installed.</exception>
    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            if (!MSBuildLocator.IsRegistered)
            {
                var instance = MSBuildLocator.QueryVisualStudioInstances()
                    .OrderByDescending(i => i.Version)
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "No .NET SDK MSBuild instance was found. Install the .NET SDK to index solutions.");

                MSBuildLocator.RegisterInstance(instance);
            }

            _registered = true;
        }
    }
}
