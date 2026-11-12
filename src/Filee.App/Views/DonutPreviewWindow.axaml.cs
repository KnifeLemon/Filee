// The donut on screen of Settings → Donut toolbar (see DonutPreviewWindow.axaml). ToolbarPage shows, fills and places
// it (DonutPreviewPlacement); this keeps its anchored edge where it was put while its size changes, flips it as the
// live donut flips, and moves it where its middle is dragged.

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

    // Where it was put (its anchored edge stays there while the sliders change its size), and whether a move is ours.
    private PreviewPlace? _place;
    private bool _placing;

    public DonutPreviewWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        // Halfway through a flip (Tab or the wheel) the donut shows the other face of the profile.
        Donut.FaceSwapping += (_, _) => (DataContext as RadialViewModel)?.ToggleFace();
        SizeChanged += (_, _) => KeepPlace();
        // The middle is the handle: pressed there, the window moves with the pointer (before the donut takes the
        // press).
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (_, e) => Cursor = OnHandle(e) ? MoveCursor : null, RoutingStrategies.Tunnel, handledEventsToo: true);
        PositionChanged += (_, _) => OnMoved();
    }

    public DonutMenu DonutControl => Donut;

    /// <summary>The user moved the donut (dragged its middle): its new centre, in screen pixels.</summary>
    public event Action<PixelPoint>? Moved;

    /// <summary>Escape was pressed on it (with no ring of its own to fold): the page puts it away.</summary>
    public event Action? EscapePressed;

    /// <summary>Where it was put, or last moved to (null before it is first shown).</summary>
    internal PreviewPlace? Place => _place;

    /// <summary>The square it takes on screen, in pixels.</summary>
    internal PixelRect Box => new(Position, PixelSize.FromSize(Bounds.Size, Scaling));

    private bool OnHandle(PointerEventArgs e) => Donut.HitTest(e.GetPosition(Donut)).Kind == DonutHitKind.Center;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !OnHandle(e))
            return;
        e.Handled = true;
        BeginMoveDrag(e);
    }

    // macOS reports window positions in points, the others in device pixels.
    private double Scaling => OperatingSystem.IsMacOS() ? 1 : RenderScaling;

    private int Side => (int)Math.Ceiling(Math.Max(Bounds.Width, Bounds.Height) * Scaling);

    // A move this window didn't make is the user's: dragged by its middle. It stays centred there from then on.
    private void OnMoved()
    {
        if (_placing || !IsVisible || _place is null)
            return;
        var box = Box;
        _place = new PreviewPlace(box.Center, PreviewAnchor.Centre);
        Moved?.Invoke(box.Center);
    }

    /// <summary>
    /// Shows the donut at <paramref name="place"/>, or moves it there, above <paramref name="owner"/> when it can only
    /// go over that window (<see cref="PreviewPlace.Over"/>). <paramref name="side"/>: its size in pixels, until it is
    /// laid out, so it appears where it belongs rather than jumping there.
    /// </summary>
    internal void ShowAt(PreviewPlace place, int side, Window? owner)
    {
        _place = place;
        var wantOwner = place.Over ? owner : null;
        if (IsVisible && !ReferenceEquals(Owner, wantOwner))
            Hide(); // the owner is set by Show only
        Move(place.TopLeftFor(IsVisible ? Side : side));
        if (!IsVisible)
        {
            if (wantOwner is not null)
                Show(wantOwner);
            else
                Show();
        }
        KeepPlace(); // again, in case showing changed the scale
    }

    private void KeepPlace()
    {
        if (_place is { } place && IsVisible)
            Move(place.TopLeftFor(Side));
    }

    private void Move(PixelPoint to)
    {
        _placing = true;
        try
        {
            Position = to;
        }
        finally
        {
            _placing = false;
        }
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
