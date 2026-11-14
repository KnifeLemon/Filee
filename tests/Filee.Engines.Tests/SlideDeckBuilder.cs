// Builds PowerPoint decks in code for the slide renderer's tests and the render quality harness: a theme with Office's
// colour, font and format schemes (fill, line, effect and background styles), a master with title / body / other text
// styles, placeholders and a decorative bar, three layouts (title, title and content, blank), and slides from
// p:spTree XML with their pictures and extra parts (SmartArt drawings, charts). Nothing is read from disk.

using System.Globalization;
using SkiaSharp;

namespace Filee.Engines.Tests;

internal sealed class SlideDeckBuilder
{
    public const string Namespaces =
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" " +
        "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"";

    /// <summary>Slide size in points (16:9, 13.333 × 7.5 in).</summary>
    public const double Width = 960, Height = 540;

    public enum Layout
    {
        Title = 1,
        Content = 2,
        Blank = 3,
    }

    private const string TreeStart = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>";

    private sealed record SlideEntry(string Xml, Layout Layout, List<(string Id, string Type, string Target)> Relationships);

    private readonly List<SlideEntry> _slides = [];
    private readonly Dictionary<string, byte[]> _media = [];
    private readonly Dictionary<string, string> _extraParts = [];
    private readonly List<(string Part, string Type)> _extraTypes = [];
    private int _nextId = 10;

    /// <summary>EMU of <paramref name="points"/>.</summary>
    public static long E(double points) => (long)Math.Round(points * 12700);

    private static string N(double value) => value.ToString(CultureInfo.InvariantCulture);

    public int NextId() => _nextId++;

    /// <summary>Adds a picture part; slides refer to it by the returned name (see <see cref="Slide"/>'s pictures).</summary>
    public string Media(byte[] bytes, string extension = "png")
    {
        var name = $"media/image{_media.Count + 1}.{extension}";
        _media[$"ppt/{name}"] = bytes;
        return name;
    }

    /// <summary>Adds a part of the package (a SmartArt drawing, a chart) with its content type.</summary>
    public void Part(string name, string xml, string contentType)
    {
        _extraParts[name] = xml;
        _extraTypes.Add((name, contentType));
    }

