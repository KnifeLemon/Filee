// DonutMenu: drawing. The ring of wedges, labels, the hub, the editor's ✎ button and the drop preview.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Filee.App.Services;

namespace Filee.App.Controls;

public sealed partial class DonutMenu
{
    // ───────────────────────── Rendering ─────────────────────────

    public override void Render(DrawingContext context)
    {
        var center = Center;
        var r2 = OuterRadius;
        var r1 = InnerRadius;
        var n = Count;
        var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, FontWeight.SemiBold);
        var typefaceRegular = new Typeface(GetValue(TextElement.FontFamilyProperty));

        if (_progressMode)
        {
            RenderProgress(context, center, r1, r2, typeface, typefaceRegular);
            return;
        }

        RenderRing(context, center, r1, r2, n, typeface, typefaceRegular);

        var reaching = _expanded >= 0 ? _expanded : _pendingGroup;
        if (!IsEditMode && reaching >= 0 && reaching < n && Items![reaching].Children is { Count: > 0 } reachable)
            DrawHitBackdrop(context, center, reaching, reachable.Count);
        if (!IsEditMode && _shownExpanded >= 0 && _shownExpanded < n && _expand.Value > 0.01)
            DrawSubRing(context, center, typeface, typefaceRegular);
        if (HasOldGroupRing)
            DrawOldSubRing(context, center, typeface, typefaceRegular);

        DrawCenter(context, center, r1, typeface, typefaceRegular);

