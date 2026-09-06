using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Indexing;

public sealed record IndexingProgress(string Message, int CompletedProjects, int TotalProjects);

public sealed record IndexingResult(IndexMetadata Metadata, IReadOnlyList<IndexDiagnostic> Diagnostics);

/// <summary>
/// Builds a complete index for a workspace: load with Roslyn, walk every project, write
/// the result to the cache file.
/// </summary>
/// <remarks>
/// Failures are contained per project. A project MSBuild cannot load is reported by the
/// workspace and simply absent; a project whose compilation fails is recorded as
/// unloaded with a diagnostic. Either way the remaining projects are still indexed.
/// </remarks>
public sealed class IndexingService
{
    public async Task<IndexingResult> BuildAsync(
        WorkspaceTarget target,
        string databasePath,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        MSBuildEnvironment.EnsureRegistered();

        progress?.Report(new IndexingProgress($"Loading {target.DisplayName}...", 0, 0));

        var loadProgress = new Progress<string>(message =>
            progress?.Report(new IndexingProgress(message, 0, 0)));

        using var workspace = await MSBuildWorkspaceLoader
            .OpenAsync(target, loadProgress, cancellationToken)
            .ConfigureAwait(false);

        var projects = workspace.Solution.Projects
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var diagnostics = new List<IndexDiagnostic>(workspace.Diagnostics);

        using var database = SymbolIndexDatabase.Open(databasePath);
        using var session = database.BeginRebuild();

        for (var i = 0; i < projects.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var project = projects[i];
            progress?.Report(new IndexingProgress($"Indexing {project.Name}...", i, projects.Count));

            ProjectIndexData data;
            try
            {
                data = await SymbolCollector.CollectAsync(project, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                diagnostics.Add(new IndexDiagnostic(
                    Model.DiagnosticSeverity.Error,
                    project.Name,
                    $"Indexing failed and the project was skipped: {e.Message}"));
                continue;
            }

            var projectId = session.AddProject(data.Project);
            session.AddSymbols(projectId, data.Symbols);
            session.AddRelations(projectId, data.Relations);
            session.AddRegistrations(data.Registrations);
            session.AddEndpoints(projectId, data.Endpoints);
            session.AddEntities(projectId, data.Entities);
            session.AddMigrations(projectId, data.Migrations);
            session.AddConfiguration(projectId, data.Configuration);
            session.AddExternalDependencies(projectId, data.ExternalDependencies);
            diagnostics.AddRange(data.Diagnostics);
        }

        progress?.Report(new IndexingProgress("Writing index...", projects.Count, projects.Count));

        if (projects.Count == 0)
        {
            diagnostics.Add(new IndexDiagnostic(
                Model.DiagnosticSeverity.Warning,
                null,
                "No projects could be loaded from this workspace."));
        }

        session.AddDiagnostics(diagnostics);
        session.Complete(target.Path);

        var metadata = database.ReadMetadata()
            ?? throw new InvalidOperationException("The index was written but no metadata could be read back.");

        progress?.Report(new IndexingProgress(
            $"Indexed {metadata.SymbolCount:N0} symbols in {metadata.ProjectCount} project(s).",
            projects.Count,
            projects.Count));

        return new IndexingResult(metadata, diagnostics);
    }
}
