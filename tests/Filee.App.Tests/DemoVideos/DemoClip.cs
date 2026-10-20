// What every demo clip shares: a timeline rendered one frame at a time, and Filee's real donut window drawn into it.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.ViewModels;
using Filee.App.Views;

namespace Filee.App.Tests.DemoVideos;

/// <summary>
/// A demo clip: <see cref="Render"/> is called for every frame in order, 1/<see cref="DemoEncoder.Fps"/> s apart, from
/// 0 up to (not including) <see cref="Duration"/>, and draws the whole frame in layout units (scaled to pixels here).
/// The last frame leads back into the first, so the clip loops without a seam.
/// </summary>
internal abstract class DemoClip(DemoFormat format, DemoPainter painter) : IDisposable
{
    protected DemoFormat Format { get; } = format;
    protected DemoPainter Painter { get; } = painter;
    protected Cues Cues { get; } = new();

    public abstract double Duration { get; }

    private RenderTargetBitmap? _backdrop;

    public void Render(DrawingContext dc, double t)
    {
        Cues.Advance(t);
        Step(t);
        // Scaled by a transform: drawing text into a bitmap with a DPI above 96 placed everything after it twice as far.
        using (dc.PushTransform(Matrix.CreateScale(Format.Scale, Format.Scale)))
            Draw(dc, t);
    }

    /// <summary>The parts of the scene that never change (<see cref="DrawBackdrop"/>), drawn once and reused.</summary>
    protected void Backdrop(DrawingContext dc)
    {
        if (_backdrop is null)
        {
            _backdrop = new RenderTargetBitmap(Format.Pixels);
            using var context = _backdrop.CreateDrawingContext(true);
            using (context.PushTransform(Matrix.CreateScale(Format.Scale, Format.Scale)))
                DrawBackdrop(context);
        }
        dc.DrawImage(_backdrop, new Rect(0, 0, Format.Width, Format.Height));
    }

    protected abstract void DrawBackdrop(DrawingContext dc);

    /// <summary>Moves the clip's state to <paramref name="t"/> (Filee's controls advance one frame).</summary>
    protected abstract void Step(double t);

    protected abstract void Draw(DrawingContext dc, double t);

    public virtual void Dispose() => _backdrop?.Dispose();
}

/// <summary>
/// Filee's live donut window (<see cref="RadialWindow"/> with the real <see cref="RadialViewModel"/>), driven the way
/// RadialController drives it during a drag, with its animation clock stepped by hand. It is rendered into a bitmap
/// every frame and drawn where the window would be on the made-up desktop. Its content is scaled up by a layout
/// transform (crisp, like a high-DPI screen) rather than by the bitmap's DPI, which misplaces drawing after text.
/// </summary>
/// <remarks>
/// The dispatcher and the render timer are deliberately not run while recording: the donut's own frame loop would then
/// step it by wall-clock time (and its stall watchdog finish animations at once), so nothing here would be repeatable.
/// </remarks>
internal sealed class DonutLayer : IDisposable
{
    private readonly RadialViewModel _vm;
    private readonly double _scale;
    private readonly EventHandler<int> _hint;
    private RenderTargetBitmap? _bitmap;
    private Point _origin;

