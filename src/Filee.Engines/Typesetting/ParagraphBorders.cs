// Paragraph borders and backgrounds (GitHub Project 4, "Draw paragraph borders and backgrounds"): the box a paragraph
// draws around its lines, as Word (w:pBdr, w:shd) and 한글 (문단 테두리/배경) draw it.
//
// - Where the box goes: Word puts each line its distance (w:space) outside the text, so the text keeps its place and
//   the box reaches into the margin. 한글 puts the box at the paragraph's margins (the column's edges with "문단 여백
//   무시") and moves the text in by the line and its offset. Models read from other formats take Word's way; a block
//   quote's bar and background (HParaFormat.LeftBar / Fill, as the HWPX writer writes them) take 한글's.
// - Above and below: the top line and its distance go above the first line and the bottom ones below the last, inside
//   the paragraph's spacing. Lines on 한글's stored layout keep their stored places; their box goes around them.
// - Consecutive paragraphs with the same border and the same edges share one box (Word always, 한글 with "문단 테두리
//   연결"): no lines between them but Word's between line, and the background goes on through the spacing between them.
// - A box split by a page or column break is left open at the break: no bottom line before it, no top line after it.
//
// The box is drawn a line at a time (BorderSlice): each line draws its part of the background and the side lines, so a
// paragraph that splits, or columns that are balanced afterwards, need nothing more.

using Filee.Engines.Hwp.Hwpx;
using SkiaSharp;

namespace Filee.Engines.Typesetting;

/// <summary>A paragraph's border and background as laid out in its column (lengths in points).</summary>
/// <param name="Word">Word's way: the lines go outside the text. Else 한글's: the box sits at the margins and the text moves in.</param>
/// <param name="Left">The box's left edge (outside its line) from the column's left edge.</param>
/// <param name="Right">The box's right edge (outside its line) from the column's left edge.</param>
internal sealed record BoxBorder(HParaBorder Border, bool Word, float Left, float Right)
{
    /// <summary>
    /// The border a paragraph draws, or null: its own (HParaLayout.Border), else a block quote's bar and background or
    /// the borders of a model of another format, as the HWPX writer writes them.
    /// </summary>
    /// <param name="word">The paragraph's own border is Word's (a model of another format).</param>
    /// <param name="width">The column's width the paragraph is laid out in.</param>
    public static BoxBorder? Of(HParaFormat format, bool word, float width, bool rightToLeft = false)
    {
        var border = Visible(format, ref word);
        if (border is null)
            return null;
        // The text's edges: the leftmost of the first line and the others, and the right margin.
        var start = Math.Min(format.Left ?? 0, (format.Left ?? 0) + (format.FirstLine ?? 0)) / 100f;
        var end = (format.Right ?? 0) / 100f;
        var (left, right) = rightToLeft ? (end, start) : (start, end);
        var sides = border.Borders;
        if (word)
        {
            // Each line its distance outside the text.
            return new BoxBorder(border, true,
                left - (sides.Left.Style == HBorderStyle.None ? 0 : Thickness(sides.Left) + border.Offsets.Left / 100f),
                width - right + (sides.Right.Style == HBorderStyle.None ? 0 : Thickness(sides.Right) + border.Offsets.Right / 100f));
        }
        return border.IgnoreMargin
            ? new BoxBorder(border, false, 0, width)
            : new BoxBorder(border, false, left, width - right);
    }

    /// <summary>How far 한글's box moves the text in from the margins (HWPUNIT): the side's line and its offset; none in Word's way.</summary>
    public static (int Left, int Right) TextInset(HParaFormat format, bool word)
    {
        if (Visible(format, ref word) is not { } border || word)
            return (0, 0);
        return ((int)Math.Round(Thickness(border.Borders.Left) * 100) + border.Offsets.Left,
            (int)Math.Round(Thickness(border.Borders.Right) * 100) + border.Offsets.Right);
    }

    private static HParaBorder? Visible(HParaFormat format, ref bool word)
    {
        if (format.Layout?.Border is { IsVisible: true } own)
            return own;
        word = false;
        // As the HWPX writer writes them: 2 mm between the text and a quote's bar or the edge of its background, and a
        // box of Word borders 4 mm from the text at its sides.
        if (format.LeftBar is not null || format.Fill is not null)
            return new HParaBorder(HBorders.Empty with { Left = format.LeftBar ?? HBorder.None }, format.Fill, new HInsets(567, 567, 0, 0), Connect: true);
        if (format.Borders is { } borders)
            return new HParaBorder(borders, null, new HInsets(borders.Left.Style == HBorderStyle.None ? 0 : 400,
                borders.Right.Style == HBorderStyle.None ? 0 : 400, 0, 0));
        return null;
    }

    /// <summary>A line's thickness in points (as table borders draw it); 0 for none.</summary>
    public static float Thickness(HBorder line) => line.Style == HBorderStyle.None ? 0 : Math.Max(0.25f, (float)(line.WidthMm * 72 / 25.4));

    /// <summary>Room above the first line of the box: the top line and its distance (한글's offset also without a line).</summary>
    public float Top => Room(Border.Borders.Top, Border.Offsets.Top);

    /// <summary>Room below the last line of the box.</summary>
    public float Bottom => Room(Border.Borders.Bottom, Border.Offsets.Bottom);

    /// <summary>Room above each paragraph of the box after the first: Word's between line and the top distance.</summary>
    public float Between => Border.Between is { Style: not HBorderStyle.None } between ? Thickness(between) + Border.Offsets.Top / 100f : 0;

