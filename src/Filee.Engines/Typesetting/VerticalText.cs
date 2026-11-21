// Vertical text in table cells and text boxes (documents), in sections written vertically (PageComposer) and in shapes
// and table cells (slides). The text is laid out across, with lines as long as the box is high, then turned into place:
// 90° clockwise (top to bottom, the next line to the left: Word tbRl, 한글 세로쓰기, DrawingML vert / eaVert) or 270°
// (bottom to top, the next line to the right: Word btLr, DrawingML vert270).
//
// East Asian vertical writing (HTextFlow.Vertical) stands the CJK characters upright, as Word, 한글 and PowerPoint set
// them: Han, kana, Hangul and the other wide and fullwidth characters are each turned back about the middle of their
// em square (the ideographic em box, 0.88 em above the baseline to 0.12 em below). Latin and other text lies on its
// side. Stacked text (wordArtVert, 한글 영문 세움) stands every character upright.
//
// - Vertical forms: an upright character takes the font's vertical alternate (OpenType vert, or vrt2 where the font
//   has only that; Shaper.Vertical shapes it downwards with HarfBuzz), so the ideographic comma and full stop sit in
//   the top-right corner, brackets open downwards and the prolonged sound mark runs down. A font without them: the
//   comma and full stop move to the corner, and brackets, dashes, the ellipsis and the prolonged sound mark stay
//   turned, which is the shape of their vertical forms.
// - Vertical metrics: where the font has them (vmtx, VORG) an upright glyph is placed by them in its cell; else about
//   the em box's middle.
// - Numbers set across (縦中横, tate-chu-yoko): two or three ASCII digits on their own (not part of a longer number or
//   a word) in East Asian vertical text stand across in one character's room, squeezed to an em where they are wider,
//   as Japanese and Korean vertical typesetting sets dates and numbers; so does a run the document marks so (Word
//   w:eastAsianLayout w:vert, GlyphRun.Across). The layout gives them one em of the line, without auto-spacing beside
//   them (ParagraphLayout.Vertical.cs), in sections, table cells, text boxes, headers and notes written vertically;
//   slides' shapes and cells keep the room their layout across gave them.
//
// Upright glyphs get their own GlyphRun with their own slice of the text, so PDF text extraction stays one-to-one.
//
// Not done: upright Latin advancing by its height rather than its width.

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
    /// names turned back upright, each about the middle of its em square, in its vertical form; numbers set across
    /// where they stand on their own; the rest as it was. <paramref name="shaper"/> shapes the vertical forms (one of its
    /// own when null).
    /// </summary>
    public static PageContent Stand(PageContent layer, UprightGlyphs which, Shaper? shaper = null)
    {
        if (which == UprightGlyphs.None)
            return layer;
        var own = shaper is null ? new Shaper() : null;
        try
        {
            var result = new PageContent(layer.Width, layer.Height);
            var items = layer.Items;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i] is GlyphItem { Run: { Transform: null } run } && run.Glyphs.Length > 0)
                {
                    // The characters on either side, for numbers that stand on their own.
                    var before = i > 0 && items[i - 1] is GlyphItem { Run.Text: [.., var last] } ? last : ' ';
                    var after = i + 1 < items.Count && items[i + 1] is GlyphItem { Run.Text: [var next, ..] } ? next : ' ';
                    foreach (var part in Split(run, which, shaper ?? own, before, after))
                        result.DrawGlyphs(part);
                }
                else
                {
                    result.Add(items[i]);
                }
            }
            return result;
        }
        finally
        {
            own?.Dispose();
        }
    }

    /// <summary>
    /// A run split into runs of glyphs that lie on their side, single upright glyphs and numbers set across, each with
    /// its own part of the text; the run itself when none stands upright or its clusters don't run forward
    /// (right-to-left text). <paramref name="before"/> and <paramref name="after"/> are the characters next to the run;
    /// without <paramref name="shaper"/> upright glyphs keep their horizontal forms.
    /// </summary>
    internal static IEnumerable<GlyphRun> Split(GlyphRun run, UprightGlyphs which, Shaper? shaper = null, char before = ' ', char after = ' ')
    {
        var count = run.Glyphs.Length;
        if (which == UprightGlyphs.None || count == 0)
            return [run];
        for (var i = 1; i < count; i++)
        {
            if (run.Clusters[i] < run.Clusters[i - 1])
                return [run];
        }
        var across = Across(run, which, before, after);
        var upright = new bool[count];
        var forms = new VerticalGlyph?[count];
        var any = across.Any(a => a);
        for (var i = 0; i < count; i++)
        {
            var cluster = run.Clusters[i];
            if (across[i] || cluster >= run.Text.Length)
                continue;
            var codePoint = UnicodeData.CodePointAt(run.Text, cluster);
            var stands = Stands(codePoint, which);
            if (!stands && !IsTurnedForm(codePoint))
                continue;
            // A cluster of one glyph takes its vertical form; a turned form stands only when the font has one.
            var single = (i == 0 || run.Clusters[i - 1] != cluster) && (i + 1 == count || run.Clusters[i + 1] != cluster);
            var form = single && shaper is not null ? shaper.Vertical(run.Font, ClusterText(run, i)) : null;
            if (!stands && form?.Substituted != true)
                continue;
            upright[i] = true;
            forms[i] = form;
            any = true;
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
            if (across[start])
            {
                // A number set across: its digits together.
                while (end < count && across[end])
                    end++;
                parts.Add(SetAcross(run, start, end, widths));
                start = end;
                continue;
            }
            // Glyphs of one cluster go together; sideways glyphs run on until the next upright or across one.
            while (end < count && (run.Clusters[end] == run.Clusters[start] || !upright[start] && !upright[end] && !across[end]))
                end++;
            var part = Part(run, start, end);
            if (upright[start])
                part = Standing(run, part, start, widths[start], forms[start]);
            parts.Add(part);
            start = end;
        }
        return parts;
    }

    /// <summary>An upright glyph turned back about the middle of its em square, in its vertical form where it has one.</summary>
    private static GlyphRun Standing(GlyphRun run, GlyphRun part, int index, float width, VerticalGlyph? form)
    {
        var origin = run.Positions[index];
        var middle = new SKPoint(origin.X + width / 2, origin.Y - EmBoxMiddle * run.Size);
        var turn = SKMatrix.CreateRotationDegrees(-90, middle.X, middle.Y);
        var substituted = form?.Substituted == true;
        if (form is { } shaped)
        {
            if (substituted)
                part = part with { Glyphs = [shaped.Glyph] };
            if (shaped.Metrics)
            {
                // Placed by the font's vertical metrics: the pen at the top of the cell (as long as the glyph's vertical
                // advance, about the middle), HarfBuzz's offsets to the glyph's origin (y up).
                var scale = run.Size / shaped.UnitsPerEm;
                var top = middle.Y - shaped.Advance * scale / 2;
                part = part with { Positions = [new SKPoint(middle.X + shaped.XOffset * scale * run.ScaleX, top - shaped.YOffset * scale)] };
            }
        }
        if (!substituted && IsCornerPunctuation(UnicodeData.CodePointAt(run.Text, run.Clusters[index])))
        {
            // No vertical form: up and right on the page is left and up before the text is turned clockwise.
            var shift = PunctuationShift * run.Size;
            turn = SKMatrix.CreateTranslation(-shift, -shift).PreConcat(turn);
        }
        return part with { Transform = turn };
    }

    /// <summary>
    /// Glyphs <paramref name="start"/> up to <paramref name="end"/> set across in the room they take along the line,
    /// standing upright about its middle, squeezed to an em where they are wider.
    /// </summary>
    private static GlyphRun SetAcross(GlyphRun run, int start, int end, float[] widths)
    {
        var part = Part(run, start, end);
        var left = run.Positions[start].X;
        var right = Enumerable.Range(start, end - start).Max(i => run.Positions[i].X + widths[i]);
        var width = Math.Max(0.01f, right - left);
        var squeeze = Math.Min(1, run.Size / width);
        var middle = new SKPoint((left + right) / 2, run.Positions[start].Y - EmBoxMiddle * run.Size);
        // Upright about the middle, the baseline stays where it is: the em box's middle is the cell's.
        var positions = new SKPoint[end - start];
        for (var i = start; i < end; i++)
            positions[i - start] = new SKPoint(middle.X + (run.Positions[i].X - left - width / 2) * squeeze, run.Positions[i].Y);
        return part with
        {
            Positions = positions,
            ScaleX = run.ScaleX * squeeze,
            Transform = SKMatrix.CreateRotationDegrees(-90, middle.X, middle.Y),
        };
    }

    /// <summary>
    /// Which glyphs of the run are set across: all of a run marked so (GlyphRun.Across), else in East Asian vertical
    /// text two or three ASCII digits that stand on their own (no letter or digit just before or after them).
    /// </summary>
    private static bool[] Across(GlyphRun run, UprightGlyphs which, char before, char after)
    {
        var count = run.Glyphs.Length;
        var across = new bool[count];
        if (run.Across)
        {
            Array.Fill(across, true);
            return across;
        }
        if (which != UprightGlyphs.EastAsian)
            return across;
        var text = run.Text;
        static bool Joins(char c) => char.IsAsciiLetterOrDigit(c);
        for (var i = 0; i < text.Length;)
        {
            if (!char.IsAsciiDigit(text[i]))
            {
                i++;
                continue;
            }
            var end = i;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
                end++;
            var alone = !Joins(i > 0 ? text[i - 1] : before) && !Joins(end < text.Length ? text[end] : after);
            if (alone && end - i is 2 or 3)
            {
                for (var g = 0; g < count; g++)
                    across[g] |= run.Clusters[g] >= i && run.Clusters[g] < end;
            }
            i = end;
        }
        return across;
    }

    /// <summary>The text of glyph <paramref name="index"/>'s cluster.</summary>
    private static string ClusterText(GlyphRun run, int index)
    {
        var from = run.Clusters[index];
        var to = run.Text.Length;
        for (var i = index + 1; i < run.Glyphs.Length; i++)
        {
            if (run.Clusters[i] != from)
            {
                to = run.Clusters[i];
                break;
            }
        }
        return run.Text[from..Math.Max(from, to)];
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
            Across = false,
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
    /// wave dash and the prolonged sound mark. They stand upright in their vertical forms where the font has them.
    /// </summary>
    private static bool IsTurnedForm(int codePoint) => codePoint is >= 0x3008 and <= 0x3011 or >= 0x3014 and <= 0x301F
        or 0x2014 or 0x2015 or 0x2025 or 0x2026 or 0x30A0 or 0x30FC or 0xFF08 or 0xFF09 or 0xFF0D or 0xFF1C or 0xFF1E
        or 0xFF3B or 0xFF3D or 0xFF3F or 0xFF5B or 0xFF5C or 0xFF5D or 0xFF5E or 0xFF5F or 0xFF60 or 0xFFE3;

    /// <summary>The ideographic comma and full stop (and their fullwidth Latin forms), set in the top-right corner.</summary>
    private static bool IsCornerPunctuation(int codePoint) => codePoint is 0x3001 or 0x3002 or 0xFF0C or 0xFF0E;
}
