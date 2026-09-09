using System.Collections.ObjectModel;
using CodeAtlas.Core.Graph;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using CodeAtlas.Desktop.Mvvm;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>
/// The semantic map of one symbol's surroundings.
/// </summary>
/// <remarks>
/// <para>
/// The graph is only ever built for the symbol the user selected, at the depth they
/// asked for. It is also built lazily: while the graph section is not on screen a new
/// selection only marks the view stale, so browsing the tree or search results costs
/// nothing extra.
/// </para>
/// <para>
/// Expansion is held here as a set of symbol ids and passed into each query rather than
/// mutating a graph in place, so expanding and collapsing are the same operation run with
/// a different set and always agree with what a fresh query would produce.
/// </para>
/// </remarks>
public sealed class GraphViewModel : ObservableObject
{
    /// <summary>Display order when several relations between the same pair collapse into one line.</summary>
    private static readonly RelationKind[] EdgePriority =
    [
        RelationKind.Inherits,
        RelationKind.Implements,
        RelationKind.Overrides,
        RelationKind.Calls,
        RelationKind.ParameterType,
        RelationKind.ReturnType,
        RelationKind.References,
    ];

    /// <summary>
    /// Wiring is drawn ahead of a call: when a controller both injects a service and calls
    /// into it, or a repository both reads an entity and returns it, the wiring — or the
    /// resource — is the more informative of the two. Ordered within itself so a write to
    /// an entity outranks a read of it.
    /// </summary>
    private static readonly RelationKind[] StructuralFirst =
    [
        RelationKind.Resolves,
        RelationKind.Injects,
        RelationKind.DeclaresEntity,
        RelationKind.ConfiguresEntity,
        RelationKind.CreatesEntity,
        RelationKind.ModifiesEntity,
        RelationKind.DeletesEntity,
        RelationKind.ReadsEntity,
        RelationKind.RelatesToEntity,
        RelationKind.ReadsConfiguration,
        RelationKind.UsesExternal,
    ];

    /// <summary>
    /// Shown in place of the graph when an endpoint has no handler to walk from, so the
    /// section says why it is empty rather than looking like nothing happened.
    /// </summary>
    private const string NoHandlerNotice =
        "The endpoint was detected, but nothing it reaches is indexed in this workspace. " +
        "An inline handler that only calls framework code, or one whose services live " +
        "outside the solution, has no flow of its own to map.";

    private readonly HashSet<long> _expanded = [];
    private readonly GraphCommands _commands;

    private SymbolIndexDatabase? _database;
    private IndexedSymbol? _root;
    private IReadOnlyList<long> _seeds = [];
    private string? _rootCaption;
    private string? _endpointNotice;
    private GraphNodeViewModel? _selectedNode;
    private int _generation;

    private bool _isActive;
    private bool _isStale = true;
    private bool _isLoading;
    private bool _isTruncated;
    private int _depth = 1;
    private double _canvasWidth;
    private double _canvasHeight;
    private double _focusX;
    private double _focusY;
    private int _viewResetToken;
    private bool _showCalls = true;
    private bool _showReferences = true;
    private bool _showInheritance = true;
    private bool _showImplementations = true;
    private bool _showTypeDependencies = true;
    private bool _showComposition = true;
    private bool _showDatabase = true;
    private bool _showConfiguration = true;
    private bool _showExternalServices = true;
    private bool _showChangedOnly;
    private IReadOnlySet<long> _changed = new HashSet<long>();

    public GraphViewModel()
    {
        _commands = new GraphCommands(
            new RelayCommand(parameter => Select(parameter as GraphNodeViewModel)),
            new RelayCommand(parameter => SetExpanded(parameter as GraphNodeViewModel, expanded: true)),
            new RelayCommand(parameter => SetExpanded(parameter as GraphNodeViewModel, expanded: false)),
            new RelayCommand(parameter => Focus(parameter as GraphNodeViewModel)),
            new RelayCommand(parameter => OpenSource(parameter as GraphNodeViewModel)));

        SetDepthCommand = new RelayCommand(parameter => Depth = Convert.ToInt32(parameter, provider: null));
    }

    /// <summary>Raised when a node is picked, so the details panel can follow the graph.</summary>
    public event Action<long>? NodeSelected;

    public event Action<IndexedSymbol>? OpenSourceRequested;

    public ObservableCollection<GraphNodeViewModel> Nodes { get; } = [];

