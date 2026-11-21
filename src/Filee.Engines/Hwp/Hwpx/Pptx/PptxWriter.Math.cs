// Equations on slides as PowerPoint keeps them: Office Math (m:oMath, m:oMathPara for a display equation) in a14:m
// among the runs of a text shape, the shape in an mc:AlternateContent whose choice needs Office 2010's drawing
// extensions (a14) and whose fallback is the same shape filled with a picture of its text, locked, with an empty
// paragraph, for readers without Office Math. The picture is the shape's paragraphs drawn by the slide renderer
// (equations by MathLayout) on a transparent ground, encoded straight into its part; a shape that can't be drawn falls
// back to its text with the equations' linear text. Titles and tables with equations have them in a14:m too, in an
// mc:AlternateContent: a title's fallback is the title placeholder filled with a picture of its text, as for a text
// shape; a table's is the same table whose cells with equations are filled with a picture of the cell (its fill and its
// text, drawn by the slide renderer) and hold an empty paragraph, the other cells as they are. Where a picture can't be
// drawn, the title or cell has the equations' linear text.

using System.Text;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Rendering;
using Filee.Engines.Slides;
using Filee.Engines.Typesetting;
using SkiaSharp;
using static Filee.Engines.Hwp.Hwpx.Pptx.PresentationPackage;

namespace Filee.Engines.Hwp.Hwpx.Pptx;

internal sealed partial class PptxWriter
{
    private const string McNamespace = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private const string A14Namespace = "http://schemas.microsoft.com/office/drawing/2010/main";
    private const string MathNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/math";

    /// <summary>The fallback pictures are drawn at this resolution.</summary>
    private const float FallbackDpi = 144;

    /// <summary>The fonts and shaper the fallback pictures are drawn with, made for the first one.</summary>
    private (FontResolver Fonts, Shaper Shaper)? _drawing;

    private int _mathPictures;

    /// <summary>An equation run as Office Math in a14:m, in the paragraph's size and the run's colour.</summary>
    private static string MathXml(Run run, HEquation equation, Para paragraph) => MathXml(run, equation, paragraph.Size, paragraph.Align, null);

    /// <summary>
    /// An equation run as Office Math in a14:m at <paramref name="size"/> (hundredths of a point), in
    /// <paramref name="color"/> when given (a table's header row), else the run's own.
    /// </summary>
    private static string MathXml(Run run, HEquation equation, int size, HAlign? paragraphAlign, string? color)
    {
        var fill = (color ?? (Color(equation.Format.Color) is { } own && own != "FFFFFF" ? own : null)) is { } rgb
            ? $"<a:solidFill><a:srgbClr val=\"{rgb}\"/></a:solidFill>" : "";
        var align = paragraphAlign is HAlign.Left or HAlign.Right ? paragraphAlign : null;
        return "<a14:m>" + OmmlWriter.WriteDrawing(equation.Math, run.Display, $" sz=\"{N(size)}\"", fill, align) + "</a14:m>";
    }

    /// <summary>
    /// Equations as Office Math (a14:m) in a title or table: <paramref name="write"/> writes it with them
    /// (<c>true</c>, the choice) and with their linear text (<c>false</c>, the fallback), both with the same shape id.
    /// </summary>
    private string WithMath(Func<bool, string> write)
    {
        var id = _shapeId;
        var choice = write(true);
        _shapeId = id; // the same shape again
        return Alternate(choice, write(false));
    }

    /// <summary>Content that needs Office 2010's drawing extensions (a14), and what readers without them show.</summary>
    private static string Alternate(string choice, string fallback) =>
        $"<mc:AlternateContent xmlns:mc=\"{McNamespace}\" xmlns:m=\"{MathNamespace}\">" +
        $"<mc:Choice xmlns:a14=\"{A14Namespace}\" Requires=\"a14\">{choice}</mc:Choice>" +
        $"<mc:Fallback>{fallback}</mc:Fallback></mc:AlternateContent>";

