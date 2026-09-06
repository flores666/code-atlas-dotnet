using System.Runtime.CompilerServices;
using CodeAtlas.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeAtlas.Core.Analysis;

/// <summary>A loaded Roslyn solution together with everything that went wrong loading it.</summary>
public sealed class LoadedWorkspace(MSBuildWorkspace workspace, Solution solution, IReadOnlyList<IndexDiagnostic> diagnostics)
    : IDisposable
{
    public Solution Solution { get; } = solution;

    /// <summary>Problems MSBuild reported while loading, in the order they occurred.</summary>
    public IReadOnlyList<IndexDiagnostic> Diagnostics { get; } = diagnostics;

    public void Dispose() => workspace.Dispose();
}

/// <summary>
/// Opens a solution or project with <see cref="MSBuildWorkspace"/>.
/// </summary>
/// <remarks>
/// Load failures are collected rather than thrown: MSBuild reports an unloadable
/// project as a workspace diagnostic and yields an empty project in its place, which is
/// exactly the partial-failure behaviour CodeAtlas wants.
/// </remarks>
public static class MSBuildWorkspaceLoader
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<LoadedWorkspace> OpenAsync(
        WorkspaceTarget target,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var workspace = MSBuildWorkspace.Create();

        try
        {
            var loadProgress = progress is null
                ? null
                : new Progress<ProjectLoadProgress>(p => progress.Report(
                    $"{p.Operation} {Path.GetFileNameWithoutExtension(p.FilePath)}"));

            var solution = target.Kind switch
            {
                WorkspaceTargetKind.Solution =>
                    await workspace.OpenSolutionAsync(target.Path, loadProgress, cancellationToken)
                        .ConfigureAwait(false),
                _ => (await workspace.OpenProjectAsync(target.Path, loadProgress, cancellationToken)
                        .ConfigureAwait(false)).Solution,
            };

            // Read once the load has settled, rather than subscribing: the workspace
            // accumulates the same diagnostics and needs no cross-thread handling.
            var diagnostics = workspace.Diagnostics
                .Select(d => new IndexDiagnostic(
                    d.Kind == WorkspaceDiagnosticKind.Failure
                        ? Model.DiagnosticSeverity.Error
                        : Model.DiagnosticSeverity.Warning,
                    Project: null,
                    d.Message))
                .ToList();

            return new LoadedWorkspace(workspace, solution, diagnostics);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }
}
