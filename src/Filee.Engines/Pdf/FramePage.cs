// A page of the PDF of a video's marked frames (#1205): the frame's picture as large as fits, under it the time and
// number (bold) and the note (regular), and the video's name small at the foot. The text is drawn as glyph outlines
// laid out as Annotate's labels are (LabelText: shaped, the UI's fonts and their fallbacks), so every script draws and
// no font is embedded; a glyph with no outline (a colour emoji) goes in as a small picture. The sizes are on paper, so
// the text reads the same under a phone's frame and a 4K one.

using Filee.Core.Presets;
using Filee.Engines.Annotations;
using PdfSharp.Drawing;
using SkiaSharp;

namespace Filee.Engines.Pdf;

internal static class FramePage
{
    private const double PointsPerMm = 72 / 25.4;

    /// <summary>
    /// The note's text height, in points; the time and number are <see cref="FrameCaption.LabelScale"/> larger.
    /// </summary>
    private const float NoteSize = 10;

    /// <summary>The footer's text height, in points, as the page number's (PdfFinishing).</summary>
    private const float FooterSize = 9;

    /// <summary>Between the picture and its caption.</summary>
    private const double Gap = 4 * PointsPerMm;

    /// <summary>Kept clear above the bottom margin for the footer (the page number and the video's name).</summary>
    internal const double FooterRoom = 6 * PointsPerMm;

    /// <summary>The caption is never narrower than this, under a narrow (portrait) picture.</summary>
    private const double MinRoom = 85 * PointsPerMm;

    private static readonly XColor FooterInk = XColor.FromArgb(96, 96, 96);

    /// <summary>Where a page's picture and caption go inside its box (the page less its margins and footer).</summary>
    internal sealed record Layout(XRect Picture, double CaptionLeft, double CaptionTop, double Room, List<FrameCaption.Line> Lines);

    /// <summary>
    /// The picture as large as fits in <paramref name="box"/> with its caption under it, the two centred together;
    /// the caption as wide as the picture, or <see cref="MinRoom"/> under a narrower one.
    /// </summary>
    internal static Layout Place(XRect box, double imageWidth, double imageHeight, FrameFacts facts, string frameWord)
    {
        // The caption's height depends on its width, which follows the picture's, whose height leaves room for the
        // caption: a few rounds settle it (each only narrows the caption), and the picture is always scaled for the
        // caption it ends with, so the caption never runs into the footer.
        var room = box.Width;
        var lines = FrameCaption.Lines(facts, frameWord, NoteSize, (float)room);
        double Scale(List<FrameCaption.Line> caption) =>
            Math.Max(0, Math.Min(box.Width / imageWidth, (box.Height - Gap - FrameCaption.Height(caption)) / imageHeight));
        for (var round = 0; round < 4; round++)
        {
            var narrower = Math.Min(box.Width, Math.Max(imageWidth * Scale(lines), MinRoom));
            if (narrower >= room)
                break;
            room = narrower;
            lines = FrameCaption.Lines(facts, frameWord, NoteSize, (float)room);
        }
        var scale = Scale(lines);
        var width = imageWidth * scale;
        var height = imageHeight * scale;
        var block = height + Gap + FrameCaption.Height(lines);
        var top = box.Top + Math.Max(0, (box.Height - block) / 2);
        var picture = new XRect(box.Left + (box.Width - width) / 2, top, width, height);
        return new(picture, box.Left + (box.Width - room) / 2, top + height + Gap, room, lines);
    }

