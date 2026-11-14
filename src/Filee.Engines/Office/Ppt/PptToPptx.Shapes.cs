// The drawing of a slide, master or title master ([MS-ODRAW] through the shared OfficeArt code) as a PresentationML
// shape tree: the DrawingContainer's OfficeArtDgContainer holds the group of all shapes (the patriarch) and the
// background shape. Each OfficeArtSpContainer becomes a p:sp, p:cxnSp (lines and connectors) or p:pic, each group a
// p:grpSp with its child coordinate space (FSPGR), in drawing order.
//
// - Position: the client anchor (master units, 576 per inch) of a shape on the slide, the child anchor of a shape in a
//   group; a shape turned by 45–135° or 225–315° stores the box it covers turned, so its width and height swap back.
// - Properties: the shape's own property tables, over those of its master shape (hspMaster: a slide's placeholder
//   takes what it doesn't set from the master's), over the drawing group's defaults, over [MS-ODRAW]'s defaults.
//   Boolean property sets merge bit by bit (each bit counts where its "use" bit is set).
// - Geometry: the shape type (MSOSPT) as the preset of the same shape (prstGeom); a freeform (custom geometry) and
//   WordArt are left out for now (Dropped).
// - Fill: solid (with opacity) and picture / texture fills from the BLIP store; gradients and patterns in their first
//   colour. Line: colour, width, dash, arrowheads. Colours: RGB, or the colour scheme by index as theme colours.
// - Pictures: a picture frame, or any shape with a picture (OLE objects, charts and media show theirs), with its crop.
// - Placeholders: OEPlaceholderAtom's kind as the p:ph type and index the layout and master match.
// - Text: the client text box's text, or the slide list's outline text an OutlineTextRefAtom names; body properties
//   (insets, anchoring, wrapping, text direction) from the shape's properties.

using System.Globalization;
using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Office.OfficeArt;

namespace Filee.Engines.Office.Ppt;

internal sealed partial class PptToPptx
{
    // FSP flags not in OfficeArtShape.
    private const uint OleShape = 0x10;
    private const uint HaveMaster = 0x20;
    private const uint Connector = 0x100;

    // Properties.
    private const int Rotation = OfficeArtProperties.Rotation;
    private const int WrapText = 0x0085;
    private const int AnchorText = 0x0087;
    private const int TextFlow = 0x0088;
    private const int LineOpacity = 0x01C1;
    private const int LineBackColor = 0x01C2;
    private const int LineDashing = 0x01CE;
    private const int LineStartArrow = 0x01D0;
    private const int LineEndArrow = 0x01D1;
    private const int LineStartArrowWidth = 0x01D2;
    private const int LineStartArrowLength = 0x01D3;
    private const int LineEndArrowWidth = 0x01D4;
    private const int LineEndArrowLength = 0x01D5;
    private const int MasterShape = 0x0301;

    /// <summary>Master shapes followed from a shape at most (a title master's placeholder names the main master's).</summary>
    private const int MaxMasterChain = 4;

