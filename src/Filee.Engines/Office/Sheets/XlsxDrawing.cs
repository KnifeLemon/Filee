// The pictures and shapes of a worksheet's drawing part (xl/drawings/drawingN.xml, DrawingML's spreadsheet drawing,
// ECMA-376 Part 1, 20.5), read for the sheet printer: pictures with their turn, mirroring and cropping (a:xfrm rot,
// flipH, flipV and a:srcRect), preset shapes with one fill and one outline colour (the shape's own or its theme
// style's, through the slide renderer's preset definitions), and groups of both, each child placed in its share of the
// group's anchor. What the printer doesn't draw is named instead: charts, SmartArt, embedded objects, ink, text in
// shapes, freeform shapes, gradient, pattern and picture fills, arrowheads, turned groups, EMF / WMF / TIFF and linked
// pictures. SheetCapabilities asks for that name, so such a workbook can go to LibreOffice where it is installed.
//
// Excel anchors a turned picture or shape by the box it covers on the sheet: one turned by 90° has an anchor as wide as
// the picture is tall. The unturned size (a:xfrm ext) tells which way round the anchor is (SheetDrawing.Size).
// Effects (shadows, glow, 3D) are left out without a word: the shape itself is drawn.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Office.Ooxml;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Office.Sheets;

internal sealed class XlsxDrawing
{
    private static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private static readonly XNamespace A = DrawingColors.A;
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

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

    /// <summary>The largest EMU value taken from a position or size (about 4 km); larger ones are clamped.</summary>
    private const long MaxEmu = 50_000_000_000;

    private readonly OpcPackage _package;
    private readonly string _part;
    private readonly DrawingColors _colors;
    private readonly XElement? _formatScheme;
    private readonly bool _read;

    /// <summary>What is drawn, in drawing order; empty when only the check was asked for.</summary>
    public List<SheetDrawing> Drawings { get; } = [];

    /// <summary>The first thing in the part the printer doesn't draw, or null.</summary>
    public string? FirstLeftOut { get; private set; }

    private XlsxDrawing(OpcPackage package, string part, XElement? theme, bool read)
    {
        _package = package;
        _part = part;
        _colors = DrawingColors.FromTheme(theme);
        _formatScheme = theme?.Descendants(A + "fmtScheme").FirstOrDefault();
        _read = read;
    }

    /// <summary>
    /// Reads drawing part <paramref name="part"/>: its pictures and shapes (with <paramref name="read"/>; without it,
    /// no picture is unpacked and only <see cref="FirstLeftOut"/> is found).
    /// </summary>
    /// <param name="theme">The workbook's theme part (colours and line styles shapes refer to), or null.</param>
    public static XlsxDrawing Read(OpcPackage package, string part, XElement? theme, bool read)
    {
        var drawing = new XlsxDrawing(package, part, theme, read);
        foreach (var anchor in package.Part(part)?.Root?.Elements() ?? [])
            drawing.Anchor(anchor);
        return drawing;
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
            var chosen = anchor.Elements().FirstOrDefault(e => e.Name.LocalName == "Fallback") ?? anchor.Elements().FirstOrDefault(e => e.Name.LocalName == "Choice");
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

    private static bool IsObject(XElement e) =>
        e.Name.LocalName is "sp" or "grpSp" or "graphicFrame" or "cxnSp" or "pic" or "contentPart" or "AlternateContent";

    /// <summary>One object of an anchor or of a group (<paramref name="space"/> maps a group's child coordinates).</summary>
    private void Object(XElement element, Box box, Space? space, int depth)
    {
        if (element.Name.LocalName == "AlternateContent")
        {
            var chosen = element.Elements().FirstOrDefault(e => e.Name.LocalName == "Fallback") ?? element.Elements().FirstOrDefault(e => e.Name.LocalName == "Choice");
            if (chosen?.Elements().FirstOrDefault(IsObject) is { } inner)
                Object(inner, box, space, depth);
            return;
        }
        var properties = element.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("nv", StringComparison.Ordinal))?.Element(Xdr + "cNvPr");
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
                LeftOut(Graphic((string?)element.Element(A + "graphic")?.Element(A + "graphicData")?.Attribute("uri") ?? ""));
                break;
            case "contentPart":
                LeftOut("ink");
                break;
        }
    }

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
        var xfrm = group.Element(Xdr + "grpSpPr")?.Element(A + "xfrm");
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
        var blip = picture.Element(Xdr + "blipFill")?.Element(A + "blip");
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
        var properties = picture.Element(Xdr + "spPr");
        var crop = picture.Element(Xdr + "blipFill")?.Element(A + "srcRect");
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
        var properties = shape.Element(Xdr + "spPr");
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
        // The shape is drawn; its text is not (yet).
        if (shape.Element(Xdr + "txBody")?.Descendants(A + "t").Any(t => !string.IsNullOrWhiteSpace(t.Value)) == true
            || !string.IsNullOrEmpty((string?)shape.Attribute("textlink")))
            LeftOut("text in shapes");

        var style = shape.Element(Xdr + "style");
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
        };
        Add(Placed(drawing, box, space, properties?.Element(A + "xfrm")));
    }

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