    /// <summary>Draws the caption of <paramref name="layout"/>, and the video's name at the foot of the page.</summary>
    internal static void Draw(XGraphics graphics, Layout layout, FrameFacts facts, double pageWidth, double pageHeight, double margin)
    {
        var top = layout.CaptionTop;
        foreach (var line in layout.Lines)
        {
            var width = FrameCaption.Width(line.Text, line.Size, line.Bold);
            var x = line.RightToLeft ? layout.CaptionLeft + layout.Room - width : layout.CaptionLeft;
            var baseline = LabelText.BaselineFor(line.Text, line.Size, (float)top + line.Size * FrameCaption.LineHeight / 2);
            DrawText(graphics, line.Text, line.Size, line.Bold, (float)x, baseline, line.Bold ? FrameCaption.LabelInk : FrameCaption.NoteInk);
            top += line.Size * FrameCaption.LineHeight;
        }
        // The video's name, on the page number's line (10 mm above the bottom edge), at the margin it reads from.
        var name = facts.VideoName;
        var nameWidth = FrameCaption.Width(name, FooterSize, bold: false);
        var room = pageWidth / 2 - margin - 12 * PointsPerMm;
        if (room <= 0)
            return;
        var shown = nameWidth <= room ? name : Shortened(name, room);
        var rightToLeft = AnnotationPainter.IsRightToLeft(name);
        var left = rightToLeft ? pageWidth - margin - FrameCaption.Width(shown, FooterSize, false) : margin;
        DrawText(graphics, shown, FooterSize, false, (float)left, (float)(pageHeight - 10 * PointsPerMm),
            new SKColor(FooterInk.R, FooterInk.G, FooterInk.B));
    }

    /// <summary><paramref name="name"/> cut to fit <paramref name="room"/>, ending in "…".</summary>
    private static string Shortened(string name, double room)
    {
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(name);
        var kept = "";
        while (elements.MoveNext())
        {
            var next = kept + elements.GetTextElement();
            if (FrameCaption.Width(next + "…", FooterSize, false) > room)
                break;
            kept = next;
        }
        return kept + "…";
    }

    /// <summary>
    /// One line of text as outlines from <paramref name="x"/> on <paramref name="baseline"/>, in points.
    /// </summary>
    private static void DrawText(XGraphics graphics, string text, float size, bool bold, float x, float baseline, SKColor ink)
    {
        using var path = new SKPath { FillType = SKPathFillType.Winding };
        var pictures = new List<LabelGlyph>();
        LabelText.Outline(text, size, bold, path, pictures, x, baseline);
        if (!path.IsEmpty)
            graphics.DrawPath(new XSolidBrush(XColor.FromArgb(ink.Red, ink.Green, ink.Blue)), PdfFinishing.ToXPath(path));
        foreach (var glyph in pictures)
            DrawGlyphPicture(graphics, glyph);
    }

    /// <summary>Pixels per point of a glyph drawn as a picture: sharp in print (576 per inch).</summary>
    private const float GlyphScale = 8;

    /// <summary>A glyph with no outline (a colour emoji) as a small picture where it is laid out.</summary>
    private static void DrawGlyphPicture(XGraphics graphics, LabelGlyph glyph)
    {
        using var font = new SKFont(glyph.Face, glyph.Size * GlyphScale) { Embolden = glyph.Embolden, Edging = SKFontEdging.Antialias };
        var bounds = new SKRect[1];
        font.GetGlyphWidths([glyph.Glyph], [0f], bounds);
        var area = bounds[0];
        if (area.IsEmpty)
            return;
        int width = (int)Math.Ceiling(area.Width) + 2, height = (int)Math.Ceiling(area.Height) + 2;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var builder = new SKTextBlobBuilder();
            var run = builder.AllocatePositionedRun(font, 1);
            run.SetGlyphs([glyph.Glyph]);
            run.SetPositions([new SKPoint(1 - area.Left, 1 - area.Top)]);
            using var blob = builder.Build();
            using var paint = new SKPaint { IsAntialias = true };
            if (blob is not null)
                canvas.DrawText(blob, 0, 0, paint);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        using var image = XImage.FromStream(stream);
        graphics.DrawImage(image, new XRect(glyph.At.X + (area.Left - 1) / GlyphScale, glyph.At.Y + (area.Top - 1) / GlyphScale,
            width / GlyphScale, height / GlyphScale));
    }
}