    public DonutLayer(RadialViewModel vm, double scale)
    {
        _vm = vm;
        _scale = scale;
        vm.ResetForDrag(); // sizes the window with room for a group's ring, as for a real drag
        Window = new RadialWindow { DataContext = vm };
        Donut = Window.DonutControl;
        var root = (Control)Window.Content!;
        Window.Content = null;
        Window.Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(scale, scale), Child = root };
        // Drawn into a see-through bitmap: subpixel text on the half-transparent slices (greyed-out presets) came out jagged.
        TextOptions.SetTextRenderingMode(Window, TextRenderingMode.Antialias);
        _hint = (_, index) => vm.ShowHint(index, Donut.ActiveSubIndex, Donut.IsCenterHot);
        Donut.ActiveIndexChanged += _hint;
        Window.Show();
        Window.UpdateLayout();
    }

    public RadialWindow Window { get; }
    public DonutMenu Donut { get; }

    /// <summary>The donut is on screen (from <see cref="OpenAt"/> until it hides itself after the conversion).</summary>
    public bool IsShown { get; private set; }

    /// <summary>Size of the window in layout units.</summary>
    public Size Size => Window.ClientSize / _scale;

    /// <summary>Where the window's top left corner is on the desktop.</summary>
    public Point Origin => _origin;

    /// <summary>The middle of the donut on the desktop.</summary>
    public Point Center => _origin + DonutOffset + new Point(Donut.Bounds.Width / 2, Donut.Bounds.Height / 2);

    private Point DonutOffset => (Donut.TranslatePoint(default, Window) ?? default) / _scale;

    /// <summary>A point in the donut control's coordinates → on the desktop.</summary>
    public Point ToDesktop(Point donut) => _origin + DonutOffset + donut;

    /// <summary>
    /// A drag with the trigger started at <paramref name="center"/>: the window is placed centred on it and the dragged
    /// files enter it at once (RadialController: ResetForDrag, then FilesEntered → Load + PlayOpenAnimation).
    /// </summary>
    public void OpenAt(Point center, IReadOnlyList<string> files)
    {
        _vm.ResetForDrag();
        _vm.Load(files, clickMode: false);
        // Load checks the back face just after the donut is drawn (a dispatcher post): the hub's flip hint and Tab need
        // it. Run now, before the open animation starts a frame loop whose watchdog timers must not run.
        Dispatcher.UIThread.RunJobs();
        Window.UpdateLayout();
        var size = Size;
        // Whole pixels, so the donut is as sharp as in the app.
        _origin = new Point(Math.Round((center.X - size.Width / 2) * _scale) / _scale, Math.Round((center.Y - size.Height / 2) * _scale) / _scale);
        Donut.PlayOpenAnimation();
        IsShown = true;
    }

    /// <summary>Hides the donut (what RadialController's HideWindow does once the ring has faded).</summary>
    public void Hide() => IsShown = false;

    /// <summary>Desktop point → the donut control's coordinates.</summary>
    public Point ToDonut(Point desktop) => desktop - _origin - DonutOffset;

    /// <summary>Desktop point → <see cref="DonutMenu"/> coordinates → what a drag-over there does (RadialWindow.OnDragOver).</summary>
    public DonutHit DragOver(Point desktop)
    {
        var at = ToDonut(desktop);
        var hit = Donut.HitTest(at);
        Donut.SetExternalHit(hit, at);
        return hit;
    }

    /// <summary>A drop at <paramref name="desktop"/>: what RadialWindow.OnDrop does before the conversion starts.</summary>
    public DonutHit Drop(Point desktop)
    {
        var hit = Donut.HitTest(ToDonut(desktop));
        Donut.CollapseGroup();
        Donut.SetExternalHighlight(-1);
        return hit;
    }

    /// <summary>One frame of the donut's animations (and of the download badge above its progress ring).</summary>
    public void Step()
    {
        Donut.Advance(1.0 / DemoEncoder.Fps);
        StepBadge(1.0 / DemoEncoder.Fps);
    }

    private Border? _badge;
    private bool _badgeShown;
    private double _badgeClock, _badgeOpacityFrom, _badgeScaleFrom = 0.6, _badgeOpacity, _badgeScale = 0.6;

    /// <summary>
    /// The download badge's pop (RadialWindow.axaml: opacity over 0.16 s, scale 0.6 → 1 over 0.22 s with BackEaseOut),
    /// played on this clip's clock: its transitions run on the render timer, which doesn't run here.
    /// </summary>
    private void StepBadge(double dt)
    {
        if (_badge is null)
        {
            _badge = Window.FindControl<Border>("DownloadBadge");
            if (_badge is null)
                return;
            _badge.Transitions = null;
        }
        var shown = _vm.ProgressJob?.IsDownloading == true;
        if (shown != _badgeShown)
        {
            _badgeShown = shown;
            _badgeClock = 0;
            (_badgeOpacityFrom, _badgeScaleFrom) = (_badgeOpacity, _badgeScale);
        }
        _badgeClock += dt;
        var (opacity, scale) = shown ? (1.0, 1.0) : (0.0, 0.6);
        _badgeOpacity = _badgeOpacityFrom + (opacity - _badgeOpacityFrom) * Ease.Clamp01(_badgeClock / 0.16);
        _badgeScale = _badgeScaleFrom + (scale - _badgeScaleFrom) * BackEaseOut(Ease.Clamp01(_badgeClock / 0.22));
        _badge.Opacity = _badgeOpacity;
        _badge.RenderTransform = new ScaleTransform(_badgeScale, _badgeScale);

        // Avalonia's BackEaseOut.
        static double BackEaseOut(double t)
        {
            var p = 1 - t;
            return 1 - p * (p * p - Math.Sin(p * Math.PI));
        }
    }

    public void Draw(DrawingContext dc)
    {
        if (!IsShown)
            return;
        Window.UpdateLayout();
        var pixels = new PixelSize((int)Math.Ceiling(Window.ClientSize.Width), (int)Math.Ceiling(Window.ClientSize.Height));
        if (_bitmap?.PixelSize != pixels)
        {
            _bitmap?.Dispose();
            _bitmap = new RenderTargetBitmap(pixels);
        }
        using (_bitmap.CreateDrawingContext(true))
        {
            // cleared: the window is see-through around the donut
        }
        _bitmap.Render(Window);
        dc.DrawImage(_bitmap, new Rect(_origin, new Size(pixels.Width / _scale, pixels.Height / _scale)));
    }

    public void Dispose()
    {
        Donut.ActiveIndexChanged -= _hint;
        Window.Close();
        _bitmap?.Dispose();
    }
}