    public ObservableCollection<GraphEdgeViewModel> Edges { get; } = [];

    public RelayCommand SetDepthCommand { get; }

    public bool HasRoot => _root is not null;

    public string RootTitle => _rootCaption ?? _root?.Display ?? "No symbol selected";

    public string RootSubtitle => (_root, _endpointNotice) switch
    {
        ({ } root, _) => $"{SymbolGlyph.Keyword(root.Kind)} · {root.FullyQualifiedName}",
        (_, not null) => "No navigable handler",
        _ => "Select a symbol to map its neighbourhood.",
    };

    /// <summary>
    /// Why the endpoint the reader picked has no flow, or <c>null</c> when nothing is
    /// wrong. Shown where the graph would be, because a status-bar line is missable.
    /// </summary>
    public string? EndpointNotice => _endpointNotice;

    public bool HasEndpointNotice => _endpointNotice is not null;

    public bool HasGraph => Nodes.Count > 0;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>True when a cap stopped the walk, so the neighbourhood shown is partial.</summary>
    public bool IsTruncated
    {
        get => _isTruncated;
        private set => SetProperty(ref _isTruncated, value);
    }

    public string SummaryText => Nodes.Count == 0
        ? string.Empty
        : $"{Nodes.Count} symbols · {Edges.Count} relations";

    public double CanvasWidth
    {
        get => _canvasWidth;
        private set => SetProperty(ref _canvasWidth, value);
    }

    public double CanvasHeight
    {
        get => _canvasHeight;
        private set => SetProperty(ref _canvasHeight, value);
    }

    /// <summary>Where the surface centres: the root node, so the subject is always on screen.</summary>
    public double FocusX
    {
        get => _focusX;
        private set => SetProperty(ref _focusX, value);
    }

    public double FocusY
    {
        get => _focusY;
        private set => SetProperty(ref _focusY, value);
    }

    /// <summary>Bumped whenever the graph is rebuilt, which the surface reads as "recentre".</summary>
    public int ViewResetToken
    {
        get => _viewResetToken;
        private set => SetProperty(ref _viewResetToken, value);
    }

    public int Depth
    {
        get => _depth;
        set
        {
            var clamped = Math.Clamp(value, 1, GraphOptions.MaxDepth);
            if (SetProperty(ref _depth, clamped))
            {
                OnPropertyChanged(nameof(IsDepth1));
                OnPropertyChanged(nameof(IsDepth2));
                OnPropertyChanged(nameof(IsDepth3));
                Invalidate();
            }
        }
    }

    public bool IsDepth1 => Depth == 1;

    public bool IsDepth2 => Depth == 2;

    public bool IsDepth3 => Depth == 3;

    public bool ShowCalls
    {
        get => _showCalls;
        set => SetFilter(ref _showCalls, value);
    }

    public bool ShowReferences
    {
        get => _showReferences;
        set => SetFilter(ref _showReferences, value);
    }

    public bool ShowInheritance
    {
        get => _showInheritance;
        set => SetFilter(ref _showInheritance, value);
    }

    public bool ShowImplementations
    {
        get => _showImplementations;
        set => SetFilter(ref _showImplementations, value);
    }

    public bool ShowTypeDependencies
    {
        get => _showTypeDependencies;
        set => SetFilter(ref _showTypeDependencies, value);
    }

    /// <summary>Injection and container resolution: how the application is wired together.</summary>
    public bool ShowComposition
    {
        get => _showComposition;
        set => SetFilter(ref _showComposition, value);
    }

    /// <summary>Entities, the contexts that declare them, and what reads and writes them.</summary>
    public bool ShowDatabase
    {
        get => _showDatabase;
        set => SetFilter(ref _showDatabase, value);
    }

    /// <summary>What binds and reads configuration.</summary>
    public bool ShowConfiguration
    {
        get => _showConfiguration;
        set => SetFilter(ref _showConfiguration, value);
    }

    /// <summary>Where the application crosses out to infrastructure it does not own.</summary>
    public bool ShowExternalServices
    {
        get => _showExternalServices;
        set => SetFilter(ref _showExternalServices, value);
    }

