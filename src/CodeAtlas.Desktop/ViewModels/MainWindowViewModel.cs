using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CodeAtlas.Core.Indexing;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;
using CodeAtlas.Desktop.Mvvm;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>
/// Drives the whole window: opening a workspace, indexing it, and browsing the result.
/// </summary>
/// <remarks>
/// Every index read runs on the thread pool; the database serialises them internally.
/// The analysed repository is only ever read.
/// </remarks>
public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>Keystrokes settle for this long before a search runs.</summary>
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(150);

    private const int SearchLimit = 200;

    private readonly RecentWorkspaces _recentWorkspaces = new();
    private readonly IndexingService _indexingService = new();

    private SymbolIndexDatabase? _database;
    private WorkspaceTarget? _target;
    private CancellationTokenSource? _indexingCancellation;
    private CancellationTokenSource? _searchCancellation;

    private string _workspaceTitle = "No workspace open";
    private string _workspacePath = string.Empty;
    private string _gitStatus = string.Empty;
    private string _indexedAtText = string.Empty;
    private string _statusMessage = "Open a solution or project to begin.";
    private string _searchText = string.Empty;
    private string _searchSummary = string.Empty;
    private AppSection _activeSection = AppSection.Overview;
    private IndexHealth _health = IndexHealth.None;
    private bool _hasWorkspace;
    private bool _hasIndex;
    private bool _indexingFailed;
    private int _projectCount;
    private int _symbolCount;
    private int _errorCount;
    private int _warningCount;
    private bool _isIndexing;
    private bool _isProgressIndeterminate = true;
    private double _progressValue;
    private double _progressMaximum = 1;
    private SymbolDetailsViewModel? _details;
    private TreeNodeViewModel? _selectedNode;
    private SearchResultViewModel? _selectedSearchResult;
    private EndpointViewModel? _selectedEndpoint;
    private string _endpointFilter = string.Empty;
    private string _infrastructureFilter = string.Empty;
    private InfrastructureView _infrastructureView = InfrastructureView.Database;
    private EntityRowViewModel? _selectedEntity;
    private MigrationRowViewModel? _selectedMigration;
    private ConfigurationRowViewModel? _selectedConfiguration;
    private ExternalRowViewModel? _selectedExternalService;

    public MainWindowViewModel()
    {
        ReindexCommand = new AsyncRelayCommand(_ => ReindexAsync(), _ => _target is not null && !IsIndexing);
        CancelIndexingCommand = new RelayCommand(_ => _indexingCancellation?.Cancel(), _ => IsIndexing);
        OpenRecentCommand = new AsyncRelayCommand(parameter => OpenAsync((string)parameter!));
        OpenSourceCommand = new RelayCommand(_ => OpenSource(), _ => Details?.CanOpenSource == true);
        NavigateCommand = new RelayCommand(parameter => Navigate(parameter as SymbolLink));
        ShowInGraphCommand = new RelayCommand(_ => ActiveSection = AppSection.Graph, _ => HasDetails);
        OpenEndpointSourceCommand = new RelayCommand(
            parameter => OpenEndpointSource(parameter as EndpointViewModel));
        SetInfrastructureViewCommand = new RelayCommand(
            parameter => InfrastructureView = (InfrastructureView)parameter!);

        // Picking a node explores from where the reader already is, so it updates the
        // details pane without moving the graph out from under them; "focus here" is the
        // explicit way to re-root.
        Graph.NodeSelected += id => _ = ShowDetailsAsync(id, updateGraphRoot: false);
        Graph.OpenSourceRequested += OpenSource;

        // A changed symbol is explored the way an endpoint or a resource is: show it, and
        // open the graph on it, because "what does this change touch" is a graph question.
        // Every starting point the spec names reduces to a symbol id, so there is one
        // command rather than one entry point per kind: a method or type from the details
        // pane, an endpoint's handler, an entity, or a changed symbol.
        AnalyzeImpactCommand = new RelayCommand(
            parameter => AnalyzeImpact(ImpactTarget(parameter)),
            parameter => ImpactTarget(parameter) is not null);

        Impact.SymbolSelected += id => _ = ShowDetailsAsync(id);
        Impact.EndpointSelected += endpoint => _ = ShowEndpointAsync(new EndpointViewModel(endpoint));

        GitChanges.SymbolSelected += id => _ = ShowChangedSymbolAsync(id);
        GitChanges.ChangedSetUpdated += Graph.SetChangedSymbols;
        GitChanges.StatusReported += message => StatusMessage = message;

        foreach (var path in _recentWorkspaces.Load())
        {
            RecentWorkspaces.Add(new RecentWorkspaceViewModel(path, OpenRecentCommand));
        }

        OnPropertyChanged(nameof(HasRecentWorkspaces));
    }

    // ---- workspace ----------------------------------------------------------

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

    public string GitStatus
    {
        get => _gitStatus;
        private set => SetProperty(ref _gitStatus, value);
    }

    public bool HasGitRepository => _target?.IsGitRepository == true;

    /// <summary>False until a workspace is opened, which is what the empty state keys off.</summary>
    public bool HasWorkspace
    {
        get => _hasWorkspace;
        private set => SetProperty(ref _hasWorkspace, value);
    }

    /// <summary>The section the sidebar currently has selected.</summary>
    public AppSection ActiveSection
    {
        get => _activeSection;
        set
        {
            if (SetProperty(ref _activeSection, value))
            {
                // The graph and the impact walk only query while they are on screen;
                // selecting symbols in the tree or in search results costs nothing until
                // the reader looks at them.
                Graph.IsActive = value == AppSection.Graph;
                Impact.IsActive = value == AppSection.Impact;
            }
        }
    }

    /// <summary>The semantic neighbourhood of the selected symbol.</summary>
    public GraphViewModel Graph { get; } = new();

    /// <summary>How the working tree differs from its Git baseline.</summary>
    public GitChangesViewModel GitChanges { get; } = new();

    /// <summary>What a change to the selected symbol can affect.</summary>
    public ImpactViewModel Impact { get; } = new();

    /// <summary>Runs impact analysis on a symbol and shows the result.</summary>
    public RelayCommand AnalyzeImpactCommand { get; }

    /// <summary>
    /// The symbol an impact request is about, whatever kind of row it came from.
    /// </summary>
    /// <remarks>
    /// A null parameter means "whatever is selected", which is what the details pane's own
    /// button passes. An endpoint is analysed through its handler, falling back to the
    /// controller that declares it, because that is the symbol its behaviour lives on.
    /// </remarks>
    private long? ImpactTarget(object? parameter) => parameter switch
    {
        EndpointViewModel endpoint =>
            endpoint.Endpoint.HandlerSymbolId ?? endpoint.Endpoint.DeclaringTypeSymbolId,
        ChangedSymbolViewModel changed => changed.SymbolId,
        EntityRowViewModel entity => entity.SymbolId,
        ExternalRowViewModel external => external.SymbolId,
        ConfigurationRowViewModel configuration => configuration.SymbolId,
        SymbolLink link => link.SymbolId,
        long id => id,
        _ => Details?.Symbol.Id,
    };

    private void AnalyzeImpact(long? symbolId)
    {
        if (symbolId is not { } id)
        {
            StatusMessage = "That row has no declaration in this solution to analyse.";
            return;
        }

        Impact.Analyze(id);
        ActiveSection = AppSection.Impact;
    }

    /// <summary>
    /// Opens a changed symbol: its details, and the graph rooted on it.
    /// </summary>
    /// <remarks>
    /// Nothing about this is Git-specific by the time it lands here — the callers, callees,
    /// implementations, endpoints and entities of a changed method are the ones the index
    /// already holds. Mapping the diff to a symbol is the whole of the work; navigating
    /// from it is the existing details pane.
    /// </remarks>
    private async Task ShowChangedSymbolAsync(long symbolId)
    {
        await ShowDetailsAsync(symbolId);

        if (Details is { } details)
        {
            StatusMessage = $"{details.Symbol.Display} changed in the working tree.";
            ActiveSection = AppSection.Graph;
        }
    }

    // ---- endpoints ----------------------------------------------------------

    /// <summary>Every HTTP entry point the index found, filtered by <see cref="EndpointFilter"/>.</summary>
    public ObservableCollection<EndpointViewModel> Endpoints { get; } = [];

    public bool HasEndpoints => Endpoints.Count > 0;

    public int EndpointCount => _allEndpoints.Count;

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
    /// Selecting an endpoint opens its flow: the graph is rooted at the action and seeded
    /// with the type that carries the injected dependencies, then the section switches to it.
    /// </summary>
    public EndpointViewModel? SelectedEndpoint
    {
        get => _selectedEndpoint;
        set
        {
            if (SetProperty(ref _selectedEndpoint, value) && value is not null)
            {
                _ = ShowEndpointAsync(value);
            }
        }
    }

    public RelayCommand OpenEndpointSourceCommand { get; }

    /// <summary>
    /// Opens the graph on an endpoint.
    /// </summary>
    /// <remarks>
    /// A controller action is its own root and its declaring type carries the injected
    /// services. An inline Minimal API handler declares nothing, so the flow is rooted at
    /// the first thing the lambda reaches and seeded with the rest. An endpoint that
    /// reaches nothing indexed still opens the section, on a notice saying why: a
    /// selection that produced nothing at all reads as a broken click.
    /// </remarks>
    private async Task ShowEndpointAsync(EndpointViewModel endpoint)
    {
        if (_database is not { } database)
        {
            return;
        }

        var target = endpoint.Endpoint;

        var (details, seeds) = await Task.Run<(SymbolDetails?, IReadOnlyList<long>)>(() =>
            target.HandlerSymbolId is { } handlerId
                ? (database.GetDetails(handlerId),
                   target.DeclaringTypeSymbolId is { } declaringId ? [declaringId] : [])
                : InlineFlow(database, target));

        Details = details is null ? null : new SymbolDetailsViewModel(details, NavigateCommand);
        Graph.FocusEndpoint(target, details?.Symbol, seeds);

        StatusMessage = details is null
            ? $"{endpoint.HttpMethod} {endpoint.Route} reaches nothing indexed in this workspace."
            : $"{endpoint.HttpMethod} {endpoint.Route} → {details.Symbol.Display}";

        ActiveSection = AppSection.Graph;
    }

    /// <summary>
    /// The flow behind an inline handler: rooted at the first symbol it reaches — the
    /// collector puts the services it is handed before the methods it calls — and seeded
    /// with the others, so one click maps the whole lambda rather than one arbitrary hop.
    /// </summary>
    private static (SymbolDetails? Details, IReadOnlyList<long> Seeds) InlineFlow(
        SymbolIndexDatabase database,
        HttpEndpoint endpoint)
    {
        var reached = database.GetEndpointDependencies(endpoint.Id)
            .Select(dependency => dependency.SymbolId)
            .OfType<long>()
            .Distinct()
            .ToList();

        return reached is [var first, .. var rest]
            ? (database.GetDetails(first), rest)
            : (null, []);
    }

    private void OpenEndpointSource(EndpointViewModel? endpoint)
    {
        if (endpoint?.Endpoint is { FilePath: { } path } target)
        {
            StatusMessage = SourceLauncher.Open(path, target.Line) ?? $"Opened {path}:{target.Line}";
        }
    }

    private void RefreshEndpoints()
    {
        Endpoints.Clear();

        var filter = EndpointFilter.Trim();
        foreach (var endpoint in _allEndpoints)
        {
            if (filter.Length == 0 ||
                endpoint.Route.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                endpoint.Handler.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                endpoint.HttpMethod.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                Endpoints.Add(endpoint);
            }
        }

        OnPropertyChanged(nameof(HasEndpoints));
        OnPropertyChanged(nameof(EndpointCount));
    }

    // ---- infrastructure -----------------------------------------------------

    /// <summary>
    /// The three infrastructure lists, shown one at a time. They are the same three cuts
    /// the graph filters by, seen as tables instead of as edges.
    /// </summary>
    public ObservableCollection<EntityRowViewModel> Entities { get; } = [];

    public ObservableCollection<MigrationRowViewModel> Migrations { get; } = [];

    public ObservableCollection<ConfigurationRowViewModel> ConfigurationKeys { get; } = [];

    public ObservableCollection<ExternalRowViewModel> ExternalServices { get; } = [];

    public RelayCommand SetInfrastructureViewCommand { get; }

    public InfrastructureView InfrastructureView
    {
        get => _infrastructureView;
        set
        {
            if (SetProperty(ref _infrastructureView, value))
            {
                OnPropertyChanged(nameof(IsDatabaseView));
                OnPropertyChanged(nameof(IsConfigurationView));
                OnPropertyChanged(nameof(IsExternalView));
                OnPropertyChanged(nameof(InfrastructureSubtitle));
            }
        }
    }

    public bool IsDatabaseView => InfrastructureView == InfrastructureView.Database;

    public bool IsConfigurationView => InfrastructureView == InfrastructureView.Configuration;

    public bool IsExternalView => InfrastructureView == InfrastructureView.ExternalServices;

    public string InfrastructureSubtitle => InfrastructureView switch
    {
        InfrastructureView.Database =>
            "EF Core entities, the contexts that declare them, and the migrations that touch their tables.",
        InfrastructureView.Configuration =>
            "Configuration keys, sections and options types found in source.",
        _ => "Where this application reaches infrastructure it does not own.",
    };

    public string InfrastructureFilter
    {
        get => _infrastructureFilter;
        set
        {
            if (SetProperty(ref _infrastructureFilter, value))
            {
                RefreshInfrastructure();
            }
        }
    }

    public bool HasEntities => Entities.Count > 0;

    public bool HasMigrations => Migrations.Count > 0;

    public bool HasConfigurationKeys => ConfigurationKeys.Count > 0;

    public bool HasExternalServices => ExternalServices.Count > 0;

    public int InfrastructureCount =>
        _allEntities.Count + _allConfiguration.Count + _allExternalServices.Count;

    public bool HasInfrastructure => InfrastructureCount > 0;

    public EntityRowViewModel? SelectedEntity
    {
        get => _selectedEntity;
        set
        {
            if (SetProperty(ref _selectedEntity, value) && value is not null)
            {
                Explore(value.SymbolId, value.HasTable ? $"{value.Entity} · {value.Table}" : value.Entity);
            }
        }
    }

    public MigrationRowViewModel? SelectedMigration
    {
        get => _selectedMigration;
        set
        {
            if (SetProperty(ref _selectedMigration, value) && value is not null)
            {
                Explore(value.SymbolId, value.Name);
            }
        }
    }

    public ConfigurationRowViewModel? SelectedConfiguration
    {
        get => _selectedConfiguration;
        set
        {
            if (SetProperty(ref _selectedConfiguration, value) && value is not null)
            {
                Explore(value.SymbolId, value.Key);
            }
        }
    }

    public ExternalRowViewModel? SelectedExternalService
    {
        get => _selectedExternalService;
        set
        {
            if (SetProperty(ref _selectedExternalService, value) && value is not null)
            {
                Explore(value.SymbolId, $"{value.Consumer} · {value.Resource}");
            }
        }
    }

    /// <summary>
    /// Opens a resource's flow the way selecting an endpoint opens one: the graph is rooted
    /// on it and framed to show what reaches it.
    /// </summary>
    private void Explore(long? symbolId, string caption)
    {
        if (symbolId is not { } id)
        {
            StatusMessage = $"{caption} has no declaration in this solution to explore.";
            return;
        }

        _ = ShowResourceAsync(id, caption);
    }

    private async Task ShowResourceAsync(long symbolId, string caption)
    {
        if (_database is not { } database)
        {
            return;
        }

        var details = await Task.Run(() => database.GetDetails(symbolId));
        Details = details is null ? null : new SymbolDetailsViewModel(details, NavigateCommand);

        if (details is not null)
        {
            Graph.FocusResource(details.Symbol, caption);
            ActiveSection = AppSection.Graph;
        }
    }

    private void RefreshInfrastructure()
    {
        var filter = InfrastructureFilter.Trim();

        Fill(Entities, _allEntities, row =>
            Matches(filter, row.Entity, row.Table, row.Context, row.Configuration));
        Fill(Migrations, _allMigrations, row => Matches(filter, row.Name, row.Context, row.Tables));
        Fill(ConfigurationKeys, _allConfiguration, row =>
            Matches(filter, row.Key, row.Options, row.Consumer));
        Fill(ExternalServices, _allExternalServices, row =>
            Matches(filter, row.Resource, row.Consumer, row.Client, row.TechnologyLabel));

        foreach (var property in (string[])
                 [
                     nameof(HasEntities), nameof(HasMigrations), nameof(HasConfigurationKeys),
                     nameof(HasExternalServices), nameof(InfrastructureCount), nameof(HasInfrastructure),
                 ])
        {
            OnPropertyChanged(property);
        }

        static void Fill<T>(ObservableCollection<T> view, List<T> all, Func<T, bool> keep)
        {
            view.Clear();
            foreach (var row in all.Where(keep))
            {
                view.Add(row);
            }
        }

        static bool Matches(string filter, params string[] fields) =>
            filter.Length == 0 ||
            fields.Any(field => field.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    // ---- index summary ------------------------------------------------------

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

    /// <summary>When the current index was built, as local wall-clock time.</summary>
    public string IndexedAtText
    {
        get => _indexedAtText;
        private set => SetProperty(ref _indexedAtText, value);
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
        IndexHealth.Indexing => "Indexing\u2026",
        IndexHealth.Ready => "Index up to date",
        IndexHealth.Warnings => "Indexed with issues",
        IndexHealth.Failed => "Indexing failed",
        _ => "Not indexed",
    };

    public ObservableCollection<RecentWorkspaceViewModel> RecentWorkspaces { get; } = [];

    public bool HasRecentWorkspaces => RecentWorkspaces.Count > 0;

    // ---- status -------------------------------------------------------------

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
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

    /// <summary>The unfiltered lists; the observable collections above are views of them.</summary>
    private readonly List<EndpointViewModel> _allEndpoints = [];
    private readonly List<EntityRowViewModel> _allEntities = [];
    private readonly List<MigrationRowViewModel> _allMigrations = [];
    private readonly List<ConfigurationRowViewModel> _allConfiguration = [];
    private readonly List<ExternalRowViewModel> _allExternalServices = [];

    public ObservableCollection<IndexDiagnostic> Diagnostics { get; } = [];

    public int ErrorCount
    {
        get => _errorCount;
        private set
        {
            if (SetProperty(ref _errorCount, value))
            {
                OnPropertyChanged(nameof(HasErrors));
                OnPropertyChanged(nameof(ErrorSummary));
            }
        }
    }

    public int WarningCount
    {
        get => _warningCount;
        private set
        {
            if (SetProperty(ref _warningCount, value))
            {
                OnPropertyChanged(nameof(HasWarnings));
                OnPropertyChanged(nameof(WarningSummary));
            }
        }
    }

    public int DiagnosticCount => Diagnostics.Count;

    public bool HasDiagnostics => Diagnostics.Count > 0;

    public bool HasErrors => ErrorCount > 0;

    public bool HasWarnings => WarningCount > 0;

    public string ErrorSummary => ErrorCount == 1 ? "1 error" : $"{ErrorCount} errors";

    public string WarningSummary => WarningCount == 1 ? "1 warning" : $"{WarningCount} warnings";

    // ---- browsing -----------------------------------------------------------

    public ObservableCollection<TreeNodeViewModel> ProjectNodes { get; } = [];

    /// <summary>The indexed projects, listed flat on the overview.</summary>
    public ObservableCollection<IndexedProject> Projects { get; } = [];

    public TreeNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value) && value?.Symbol is { } symbol)
            {
                _ = ShowDetailsAsync(symbol.Id);
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                OnPropertyChanged(nameof(HasSearchQuery));

                if (value.Length > 0)
                {
                    ActiveSection = AppSection.Search;
                }

                _ = SearchAsync();
            }
        }
    }

    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = [];

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchText);

    public bool HasSearchResults => SearchResults.Count > 0;

    public string SearchSummary
    {
        get => _searchSummary;
        private set => SetProperty(ref _searchSummary, value);
    }

    public SearchResultViewModel? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set
        {
            if (SetProperty(ref _selectedSearchResult, value) && value is not null)
            {
                _ = ShowDetailsAsync(value.Symbol.Id);
            }
        }
    }

    public SymbolDetailsViewModel? Details
    {
        get => _details;
        private set
        {
            if (SetProperty(ref _details, value))
            {
                OnPropertyChanged(nameof(HasDetails));
                OpenSourceCommand.RaiseCanExecuteChanged();
                ShowInGraphCommand.RaiseCanExecuteChanged();
                AnalyzeImpactCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasDetails => Details is not null;

    // ---- commands -----------------------------------------------------------

    public AsyncRelayCommand ReindexCommand { get; }

    public RelayCommand CancelIndexingCommand { get; }

    public AsyncRelayCommand OpenRecentCommand { get; }

    public RelayCommand OpenSourceCommand { get; }

    public RelayCommand NavigateCommand { get; }

    public RelayCommand ShowInGraphCommand { get; }

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
        GitStatus = target.IsGitRepository
            ? $"Git repository: {target.GitRoot}"
            : "Not inside a Git repository";
        OnPropertyChanged(nameof(HasGitRepository));
        GitChanges.SetWorkspace(target);
        ActiveSection = AppSection.Overview;

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
        }
        else
        {
            await RunIndexingAsync();
        }
    }

    private async Task ReindexAsync()
    {
        if (_target is not null)
        {
            await RunIndexingAsync();
        }
    }

    private async Task RunIndexingAsync()
    {
        if (_target is null || _database is null)
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

        var target = _target;
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

            // Git does not depend on the index, so the section still has a file-level
            // answer to give even when there is nothing to map it onto.
            GitChanges.SetIndex(null);
            Diagnostics.Add(new IndexDiagnostic(Core.Model.DiagnosticSeverity.Error, null, e.Message));
            RefreshDiagnosticCounts();
        }
        finally
        {
            IsIndexing = false;
            IsProgressIndeterminate = false;
            UpdateHealth();
        }
    }

    private void RefreshFromIndex()
    {
        if (_database is null)
        {
            return;
        }

        Details = null;
        SearchResults.Clear();
        OnPropertyChanged(nameof(HasSearchResults));
        SearchSummary = string.Empty;
        ProjectNodes.Clear();
        Projects.Clear();

        foreach (var project in _database.GetProjects())
        {
            var projectId = project.Id;
            ProjectNodes.Add(TreeNodeViewModel.ForProject(project, () => LoadNamespacesAsync(projectId)));
            Projects.Add(project);
        }

        Diagnostics.Clear();
        foreach (var diagnostic in _database.GetDiagnostics())
        {
            Diagnostics.Add(diagnostic);
        }

        RefreshDiagnosticCounts();
        Graph.SetDatabase(_database);

        Impact.SetDatabase(_database);

        // Re-read Git against the index that has just become current: a reindex can move
        // every declaration's recorded span, and the changed set is derived from those.
        GitChanges.SetIndex(_database);

        _allEndpoints.Clear();
        _allEndpoints.AddRange(_database.GetEndpoints().Select(endpoint => new EndpointViewModel(endpoint)));
        RefreshEndpoints();

        _allEntities.Clear();
        _allEntities.AddRange(_database.GetEntities().Select(entity => new EntityRowViewModel(entity)));
        _allMigrations.Clear();
        _allMigrations.AddRange(_database.GetMigrations().Select(migration => new MigrationRowViewModel(migration)));
        _allConfiguration.Clear();
        _allConfiguration.AddRange(
            _database.GetConfigurationUsages().Select(usage => new ConfigurationRowViewModel(usage)));
        _allExternalServices.Clear();
        _allExternalServices.AddRange(
            _database.GetExternalDependencies().Select(dependency => new ExternalRowViewModel(dependency)));
        RefreshInfrastructure();

        if (_database.ReadMetadata() is { } metadata)
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

        UpdateHealth();

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            _ = SearchAsync();
        }
    }

    private void RefreshDiagnosticCounts()
    {
        ErrorCount = Diagnostics.Count(diagnostic => diagnostic.Severity == Core.Model.DiagnosticSeverity.Error);
        WarningCount = Diagnostics.Count - ErrorCount;
        OnPropertyChanged(nameof(DiagnosticCount));
        OnPropertyChanged(nameof(HasDiagnostics));
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

    // ---- tree ---------------------------------------------------------------

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
            .Select(member => member.Kind is IndexedSymbolKind.Class or IndexedSymbolKind.Interface
                    or IndexedSymbolKind.Record or IndexedSymbolKind.Struct or IndexedSymbolKind.Enum
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

    // ---- search and details -------------------------------------------------

    private async Task SearchAsync()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();

        var cancellationToken = _searchCancellation.Token;
        var query = SearchText;

        if (_database is not { } database || string.IsNullOrWhiteSpace(query))
        {
            SearchResults.Clear();
            OnPropertyChanged(nameof(HasSearchResults));
            SearchSummary = string.Empty;
            return;
        }

        try
        {
            // Lets a burst of keystrokes settle before touching the index.
            await Task.Delay(SearchDebounce, cancellationToken);

            var results = await Task.Run(() => database.Search(query, SearchLimit), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            SearchResults.Clear();
            foreach (var result in results)
            {
                SearchResults.Add(new SearchResultViewModel(result));
            }

            OnPropertyChanged(nameof(HasSearchResults));

            SearchSummary = results.Count switch
            {
                0 => "No matches",
                SearchLimit => $"First {SearchLimit} matches",
                1 => "1 match",
                _ => $"{results.Count} matches",
            };
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
    }

    /// <summary>
    /// Shows a symbol. <paramref name="updateGraphRoot"/> is false only when the selection
    /// came from the graph itself, which must not re-centre on every click.
    /// </summary>
    private async Task ShowDetailsAsync(long symbolId, bool updateGraphRoot = true)
    {
        if (_database is not { } database)
        {
            return;
        }

        var details = await Task.Run(() => database.GetDetails(symbolId));
        Details = details is null ? null : new SymbolDetailsViewModel(details, NavigateCommand);

        if (updateGraphRoot)
        {
            Graph.SetRoot(details?.Symbol);
        }
    }

    private void Navigate(SymbolLink? link)
    {
        if (link?.SymbolId is { } symbolId)
        {
            _ = ShowDetailsAsync(symbolId);
        }
    }

    private void OpenSource()
    {
        if (Details is { } details)
        {
            OpenSource(details.Symbol);
        }
    }

    private void OpenSource(IndexedSymbol symbol)
    {
        StatusMessage = SourceLauncher.Open(symbol.FilePath ?? string.Empty, symbol.Line)
                        ?? $"Opened {symbol.FilePath}:{symbol.Line}";
    }

    private void ClearWorkspace()
    {
        Graph.SetDatabase(null);
        Impact.SetDatabase(null);
        GitChanges.SetWorkspace(null);
        _database?.Dispose();
        _database = null;

        ProjectNodes.Clear();
        Projects.Clear();
        _allEndpoints.Clear();
        RefreshEndpoints();
        _allEntities.Clear();
        _allMigrations.Clear();
        _allConfiguration.Clear();
        _allExternalServices.Clear();
        RefreshInfrastructure();
        SearchResults.Clear();
        OnPropertyChanged(nameof(HasSearchResults));
        Diagnostics.Clear();
        Details = null;
        SearchSummary = string.Empty;
        IndexedAtText = string.Empty;
        ProjectCount = 0;
        SymbolCount = 0;
        _hasIndex = false;
        _indexingFailed = false;
        RefreshDiagnosticCounts();
        UpdateHealth();
    }

    public void Dispose()
    {
        _indexingCancellation?.Cancel();
        _indexingCancellation?.Dispose();
        _searchCancellation?.Dispose();
        _database?.Dispose();
    }
}