    private float Room(HBorder line, int offset) => line.Style == HBorderStyle.None ? (Word ? 0 : offset / 100f) : Thickness(line) + offset / 100f;

    /// <summary>The next paragraph's border continues this box (and isn't a box of its own).</summary>
    public bool Joins(BoxBorder? next) =>
        next is not null && Word == next.Word && Border == next.Border && (Word || Border.Connect)
        && Math.Abs(Left - next.Left) < 0.01f && Math.Abs(Right - next.Right) < 0.01f;

    /// <summary>
    /// The paragraph's lines with the room its part of the box takes: above the first line when it
    /// <paramref name="opens"/> the box (or Word's between line when it continues one), below the last when it
    /// <paramref name="closes"/> it. Lines on 한글's stored layout keep their places and get none.
    /// </summary>
    public ParagraphBox Pad(ParagraphBox box, bool opens, bool closes)
    {
        var (above, below) = (opens ? Top : Between, closes ? Bottom : 0);
        if (box.StoredLines || box.Lines.Count == 0 || above <= 0 && below <= 0)
            return box;
        var lines = box.Lines.ToList();
        if (lines.Count == 1)
        {
            lines[0] = lines[0].Padded(above, below);
        }
        else
        {
            lines[0] = lines[0].Padded(above, 0);
            lines[^1] = lines[^1].Padded(0, below);
        }
        return box with { Lines = lines };
    }

    /// <summary>
    /// The part of the box a line of a paragraph laid out by <see cref="Pad"/> draws: <paramref name="first"/> and
    /// <paramref name="last"/> say whether the line is the paragraph's first or last, <paramref name="opens"/> and
    /// <paramref name="closes"/> whether the paragraph starts and ends the box.
    /// </summary>
    /// <param name="gap">The spacing between this line and the box's line above it, which the background goes on through.</param>
    public BorderSlice Slice(ParagraphBox box, bool first, bool last, bool opens, bool closes, float gap)
    {
        var continues = first && !opens;
        var (above, below) = box.StoredLines ? (first ? opens ? Top : Between : 0, last && closes ? Bottom : 0) : (0f, 0f);
        return new BorderSlice(this, continues ? gap : 0, above, below, first && opens, last && closes, continues && Between > 0);
    }
}

/// <summary>
/// The part of a paragraph's box one line draws: its background and side lines over the line's height, the spacing
/// above it inside the box (<paramref name="Gap"/>), and the top, bottom or between line where it has one.
/// </summary>
/// <param name="Above">Room for the box above the line that the line doesn't take (lines on 한글's stored layout).</param>
/// <param name="Below">Room for the box below the line that the line doesn't take.</param>
internal sealed record BorderSlice(BoxBorder Box, float Gap, float Above, float Below, bool TopLine, bool BottomLine, bool BetweenLine)
{
    /// <summary>How far a side line goes on into the next line's part (points).</summary>
    private const float SideOverlap = 0.5f;

    /// <summary>
    /// Draws the slice of a line drawn at <paramref name="top"/>, <paramref name="height"/> high, in a column whose left
    /// edge is <paramref name="x"/>. At a column's top the spacing above isn't drawn: the box broke there.
    /// </summary>
    public void Draw(SKCanvas canvas, float x, float top, float height, bool columnTop = false)
    {
        var box = Box.Border;
        var outer = top - Above;
        var upper = outer - (columnTop ? 0 : Gap);
        var lower = top + height + Below;
        var (left, right) = (x + Box.Left, x + Box.Right);
        if (right <= left || lower <= upper)
            return;
        if (ParagraphText.Color(box.Fill) is { } color)
        {
            // Without antialiasing, so the lines' parts meet without a seam.
            using var paint = new SKPaint { Style = SKPaintStyle.Fill, Color = color, IsAntialias = false };
            canvas.DrawRect(new SKRect(left, upper, right, lower), paint);
        }
        var sides = box.Borders;
        // The side lines reach a little into the parts above and below (drawn before and after this one, the background
        // too), so antialiasing leaves no lighter seam between them.
        var start = TopLine || columnTop ? upper : upper - SideOverlap;
        var end = BottomLine ? lower : lower + SideOverlap;
        if (sides.Left.Style != HBorderStyle.None)
        {
            var at = left + BoxBorder.Thickness(sides.Left) / 2;
            TableLayout.Line(canvas, sides.Left, at, start, at, end, phase: start);
        }
        if (sides.Right.Style != HBorderStyle.None)
        {
            var at = right - BoxBorder.Thickness(sides.Right) / 2;
            TableLayout.Line(canvas, sides.Right, at, start, at, end, phase: start);
        }
        if (TopLine && sides.Top.Style != HBorderStyle.None)
        {
            var at = outer + BoxBorder.Thickness(sides.Top) / 2;
            TableLayout.Line(canvas, sides.Top, left, at, right, at);
        }
        if (BetweenLine && box.Between is { } between)
        {
            var at = outer + BoxBorder.Thickness(between) / 2;
            TableLayout.Line(canvas, between, left, at, right, at);
        }
        if (BottomLine && sides.Bottom.Style != HBorderStyle.None)
        {
            var at = lower - BoxBorder.Thickness(sides.Bottom) / 2;
            TableLayout.Line(canvas, sides.Bottom, left, at, right, at);
        }
    }
}
