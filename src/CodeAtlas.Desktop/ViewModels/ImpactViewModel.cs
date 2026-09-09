using System.Collections.ObjectModel;
using CodeAtlas.Core.Impact;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using CodeAtlas.Desktop.Mvvm;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>
/// What a change to the selected symbol can affect.
/// </summary>
/// <remarks>
/// The report is the model's; this shapes it for reading. Two things matter in the
/// presentation and both come from the spec rather than from taste: direct and indirect
/// effects stay apart, and the risk level is never shown without the facts underneath it.
/// </remarks>
public sealed class ImpactViewModel : ObservableObject
{
    private readonly List<ImpactRowViewModel> _allIndirect = [];

    private SymbolIndexDatabase? _database;
    private ImpactReport? _report;
    private long? _pending;
    private CancellationTokenSource? _analysis;

    private bool _isActive;
    private bool _isLoading;
    private string _filter = string.Empty;
    private ImpactRowViewModel? _selectedRow;
    private EndpointViewModel? _selectedEndpoint;

    public ImpactViewModel() =>
        AnalyzeCommand = new AsyncRelayCommand(_ => RunAsync(), _ => _pending is not null && !IsLoading);

    /// <summary>Raised when a row is picked, so the shell can show that symbol.</summary>
    public event Action<long>? SymbolSelected;

    public event Action<HttpEndpoint>? EndpointSelected;

    public ObservableCollection<ImpactRowViewModel> DirectCallers { get; } = [];

    public ObservableCollection<ImpactRowViewModel> IndirectCallers { get; } = [];

    public ObservableCollection<ImpactRowViewModel> Implementations { get; } = [];

    public ObservableCollection<ImpactRowViewModel> Workers { get; } = [];

    public ObservableCollection<EndpointViewModel> Endpoints { get; } = [];

    public ObservableCollection<EntityRowViewModel> Entities { get; } = [];

    public ObservableCollection<ExternalRowViewModel> ExternalIntegrations { get; } = [];

    public ObservableCollection<ConfigurationRowViewModel> Configuration { get; } = [];

    /// <summary>The five-line answer, before any of it is opened.</summary>
    public ObservableCollection<ImpactCountViewModel> Summary { get; } = [];

    /// <summary>Every risk property, present or not, each with its evidence.</summary>
    public ObservableCollection<RiskSignalViewModel> RiskSignals { get; } = [];

    public AsyncRelayCommand AnalyzeCommand { get; }

    public bool HasReport => _report is not null;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                AnalyzeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string RootTitle => _report?.Root.Display ?? "No symbol analysed";

    public string RootSubtitle => _report is { } report
        ? $"{SymbolGlyph.Keyword(report.Root.Kind)} · {report.Root.FullyQualifiedName}"
        : "Select a symbol and run impact analysis to see what a change to it can reach.";

    public string RiskLevelText => _report?.Risk.Level switch
    {
        RiskLevel.High => "High impact",
        RiskLevel.Moderate => "Moderate impact",
        RiskLevel.Low => "Low impact",
        _ => string.Empty,
    };

    public bool IsHighRisk => _report?.Risk.Level == RiskLevel.High;

    public bool IsModerateRisk => _report?.Risk.Level == RiskLevel.Moderate;

    /// <summary>The rule behind the level, so the verdict is never bare.</summary>
    public string RiskRationale => _report?.Risk.Rationale ?? string.Empty;

    /// <summary>
    /// How far the closure reaches, which is what stops a long transitive list from
    /// reading as one undifferentiated blast radius.
    /// </summary>
    public string ReachText => _report is { } report
        ? $"{report.Impacted.Count} symbols affected · up to {report.MaxDistance} hop(s) away"
        : string.Empty;

    public bool IsTruncated => _report?.Truncated == true;

    public bool HasDirectCallers => DirectCallers.Count > 0;

    public bool HasIndirectCallers => IndirectCallers.Count > 0;

    public bool HasImplementations => Implementations.Count > 0;

    public bool HasWorkers => Workers.Count > 0;

    public bool HasEndpoints => Endpoints.Count > 0;

    public bool HasEntities => Entities.Count > 0;

    public bool HasExternalIntegrations => ExternalIntegrations.Count > 0;

    public bool HasConfiguration => Configuration.Count > 0;

