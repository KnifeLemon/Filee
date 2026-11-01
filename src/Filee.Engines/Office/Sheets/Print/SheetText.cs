// Fonts and text of a printed sheet, on the layout engine's services (FontResolver, Shaper, LineBreaker): fonts by
// the names Excel uses, Excel's pixel metrics of a font (the widest digit sets column widths, the rounded ascent and
// descent set row heights), and cell text measured, broken into lines and drawn as glyph runs.
//
// Excel measures at 96 dpi with the font at a whole pixel size: 11 pt is 15 px, whose digits are 7 px wide in Calibri
// (columns) and whose ascent and descent round to 14 + 4 px, plus 2 px of padding: the 20 px (15 pt) default row.

using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
using SkiaSharp;

namespace Filee.Engines.Office.Sheets;

/// <summary>How a piece of cell, header or footer text looks (size in points at 100 %).</summary>
internal sealed record TextStyle(string Family, double Size, bool Bold = false, bool Italic = false, bool Underline = false,
    bool DoubleUnderline = false, bool Strike = false, int Script = 0)
{
    public SKColor Color { get; init; } = SKColors.Black;
}

/// <summary>Excel's metrics of a font at a size, in 96 dpi pixels.</summary>
/// <param name="Ascent">Rounded ascent.</param>
/// <param name="Descent">Rounded descent.</param>
/// <param name="DigitWidth">The widest digit, truncated.</param>
internal readonly record struct PixelMetrics(int Ascent, int Descent, int DigitWidth, double SpaceWidth)
{
    /// <summary>The height of a line of this font in a cell (Excel's row height for it): ascent, descent and 2 px.</summary>
    public int LineHeight => Ascent + Descent + 2;
}

internal sealed class SheetFonts : IDisposable
{
    private readonly FontResolver _resolver;
    private readonly Shaper _shaper = new();
    private readonly Dictionary<(string Family, bool Bold, bool Italic), ResolvedFont?> _fonts = [];
    private readonly Dictionary<(string Family, bool Bold, bool Italic, double Size), PixelMetrics> _pixels = [];
    private readonly Dictionary<PageFont, FontMetrics> _metrics = new(ReferenceEqualityComparer.Instance);

    /// <summary>Digit and space widths and win ascent / descent (per 2048 units) of Excel's usual fonts, for when they aren't installed.</summary>
    private static readonly Dictionary<string, (int Digit, int Space, int Ascent, int Descent)> KnownFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Calibri"] = (1038, 463, 1950, 550),
        ["Arial"] = (1139, 569, 1854, 434),
        ["Times New Roman"] = (1024, 512, 1825, 443),
    };

    public SheetFonts(FontResolver? resolver = null) => _resolver = resolver ?? new FontResolver();

    public Shaper Shaper => _shaper;

    /// <summary>The font that draws a family (the family, a font for its script, or the language's default).</summary>
    public ResolvedFont? Font(string family, bool bold, bool italic)
    {
        var key = (family, bold, italic);
        if (!_fonts.TryGetValue(key, out var font))
            _fonts[key] = font = _resolver.Resolve(family, FontStyle.From(bold, italic));
        return font;
    }

    public FontMetrics Metrics(PageFont font)
    {
        if (!_metrics.TryGetValue(font, out var metrics))
            _metrics[font] = metrics = _shaper.Metrics(font);
        return metrics;
    }

    /// <summary>Excel's pixel metrics of a font at <paramref name="size"/> points.</summary>
    public PixelMetrics Pixels(string family, double size, bool bold = false, bool italic = false)
    {
        var key = (family, bold, italic, size);
        if (_pixels.TryGetValue(key, out var cached))
            return cached;
        var pixelSize = Math.Max(1, Math.Round(size * 96 / 72, MidpointRounding.AwayFromZero));
        PixelMetrics result;
        var installed = _resolver.Find(family, FontStyle.From(bold, italic));
        if (installed is null && KnownFonts.TryGetValue(family, out var known))
        {
            result = new PixelMetrics((int)Math.Round(known.Ascent * pixelSize / 2048), (int)Math.Round(known.Descent * pixelSize / 2048),
                (int)Math.Floor(known.Digit * pixelSize / 2048), known.Space * pixelSize / 2048);
        }
        else if ((installed ?? Font(family, bold, italic)) is { } font)
        {
            var metrics = Metrics(font.Font);
            var em = (double)metrics.UnitsPerEm;
            var ascent = metrics.WinAscent > 0 ? metrics.WinAscent : metrics.Ascender;
            var descent = metrics.WinDescent > 0 ? metrics.WinDescent : -metrics.Descender;
            var digit = 0;
            for (var d = '0'; d <= '9'; d++)
                digit = Math.Max(digit, _shaper.Shape(font.Font, d.ToString()).Advance);
            result = new PixelMetrics((int)Math.Round(ascent * pixelSize / em), (int)Math.Round(descent * pixelSize / em),
                (int)Math.Floor(digit * pixelSize / em), _shaper.Shape(font.Font, " ").Advance * pixelSize / em);
        }
        else
        {
            // No font at all (a system without fonts): Calibri's numbers.
            result = new PixelMetrics((int)Math.Round(1950 * pixelSize / 2048), (int)Math.Round(550 * pixelSize / 2048), (int)Math.Floor(1038 * pixelSize / 2048), 463 * pixelSize / 2048);
        }
        result = result with { DigitWidth = Math.Max(1, result.DigitWidth) };
        return _pixels[key] = result;
    }

    /// <summary>Pieces of <c>text[start..start+length]</c> that each draw with one font (FontResolver.Fallback).</summary>
    public List<FontRun> Fallback(string text, int start, int length, ResolvedFont primary, FontStyle style) =>
        _resolver.Fallback(text, start, length, primary, style);

    public void Dispose() => _shaper.Dispose();
}