        if (IsEditMode && _hoverIndex >= 0 && _hoverIndex < n && _dimmedIndex < 0 && !IsFlipping)
            DrawEditButton(context, EditButtonCenter(_hoverIndex));
        DrawFocusRing(context);
    }

    /// <summary>The shadow, the slices and the edit mode's drop preview.</summary>
    private void RenderRing(DrawingContext context, Point center, double r1, double r2, int n, Typeface typeface, Typeface typefaceRegular)
    {
        DrawSoftShadow(context, center, r2);

        if (n == 0)
        {
            var ring = CreateRing(center, r1, r2);
            context.DrawGeometry(WithOpacity(SliceBrush, SliceOpacity), BorderPen(), ring);
            if (_dropTargetIndex >= 0)
                context.DrawGeometry(WithOpacity(AccentBrush, 0.35), null, ring);
        }

        for (var i = 0; i < n; i++)
        {
            if (IsFlipping && SliceFlipWidth(false, i) == 0)
                continue;
            var item = Items![i];
            var open = i < _open.Length ? _open[i] : 1;
            if (open <= 0.001)
                continue;

            var previewing = IsEditMode && i < _mid.Length;
            var hover = i < _hover.Length && _previewSlot < 0 ? _hover[i].Value : 0;
            var mid = previewing ? _mid[i].Value : DonutGeometry.MidAngle(i, n);
            var span = previewing ? _span[i].Value : DonutGeometry.Span(n);
            if (span < 0.01)
                continue;
            var offset = DonutGeometry.PointAt(new Point(0, 0), hover * PopOut, mid);

            // Open animation: scale up from the centre with a small twist.
            var scale = 0.55 + 0.45 * open;
            var rotate = (1 - Math.Min(open, 1)) * -0.35;
            var transform =
                Matrix.CreateTranslation(-center.X, -center.Y) *
                Matrix.CreateRotation(rotate) *
                Matrix.CreateScale(scale * (1 + hover * 0.025), scale * (1 + hover * 0.025)) *
                Matrix.CreateTranslation(center.X + offset.X, center.Y + offset.Y) *
                SliceFlipTransform(false, i);

            using (context.PushTransform(transform))
            using (context.PushOpacity(Math.Clamp(open, 0, 1) * (i == _dimmedIndex ? 0.3 : 1)))
            {
                var wedge = CreateWedge(center, r1, r2, mid, span);
                context.DrawGeometry(WithOpacity(SliceBrush, SliceOpacity * (item.IsEnabled ? 1 : 0.55)), BorderPen(), wedge);

                // The accent fades in and out with the highlight (no floor: a floor made it vanish with a jump).
                var emphasis = i == _dropTargetIndex ? 1.0 : hover;
                if (emphasis > 0.01 && item.IsEnabled)
                    context.DrawGeometry(WithOpacity(AccentBrush, 0.82 * Math.Clamp(emphasis, 0, 1)), null, wedge);

                var hot = (i == ActiveIndex || i == _dropTargetIndex) && item.IsEnabled;
                DrawLabel(context, item, center, (r1 + r2) / 2, mid, span * (r1 + r2) / 2 - 10,
                    r2 - r1 - 16, typeface, typefaceRegular, hot);
            }
        }

        if (_oldFace is { } old)
            for (var i = 0; i < old.Length; i++)
            {
                var slice = old[i];
                if (SliceFlipWidth(true, i) == 0 || slice.Open <= 0.001)
                    continue;
                var offset = DonutGeometry.PointAt(new Point(0, 0), slice.Hover * PopOut, slice.Mid);
                var scale = 0.55 + 0.45 * slice.Open;
                var rotate = (1 - Math.Min(slice.Open, 1)) * -0.35;
                var transform = Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateRotation(rotate) *
                                Matrix.CreateScale(scale * (1 + slice.Hover * 0.025), scale * (1 + slice.Hover * 0.025)) *
                                Matrix.CreateTranslation(center.X + offset.X, center.Y + offset.Y) *
                                SliceFlipTransform(true, i);
                using (context.PushTransform(transform))
                using (context.PushOpacity(Math.Clamp(slice.Open, 0, 1) * (slice.Dimmed ? 0.3 : 1)))
                {
                    var wedge = CreateWedge(center, r1, r2, slice.Mid, slice.Span);
                    context.DrawGeometry(WithOpacity(SliceBrush, SliceOpacity * (slice.Item.IsEnabled ? 1 : 0.55)), BorderPen(), wedge);
                    if (slice.Emphasis > 0.01 && slice.Item.IsEnabled)
                        context.DrawGeometry(WithOpacity(AccentBrush, 0.82 * Math.Clamp(slice.Emphasis, 0, 1)), null, wedge);
                    DrawLabel(context, slice.Item, center, (r1 + r2) / 2, slice.Mid,
                        slice.Span * (r1 + r2) / 2 - 10, r2 - r1 - 16, typeface, typefaceRegular,
                        slice.Hot && slice.Item.IsEnabled);
                }
            }

        if (IsEditMode && _previewSpan.Value > 0.01)
            DrawPreview(context, center, r1, r2, typeface, typefaceRegular);
    }

    private void DrawSoftShadow(DrawingContext context, Point center, double r2)
    {
        // A radial gradient ring fakes a soft drop shadow without blur effects (cheap on any GPU).
        var shadowRadius = r2 + 14;
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), (r2 - 18) / shadowRadius),
                new GradientStop(Color.FromArgb(46, 0, 0, 0), (r2 - 2) / shadowRadius),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 1),
            },
        };
        var opening = _open.Length == 0 ? 1 : Math.Clamp(_open.Average(), 0, 1);
        using (context.PushOpacity(opening * ShadowCoverage))
            context.DrawEllipse(brush, null, new Point(center.X, center.Y + 4), shadowRadius, shadowRadius);
    }

    private void DrawCenter(DrawingContext context, Point center, double r1, Typeface bold, Typeface regular)
    {
        var pulse = 1 + _centerPulse.Value * 0.06;
        var radius = (r1 - 6) * pulse;
        var cancelHot = _externalCenterHot;
        context.DrawEllipse(CenterBrush, BorderPen(), center, radius, radius);
        if (cancelHot)
            context.DrawEllipse(WithOpacity(DangerBrush, 0.18), null, center, radius, radius);

        var maxWidth = radius * 1.6;
        var title = MakeText(CenterTitle ?? "", bold, 14, Foreground, maxWidth, maxLines: 2);
        var subtitle = MakeText(CenterSubtitle ?? "", regular, 11.5, cancelHot ? DangerBrush : SecondaryForeground, maxWidth, maxLines: 2);
        // "Tab ↻ back": small, under the rest, a little narrower (the hub is round).
        var hint = string.IsNullOrEmpty(CenterHint) ? null : MakeText(CenterHint, regular, 10.5, SecondaryForeground, maxWidth * 0.85);
        var total = title.Height + (string.IsNullOrEmpty(CenterSubtitle) ? 0 : subtitle.Height + 2) + (hint is null ? 0 : hint.Height + 5);
        var y = center.Y - total / 2;
        DrawCentered(context, title, center.X, y);
        y += title.Height;
        if (!string.IsNullOrEmpty(CenterSubtitle))
        {
            DrawCentered(context, subtitle, center.X, y + 2);
            y += subtitle.Height + 2;
        }
        if (hint is not null)
        {
            using (context.PushOpacity(0.85))
            {
                if (!CenterHintWheel)
                {
                    DrawCentered(context, hint, center.X, y + 5);
                }
                else
                {
                    // A small mouse in front: "🖱 ↻ back" when the wheel flips and no key does.
                    const double iconWidth = 7, gap = 4;
                    var left = center.X - (hint.Width + iconWidth + gap) / 2;
                    DrawMouse(context, new Rect(left, y + 5 + (hint.Height - 10) / 2, iconWidth, 10), SecondaryForeground);
                    DrawCentered(context, hint, center.X + (iconWidth + gap) / 2, y + 5);
                }
            }
        }
    }

    /// <summary>An outline of a mouse with its wheel, filling <paramref name="box"/>.</summary>
    private static void DrawMouse(DrawingContext context, Rect box, IBrush? brush)
    {
        var pen = new Pen(brush, 1.1);
        context.DrawRectangle(null, pen, box, box.Width / 2, box.Width / 2);
        context.DrawLine(pen, new Point(box.Center.X, box.Top + 2), new Point(box.Center.X, box.Top + 4.5));
    }

    private void DrawLabel(DrawingContext context, DonutItem item, Point center, double radius, double angle,
        double maxWidth, double maxHeight, Typeface bold, Typeface regular, bool hot)
    {
        var foreground = hot ? OnAccentBrush : item.IsEnabled ? Foreground : SecondaryForeground;
        var width = Math.Clamp(maxWidth, 30, 120);
        var text = KeepKoreanWords(item.Label);
        var label = MakeText(text, bold, LabelSize(text, bold, width), foreground, width, maxLines: 2);
        var showCaption = !string.IsNullOrEmpty(item.Caption) &&
                          !string.Equals(item.Caption, item.Label, StringComparison.OrdinalIgnoreCase) &&
                          label.Height + 14 < maxHeight;
        var caption = showCaption
            ? MakeCaption(item.Caption!, regular, hot ? OnAccentBrush : SecondaryForeground, width, label.Height, maxHeight)
            : null;

        var total = label.Height + (caption?.Height ?? 0);
        var anchor = DonutGeometry.PointAt(center, radius, angle);
        var y = anchor.Y - total / 2;
        DrawCentered(context, label, anchor.X, y);
        if (caption is not null)
            DrawCentered(context, caption, anchor.X, y + label.Height);
    }

    /// <summary>
    /// A slice's caption under its name: one line, or two where the slice is deep enough, so a caption wider than the
    /// slice ("Same format" on a small donut) wraps instead of ending in "Same for…".
    /// </summary>
    internal static FormattedText MakeCaption(string caption, Typeface typeface, IBrush? brush, double width,
        double labelHeight, double maxHeight) =>
        MakeText(caption, typeface, 10.5, brush, width, maxLines: labelHeight + 28 < maxHeight ? 2 : 1);

    private const double LabelFontSize = 13;
    private const double MinLabelFontSize = 10;
    private readonly Dictionary<(string Text, Typeface Typeface, double Width), double> _labelSizes = new();

    /// <summary>
    /// The size a slice's name is drawn at: 13, or smaller (down to 10) when one of its words is wider than the slice
    /// (<paramref name="width"/>), which would otherwise break in the middle ("Grayscal|e" on a thin slice). Words in
    /// Chinese or Japanese are left out: those break between any two characters.
    /// </summary>
    internal double LabelSize(string text, Typeface typeface, double width)
    {
        var line = Math.Round(width);
        var key = (text, typeface, line);
        if (_labelSizes.TryGetValue(key, out var size))
            return size;
        if (_labelSizes.Count >= MaxCachedWedges)
            _labelSizes.Clear();
        var widest = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !word.Any(IsHanOrKana))
            .Select(word => new FormattedText(word, CultureInfo.CurrentUICulture, TextDirection, typeface, LabelFontSize, null).Width)
            .DefaultIfEmpty(0)
            .Max();
        // A pixel to spare: a word exactly as wide as the line still wraps.
        size = widest < line ? LabelFontSize : Math.Max(MinLabelFontSize, LabelFontSize * (line - 1) / widest);
        return _labelSizes[key] = size;
    }

    /// <summary>Chinese characters, kana and the CJK symbols and full-width forms: a line may break between any two.</summary>
    private static bool IsHanOrKana(char c) =>
        c is >= '\u2E80' and <= '\u312F' or >= '\u3190' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF' or >= '\uFF00' and <= '\uFFEF';

    /// <summary>
    /// Korean is written with spaces between words and wraps there, but the text layout also breaks between any two
    /// Hangul syllables ("ZIP 하나|로"). A word joiner between them keeps each word on one line.
    /// </summary>
    internal static string KeepKoreanWords(string text)
    {
        if (!text.Any(IsHangul))
            return text;
        var kept = new System.Text.StringBuilder(text.Length * 2);
        for (var i = 0; i < text.Length; i++)
        {
            kept.Append(text[i]);
            if (i + 1 < text.Length && IsHangul(text[i]) && IsHangul(text[i + 1]))
                kept.Append('\u2060');
        }
        return kept.ToString();
    }

    private static bool IsHangul(char c) => c is >= '\uAC00' and <= '\uD7A3';

    private Point EditButtonCenter(int index)
    {
        var mid = DonutGeometry.MidAngle(index, Count);
        return DonutGeometry.PointAt(Center, OuterRadius - 14 + PopOut, mid);
    }

    private void DrawEditButton(DrawingContext context, Point at)
    {
        context.DrawEllipse(AccentBrush, new Pen(CenterBrush, 2), at, 12, 12);
        var bounds = PencilIcon.Bounds;
        var s = 13 / Math.Max(bounds.Width, bounds.Height);
        var matrix = Matrix.CreateTranslation(-bounds.Center.X, -bounds.Center.Y) * Matrix.CreateScale(s, s) * Matrix.CreateTranslation(at.X, at.Y);
        using (context.PushTransform(matrix))
            context.DrawGeometry(OnAccentBrush, null, PencilIcon);
    }

    /// <summary>
    /// Draws text horizontally centred on <paramref name="centerX"/>.
    /// The text is laid out with <see cref="TextAlignment.Center"/> inside a box of <see cref="FormattedText.MaxTextWidth"/>,
    /// so the box (not the measured <see cref="FormattedText.Width"/>) must be centred — otherwise short labels
    /// drift right by (box − text) / 2.
    /// </summary>
    internal static void DrawCentered(DrawingContext context, FormattedText text, double centerX, double top) =>
        context.DrawText(text, new Point(centerX - text.MaxTextWidth / 2, top));

    private Pen? BorderPen() => SliceBorderBrush is null ? null : new Pen(SliceBorderBrush, IsHighContrast ? 2 : 1);

    private static FormattedText MakeText(string text, Typeface typeface, double size, IBrush? brush, double maxWidth, int maxLines = 1) =>
        new(text, CultureInfo.CurrentUICulture, TextDirection, typeface, size, brush)
        {
            MaxTextWidth = Math.Max(10, maxWidth),
            MaxLineCount = maxLines,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };

    private static FlowDirection TextDirection => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft
        ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    private static IBrush? WithOpacity(IBrush? brush, double opacity) => brush switch
    {
        ISolidColorBrush solid => new SolidColorBrush(solid.Color, solid.Opacity * opacity),
        _ => brush,
    };

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    /// <summary>The translucent slice where a dragged preset would land, with its name.</summary>
    private void DrawPreview(DrawingContext context, Point center, double r1, double r2, Typeface bold, Typeface regular)
    {
        var mid = _previewMid.Value;
        var span = _previewSpan.Value;
        var wedge = CreateWedge(center, r1, r2, mid, span);
        var outline = new Pen(WithOpacity(AccentBrush, 0.9), 1.5, new DashStyle([4, 3], 0));
        context.DrawGeometry(WithOpacity(AccentBrush, 0.22), outline, wedge);
        if (_previewLabel is null || span < 0.25)
            return;
        using (context.PushOpacity(Math.Clamp((span - 0.25) / 0.3, 0, 1) * 0.9))
            DrawLabel(context, new DonutItem { Id = "", Label = _previewLabel }, center, (r1 + r2) / 2, mid,
                span * (r1 + r2) / 2 - 10, r2 - r1 - 16, bold, regular, hot: false);
    }

    /// <summary>
    /// How round the slices' corners are: half the theme's softness (0 to 28), so the largest setting still leaves the
    /// slices clearly apart. <see cref="DonutGeometry.Wedge"/> shrinks it for slices too thin for it.
    /// </summary>
    internal double SliceCornerRadius => Math.Clamp(CornerRadius.TopLeft, 0, 28) * 0.5;

    private StreamGeometry CreateWedge(Point c, double r1, double r2, int index, int count) =>
        count == 1 ? CreateRing(c, r1, r2) : CreateWedge(c, r1, r2, DonutGeometry.MidAngle(index, count), DonutGeometry.Span(count));

    /// <summary>A rounded slice of <paramref name="span"/> radians centred on <paramref name="mid"/> (a ring when it is all of it).</summary>
    private StreamGeometry CreateWedge(Point c, double r1, double r2, double mid, double span) =>
        CreateWedge(c, r1, r2, mid, span, SliceCornerRadius);

    // Slices are drawn every frame while the donut animates, but their shapes only change with the size, the layout
    // or the softness: the shapes are kept and reused. Animations move them with transforms; only the edit mode's
    // sliding slices make new ones, and the cache starts over when it fills up.
    private readonly Dictionary<(Point C, double R1, double R2, double Mid, double Span, double Corner), StreamGeometry> _wedges = new();
    private const int MaxCachedWedges = 256;

    private StreamGeometry CreateWedge(Point c, double r1, double r2, double mid, double span, double cornerRadius)
    {
        var key = (c, r1, r2, mid, span, cornerRadius);
        if (_wedges.TryGetValue(key, out var cached))
            return cached;
        if (_wedges.Count >= MaxCachedWedges)
            _wedges.Clear();
        return _wedges[key] = BuildWedge(c, r1, r2, mid, span, cornerRadius);
    }

    /// <summary>A slice with a constant-width gap to its neighbours and corners rounded by <paramref name="cornerRadius"/>.</summary>
    private static StreamGeometry BuildWedge(Point c, double r1, double r2, double mid, double span, double cornerRadius)
    {
        if (span >= 2 * Math.PI - 0.001)
            return CreateRing(c, r1, r2);

        var outline = DonutGeometry.Wedge(c, r1, r2, mid, span, GapPixels, cornerRadius);
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(outline.Start, true);
        foreach (var step in outline.Steps)
        {
            if (step.IsArc)
                ctx.ArcTo(step.To, new Size(step.ArcRadius, step.ArcRadius), 0, step.IsLargeArc,
                    step.Clockwise ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
            else
                ctx.LineTo(step.To);
        }
        ctx.EndFigure(true);
        return geometry;
    }

    /// <summary>Full ring (used for a single slice and for the empty donut).</summary>
    private static StreamGeometry CreateRing(Point c, double r1, double r2)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.SetFillRule(FillRule.EvenOdd);
        foreach (var r in new[] { r2, r1 })
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), true);
            ctx.ArcTo(new Point(c.X, c.Y + r), new Size(r, r), 0, false, SweepDirection.Clockwise);
            ctx.ArcTo(new Point(c.X, c.Y - r), new Size(r, r), 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(true);
        }
        return geometry;
    }
}
