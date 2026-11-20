// Vertical text in table cells and text boxes (documents) and in shapes and table cells (slides). The text is laid
// out across, with lines as long as the box is high, then turned into place: 90° clockwise (top to bottom, the next
// line to the left: Word tbRl, 한글 세로쓰기, DrawingML vert / eaVert) or 270° (bottom to top, the next line to the
// right: Word btLr, DrawingML vert270).
//
// East Asian vertical writing (HTextFlow.Vertical) stands the CJK characters upright, as Word, 한글 and PowerPoint set
// them: Han, kana, Hangul and the other wide and fullwidth characters are each turned back about the middle of their
// em square (the ideographic em box, 0.88 em above the baseline to 0.12 em below), and the ideographic comma and full
// stop move to the top-right corner of theirs. Brackets, dashes, the ellipsis and the prolonged sound mark stay turned,
// which is the shape of their vertical forms. Latin and other text lies on its side. Stacked text (wordArtVert, 한글
// 영문 세움) stands every character upright.
//
// Not done: the font's vertical alternates (vert / vrt2) and vertical metrics, numbers set across (tate-chu-yoko),
// upright Latin advancing by its height rather than its width.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Typesetting;

/// <summary>Which glyphs of vertical text stand upright.</summary>
internal enum UprightGlyphs
{
    /// <summary>None: the text is turned as a whole.</summary>
    None,

    /// <summary>CJK characters (East Asian vertical writing).</summary>
    EastAsian,

    /// <summary>Every character (stacked text).</summary>
    All,
}

/// <summary>Turns text laid out across into vertical text.</summary>
internal static class VerticalText
{
    /// <summary>Where the ideographic em box's middle is above the baseline, in em.</summary>
    private const float EmBoxMiddle = 0.38f;

    /// <summary>How far the ideographic comma and full stop move up and right in vertical text, in em.</summary>
    private const float PunctuationShift = 0.6f;

    /// <summary>True when <paramref name="flow"/> runs from bottom to top (lines turned 270°).</summary>
    public static bool Upward(HTextFlow flow) => flow == HTextFlow.Up;

    /// <summary>The glyphs <paramref name="flow"/> stands upright.</summary>
    public static UprightGlyphs Upright(HTextFlow flow) => flow switch
    {
        HTextFlow.Vertical => UprightGlyphs.EastAsian,
        HTextFlow.Stacked => UprightGlyphs.All,
        _ => UprightGlyphs.None,
    };

    /// <summary>
    /// The matrix that turns a layer laid out across (lines along x from 0, the first line at the top) into
    /// <paramref name="box"/>: 90° clockwise with the first line at the box's right, or 270° (<paramref name="upward"/>)
    /// with it at the box's left.
    /// </summary>
    public static SKMatrix Place(bool upward, SKRect box) => upward
        ? new SKMatrix(0, 1, box.Left, -1, 0, box.Bottom, 0, 0, 1)
        : new SKMatrix(0, -1, box.Right, 1, 0, box.Top, 0, 0, 1);

    /// <summary>
    /// One piece, <paramref name="length"/> high, that draws <paramref name="pieces"/> (stacked <paramref name="length"/>
    /// wide) turned by <paramref name="flow"/> into a box <paramref name="width"/> wide. <paramref name="align"/> places
    /// the lines across the box as a cell's or text box's vertical alignment does in vertical text: top is where the
    /// first line starts (the right for top-to-bottom text, the left for bottom-to-top), bottom the far side.
    /// </summary>
    public static Piece Turned(IReadOnlyList<Piece> pieces, HTextFlow flow, float width, float length, HVerticalAlign align)
    {
        var thickness = pieces.Count == 0 ? 0 : pieces.Max(p => p.Top + p.Height - p.Below);
        var room = Math.Max(0, width - thickness);
        var offset = align switch
        {
            HVerticalAlign.Center => room / 2,
            HVerticalAlign.Bottom => room,
            _ => 0f,
        };
        var upward = Upward(flow);
        return new Piece(0, length, (page, x, y) =>
        {
            var layer = new PageContent(page.Width, page.Height);
            foreach (var piece in pieces)
                piece.Draw(layer, 0, piece.Top);
            var box = upward
                ? new SKRect(x + offset, y, x + offset + thickness, y + length)
                : new SKRect(x + width - offset - thickness, y, x + width - offset, y + length);
            page.Add(Stand(layer, Upright(flow)), Place(upward, box));
        });
    }

    /// <summary>
    /// <paramref name="layer"/> (text laid out across, to be turned 90° clockwise) with the glyphs <paramref name="which"/>
    /// names turned back upright, each about the middle of its em square; the rest as it was.
    /// </summary>
    public static PageContent Stand(PageContent layer, UprightGlyphs which)
    {
        if (which == UprightGlyphs.None)
            return layer;
        var result = new PageContent(layer.Width, layer.Height);
        foreach (var item in layer.Items)
        {
            if (item is GlyphItem { Run: { Transform: null } run } && run.Glyphs.Length > 0)
            {
                foreach (var part in Split(run, which))
                    result.DrawGlyphs(part);
            }
            else
            {
                result.Add(item);
            }
        }
        return result;
    }

