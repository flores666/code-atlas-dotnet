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

    private readonly HashSet<long> _expanded = [];
    private readonly GraphCommands _commands;

    private SymbolIndexDatabase? _database;
    private IndexedSymbol? _root;
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

    public string RootTitle => _root?.Display ?? "No symbol selected";

    public string RootSubtitle => _root is { } root
        ? $"{SymbolGlyph.Keyword(root.Kind)} · {root.FullyQualifiedName}"
        : "Select a symbol to map its neighbourhood.";

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
    public void SetRoot(IndexedSymbol? root)
    {
        _root = root;
        _expanded.Clear();
        _isStale = true;

        OnPropertyChanged(nameof(HasRoot));
        OnPropertyChanged(nameof(RootTitle));
        OnPropertyChanged(nameof(RootSubtitle));

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
        };

        try
        {
            var graph = await Task.Run(() => NeighborhoodBuilder.Build(database, root.Id, options));

            // A newer request finished first; its result is the current one.
            if (generation == _generation)
            {
                Apply(graph);
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

        return kinds;

        void Add(bool enabled, RelationGroupKind group)
        {
            if (enabled)
            {
                kinds.UnionWith(RelationKinds.InGroup(group));
            }
        }
    }

    private void Apply(SymbolGraph? graph)
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
                _commands);

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

        foreach (var edge in edges.OrderBy(edge => Array.IndexOf(EdgePriority, edge.Kind)))
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

    private void OpenSource(GraphNodeViewModel? node)
    {
        if (node is not null)
        {
            OpenSourceRequested?.Invoke(node.Symbol);
        }
    }
}
