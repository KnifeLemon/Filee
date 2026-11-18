// Brief outcome feedback inside a Save button. The label keeps its space, and reduced motion only swaps the icon.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Filee.App.Services;

namespace Filee.App.Controls;

/// <summary>The result of an explicit save attempt.</summary>
public enum SaveResult { None, Succeeded, Failed }

/// <summary>A button label that briefly becomes an outcome badge, without changing the button's size.</summary>
public sealed class SaveFeedback : Grid
{
    /// <summary>The result to show. Reset to None before another attempt, even if its result is the same.</summary>
    public static readonly StyledProperty<SaveResult> ResultProperty =
        AvaloniaProperty.Register<SaveFeedback, SaveResult>(nameof(Result));

    /// <summary>The normal button label.</summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<SaveFeedback, string?>(nameof(Text));

    private readonly TextBlock _label;
    private readonly OutcomeGlyph _badge;
    private readonly Stopwatch _clock = new();
    private DispatcherTimer? _animation;
    private IDisposable? _restore;

    /// <summary>Creates a label with a short, centred outcome animation.</summary>
    public SaveFeedback()
    {
        MinWidth = 24;
        MinHeight = 24;
        _label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _badge = new OutcomeGlyph { Width = 24, Height = 24, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Children.Add(_label);
        Children.Add(_badge);
        AttachedToVisualTree += (_, _) => Motion.Changed += MotionChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            Motion.Changed -= MotionChanged;
            Reset();
        };
    }

    /// <summary>The latest save outcome.</summary>
    public SaveResult Result { get => GetValue(ResultProperty); set => SetValue(ResultProperty, value); }

    /// <summary>The translated Save label.</summary>
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    internal bool IsShowingResult => _badge.IsVisible;
    internal double BadgeScale => _badge.Scale;
    internal bool IsPlayingAnimation => _animation is not null;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
            _label.Text = Text;
        if (change.Property != ResultProperty)
            return;
        Reset();
        if (Result == SaveResult.None)
            return;
        _badge.Succeeded = Result == SaveResult.Succeeded;
        _badge.IsVisible = true;
        _label.Opacity = 0; // Still measured: long translations and the surrounding buttons never jump.
        _badge.Scale = Motion.Enabled ? 0.3 : 1;
        _clock.Restart();
        if (Motion.Enabled)
        {
            _animation = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _animation.Tick += Animate;
            _animation.Start();
        }
        _restore = DispatcherTimer.RunOnce(Reset, TimeSpan.FromMilliseconds(900));
    }

    private void Animate(object? sender, EventArgs e)
    {
        var progress = Math.Clamp(_clock.Elapsed.TotalMilliseconds / 180, 0, 1);
        _badge.Scale = 0.3 + 0.7 * (1 - Math.Pow(1 - progress, 3)) + 0.12 * Math.Sin(Math.PI * progress);
        if (progress >= 1)
            StopAnimation();
    }

    private void MotionChanged(object? sender, EventArgs e)
    {
        if (!Motion.Enabled)
            StopAnimation();
    }

    private void StopAnimation()
    {
        _animation?.Stop();
        _animation = null;
        _badge.Scale = 1;
    }

    private void Reset()
    {
        _restore?.Dispose();
        _restore = null;
        StopAnimation();
        _badge.IsVisible = false;
        _label.Opacity = 1;
    }
}

/// <summary>The donut's success tick, or a cross, drawn on a themed outcome badge.</summary>
internal sealed class OutcomeGlyph : Control
{
    private double _scale = 1;

    internal OutcomeGlyph()
    {
        // Brushes follow the theme, including custom colours, just like the donut.
        Bind(SuccessProperty, this.GetResourceObservable("SuccessBrush"));
        Bind(DangerProperty, this.GetResourceObservable("DangerBrush"));
        Bind(InkProperty, this.GetResourceObservable("OnAccentBrush"));
    }

    private static readonly StyledProperty<IBrush?> SuccessProperty = AvaloniaProperty.Register<OutcomeGlyph, IBrush?>("Success", Brushes.SeaGreen);
    private static readonly StyledProperty<IBrush?> DangerProperty = AvaloniaProperty.Register<OutcomeGlyph, IBrush?>("Danger", Brushes.IndianRed);
    private static readonly StyledProperty<IBrush?> InkProperty = AvaloniaProperty.Register<OutcomeGlyph, IBrush?>("Ink", Brushes.White);

    static OutcomeGlyph() => AffectsRender<OutcomeGlyph>(SuccessProperty, DangerProperty, InkProperty);

    internal bool Succeeded { get; set; }
    internal double Scale { get => _scale; set { _scale = value; InvalidateVisual(); } }

    public override void Render(DrawingContext context)
    {
        var center = new Rect(Bounds.Size).Center;
        using var transform = context.PushTransform(Matrix.CreateTranslation(-center.X, -center.Y) *
            Matrix.CreateScale(Scale * 0.48, Scale * 0.48) * Matrix.CreateTranslation(center.X, center.Y));
        context.DrawEllipse(GetValue(Succeeded ? SuccessProperty : DangerProperty), null, center, 22, 22);
        var ink = GetValue(InkProperty);
        if (Succeeded)
            DrawCheck(context, center, ink);
        else
        {
            var pen = new Pen(ink, 3.6, lineCap: PenLineCap.Round);
            context.DrawLine(pen, center + new Vector(-7, -7), center + new Vector(7, 7));
            context.DrawLine(pen, center + new Vector(-7, 7), center + new Vector(7, -7));
        }
    }

    /// <summary>Shared with the donut so the success mark has exactly the same shape.</summary>
    internal static void DrawCheck(DrawingContext context, Point center, IBrush? ink)
    {
        var check = new StreamGeometry();
        using (var g = check.Open())
        {
            g.BeginFigure(new Point(center.X - 9, center.Y + 0.5), false);
            g.LineTo(new Point(center.X - 2.5, center.Y + 7));
            g.LineTo(new Point(center.X + 10, center.Y - 6.5));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(ink, 3.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), check);
    }
}