/// <summary>
/// Text in one or more styles, shaped and measured at 100 % (points): what a cell shows, broken into lines and drawn.
/// </summary>
internal sealed class StyledText
{
    private readonly SheetFonts _fonts;
    private readonly List<Piece> _pieces = [];

    /// <summary>Advance of each UTF-16 index (the whole cluster on its first index, 0 on the others), in points.</summary>
    private readonly float[] _advances;

    private LineBreak[]? _breaks;

    /// <summary>A piece drawn with one font in one style.</summary>
    private sealed record Piece(int Start, int Length, TextStyle Style, ResolvedFont Font, float Size, float Ascent, float Descent, float LineHeight)
    {
        public int End => Start + Length;
    }

    public StyledText(SheetFonts fonts, string text, IReadOnlyList<(int Start, int Length, TextStyle Style)> spans)
    {
        _fonts = fonts;
        Text = text;
        _advances = new float[text.Length];
        foreach (var (start, length, style) in spans)
        {
            if (length <= 0)
                continue;
            var size = (float)(style.Script != 0 ? style.Size * 0.65 : style.Size);
            var primary = fonts.Font(style.Family, style.Bold, style.Italic);
            if (primary is null)
                continue;
            var pixels = fonts.Pixels(style.Family, style.Size, style.Bold, style.Italic);
            var runs = SafeFallback(fonts, text, start, length, primary, style);
            foreach (var run in runs)
            {
                var piece = new Piece(run.Start, run.Length, style, run.Font, size, pixels.Ascent * 0.75f, pixels.Descent * 0.75f, pixels.LineHeight * 0.75f);
                _pieces.Add(piece);
                var shaped = fonts.Shaper.Shape(run.Font.Font, text, run.Start, run.Length);
                var scale = size / shaped.UnitsPerEm;
                for (var g = 0; g < shaped.Glyphs.Length; g++)
                {
                    var index = run.Start + Math.Clamp(shaped.Clusters[g], 0, run.Length - 1);
                    _advances[index] += shaped.XAdvances[g] * scale;
                }
            }
        }
    }

    /// <summary>Where lines may break (only wrapped text asks).</summary>
    private LineBreak[] Breaks => _breaks ??= LineBreaker.Breaks(Text);

    private static List<FontRun> SafeFallback(SheetFonts fonts, string text, int start, int length, ResolvedFont primary, TextStyle style) =>
        fonts.Fallback(text, start, length, primary, FontStyle.From(style.Bold, style.Italic));

    public string Text { get; }

    public bool IsEmpty => _pieces.Count == 0;

    /// <summary>Width of <c>Text[start..end]</c> in points at 100 %.</summary>
    public float Width(int start, int end)
    {
        var width = 0f;
        for (var i = Math.Max(0, start); i < Math.Min(end, _advances.Length); i++)
            width += _advances[i];
        return width;
    }

