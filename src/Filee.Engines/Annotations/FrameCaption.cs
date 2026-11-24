// The export of marked video frames (#1205): under each marked frame's picture, never over it, the frame's time and
// number (bold) and the note typed with it (regular), read from the copy's own text (FrameTags). The text is laid out
// as Annotate's labels are (LabelText: shaped, with the UI's fonts and their fallbacks), each line wrapped to the room
// it has, right-aligned when it reads right to left. A PNG gets it as a white strip below the picture, sized to the
// picture's width so it reads the same on a phone's frame and a 4K one; a PDF page draws the same lines as outlines at
// a size on paper (PdfSharpConverter), not as pixels.

using System.Globalization;
using Filee.Core.Presets;
using ImageMagick;
using SkiaSharp;

namespace Filee.Engines.Annotations;

internal static class FrameCaption
{
    /// <summary>
    /// The text's height is the picture's width divided by this, and never below <see cref="MinSize"/>.
    /// </summary>
    private const float WidthPerSize = 64;

    private const float MinSize = 14;

    /// <summary>A line's height, in text heights.</summary>
    internal const float LineHeight = 1.45f;

    /// <summary>The time and number are this much larger than the note.</summary>
    internal const float LabelScale = 1.1f;

    private static readonly SKColor Paper = SKColors.White;
    internal static readonly SKColor LabelInk = new(0x1C, 0x1C, 0x1C);
    internal static readonly SKColor NoteInk = new(0x3A, 0x3A, 0x3A);

    /// <summary>One line of the caption: the time and number (bold) or a part of the note, and its direction.</summary>
    internal sealed record Line(string Text, float Size, bool Bold, bool RightToLeft);

    /// <summary>
    /// The caption of <paramref name="facts"/> in lines no wider than <paramref name="room"/>, the note's text
    /// <paramref name="size"/> high: the time and number first, then the note (none when it is blank).
    /// </summary>
    internal static List<Line> Lines(FrameFacts facts, string frameWord, float size, float room)
    {
        var label = FrameTags.Label(facts, frameWord, CultureInfo.CurrentCulture);
        var labelSize = size * LabelScale;
        var labelRtl = AnnotationPainter.IsRightToLeft(label);
        var lines = Wrap(label, labelSize, room, bold: true).Select(text => new Line(text, labelSize, true, labelRtl)).ToList();
        if (!string.IsNullOrWhiteSpace(facts.Note))
        {
            var noteRtl = AnnotationPainter.IsRightToLeft(facts.Note);
            lines.AddRange(Wrap(facts.Note.Trim(), size, room, bold: false).Select(text => new Line(text, size, false, noteRtl)));
        }
        return lines;
    }

    /// <summary>How tall <paramref name="lines"/> are.</summary>
    internal static float Height(List<Line> lines) => lines.Sum(line => line.Size * LineHeight);

    /// <summary>
    /// Adds the strip below <paramref name="image"/> for <paramref name="facts"/>, the marked frame's text it carried
    /// (read before <see cref="Magick.ImageEncoder.ApplyOptions"/> drops it), when
    /// <see cref="ImageOptions.FrameCaption"/> is set; without either, does nothing.
    /// </summary>
    public static void Apply(IMagickImage<byte> image, ImageOptions options, FrameFacts? facts)
    {
        if (options.FrameCaption is not { } frameWord || facts is null)
            return;
        int width = (int)image.Width, height = (int)image.Height;
        var size = Math.Max(MinSize, width / WidthPerSize);
        var margin = size;
        var lines = Lines(facts, frameWord, size, Math.Max(size, width - 2 * margin));
        var strip = (int)Math.Ceiling(2 * margin + Height(lines));

        using var layer = Paint(width, strip, margin, lines);
        image.BackgroundColor = MagickColors.White;
        image.Extent(new MagickGeometry((uint)width, (uint)(height + strip)), Gravity.North);
        image.Composite(layer, 0, height, CompositeOperator.Over);
    }

    /// <summary>
    /// <paramref name="text"/> in lines no wider than <paramref name="room"/>: broken between words, and within a word
    /// (Chinese and Japanese have no spaces) only by whole characters.
    /// </summary>
    internal static List<string> Wrap(string text, float size, float room, bool bold = false)
    {
        var lines = new List<string>();
        if (text.Length == 0)
            return lines;
        var line = "";
        foreach (var word in text.Split(' '))
        {
            var tried = line.Length == 0 ? word : line + " " + word;
            if (Width(tried, size, bold) <= room)
            {
                line = tried;
                continue;
            }
            if (line.Length > 0)
                lines.Add(line);
            line = "";
            // A word wider than the line goes a character at a time.
            var characters = StringInfo.GetTextElementEnumerator(word);
            while (characters.MoveNext())
            {
                var next = line + characters.GetTextElement();
                if (line.Length > 0 && Width(next, size, bold) > room)
                {
                    lines.Add(line);
                    next = characters.GetTextElement();
                }
                line = next;
            }
        }
        if (line.Length > 0)
            lines.Add(line);
        return lines;
    }

    internal static float Width(string text, float size, bool bold) => LabelText.Lay(text, size, null, 0, 0, bold);

    /// <summary>
    /// The strip: white, its lines from the left, or from the right for a line that reads right to left.
    /// </summary>
    private static MagickImage Paint(int width, int height, float margin, List<Line> lines)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(Paper);
            var top = margin;
            foreach (var line in lines)
            {
                var x = line.RightToLeft ? width - margin - Width(line.Text, line.Size, line.Bold) : margin;
                AnnotationPainter.DrawText(canvas, line.Text, line.Size, x, top + line.Size * LineHeight / 2, centred: false,
                    line.Bold ? LabelInk : NoteInk, line.Bold);
                top += line.Size * LineHeight;
            }
        }
        return Annotator.ToMagick(bitmap);
    }
}
