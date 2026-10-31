// Writes a PowerPoint presentation (PPTX) from the document model, so Markdown, the markup languages and every other
// format Filee reads (DocumentReaders) become slides without Pandoc. Written by hand on the package code the PDF
// picture decks use (PresentationPackage); the Open XML SDK only validates the output in the tests.
//
// Split over partial files:
//  * PptxWriter.cs          the package: presentation, slide master, layouts, theme, slides, pictures, notes;
//  * PptxWriter.Slides.cs   which blocks go on which slide (Pandoc's PowerPoint writer's rules);
//  * PptxWriter.Content.cs  slide content as items (text, code, tables, pictures), their estimated heights, and
//                           text that runs over continuing on the next slide.
//
// The deck is 16:9 (13.33 × 7.5 in) with PowerPoint's own "Title Slide", "Title and Content" and "Section Header"
// layouts and Office's theme. Every shape on a slide has its position and its paragraphs their indents, bullets and
// sizes written out, so a reader that does not follow the layouts (or a template applied later) still shows them.

using System.Globalization;
using System.Text;
using Filee.Engines.Magick;
using ImageMagick;
using static Filee.Engines.Hwp.Hwpx.Pptx.PresentationPackage;

namespace Filee.Engines.Hwp.Hwpx.Pptx;

/// <summary>Converts an <see cref="HDocument"/> into a .pptx deck.</summary>
internal sealed partial class PptxWriter
{
    private const string TableUri = "http://schemas.openxmlformats.org/drawingml/2006/table";

