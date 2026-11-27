// A small, still copy of the donut's progress ring (the plate, the ring and the percentage) for the Theme page, so
// the arc and the dial of ticks can be seen side by side before one is chosen. It draws with the donut's own code
// (DonutMenu.DrawTickDial, DrawArc), so what the card shows is what the donut will draw.

using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Filee.App.Controls;

public sealed class ProgressRingPreview : TemplatedControl
{
    public static readonly StyledProperty<bool> TickProperty = AvaloniaProperty.Register<ProgressRingPreview, bool>(nameof(Tick));
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<ProgressRingPreview, double>(nameof(Value), 0.72);
    public static readonly StyledProperty<IBrush?> PlateBrushProperty = AvaloniaProperty.Register<ProgressRingPreview, IBrush?>(nameof(PlateBrush));
    public static readonly StyledProperty<IBrush?> PlateBorderBrushProperty = AvaloniaProperty.Register<ProgressRingPreview, IBrush?>(nameof(PlateBorderBrush));
    public static readonly StyledProperty<IBrush?> AccentBrushProperty = AvaloniaProperty.Register<ProgressRingPreview, IBrush?>(nameof(AccentBrush), Brushes.MediumPurple);
    public static readonly StyledProperty<IBrush?> SecondaryForegroundProperty = AvaloniaProperty.Register<ProgressRingPreview, IBrush?>(nameof(SecondaryForeground), Brushes.Gray);

    // The plate (RingRadius + stroke + 2) and its soft shadow around it.
    private const double Plate = DonutMenu.RingRadius + 8;
    private const double Side = (Plate + 10) * 2;
    private const string SampleCaption = "MP4 → WEBP";

    static ProgressRingPreview()
    {
        AffectsRender<ProgressRingPreview>(TickProperty, ValueProperty, PlateBrushProperty, PlateBorderBrushProperty,
            AccentBrushProperty, ForegroundProperty, SecondaryForegroundProperty);
    }

    /// <summary>Draw the dial of ticks (true) or the arc (false).</summary>
    public bool Tick { get => GetValue(TickProperty); set => SetValue(TickProperty, value); }

    /// <summary>How far along the ring is, 0..1.</summary>
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public IBrush? PlateBrush { get => GetValue(PlateBrushProperty); set => SetValue(PlateBrushProperty, value); }
    public IBrush? PlateBorderBrush { get => GetValue(PlateBorderBrushProperty); set => SetValue(PlateBorderBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? SecondaryForeground { get => GetValue(SecondaryForegroundProperty); set => SetValue(SecondaryForegroundProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Side, Side);

    public override void Render(DrawingContext context)
    {
        var center = new Point(Side / 2, Side / 2);
        var value = Math.Clamp(Value, 0, 1);

        DonutMenu.DrawPlateShadow(context, center, Plate);
        context.DrawEllipse(PlateBrush, PlateBorderBrush is null ? null : new Pen(PlateBorderBrush, 1), center, Plate, Plate);

        var faint = DonutMenu.WithOpacity(SecondaryForeground, 0.22);
        if (Tick)
        {
            DonutMenu.DrawTickDial(context, center, AccentBrush, faint, Foreground, DonutMenu.LitTicks(value));
        }
        else
        {
            const double stroke = 6;
            context.DrawEllipse(null, new Pen(faint, stroke), center, DonutMenu.RingRadius, DonutMenu.RingRadius);
            DonutMenu.DrawArc(context, center, DonutMenu.RingRadius, value, new Pen(AccentBrush, stroke, lineCap: PenLineCap.Round));
        }

        var bold = new Typeface(FontFamily, FontStyle.Normal, FontWeight.Bold);
        if (Tick)
        {
            // The dial shows what it will show in the donut: the source format and the preset (a sample here).
            DonutMenu.DrawPercentWithCaption(context, center, bold, Math.Round(value * 100), SampleCaption, Foreground, SecondaryForeground);
            return;
        }

        var percent = DonutMenu.MakeText($"{Math.Round(value * 100):0}%", bold, 15, Foreground, DonutMenu.RingRadius * 1.6);
        DonutMenu.DrawCentered(context, percent, center.X, center.Y - percent.Height / 2);
    }
}