    /// <summary>MSOSPT shape types and the DrawingML presets of the same shapes.</summary>
    private static readonly Dictionary<int, string> Presets = new()
    {
        [1] = "rect",
        [2] = "roundRect",
        [3] = "ellipse",
        [4] = "diamond",
        [5] = "triangle",
        [6] = "rtTriangle",
        [7] = "parallelogram",
        [8] = "trapezoid",
        [9] = "hexagon",
        [10] = "octagon",
        [11] = "plus",
        [12] = "star5",
        [13] = "rightArrow",
        [14] = "rightArrow",
        [15] = "homePlate",
        [16] = "cube",
        [17] = "wedgeRoundRectCallout",
        [18] = "star16",
        [19] = "arc",
        [20] = "line",
        [21] = "plaque",
        [22] = "can",
        [23] = "donut",
        [32] = "straightConnector1",
        [33] = "bentConnector2",
        [34] = "bentConnector3",
        [35] = "bentConnector4",
        [36] = "bentConnector5",
        [37] = "curvedConnector2",
        [38] = "curvedConnector3",
        [39] = "curvedConnector4",
        [40] = "curvedConnector5",
        [41] = "callout1",
        [42] = "callout2",
        [43] = "callout3",
        [44] = "accentCallout1",
        [45] = "accentCallout2",
        [46] = "accentCallout3",
        [47] = "borderCallout1",
        [48] = "borderCallout2",
        [49] = "borderCallout3",
        [50] = "accentBorderCallout1",
        [51] = "accentBorderCallout2",
        [52] = "accentBorderCallout3",
        [53] = "ribbon",
        [54] = "ribbon2",
        [55] = "chevron",
        [56] = "pentagon",
        [57] = "noSmoking",
        [58] = "star8",
        [59] = "star16",
        [60] = "star32",
        [61] = "wedgeRectCallout",
        [62] = "wedgeRoundRectCallout",
        [63] = "wedgeEllipseCallout",
        [64] = "wave",
        [65] = "foldedCorner",
        [66] = "leftArrow",
        [67] = "downArrow",
        [68] = "upArrow",
        [69] = "leftRightArrow",
        [70] = "upDownArrow",
        [71] = "irregularSeal1",
        [72] = "irregularSeal2",
        [73] = "lightningBolt",
        [74] = "heart",
        [75] = "rect",
        [76] = "quadArrow",
        [77] = "leftArrowCallout",
        [78] = "rightArrowCallout",
        [79] = "upArrowCallout",
        [80] = "downArrowCallout",
        [81] = "leftRightArrowCallout",
        [82] = "upDownArrowCallout",
        [83] = "quadArrowCallout",
        [84] = "bevel",
        [85] = "leftBracket",
        [86] = "rightBracket",
        [87] = "leftBrace",
        [88] = "rightBrace",
        [89] = "leftUpArrow",
        [90] = "bentUpArrow",
        [91] = "bentArrow",
        [92] = "star24",
        [93] = "stripedRightArrow",
        [94] = "notchedRightArrow",
        [95] = "blockArc",
        [96] = "smileyFace",
        [97] = "verticalScroll",
        [98] = "horizontalScroll",
        [99] = "circularArrow",
        [100] = "circularArrow",
        [101] = "uturnArrow",
        [102] = "curvedRightArrow",
        [103] = "curvedLeftArrow",
        [104] = "curvedUpArrow",
        [105] = "curvedDownArrow",
        [106] = "cloudCallout",
        [107] = "ellipseRibbon",
        [108] = "ellipseRibbon2",
        [109] = "flowChartProcess",
        [110] = "flowChartDecision",
        [111] = "flowChartInputOutput",
        [112] = "flowChartPredefinedProcess",
        [113] = "flowChartInternalStorage",
        [114] = "flowChartDocument",
        [115] = "flowChartMultidocument",
        [116] = "flowChartTerminator",
        [117] = "flowChartPreparation",
        [118] = "flowChartManualInput",
        [119] = "flowChartManualOperation",
        [120] = "flowChartConnector",
        [121] = "flowChartPunchedCard",
        [122] = "flowChartPunchedTape",
        [123] = "flowChartSummingJunction",
        [124] = "flowChartOr",
        [125] = "flowChartCollate",
        [126] = "flowChartSort",
        [127] = "flowChartExtract",
        [128] = "flowChartMerge",
        [129] = "flowChartOfflineStorage",
        [130] = "flowChartOnlineStorage",
        [131] = "flowChartMagneticTape",
        [132] = "flowChartMagneticDisk",
        [133] = "flowChartMagneticDrum",
        [134] = "flowChartDisplay",
        [135] = "flowChartDelay",
        [176] = "flowChartAlternateProcess",
        [177] = "flowChartOffpageConnector",
        [178] = "callout1",
        [179] = "accentCallout1",
        [180] = "borderCallout1",
        [181] = "accentBorderCallout1",
        [182] = "leftRightUpArrow",
        [183] = "sun",
        [184] = "moon",
        [185] = "bracketPair",
        [186] = "bracePair",
        [187] = "star4",
        [188] = "doubleWave",
        [189] = "actionButtonBlank",
        [190] = "actionButtonHome",
        [191] = "actionButtonHelp",
        [192] = "actionButtonInformation",
        [193] = "actionButtonForwardNext",
        [194] = "actionButtonBackPrevious",
        [195] = "actionButtonEnd",
        [196] = "actionButtonBeginning",
        [197] = "actionButtonReturn",
        [198] = "actionButtonDocument",
        [199] = "actionButtonSound",
        [200] = "actionButtonMovie",
        [201] = "rect",
        [202] = "rect",
    };

    /// <summary>A box in master units (or a group's child units).</summary>
    private readonly record struct Box(double X, double Y, double Width, double Height);

    /// <summary>A placeholder: its kind (PlaceholderEnum) and position.</summary>
    private readonly record struct PlaceholderInfo(int Placement, int Position);