    /// <summary>
    /// A text shape with equations: the shape as written (<paramref name="shape"/>, its equations in a14:m) as the
    /// choice, and a picture of it (or its text with the equations' linear text) as the fallback.
    /// </summary>
    private string MathShape(string shape, List<Para> paragraphs, bool placeholder, long x, long y, long width, long height, string bodyPr)
    {
        var id = _shapeId;
        var name = placeholder ? "Content Placeholder" : "TextBox";
        var nonVisual = placeholder
            ? "<p:cNvSpPr><a:spLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\" noMove=\"1\" noResize=\"1\" noEditPoints=\"1\" noAdjustHandles=\"1\" noChangeArrowheads=\"1\" noChangeShapeType=\"1\" noTextEdit=\"1\"/></p:cNvSpPr><p:nvPr><p:ph idx=\"1\"/></p:nvPr>"
            : "<p:cNvSpPr txBox=\"1\"><a:spLocks noRot=\"1\" noChangeAspect=\"1\" noMove=\"1\" noResize=\"1\" noEditPoints=\"1\" noAdjustHandles=\"1\" noChangeArrowheads=\"1\" noChangeShapeType=\"1\" noTextEdit=\"1\"/></p:cNvSpPr><p:nvPr/>";
        string fallback;
        if (PicturePart(FallbackPicture([.. paragraphs.Select((p, i) => Drawn(p, first: i == 0))], width, height)) is { } relationship)
        {
            fallback = PictureShape(id, name, nonVisual, x, y, width, height, relationship, bodyPr);
        }
        else
        {
            var body = new StringBuilder("<p:txBody>").Append(bodyPr).Append("<a:lstStyle/>");
            for (var i = 0; i < paragraphs.Count; i++)
                body.Append(Paragraph(paragraphs[i], first: i == 0));
            body.Append("</p:txBody>");
            _shapeId = id - 1; // the same shape again
            fallback = placeholder
                ? TextShape(name, "<p:ph idx=\"1\"/>", x, y, width, height, body.ToString())
                : TextShape(name, null, x, y, width, height, body.ToString());
        }
        return Alternate(shape, fallback);
    }

