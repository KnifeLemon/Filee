// Filee's logo (src/Filee.App/Assets/Icons/filee.svg) drawn from its paths, so it can assemble itself: the six slices
// spring in one after the other while the ring turns into place, then the file in the middle drops in. Without
// animations (or after 1.4 s) it is simply the logo. The slices and the file are filled with the same gradients as
// the SVG and have rounded corners (a stroke in the fill's own gradient with round joins), like the SVG. The SVG's
// drop shadow (a pale purple blur at 10 %, 6 units below the shapes) is not drawn: Avalonia can't blur a shape, it is
// hardly visible at this strength, and the card behind the logo already has a soft backdrop.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Filee.Setup.Controls;

public sealed class DonutLogo : Control
{
    /// <summary>Plays the entrance when shown; off draws the finished logo.</summary>
    public static readonly StyledProperty<bool> AnimateProperty =
        AvaloniaProperty.Register<DonutLogo, bool>(nameof(Animate), true);

    private const double ViewBox = 512;
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(1400);

    // filee.svg: the gradients (light and dark purple, green, the file), top left to bottom right except the file.
    private static readonly IBrush Light = Gradient("#A39CF4", "#6C63D8", diagonal: true);
    private static readonly IBrush Dark = Gradient("#8179E6", "#4F46B5", diagonal: true);
    private static readonly IBrush Green = Gradient("#3FD6A0", "#13875F", diagonal: true);
    private static readonly IBrush Document = Gradient("#4B41AA", "#2D2675", diagonal: false);

    // The slices (path, gradient, offset of the "pulled out" slice). Each is also stroked 24 wide with round joins in
    // the same gradient, which is what rounds its corners (the paths are 12 smaller all round to make up for it).
    private static readonly Slice[] Slices =
    [
        new("M169.99 71.02 A204 204 0 0 1 342.01 71.02 L297.71 147.76 A116 116 0 0 0 214.29 147.76 Z", Light, default),
        new("M373.19 89.02 A204 204 0 0 1 459.20 238.00 L370.59 238.00 A116 116 0 0 0 328.89 165.76 Z", Green, new Vector(10.39, -6)),
        new("M459.20 274.00 A204 204 0 0 1 373.19 422.98 L328.89 346.24 A116 116 0 0 0 370.59 274.00 Z", Light, default),
        new("M342.01 440.98 A204 204 0 0 1 169.99 440.98 L214.29 364.24 A116 116 0 0 0 297.71 364.24 Z", Dark, default),
        new("M138.81 422.98 A204 204 0 0 1 52.80 274.00 L141.41 274.00 A116 116 0 0 0 183.11 346.24 Z", Light, default),
        new("M52.80 238.00 A204 204 0 0 1 138.81 89.02 L183.11 165.76 A116 116 0 0 0 141.41 238.00 Z", Dark, default),
    ];

    // The file in the middle: the body and its folded corner (both with rounded corners) and three lines of "text".
    private static readonly Geometry FileBody = Geometry.Parse("M227 217 H262 L285 240 V295 H227 Z");
    private static readonly Geometry FileCorner = Geometry.Parse("M262 217 V240 H285 Z");
    private static readonly IBrush CornerBrush = Brush("#C9C4F7");
    private static readonly IPen FileBodyPen = RoundPen(Document, 10);
    private static readonly IPen FileCornerPen = RoundPen(CornerBrush, 4);
    private static readonly (Rect Bounds, IBrush Fill)[] FileLines =
    [
        (new Rect(235, 256, 42, 5), White(0.55)),
        (new Rect(235, 268, 32, 5), White(0.40)),
        (new Rect(235, 280, 38, 5), White(0.30)),
    ];

    private readonly FrameLoop _loop;
    private double _progress;

    static DonutLogo()
    {
        AffectsRender<DonutLogo>(AnimateProperty);
    }