    /// <summary>The drawing's background shape, found while its shapes are written.</summary>
    private OfficeArtShape? _background;

    /// <summary>Master shapes by shape id, from every master's drawing (read on first use).</summary>
    private Dictionary<int, OfficeArtShape>? _masterShapes;

    // ───────────────────────── Drawings and groups ─────────────────────────

    /// <summary>Writes the shapes of a slide's, master's or notes page's drawing into <paramref name="tree"/>; returns its background shape.</summary>
    private OfficeArtShape? WriteDrawing(StringBuilder tree, OfficeArtRecord container, Sheet sheet)
    {
        var bytes = _file.Stream;
        _background = null;
        if (OfficeArtRecords.Child(bytes, container, PptRecords.Drawing) is not { IsContainer: true } drawing
            || OfficeArtRecords.Child(bytes, drawing, OfficeArtRecords.DgContainer) is not { IsContainer: true } dg)
            return null;
        foreach (var child in OfficeArtRecords.Children(bytes, dg))
        {
            if (child.Type == OfficeArtRecords.SpgrContainer && child.IsContainer)
            {
                using var level = _budget.Enter();
                WriteChildren(tree, OfficeArtRecords.Children(bytes, child), 0, sheet);
            }
            else if (child.Type == OfficeArtRecords.SpContainer && child.IsContainer)
            {
                var shape = OfficeArtShape.Read(bytes, child);
                if ((shape.Flags & OfficeArtShape.Background) != 0)
                    _background ??= shape;
            }
        }
        return _background;
    }

    private void WriteChildren(StringBuilder xml, List<OfficeArtRecord> children, int start, Sheet sheet)
    {
        var bytes = _file.Stream;
        for (var i = start; i < children.Count; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var child = children[i];
            if (!child.IsContainer)
                continue;
            if (child.Type == OfficeArtRecords.SpgrContainer)
            {
                WriteGroup(xml, child, sheet);
            }
            else if (child.Type == OfficeArtRecords.SpContainer)
            {
                var shape = OfficeArtShape.Read(bytes, child);
                if ((shape.Flags & OfficeArtShape.Background) != 0)
                    _background ??= shape;
                else if ((shape.Flags & (OfficeArtShape.Patriarch | OfficeArtShape.Deleted)) == 0)
                    WriteShape(xml, shape, sheet);
            }
        }
    }

    private void WriteGroup(StringBuilder xml, OfficeArtRecord group, Sheet sheet)
    {
        using var level = _budget.Enter();
        _budget.Object();
        var bytes = _file.Stream;
        var children = OfficeArtRecords.Children(bytes, group);
        var shape = children.Count > 0 && children[0].Type == OfficeArtRecords.SpContainer ? OfficeArtShape.Read(bytes, children[0]) : null;
        if (shape is null || (shape.Flags & OfficeArtShape.Group) == 0 || Anchor(shape, Properties(shape, defaults: false)) is not { } box)
        {
            // No group shape or no place: its shapes as they are.
            WriteChildren(xml, children, shape is null ? 0 : 1, sheet);
            return;
        }
        var space = shape.Records.FirstOrDefault(r => r.Type == OfficeArtRecords.Fspgr && r.Length >= 16) is { Type: OfficeArtRecords.Fspgr } fspgr
            ? new Box(PptRecords.I32(bytes, fspgr.Start), PptRecords.I32(bytes, fspgr.Start + 4),
                (double)PptRecords.I32(bytes, fspgr.Start + 8) - PptRecords.I32(bytes, fspgr.Start),
                (double)PptRecords.I32(bytes, fspgr.Start + 12) - PptRecords.I32(bytes, fspgr.Start + 4))
            : box;
        var properties = Properties(shape, defaults: false);
        var id = sheet.NextId();
        xml.Append(CultureInfo.InvariantCulture, $"<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"{id}\" name=\"Group {id - 1}\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr>");
        xml.Append(Xfrm(box, properties, shape.Flags,
            $"<a:chOff x=\"{Emu(space.X)}\" y=\"{Emu(space.Y)}\"/><a:chExt cx=\"{Emu(Math.Max(0, space.Width))}\" cy=\"{Emu(Math.Max(0, space.Height))}\"/>"));
        xml.Append("</p:grpSpPr>");
        WriteChildren(xml, children, 1, sheet);
        xml.Append("</p:grpSp>");
    }

    // ───────────────────────── Shapes ─────────────────────────

