// The pictures and shapes of a worksheet's drawing part (xl/drawings/drawingN.xml, DrawingML's spreadsheet drawing,
// ECMA-376 Part 1, 20.5), read for the sheet printer: pictures with their turn, mirroring and cropping (a:xfrm rot,
// flipH, flipV and a:srcRect), preset shapes with one fill and one outline colour (the shape's own or its theme
// style's, through the slide renderer's preset definitions) with their text (paragraphs and runs of the txBody with
// their bullets, indents and line spacing, the body's anchor, insets, wrap, vertical text and saved autofit shrink;
// text linked to a cell is that cell's text, LinkTexts), and groups of both, each child placed in its share of the
// group's anchor. What the printer doesn't draw is named instead: charts, SmartArt, embedded objects, ink, freeform
// shapes, gradient, pattern and picture fills, arrowheads, turned groups, EMF / WMF / TIFF and linked
// pictures. SheetCapabilities asks for that name, so such a workbook can go to LibreOffice where it is installed.
// Charts are read from their chart part (ChartReader, with the workbook's theme) and drawn, Office 2016 charts
// (chartex) too; those ChartReader doesn't draw (region maps) are named "charts" and Excel's fallback is drawn instead.
// A chart sheet's drawing gives its one chart (ChartSheet), which the printer draws alone on the sheet's page.
//
// Excel anchors a turned picture or shape by the box it covers on the sheet: one turned by 90° has an anchor as wide as
// the picture is tall. The unturned size (a:xfrm ext) tells which way round the anchor is (SheetDrawing.Size).
// Effects (shadows, glow, 3D) are left out without a word: the shape itself is drawn.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Charts;
using Filee.Engines.Office.Ooxml;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Office.Sheets;

internal sealed class XlsxDrawing
{
    private static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private static readonly XNamespace A = DrawingColors.A;
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>DrawingML's chart drawing (c:userShapes parts): shapes and pictures placed over a chart.</summary>
    private static readonly XNamespace Cdr = "http://schemas.openxmlformats.org/drawingml/2006/chartDrawing";

    /// <summary>Points per EMU (DrawingML positions).</summary>
    private const double PointsPerEmu = 1 / 12700.0;

    /// <summary>Bytes of one picture read at most.</summary>
    private const long MaxPictureBytes = 64L * 1024 * 1024;

    /// <summary>Groups inside groups followed at most; deeper ones are left out (and named).</summary>
    private const int MaxGroupDepth = 32;

    /// <summary>
    /// Pictures and shapes of one drawing part drawn at most, against a part built to make printing slow; the rest is
    /// left out (and named). Sheets people make hold a few hundred.
    /// </summary>
    internal const int MaxDrawings = 20_000;

    /// <summary>Paragraphs of one shape's text read at most.</summary>
    private const int MaxParagraphs = 1000;

    /// <summary>Characters of one shape's text read at most (Excel's limit for a cell's text).</summary>
    private const int MaxTextCharacters = 32_767;

    /// <summary>The longest textlink reference followed (a sheet name of 31 characters, quoted, and a range).</summary>
    private const int MaxLinkLength = 255;

    /// <summary>The largest EMU value taken from a position or size (about 4 km); larger ones are clamped.</summary>
    private const long MaxEmu = 50_000_000_000;

    private readonly OpcPackage _package;
    private readonly string _part;
    private readonly DrawingColors _colors;
    private readonly XElement? _formatScheme;
    private readonly string? _themeFont;
    private readonly ChartBudget? _charts;
    private readonly bool _read;
    private readonly XElement? _theme;

    /// <summary>The namespace of the objects' own elements: the spreadsheet drawing's, or the chart drawing's.</summary>
    private readonly XNamespace _ns = Xdr;

    /// <summary>What is drawn, in drawing order; empty when only the check was asked for.</summary>
    public List<SheetDrawing> Drawings { get; } = [];

    /// <summary>The first thing in the part the printer doesn't draw, or null.</summary>
    public string? FirstLeftOut { get; private set; }

    private XlsxDrawing(OpcPackage package, string part, XElement? theme, bool read, ChartBudget? charts)
    {
        _package = package;
        _part = part;
        _colors = DrawingColors.FromTheme(theme);
        _formatScheme = theme?.Descendants(A + "fmtScheme").FirstOrDefault();
        _themeFont = ChartReader.ThemeFont(theme);
        _charts = charts;
        _read = read;
        _theme = theme;
    }

    /// <summary>A chart drawing part (a chart's userShapes) read with what <paramref name="sheet"/> reads with.</summary>
    private XlsxDrawing(XlsxDrawing sheet, string part)
        : this(sheet._package, part, sheet._theme, sheet._read, sheet._charts) => _ns = Cdr;

    /// <summary>
    /// Reads drawing part <paramref name="part"/>: its pictures and shapes (with <paramref name="read"/>; without it,
    /// no picture is unpacked and only <see cref="FirstLeftOut"/> is found).
    /// </summary>
    /// <param name="theme">The workbook's theme part (colours and line styles shapes refer to), or null.</param>
    /// <param name="charts">The workbook's count of chart points drawn (with <paramref name="read"/>).</param>
    public static XlsxDrawing Read(OpcPackage package, string part, XElement? theme, bool read, ChartBudget? charts = null)
    {
        var drawing = new XlsxDrawing(package, part, theme, read, charts);
        foreach (var anchor in package.Part(part)?.Root?.Elements() ?? [])
            drawing.Anchor(anchor);
        return drawing;
    }