    /// <summary>
    /// Narrows the graph to the symbols the working tree changed.
    /// </summary>
    /// <remarks>
    /// Unlike every other chip this one filters nodes rather than edge kinds, because
    /// "changed code" is a statement about which symbols matter rather than about which
    /// relations to follow. It is only offered while there is a changed set to filter by,
    /// so it cannot silently empty the graph in a clean working tree.
    /// </remarks>
    public bool ShowChangedOnly
    {
        get => _showChangedOnly;
        set => SetFilter(ref _showChangedOnly, value);
    }

    /// <summary>False in a clean working tree, which is when the chip has nothing to offer.</summary>
    public bool CanFilterChanged => _changed.Count > 0;

    /// <summary>
    /// Hands the graph the symbols Git reports as changed.
    /// </summary>
    /// <remarks>
    /// A set the shell pushes rather than a query the graph makes: the changed set is Git
    /// state, it is already computed for the Git Changes section, and the graph has no
    /// business reading a repository.
    /// </remarks>
    public void SetChangedSymbols(IReadOnlySet<long> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);

        _changed = changed;
        OnPropertyChanged(nameof(CanFilterChanged));

        // A filter that can no longer be satisfied is dropped rather than left on, so the
        // graph never comes back empty for a reason the toolbar has stopped showing.
        if (_showChangedOnly && changed.Count == 0)
        {
            _showChangedOnly = false;
            OnPropertyChanged(nameof(ShowChangedOnly));
        }