    /// <summary>
    /// Adds a slide of <paramref name="shapes"/> (p:spTree children). <paramref name="pictures"/> maps relationship ids
    /// to media names from <see cref="Media"/>; <paramref name="relationships"/> adds other relationships of the slide.
    /// </summary>
    public SlideDeckBuilder Slide(string shapes, Layout layout = Layout.Blank, string? background = null, IReadOnlyDictionary<string, string>? pictures = null,
        IEnumerable<(string Id, string Type, string Target)>? relationships = null, bool hidden = false)
    {
        var rels = new List<(string, string, string)> { ("rId1", "slideLayout", $"../slideLayouts/slideLayout{(int)layout}.xml") };
        foreach (var (id, media) in pictures ?? new Dictionary<string, string>())
            rels.Add((id, "image", "../" + media));
        rels.AddRange(relationships ?? []);
        var bg = background is null ? "" : $"<p:bg>{background}</p:bg>";
        _slides.Add(new SlideEntry(
            $"<p:sld {Namespaces}{(hidden ? " show=\"0\"" : "")}><p:cSld>{bg}<p:spTree>{TreeStart}{shapes}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>",
            layout, rels));
        return this;
    }

    // ───────────────────────── Shapes ─────────────────────────

    public static string Xfrm(double x, double y, double w, double h, double rotation = 0, bool flipH = false, bool flipV = false, string element = "a:xfrm") =>
        $"<{element}{(rotation != 0 ? $" rot=\"{(long)(rotation * 60000)}\"" : "")}{(flipH ? " flipH=\"1\"" : "")}{(flipV ? " flipV=\"1\"" : "")}>" +
        $"<a:off x=\"{E(x)}\" y=\"{E(y)}\"/><a:ext cx=\"{E(w)}\" cy=\"{E(h)}\"/></{element}>";

    public static string Solid(string color) => color.StartsWith('#')
        ? $"<a:solidFill><a:srgbClr val=\"{color[1..]}\"/></a:solidFill>"
        : $"<a:solidFill><a:schemeClr val=\"{color}\"/></a:solidFill>";

    /// <summary>The shape style PowerPoint gives new shapes: accent 1 fill, a darker outline, white text.</summary>
    public const string ShapeStyle =
        "<p:style><a:lnRef idx=\"2\"><a:schemeClr val=\"accent1\"><a:shade val=\"50000\"/></a:schemeClr></a:lnRef>" +
        "<a:fillRef idx=\"1\"><a:schemeClr val=\"accent1\"/></a:fillRef><a:effectRef idx=\"0\"><a:schemeClr val=\"accent1\"/></a:effectRef>" +
        "<a:fontRef idx=\"minor\"><a:schemeClr val=\"lt1\"/></a:fontRef></p:style>";

    public static string Paragraph(string text, string pPr = "", string rPr = "<a:rPr lang=\"en-US\" dirty=\"0\"/>") =>
        $"<a:p>{pPr}<a:r>{rPr}<a:t>{OpcWriter.Escape(text)}</a:t></a:r></a:p>";

    /// <summary>A shape (sp) with any geometry, fill, line, body properties and paragraphs.</summary>
    public string Shape(string geometry, double x, double y, double w, double h, string fill = "", string line = "", string paragraphs = "",
        string bodyPr = "<a:bodyPr rtlCol=\"0\" anchor=\"ctr\"/>", bool styled = true, double rotation = 0, bool flipH = false, bool flipV = false,
        string name = "Shape")
    {
        var geom = geometry.StartsWith('<') ? geometry : $"<a:prstGeom prst=\"{geometry}\"><a:avLst/></a:prstGeom>";
        return $"<p:sp><p:nvSpPr><p:cNvPr id=\"{NextId()}\" name=\"{name}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>" +
               $"<p:spPr>{Xfrm(x, y, w, h, rotation, flipH, flipV)}{geom}{fill}{line}</p:spPr>" +
               (styled ? ShapeStyle : "") +
               $"<p:txBody>{bodyPr}<a:lstStyle/>{(paragraphs.Length > 0 ? paragraphs : "<a:p><a:endParaRPr lang=\"en-US\"/></a:p>")}</p:txBody></p:sp>";
    }

    /// <summary>A text box: no fill or line, text wrapped in its width, top-anchored.</summary>
    public string TextBox(double x, double y, double w, double h, string paragraphs, string bodyPr = "<a:bodyPr wrap=\"square\" rtlCol=\"0\"><a:noAutofit/></a:bodyPr>") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{NextId()}\" name=\"TextBox\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr>" +
        $"<p:spPr>{Xfrm(x, y, w, h)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></p:spPr>" +
        $"<p:txBody>{bodyPr}<a:lstStyle/>{paragraphs}</p:txBody></p:sp>";

    /// <summary>A placeholder of the layout (title, ctrTitle, subTitle, or body by index) with paragraphs.</summary>
    public string Placeholder(string type, string paragraphs, int? index = null, string spPr = "") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{NextId()}\" name=\"{type}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr>" +
        $"<p:nvPr><p:ph{(type == "body" ? "" : $" type=\"{type}\"")}{(index is { } i ? $" idx=\"{i}\"" : "")}/></p:nvPr></p:nvSpPr>" +
        $"<p:spPr>{spPr}</p:spPr><p:txBody><a:bodyPr/><a:lstStyle/>{paragraphs}</p:txBody></p:sp>";

    /// <summary>A connector (cxnSp) with its geometry and line.</summary>
    public string Connector(string geometry, double x, double y, double w, double h, string line, bool flipH = false, bool flipV = false, string avLst = "") =>
        $"<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"{NextId()}\" name=\"Connector\"/><p:cNvCxnSpPr/><p:nvPr/></p:nvCxnSpPr>" +
        $"<p:spPr>{Xfrm(x, y, w, h, 0, flipH, flipV)}<a:prstGeom prst=\"{geometry}\"><a:avLst>{avLst}</a:avLst></a:prstGeom>{line}</p:spPr></p:cxnSp>";

    /// <summary>A picture (pic) of media relationship <paramref name="id"/>, with optional crop, geometry and blip effects.</summary>
    public string Picture(string id, double x, double y, double w, double h, string srcRect = "", string geometry = "rect", string blipEffects = "",
        double rotation = 0, string line = "") =>
        $"<p:pic><p:nvPicPr><p:cNvPr id=\"{NextId()}\" name=\"Picture\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>" +
        $"<p:blipFill><a:blip r:embed=\"{id}\">{blipEffects}</a:blip>{srcRect}<a:stretch><a:fillRect/></a:stretch></p:blipFill>" +
        $"<p:spPr>{Xfrm(x, y, w, h, rotation)}<a:prstGeom prst=\"{geometry}\"><a:avLst/></a:prstGeom>{line}</p:spPr></p:pic>";

    /// <summary>A group whose child space is its own box scaled by <paramref name="scale"/>.</summary>
    public string Group(double x, double y, double w, double h, double scale, string children, double rotation = 0) =>
        $"<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"{NextId()}\" name=\"Group\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
        $"<p:grpSpPr><a:xfrm{(rotation != 0 ? $" rot=\"{(long)(rotation * 60000)}\"" : "")}><a:off x=\"{E(x)}\" y=\"{E(y)}\"/><a:ext cx=\"{E(w)}\" cy=\"{E(h)}\"/>" +
        $"<a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"{E(w / scale)}\" cy=\"{E(h / scale)}\"/></a:xfrm></p:grpSpPr>{children}</p:grpSp>";

    /// <summary>A table: rows of cell XML (a:tc), column widths in points.</summary>
    public string Table(double x, double y, double[] columns, double rowHeight, IEnumerable<string> rows, string tblPr =
        "<a:tblPr firstRow=\"1\" bandRow=\"1\"><a:tableStyleId>{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}</a:tableStyleId></a:tblPr>")
    {
        var rowList = rows.ToList();
        return $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{NextId()}\" name=\"Table\"/><p:cNvGraphicFramePr><a:graphicFrameLocks noGrp=\"1\"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr>" +
               Xfrm(x, y, columns.Sum(), rowHeight * rowList.Count, element: "p:xfrm") +
               "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\"><a:tbl>" + tblPr +
               $"<a:tblGrid>{string.Concat(columns.Select(c => $"<a:gridCol w=\"{E(c)}\"/>"))}</a:tblGrid>" +
               string.Concat(rowList.Select(r => $"<a:tr h=\"{E(rowHeight)}\">{r}</a:tr>")) +
               "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
    }

    public static string Cell(string text, string tcPr = "<a:tcPr/>", string attributes = "") =>
        $"<a:tc{attributes}><a:txBody><a:bodyPr/><a:lstStyle/>{(text.Length > 0 ? Paragraph(text) : "<a:p><a:endParaRPr lang=\"en-US\"/></a:p>")}</a:txBody>{tcPr}</a:tc>";

    // ───────────────────────── Pictures ─────────────────────────

    /// <summary>A PNG drawn in code: a gradient with circles and a diagonal, so crops and turns show.</summary>
    public static byte[] TestPicture(int width = 320, int height = 200)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height),
            [new SKColor(0x1E, 0x88, 0xE5), new SKColor(0xFF, 0xB3, 0x00)], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(0, 0, width, height, paint);
        using var ink = new SKPaint { Color = new SKColor(0x26, 0x32, 0x38), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6 };
        canvas.DrawLine(0, 0, width, height, ink);
        using var dot = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawCircle(width * 0.25f, height * 0.5f, height * 0.2f, dot);
        dot.Color = new SKColor(0xC6, 0x28, 0x28);
        canvas.DrawCircle(width * 0.75f, height * 0.3f, height * 0.15f, dot);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ───────────────────────── Package ─────────────────────────

    private const string Theme =
        "<a:themeElements><a:clrScheme name=\"Office\">" +
        "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
        "<a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2>" +
        "<a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1><a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2><a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3>" +
        "<a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4><a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6>" +
        "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
        "<a:fontScheme name=\"Office\"><a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/>" +
        "<a:font script=\"Hang\" typeface=\"맑은 고딕\"/><a:font script=\"Jpan\" typeface=\"Yu Gothic Light\"/><a:font script=\"Arab\" typeface=\"Times New Roman\"/></a:majorFont>" +
        "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/>" +
        "<a:font script=\"Hang\" typeface=\"맑은 고딕\"/><a:font script=\"Jpan\" typeface=\"Yu Gothic\"/><a:font script=\"Arab\" typeface=\"Arial\"/></a:minorFont></a:fontScheme>" +
        "<a:fmtScheme name=\"Office\"><a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>" +
        "<a:gradFill rotWithShape=\"1\"><a:gsLst><a:gs pos=\"0\"><a:schemeClr val=\"phClr\"><a:lumMod val=\"110000\"/><a:satMod val=\"105000\"/><a:tint val=\"67000\"/></a:schemeClr></a:gs>" +
        "<a:gs pos=\"100000\"><a:schemeClr val=\"phClr\"><a:satMod val=\"103000\"/><a:lumMod val=\"102000\"/><a:tint val=\"94000\"/></a:schemeClr></a:gs></a:gsLst><a:lin ang=\"5400000\" scaled=\"0\"/></a:gradFill>" +
        "<a:gradFill rotWithShape=\"1\"><a:gsLst><a:gs pos=\"0\"><a:schemeClr val=\"phClr\"><a:satMod val=\"103000\"/><a:lumMod val=\"102000\"/><a:tint val=\"94000\"/></a:schemeClr></a:gs>" +
        "<a:gs pos=\"100000\"><a:schemeClr val=\"phClr\"><a:lumMod val=\"99000\"/><a:satMod val=\"120000\"/><a:shade val=\"78000\"/></a:schemeClr></a:gs></a:gsLst><a:lin ang=\"5400000\" scaled=\"0\"/></a:gradFill></a:fillStyleLst>" +
        "<a:lnStyleLst><a:ln w=\"6350\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln>" +
        "<a:ln w=\"12700\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln>" +
        "<a:ln w=\"19050\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln></a:lnStyleLst>" +
        "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle>" +
        "<a:effectStyle><a:effectLst><a:outerShdw blurRad=\"57150\" dist=\"19050\" dir=\"5400000\" algn=\"ctr\" rotWithShape=\"0\"><a:srgbClr val=\"000000\"><a:alpha val=\"63000\"/></a:srgbClr></a:outerShdw></a:effectLst></a:effectStyle></a:effectStyleLst>" +
        "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"><a:tint val=\"95000\"/><a:satMod val=\"170000\"/></a:schemeClr></a:solidFill>" +
        "<a:gradFill rotWithShape=\"1\"><a:gsLst><a:gs pos=\"0\"><a:schemeClr val=\"phClr\"><a:tint val=\"93000\"/><a:satMod val=\"150000\"/><a:shade val=\"98000\"/><a:lumMod val=\"102000\"/></a:schemeClr></a:gs>" +
        "<a:gs pos=\"100000\"><a:schemeClr val=\"phClr\"><a:shade val=\"63000\"/><a:satMod val=\"120000\"/></a:schemeClr></a:gs></a:gsLst><a:lin ang=\"5400000\" scaled=\"0\"/></a:gradFill></a:bgFillStyleLst></a:fmtScheme></a:themeElements>";

    private static string MasterPlaceholder(int id, string type, int? index, double x, double y, double w, double h, string bodyPr = "<a:bodyPr/>") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{type}\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"{type}\"{(index is { } i ? $" idx=\"{i}\"" : "")}/></p:nvPr></p:nvSpPr>" +
        $"<p:spPr>{Xfrm(x, y, w, h)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr><p:txBody>{bodyPr}<a:lstStyle/><a:p><a:r><a:rPr lang=\"en-US\"/><a:t>{type}</a:t></a:r></a:p></p:txBody></p:sp>";

    private static string Level(int level, int marL, int indent, int size, string bullet) =>
        $"<a:lvl{level}pPr marL=\"{marL}\" indent=\"{indent}\" algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\">" +
        "<a:lnSpc><a:spcPct val=\"90000\"/></a:lnSpc><a:spcBef><a:spcPts val=\"1000\"/></a:spcBef>" +
        $"{bullet}<a:defRPr sz=\"{size}\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl{level}pPr>";

    private static string Master =>
        "<p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>" + TreeStart +
        MasterPlaceholder(2, "title", null, 66, 28.75, 828, 104.4, "<a:bodyPr vert=\"horz\" lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" rtlCol=\"0\" anchor=\"ctr\"><a:normAutofit/></a:bodyPr>") +
        MasterPlaceholder(3, "body", 1, 66, 143.75, 828, 342.6, "<a:bodyPr vert=\"horz\" lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" rtlCol=\"0\"><a:normAutofit/></a:bodyPr>") +
        MasterPlaceholder(4, "sldNum", 4, 678, 500.5, 216, 28.75, "<a:bodyPr anchor=\"ctr\"/>") +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"5\" name=\"Bar\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>" +
        $"<p:spPr>{Xfrm(0, 528, 960, 12)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:solidFill><a:schemeClr val=\"accent2\"/></a:solidFill><a:ln><a:noFill/></a:ln></p:spPr></p:sp>" +
        "</p:spTree></p:cSld>" +
        "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
        "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/><p:sldLayoutId id=\"2147483650\" r:id=\"rId2\"/><p:sldLayoutId id=\"2147483651\" r:id=\"rId3\"/></p:sldLayoutIdLst>" +
        "<p:txStyles><p:titleStyle><a:lvl1pPr algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\"><a:lnSpc><a:spcPct val=\"90000\"/></a:lnSpc>" +
        "<a:spcBef><a:spcPct val=\"0\"/></a:spcBef><a:buNone/><a:defRPr sz=\"4400\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mj-lt\"/><a:ea typeface=\"+mj-ea\"/><a:cs typeface=\"+mj-cs\"/></a:defRPr></a:lvl1pPr></p:titleStyle>" +
        "<p:bodyStyle>" + Level(1, 228600, -228600, 2800, "<a:buFont typeface=\"Arial\"/><a:buChar char=\"•\"/>") +
        Level(2, 685800, -228600, 2400, "<a:buFont typeface=\"Arial\"/><a:buChar char=\"•\"/>") +
        Level(3, 1143000, -228600, 2000, "<a:buFont typeface=\"Arial\"/><a:buChar char=\"•\"/>") + "</p:bodyStyle>" +
        "<p:otherStyle><a:defPPr><a:defRPr lang=\"en-US\"/></a:defPPr><a:lvl1pPr marL=\"0\" algn=\"l\" defTabSz=\"914400\"><a:defRPr sz=\"1800\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill>" +
        "<a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl1pPr></p:otherStyle></p:txStyles>";

    private static string LayoutPart(Layout layout) => layout switch
    {
        Layout.Title => "<p:cSld name=\"Title Slide\"><p:spTree>" + TreeStart +
                        "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"ctrTitle\"/></p:nvPr></p:nvSpPr>" +
                        $"<p:spPr>{Xfrm(120, 88.4, 720, 188)}</p:spPr><p:txBody><a:bodyPr anchor=\"b\"/><a:lstStyle><a:lvl1pPr algn=\"ctr\"><a:defRPr sz=\"6000\"/></a:lvl1pPr></a:lstStyle><a:p><a:endParaRPr lang=\"en-US\"/></a:p></p:txBody></p:sp>" +
                        "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Subtitle\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"subTitle\" idx=\"1\"/></p:nvPr></p:nvSpPr>" +
                        $"<p:spPr>{Xfrm(120, 283.6, 720, 130.4)}</p:spPr><p:txBody><a:bodyPr/><a:lstStyle><a:lvl1pPr marL=\"0\" indent=\"0\" algn=\"ctr\"><a:buNone/><a:defRPr sz=\"2400\"><a:solidFill><a:schemeClr val=\"tx1\"><a:lumMod val=\"65000\"/><a:lumOff val=\"35000\"/></a:schemeClr></a:solidFill></a:defRPr></a:lvl1pPr></a:lstStyle><a:p><a:endParaRPr lang=\"en-US\"/></a:p></p:txBody></p:sp>" +
                        "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>",
        Layout.Content => "<p:cSld name=\"Title and Content\"><p:spTree>" + TreeStart +
                          "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang=\"en-US\"/></a:p></p:txBody></p:sp>" +
                          "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Content\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang=\"en-US\"/></a:p></p:txBody></p:sp>" +
                          "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>",
        _ => "<p:cSld name=\"Blank\"><p:spTree>" + TreeStart + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>",
    };

    private const string DefaultTextStyle =
        "<p:defaultTextStyle><a:defPPr><a:defRPr lang=\"en-US\"/></a:defPPr>" +
        "<a:lvl1pPr marL=\"0\" algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\"><a:defRPr sz=\"1800\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill>" +
        "<a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl1pPr>" +
        "<a:lvl2pPr marL=\"457200\" algn=\"l\" defTabSz=\"914400\" rtl=\"0\"><a:defRPr sz=\"1800\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill>" +
        "<a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl2pPr></p:defaultTextStyle>";

    public string Save(string path)
    {
        var presentationRels = new List<(string, string, string)>
        {
            ("rId1", "slideMaster", "slideMasters/slideMaster1.xml"), ("rId2", "theme", "theme/theme1.xml"), ("rId3", "tableStyles", "tableStyles.xml"),
        };
        presentationRels.AddRange(_slides.Select((_, i) => ($"rId{i + 10}", "slide", $"slides/slide{i + 1}.xml")));
        var parts = new Dictionary<string, string>
        {
            ["_rels/.rels"] = OpcWriter.Relationships([("rId1", "officeDocument", "ppt/presentation.xml")]),
            ["ppt/presentation.xml"] = $"<p:presentation {Namespaces} saveSubsetFonts=\"1\">" +
                                       "<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>" +
                                       $"<p:sldIdLst>{string.Concat(_slides.Select((_, i) => $"<p:sldId id=\"{256 + i}\" r:id=\"rId{i + 10}\"/>"))}</p:sldIdLst>" +
                                       $"<p:sldSz cx=\"{E(Width)}\" cy=\"{E(Height)}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/>{DefaultTextStyle}</p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = OpcWriter.Relationships(presentationRels),
            ["ppt/tableStyles.xml"] = "<a:tblStyleLst xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" def=\"{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}\"/>",
            ["ppt/slideMasters/slideMaster1.xml"] = $"<p:sldMaster {Namespaces}>{Master}</p:sldMaster>",
            ["ppt/slideMasters/_rels/slideMaster1.xml.rels"] = OpcWriter.Relationships([
                ("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"), ("rId2", "slideLayout", "../slideLayouts/slideLayout2.xml"),
                ("rId3", "slideLayout", "../slideLayouts/slideLayout3.xml"), ("rId4", "theme", "../theme/theme1.xml")]),
            ["ppt/theme/theme1.xml"] = $"<a:theme {Namespaces} name=\"Office Theme\">{Theme}</a:theme>",
        };
        foreach (var layout in Enum.GetValues<Layout>())
        {
            parts[$"ppt/slideLayouts/slideLayout{(int)layout}.xml"] = $"<p:sldLayout {Namespaces}>{LayoutPart(layout)}</p:sldLayout>";
            parts[$"ppt/slideLayouts/_rels/slideLayout{(int)layout}.xml.rels"] = OpcWriter.Relationships([("rId1", "slideMaster", "../slideMasters/slideMaster1.xml")]);
        }
        for (var i = 0; i < _slides.Count; i++)
        {
            parts[$"ppt/slides/slide{i + 1}.xml"] = _slides[i].Xml;
            parts[$"ppt/slides/_rels/slide{i + 1}.xml.rels"] = OpcWriter.Relationships(_slides[i].Relationships);
        }
        foreach (var (name, xml) in _extraParts)
            parts[name] = xml;
        var types = _slides.Select((_, i) => ($"ppt/slides/slide{i + 1}.xml", "presentationml.slide"))
            .Append(("ppt/presentation.xml", "presentationml.presentation.main"))
            .Append(("ppt/slideMasters/slideMaster1.xml", "presentationml.slideMaster"))
            .Append(("ppt/theme/theme1.xml", "theme"))
            .Append(("ppt/tableStyles.xml", "presentationml.tableStyles"))
            .Concat(Enum.GetValues<Layout>().Select(l => ($"ppt/slideLayouts/slideLayout{(int)l}.xml", "presentationml.slideLayout")))
            .Concat(_extraTypes);
        parts["[Content_Types].xml"] = OpcWriter.ContentTypes(types);
        OpcWriter.Save(path, parts, _media);
        return path;
    }
}