    /// <summary>Filters the transitive list, which is the only one long enough to need it.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                ApplyFilter();
            }
        }
    }

    public ImpactRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value) && value?.SymbolId is { } id)
            {
                SymbolSelected?.Invoke(id);
            }
        }
    }

    public EndpointViewModel? SelectedEndpoint
    {
        get => _selectedEndpoint;
        set
        {
            if (SetProperty(ref _selectedEndpoint, value) && value is not null)
            {
                EndpointSelected?.Invoke(value.Endpoint);
            }
        }
    }

    /// <summary>Set by the shell: analysis only runs while the section is on screen.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value) && value)
            {
                _ = RunAsync();
            }
        }
    }

    public void SetDatabase(SymbolIndexDatabase? database)
    {
        _database = database;
        _pending = null;
        _report = null;
        Clear();
        Raise();
    }

    /// <summary>
    /// Points the section at a symbol. The walk itself waits until the section is looked
    /// at, the way the graph's does, so choosing a starting point costs nothing.
    /// </summary>
    public void Analyze(long symbolId)
    {
        _pending = symbolId;
        AnalyzeCommand.RaiseCanExecuteChanged();

        if (_isActive)
        {
            _ = RunAsync();
        }
    }

    private async Task RunAsync()
    {
        if (_database is not { } database || _pending is not { } rootId)
        {
            return;
        }

        if (_report?.Root.Id == rootId)
        {
            return;
        }

        _analysis?.Cancel();
        _analysis?.Dispose();
        _analysis = new CancellationTokenSource();
        var cancellationToken = _analysis.Token;

        IsLoading = true;

        try
        {
            var report = await Task.Run(
                () => ImpactAnalyzer.Analyze(database, rootId, options: null, cancellationToken),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            _report = report;
            Load(report);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request.
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Load(ImpactReport? report)
    {
        Clear();

        if (report is null)
        {
            Raise();
            return;
        }

        foreach (var (label, count) in report.Summary)
        {
            Summary.Add(new ImpactCountViewModel(label, count));
        }

        foreach (var signal in report.Risk.Signals)
        {
            RiskSignals.Add(new RiskSignalViewModel(signal));
        }

        foreach (var caller in report.DirectCallers)
        {
            DirectCallers.Add(new ImpactRowViewModel(caller));
        }

        _allIndirect.AddRange(report.IndirectCallers.Select(caller => new ImpactRowViewModel(caller)));

        foreach (var link in report.Implementations.Concat(report.DerivedTypes))
        {
            Implementations.Add(new ImpactRowViewModel(link));
        }

        foreach (var worker in report.Workers)
        {
            Workers.Add(new ImpactRowViewModel(worker));
        }

        foreach (var endpoint in report.Endpoints)
        {
            Endpoints.Add(new EndpointViewModel(endpoint));
        }

        foreach (var entity in report.Entities)
        {
            Entities.Add(new EntityRowViewModel(entity));
        }

        foreach (var dependency in report.ExternalIntegrations)
        {
            ExternalIntegrations.Add(new ExternalRowViewModel(dependency));
        }

        foreach (var usage in report.Configuration)
        {
            Configuration.Add(new ConfigurationRowViewModel(usage));
        }

        ApplyFilter();
        Raise();
    }

    private void ApplyFilter()
    {
        var filter = Filter.Trim();

        IndirectCallers.Clear();
        foreach (var row in _allIndirect.Where(row =>
                     filter.Length == 0 ||
                     row.Display.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                     row.Container.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            IndirectCallers.Add(row);
        }

        OnPropertyChanged(nameof(HasIndirectCallers));
    }

    private void Clear()
    {
        _allIndirect.Clear();
        Summary.Clear();
        RiskSignals.Clear();
        DirectCallers.Clear();
        IndirectCallers.Clear();
        Implementations.Clear();
        Workers.Clear();
        Endpoints.Clear();
        Entities.Clear();
        ExternalIntegrations.Clear();
        Configuration.Clear();
    }

    private void Raise()
    {
        foreach (var property in (string[])
                 [
                     nameof(HasReport), nameof(RootTitle), nameof(RootSubtitle), nameof(RiskLevelText),
                     nameof(IsHighRisk), nameof(IsModerateRisk), nameof(RiskRationale), nameof(ReachText),
                     nameof(IsTruncated), nameof(HasDirectCallers), nameof(HasIndirectCallers),
                     nameof(HasImplementations), nameof(HasWorkers), nameof(HasEndpoints),
                     nameof(HasEntities), nameof(HasExternalIntegrations), nameof(HasConfiguration),
                 ])
        {
            OnPropertyChanged(property);
        }
    }
}

/// <summary>One line of the summary: a label and the count behind it.</summary>
public sealed class ImpactCountViewModel(string label, int count)
{
    public string Label { get; } = label;

    public int Count { get; } = count;

    /// <summary>Nothing found is worth showing plainly rather than hiding the row.</summary>
    public bool IsEmpty => Count == 0;
}

/// <summary>
/// One risk property. Shown whether or not it is present, so the surface accounts for
/// what it ruled out as well as what it found.
/// </summary>
public sealed class RiskSignalViewModel(RiskSignal signal)
{
    public string Title { get; } = signal.Title;

    public string Evidence { get; } = signal.Evidence;

    public bool IsPresent { get; } = signal.IsPresent;

    public string Marker { get; } = signal.IsPresent ? "!" : "·";
}

/// <summary>
/// One impacted symbol. Distance is on the row because a transitive effect that is
/// twenty hops away should not read like one that is two.
/// </summary>
public sealed class ImpactRowViewModel
{
    public ImpactRowViewModel(ImpactedSymbol impacted)
    {
        ArgumentNullException.ThrowIfNull(impacted);

        SymbolId = impacted.Symbol.Id;
        Display = impacted.Symbol.Display;
        Container = impacted.Symbol.ContainerFullyQualifiedName
                    ?? impacted.Symbol.Namespace
                    ?? string.Empty;
        Glyph = SymbolGlyph.For(impacted.Symbol.Kind);
        IsContainer = SymbolGlyph.IsContainer(impacted.Symbol.Kind);
        Kind = SymbolGlyph.Keyword(impacted.Symbol.Kind);
        Distance = impacted.Distance;
        Origin = impacted.Symbol.FilePath is { } path
            ? $"{System.IO.Path.GetFileName(path)}:{impacted.Symbol.Line?.ToString() ?? "?"}"
            : string.Empty;
    }

    /// <summary>
    /// A relation the index already resolved, which is one hop away by definition.
    /// </summary>
    public ImpactRowViewModel(SymbolLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        SymbolId = link.SymbolId;
        Display = link.Display;
        Container = link.FullyQualifiedName;
        Glyph = "I";
        IsContainer = true;
        Kind = string.Empty;
        Distance = 1;
        Origin = string.Empty;
    }

    public long? SymbolId { get; }

    public string Display { get; }

    public string Container { get; }

    public string Glyph { get; }

    public bool IsContainer { get; }

    public string Kind { get; }

    public int Distance { get; }

    public string DistanceText => Distance == 1 ? "1 hop" : $"{Distance} hops";

    public string Origin { get; }

    public bool IsNavigable => SymbolId is not null;
}
