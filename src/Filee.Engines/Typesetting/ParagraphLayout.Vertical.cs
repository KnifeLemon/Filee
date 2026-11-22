// Paragraphs of text written vertically (a section written top to bottom, PageComposer; a table cell, text box, header
// or note written so, TableLayout.Stack with its flow), laid out across before VerticalText turns them into place.
//
// - Objects laid out like characters (pictures, shapes, text boxes) stand upright on the page, as Word and 한글 keep
//   them: on the line their height runs along it and their width across it, and they are drawn turned back a quarter
//   about their middle. Equations lie with the text. In East Asian vertical and stacked text they stand in the middle
//   of the line, as its upright characters do (centred on the em box's middle of the line's largest text, the
//   paragraph mark's on a line without text), so one wider than the em reaches out on both sides of the characters and
//   the line grows on both sides to hold it, inside its cell or text box; in text turned as a whole (Word btLr) they
//   stand on the baseline like the turned text. Not compared with a Word or 한글 PDF.
// - Numbers set across (縦中横, see VerticalText): in East Asian vertical text two or three ASCII digits on their own,
//   and in vertical text a run the document marks so (HCharLayout.Across), take one em along the line, their digits
//   centred in it (VerticalText squeezes them to the em where they are wider). No auto-spacing goes between them and the
//   East Asian text beside them: they are set as one upright character, as Word sets "第12回".

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Typesetting;

internal sealed partial class ParagraphLayouter
{
    /// <summary>
    /// How the text being laid out runs on the page (the section's HSection.TextFlow while the page composer lays out a
    /// body written vertically, a cell's or text box's own while TableLayout stacks it); Horizontal for text across.
    /// </summary>
    public HTextFlow TextFlow { get; set; }

    /// <summary>The text is written vertically: objects laid out like characters stand upright.</summary>
    private bool UprightObjects => TextFlow is HTextFlow.Vertical or HTextFlow.Stacked or HTextFlow.Up or HTextFlow.Down;

    /// <summary>A run in <paramref name="style"/> is a number set across, one em along the line.</summary>
    private bool SetAcross(TextStyle style) => style.Across && TextFlow is HTextFlow.Vertical or HTextFlow.Stacked;

    /// <summary>
    /// In East Asian vertical text, the items with the numbers VerticalText sets across (two or three ASCII digits with
    /// no letter or digit beside them) split off with <see cref="TextStyle.Across"/> set; the items as they are otherwise.
    /// </summary>
    private List<Item> AcrossNumbers(string text, List<Item> items)
    {
        if (TextFlow != HTextFlow.Vertical)
            return items;
        var numbers = new List<(int Start, int End)>();
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
            if (end - i is 2 or 3 && (i == 0 || !char.IsAsciiLetterOrDigit(text[i - 1])) && (end == text.Length || !char.IsAsciiLetterOrDigit(text[end])))
                numbers.Add((i, end));
            i = end;
        }
        if (numbers.Count == 0)
            return items;
        var result = new List<Item>(items.Count + 2 * numbers.Count);
        var next = 0;
        foreach (var item in items)
        {
            if (item.Kind != ItemKind.Text || item.Style.Across)
            {
                result.Add(item);
                continue;
            }
            while (next < numbers.Count && numbers[next].End <= item.Start)
                next++;
            var at = item.Start;
            for (var k = next; k < numbers.Count && numbers[k].Start < item.End; k++)
            {
                var (from, to) = (Math.Max(numbers[k].Start, at), Math.Min(numbers[k].End, item.End));
                if (from > at)
                    result.Add(item with { Start = at, Length = from - at });
                result.Add(item with { Start = from, Length = to - from, Style = item.Style with { Across = true } });
                at = to;
            }
            if (at < item.End)
                result.Add(item with { Start = at, Length = item.End - at });
        }
        return result;
    }

    /// <summary>
    /// The advances of a number set across (clusters <paramref name="from"/> to <paramref name="to"/> of
    /// <paramref name="advances"/>, letter spacing in <paramref name="spacing"/> when given) made one em together: the
    /// spacing left out, the room put on its last cluster.
    /// </summary>
    private static void OneEm(float[] advances, float[]? spacing, bool[] starts, int from, int to, float em)
    {
        var (sum, last) = (0f, -1);
        for (var i = from; i < to; i++)
        {
            if (!starts[i])
                continue;
            if (spacing is not null)
            {
                advances[i] -= spacing[i];
                spacing[i] = 0;
            }
            sum += advances[i];
            last = i;
        }
        if (last >= 0)
            advances[last] += em - sum;
    }

    /// <summary>
    /// In East Asian vertical and stacked text, the line's upright objects (not equations, not an equation's piece)
    /// centred across the line on the em box's middle of its largest text (the paragraph mark's on a line without text):
    /// their height above the baseline and depth below it, which may be less than 0, made so.
    /// </summary>
    private void CentreUpright(List<Part> parts, TextStyle mark, ParagraphText source)
    {
        if (TextFlow is not (HTextFlow.Vertical or HTextFlow.Stacked) || !parts.Any(p => p.Item.Kind == ItemKind.Object))
            return;
        var em = 0f;
        foreach (var part in parts)
        {
            if (part.Item.Kind == ItemKind.Text)
                em = Math.Max(em, part.Item.Style.Size);
        }
        var middle = VerticalText.EmBoxMiddle * (em > 0 ? em : mark.Size);
        foreach (var part in parts)
        {
            if (part.Item.Kind != ItemKind.Object || part.Edge is not null || source.Objects.GetValueOrDefault(part.Start) is null or HEquation)
                continue;
            var across = part.Size.Height + part.Size.Depth;
            part.Size = (part.Size.Width, middle + across / 2, across / 2 - middle);
        }
    }

    /// <summary>
    /// <paramref name="draw"/> (an object drawn into its area) for text written vertically: drawn at its own size into
    /// the area with its sides swapped, then turned a quarter back about the area's middle, so it stands upright once the
    /// text is turned into place. Its own content (a text box's) is laid out across.
    /// </summary>
    private Action<PageContent, SKRect> Upright(Action<PageContent, SKRect> draw)
    {
        var degrees = TextFlow == HTextFlow.Up ? 90 : -90;
        return (page, area) =>
        {
            var upright = SKRect.Create(area.MidX - area.Height / 2, area.MidY - area.Width / 2, area.Height, area.Width);
            var layer = new PageContent(page.Width, page.Height);
            var flow = TextFlow;
            TextFlow = HTextFlow.Horizontal;
            try
            {
                draw(layer, upright);
            }
            finally
            {
                TextFlow = flow;
            }
            page.Add(layer, SKMatrix.CreateRotationDegrees(degrees, area.MidX, area.MidY));
        };
    }
}