    /// <summary>
    /// The characters of <c>Text[start..end]</c> drawn from <paramref name="x"/> that reach into
    /// <paramref name="left"/>..<paramref name="right"/>, and where the first of them starts (from <paramref name="x"/>).
    /// </summary>
    public (int Start, int End, float Offset) Visible(int start, int end, float x, float scale, float spaceExtra, float left, float right)
    {
        var from = start;
        var position = x;
        while (from < end && position + Advance(from, scale, spaceExtra) <= left)
            position += Advance(from++, scale, spaceExtra);
        while (from > start && char.IsLowSurrogate(Text[from]))
            position -= Advance(--from, scale, spaceExtra);
        var offset = position - x;
        var to = from;
        while (to < end && position < right)
            position += Advance(to++, scale, spaceExtra);
        while (to < end && (char.IsLowSurrogate(Text[to]) || _advances[to] == 0))
            to++;
        return (from, to, offset);
    }

    private float Advance(int index, float scale, float spaceExtra) => _advances[index] * scale + (Text[index] == ' ' ? spaceExtra : 0);

    /// <summary>Width of the whole text on one line (line breaks ignored: the widest line).</summary>
    public float SingleLineWidth() => Lines(float.MaxValue, wrap: false).Select(l => Width(l.Start, l.End)).DefaultIfEmpty(0).Max();

    /// <summary>
    /// The lines of the text: at its line feeds, and with <paramref name="wrap"/> where it may break so a line fits
    /// <paramref name="width"/> points (a word wider than that is broken between characters). Spaces at a wrapped
    /// line's end are left out.
    /// </summary>
    public List<(int Start, int End)> Lines(float width, bool wrap)
    {
        var lines = new List<(int, int)>();
        var start = 0;
        while (start <= Text.Length)
        {
            var hard = Text.IndexOf('\n', start);
            var end = hard < 0 ? Text.Length : hard;
            if (!wrap)
                lines.Add((start, TrimEnd(start, end, carriageOnly: true)));
            else
                Wrap(start, end, width, lines);
            if (hard < 0)
                break;
            start = hard + 1;
        }
        return lines;
    }

    private void Wrap(int start, int end, float width, List<(int, int)> lines)
    {
        if (start >= end)
        {
            lines.Add((start, start));
            return;
        }
        var lineStart = start;
        while (lineStart < end)
        {
            var used = 0f;
            var lastBreak = -1;
            var i = lineStart;
            for (; i < end; i++)
            {
                if (i > lineStart && Breaks[i] != LineBreak.None)
                    lastBreak = i;
                if (used + _advances[i] > width + 0.01f && !char.IsWhiteSpace(Text[i]) && i > lineStart)
                    break;
                used += _advances[i];
            }
            if (i >= end)
            {
                lines.Add((lineStart, TrimEnd(lineStart, end)));
                return;
            }
            var cut = lastBreak > lineStart ? lastBreak : i;
            lines.Add((lineStart, TrimEnd(lineStart, cut)));
            lineStart = cut;
            while (lineStart < end && Text[lineStart] == ' ')
                lineStart++;
        }
    }

    private int TrimEnd(int start, int end, bool carriageOnly = false)
    {
        while (end > start && (Text[end - 1] == '\r' || !carriageOnly && Text[end - 1] == ' '))
            end--;
        return end;
    }

    /// <summary>Ascent, descent and line height (Excel's: rounded pixels) of the tallest style on the line, in points at 100 %.</summary>
    public (float Ascent, float Descent, float LineHeight) LineMetrics(int start, int end)
    {
        float ascent = 0, descent = 0, height = 0;
        foreach (var piece in _pieces)
        {
            if (piece.End <= start && !(start == end && piece.Start <= start && start <= piece.End) || piece.Start >= Math.Max(end, start + 1))
                continue;
            ascent = Math.Max(ascent, piece.Ascent);
            descent = Math.Max(descent, piece.Descent);
            height = Math.Max(height, piece.LineHeight);
        }
        if (height == 0 && _pieces.Count > 0)
            (ascent, descent, height) = (_pieces[0].Ascent, _pieces[0].Descent, _pieces[0].LineHeight);
        return (ascent, descent, height);
    }