    private void WriteShape(StringBuilder xml, OfficeArtShape shape, Sheet sheet)
    {
        _budget.Object();
        var properties = Properties(shape, defaults: true);
        if (Anchor(shape, properties) is not { } box)
            return;
        var placeholder = Placeholder(shape);
        var ph = placeholder is { } p ? PlaceholderXml(p) : "";
        var pib = Properties(shape, defaults: false).GetValueOrDefault(OfficeArtProperties.Pib);
        if (pib != 0 && pib <= _file.Blips.Count)
        {
            WritePicture(xml, shape, box, pib, ph, sheet);
            return;
        }
        if ((shape.Flags & OleShape) != 0)
        {
            Drop("OLE objects without a picture");
            return;
        }
        var text = TextOf(shape, sheet);
        string? preset;
        if (shape.Type is >= 136 and <= 175)
        {
            Drop("WordArt");
            return;
        }
        if (!Presets.TryGetValue(shape.Type, out preset))
        {
            Drop("freeform shapes");
            if (text is null)
                return;
        }

        var id = sheet.NextId();
        var line = (shape.Flags & Connector) != 0 || shape.Type is 20 or (>= 32 and <= 40);
        var spPr = new StringBuilder("<p:spPr>").Append(Xfrm(box, properties, shape.Flags, ""));
        spPr.Append("<a:prstGeom prst=\"").Append(preset ?? "rect").Append("\"><a:avLst/></a:prstGeom>");
        // A placeholder is filled and outlined only where it or its master says so, not by the drawing's defaults.
        var own = placeholder is null ? null : Properties(shape, defaults: false);
        var filled = own is null || Uses(own, OfficeArtProperties.FillBooleans, 4);
        var outlined = own is null || Uses(own, OfficeArtProperties.LineBooleans, 3);
        if (preset is null)
            spPr.Append("<a:noFill/><a:ln><a:noFill/></a:ln>");
        else
            spPr.Append(line || !filled ? "<a:noFill/>" : FillXml(properties, sheet) ?? "<a:noFill/>")
                .Append(outlined ? LineXml(properties) : "<a:ln><a:noFill/></a:ln>");
        spPr.Append("</p:spPr>");

        if (line && text is null && placeholder is null)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"{id}\" name=\"Connector {id - 1}\"/><p:cNvCxnSpPr/><p:nvPr/></p:nvCxnSpPr>")
                .Append(spPr).Append("</p:cxnSp>");
            return;
        }
        var box202 = shape.Type == 202 && placeholder is null ? " txBox=\"1\"" : "";
        var locks = placeholder is null ? "" : "<a:spLocks noGrp=\"1\"/>";
        xml.Append(CultureInfo.InvariantCulture, $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{(placeholder is null ? "Shape" : "Placeholder")} {id - 1}\"/>")
            .Append(locks.Length == 0 ? $"<p:cNvSpPr{box202}/>" : $"<p:cNvSpPr{box202}>{locks}</p:cNvSpPr>")
            .Append("<p:nvPr>").Append(ph).Append("</p:nvPr></p:nvSpPr>").Append(spPr);
        if (text is not null)
            WriteTextBody(xml, text, BodyProperties(properties), sheet.Styles, placeholder is not null);
        xml.Append("</p:sp>");
    }

    private void WritePicture(StringBuilder xml, OfficeArtShape shape, Box box, uint pib, string ph, Sheet sheet)
    {
        if (MediaPart(pib) is not { } media)
            return;
        var properties = Properties(shape, defaults: true);
        var own = Properties(shape, defaults: false);
        var id = sheet.NextId();
        var embed = sheet.Relationship("image", media);
        xml.Append(CultureInfo.InvariantCulture, $"<p:pic><p:nvPicPr><p:cNvPr id=\"{id}\" name=\"Picture {id - 1}\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr>")
            .Append("<p:nvPr>").Append(ph).Append("</p:nvPr></p:nvPicPr>")
            .Append("<p:blipFill><a:blip r:embed=\"").Append(embed).Append("\"/>");
        int Crop(int pid) => (int)Math.Clamp(Math.Round((int)own.GetValueOrDefault(pid) / 65536.0 * 100000), -1000000, 1000000);
        var (left, top, right, bottom) = (Crop(OfficeArtProperties.CropFromLeft), Crop(OfficeArtProperties.CropFromTop),
            Crop(OfficeArtProperties.CropFromRight), Crop(OfficeArtProperties.CropFromBottom));
        if ((left, top, right, bottom) != (0, 0, 0, 0))
            xml.Append(CultureInfo.InvariantCulture, $"<a:srcRect l=\"{left}\" t=\"{top}\" r=\"{right}\" b=\"{bottom}\"/>");
        xml.Append("<a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr>").Append(Xfrm(box, properties, shape.Flags, ""))
            .Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>");
        // A picture's frame is drawn only when the picture (or its master) turns it on; the drawing's default line doesn't count.
        if (OfficeArtProperties.Flag(own, OfficeArtProperties.LineBooleans, 3, false))
            xml.Append(LineXml(properties));
        xml.Append("</p:spPr></p:pic>");
    }

    /// <summary>a:xfrm of a box with the shape's rotation and flips, and the group's child space inside when given.</summary>
    private static string Xfrm(Box box, Dictionary<int, uint> properties, uint flags, string child)
    {
        var sb = new StringBuilder("<a:xfrm");
        var rotation = Degrees(properties);
        if (rotation != 0)
            sb.Append(" rot=\"").Append(((long)Math.Round(rotation * 60000)).ToString(CultureInfo.InvariantCulture)).Append('"');
        if ((flags & OfficeArtShape.FlipH) != 0)
            sb.Append(" flipH=\"1\"");
        if ((flags & OfficeArtShape.FlipV) != 0)
            sb.Append(" flipV=\"1\"");
        sb.Append("><a:off x=\"").Append(Emu(box.X)).Append("\" y=\"").Append(Emu(box.Y)).Append("\"/><a:ext cx=\"")
            .Append(Emu(Math.Max(0, box.Width))).Append("\" cy=\"").Append(Emu(Math.Max(0, box.Height))).Append("\"/>")
            .Append(child).Append("</a:xfrm>");
        return sb.ToString();
    }

    /// <summary>The rotation in degrees, 0 ≤ r &lt; 360 (16.16 fixed point in the file).</summary>
    private static double Degrees(Dictionary<int, uint> properties)
    {
        var degrees = (int)properties.GetValueOrDefault(Rotation) / 65536.0 % 360;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    /// <summary>
    /// The shape's box from its client anchor (on the slide; SmallRectStruct or RectStruct: top, left, right, bottom) or
    /// child anchor (in a group: left, top, right, bottom); null without one. Turned by about a quarter, the anchor is
    /// the turned box: its sides swap around the centre.
    /// </summary>
    private Box? Anchor(OfficeArtShape shape, Dictionary<int, uint> properties)
    {
        var bytes = _file.Stream;
        Box? box = null;
        foreach (var record in shape.Records)
        {
            if (record.Type == OfficeArtRecords.ClientAnchor && record.Length is >= 8 and < 16)
            {
                var (top, left, right, bottom) = (PptRecords.I16(bytes, record.Start), PptRecords.I16(bytes, record.Start + 2),
                    PptRecords.I16(bytes, record.Start + 4), PptRecords.I16(bytes, record.Start + 6));
                box = new Box(left, top, right - left, bottom - top);
            }
            else if (record.Type == OfficeArtRecords.ClientAnchor && record.Length >= 16)
            {
                var (top, left, right, bottom) = (PptRecords.I32(bytes, record.Start), PptRecords.I32(bytes, record.Start + 4),
                    PptRecords.I32(bytes, record.Start + 8), PptRecords.I32(bytes, record.Start + 12));
                box = new Box(left, top, (double)right - left, (double)bottom - top);
            }
            else if (record.Type == OfficeArtRecords.ChildAnchor && record.Length >= 16)
            {
                var (left, top, right, bottom) = (PptRecords.I32(bytes, record.Start), PptRecords.I32(bytes, record.Start + 4),
                    PptRecords.I32(bytes, record.Start + 8), PptRecords.I32(bytes, record.Start + 12));
                box = new Box(left, top, (double)right - left, (double)bottom - top);
            }
        }
        if (box is not { } b)
            return null;
        var rotation = Degrees(properties);
        if (rotation is >= 45 and < 135 or >= 225 and < 315)
            return new Box(b.X + (b.Width - b.Height) / 2, b.Y + (b.Height - b.Width) / 2, b.Height, b.Width);
        return b;
    }

    /// <summary>The OEPlaceholderAtom in the shape's client data, if it is a placeholder.</summary>
    private PlaceholderInfo? Placeholder(OfficeArtShape shape)
    {
        var bytes = _file.Stream;
        foreach (var record in shape.Records)
        {
            if (record.Type != OfficeArtRecords.ClientData || !record.IsContainer)
                continue;
            if (OfficeArtRecords.Child(bytes, record, PptRecords.PlaceholderAtom) is { Length: >= 8 } atom && bytes[atom.Start + 4] != 0)
                return new PlaceholderInfo(bytes[atom.Start + 4], PptRecords.I32(bytes, atom.Start));
        }
        return null;
    }

    /// <summary>The p:ph of a placeholder kind: the type and index the synthesised layouts and masters match.</summary>
    private static string PlaceholderXml(PlaceholderInfo placeholder)
    {
        var index = Math.Clamp(placeholder.Position, 1, 1000).ToString(CultureInfo.InvariantCulture);
        return placeholder.Placement switch
        {
            1 or 13 or 17 => "<p:ph type=\"title\"/>",
            3 or 15 => "<p:ph type=\"ctrTitle\"/>",
            2 or 6 or 12 or 14 or 18 => $"<p:ph type=\"body\" idx=\"{index}\"/>",
            4 or 16 => $"<p:ph type=\"subTitle\" idx=\"{index}\"/>",
            5 or 11 => "<p:ph type=\"sldImg\"/>",
            7 => "<p:ph type=\"dt\" sz=\"half\" idx=\"10\"/>",
            8 => "<p:ph type=\"sldNum\" sz=\"quarter\" idx=\"12\"/>",
            9 => "<p:ph type=\"ftr\" sz=\"quarter\" idx=\"11\"/>",
            10 => "<p:ph type=\"hdr\" sz=\"quarter\" idx=\"13\"/>",
            20 => $"<p:ph type=\"chart\" idx=\"{index}\"/>",
            21 => $"<p:ph type=\"tbl\" idx=\"{index}\"/>",
            22 => $"<p:ph type=\"clipArt\" idx=\"{index}\"/>",
            23 => $"<p:ph type=\"dgm\" idx=\"{index}\"/>",
            24 => $"<p:ph type=\"media\" idx=\"{index}\"/>",
            26 => $"<p:ph type=\"pic\" idx=\"{index}\"/>",
            _ => $"<p:ph idx=\"{index}\"/>",
        };
    }

    /// <summary>The text of a shape: its client text box's, or the outline text it refers to.</summary>
    private ShapeText? TextOf(OfficeArtShape shape, Sheet? sheet)
    {
        var bytes = _file.Stream;
        foreach (var record in shape.Records)
        {
            if (record.Type != OfficeArtRecords.ClientTextbox || !record.IsContainer)
                continue;
            var records = OfficeArtRecords.Children(bytes, record);
            if (records.FirstOrDefault(r => r.Type == PptRecords.OutlineTextRefAtom && r.Length >= 4) is { Type: PptRecords.OutlineTextRefAtom } reference)
            {
                var index = PptRecords.I32(bytes, reference.Start);
                return sheet is not null && index >= 0 && index < sheet.Outline.Count ? ReadText(sheet.Outline[index]) : null;
            }
            return ReadText(records);
        }
        return null;
    }

    /// <summary>a:bodyPr from the shape's text properties: insets, anchoring, wrapping and direction.</summary>
    private static string BodyProperties(Dictionary<int, uint> properties)
    {
        long Inset(int pid, long fallback) => Math.Clamp((long)(int)properties.GetValueOrDefault(pid, (uint)fallback), 0, 51206400);
        var anchor = properties.GetValueOrDefault(AnchorText);
        var vertical = properties.GetValueOrDefault(TextFlow) switch
        {
            1 => " vert=\"vert\"",
            2 => " vert=\"vert270\"",
            3 or 5 => " vert=\"eaVert\"",
            _ => "",
        };
        return string.Create(CultureInfo.InvariantCulture,
            $"<a:bodyPr wrap=\"{(properties.GetValueOrDefault(WrapText) == 2 ? "none" : "square")}\" lIns=\"{Inset(OfficeArtProperties.TextLeft, 91440)}\" " +
            $"tIns=\"{Inset(OfficeArtProperties.TextTop, 45720)}\" rIns=\"{Inset(OfficeArtProperties.TextRight, 91440)}\" " +
            $"bIns=\"{Inset(OfficeArtProperties.TextBottom, 45720)}\"{vertical} anchor=\"{(anchor is 1 or 4 ? "ctr" : anchor is 2 or 5 or 7 or 9 ? "b" : "t")}\"" +
            $"{(anchor is 3 or 4 or 5 or 8 or 9 ? " anchorCtr=\"1\"" : "")} rtlCol=\"0\"/>");
    }

    // ───────────────────────── Properties ─────────────────────────

    /// <summary>
    /// The shape's properties over its master shapes' and, with <paramref name="defaults"/>, the drawing group's
    /// defaults.
    /// </summary>
    private Dictionary<int, uint> Properties(OfficeArtShape shape, bool defaults)
    {
        var result = defaults ? new Dictionary<int, uint>(_file.DefaultProperties) : [];
        var chain = new List<OfficeArtShape>();
        var current = shape;
        while ((current.Flags & HaveMaster) != 0 && chain.Count < MaxMasterChain
               && current.Properties.TryGetValue(MasterShape, out var spid) && MasterShapeOf((int)spid) is { } master && !chain.Contains(master) && master != shape)
        {
            chain.Add(master);
            current = master;
        }
        for (var i = chain.Count - 1; i >= 0; i--)
            Overlay(result, chain[i].Properties);
        Overlay(result, shape.Properties);
        return result;
    }

    /// <summary>The boolean property set <paramref name="pid"/> sets bit <paramref name="bit"/> (its "use" bit is on).</summary>
    private static bool Uses(Dictionary<int, uint> properties, int pid, int bit) =>
        properties.TryGetValue(pid, out var value) && (value & (1u << (bit + 16))) != 0;

    private static void Overlay(Dictionary<int, uint> under, Dictionary<int, uint> over)
    {
        foreach (var (pid, value) in over)
        {
            if ((pid & 0x3F) == 0x3F && under.TryGetValue(pid, out var old))
            {
                // A boolean property set: the bits whose "use" bit is set win.
                var use = value >> 16;
                var mask = use | (use << 16);
                under[pid] = (old & ~mask) | (value & mask);
            }
            else
            {
                under[pid] = value;
            }
        }
    }

    /// <summary>The shape with id <paramref name="spid"/> in a master's drawing.</summary>
    private OfficeArtShape? MasterShapeOf(int spid)
    {
        if (_masterShapes is null)
        {
            _masterShapes = [];
            var bytes = _file.Stream;
            var budget = new DocumentBudget(_file.Limits);
            foreach (var master in _masters.Values.Select(m => m is MainMaster main ? main.Container : ((TitleMaster)m).Container).OfType<OfficeArtRecord>().Distinct())
            {
                if (OfficeArtRecords.Child(bytes, master, PptRecords.Drawing) is not { IsContainer: true } drawing
                    || OfficeArtRecords.Child(bytes, drawing, OfficeArtRecords.DgContainer) is not { IsContainer: true } dg)
                    continue;
                // Groups in groups, without recursion: a stack of containers with their depth.
                var open = new Stack<(OfficeArtRecord Container, int Depth)>();
                open.Push((dg, 0));
                while (open.Count > 0)
                {
                    var (container, depth) = open.Pop();
                    foreach (var child in OfficeArtRecords.Children(bytes, container))
                    {
                        if (child.Type == OfficeArtRecords.SpgrContainer && child.IsContainer && depth < _file.Limits.MaxNestingDepth)
                        {
                            open.Push((child, depth + 1));
                        }
                        else if (child.Type == OfficeArtRecords.SpContainer && child.IsContainer)
                        {
                            budget.Object();
                            var shape = OfficeArtShape.Read(bytes, child);
                            if (shape.Id is { } id)
                                _masterShapes.TryAdd(id, shape);
                        }
                    }
                }
            }
        }
        return _masterShapes.GetValueOrDefault(spid);
    }

    // ───────────────────────── Fills, lines, colours ─────────────────────────

    /// <summary>The fill: none, solid, or a picture; gradients and patterns in their first colour. Null when nothing can be drawn.</summary>
    private string? FillXml(Dictionary<int, uint> properties, Sheet sheet)
    {
        if (!OfficeArtProperties.Flag(properties, OfficeArtProperties.FillBooleans, 4, true))
            return "<a:noFill/>";
        var type = properties.GetValueOrDefault(OfficeArtProperties.FillType);
        if (type is 2 or 3 && properties.GetValueOrDefault(OfficeArtProperties.FillBlip) is var pib and > 0 && MediaPart(pib) is { } media)
        {
            var embed = sheet.Relationship("image", media);
            return type == 2
                ? $"<a:blipFill rotWithShape=\"1\"><a:blip r:embed=\"{embed}\"/><a:tile tx=\"0\" ty=\"0\" sx=\"100000\" sy=\"100000\" algn=\"tl\"/></a:blipFill>"
                : $"<a:blipFill rotWithShape=\"1\"><a:blip r:embed=\"{embed}\"/><a:stretch><a:fillRect/></a:stretch></a:blipFill>";
        }
        if (type == 9)
            return "<a:noFill/>"; // the slide's background shows through
        var color = ColorRef(properties.GetValueOrDefault(OfficeArtProperties.FillColor, 0xFFFFFFu), properties,
            properties.GetValueOrDefault(OfficeArtProperties.FillOpacity, 0x10000u), 0);
        return $"<a:solidFill>{color}</a:solidFill>";
    }

    /// <summary>a:ln: no line, or colour, width, dash and arrowheads.</summary>
    private static string LineXml(Dictionary<int, uint> properties)
    {
        if (!OfficeArtProperties.Flag(properties, OfficeArtProperties.LineBooleans, 3, true))
            return "<a:ln><a:noFill/></a:ln>";
        var width = Math.Clamp((long)(int)properties.GetValueOrDefault(OfficeArtProperties.LineWidth, 9525u), 0, 20116800);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<a:ln w=\"{width}\"><a:solidFill>");
        sb.Append(ColorRef(properties.GetValueOrDefault(OfficeArtProperties.LineColor, 0u), properties, properties.GetValueOrDefault(LineOpacity, 0x10000u), 0));
        sb.Append("</a:solidFill>");
        var dash = properties.GetValueOrDefault(LineDashing) switch
        {
            1 or 6 => "dash",
            2 or 5 => "sysDot",
            3 or 8 => "dashDot",
            4 => "sysDashDotDot",
            7 => "lgDash",
            9 => "lgDashDot",
            10 => "lgDashDotDot",
            _ => null,
        };
        if (dash is not null)
            sb.Append("<a:prstDash val=\"").Append(dash).Append("\"/>");
        sb.Append(Arrow("a:headEnd", properties, LineStartArrow, LineStartArrowWidth, LineStartArrowLength));
        sb.Append(Arrow("a:tailEnd", properties, LineEndArrow, LineEndArrowWidth, LineEndArrowLength));
        return sb.Append("</a:ln>").ToString();
    }

    private static string Arrow(string element, Dictionary<int, uint> properties, int type, int width, int length)
    {
        var kind = properties.GetValueOrDefault(type) switch
        {
            1 => "triangle",
            2 => "stealth",
            3 => "diamond",
            4 => "oval",
            5 => "arrow",
            _ => null,
        };
        if (kind is null)
            return "";
        static string Size(uint value) => value switch
        {
            0 => "sm",
            2 => "lg",
            _ => "med",
        };
        return $"<{element} type=\"{kind}\" w=\"{Size(properties.GetValueOrDefault(width, 1u))}\" len=\"{Size(properties.GetValueOrDefault(length, 1u))}\"/>";
    }

    /// <summary>
    /// An OfficeArtCOLORREF: an entry of the colour scheme (as its theme colour), a colour of the shape itself (system
    /// index 0xF0–0xF7: its fill, line, ...), or RGB; with the opacity (16.16) as alpha.
    /// </summary>
    private static string ColorRef(uint value, Dictionary<int, uint> properties, uint opacity, int depth)
    {
        var flags = value >> 24;
        string color;
        if ((flags & 0x08) != 0)
        {
            color = $"<a:schemeClr val=\"{SchemeSlots[Math.Min(value & 0xFF, 7)]}\"";
        }
        else if ((flags & 0x10) != 0 && depth == 0)
        {
            var other = (value & 0xFF) switch
            {
                0xF0 or 0xF7 => OfficeArtProperties.FillColor,
                0xF1 or 0xF2 => OfficeArtProperties.LineColor,
                0xF5 => OfficeArtProperties.FillBackColor,
                0xF6 => LineBackColor,
                _ => -1,
            };
            return other < 0
                ? Alpha("<a:srgbClr val=\"000000\"", opacity)
                : ColorRef(properties.GetValueOrDefault(other, other == OfficeArtProperties.FillColor ? 0xFFFFFFu : 0u), properties, opacity, depth + 1);
        }
        else
        {
            color = $"<a:srgbClr val=\"{value & 0xFF:X2}{(value >> 8) & 0xFF:X2}{(value >> 16) & 0xFF:X2}\"";
        }
        return Alpha(color, opacity);
    }

    private static string Alpha(string open, uint opacity)
    {
        var name = open[1..open.IndexOf(' ')];
        return opacity >= 0x10000
            ? open + "/>"
            : $"{open}><a:alpha val=\"{(long)opacity * 100000 / 0x10000}\"/></{name}>";
    }
}
