// The donut on screen of Settings → Donut toolbar (see DonutPreviewWindow.axaml). ToolbarPage shows, fills and places
// it; this keeps it where it was put while its size changes, flips it as the live donut flips, and moves it where its
// middle is dragged.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

public partial class DonutPreviewWindow : Window
{
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);

    // Where the donut's centre stays (in screen pixels) while the sliders change its size.
    private PixelPoint? _center;

    // Set from a press on the middle until the page places the donut again: only then is a move the user's.
    private bool _dragging;

    public DonutPreviewWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        // Halfway through a flip (Tab or the wheel) the donut shows the other face of the profile.
        Donut.FaceSwapping += (_, _) => (DataContext as RadialViewModel)?.ToggleFace();
        SizeChanged += (_, _) => KeepCentered();
        // The middle is the handle: pressed there, the window moves with the pointer (before the donut takes the press).
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (_, e) => Cursor = OnHandle(e) ? MoveCursor : null, RoutingStrategies.Tunnel, handledEventsToo: true);
        PositionChanged += (_, _) => OnMoved();
    }

    public DonutMenu DonutControl => Donut;

    /// <summary>The user dragged the donut somewhere else: its new centre, in screen pixels.</summary>
    public event Action<PixelPoint>? Moved;

    /// <summary>Escape was pressed on it (with no ring of its own to fold): the page puts it away.</summary>
    public event Action? EscapePressed;

    /// <summary>Where the donut's centre is kept, in screen pixels (null before it is first shown).</summary>
    public PixelPoint? Center => _center;

    private bool OnHandle(PointerEventArgs e) => Donut.HitTest(e.GetPosition(Donut)).Kind == DonutHitKind.Center;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !OnHandle(e))
            return;
        e.Handled = true;
        _dragging = true;
        BeginMoveDrag(e);
    }

    // macOS reports window positions in points, the others in device pixels.
    private double Scaling => OperatingSystem.IsMacOS() ? 1 : RenderScaling;

    private void OnMoved()
    {
        if (!_dragging || !IsVisible)
            return;
        _center = new PixelPoint(Position.X + (int)Math.Round(Bounds.Width * Scaling / 2),
            Position.Y + (int)Math.Round(Bounds.Height * Scaling / 2));
        Moved?.Invoke(_center.Value);
    }

    /// <summary>Shows the donut centred on <paramref name="center"/> (screen pixels), or moves it there.</summary>
    public void ShowAt(PixelPoint center)
    {
        _center = center;
        _dragging = false;
        if (!IsVisible)
            Show();
        KeepCentered();
    }

    private void KeepCentered()
    {
        if (_center is not { } center)
            return;
        Position = new PixelPoint(center.X - (int)Math.Round(Bounds.Width * Scaling / 2),
            center.Y - (int)Math.Round(Bounds.Height * Scaling / 2));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Donut.HandleKey(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            EscapePressed?.Invoke();
            e.Handled = true;
        }
    }
}