    /// <summary>Picture types written as they are; others become PNG.</summary>
    private static readonly Dictionary<string, string> PictureTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["bmp"] = "image/bmp",
        ["emf"] = "image/x-emf",
        ["wmf"] = "image/x-wmf",
    };

    /// <summary>Title of the slide that gathers the footnotes (Pandoc's default).</summary>
    internal const string EndNotesTitle = "Notes";

    private readonly HDocument _document;
    private readonly Dictionary<HNumbering, int?[]> _counters = new(ReferenceEqualityComparer.Instance);
    private readonly List<List<HBlock>> _endNotes = [];
    private readonly Dictionary<string, string?> _media = new(StringComparer.OrdinalIgnoreCase);
    private bool _writingEndNotes;
    private int _slides;
    private PresentationPackage? _package;

    private PptxWriter(HDocument document) => _document = document;

    /// <summary>Writes <paramref name="document"/> as a .pptx file (replaced atomically).</summary>
    public static void Write(HDocument document, string outputPath)
    {
        var writer = new PptxWriter(document);
        var pages = writer.Pages();
        using var package = new PresentationPackage(outputPath);
        writer._package = package;
        writer.WritePackage(pages);
        package.Save();
    }

    // ───────────────────────── Slides ─────────────────────────

    /// <summary>Every slide of the deck, the document's groups fitted onto slides, the footnotes' slide last.</summary>
    private List<Page> Pages()
    {
        var pages = new List<Page>();
        foreach (var source in Group(_document))
        {
            var notes = NotesText(source.Notes);
            switch (source.Kind)
            {
                case SlideKind.Title:
                    {
                        var page = new Page(SlideKind.Title);
                        page.Notes.AddRange(notes);
                        pages.Add(page);
                        break;
                    }
                case SlideKind.Section:
                    {
                        var page = new Page(SlideKind.Section) { Title = TitleRuns(source.Title!) };
                        page.Notes.AddRange(notes);
                        pages.Add(page);
                        break;
                    }
                default:
                    pages.AddRange(Paginate(SlideKind.Content, source.Title is null ? null : TitleRuns(source.Title), Items(source.Blocks), notes));
                    break;
            }
            _slides = pages.Count;
            CheckSlides(0);
        }

        if (_endNotes.Count > 0)
        {
            _writingEndNotes = true;
            var paragraphs = new List<Para>();
            for (var i = 0; i < _endNotes.Count; i++)
            {
                var first = true;
                foreach (var paragraph in _endNotes[i].OfType<HParagraph>())
                {
                    var runs = Runs(paragraph.Inlines, codeBlock: paragraph.Role == HBlockRole.Code);
                    if (runs.Count == 0)
                        continue;
                    paragraphs.Add(new Para
                    {
                        Runs = runs,
                        InList = true,
                        Bullet = first ? BulletKind.Number : BulletKind.None,
                        Number = i + 1,
                        Size = EndNoteSize,
                        Before = first ? 600 : 0,
                    });
                    first = false;
                }
            }
            if (paragraphs.Count > 0)
                pages.AddRange(Paginate(SlideKind.Content, [new Run(EndNotesTitle, default)], [new TextItem(paragraphs)], []));
        }
        if (pages.Count == 0)
            pages.Add(new Page(SlideKind.Content)); // a presentation needs a slide
        return pages;
    }

    /// <summary>The runs of a heading as a slide title.</summary>
    private List<Run> TitleRuns(HParagraph heading) =>
        Runs(heading.Inlines, codeBlock: false).Select(r => r with { Format = r.Format with { Bold = null, Size = null } }).ToList();

    /// <summary>Speaker notes as lines of text: a paragraph per line, a table row with its cells between tabs.</summary>
    private static List<string> NotesText(IEnumerable<HBlock> blocks)
    {
        var lines = new List<string>();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HParagraph paragraph:
                    var text = HDocumentWalker.Text(paragraph.Inlines);
                    if (text.Length > 0)
                        lines.Add(paragraph.List is { Numbered: true } ? "• " + text : text);
                    break;
                case HTable table:
                    foreach (var row in table.Rows)
                        lines.Add(string.Join("\t", row.Cells.Select(c => string.Join(" ", c.Blocks.OfType<HParagraph>().Select(p => HDocumentWalker.Text(p.Inlines))))));
                    break;
            }
        }
        return lines;
    }

    // ───────────────────────── Package ─────────────────────────

    private void WritePackage(List<Page> pages)
    {
        var package = _package!;
        var notes = 0;
        for (var i = 0; i < pages.Count; i++)
        {
            var hasNotes = pages[i].Notes.Count > 0;
            WriteSlide(pages[i], i + 1, hasNotes);
            if (hasNotes)
            {
                package.WriteNotesSlide(i + 1, pages[i].Notes);
                notes++;
            }
        }

        // Relationship ids: 1-5 fixed parts, 6 the notes master, slides from 7.
        var presentation = new StringBuilder($"<p:presentation {Namespaces} saveSubsetFonts=\"1\">")
            .Append("<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>");
        if (notes > 0)
            presentation.Append("<p:notesMasterIdLst><p:notesMasterId r:id=\"rId6\"/></p:notesMasterIdLst>");
        presentation.Append("<p:sldIdLst>");
        for (var i = 0; i < pages.Count; i++)
            presentation.Append($"<p:sldId id=\"{256 + i}\" r:id=\"rId{7 + i}\"/>");
        presentation.Append("</p:sldIdLst>")
            .Append($"<p:sldSz cx=\"{N(SlideWidth)}\" cy=\"{N(SlideHeight)}\"/>")
            .Append($"<p:notesSz cx=\"{N(NotesWidth)}\" cy=\"{N(NotesHeight)}\"/>")
            .Append("</p:presentation>");
        package.Write("ppt/presentation.xml", presentation.ToString(), PresentationTypes + "presentation.main+xml");

        var relationships = new List<(string, string, string)>
        {
            ("rId1", "slideMaster", "slideMasters/slideMaster1.xml"),
            ("rId2", "theme", "theme/theme1.xml"),
            ("rId3", "presProps", "presProps.xml"),
            ("rId4", "viewProps", "viewProps.xml"),
            ("rId5", "tableStyles", "tableStyles.xml"),
        };
        if (notes > 0)
            relationships.Add(("rId6", "notesMaster", "notesMasters/notesMaster1.xml"));
        for (var i = 0; i < pages.Count; i++)
            relationships.Add(($"rId{7 + i}", "slide", $"slides/slide{i + 1}.xml"));
        package.Write("ppt/_rels/presentation.xml.rels", Rels([.. relationships]));

        package.Write("ppt/slideMasters/slideMaster1.xml", SlideMaster(), PresentationTypes + "slideMaster+xml");
        package.Write("ppt/slideMasters/_rels/slideMaster1.xml.rels", Rels(
            ("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"),
            ("rId2", "slideLayout", "../slideLayouts/slideLayout2.xml"),
            ("rId3", "slideLayout", "../slideLayouts/slideLayout3.xml"),
            ("rId4", "theme", "../theme/theme1.xml")));
        string[] layouts = [TitleSlideLayout(), TitleAndContentLayout(), SectionHeaderLayout()];
        for (var i = 0; i < layouts.Length; i++)
        {
            package.Write($"ppt/slideLayouts/slideLayout{i + 1}.xml", layouts[i], PresentationTypes + "slideLayout+xml");
            package.Write($"ppt/slideLayouts/_rels/slideLayout{i + 1}.xml.rels", Rels(("rId1", "slideMaster", "../slideMasters/slideMaster1.xml")));
        }
        package.Write("ppt/theme/theme1.xml", Theme(), ThemeType);
        if (notes > 0)
            package.WriteNotesMaster(SlideWidth, SlideHeight);
        package.WriteCommonParts(_document.Title, _document.Authors, pages.Count, notes);
    }

    /// <summary>Relationships of the slide being written (rId1 is its layout).</summary>
    private readonly List<(string Id, string Type, string Target, bool External)> _relationships = [];

    private string Relate(string type, string target, bool external = false)
    {
        foreach (var rel in _relationships)
            if (rel.Type == type && rel.Target == target && rel.External == external)
                return rel.Id;
        var id = "rId" + (_relationships.Count + 1).ToString(CultureInfo.InvariantCulture);
        _relationships.Add((id, type, target, external));
        return id;
    }

    private int _shapeId;

    private void WriteSlide(Page page, int number, bool hasNotes)
    {
        _relationships.Clear();
        _shapeId = 1;
        var layout = page.Kind switch
        {
            SlideKind.Title => 1,
            SlideKind.Section => 3,
            _ => 2,
        };
        Relate("slideLayout", $"../slideLayouts/slideLayout{layout}.xml");

        var shapes = new StringBuilder();
        switch (page.Kind)
        {
            case SlideKind.Title:
                TitleSlide(shapes);
                break;
            case SlideKind.Section:
                shapes.Append(TextShape("Title", "<p:ph type=\"title\"/>", 831850, 1709738, 10515600, 2852737,
                    TitleBody(page.Title!, 10515600, 2852737, 6000, "b")));
                break;
            default:
                if (page.Title is { } title)
                    shapes.Append(TextShape("Title", "<p:ph type=\"title\"/>", TitleX, TitleY, TitleWidth, TitleHeight,
                        TitleBody(title, TitleWidth, TitleHeight, TitleSize, "ctr")));
                var single = page.Items is [{ Item: TextItem }];
                foreach (var placed in page.Items)
                    shapes.Append(Shape(page, placed, single));
                break;
        }
        if (hasNotes)
            Relate("notesSlide", $"../notesSlides/notesSlide{number}.xml");

        _package!.Write($"ppt/slides/slide{number}.xml",
            $"<p:sld {Namespaces}><p:cSld>{TreeStart}{shapes}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>",
            PresentationTypes + "slide+xml");
        _package.Write($"ppt/slides/_rels/slide{number}.xml.rels", Rels(_relationships));
    }

    /// <summary>The document's title, subtitle with the authors under it, and the date.</summary>
    private void TitleSlide(StringBuilder shapes)
    {
        if (!string.IsNullOrWhiteSpace(_document.Title))
            shapes.Append(TextShape("Title", "<p:ph type=\"ctrTitle\"/>", 1524000, 1122363, 9144000, 2387600,
                TitleBody([new Run(_document.Title.Trim(), default)], 9144000, 2387600, 6000, "b", center: true)));
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(_document.Subtitle))
            lines.Add(_document.Subtitle.Trim());
        lines.AddRange(_document.Authors);
        if (lines.Count > 0)
        {
            var body = new StringBuilder("<p:txBody><a:bodyPr><a:normAutofit/></a:bodyPr><a:lstStyle/>");
            foreach (var line in lines)
                body.Append(Paragraph(new Para { Runs = [new Run(line, default)], Size = 2400, Before = 0, Align = HAlign.Center }));
            shapes.Append(TextShape("Subtitle", "<p:ph type=\"subTitle\" idx=\"1\"/>", 1524000, 3602038, 9144000, 1655762, body.Append("</p:txBody>").ToString()));
        }
        if (!string.IsNullOrWhiteSpace(_document.Date))
            shapes.Append(TextShape("Date", "<p:ph type=\"dt\" sz=\"half\" idx=\"10\"/>", 838200, 6356350, 2743200, 365125,
                "<p:txBody><a:bodyPr/><a:lstStyle/>" + Paragraph(new Para { Runs = [new Run(_document.Date.Trim(), default)], Size = 1200, Before = 0 }) + "</p:txBody>"));
    }

    /// <summary>
    /// A title's text body: one paragraph without bullet. A title of more than two lines is shrunk to fit its box
    /// (normAutofit with the scale written out, as PowerPoint stores it).
    /// </summary>
    private string TitleBody(List<Run> runs, long width, long height, int size, string anchor, bool center = false)
    {
        var scale = 100;
        var para = new Para { Runs = runs, Size = size, Before = 0 };
        while (scale > 40 && LineStarts(para with { Size = size * scale / 100 }, width).Count * (long)(size * scale / 100 / 100.0 * LineHeight * Emu) > height - 2 * InsetY)
            scale -= 10;
        var fit = scale < 100 ? $"<a:normAutofit fontScale=\"{scale * 1000}\"/>" : "<a:normAutofit/>";
        var sb = new StringBuilder($"<p:txBody><a:bodyPr anchor=\"{anchor}\">{fit}</a:bodyPr><a:lstStyle/><a:p>");
        if (center)
            sb.Append("<a:pPr algn=\"ctr\"/>");
        foreach (var run in runs)
            sb.Append(RunXml(run, 0, null));
        return sb.Append("</a:p></p:txBody>").ToString();
    }

    /// <summary>A shape (a placeholder when <paramref name="placeholder"/> is set) at the given place with a text body.</summary>
    private string TextShape(string name, string? placeholder, long x, long y, long width, long height, string body, string geometry = "")
    {
        var id = ++_shapeId;
        var nonVisual = placeholder is null
            ? "<p:cNvSpPr txBox=\"1\"/><p:nvPr/>"
            : $"<p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr>{placeholder}</p:nvPr>";
        var shapeGeometry = placeholder is null ? "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" + (geometry.Length > 0 ? geometry : "<a:noFill/>") : geometry;
        return $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{Escape(name)} {id - 1}\"/>{nonVisual}</p:nvSpPr>" +
               $"<p:spPr>{Xfrm(x, y, width, height)}{shapeGeometry}</p:spPr>{body}</p:sp>";
    }

    private static string Xfrm(long x, long y, long width, long height) =>
        $"<a:xfrm><a:off x=\"{N(x)}\" y=\"{N(y)}\"/><a:ext cx=\"{N(Math.Max(1, width))}\" cy=\"{N(Math.Max(1, height))}\"/></a:xfrm>";

    /// <summary>One placed item: text in the content placeholder (when it is the slide's only item) or a text box, a code box, a table, a picture.</summary>
    private string Shape(Page page, Placed placed, bool single)
    {
        switch (placed.Item)
        {
            case TextItem text:
                {
                    var body = new StringBuilder($"<p:txBody><a:bodyPr wrap=\"square\" lIns=\"{N(InsetX)}\" tIns=\"{N(InsetY)}\" rIns=\"{N(InsetX)}\" bIns=\"{N(InsetY)}\" rtlCol=\"0\">")
                        .Append(single ? "<a:normAutofit/>" : "").Append("</a:bodyPr><a:lstStyle/>");
                    for (var i = 0; i < text.Paragraphs.Count; i++)
                        body.Append(Paragraph(text.Paragraphs[i], first: i == 0));
                    body.Append("</p:txBody>");
                    return single
                        ? TextShape("Content Placeholder", "<p:ph idx=\"1\"/>", BodyX, BodyY, BodyWidth, BodyHeight, body.ToString())
                        : TextShape("TextBox", null, placed.X, placed.Y, placed.Width, placed.Height, body.ToString());
                }
            case CodeItem code:
                {
                    var body = new StringBuilder($"<p:txBody><a:bodyPr wrap=\"square\" lIns=\"{N(CodePadding)}\" tIns=\"{N(CodePadding)}\" rIns=\"{N(CodePadding)}\" bIns=\"{N(CodePadding)}\" rtlCol=\"0\"/><a:lstStyle/>");
                    foreach (var line in code.Lines)
                        body.Append(Paragraph(line));
                    body.Append("</p:txBody>");
                    return TextShape("Code", null, placed.X, placed.Y, placed.Width, placed.Height, body.ToString(),
                        "<a:solidFill><a:srgbClr val=\"F2F2F2\"/></a:solidFill><a:ln w=\"6350\"><a:solidFill><a:srgbClr val=\"D9D9D9\"/></a:solidFill></a:ln>");
                }
            case PictureItem picture:
                return Picture(picture, placed);
            case TableItem table:
                return TableXml(table, placed, page.RowHeights.GetValueOrDefault(table) ?? RowHeights(table));
        }
        return "";
    }

    // ───────────────────────── Text ─────────────────────────

    /// <summary>A paragraph with its indent, bullet or number, spacing and runs, all written out.</summary>
    private string Paragraph(Para paragraph, bool first = false)
    {
        var sb = new StringBuilder("<a:p><a:pPr");
        var marked = paragraph.Bullet != BulletKind.None;
        sb.Append($" marL=\"{N(paragraph.TextLeft)}\"");
        if (paragraph.InList && paragraph.Level > 0)
            sb.Append($" lvl=\"{N(paragraph.Level)}\"");
        sb.Append($" indent=\"{(marked ? "-228600" : "0")}\"");
        if (paragraph.Align is { } align)
            sb.Append(align switch { HAlign.Center => " algn=\"ctr\"", HAlign.Right => " algn=\"r\"", _ => " algn=\"just\"" });
        sb.Append("><a:lnSpc><a:spcPct val=\"100000\"/></a:lnSpc>")
          .Append($"<a:spcBef><a:spcPts val=\"{N(first ? 0 : paragraph.Before)}\"/></a:spcBef>");
        sb.Append(paragraph.Bullet switch
        {
            BulletKind.Char => "<a:buFont typeface=\"Arial\"/><a:buChar char=\"•\"/>",
            BulletKind.Number => $"<a:buFont typeface=\"+mj-lt\"/><a:buAutoNum type=\"{paragraph.Scheme}\" startAt=\"{N(Math.Clamp(paragraph.Number, 1, 32767))}\"/>",
            _ => "<a:buNone/>",
        });
        sb.Append("</a:pPr>");
        foreach (var run in paragraph.Runs)
            sb.Append(RunXml(run, paragraph.Size, null, paragraph.Bold, paragraph.Code));
        sb.Append($"<a:endParaRPr sz=\"{N(paragraph.Size)}\" dirty=\"0\"/></a:p>");
        return sb.ToString();
    }

    /// <summary>A run (or line break) with its format; <paramref name="size"/> 0 keeps the placeholder's size.</summary>
    private string RunXml(Run run, int size, string? color, bool bold = false, bool code = false)
    {
        var format = run.Format;
        var properties = new StringBuilder("<a:rPr");
        if (size > 0)
            properties.Append($" sz=\"{N(size)}\"");
        if (bold || format.Bold == true)
            properties.Append(" b=\"1\"");
        if (format.Italic == true)
            properties.Append(" i=\"1\"");
        if (format.Underline == true)
            properties.Append(" u=\"sng\"");
        if (format.Strike == true)
            properties.Append(" strike=\"sngStrike\"");
        if (format.Superscript == true)
            properties.Append(" baseline=\"30000\"");
        else if (format.Subscript == true)
            properties.Append(" baseline=\"-25000\"");
        properties.Append(" dirty=\"0\">");
        // White text of the source is left out (slides are white); white given here (a header row) is kept.
        var fill = color ?? (Color(format.Color) is { } own && own != "FFFFFF" ? own : null);
        if (fill is not null)
            properties.Append($"<a:solidFill><a:srgbClr val=\"{fill}\"/></a:solidFill>");
        if (!run.Code && !code && Color(format.Shade) is { } shade && shade != "FFFFFF")
            properties.Append($"<a:highlight><a:srgbClr val=\"{shade}\"/></a:highlight>");
        if (run.Code || code)
            properties.Append($"<a:latin typeface=\"{CodeFont}\"/><a:cs typeface=\"{CodeFont}\"/>");
        if (run.Link is { } link && !run.Break)
            properties.Append($"<a:hlinkClick r:id=\"{Relate("hyperlink", link, external: true)}\"/>");
        properties.Append("</a:rPr>");

        if (run.Break)
            return $"<a:br>{properties}</a:br>";
        return run.Text.Length == 0 ? "" : $"<a:r>{properties}<a:t>{Escape(run.Text)}</a:t></a:r>";
    }

    // ───────────────────────── Pictures and tables ─────────────────────────

    private string Picture(PictureItem picture, Placed placed)
    {
        if (Media(picture.Path) is not { } target)
            return "";
        var id = ++_shapeId;
        var relationship = Relate("image", "../media/" + target);
        return $"<p:pic><p:nvPicPr><p:cNvPr id=\"{id}\" name=\"Picture {id - 1}\" descr=\"{Escape(picture.Name)}\"/>" +
               "<p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>" +
               $"<p:blipFill><a:blip r:embed=\"{relationship}\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>" +
               $"<p:spPr>{Xfrm(placed.X, placed.Y, placed.Width, placed.Height)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>";
    }

    /// <summary>
    /// The name of a picture in ppt/media, added to the package once: PNG, JPEG, GIF, BMP, EMF and WMF as they are,
    /// other formats (WEBP, TIFF, SVG, ...) converted to PNG. Null when it can't be read.
    /// </summary>
    private string? Media(string path)
    {
        if (_media.TryGetValue(path, out var known))
            return known;
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var convert = !PictureTypes.ContainsKey(extension);
        if (convert)
            extension = "png";
        var name = $"image{_media.Count + 1}.{extension}";
        string? result = name;
        try
        {
            if (convert)
            {
                using var image = new MagickImage(path);
                DdsChannelOrder.Correct(path, image);
                var bytes = image.ToByteArray(MagickFormat.Png);
                using var target = _package!.Create("ppt/media/" + name, extension, PictureTypes[extension], compress: false);
                target.Write(bytes);
            }
            else
            {
                using var input = File.OpenRead(path);
                using var target = _package!.Create("ppt/media/" + name, extension, PictureTypes[extension], compress: extension is "bmp" or "emf" or "wmf");
                input.CopyTo(target);
            }
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException)
        {
            result = null;
        }
        _media[path] = result;
        return result;
    }

    /// <summary>
    /// A table in PowerPoint's default look (Medium Style 2, accent 1) written cell by cell: a blue header row with
    /// white bold text, banded rows, white lines. A cell's own fill wins.
    /// </summary>
    private string TableXml(TableItem table, Placed placed, long[] heights)
    {
        var id = ++_shapeId;
        var sb = new StringBuilder($"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id}\" name=\"Table {id - 1}\"/>")
            .Append("<p:cNvGraphicFramePr><a:graphicFrameLocks noGrp=\"1\"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr>")
            .Append($"<p:xfrm><a:off x=\"{N(placed.X)}\" y=\"{N(placed.Y)}\"/><a:ext cx=\"{N(placed.Width)}\" cy=\"{N(Math.Max(1, placed.Height))}\"/></p:xfrm>")
            .Append($"<a:graphic><a:graphicData uri=\"{TableUri}\"><a:tbl><a:tblPr firstRow=\"{(table.HeaderRows > 0 ? 1 : 0)}\" bandRow=\"1\"/><a:tblGrid>");
        foreach (var width in table.Columns)
            sb.Append($"<a:gridCol w=\"{N(width)}\"/>");
        sb.Append("</a:tblGrid>");
        const string line = "<a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill>";
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var header = r < table.HeaderRows;
            var band = (table.FirstRow + r - table.HeaderRows) % 2 == 0;
            var rowFill = header ? "4472C4" : band ? "CFD5EA" : "E9EBF5";
            sb.Append($"<a:tr h=\"{N(r < heights.Length ? heights[r] : 370840)}\">");
            foreach (var cell in table.Rows[r])
            {
                sb.Append("<a:tc");
                if (cell.Paragraphs is not null && cell.ColSpan > 1)
                    sb.Append($" gridSpan=\"{N(cell.ColSpan)}\"");
                if (cell.Paragraphs is not null && cell.RowSpan > 1)
                    sb.Append($" rowSpan=\"{N(cell.RowSpan)}\"");
                if (cell.HMerge)
                    sb.Append(" hMerge=\"1\"");
                if (cell.VMerge)
                    sb.Append(" vMerge=\"1\"");
                sb.Append("><a:txBody><a:bodyPr/><a:lstStyle/>");
                var paragraphs = cell.Paragraphs ?? [];
                var textColor = header && cell.Fill is null ? "FFFFFF" : null;
                if (paragraphs.Count == 0)
                    sb.Append($"<a:p><a:endParaRPr sz=\"{N(table.Size)}\" dirty=\"0\"/></a:p>");
                foreach (var paragraph in paragraphs)
                {
                    sb.Append("<a:p><a:pPr marL=\"0\" indent=\"0\"");
                    if (paragraph.Align is HAlign.Center)
                        sb.Append(" algn=\"ctr\"");
                    else if (paragraph.Align is HAlign.Right)
                        sb.Append(" algn=\"r\"");
                    sb.Append("><a:buNone/></a:pPr>");
                    foreach (var run in paragraph.Runs)
                        sb.Append(RunXml(run, table.Size, textColor, bold: header || paragraph.Bold, code: paragraph.Code));
                    sb.Append($"<a:endParaRPr sz=\"{N(table.Size)}\" dirty=\"0\"/></a:p>");
                }
                sb.Append("</a:txBody><a:tcPr");
                sb.Append(cell.Align switch { HVerticalAlign.Center => " anchor=\"ctr\"", HVerticalAlign.Bottom => " anchor=\"b\"", _ => "" });
                sb.Append('>');
                foreach (var side in new[] { "lnL", "lnR", "lnT", "lnB" })
                    sb.Append($"<a:{side} w=\"12700\" cmpd=\"sng\">{line}</a:{side}>");
                sb.Append($"<a:solidFill><a:srgbClr val=\"{cell.Fill ?? rowFill}\"/></a:solidFill></a:tcPr></a:tc>");
            }
            sb.Append("</a:tr>");
        }
        return sb.Append("</a:tbl></a:graphicData></a:graphic></p:graphicFrame>").ToString();
    }

    // ───────────────────────── Master and layouts ─────────────────────────

    /// <summary>A placeholder of the master or a layout: position, text anchoring and its own level styles.</summary>
    private static string Placeholder(int id, string name, string ph, long x, long y, long width, long height, string bodyPr = "<a:bodyPr/>", string levels = "") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr>{ph}</p:nvPr></p:nvSpPr>" +
        $"<p:spPr>{Xfrm(x, y, width, height)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>" +
        $"<p:txBody>{bodyPr}<a:lstStyle>{levels}</a:lstStyle><a:p><a:endParaRPr lang=\"en-US\"/></a:p></p:txBody></p:sp>";

    /// <summary>White background, title / body / date placeholders and the text styles of PowerPoint's Office theme.</summary>
    private static string SlideMaster()
    {
        static string Font(string kind) => $"<a:latin typeface=\"+{kind}-lt\"/><a:ea typeface=\"+{kind}-ea\"/><a:cs typeface=\"+{kind}-cs\"/>";
        var body = new StringBuilder();
        for (var level = 0; level < 9; level++)
        {
            body.Append($"<a:lvl{level + 1}pPr marL=\"{N(228600 + level * 457200L)}\" indent=\"-228600\" algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\">")
                .Append($"<a:lnSpc><a:spcPct val=\"100000\"/></a:lnSpc><a:spcBef><a:spcPts val=\"{(level == 0 ? 1000 : 500)}\"/></a:spcBef>")
                .Append("<a:buFont typeface=\"Arial\"/><a:buChar char=\"•\"/>")
                .Append($"<a:defRPr sz=\"{N(LevelSizes[level])}\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill>{Font("mn")}</a:defRPr></a:lvl{level + 1}pPr>");
        }
        return $"<p:sldMaster {Namespaces}><p:cSld>" +
               "<p:bg><p:bgPr><a:solidFill><a:schemeClr val=\"bg1\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg>" +
               TreeStart +
               Placeholder(2, "Title Placeholder 1", "<p:ph type=\"title\"/>", TitleX, TitleY, TitleWidth, TitleHeight,
                   "<a:bodyPr vert=\"horz\" lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" rtlCol=\"0\" anchor=\"ctr\"><a:normAutofit/></a:bodyPr>") +
               Placeholder(3, "Text Placeholder 2", "<p:ph type=\"body\" idx=\"1\"/>", BodyX, BodyY, BodyWidth, BodyHeight,
                   "<a:bodyPr vert=\"horz\" lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" rtlCol=\"0\"><a:normAutofit/></a:bodyPr>") +
               Placeholder(4, "Date Placeholder 3", "<p:ph type=\"dt\" sz=\"half\" idx=\"2\"/>", 838200, 6356350, 2743200, 365125,
                   "<a:bodyPr vert=\"horz\" lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" rtlCol=\"0\" anchor=\"ctr\"/>",
                   "<a:lvl1pPr algn=\"l\"><a:defRPr sz=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"><a:tint val=\"75000\"/></a:schemeClr></a:solidFill></a:defRPr></a:lvl1pPr>") +
               $"</p:spTree></p:cSld>{ColorMap}" +
               "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/><p:sldLayoutId id=\"2147483650\" r:id=\"rId2\"/>" +
               "<p:sldLayoutId id=\"2147483651\" r:id=\"rId3\"/></p:sldLayoutIdLst>" +
               "<p:txStyles><p:titleStyle><a:lvl1pPr algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\">" +
               "<a:lnSpc><a:spcPct val=\"100000\"/></a:lnSpc><a:spcBef><a:spcPct val=\"0\"/></a:spcBef><a:buNone/>" +
               $"<a:defRPr sz=\"{N(TitleSize)}\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill>{Font("mj")}</a:defRPr></a:lvl1pPr></p:titleStyle>" +
               $"<p:bodyStyle>{body}</p:bodyStyle>" +
               "<p:otherStyle><a:defPPr><a:defRPr lang=\"en-US\"/></a:defPPr>" +
               $"<a:lvl1pPr marL=\"0\" algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\"><a:defRPr sz=\"1800\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill>{Font("mn")}</a:defRPr></a:lvl1pPr>" +
               "</p:otherStyle></p:txStyles></p:sldMaster>";
    }

    private static string Layout(string type, string name, string shapes) =>
        $"<p:sldLayout {Namespaces} type=\"{type}\" preserve=\"1\"><p:cSld name=\"{name}\">{TreeStart}{shapes}</p:spTree></p:cSld>" +
        "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

    private static string TitleSlideLayout() => Layout("title", "Title Slide",
        Placeholder(2, "Title 1", "<p:ph type=\"ctrTitle\"/>", 1524000, 1122363, 9144000, 2387600, "<a:bodyPr anchor=\"b\"/>",
            "<a:lvl1pPr algn=\"ctr\"><a:defRPr sz=\"6000\"/></a:lvl1pPr>") +
        Placeholder(3, "Subtitle 2", "<p:ph type=\"subTitle\" idx=\"1\"/>", 1524000, 3602038, 9144000, 1655762, "<a:bodyPr/>",
            "<a:lvl1pPr marL=\"0\" indent=\"0\" algn=\"ctr\"><a:buNone/><a:defRPr sz=\"2400\"/></a:lvl1pPr>") +
        Placeholder(4, "Date Placeholder 3", "<p:ph type=\"dt\" sz=\"half\" idx=\"10\"/>", 838200, 6356350, 2743200, 365125));

    private static string TitleAndContentLayout() => Layout("obj", "Title and Content",
        Placeholder(2, "Title 1", "<p:ph type=\"title\"/>", TitleX, TitleY, TitleWidth, TitleHeight) +
        Placeholder(3, "Content Placeholder 2", "<p:ph idx=\"1\"/>", BodyX, BodyY, BodyWidth, BodyHeight));

    private static string SectionHeaderLayout() => Layout("secHead", "Section Header",
        Placeholder(2, "Title 1", "<p:ph type=\"title\"/>", 831850, 1709738, 10515600, 2852737, "<a:bodyPr anchor=\"b\"/>",
            "<a:lvl1pPr><a:defRPr sz=\"6000\"/></a:lvl1pPr>") +
        Placeholder(3, "Text Placeholder 2", "<p:ph type=\"body\" idx=\"1\"/>", 831850, 4589463, 10515600, 1500187, "<a:bodyPr/>",
            "<a:lvl1pPr marL=\"0\" indent=\"0\"><a:buNone/><a:defRPr sz=\"2400\"><a:solidFill><a:schemeClr val=\"tx1\"><a:tint val=\"75000\"/></a:schemeClr></a:solidFill></a:defRPr></a:lvl1pPr>"));
}