    /// <summary>
    /// Draws <c>Text[start..end]</c> on <paramref name="page"/> with the pen at (<paramref name="x"/>,
    /// <paramref name="baseline"/>), scaled by <paramref name="scale"/>; <paramref name="spaceExtra"/> points are added at
    /// each space (justified lines). Coordinates are in <paramref name="transform"/>'s frame when one is given.
    /// </summary>
    public float Draw(PageContent page, int start, int end, float x, float baseline, float scale, SKMatrix? transform = null, float spaceExtra = 0,
        SKRect? clip = null)
    {
        foreach (var piece in _pieces.OrderBy(p => p.Start))
        {
            var from = Math.Max(start, piece.Start);
            var to = Math.Min(end, piece.End);
            if (from >= to)
                continue;
            var size = piece.Size * scale;
            var rise = piece.Style.Script > 0 ? -piece.Ascent * 0.35f * scale : piece.Style.Script < 0 ? piece.Ascent * 0.15f * scale : 0;
            var x0 = x;
            // Words one by one when spaces are widened; else the piece in one run.
            var segments = spaceExtra > 0 ? Words(from, to) : [(from, to)];
            foreach (var (s, e) in segments)
            {
                if (s > from)
                    x = x0 + (Width(from, s) * scale) + spaceExtra * CountSpaces(from, s);
                var shaped = _fonts.Shaper.Shape(piece.Font.Font, Text, s, e - s);
                var run = shaped.ToGlyphRun(piece.Font.Font, size, new SKPoint(x, baseline + rise), Text.Substring(s, e - s)) with
                {
                    Color = piece.Style.Color,
                    Embolden = piece.Font.SyntheticBold,
                    SkewX = piece.Font.SyntheticItalic ? GlyphRun.SyntheticSlant : 0,
                    Transform = transform,
                    Clip = clip,
                };
                if (run.Glyphs.Length > 0)
                    page.DrawGlyphs(run);
                x += shaped.Width(size);
            }
            var x1 = x0 + Width(from, to) * scale + spaceExtra * CountSpaces(from, to);
            Decorate(page, piece, x0, x1, baseline, scale, transform, clip);
            x = x1;
        }
        return x;
    }

    private List<(int, int)> Words(int from, int to)
    {
        var words = new List<(int, int)>();
        var s = from;
        for (var i = from; i <= to; i++)
        {
            if (i == to || Text[i] == ' ')
            {
                if (i > s)
                    words.Add((s, i));
                s = i + 1;
            }
        }
        return words;
    }

    private int CountSpaces(int from, int to)
    {
        var count = 0;
        for (var i = from; i < to; i++)
        {
            if (Text[i] == ' ')
                count++;
        }
        return count;
    }

    private void Decorate(PageContent page, Piece piece, float x0, float x1, float baseline, float scale, SKMatrix? transform, SKRect? clip)
    {
        var style = piece.Style;
        if (!style.Underline && !style.DoubleUnderline && !style.Strike || x1 <= x0)
            return;
        var metrics = _fonts.Metrics(piece.Font.Font);
        var unit = piece.Size * scale / metrics.UnitsPerEm;
        var thickness = Math.Max(0.4f, (metrics.UnderlineThickness > 0 ? metrics.UnderlineThickness : metrics.UnitsPerEm / 14f) * unit);
        var under = baseline - (metrics.UnderlinePosition != 0 ? metrics.UnderlinePosition : -metrics.UnitsPerEm / 10) * unit;
        var strike = baseline - (metrics.StrikeoutPosition > 0 ? metrics.StrikeoutPosition : metrics.XHeight / 2f) * unit;
        var color = style.Color;
        page.Draw(canvas =>
        {
            if (clip is { } area)
                canvas.ClipRect(area);
            if (transform is { } m)
                canvas.Concat(in m);
            using var paint = new SKPaint { Color = color, IsAntialias = true, StrokeWidth = thickness, Style = SKPaintStyle.Stroke };
            if (style.Underline || style.DoubleUnderline)
                canvas.DrawLine(x0, under, x1, under, paint);
            if (style.DoubleUnderline)
                canvas.DrawLine(x0, under + thickness * 2, x1, under + thickness * 2, paint);
            if (style.Strike)
                canvas.DrawLine(x0, strike, x1, strike, paint);
        });
    }
}