    public DonutLogo()
    {
        // The logo is never mirrored, also in Arabic.
        FlowDirection = FlowDirection.LeftToRight;
        _progress = SetupMotion.Enabled ? 0 : 1;
        _loop = new FrameLoop(this, elapsed =>
        {
            _progress = Math.Min(1, elapsed / Duration);
            InvalidateVisual();
            return _progress < 1;
        });
    }

    public bool Animate { get => GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }

    /// <summary>Plays the entrance again.</summary>
    public void Replay()
    {
        if (!SetupMotion.Enabled || !Animate)
            return;
        _progress = 0;
        _loop.Restart();
    }

    private static IBrush Brush(string color) => new ImmutableSolidColorBrush(Color.Parse(color));

    private static IBrush White(double opacity) => new ImmutableSolidColorBrush(Color.FromArgb((byte)Math.Round(255 * opacity), 255, 255, 255));

    private static IPen RoundPen(IBrush brush, double width) => new Pen(brush, width, lineJoin: PenLineJoin.Round);

    /// <summary>A two-colour gradient across the shape: top left to bottom right, or top to bottom.</summary>
    private static IBrush Gradient(string from, string to, bool diagonal) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = diagonal ? new RelativePoint(1, 1, RelativeUnit.Relative) : new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse(from), 0),
            new GradientStop(Color.Parse(to), 1),
        },
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(double.IsInfinity(availableSize.Width) ? 96 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 96 : availableSize.Height);
        return new Size(side, side);
    }

    public override void Render(DrawingContext context)
    {
        var progress = Animate && SetupMotion.Enabled ? _progress : 1;
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0)
            return;
        var scale = side / ViewBox;
        var origin = new Point((Bounds.Width - side) / 2, (Bounds.Height - side) / 2);
        var center = new Point(ViewBox / 2, ViewBox / 2);
        // The ring turns a third of a turn into place while the slices arrive.
        var ring = EaseOut(Math.Min(1, progress / 0.75));
        var rotation = Matrix.CreateTranslation(-center.X, -center.Y)
                       * Matrix.CreateRotation((1 - ring) * -2.1)
                       * Matrix.CreateTranslation(center.X, center.Y);
        var view = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(origin.X, origin.Y);

        for (var i = 0; i < Slices.Length; i++)
        {
            var slice = Slices[i];
            var local = Math.Clamp((progress - i * 0.07) / 0.45, 0, 1);
            if (local <= 0)
                continue;
            var spring = BackOut(local);
            // Each slice grows from the ring's centre line outwards, with a little overshoot.
            var grow = Matrix.CreateTranslation(-center.X, -center.Y)
                       * Matrix.CreateScale(0.55 + 0.45 * spring, 0.55 + 0.45 * spring)
                       * Matrix.CreateTranslation(center.X + slice.Offset.X, center.Y + slice.Offset.Y);
            using (context.PushTransform(grow * rotation * view))
            using (context.PushOpacity(Math.Min(1, local * 1.6)))
                context.DrawGeometry(slice.Fill, slice.Outline, slice.Path);
        }

        var file = Math.Clamp((progress - 0.55) / 0.4, 0, 1);
        if (file > 0)
        {
            var drop = (1 - BackOut(file)) * -40;
            using (context.PushTransform(Matrix.CreateTranslation(0, drop) * view))
            using (context.PushOpacity(file))
            {
                context.DrawGeometry(Document, FileBodyPen, FileBody);
                context.DrawGeometry(CornerBrush, FileCornerPen, FileCorner);
                foreach (var (bounds, fill) in FileLines)
                    context.DrawRectangle(fill, null, bounds, 2.5, 2.5);
            }
        }
    }

    internal static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    internal static double BackOut(double t)
    {
        const double c1 = 1.70158;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    /// <summary>One slice of the ring: its shape, gradient, rounded outline and where it sits (the green one is pulled out).</summary>
    private sealed class Slice(string path, IBrush fill, Vector offset)
    {
        public Geometry Path { get; } = Geometry.Parse(path);
        public IBrush Fill { get; } = fill;
        public IPen Outline { get; } = RoundPen(fill, 24);
        public Vector Offset { get; } = offset;
    }
}
