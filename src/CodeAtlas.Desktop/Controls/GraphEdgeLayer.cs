using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CodeAtlas.Core.Model;
using CodeAtlas.Desktop.ViewModels;

namespace CodeAtlas.Desktop.Controls;

/// <summary>
/// Draws the graph's relations underneath its nodes.
/// </summary>
/// <remarks>
/// Edges are painted rather than built as controls: there is one per relation and none of
/// them is interactive, so a hundred shapes in the visual tree would cost layout for
/// nothing. Kinds are told apart by line weight and dash, not colour, which keeps the
/// surface inside the application's neutral palette and readable to a reader who cannot
/// distinguish hues.
/// </remarks>
public sealed class GraphEdgeLayer : Control
{
    public static readonly StyledProperty<IEnumerable?> EdgesProperty =
        AvaloniaProperty.Register<GraphEdgeLayer, IEnumerable?>(nameof(Edges));

    /// <summary>Used for the quieter relations: references and type dependencies.</summary>
    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<GraphEdgeLayer, IBrush?>(nameof(LineBrush));

    /// <summary>Used for the structural relations: inheritance, implementation and calls.</summary>
    public static readonly StyledProperty<IBrush?> EmphasisBrushProperty =
        AvaloniaProperty.Register<GraphEdgeLayer, IBrush?>(nameof(EmphasisBrush));

    private const double ArrowLength = 9;
    private const double ArrowHalfWidth = 4;

    /// <summary>Gap between a node's edge and the arrowhead pointing at it.</summary>
    private const double NodeGap = 5;

    private INotifyCollectionChanged? _observed;

    static GraphEdgeLayer()
    {
        AffectsRender<GraphEdgeLayer>(EdgesProperty, LineBrushProperty, EmphasisBrushProperty);
    }

    public IEnumerable? Edges
    {
        get => GetValue(EdgesProperty);
        set => SetValue(EdgesProperty, value);
    }

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public IBrush? EmphasisBrush
    {
        get => GetValue(EmphasisBrushProperty);
        set => SetValue(EmphasisBrushProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == EdgesProperty)
        {
            if (_observed is not null)
            {
                _observed.CollectionChanged -= OnEdgesChanged;
            }

            _observed = change.NewValue as INotifyCollectionChanged;

            if (_observed is not null)
            {
                _observed.CollectionChanged += OnEdgesChanged;
            }
        }
    }

    private void OnEdgesChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        if (Edges is not IEnumerable edges)
        {
            return;
        }

        foreach (var item in edges)
        {
            if (item is not GraphEdgeViewModel edge)
            {
                continue;
            }

            var from = new Point(edge.Source.CentreX, edge.Source.CentreY);
            var to = new Point(edge.Target.CentreX, edge.Target.CentreY);

            var start = TrimToNode(from, to);
            var end = TrimToNode(to, from);

            var pen = PenFor(edge);
            context.DrawLine(pen, start, end);
            DrawArrow(context, pen.Brush, start, end);

            if (edge.IsReciprocal)
            {
                DrawArrow(context, pen.Brush, end, start);
            }
        }
    }

    /// <summary>
    /// Moves an endpoint from a node's centre out to its box, so a line meets the node it
    /// points at instead of disappearing under it.
    /// </summary>
    private static Point TrimToNode(Point centre, Point towards)
    {
        var dx = towards.X - centre.X;
        var dy = towards.Y - centre.Y;

        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001)
        {
            return centre;
        }

        var halfWidth = (GraphNodeViewModel.Width / 2) + NodeGap;
        var halfHeight = (GraphNodeViewModel.Height / 2) + NodeGap;

        var scale = Math.Min(
            Math.Abs(dx) < 0.001 ? double.MaxValue : halfWidth / Math.Abs(dx),
            Math.Abs(dy) < 0.001 ? double.MaxValue : halfHeight / Math.Abs(dy));

        // A scale past 1 would push the endpoint beyond the node it points at.
        scale = Math.Min(scale, 1);

        return new Point(centre.X + (dx * scale), centre.Y + (dy * scale));
    }

    private static void DrawArrow(DrawingContext context, IBrush? brush, Point from, Point to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));

        if (length < ArrowLength || brush is null)
        {
            return;
        }

        var ux = dx / length;
        var uy = dy / length;
        var baseX = to.X - (ux * ArrowLength);
        var baseY = to.Y - (uy * ArrowLength);

        var geometry = new StreamGeometry();
        using (var builder = geometry.Open())
        {
            builder.BeginFigure(to, isFilled: true);
            builder.LineTo(new Point(baseX - (uy * ArrowHalfWidth), baseY + (ux * ArrowHalfWidth)));
            builder.LineTo(new Point(baseX + (uy * ArrowHalfWidth), baseY - (ux * ArrowHalfWidth)));
            builder.EndFigure(isClosed: true);
        }

        context.DrawGeometry(brush, pen: null, geometry);
    }

    private Pen PenFor(GraphEdgeViewModel edge)
    {
        var (brush, thickness, dashes) = edge.Kind switch
        {
            RelationKind.Inherits => (EmphasisBrush, 1.6, new[] { 5d, 3d }),
            RelationKind.Implements or RelationKind.Overrides => (EmphasisBrush, 1.4, new[] { 2d, 3d }),
            RelationKind.Calls => (EmphasisBrush, 1.6, null),

            // Composition is the heaviest line on the surface: it is what the endpoint
            // flow is read along, and it crosses from an abstraction to a concrete type.
            RelationKind.Injects => (EmphasisBrush, 2.0, null),
            RelationKind.Resolves => (EmphasisBrush, 2.0, new[] { 6d, 3d }),

            // Infrastructure reads as heavily as composition, because it is the other half
            // of the same flow: a write to a resource is drawn solid, a read of one dashed.
            RelationKind.DeclaresEntity or RelationKind.ConfiguresEntity => (EmphasisBrush, 1.8, new[] { 4d, 2d }),
            RelationKind.CreatesEntity or RelationKind.ModifiesEntity or RelationKind.DeletesEntity
                or RelationKind.UsesExternal => (EmphasisBrush, 2.0, null),
            RelationKind.ReadsEntity or RelationKind.ReadsConfiguration => (EmphasisBrush, 1.6, new[] { 3d, 2d }),
            RelationKind.RelatesToEntity => (LineBrush, 1.6, new[] { 8d, 3d }),
            RelationKind.ParameterType or RelationKind.ReturnType => (LineBrush, 1.4, new[] { 1d, 3d }),
            _ => (LineBrush, 1.2, null),
        };

        // An inferred relation is drawn faintly: it is a recovered guess, and must not
        // read with the same weight as something the compiler resolved.
        if (edge.Provenance != RelationProvenance.Exact && brush is ISolidColorBrush solid)
        {
            brush = new ImmutableSolidColorBrush(solid.Color, 0.4);
        }

        return new Pen(
            brush,
            thickness,
            dashes is null ? null : new DashStyle(dashes, 0),
            lineCap: PenLineCap.Round);
    }
}
