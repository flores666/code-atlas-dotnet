using System.Windows.Input;
using CodeAtlas.Core.Model;
using CodeAtlas.Desktop.Mvvm;

namespace CodeAtlas.Desktop.ViewModels;

/// <summary>
/// One symbol drawn on the graph surface. Position is assigned by
/// <see cref="GraphLayout"/> after the neighbourhood is known.
/// </summary>
public sealed class GraphNodeViewModel : ObservableObject
{
    /// <summary>Node box size, shared with the layout and the edge trimming so they agree.</summary>
    public const double Width = 200;

    public const double Height = 46;

    private bool _isSelected;
    private double _x;
    private double _y;

    public GraphNodeViewModel(
        GraphNode node,
        bool isRoot,
        bool isExpanded,
        GraphCommands commands,
        string? infrastructure = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(commands);

        Infrastructure = infrastructure ?? string.Empty;
        Symbol = node.Symbol;
        Depth = node.Depth;
        HiddenNeighbours = node.HiddenNeighbours;
        IsRoot = isRoot;
        IsExpanded = isExpanded;
        Commands = commands;

        Glyph = SymbolGlyph.For(node.Symbol.Kind);
        IsContainer = SymbolGlyph.IsContainer(node.Symbol.Kind);
        Kind = SymbolGlyph.Keyword(node.Symbol.Kind);
        Container = node.Symbol.ContainerFullyQualifiedName
                    ?? (string.IsNullOrEmpty(node.Symbol.Namespace) ? "" : node.Symbol.Namespace);
    }

    public IndexedSymbol Symbol { get; }

    /// <summary>
    /// The infrastructure this symbol stands on — the table an entity maps to, or the
    /// technology a boundary type talks to — shown as a badge so a flow can be read
    /// without opening every node.
    /// </summary>
    public string Infrastructure { get; }

    public bool HasInfrastructure => Infrastructure.Length > 0;

    public long Id => Symbol.Id;

    public string Display => Symbol.Display;

    public string Glyph { get; }

    public bool IsContainer { get; }

    public string Kind { get; }

    /// <summary>Declaring type or namespace, shown under the name to disambiguate.</summary>
    public string Container { get; }

    public int Depth { get; }

    public int HiddenNeighbours { get; }

    public bool IsRoot { get; }

    public bool IsExpanded { get; }

    /// <summary>Worth expanding only when it has neighbours the graph is not already showing.</summary>
    public bool CanExpand => HiddenNeighbours > 0 && !IsExpanded;

    public bool CanCollapse => IsExpanded;

    public GraphCommands Commands { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public double X
    {
        get => _x;
        set => SetProperty(ref _x, value);
    }

    public double Y
    {
        get => _y;
        set => SetProperty(ref _y, value);
    }

    public double CentreX => X + (Width / 2);

    public double CentreY => Y + (Height / 2);
}

/// <summary>
/// The actions a node offers. Held once and shared by every node, so the item template
/// binds to commands without reaching up into an ancestor's data context.
/// </summary>
public sealed record GraphCommands(
    ICommand Select,
    ICommand Expand,
    ICommand Collapse,
    ICommand Focus,
    ICommand OpenSource);

/// <summary>
/// A line on the graph surface. Parallel relations between the same two symbols collapse
/// into one line: the reader needs to see that two symbols are related and how, not one
/// line per recorded edge.
/// </summary>
public sealed class GraphEdgeViewModel
{
    public required RelationKind Kind { get; init; }

    /// <summary>Exact as soon as any of the merged relations was exactly bound.</summary>
    public required RelationProvenance Provenance { get; set; }

    public required GraphNodeViewModel Source { get; init; }

    public required GraphNodeViewModel Target { get; init; }

    /// <summary>True when the relation also runs the other way, which is drawn as a second arrowhead.</summary>
    public bool IsReciprocal { get; set; }
}
