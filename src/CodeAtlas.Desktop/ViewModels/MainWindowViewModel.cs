using System.Collections.ObjectModel;
using System.Globalization;
using CodeAtlas.Core.Indexing;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Trace;
using CodeAtlas.Core.Workspace;
using CodeAtlas.Desktop.Mvvm;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>
/// Drives the whole window: opening a solution, indexing it, browsing what was declared,
/// reading the source of anything, and tracing what an endpoint executes.
/// </summary>
/// <remarks>
/// Every index read and every file read runs on the thread pool; the database serialises
/// its own access. The analysed repository is only ever read.
/// </remarks>
public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly RecentWorkspaces _recentWorkspaces = new();
    private readonly IndexingService _indexingService = new();
    private readonly List<EndpointViewModel> _allEndpoints = [];
    private readonly List<IndexedProject> _allProjects = [];

    private SymbolIndexDatabase? _database;
    private WorkspaceTarget? _target;
    private CancellationTokenSource? _indexingCancellation;

    private string _workspaceTitle = "Choose a solution";
    private string _workspacePath = string.Empty;
    private string _statusMessage = "Open a solution or project to begin.";
    private AppSection _activeSection = AppSection.Indexing;
    private DetailsTab _detailsTab = DetailsTab.ExecutionTrace;
    private IndexHealth _health = IndexHealth.None;
    private bool _hasWorkspace;
    private bool _hasIndex;
    private bool _indexingFailed;
    private string _indexedAtText = string.Empty;
    private int _projectCount;
    private int _symbolCount;
    private bool _isIndexing;
    private bool _isProgressIndeterminate = true;
    private double _progressValue;
    private double _progressMaximum = 1;
    private string _endpointFilter = string.Empty;
    private ProjectCardViewModel? _selectedProject;
    private EndpointViewModel? _selectedEndpoint;
    private TraceStepViewModel? _selectedStep;
    private TreeNodeViewModel? _selectedNode;
    private SourceViewModel? _source;
    private string _traceSummary = string.Empty;

    public MainWindowViewModel()
    {
        ReindexCommand = new AsyncRelayCommand(_ => RunIndexingAsync(), _ => _target is not null && !IsIndexing);
        CancelIndexingCommand = new RelayCommand(_ => _indexingCancellation?.Cancel(), _ => IsIndexing);
        OpenRecentCommand = new AsyncRelayCommand(parameter => OpenAsync((string)parameter!));
        OpenExternallyCommand = new RelayCommand(OpenExternally);

        foreach (var path in _recentWorkspaces.Load())
        {
            RecentWorkspaces.Add(new RecentWorkspaceViewModel(path, OpenRecentCommand));
        }
    }

    // ---- workspace ----------------------------------------------------------

    /// <summary>The name shown in the title bar, which is also the solution picker.</summary>
    public string WorkspaceTitle
    {
        get => _workspaceTitle;
        private set => SetProperty(ref _workspaceTitle, value);
    }

    public string WorkspacePath
    {
        get => _workspacePath;
        private set => SetProperty(ref _workspacePath, value);
    }

    /// <summary>False until a solution is opened, which is what the empty state keys off.</summary>
    public bool HasWorkspace
    {
        get => _hasWorkspace;
        private set => SetProperty(ref _hasWorkspace, value);
    }

    /// <summary>The solutions opened before, newest first, offered by the title-bar picker.</summary>
    public ObservableCollection<RecentWorkspaceViewModel> RecentWorkspaces { get; } = [];

    public bool HasRecentWorkspaces => RecentWorkspaces.Count > 0;

    /// <summary>The section the sidebar currently has selected.</summary>
    public AppSection ActiveSection
    {
        get => _activeSection;
        set => SetProperty(ref _activeSection, value);
    }

    public AsyncRelayCommand ReindexCommand { get; }

    public RelayCommand CancelIndexingCommand { get; }

    public AsyncRelayCommand OpenRecentCommand { get; }

    /// <summary>Hands a declaration to an external editor, which the viewer does not replace.</summary>
    public RelayCommand OpenExternallyCommand { get; }

    /// <summary>
    /// Opens a solution, project or directory. A cached index for the same workspace is
    /// reused as-is; otherwise one is built.
    /// </summary>
    public async Task OpenAsync(string path)
    {
        if (WorkspaceLocator.Resolve(path) is not { } target)
        {
            StatusMessage = $"No .sln, .slnx or .csproj was found at '{path}'.";
            return;
        }

        ClearWorkspace();

        _target = target;
        HasWorkspace = true;
        WorkspaceTitle = target.DisplayName;
        WorkspacePath = target.Path;

        RecentWorkspaces.Clear();
        foreach (var recent in _recentWorkspaces.Add(target.Path))
        {
            RecentWorkspaces.Add(new RecentWorkspaceViewModel(recent, OpenRecentCommand));
        }

        OnPropertyChanged(nameof(HasRecentWorkspaces));

        try
        {
            _database = SymbolIndexDatabase.Open(IndexCache.GetDatabasePath(target.Path));
        }
        catch (Exception e)
        {
            StatusMessage = $"Could not open the index cache: {e.Message}";
            return;
        }

        ReindexCommand.RaiseCanExecuteChanged();

        if (_database.HasUsableIndexFor(target.Path))
        {
            StatusMessage = "Loaded the cached index.";
            RefreshFromIndex();
            ActiveSection = AppSection.Endpoints;
        }
        else
        {
            ActiveSection = AppSection.Indexing;
            await RunIndexingAsync();
        }
    }

    /// <summary>The directory the workspace was opened from, which paths are shown against.</summary>
    private string? WorkspaceDirectory => _target is { } target ? Path.GetDirectoryName(target.Path) : null;

    // ---- indexing -----------------------------------------------------------

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public IndexHealth Health
    {
        get => _health;
        private set
        {
            if (SetProperty(ref _health, value))
            {
                OnPropertyChanged(nameof(IndexStateText));
            }
        }
    }

    public string IndexStateText => Health switch
    {
        IndexHealth.Indexing => "Indexing…",
        IndexHealth.Ready => "Index up to date",
        IndexHealth.Warnings => "Indexed with issues",
        IndexHealth.Failed => "Indexing failed",
        _ => "Not indexed",
    };

    /// <summary>When the current index was built, as local wall-clock time.</summary>
    public string IndexedAtText
    {
        get => _indexedAtText;
        private set => SetProperty(ref _indexedAtText, value);
    }

    /// <summary>
    /// What indexing could not do.
    /// </summary>
    /// <remarks>
    /// A partly loadable solution is the normal case rather than a failure, and the reason
    /// matters: a project whose references are missing contributes no endpoints, which
    /// looks identical to a project that declares none. This is where that shows.
    /// </remarks>
    public ObservableCollection<IndexDiagnostic> Diagnostics { get; } = [];

    public bool HasDiagnostics => Diagnostics.Count > 0;

    public int ProjectCount
    {
        get => _projectCount;
        private set => SetProperty(ref _projectCount, value);
    }

    public int SymbolCount
    {
        get => _symbolCount;
        private set => SetProperty(ref _symbolCount, value);
    }

    public bool IsIndexing
    {
        get => _isIndexing;
        private set
        {
            if (SetProperty(ref _isIndexing, value))
            {
                ReindexCommand.RaiseCanExecuteChanged();
                CancelIndexingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public double ProgressMaximum
    {
        get => _progressMaximum;
        private set => SetProperty(ref _progressMaximum, value);
    }

    private async Task RunIndexingAsync()
    {
        if (_target is not { } target || _database is null)
        {
            return;
        }

        IsIndexing = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        _indexingFailed = false;
        UpdateHealth();

        _indexingCancellation?.Dispose();
        _indexingCancellation = new CancellationTokenSource();
        var cancellationToken = _indexingCancellation.Token;

        var progress = new Progress<IndexingProgress>(report =>
        {
            StatusMessage = report.Message;
            IsProgressIndeterminate = report.TotalProjects == 0;
            ProgressMaximum = Math.Max(1, report.TotalProjects);
            ProgressValue = report.CompletedProjects;
        });

        var databasePath = IndexCache.GetDatabasePath(target.Path);

        try
        {
            var result = await Task.Run(
                () => _indexingService.BuildAsync(target, databasePath, progress, cancellationToken),
                cancellationToken);

            StatusMessage = $"Indexed {result.Metadata.SymbolCount:N0} symbols " +
                            $"in {result.Metadata.ProjectCount} project(s).";
            RefreshFromIndex();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Indexing cancelled. The previous index was left unchanged.";
        }
        catch (Exception e)
        {
            StatusMessage = $"Indexing failed: {e.Message}";
            _indexingFailed = true;
            Diagnostics.Add(new IndexDiagnostic(Core.Model.DiagnosticSeverity.Error, null, e.Message));
            OnPropertyChanged(nameof(HasDiagnostics));
        }
        finally
        {
            IsIndexing = false;
            IsProgressIndeterminate = false;
            UpdateHealth();
        }
    }

    /// <summary>
    /// Diagnostics are the normal outcome of a partly loadable solution, so they degrade
    /// the reported state rather than failing it; only a run that threw is a failure.
    /// </summary>
    private void UpdateHealth() => Health = (IsIndexing, _indexingFailed, _hasIndex, HasDiagnostics) switch
    {
        (true, _, _, _) => IndexHealth.Indexing,
        (_, true, _, _) => IndexHealth.Failed,
        (_, _, false, _) => IndexHealth.None,
        (_, _, _, true) => IndexHealth.Warnings,
        _ => IndexHealth.Ready,
    };

    private void RefreshFromIndex()
    {
        if (_database is not { } database || _target is not { } target)
        {
            return;
        }

        _allProjects.Clear();
        _allProjects.AddRange(database.GetProjects());

        Diagnostics.Clear();
        foreach (var diagnostic in database.GetDiagnostics())
        {
            Diagnostics.Add(diagnostic);
        }

        OnPropertyChanged(nameof(HasDiagnostics));

        _allEndpoints.Clear();
        _allEndpoints.AddRange(database.GetEndpoints().Select(endpoint => new EndpointViewModel(endpoint)));

        if (database.ReadMetadata() is { } metadata)
        {
            _hasIndex = true;
            ProjectCount = metadata.ProjectCount;
            SymbolCount = metadata.SymbolCount;
            IndexedAtText = metadata.IndexedAtUtc
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        }
        else
        {
            _hasIndex = false;
            ProjectCount = 0;
            SymbolCount = 0;
            IndexedAtText = string.Empty;
        }

        Projects.Clear();
        Projects.Add(ProjectCardViewModel.ForSolution(target, _allProjects.Count, SymbolCount));
        foreach (var project in _allProjects)
        {
            Projects.Add(ProjectCardViewModel.ForProject(project));
        }

        // Assigning the solution card re-scopes everything, which is what fills the tree
        // and the endpoint list.
        SelectedProject = Projects[0];

        UpdateHealth();
    }

    // ---- the project strip, which scopes everything under it ----------------

    /// <summary>The open solution, then each project in it.</summary>
    public ObservableCollection<ProjectCardViewModel> Projects { get; } = [];

    public bool HasProjects => Projects.Count > 0;

    public ProjectCardViewModel? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (!SetProperty(ref _selectedProject, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasProjects));
            RefreshTree();
            RefreshEndpoints();
        }
    }

    /// <summary>The projects in scope: one, or all of them under the solution card.</summary>
    private IEnumerable<IndexedProject> ScopedProjects =>
        SelectedProject?.ProjectName is { } name
            ? _allProjects.Where(project => project.Name == name)
            : _allProjects;

    // ---- the explorer tree --------------------------------------------------

    public ObservableCollection<TreeNodeViewModel> ProjectNodes { get; } = [];

    /// <summary>Selecting a symbol opens the file it is declared in, at its declaration.</summary>
    public TreeNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value) && value?.Symbol is { } symbol)
            {
                _ = ShowSourceAsync(symbol);
            }
        }
    }

    private void RefreshTree()
    {
        ProjectNodes.Clear();
        foreach (var project in ScopedProjects)
        {
            var projectId = project.Id;
            ProjectNodes.Add(TreeNodeViewModel.ForProject(project, () => LoadNamespacesAsync(projectId)));
        }
    }

    private Task<IReadOnlyList<TreeNodeViewModel>> LoadNamespacesAsync(long projectId) =>
        QueryAsync(database => database
            .GetNamespaces(projectId)
            .Select(@namespace => TreeNodeViewModel.ForNamespace(
                @namespace,
                () => LoadTypesAsync(projectId, @namespace)))
            .ToList());

    private Task<IReadOnlyList<TreeNodeViewModel>> LoadTypesAsync(long projectId, string @namespace) =>
        QueryAsync(database => database
            .GetTypes(projectId, @namespace)
            .Select(type => TreeNodeViewModel.ForType(
                type,
                () => LoadMembersAsync(type.FullyQualifiedName)))
            .ToList());

    private Task<IReadOnlyList<TreeNodeViewModel>> LoadMembersAsync(string containerFullyQualifiedName) =>
        QueryAsync(database => database
            .GetMembers(containerFullyQualifiedName)
            .Select(member => IndexedSymbolKinds.IsType(member.Kind)
                ? TreeNodeViewModel.ForType(member, () => LoadMembersAsync(member.FullyQualifiedName))
                : TreeNodeViewModel.ForMember(member))
            .ToList());

    private async Task<IReadOnlyList<TreeNodeViewModel>> QueryAsync(
        Func<SymbolIndexDatabase, List<TreeNodeViewModel>> query)
    {
        if (_database is not { } database)
        {
            return [];
        }

        return await Task.Run(() => query(database));
    }

    // ---- endpoints and their traces -----------------------------------------

    /// <summary>The HTTP entry points in scope, filtered by <see cref="EndpointFilter"/>.</summary>
    public ObservableCollection<EndpointViewModel> Endpoints { get; } = [];

    public bool HasEndpoints => Endpoints.Count > 0;

    public int EndpointCount => _allEndpoints.Count;

    public bool HasAnyEndpoints => _allEndpoints.Count > 0;

    public string EndpointFilter
    {
        get => _endpointFilter;
        set
        {
            if (SetProperty(ref _endpointFilter, value))
            {
                RefreshEndpoints();
            }
        }
    }

    /// <summary>
    /// Selecting an endpoint traces it and opens its handler: the flow on the right, the
    /// code it starts in in the middle.
    /// </summary>
    public EndpointViewModel? SelectedEndpoint
    {
        get => _selectedEndpoint;
        set
        {
            if (SetProperty(ref _selectedEndpoint, value))
            {
                OnPropertyChanged(nameof(HasSelectedEndpoint));
                _ = TraceAsync(value);
            }
        }
    }

    public bool HasSelectedEndpoint => SelectedEndpoint is not null;

    /// <summary>The selected endpoint's execution trace, in reading order.</summary>
    public ObservableCollection<TraceStepViewModel> Trace { get; } = [];

    public bool HasTrace => Trace.Count > 0;

    /// <summary>Selecting a step opens the code it runs.</summary>
    public TraceStepViewModel? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (SetProperty(ref _selectedStep, value) && value is not null)
            {
                _ = ShowSourceAsync(value.Symbol);
            }
        }
    }

    /// <summary>How much of the flow is shown, and what stopped it when something did.</summary>
    public string TraceSummary
    {
        get => _traceSummary;
        private set => SetProperty(ref _traceSummary, value);
    }

    public DetailsTab DetailsTab
    {
        get => _detailsTab;
        set => SetProperty(ref _detailsTab, value);
    }

    private void RefreshEndpoints()
    {
        var scope = SelectedProject?.ProjectName;
        var filter = EndpointFilter.Trim();

        Endpoints.Clear();
        foreach (var endpoint in _allEndpoints)
        {
            var inScope = scope is null || endpoint.Endpoint.ProjectName == scope;
            var matches = filter.Length == 0 ||
                          endpoint.Route.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                          endpoint.Handler.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                          endpoint.HttpMethod.Contains(filter, StringComparison.OrdinalIgnoreCase);

            if (inScope && matches)
            {
                Endpoints.Add(endpoint);
            }
        }

        OnPropertyChanged(nameof(HasEndpoints));
        OnPropertyChanged(nameof(HasAnyEndpoints));
        OnPropertyChanged(nameof(EndpointCount));

        // A selection the new scope or filter no longer contains has to go, or the
        // details panel keeps describing a row that is not on screen.
        if (SelectedEndpoint is { } selected && !Endpoints.Contains(selected))
        {
            SelectedEndpoint = null;
        }
    }

    private async Task TraceAsync(EndpointViewModel? endpoint)
    {
        Trace.Clear();
        OnPropertyChanged(nameof(HasTrace));
        TraceSummary = string.Empty;

        if (_database is not { } database || endpoint is null)
        {
            return;
        }

        var trace = await Task.Run(() => EndpointTraceBuilder.Build(database, endpoint.Endpoint));

        foreach (var step in trace.Steps)
        {
            Trace.Add(new TraceStepViewModel(step, Trace.Count + 1));
        }

        OnPropertyChanged(nameof(HasTrace));

        TraceSummary = (trace.Steps.Count, trace.Truncated) switch
        {
            (0, _) => "Nothing this endpoint reaches is declared in this solution.",
            (_, true) => $"First {trace.Steps.Count} steps; the flow is longer than that.",
            (1, _) => "1 step",
            var (count, _) => $"{count} steps",
        };

        // The first step is where the request lands, so that is the code to show.
        SelectedStep = Trace.FirstOrDefault();
    }

    // ---- source -------------------------------------------------------------

    /// <summary>The file open in the viewer, or <c>null</c> when nothing has been opened.</summary>
    public SourceViewModel? Source
    {
        get => _source;
        private set
        {
            if (SetProperty(ref _source, value))
            {
                OnPropertyChanged(nameof(HasSource));
            }
        }
    }

    public bool HasSource => Source is not null;

    private async Task ShowSourceAsync(IndexedSymbol symbol)
    {
        var directory = WorkspaceDirectory;
        var source = await Task.Run(() => SourceViewModel.Load(symbol, directory));

        if (source is null)
        {
            StatusMessage = symbol.FilePath is { Length: > 0 } path
                ? $"Could not open {path}."
                : $"{symbol.Display} has no source file in this solution.";
            return;
        }

        Source = source;
        StatusMessage = $"{source.FileName}:{source.Line}";
    }

    /// <summary>
    /// Hands whatever the row stands for to an external editor. The viewer shows the code;
    /// this is for when the reader wants to change it.
    /// </summary>
    private void OpenExternally(object? parameter)
    {
        var (path, line) = parameter switch
        {
            TraceStepViewModel step => (step.Symbol.FilePath, step.Symbol.Line),
            EndpointViewModel endpoint => (endpoint.Endpoint.FilePath, endpoint.Endpoint.Line),
            TreeNodeViewModel { Symbol: { } symbol } => (symbol.FilePath, symbol.Line),
            _ => (Source?.FilePath, Source?.Line),
        };

        StatusMessage = path is null
            ? "That row has no declaration in this solution to open."
            : SourceLauncher.Open(path, line) ?? $"Opened {path}:{line}";
    }

    private void ClearWorkspace()
    {
        _database?.Dispose();
        _database = null;

        SelectedEndpoint = null;
        SelectedStep = null;
        SelectedNode = null;
        Source = null;
        _allProjects.Clear();
        _allEndpoints.Clear();
        Projects.Clear();
        SelectedProject = null;
        ProjectNodes.Clear();
        RefreshEndpoints();

        Diagnostics.Clear();
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(HasProjects));
        IndexedAtText = string.Empty;
        ProjectCount = 0;
        SymbolCount = 0;
        _hasIndex = false;
        _indexingFailed = false;
        UpdateHealth();
    }

    public void Dispose()
    {
        _indexingCancellation?.Cancel();
        _indexingCancellation?.Dispose();
        _database?.Dispose();
    }
}
