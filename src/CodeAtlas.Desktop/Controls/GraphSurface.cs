using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace CodeAtlas.Desktop.Controls;

/// <summary>
/// The viewport the graph is drawn in: it pans with a drag, zooms at the pointer, and
/// recentres whenever the graph is re-rooted.
/// </summary>
/// <remarks>
/// Zoom and pan are held on the control rather than in a view model. They describe where
/// the reader is looking, not what the application knows, and nothing outside this control
/// needs to ask.
/// </remarks>
public sealed class GraphSurface : Border
{
    /// <summary>Any change to this value recentres the view; the graph bumps it when rebuilt.</summary>
    public static readonly StyledProperty<int> ResetTokenProperty =
        AvaloniaProperty.Register<GraphSurface, int>(nameof(ResetToken));

    /// <summary>
    /// The size of the content, supplied by the graph rather than measured. A child larger
    /// than the viewport is arranged clipped, so its bounds cannot be trusted to say how
    /// much there is to fit.
    /// </summary>
    public static readonly StyledProperty<double> ContentWidthProperty =
        AvaloniaProperty.Register<GraphSurface, double>(nameof(ContentWidth));

    public static readonly StyledProperty<double> ContentHeightProperty =
        AvaloniaProperty.Register<GraphSurface, double>(nameof(ContentHeight));

    /// <summary>
    /// The point the view centres on, in content coordinates. It is the graph's root, so
    /// that a neighbourhood too large to fit still opens on its subject.
    /// </summary>
    public static readonly StyledProperty<double> FocusXProperty =
        AvaloniaProperty.Register<GraphSurface, double>(nameof(FocusX));

    public static readonly StyledProperty<double> FocusYProperty =
        AvaloniaProperty.Register<GraphSurface, double>(nameof(FocusY));

    private const double MinScale = 0.2;

    /// <summary>
    /// Fitting stops here. Past it labels stop being readable, and a picture of unreadable
    /// boxes is worse than one the reader has to pan.
    /// </summary>
    private const double MinFitScale = 0.5;
    private const double MaxScale = 3.0;
    private const double ZoomStep = 1.2;

    private readonly ScaleTransform _scale = new();
    private readonly TranslateTransform _translate = new();

    private Point _dragOrigin;
    private bool _dragging;

    public GraphSurface()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public int ResetToken
    {
        get => GetValue(ResetTokenProperty);
        set => SetValue(ResetTokenProperty, value);
    }

    public double ContentWidth
    {
        get => GetValue(ContentWidthProperty);
        set => SetValue(ContentWidthProperty, value);
    }

    public double ContentHeight
    {
        get => GetValue(ContentHeightProperty);
        set => SetValue(ContentHeightProperty, value);
    }

    public double FocusX
    {
        get => GetValue(FocusXProperty);
        set => SetValue(FocusXProperty, value);
    }

    public double FocusY
    {
        get => GetValue(FocusYProperty);
        set => SetValue(FocusYProperty, value);
    }

    public void ZoomIn() => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), ZoomStep);

    public void ZoomOut() => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), 1 / ZoomStep);

    /// <summary>Fits the graph in the viewport and centres it, never magnifying past 1:1.</summary>
    public void ResetView()
    {
        if (ContentWidth <= 0 || ContentHeight <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        // Shrink to fit, but never magnify and never past legibility: a two-node graph
        // blown up to fill the panel, or a large one crushed to unreadable boxes, are both
        // worse pictures than the reader panning.
        var scale = Math.Clamp(
            Math.Min(Bounds.Width / ContentWidth, Bounds.Height / ContentHeight),
            MinFitScale,
            1);

        var focusX = FocusX > 0 ? FocusX : ContentWidth / 2;
        var focusY = FocusY > 0 ? FocusY : ContentHeight / 2;

        _scale.ScaleX = _scale.ScaleY = scale;
        _translate.X = (Bounds.Width / 2) - (focusX * scale);
        _translate.Y = (Bounds.Height / 2) - (focusY * scale);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ChildProperty && Child is { } child)
        {
            child.RenderTransformOrigin = RelativePoint.TopLeft;
            child.RenderTransform = new TransformGroup { Children = { _scale, _translate } };
        }
        else if (change.Property == ResetTokenProperty)
        {
            // The new graph has not been measured yet, so recentring waits for layout.
            Dispatcher.UIThread.Post(ResetView, DispatcherPriority.Loaded);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        ZoomAt(e.GetPosition(this), e.Delta.Y > 0 ? ZoomStep : 1 / ZoomStep);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // Only a drag on empty surface pans; a press that reached a node is that node's.
        if (e.Source is not Visual source || ReferenceEquals(source, this) || ReferenceEquals(source, Child))
        {
            _dragging = true;
            _dragOrigin = e.GetPosition(this);
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_dragging)
        {
            return;
        }

        var position = e.GetPosition(this);
        _translate.X += position.X - _dragOrigin.X;
        _translate.Y += position.Y - _dragOrigin.Y;
        _dragOrigin = position;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        _dragging = false;
        e.Pointer.Capture(null);
    }

    /// <summary>Scales about <paramref name="anchor"/>, so the point under the pointer stays put.</summary>
    private void ZoomAt(Point anchor, double factor)
    {
        var current = _scale.ScaleX <= 0 ? 1 : _scale.ScaleX;
        var next = Math.Clamp(current * factor, MinScale, MaxScale);

        if (Math.Abs(next - current) < 0.0001)
        {
            return;
        }

        _translate.X = anchor.X - ((anchor.X - _translate.X) / current * next);
        _translate.Y = anchor.Y - ((anchor.Y - _translate.Y) / current * next);
        _scale.ScaleX = _scale.ScaleY = next;
    }
}