    /// <summary>A shape filled with a picture of its text (its relationship), locked, with an empty paragraph.</summary>
    private static string PictureShape(int id, string name, string nonVisual, long x, long y, long width, long height, string relationship, string bodyPr) =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name} {id - 1}\"/>{nonVisual}</p:nvSpPr>" +
        $"<p:spPr>{Xfrm(x, y, width, height)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" +
        $"<a:blipFill><a:blip r:embed=\"{relationship}\"/><a:stretch><a:fillRect/></a:stretch></a:blipFill></p:spPr>" +
        $"<p:txBody>{bodyPr}<a:lstStyle/><a:p><a:r><a:rPr lang=\"en-US\"><a:noFill/></a:rPr><a:t> </a:t></a:r></a:p></p:txBody></p:sp>";

    /// <summary>A fallback picture written into its own part (encoded straight into it), its relationship id; null for none.</summary>
    private string? PicturePart(SKBitmap? picture)
    {
        if (picture is null)
            return null;
        using (picture)
        {
            var file = $"math{++_mathPictures}.png";
            using (var target = _package!.Create("ppt/media/" + file, "png", "image/png", compress: false))
                picture.Encode(target, SKEncodedImageFormat.Png, 100);
            return Relate("image", "../media/" + file);
        }
    }

    /// <summary>
    /// A title with equations: the title as written (<paramref name="write"/> <c>true</c>, its equations in a14:m) as
    /// the choice, and the title placeholder filled with a picture of its text as the fallback (else its text with the
    /// equations' linear text), both with the same shape id.
    /// </summary>
    private string MathTitle(Func<bool, string> write, List<Run> runs, long x, long y, long width, long height, int size, string anchor)
    {
        var before = _shapeId;
        var choice = write(true);
        var id = _shapeId;
        var para = new Para { Runs = runs, Size = size * TitleScale(runs, width, height, size) / 100, Before = 0 };
        var place = anchor switch { "b" => TextAnchor.Bottom, "ctr" => TextAnchor.Middle, _ => TextAnchor.Top };
        if (PicturePart(FallbackPicture([Drawn(para, first: true, font: "Calibri Light")], width, height, place)) is not { } relationship)
        {
            _shapeId = before; // the same shape again
            return Alternate(choice, write(false));
        }
        const string NonVisual = "<p:cNvSpPr><a:spLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\" noMove=\"1\" noResize=\"1\" noEditPoints=\"1\" noAdjustHandles=\"1\" noChangeArrowheads=\"1\" noChangeShapeType=\"1\" noTextEdit=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"title\"/></p:nvPr>";
        return Alternate(choice, PictureShape(id, "Title", NonVisual, x, y, width, height, relationship, $"<a:bodyPr anchor=\"{anchor}\"><a:normAutofit/></a:bodyPr>"));
    }

    /// <summary>
    /// A table cell's text as the slide renderer draws it in the fallback's picture of the cell: its size, bold header
    /// and colour, no space before its first paragraph.
    /// </summary>
    private List<HParagraph> CellText(List<Para> paragraphs, int size, bool bold, string? color) =>
        [.. paragraphs.Select((p, i) => Drawn(p with { Size = size, Bold = bold || p.Bold, Before = 0, Bullet = BulletKind.None, InList = false, Margin = 0 },
            first: i == 0, color: color))];

    /// <summary>How high a cell's text drawn <paramref name="width"/> wide is with its insets (EMU), as PowerPoint grows the row to it.</summary>
    private long CellHeight(List<HParagraph> text, long width)
    {
        var frame = Frame(text);
        _drawing ??= (new FontResolver(), new Shaper());
        using var renderer = new SlideRenderer(_drawing.Value.Fonts, _drawing.Value.Shaper, _token);
        return (long)Math.Ceiling(renderer.TextHeight(frame, (float)(width / Emu)) * Emu);
    }

    /// <summary>
    /// A table cell with equations drawn as the slide renderer draws the cell's text (<see cref="CellText"/>, its insets
    /// and anchor) on its fill, for the fallback table; its relationship id, null when it couldn't be drawn.
    /// </summary>
    private string? CellPicture(List<HParagraph> text, long width, long height, HVerticalAlign align, string fill)
    {
        var anchor = align switch { HVerticalAlign.Center => TextAnchor.Middle, HVerticalAlign.Bottom => TextAnchor.Bottom, _ => TextAnchor.Top };
        var ground = SKColor.TryParse("#" + fill, out var parsed) ? parsed : SKColors.White;
        return PicturePart(FallbackPicture(text, width, height, anchor, ground));
    }

    /// <summary>A text frame with a text box's insets holding <paramref name="paragraphs"/> at <paramref name="anchor"/>.</summary>
    private static SlideTextFrame Frame(List<HParagraph> paragraphs, TextAnchor anchor = TextAnchor.Top)
    {
        var frame = new SlideTextFrame
        {
            InsetLeft = (float)(InsetX / Emu),
            InsetRight = (float)(InsetX / Emu),
            InsetTop = (float)(InsetY / Emu),
            InsetBottom = (float)(InsetY / Emu),
            Anchor = anchor,
        };
        frame.Paragraphs.AddRange(paragraphs);
        return frame;
    }

    /// <summary>
    /// Text drawn as the slide renderer draws a text box (its insets, the paragraphs' sizes, indents, bullets and
    /// equations) at <paramref name="anchor"/>, on a transparent ground or <paramref name="ground"/>, for its PNG; null
    /// when nothing could be drawn.
    /// </summary>
    private SKBitmap? FallbackPicture(List<HParagraph> paragraphs, long width, long height, TextAnchor anchor = TextAnchor.Top, SKColor? ground = null)
    {
        var (w, h) = ((float)(width / Emu), (float)(height / Emu));
        if (!(w > 1 && h > 1))
            return null;
        var frame = Frame(paragraphs, anchor);
        var deck = new SlideDeck { Width = w, Height = h };
        var slide = new Slide();
        slide.Elements.Add(new SlideShape { Bounds = new SKRect(0, 0, w, h), Text = frame });
        deck.Slides.Add(slide);

        _drawing ??= (new FontResolver(), new Shaper());
        PageContent page;
        using (var renderer = new SlideRenderer(_drawing.Value.Fonts, _drawing.Value.Shaper, _token))
            page = renderer.Render(deck, slide, withBackground: false);
        var scale = Math.Min(FallbackDpi / 72f, 4000 / Math.Max(w, h));
        var bitmap = new SKBitmap(Math.Max(1, (int)Math.Ceiling(w * scale)), Math.Max(1, (int)Math.Ceiling(h * scale)), SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(ground ?? SKColors.Transparent);
            canvas.Scale(scale);
            page.Draw(canvas);
        }
        return bitmap;
    }

    /// <summary>
    /// A slide paragraph as the document model has it, for the renderer: its runs at its size, its bullet as text, in
    /// <paramref name="font"/> where a run names none, in <paramref name="color"/> when given (a table's header row).
    /// </summary>
    private HParagraph Drawn(Para paragraph, bool first, string font = "Calibri", string? color = null)
    {
        HCharFormat Look(HCharFormat format) => format with
        {
            Size = paragraph.Size,
            Bold = paragraph.Bold || format.Bold == true ? true : format.Bold,
            Font = paragraph.Code ? CodeFont : format.Font ?? font,
            Color = color is not null ? "#" + color : Color(format.Color) is { } own && own != "FFFFFF" ? "#" + own : null,
        };
        var marked = paragraph.Bullet != BulletKind.None;
        var result = new HParagraph
        {
            Format = new HParaFormat(
                Align: paragraph.Align,
                Left: (int)(paragraph.TextLeft / 127),
                FirstLine: marked ? -228600 / 127 : 0,
                Before: first ? 0 : paragraph.Before,
                After: 0,
                LineSpacing: new HLineSpacing(HLineSpacingKind.Percent, 120)),
            MarkFormat = Look(default),
        };
        if (marked)
        {
            var marker = paragraph.Bullet == BulletKind.Char ? "•\t" : paragraph.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".\t";
            result.Inlines.Add(new HText(marker, Look(default)));
        }
        foreach (var run in paragraph.Runs)
        {
            if (run.Break)
                result.Inlines.Add(new HLineBreak(Look(run.Format)));
            else if (run.Equation is { } equation)
                result.Inlines.Add(new HEquation(equation.Math, Look(equation.Format), equation.Text) { Display = run.Display, Tex = equation.Tex });
            else if (run.Text.Length > 0)
                result.Inlines.Add(new HText(run.Text, Look(run.Code ? run.Format with { Font = CodeFont } : run.Format)));
        }
        return result;
    }
}