    /// <summary>
    /// A run split into runs of glyphs that lie on their side and single upright glyphs, each with its own part of the
    /// text; the run itself when none stands upright or its clusters don't run forward (right-to-left text).
    /// </summary>
    internal static IEnumerable<GlyphRun> Split(GlyphRun run, UprightGlyphs which)
    {
        var count = run.Glyphs.Length;
        var upright = new bool[count];
        var any = false;
        for (var i = 0; i < count; i++)
        {
            if (i > 0 && run.Clusters[i] < run.Clusters[i - 1])
                return [run];
            var cluster = run.Clusters[i];
            upright[i] = cluster < run.Text.Length && Stands(UnicodeData.CodePointAt(run.Text, cluster), which);
            any |= upright[i];
        }
        if (!any)
            return [run];

        using var font = new SKFont(run.Font.Typeface, run.Size) { ScaleX = run.ScaleX, LinearMetrics = true, Subpixel = true };
        var widths = font.GetGlyphWidths(run.Glyphs);
        var parts = new List<GlyphRun>();
        var start = 0;
        while (start < count)
        {
            var end = start + 1;
            // Glyphs of one cluster go together; sideways glyphs run on until the next upright one.
            while (end < count && (run.Clusters[end] == run.Clusters[start] || !upright[start] && !upright[end]))
                end++;
            var part = Part(run, start, end);
            if (upright[start])
            {
                var origin = run.Positions[start];
                var middle = new SKPoint(origin.X + widths[start] / 2, origin.Y - EmBoxMiddle * run.Size);
                var turn = SKMatrix.CreateRotationDegrees(-90, middle.X, middle.Y);
                if (IsCornerPunctuation(UnicodeData.CodePointAt(run.Text, run.Clusters[start])))
                {
                    // Up and right on the page is left and up before the text is turned clockwise.
                    var shift = PunctuationShift * run.Size;
                    turn = SKMatrix.CreateTranslation(-shift, -shift).PreConcat(turn);
                }
                part = part with { Transform = turn };
            }
            parts.Add(part);
            start = end;
        }
        return parts;
    }

    /// <summary>Glyphs <paramref name="start"/> up to <paramref name="end"/> of a forward run, with their text.</summary>
    private static GlyphRun Part(GlyphRun run, int start, int end)
    {
        var from = run.Clusters[start];
        var to = end < run.Glyphs.Length ? run.Clusters[end] : run.Text.Length;
        return run with
        {
            Glyphs = run.Glyphs[start..end],
            Positions = run.Positions[start..end],
            Text = run.Text[from..to],
            Clusters = [.. run.Clusters[start..end].Select(c => c - from)],
        };
    }

    /// <summary>Whether a character stands upright in vertical text that stands <paramref name="which"/> upright.</summary>
    internal static bool Stands(int codePoint, UprightGlyphs which)
    {
        if (which == UprightGlyphs.None || codePoint is ' ' or '　' || char.IsControl((char)Math.Min(codePoint, 0xFFFF)))
            return false;
        if (which == UprightGlyphs.All)
            return true;
        if (IsTurnedForm(codePoint))
            return false;
        return UnicodeData.IsWide(codePoint) || UnicodeData.GetScript(codePoint) is UnicodeScript.Han or UnicodeScript.Hangul
            or UnicodeScript.Hiragana or UnicodeScript.Katakana or UnicodeScript.KatakanaOrHiragana or UnicodeScript.Bopomofo
            or UnicodeScript.Yi;
    }

    /// <summary>
    /// Wide characters whose vertical form is the horizontal one turned: brackets, dashes and lines, the ellipsis, the
    /// wave dash and the prolonged sound mark.
    /// </summary>
    private static bool IsTurnedForm(int codePoint) => codePoint is >= 0x3008 and <= 0x3011 or >= 0x3014 and <= 0x301F
        or 0x2014 or 0x2015 or 0x2025 or 0x2026 or 0x30A0 or 0x30FC or 0xFF08 or 0xFF09 or 0xFF0D or 0xFF1C or 0xFF1E
        or 0xFF3B or 0xFF3D or 0xFF3F or 0xFF5B or 0xFF5C or 0xFF5D or 0xFF5E or 0xFF5F or 0xFF60 or 0xFFE3;

    /// <summary>The ideographic comma and full stop (and their fullwidth Latin forms), set in the top-right corner.</summary>
    private static bool IsCornerPunctuation(int codePoint) => codePoint is 0x3001 or 0x3002 or 0xFF0C or 0xFF0E;
}