    /// <summary>
    /// The chart of a chart sheet's drawing part (Excel puts one chart there, on an absolute anchor), printed alone on
    /// the sheet's page, with what is placed over it (pictures and shapes of the drawing, the chart's own userShapes);
    /// null when there is none ChartReader draws. <c>LeftOut</c> names what isn't printed.
    /// </summary>
    /// <param name="charts">The workbook's count of chart points drawn, or null for the check alone.</param>
    public static (SheetChart? Chart, List<SheetDrawing> Over, string? LeftOut) ChartSheet(OpcPackage package, string part, XElement? theme, ChartBudget? charts = null)
    {
        var drawing = Read(package, part, theme, read: true, charts);
        var chart = drawing.Drawings.OfType<SheetChart>().FirstOrDefault();
        var over = drawing.Drawings.Where(d => !ReferenceEquals(d, chart)).ToList();
        // A second chart over the first isn't placed: Excel keeps one chart on a chart sheet.
        var second = over.OfType<SheetChart>().Any();
        over.RemoveAll(d => d is SheetChart);
        return (chart, over, drawing.FirstLeftOut ?? (chart is null || second ? "charts" : null));
    }

    private void LeftOut(string what) => FirstLeftOut ??= what;

    private void Add(SheetDrawing drawing)
    {
        if (Drawings.Count < MaxDrawings)
            Drawings.Add(drawing);
        else
            LeftOut("more drawings than Filee prints on one sheet");
    }

    // ───────────────────────── Anchors ─────────────────────────

    /// <summary>Where an anchor puts its object: top-left cell and offset in points, size in points or bottom-right cell.</summary>
    private readonly record struct Box(int Row, int Column, double X, double Y, double Width, double Height,
        (int Row, int Column, double OffsetX, double OffsetY)? To);

    /// <summary>
    /// Group child space mapped onto a share of the anchor's box: child coordinates (EMU) from
    /// <c>(X, Y)</c> over <c>(Width, Height)</c> become fractions <c>Left..Right</c>, <c>Top..Bottom</c> of the box.
    /// </summary>
    private readonly record struct Space(double X, double Y, double Width, double Height, double Left, double Top, double Right, double Bottom)
    {
        public (double Left, double Top, double Right, double Bottom) Map(double x, double y, double width, double height)
        {
            var sx = (Right - Left) / Width;
            var sy = (Bottom - Top) / Height;
            return (Left + (x - X) * sx, Top + (y - Y) * sy, Left + (x + width - X) * sx, Top + (y + height - Y) * sy);
        }
    }

    private void Anchor(XElement anchor)
    {
        // Content a consumer may not know (slicers, new chart types) comes with a fallback to draw instead.
        if (anchor.Name.LocalName == "AlternateContent")
        {
            var chosen = Alternative(anchor);
            foreach (var inner in chosen?.Elements() ?? [])
                Anchor(inner);
            return;
        }
        if (anchor.Name.Namespace != Xdr)
            return;
        static (int Row, int Column, double X, double Y)? Marker(XElement? marker) => marker is null ? null : (
            (int)Math.Clamp(Unsigned(marker.Element(Xdr + "row")), 0, SheetPagination.MaxRow),
            (int)Math.Clamp(Unsigned(marker.Element(Xdr + "col")), 0, SheetPagination.MaxColumn),
            Unsigned(marker.Element(Xdr + "colOff")) * PointsPerEmu,
            Unsigned(marker.Element(Xdr + "rowOff")) * PointsPerEmu);
        var extent = anchor.Element(Xdr + "ext");
        var (width, height) = (Unsigned(extent?.Attribute("cx")) * PointsPerEmu, Unsigned(extent?.Attribute("cy")) * PointsPerEmu);
        Box? box = anchor.Name.LocalName switch
        {
            "twoCellAnchor" when Marker(anchor.Element(Xdr + "from")) is { } from && Marker(anchor.Element(Xdr + "to")) is { } to =>
                new Box(from.Row, from.Column, from.X, from.Y, 0, 0, (to.Row, to.Column, to.X, to.Y)),
            "oneCellAnchor" when Marker(anchor.Element(Xdr + "from")) is { } from => new Box(from.Row, from.Column, from.X, from.Y, width, height, null),
            "absoluteAnchor" => new Box(0, 0, Unsigned(anchor.Element(Xdr + "pos")?.Attribute("x")) * PointsPerEmu,
                Unsigned(anchor.Element(Xdr + "pos")?.Attribute("y")) * PointsPerEmu, width, height, null),
            _ => null,
        };
        // An object set not to print (Format → Properties → Print object off) is left out, as Excel leaves it out.
        if (box is not { } placed || (string?)anchor.Element(Xdr + "clientData")?.Attribute("fPrintsWithSheet") is "0" or "false")
            return;
        if (anchor.Elements().FirstOrDefault(IsObject) is { } shape)
            Object(shape, placed, null, 0);
    }

    /// <summary>
    /// What of an mc:AlternateContent is drawn: an Office 2016 chart (chartex) ChartReader reads, else the fallback
    /// (Excel's shape saying the chart can't be shown: the chart is named), else the choice.
    /// </summary>
    private XElement? Alternative(XElement alternate)
    {
        if (ChartReader.ChartExChoice(alternate) is { } chartEx)
        {
            var target = _package.Target(_part, chartEx.RelationshipId);
            if (ChartReader.Read(target is null ? null : _package.Part(target)?.Root, _colors, _themeFont).Chart is not null)
                return chartEx.Choice;
            LeftOut("charts");
        }
        return alternate.Elements().FirstOrDefault(e => e.Name.LocalName == "Fallback") ?? alternate.Elements().FirstOrDefault(e => e.Name.LocalName == "Choice");
    }