        if (_showChangedOnly)
        {
            Invalidate();
        }
    }

    /// <summary>Set by the shell: the graph only queries while it is the visible section.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value) && value)
            {
                Refresh();
            }
        }
    }

    public void SetDatabase(SymbolIndexDatabase? database)
    {
        _database = database;
        SetRoot(null);
    }

    /// <summary>Points the graph at a symbol, discarding any expansion of the previous one.</summary>
    /// <param name="seeds">
    /// Extra symbols to walk from alongside the root. An endpoint supplies its controller,
    /// which is what holds the injected dependencies the action itself does not name.
    /// </param>
    /// <param name="caption">Overrides the heading, so an endpoint's graph is titled by its route.</param>
    public void SetRoot(IndexedSymbol? root, IReadOnlyList<long>? seeds = null, string? caption = null)
    {
        _root = root;
        _seeds = seeds ?? [];
        _rootCaption = caption;
        _endpointNotice = null;
        _expanded.Clear();
        _isStale = true;

        RaiseHeading();

        if (root is null)
        {
            Apply(null);
        }
        else
        {
            Refresh();
        }
    }

    private void SetFilter(ref bool field, bool value)
    {
        if (SetProperty(ref field, value))
        {
            Invalidate();
        }
    }

    private void Invalidate()
    {
        _isStale = true;
        Refresh();
    }

    private void Refresh()
    {
        if (_isStale && _isActive && _root is not null && _database is not null)
        {
            _ = BuildAsync();
        }
    }

    private async Task BuildAsync()
    {
        if (_database is not { } database || _root is not { } root)
        {
            return;
        }

        _isStale = false;
        var generation = ++_generation;
        IsLoading = true;

        var options = new GraphOptions
        {
            Depth = Depth,
            Kinds = SelectedKinds(),
            Expanded = _expanded.ToList(),
            Seeds = _seeds,
            RestrictTo = _showChangedOnly && _changed.Count > 0 ? _changed : null,
        };

        try
        {
            var (graph, labels) = await Task.Run(() =>
            {
                var built = NeighborhoodBuilder.Build(database, root.Id, options);
                return (built, database.GetInfrastructureLabels(
                    built.Nodes.Select(node => node.Symbol.Id).ToList()));
            });

            // A newer request finished first; its result is the current one.
            if (generation == _generation)
            {
                Apply(graph, labels);
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
            }
        }
    }

    private HashSet<RelationKind> SelectedKinds()
    {
        var kinds = new HashSet<RelationKind>();

        Add(_showCalls, RelationGroupKind.Calls);
        Add(_showReferences, RelationGroupKind.References);
        Add(_showInheritance, RelationGroupKind.Inheritance);
        Add(_showImplementations, RelationGroupKind.Implementations);
        Add(_showTypeDependencies, RelationGroupKind.TypeDependencies);
        Add(_showComposition, RelationGroupKind.Composition);
        Add(_showDatabase, RelationGroupKind.Database);
        Add(_showConfiguration, RelationGroupKind.Configuration);
        Add(_showExternalServices, RelationGroupKind.ExternalServices);

        return kinds;

        void Add(bool enabled, RelationGroupKind group)
        {
            if (enabled)
            {
                kinds.UnionWith(RelationKinds.InGroup(group));
            }
        }
    }

    private void Apply(SymbolGraph? graph, IReadOnlyDictionary<long, string>? labels = null)
    {
        Nodes.Clear();
        Edges.Clear();

        if (graph is null || graph.Nodes.Count == 0)
        {
            IsTruncated = false;
            CanvasWidth = 0;
            CanvasHeight = 0;
            ViewResetToken++;
            Raise();
            return;
        }

        var byId = new Dictionary<long, GraphNodeViewModel>();
        foreach (var node in graph.Nodes)
        {
            var viewModel = new GraphNodeViewModel(
                node,
                isRoot: node.Symbol.Id == graph.RootId,
                isExpanded: _expanded.Contains(node.Symbol.Id),
                _commands,
                labels is not null && labels.TryGetValue(node.Symbol.Id, out var label) ? label : null);

            byId[node.Symbol.Id] = viewModel;
            Nodes.Add(viewModel);
        }

        // Laid out before the edges are published, so whatever draws them sees final
        // positions the first time rather than a frame of lines at the origin.
        var edges = Merge(graph.Edges, byId).ToList();
        var (width, height) = GraphLayout.Arrange(Nodes, edges);

        foreach (var edge in edges)
        {
            Edges.Add(edge);
        }

        CanvasWidth = width;
        CanvasHeight = height;
        IsTruncated = graph.Truncated;

        if (byId.TryGetValue(graph.RootId, out var centre))
        {
            FocusX = centre.CentreX;
            FocusY = centre.CentreY;
        }

        // Signalled once the canvas has its final size, so the surface fits a graph that
        // exists rather than one that is still being queried.
        ViewResetToken++;

        if (byId.TryGetValue(graph.RootId, out var root))
        {
            SelectWithoutNotifying(root);
        }

        Raise();
    }

    /// <summary>
    /// Collapses every relation between one pair of symbols into a single line, labelled
    /// with the most structural of them. Two nodes joined five ways are one connection to
    /// a reader, not five overlapping lines.
    /// </summary>
    private static IEnumerable<GraphEdgeViewModel> Merge(
        IReadOnlyList<RelationEdge> edges,
        Dictionary<long, GraphNodeViewModel> nodes)
    {
        var merged = new Dictionary<(long Low, long High), GraphEdgeViewModel>();

        foreach (var edge in edges
                     .OrderBy(edge => Array.IndexOf(StructuralFirst, edge.Kind) >= 0 ? 0 : 1)
                     .ThenBy(edge => Array.IndexOf(StructuralFirst, edge.Kind))
                     .ThenBy(edge => Array.IndexOf(EdgePriority, edge.Kind)))
        {
            if (edge.SourceId == edge.TargetId ||
                !nodes.TryGetValue(edge.SourceId, out var source) ||
                !nodes.TryGetValue(edge.TargetId, out var target))
            {
                continue;
            }

            var key = (Math.Min(edge.SourceId, edge.TargetId), Math.Max(edge.SourceId, edge.TargetId));

            if (merged.TryGetValue(key, out var existing))
            {
                // The first edge wins the line's kind because the list is ordered by
                // priority; a later one only adds direction and exactness.
                existing.IsReciprocal |= existing.Source.Id != source.Id;

                if (edge.Provenance == RelationProvenance.Exact)
                {
                    existing.Provenance = RelationProvenance.Exact;
                }

                continue;
            }

            merged[key] = new GraphEdgeViewModel
            {
                Kind = edge.Kind,
                Provenance = edge.Provenance,
                Source = source,
                Target = target,
            };
        }

        return merged.Values;
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(HasGraph));
        OnPropertyChanged(nameof(SummaryText));
    }

    private void Select(GraphNodeViewModel? node)
    {
        if (node is null)
        {
            return;
        }

        SelectWithoutNotifying(node);
        NodeSelected?.Invoke(node.Id);
    }

    private void SelectWithoutNotifying(GraphNodeViewModel node)
    {
        if (_selectedNode is { } previous)
        {
            previous.IsSelected = false;
        }

        _selectedNode = node;
        node.IsSelected = true;
    }

    private void SetExpanded(GraphNodeViewModel? node, bool expanded)
    {
        if (node is null)
        {
            return;
        }

        if (expanded ? _expanded.Add(node.Id) : _expanded.Remove(node.Id))
        {
            Invalidate();
        }
    }

    private void Focus(GraphNodeViewModel? node)
    {
        if (node is not null)
        {
            SetRoot(node.Symbol);
            NodeSelected?.Invoke(node.Id);
        }
    }

    /// <summary>
    /// Frames the graph on one endpoint's flow: the action and the type that holds its
    /// dependencies, deep enough to reach the implementations the container resolves.
    /// </summary>
    /// <param name="root">
    /// Where the walk starts: the indexed action for a controller endpoint, or — for an
    /// inline Minimal API handler, which declares nothing — the first thing the lambda
    /// reaches. Null only when the endpoint resolved to no indexed symbol at all, and the
    /// section then says why rather than looking like a click that did not register.
    /// </param>
    /// <param name="seeds">Walked from alongside the root; see <see cref="GraphOptions.Seeds"/>.</param>
    public void FocusEndpoint(HttpEndpoint endpoint, IndexedSymbol? root, IReadOnlyList<long>? seeds = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var caption = $"{endpoint.HttpMethod} {endpoint.Route}";

        if (root is null)
        {
            SetRoot(null);
            _rootCaption = caption;
            _endpointNotice = NoHandlerNotice;
            RaiseHeading();
            return;
        }

        Focus(
            root,
            [.. (seeds ?? []).Where(id => id != root.Id)],
            caption,
            GraphOptions.EndpointFlowDepth,
            GraphOptions.FlowGroups);
    }

    /// <summary>
    /// Frames the graph on one resource — an entity, an options type, a boundary — showing
    /// what reaches it rather than what it is made of.
    /// </summary>
    public void FocusResource(IndexedSymbol resource, string caption) =>
        Focus(resource, [], caption, GraphOptions.ResourceFlowDepth, GraphOptions.FlowGroups);

    private void RaiseHeading()
    {
        OnPropertyChanged(nameof(HasRoot));
        OnPropertyChanged(nameof(RootTitle));
        OnPropertyChanged(nameof(RootSubtitle));
        OnPropertyChanged(nameof(EndpointNotice));
        OnPropertyChanged(nameof(HasEndpointNotice));
    }

    /// <summary>
    /// Reframes the graph on one question, narrowed to the edges that answer it.
    /// </summary>
    /// <remarks>
    /// The filters are set rather than left alone because a flow asks a specific question,
    /// and references and signature types answer a different one. Every chip stays visible
    /// and one click puts them back.
    /// </remarks>
    private void Focus(
        IndexedSymbol root,
        IReadOnlyList<long> seeds,
        string caption,
        int depth,
        IReadOnlyList<RelationGroupKind> groups)
    {
        ArgumentNullException.ThrowIfNull(root);

        // Applied before the root, so the whole reframing costs one query rather than one
        // per property.
        _depth = depth;
        _showCalls = groups.Contains(RelationGroupKind.Calls);
        _showReferences = groups.Contains(RelationGroupKind.References);
        _showInheritance = groups.Contains(RelationGroupKind.Inheritance);
        _showImplementations = groups.Contains(RelationGroupKind.Implementations);
        _showTypeDependencies = groups.Contains(RelationGroupKind.TypeDependencies);
        _showComposition = groups.Contains(RelationGroupKind.Composition);
        _showDatabase = groups.Contains(RelationGroupKind.Database);
        _showConfiguration = groups.Contains(RelationGroupKind.Configuration);
        _showExternalServices = groups.Contains(RelationGroupKind.ExternalServices);

        // A flow is a question about how something is wired, not about what changed, and a
        // restriction left on from an earlier click would gut the answer.
        _showChangedOnly = false;

        foreach (var property in (string[])
                 [
                     nameof(Depth), nameof(IsDepth1), nameof(IsDepth2), nameof(IsDepth3),
                     nameof(ShowCalls), nameof(ShowComposition), nameof(ShowReferences),
                     nameof(ShowTypeDependencies), nameof(ShowInheritance), nameof(ShowImplementations),
                     nameof(ShowDatabase), nameof(ShowConfiguration), nameof(ShowExternalServices),
                     nameof(ShowChangedOnly),
                 ])
        {
            OnPropertyChanged(property);
        }

        SetRoot(root, seeds, caption);
    }

    private void OpenSource(GraphNodeViewModel? node)
    {
        if (node is not null)
        {
            OpenSourceRequested?.Invoke(node.Symbol);
        }
    }
}