    private static bool IsObject(XElement e) =>
        e.Name.LocalName is "sp" or "grpSp" or "graphicFrame" or "cxnSp" or "pic" or "contentPart" or "AlternateContent";

    /// <summary>One object of an anchor or of a group (<paramref name="space"/> maps a group's child coordinates).</summary>
    private void Object(XElement element, Box box, Space? space, int depth)
    {
        if (element.Name.LocalName == "AlternateContent")
        {
            var chosen = Alternative(element);
            if (chosen?.Elements().FirstOrDefault(IsObject) is { } inner)
                Object(inner, box, space, depth);
            return;
        }
        var properties = element.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("nv", StringComparison.Ordinal))?.Element(_ns + "cNvPr");
        if ((string?)properties?.Attribute("hidden") is "1" or "true")
            return;
        switch (element.Name.LocalName)
        {
            case "pic":
                Picture(element, box, space);
                break;
            case "sp" or "cxnSp":
                Shape(element, box, space);
                break;
            case "grpSp":
                Group(element, box, space, depth);
                break;
            case "graphicFrame":
                Frame(element, box, space);
                break;
            case "contentPart":
                LeftOut("ink");
                break;
        }
    }

    /// <summary>A graphic frame: a chart ChartReader draws, or what else it holds, named.</summary>
    private void Frame(XElement frame, Box box, Space? space)
    {
        var data = frame.Element(A + "graphic")?.Element(A + "graphicData");
        var uri = (string?)data?.Attribute("uri") ?? "";
        if (!uri.EndsWith("/chart", StringComparison.Ordinal) && !uri.EndsWith("/chartex", StringComparison.Ordinal))
        {
            LeftOut(Graphic(uri));
            return;
        }
        var target = _package.Target(_part, (string?)(data?.Element(ChartReader.C + "chart") ?? data?.Element(ChartReader.Cx + "chart"))?.Attribute(R + "id"));
        var read = ChartReader.Read(target is null ? null : _package.Part(target)?.Root, _colors, _themeFont);
        if (read.Chart is not { } chart)
        {
            LeftOut(read.LeftOut ?? "charts");
            return;
        }
        if (_read)
        {
            _charts?.Charge(chart);
            var placed = Placed(new SheetChart(chart, box.Row, box.Column, box.X, box.Y, box.Width, box.Height), box, space, frame.Element(_ns + "xfrm"));
            Add(placed);
            UserShapes(target!, box, placed.Part, frame.Element(_ns + "xfrm")?.Element(A + "ext"));
        }
        else
        {
            UserShapes(target!, box, null, null);
        }
    }

    /// <summary>
    /// The shapes and pictures of a chart's userShapes part, over the chart: each anchor gives its share of the chart's
    /// box (cdr:relSizeAnchor as fractions, cdr:absSizeAnchor as a fraction and a size against the chart's size).
    /// </summary>
    /// <param name="share">The chart's own share of <paramref name="box"/> (a chart in a group), or null for all of it.</param>
    /// <param name="extent">The chart frame's size (a:ext, EMU), which an absolute size is a share of.</param>
    private void UserShapes(string chartPart, Box box, (double Left, double Top, double Right, double Bottom)? share, XElement? extent)
    {
        var root = _package.Part(chartPart)?.Root;
        if (_package.Target(chartPart, (string?)root?.Element(ChartReader.C + "userShapes")?.Attribute(R + "id")) is not { } part
            || _package.Part(part)?.Root is not { } shapes || _ns == Cdr)
            return;
        var inner = new XlsxDrawing(this, part);
        var (chartWidth, chartHeight) = (Signed(extent?.Attribute("cx")), Signed(extent?.Attribute("cy")));
        if (chartWidth <= 0 || chartHeight <= 0)
            (chartWidth, chartHeight) = ((long)(box.Width / PointsPerEmu), (long)(box.Height / PointsPerEmu));
        var (left, top, right, bottom) = share ?? (0.0, 0.0, 1.0, 1.0);
        foreach (var anchor in shapes.Elements())
        {
            static (double X, double Y)? Point(XElement? marker) => marker is null ? null
                : (Math.Clamp(Share(marker.Element(Cdr + "x")), 0, 1), Math.Clamp(Share(marker.Element(Cdr + "y")), 0, 1));
            (double X, double Y)? from = Point(anchor.Element(Cdr + "from")), to = null;
            if (anchor.Name == Cdr + "relSizeAnchor")
                to = Point(anchor.Element(Cdr + "to"));
            else if (anchor.Name == Cdr + "absSizeAnchor" && from is { } start && chartWidth > 0 && chartHeight > 0)
            {
                var size = anchor.Element(Cdr + "ext");
                to = (Math.Min(1, start.X + Unsigned(size?.Attribute("cx")) / (double)chartWidth), Math.Min(1, start.Y + Unsigned(size?.Attribute("cy")) / (double)chartHeight));
            }
            if (from is not { } f || to is not { } t || anchor.Elements().FirstOrDefault(IsObject) is not { } shape)
                continue;
            // The object's own box (its xfrm) is mapped onto its share of the chart, inside the chart's share of the anchor.
            var xfrm = (shape.Element(Cdr + "spPr") ?? shape.Element(Cdr + "grpSpPr"))?.Element(A + "xfrm") ?? shape.Element(Cdr + "xfrm");
            var (x, y) = (Signed(xfrm?.Element(A + "off")?.Attribute("x")), Signed(xfrm?.Element(A + "off")?.Attribute("y")));
            var (cx, cy) = (Math.Max(1, Signed(xfrm?.Element(A + "ext")?.Attribute("cx"))), Math.Max(1, Signed(xfrm?.Element(A + "ext")?.Attribute("cy"))));
            double Across(double fraction) => left + fraction * (right - left);
            double Down(double fraction) => top + fraction * (bottom - top);
            inner.Object(shape, box, new Space(x, y, cx, cy, Across(f.X), Down(f.Y), Across(t.X), Down(t.Y)), 0);
        }
        foreach (var drawing in inner.Drawings)
            Add(drawing);
        if (inner.FirstLeftOut is { } what)
            LeftOut(what);
    }

    /// <summary>A chart drawing coordinate (cdr:x, cdr:y: a fraction of the chart's width or height).</summary>
    private static double Share(XElement? value) =>
        double.TryParse(value?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) && double.IsFinite(fraction) ? fraction : 0;

    /// <summary>What a graphic frame holds, named.</summary>
    private static string Graphic(string uri) =>
        uri.EndsWith("/chart", StringComparison.Ordinal) || uri.EndsWith("/chartex", StringComparison.Ordinal) ? "charts"
        : uri.EndsWith("/diagram", StringComparison.Ordinal) ? "SmartArt"
        : "embedded objects";

    private void Group(XElement group, Box box, Space? space, int depth)
    {
        if (depth >= MaxGroupDepth)
        {
            LeftOut("grouped shapes");
            return;
        }
        var xfrm = group.Element(_ns + "grpSpPr")?.Element(A + "xfrm");
        if (Turn(xfrm) is { } turn && (turn.Rotation != 0 || turn.FlipH || turn.FlipV))
            LeftOut("turned groups");
        var (offX, offY) = (Signed(xfrm?.Element(A + "off")?.Attribute("x")), Signed(xfrm?.Element(A + "off")?.Attribute("y")));
        var (extX, extY) = (Signed(xfrm?.Element(A + "ext")?.Attribute("cx")), Signed(xfrm?.Element(A + "ext")?.Attribute("cy")));
        var (chOffX, chOffY) = (Signed(xfrm?.Element(A + "chOff")?.Attribute("x")), Signed(xfrm?.Element(A + "chOff")?.Attribute("y")));
        var (chExtX, chExtY) = (Signed(xfrm?.Element(A + "chExt")?.Attribute("cx")), Signed(xfrm?.Element(A + "chExt")?.Attribute("cy")));
        if (chExtX <= 0 || chExtY <= 0)
            (chOffX, chOffY, chExtX, chExtY) = (offX, offY, extX, extY);
        if (chExtX <= 0 || chExtY <= 0)
            return;
        // The group's own box: the whole anchor at the top, its share of the outer group inside one.
        var (left, top, right, bottom) = space is { } outer && extX > 0 && extY > 0 ? outer.Map(offX, offY, extX, extY) : (0.0, 0.0, 1.0, 1.0);
        var inner = new Space(chOffX, chOffY, chExtX, chExtY, left, top, right, bottom);
        foreach (var child in group.Elements().Where(IsObject))
            Object(child, box, inner, depth + 1);
    }

    // ───────────────────────── Pictures ─────────────────────────

    private void Picture(XElement picture, Box box, Space? space)
    {
        var blip = picture.Element(_ns + "blipFill")?.Element(A + "blip");
        var target = _package.Target(_part, (string?)blip?.Attribute(R + "embed"));
        if (target is null)
        {
            if (blip?.Attribute(R + "link") is not null)
                LeftOut("linked pictures");
            return;
        }
        switch (Path.GetExtension(target).ToLowerInvariant())
        {
            case ".emf" or ".wmf" or ".emz" or ".wmz":
                LeftOut("EMF and WMF pictures");
                return;
            case ".tif" or ".tiff":
                LeftOut("TIFF pictures");
                return;
        }
        if (!_read || _package.Bytes(target, MaxPictureBytes) is not { } data)
            return;
        var properties = picture.Element(_ns + "spPr");
        var crop = picture.Element(_ns + "blipFill")?.Element(A + "srcRect");
        var drawing = new SheetPicture(data, box.Row, box.Column, box.X, box.Y, box.Width, box.Height)
        {
            Crop = (Fraction(crop?.Attribute("l")), Fraction(crop?.Attribute("t")), Fraction(crop?.Attribute("r")), Fraction(crop?.Attribute("b"))),
        };
        Add(Placed(drawing, box, space, properties?.Element(A + "xfrm")));
    }

    /// <summary>A crop edge (1/1000 of a percent of the picture) as a fraction, within what can be drawn.</summary>
    private static double Fraction(XAttribute? value) =>
        int.TryParse((string?)value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) ? Math.Clamp(amount / 100_000.0, -10, 1) : 0;

    // ───────────────────────── Shapes ─────────────────────────

    private void Shape(XElement shape, Box box, Space? space)
    {
        var properties = shape.Element(_ns + "spPr");
        if (properties?.Element(A + "custGeom") is not null)
        {
            LeftOut("freeform shapes");
            return;
        }
        if ((string?)properties?.Element(A + "prstGeom")?.Attribute("prst") is not { } preset)
            return;
        if (!PresetShapes.Known(preset))
        {
            LeftOut($"the preset shape {preset}");
            return;
        }
        // Text linked to a cell (textlink="$A$1") shows the cell's text: the readers put it in once the sheets are read.
        var link = (string?)shape.Attribute("textlink") is { Length: > 0 and <= MaxLinkLength } linked ? linked : null;

        var style = shape.Element(_ns + "style");
        var fill = Fill(properties, style?.Element(A + "fillRef"));
        var (line, width, dash) = Outline(properties?.Element(A + "ln"), style?.Element(A + "lnRef"));
        var adjust = properties?.Element(A + "prstGeom")?.Element(A + "avLst")?.Elements(A + "gd")
            .Where(g => g.Attribute("name") is not null && g.Attribute("fmla") is not null)
            .GroupBy(g => (string)g.Attribute("name")!)
            .ToDictionary(g => g.Key, g => (string)g.First().Attribute("fmla")!, StringComparer.Ordinal);
        if (!_read)
            return;
        var drawing = new SheetShape(preset, box.Row, box.Column, box.X, box.Y, box.Width, box.Height)
        {
            Adjust = adjust is { Count: > 0 } ? adjust : null,
            Fill = fill,
            Line = line,
            LineWidth = width,
            Dash = dash,
            Text = TextOf(shape, link is not null),
            TextLink = link,
        };
        Add(Placed(drawing, box, space, properties?.Element(A + "xfrm")));
    }

    // ───────────────────────── Text in shapes ─────────────────────────

    /// <summary>
    /// The text of a shape or text box (xdr:txBody, DrawingML text: ECMA-376 Part 1, 21.1.2): its paragraphs (a:p with
    /// a:pPr algn, lvl, marL, indent, lnSpc and bullets: buChar in its buFont, buAutoNum, buClr, buSzPct / buSzPts),
    /// runs (a:r, a:fld, a:br) in their a:rPr fonts (latin typeface, sz, b, i, u, strike, solidFill) over the list
    /// style's level (a:lstStyle lvlNpPr) and the shape style's font reference (a:fontRef: the theme's minor or major
    /// font and its colour, Excel's 11 pt), and a:bodyPr's anchor, insets, wrap, vertical text (vert) and the shrink
    /// that "shrink text on overflow" saved (normAutofit fontScale and lnSpcReduction); null when it has no text and
    /// isn't linked to a cell (<paramref name="linked"/>).
    /// </summary>
    private ShapeText? TextOf(XElement shape, bool linked)
    {
        var body = shape.Element(_ns + "txBody");
        if (!linked && (body is null || !body.Descendants(A + "t").Any(t => !string.IsNullOrWhiteSpace(t.Value))))
            return null;
        var fontRef = shape.Element(_ns + "style")?.Element(A + "fontRef");
        var baseColor = _colors.Color(fontRef);
        var baseFont = (string?)fontRef?.Attribute("idx") == "major" ? MajorFont() : _themeFont;
        var bodyPr = body?.Element(A + "bodyPr");
        // "Shrink text on overflow": the size and line spacing Excel shrank the text to when it was saved.
        var autofit = bodyPr?.Element(A + "normAutofit");
        var fontScale = Math.Clamp(Number(autofit?.Attribute("fontScale"), 100_000) / 100_000, 0.01, 1);
        var spacingReduction = Math.Clamp(Number(autofit?.Attribute("lnSpcReduction"), 0) / 100_000, 0, 0.9);
        var lists = body?.Element(A + "lstStyle");
        var characters = 0;
        var counters = new int[9];
        var numbered = new bool[9];
        var paragraphs = new List<ShapeParagraph>();
        foreach (var paragraph in body?.Elements(A + "p").Take(MaxParagraphs) ?? [])
        {
            var properties = paragraph.Element(A + "pPr");
            var level = (int)Math.Clamp(Number(properties?.Attribute("lvl"), 0), 0, 8);
            var list = lists?.Element(A + $"lvl{level + 1}pPr");
            XElement?[] chain = [properties, list];
            XAttribute? Attribute(string name) => chain.Select(e => e?.Attribute(name)).FirstOrDefault(v => v is not null);
            XElement? Child(params string[] names) =>
                chain.Select(e => e?.Elements().FirstOrDefault(c => names.Contains(c.Name.LocalName))).FirstOrDefault(c => c is not null);
            var paragraphRun = Run(properties?.Element(A + "defRPr"), Run(list?.Element(A + "defRPr"), new CellRun("")));
            var runs = new List<CellRun>();
            foreach (var piece in paragraph.Elements())
            {
                if (characters >= MaxTextCharacters)
                    break;
                var text = piece.Name.LocalName switch
                {
                    "r" or "fld" => piece.Element(A + "t")?.Value ?? "",
                    "br" => "\n",
                    _ => null,
                };
                if (string.IsNullOrEmpty(text))
                    continue;
                text = text[..Math.Min(text.Length, MaxTextCharacters - characters)];
                characters += text.Length;
                var run = Scaled(Run(piece.Element(A + "rPr"), paragraphRun) with { Text = text }, fontScale);
                if (runs.Count > 0 && runs[^1] with { Text = "" } == run with { Text = "" })
                    runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
                else
                    runs.Add(run);
            }
            // The first run's font (or the paragraph's end mark's) is the bullet's.
            var mark = runs.FirstOrDefault() ?? Scaled(Run(paragraph.Element(A + "endParaRPr"), paragraphRun), fontScale);
            var spacing = Child("lnSpc");
            var exact = spacing?.Element(A + "spcPts")?.Attribute("val") is { } points ? Math.Clamp(Number(points, 0) / 100 * fontScale, 0.5, 1584) : (double?)null;
            var multiple = spacing?.Element(A + "spcPct")?.Attribute("val") is { } percent ? Number(percent, 100_000) / 100_000 : 1;
            paragraphs.Add(new ShapeParagraph(runs, (string?)Attribute("algn") switch
            {
                "ctr" => CellAlign.Center,
                "r" => CellAlign.Right,
                _ => CellAlign.Left,
            })
            {
                Bullet = Bullet(Child("buNone", "buChar", "buAutoNum", "buBlip"), Child("buClr", "buClrTx"), Child("buSzPct", "buSzPts", "buSzTx"),
                    Child("buFont", "buFontTx"), mark, runs.Count > 0, level, counters, numbered),
                Left = Math.Clamp(Number(Attribute("marL"), 0) * PointsPerEmu, 0, 4000),
                Indent = Math.Clamp(Number(Attribute("indent"), 0) * PointsPerEmu, -4000, 4000),
                LineSpacing = Math.Clamp(multiple - spacingReduction, 0.1, 10),
                LineHeight = exact,
            });
        }
        double Inset(string name, double fallback) =>
            bodyPr?.Attribute(name) is { } value ? Math.Clamp(Signed(value) * PointsPerEmu, 0, 1000) : fallback;
        return new ShapeText(paragraphs)
        {
            Font = new CellStyle
            {
                FontName = baseFont,
                FontSize = 11 * fontScale,
                Color = baseColor is { } color ? Hex(color) : null,
            },
            Anchor = (string?)bodyPr?.Attribute("anchor") switch
            {
                "ctr" => CellVerticalAlign.Center,
                "b" => CellVerticalAlign.Bottom,
                _ => CellVerticalAlign.Top,
            },
            Insets = (Inset("lIns", 7.2), Inset("tIns", 3.6), Inset("rIns", 7.2), Inset("bIns", 3.6)),
            Wrap = (string?)bodyPr?.Attribute("wrap") != "none",
            Flow = (string?)bodyPr?.Attribute("vert") switch
            {
                "vert" => ShapeTextFlow.Down,
                "vert270" => ShapeTextFlow.Up,
                "eaVert" or "mongolianVert" => ShapeTextFlow.EastAsian,
                "wordArtVert" or "wordArtVertRtl" => ShapeTextFlow.Stacked,
                _ => ShapeTextFlow.Across,
            },
        };
    }

    /// <summary>
    /// Puts into each shape linked to a cell (textlink: "$A$1" on the shape's own sheet, "Sheet2!$B$3", "'My
    /// sheet'!C4") that cell's text as the sheet shows it, in the font and alignment of the shape's first paragraph and
    /// run. A link to a sheet that isn't read (hidden) or to a defined name keeps the text Excel saved with the shape.
    /// </summary>
    internal static void LinkTexts(Workbook book)
    {
        foreach (var sheet in book.Sheets)
        {
            for (var i = 0; i < sheet.Drawings.Count; i++)
            {
                if (sheet.Drawings[i] is not SheetShape { TextLink: { } link } shape || !Linked(book, sheet, link, out var cell))
                    continue;
                var text = shape.Text ?? new ShapeText([]);
                var first = text.Paragraphs.FirstOrDefault();
                var run = first?.Runs.FirstOrDefault() ?? new CellRun("");
                var shown = cell?.Text ?? "";
                var paragraph = new ShapeParagraph(shown.Length == 0 ? [] : [run with { Text = shown[..Math.Min(shown.Length, MaxTextCharacters)] }],
                    first?.Align ?? CellAlign.Left)
                {
                    Bullet = shown.Length == 0 ? null : first?.Bullet,
                    Left = first?.Left ?? 0,
                    Indent = first?.Indent ?? 0,
                    LineSpacing = first?.LineSpacing ?? 1,
                    LineHeight = first?.LineHeight,
                };
                sheet.Drawings[i] = shape with { Text = text with { Paragraphs = [paragraph] } };
            }
        }
    }

    /// <summary>Whether a textlink reference names a cell of a read sheet, and that cell (null when it is empty).</summary>
    private static bool Linked(Workbook book, Worksheet own, string link, out SheetCell? cell)
    {
        cell = null;
        var reference = link.TrimStart('=').Trim();
        var sheet = own;
        var bang = reference.LastIndexOf('!');
        if (bang >= 0)
        {
            var name = reference[..bang];
            if (name.Length >= 2 && name[0] == '\'' && name[^1] == '\'')
                name = name[1..^1].Replace("''", "'", StringComparison.Ordinal);
            sheet = book.Sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            reference = reference[(bang + 1)..];
        }
        if (sheet is null || CellRange.Parse(reference) is not { } range || range.Top > SheetPagination.MaxRow || range.Left is < 0 or > SheetPagination.MaxColumn)
            return false;
        cell = sheet.Get(range.Top, range.Left);
        return true;
    }

    /// <summary>A run with its size (or, setting none, Excel's 11 pt) shrunk by <paramref name="scale"/>.</summary>
    private static CellRun Scaled(CellRun run, double scale) =>
        scale >= 1 ? run : run with { FontSize = Math.Max(1, (run.FontSize ?? 11) * scale) };

    /// <summary>
    /// A DrawingML number (EMU, a count, or a percentage: ST_Percentage's 100000 = 100 %, "62.5%" in strict files),
    /// or <paramref name="fallback"/>.
    /// </summary>
    private static double Number(XAttribute? value, double fallback)
    {
        var text = (string?)value;
        if (text is null)
            return fallback;
        var percent = text.EndsWith('%');
        return double.TryParse(percent ? text[..^1] : text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? Math.Clamp(percent ? number * 1000 : number, -MaxEmu, MaxEmu)
            : fallback;
    }

    /// <summary>
    /// A paragraph's bullet or number (buChar in its buFont, a symbol font's as the Unicode it shows; buAutoNum counted
    /// per level as PowerPoint counts), in the first run's font with buClr and buSzPct / buSzPts; null for none
    /// (buNone, no bullet element, or a paragraph without text).
    /// </summary>
    private CellRun? Bullet(XElement? kind, XElement? color, XElement? size, XElement? font, CellRun text, bool hasText, int level,
        int[] counters, bool[] numbered)
    {
        for (var deeper = level + 1; deeper < counters.Length; deeper++)
            (counters[deeper], numbered[deeper]) = (0, false);
        if (!hasText || kind is null || kind.Name.LocalName == "buNone")
        {
            if (hasText)
                numbered[level] = false;
            return null;
        }
        string label;
        var face = font?.Name.LocalName == "buFont" && (string?)font.Attribute("typeface") is { Length: > 0 } typeface ? typeface : null;
        switch (kind.Name.LocalName)
        {
            case "buAutoNum":
                var start = (int)Math.Clamp(Number(kind.Attribute("startAt"), 1), 1, 32767);
                counters[level] = numbered[level] ? Math.Min(counters[level] + 1, 32767) : start;
                numbered[level] = true;
                label = Hwp.Hwpx.Pptx.PptxReader.Number((string?)kind.Attribute("type") ?? "arabicPeriod", counters[level]);
                face = null;
                break;
            case "buChar":
                numbered[level] = false;
                label = (string?)kind.Attribute("char") is { Length: > 0 } character ? character[..Math.Min(character.Length, 8)] : "•";
                if (Hwp.Hwpx.SymbolFonts.Map(face, label) is { } mapped)
                    (label, face) = (mapped, null);
                break;
            default:
                // A picture bullet: a plain one in its place.
                numbered[level] = false;
                (label, face) = ("•", null);
                break;
        }
        var points = text.FontSize ?? 11;
        if (size?.Name.LocalName == "buSzPct")
            points *= Math.Clamp(Number(size.Attribute("val"), 100_000) / 100_000, 0.25, 4);
        else if (size?.Name.LocalName == "buSzPts")
            points = Math.Clamp(Number(size.Attribute("val"), points * 100) / 100, 1, 4000);
        var shade = color?.Name.LocalName == "buClr" && _colors.Color(color) is { } bulletColor ? Hex(bulletColor) : text.Color;
        return new CellRun(label) { FontName = face ?? text.FontName, FontSize = points, Color = shade, Bold = text.Bold, Italic = text.Italic };
    }

    /// <summary>A run's font from DrawingML run properties over <paramref name="under"/> (what they don't set stays).</summary>
    private CellRun Run(XElement? properties, CellRun under)
    {
        if (properties is null)
            return under;
        bool? Flag(string name) => (string?)properties.Attribute(name) switch
        {
            "1" or "true" => true,
            "0" or "false" => false,
            _ => null,
        };
        var size = int.TryParse((string?)properties.Attribute("sz"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hundredths)
            ? Math.Clamp(hundredths / 100.0, 1, 4000)
            : (double?)null;
        var typeface = (string?)properties.Element(A + "latin")?.Attribute("typeface") switch
        {
            "+mn-lt" => _themeFont,
            "+mj-lt" => MajorFont(),
            { Length: > 0 } name => name,
            _ => null,
        };
        var color = properties.Element(A + "solidFill") is { } fill ? _colors.Color(fill) : null;
        return under with
        {
            Bold = Flag("b") ?? under.Bold,
            Italic = Flag("i") ?? under.Italic,
            Underline = (string?)properties.Attribute("u") is { } u ? u != "none" : under.Underline,
            Strike = (string?)properties.Attribute("strike") is { } strike ? strike != "noStrike" : under.Strike,
            FontSize = size ?? under.FontSize,
            FontName = typeface ?? under.FontName,
            Color = color is { } c ? Hex(c) : under.Color,
            Script = int.TryParse((string?)properties.Attribute("baseline"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var baseline) && baseline != 0
                ? Math.Sign(baseline)
                : under.Script,
        };
    }

    /// <summary>The theme's major (headings) latin font, or null.</summary>
    private string? MajorFont() =>
        (string?)_theme?.Descendants(A + "majorFont").FirstOrDefault()?.Element(A + "latin")?.Attribute("typeface") is { Length: > 0 } font ? font : null;

    private static string Hex(SKColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    private SKColor? Fill(XElement? properties, XElement? reference)
    {
        var fill = properties?.Elements().FirstOrDefault(e => e.Name.LocalName is "noFill" or "solidFill" or "gradFill" or "pattFill" or "blipFill" or "grpFill");
        switch (fill?.Name.LocalName)
        {
            case "noFill":
                return null;
            case "solidFill":
                return _colors.Color(fill);
            case "gradFill":
                LeftOut("gradient fills");
                return _colors.Color(fill.Element(A + "gsLst")?.Elements(A + "gs").FirstOrDefault());
            case "pattFill":
                LeftOut("pattern fills");
                return _colors.Color(fill.Element(A + "fgClr"));
            case "blipFill":
                LeftOut("picture fills");
                return null;
            case "grpFill":
                return null;
        }
        // The theme's fill style the shape refers to (idx 0 = none), in the colour given with the reference.
        return Index(reference) > 0 ? _colors.Color(reference) : null;
    }

    private (SKColor? Color, double Width, float[]? Dash) Outline(XElement? line, XElement? reference)
    {
        var index = Index(reference);
        var themed = index > 0 ? _formatScheme?.Element(A + "lnStyleLst")?.Elements(A + "ln").ElementAtOrDefault(index - 1) : null;
        var fill = line?.Elements().FirstOrDefault(e => e.Name.LocalName is "noFill" or "solidFill" or "gradFill" or "pattFill");
        SKColor? color;
        switch (fill?.Name.LocalName)
        {
            case "noFill":
                return (null, 0, null);
            case "solidFill":
                color = _colors.Color(fill);
                break;
            case "gradFill":
                LeftOut("gradient lines");
                color = _colors.Color(fill.Element(A + "gsLst")?.Elements(A + "gs").FirstOrDefault());
                break;
            case "pattFill":
                LeftOut("pattern lines");
                color = _colors.Color(fill.Element(A + "fgClr"));
                break;
            default:
                if (index <= 0)
                    return (null, 0, null);
                color = _colors.Color(reference);
                break;
        }
        foreach (var end in new[] { line?.Element(A + "headEnd"), line?.Element(A + "tailEnd") })
        {
            if ((string?)end?.Attribute("type") is { } type && type != "none")
                LeftOut("arrowheads");
        }
        var emu = line?.Attribute("w") is { } w ? Unsigned(w) : themed?.Attribute("w") is { } tw ? Unsigned(tw) : 9525;
        var dash = (string?)line?.Element(A + "prstDash")?.Attribute("val") ?? (string?)themed?.Element(A + "prstDash")?.Attribute("val");
        return (color, Math.Min(emu * PointsPerEmu, 1584), dash is null ? null : Dash(dash));
    }

    /// <summary>DrawingML's preset dashes (ECMA-376 Part 1, 20.1.10.48) as multiples of the line width.</summary>
    private static float[]? Dash(string preset) => preset switch
    {
        "dot" => [1, 3],
        "dash" => [4, 3],
        "lgDash" => [8, 3],
        "dashDot" => [4, 3, 1, 3],
        "lgDashDot" => [8, 3, 1, 3],
        "lgDashDotDot" => [8, 3, 1, 3, 1, 3],
        "sysDot" => [1, 1],
        "sysDash" => [3, 1],
        "sysDashDot" => [3, 1, 1, 1],
        "sysDashDotDot" => [3, 1, 1, 1, 1, 1],
        _ => null,
    };

    private static int Index(XElement? reference) =>
        int.TryParse((string?)reference?.Attribute("idx"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ? index : 0;

    // ───────────────────────── Placement ─────────────────────────

    /// <summary>The turn and mirroring of an a:xfrm; null when there is none.</summary>
    private static (double Rotation, bool FlipH, bool FlipV)? Turn(XElement? xfrm)
    {
        if (xfrm is null)
            return null;
        var rotation = long.TryParse((string?)xfrm.Attribute("rot"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rot)
            ? (rot % 21_600_000 + 21_600_000) % 21_600_000 / 60_000.0
            : 0;
        return (rotation, (string?)xfrm.Attribute("flipH") is "1" or "true", (string?)xfrm.Attribute("flipV") is "1" or "true");
    }

    /// <summary>
    /// The drawing with its turn and mirroring, and its share of the anchor's box inside a group (whose child
    /// coordinates are its unturned box) or, at the top, its unturned size.
    /// </summary>
    private static SheetDrawing Placed(SheetDrawing drawing, Box box, Space? space, XElement? xfrm)
    {
        var (rotation, flipH, flipV) = Turn(xfrm) ?? (0, false, false);
        drawing = drawing with { To = box.To, Rotation = rotation, FlipH = flipH, FlipV = flipV };
        var (x, y) = (Signed(xfrm?.Element(A + "off")?.Attribute("x")), Signed(xfrm?.Element(A + "off")?.Attribute("y")));
        var (cx, cy) = (Signed(xfrm?.Element(A + "ext")?.Attribute("cx")), Signed(xfrm?.Element(A + "ext")?.Attribute("cy")));
        if (space is { } inside)
            return drawing with { Part = inside.Map(x, y, Math.Max(cx, 0), Math.Max(cy, 0)) };
        return cx > 0 && cy > 0 ? drawing with { Size = (cx, cy) } : drawing;
    }

    private static long Unsigned(XObject? node) => Math.Max(0, Signed(node));

    private static long Signed(XObject? node)
    {
        var text = node switch { XElement e => e.Value, XAttribute a => a.Value, _ => null };
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, -MaxEmu, MaxEmu) : 0;
    }
}
