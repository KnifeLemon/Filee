// Tables, drawing objects and equations, the inverse of HwpToHwpx.Objects. Each starts with the object properties every
// object shares (position, size, wrapping, margins, description) in its CTRL_HEADER; a caption is a paragraph list
// before the object's own records. A drawing object then has a SHAPE_COMPONENT (size, flip, rotation, transformation
// matrices and, for drawn shapes, outline, fill and shadow) with the record of its kind and, for a text box, a
// paragraph list under it; a group's members are SHAPE_COMPONENTs under the group's one.

using System.Xml.Linq;

namespace Filee.Engines.Hwp.Binary;

internal sealed partial class HwpxToHwp
{
    /// <summary>The drawing objects written: OWPML element → the kind's id in HWP.</summary>
    private static readonly Dictionary<string, string> ShapeKinds = new(StringComparer.Ordinal)
    {
        ["line"] = "$lin",
        ["rect"] = "$rec",
        ["ellipse"] = "$ell",
        ["arc"] = "$arc",
        ["polygon"] = "$pol",
        ["curve"] = "$cur",
        ["pic"] = "$pic",
        ["ole"] = "$ole",
        ["container"] = "$con",
        ["textart"] = "$tat",
        ["connectLine"] = "$col",
    };

    /// <summary>
    /// The properties every object shares: placement attributes, offsets, size, z-order, outer margins, instance id and
    /// description (hp:shapeComment), with the two bytes 한글 writes after it.
    /// </summary>
    private static RecordData ObjectCommon(string id, XElement element)
    {
        var size = Child(element, "sz");
        var position = Child(element, "pos");
        var margin = Child(element, "outMargin");
        var attributes = (Flag(position, "treatAsChar") ? 1u : 0) | (Flag(position, "affectLSpacing") ? 4u : 0)
                         | (uint)(Attr(position, "vertRelTo") switch { "PAPER" => 0, "PAGE" => 1, _ => 2 }) << 3
                         | (uint)Align(Attr(position, "vertAlign"), "TOP", "BOTTOM") << 5
                         | (uint)(Attr(position, "horzRelTo") switch { "PAPER" => 0, "PAGE" => 1, "COLUMN" => 2, _ => 3 }) << 8
                         | (uint)Align(Attr(position, "horzAlign"), "LEFT", "RIGHT") << 10
                         | (Flag(position, "flowWithText") ? 1u << 13 : 0) | (Flag(position, "allowOverlap") ? 1u << 14 : 0)
                         | (uint)(Attr(size, "widthRelTo") switch { "PAPER" => 0, "PAGE" => 1, "COLUMN" => 2, "PARA" => 3, _ => 4 }) << 15
                         | (uint)(Attr(size, "heightRelTo") switch { "PAPER" => 0, "PAGE" => 1, _ => 2 }) << 18
                         | (Flag(size, "protect") ? 1u << 20 : 0)
                         | (uint)(Attr(element, "textWrap") switch
                         {
                             "TOP_AND_BOTTOM" => 1,
                             "BEHIND_TEXT" => 2,
                             "IN_FRONT_OF_TEXT" => 3,
                             "TIGHT" => 4,
                             "THROUGH" => 5,
                             _ => 0,
                         }) << 21
                         | (uint)(Attr(element, "textFlow") switch { "LEFT_ONLY" => 1, "RIGHT_ONLY" => 2, "LARGEST_ONLY" => 3, _ => 0 }) << 24
                         | (uint)(Attr(element, "numberingType") switch { "PICTURE" => 1, "TABLE" => 2, "EQUATION" => 3, _ => 0 }) << 26;
        return new RecordData().U32(ControlId(id)).U32(attributes)
            .I32(Int(position, "vertOffset")).I32(Int(position, "horzOffset")).I32(Int(size, "width")).I32(Int(size, "height"))
            .I32(Int(element, "zOrder"))
            .I16(Int(margin, "left")).I16(Int(margin, "right")).I16(Int(margin, "top")).I16(Int(margin, "bottom"))
            .U32(UInt(element, "id")).U32(0).Text(Child(element, "shapeComment")?.Value).U16(0);
    }

    private static int Align(string? value, string start, string end) =>
        value == start ? 0 : value == "CENTER" ? 1 : value == end ? 2 : value == "INSIDE" ? 3 : value == "OUTSIDE" ? 4 : 0;

    private static void Margins(RecordData data, XElement? margin) =>
        data.I16(Int(margin, "left")).I16(Int(margin, "right")).I16(Int(margin, "top")).I16(Int(margin, "bottom"));

    /// <summary>The object's caption, a paragraph list before the object's own records.</summary>
    private void Caption(XElement element, int level, List<Rec> records)
    {
        if (Child(element, "caption") is not { } caption)
            return;
        var subList = Child(caption, "subList");
        ListHeader(subList, level, records, data => data
            .U32((uint)(Attr(caption, "side") switch { "LEFT" => 0, "RIGHT" => 1, "TOP" => 2, _ => 3 }) | (Flag(caption, "fullSz") ? 4u : 0))
            .I32(Int(caption, "width")).I16(Int(caption, "gap")).I32(Int(caption, "lastWidth")).Zeros(12));
    }

    // ───────────────────────── Tables ─────────────────────────

    private void Table(XElement table, int level, List<Rec> records)
    {
        _budget.Object();
        records.Add(new Rec(HwpTag.CtrlHeader, level, ObjectCommon("tbl ", table)));
        using var nested = _budget.Enter();
        Caption(table, level + 1, records);

        var rows = Children(table, "tr").ToList();
        var inner = Child(table, "inMargin");
        var data = new RecordData()
            .U32((uint)(Attr(table, "pageBreak") switch { "TABLE" => 1, "CELL" => 2, _ => 0 }) | (Flag(table, "repeatHeader") ? 4u : 0))
            .U16(Int(table, "rowCnt", rows.Count)).U16(Int(table, "colCnt", 1)).I16(Int(table, "cellSpacing"));
        Margins(data, inner);
        var rowCount = Int(table, "rowCnt", rows.Count);
        for (var i = 0; i < rowCount; i++)
            data.U16(i < rows.Count ? Children(rows[i], "tc").Count() : 0);
        var fill = Attr(table, "borderFillIDRef");
        data.U16(_borderFills.Contains(fill) ? _borderFills[fill] : 0).U16(0);
        records.Add(new Rec(HwpTag.Table, level + 1, data));

        foreach (var row in rows)
        {
            foreach (var cell in Children(row, "tc"))
                Cell(cell, level + 1, records);
        }
    }

    /// <summary>A cell: its paragraph list with address, span, size, margins and border fill in the list header.</summary>
    private void Cell(XElement cell, int level, List<Rec> records)
    {
        var address = Child(cell, "cellAddr");
        var span = Child(cell, "cellSpan");
        var size = Child(cell, "cellSz");
        var fill = Attr(cell, "borderFillIDRef");
        var extra = (Flag(cell, "hasMargin") ? 1u << 16 : 0) | (Flag(cell, "header") ? 1u << 18 : 0) | (Flag(cell, "editable") ? 1u << 19 : 0);
        ListHeader(Child(cell, "subList"), level, records, data =>
        {
            data.U16(Int(address, "colAddr")).U16(Int(address, "rowAddr")).U16(Math.Max(1, Int(span, "colSpan", 1))).U16(Math.Max(1, Int(span, "rowSpan", 1)))
                .I32(Int(size, "width")).I32(Int(size, "height"));
            Margins(data, Child(cell, "cellMargin"));
            data.U16(_borderFills.Contains(fill) ? _borderFills[fill] : 0).I32(Int(size, "width")).Zeros(9);
        }, extra);
    }

    // ───────────────────────── Drawing objects ─────────────────────────

    private void DrawingObject(XElement element, int level, List<Rec> records)
    {
        records.Add(new Rec(HwpTag.CtrlHeader, level, ObjectCommon("gso ", element)));
        Caption(element, level + 1, records);
        Shape(element, level + 1, topLevel: true, records);
    }

    /// <summary>One drawing object's SHAPE_COMPONENT and the records under it.</summary>
    private void Shape(XElement element, int level, bool topLevel, List<Rec> records)
    {
        _budget.Object();
        var name = element.Name.LocalName;
        var kind = ControlId(ShapeKinds[name]);
        var data = new RecordData().U32(kind);
        if (topLevel)
            data.U32(kind);
        var offset = Child(element, "offset");
        var original = Child(element, "orgSz");
        var current = Child(element, "curSz");
        var flip = Child(element, "flip");
        var rotation = Child(element, "rotationInfo");
        var (width, height) = (Int(original, "width"), Int(original, "height"));
        // 한글 writes 0 for a current size that is the original one.
        var (currentWidth, currentHeight) = (Int(current, "width") is var w && w != 0 ? w : width, Int(current, "height") is var h && h != 0 ? h : height);
        var attributes = (Flag(flip, "horizontal") ? 1u : 0) | (Flag(flip, "vertical") ? 2u : 0) | 1u << 19
                         | (name == "pic" ? 1u << 29 : 0) | (Child(element, "drawText") is not null ? 1u << 24 : 0);
        data.I32(Int(offset, "x")).I32(Int(offset, "y")).U16(Int(element, "groupLevel")).U16(1)
            .I32(width).I32(height).I32(currentWidth).I32(currentHeight).U32(attributes)
            .I16(Int(rotation, "angle")).I32(Int(rotation, "centerX")).I32(Int(rotation, "centerY"));
        var rendering = Child(element, "renderingInfo");
        var matrices = Elements(rendering).ToList();
        var scales = matrices.Where(m => m.Name.LocalName == "scaMatrix").ToList();
        var rotations = matrices.Where(m => m.Name.LocalName == "rotMatrix").ToList();
        var pairs = Math.Min(scales.Count, rotations.Count);
        data.U16(pairs);
        Matrix(data, matrices.FirstOrDefault(m => m.Name.LocalName == "transMatrix"));
        for (var i = 0; i < pairs; i++)
        {
            Matrix(data, scales[i]);
            Matrix(data, rotations[i]);
        }

        var instance = UInt(element, "id");
        switch (name)
        {
            case "pic":
                records.Add(new Rec(HwpTag.ShapeComponent, level, data));
                records.Add(new Rec(HwpTag.ShapePicture, level + 1, Picture(element, instance)));
                return;
            case "ole":
                {
                    records.Add(new Rec(HwpTag.ShapeComponent, level, data));
                    var extent = Child(element, "extent");
                    var line = Child(element, "lineShape");
                    records.Add(new Rec(HwpTag.ShapeOle, level + 1, new RecordData().U32(0).I32(Int(extent, "x")).I32(Int(extent, "y"))
                        .U16(BinId(Attr(element, "binaryItemIDRef"))).U32(Color(line, "color", 0)).I32(Int(line, "width")).U32(LineAttributes(line))));
                    return;
                }
            case "container":
                {
                    var members = Elements(element).Where(e => ShapeKinds.ContainsKey(e.Name.LocalName)).ToList();
                    data.U16(members.Count);
                    foreach (var member in members)
                        data.U32(ControlId(ShapeKinds[member.Name.LocalName]));
                    data.U32(instance);
                    records.Add(new Rec(HwpTag.ShapeComponent, level, data));
                    using var nested = _budget.Enter();
                    foreach (var member in members)
                        Shape(member, level + 1, topLevel: false, records);
                    return;
                }
        }

        // Drawn shapes: outline, fill and shadow, then the alpha of the shadow and the instance id as 한글 writes them.
        var lineShape = Child(element, "lineShape");
        data.U32(Color(lineShape, "color", 0)).I32(Int(lineShape, "width")).U32(LineAttributes(lineShape))
            .U8(Attr(lineShape, "outlineStyle") switch { "OUTER" => 1, "INNER" => 2, _ => 0 });
        if (name == "connectLine")
            data.U32(0).U32(0); // a connector has no fill
        else
            FillBrush(data, Child(element, "fillBrush"));
        var shadow = Child(element, "shadow");
        data.U32(Attr(shadow, "type") is null or "NONE" ? 0u : 1u).U32(Color(shadow, "color", 0x00B2B2B2))
            .I32(Int(shadow, "offsetX")).I32(Int(shadow, "offsetY"))
            .U8(0).U32(instance).U8(0);
        records.Add(new Rec(HwpTag.ShapeComponent, level, data));

        if (Child(element, "drawText") is { } drawText)
        {
            var subList = Child(drawText, "subList");
            var margin = Child(drawText, "textMargin");
            ListHeader(subList, level + 1, records, d =>
            {
                Margins(d, margin);
                d.I32(Int(drawText, "lastWidth")).Zeros(13);
            });
        }
        records.Add(new Rec(KindTag(name), level + 1, Geometry(element, name)));
    }

    private static int KindTag(string name) => name switch
    {
        "line" or "connectLine" => HwpTag.ShapeLine,
        "rect" => HwpTag.ShapeRectangle,
        "ellipse" => HwpTag.ShapeEllipse,
        "arc" => HwpTag.ShapeArc,
        "polygon" => HwpTag.ShapePolygon,
        "curve" => HwpTag.ShapeCurve,
        _ => HwpTag.ShapeTextArt,
    };

    private static void Matrix(RecordData data, XElement? matrix)
    {
        // The identity when the element is missing.
        double[] identity = [1, 0, 0, 0, 1, 0];
        for (var i = 0; i < 6; i++)
            data.F64(Real(matrix, $"e{i + 1}", identity[i]));
    }

    /// <summary>The packed outline style: line type, end cap, arrows and their sizes and fills.</summary>
    private static uint LineAttributes(XElement? line)
    {
        static uint Arrow(string? value) => value switch
        {
            "ARROW" => 1,
            "SPEAR" => 2,
            "CONCAVE_ARROW" => 3,
            "EMPTY_DIAMOND" => 4,
            "EMPTY_CIRCLE" => 5,
            "EMPTY_BOX" => 6,
            "FILLED_DIAMOND" => 7,
            "FILLED_CIRCLE" => 8,
            "FILLED_BOX" => 9,
            _ => 0,
        };
        static uint Size(string? value) => value switch
        {
            "SMALL_MEDIUM" => 1,
            "SMALL_LARGE" => 2,
            "MEDIUM_SMALL" => 3,
            "MEDIUM_MEDIUM" => 4,
            "MEDIUM_LARGE" => 5,
            "LARGE_SMALL" => 6,
            "LARGE_MEDIUM" => 7,
            "LARGE_LARGE" => 8,
            _ => 0,
        };
        if (line is null)
            return 0;
        return (uint)LineType(Attr(line, "style")) & 0x3F
               | (Attr(line, "endCap") == "FLAT" ? 1u : 0) << 6
               | Arrow(Attr(line, "headStyle")) << 10 | Arrow(Attr(line, "tailStyle")) << 16
               | Size(Attr(line, "headSz")) << 22 | Size(Attr(line, "tailSz")) << 26
               | (Flag(line, "headfill") ? 1u << 30 : 0) | (Flag(line, "tailfill") ? 1u << 31 : 0);
    }

    /// <summary>The points of a line, rectangle, ellipse, arc, polygon, curve, connector or text art.</summary>
    private RecordData Geometry(XElement element, string name)
    {
        var data = new RecordData();
        void Point(XElement? point) => data.I32(Int(point, "x")).I32(Int(point, "y"));
        switch (name)
        {
            case "line":
                Point(Child(element, "startPt"));
                Point(Child(element, "endPt"));
                data.U32(Flag(element, "isReverseHV") ? 1u : 0);
                break;
            case "connectLine":
                {
                    var (start, end) = (Child(element, "startPt"), Child(element, "endPt"));
                    Point(start);
                    Point(end);
                    data.U32(Attr(element, "type") switch
                    {
                        "STRAIGHT_ONEWAY" => 1u,
                        "STRAIGHT_BOTH" => 2u,
                        "STROKE_NOARROW" => 3u,
                        "STROKE_ONEWAY" => 4u,
                        "STROKE_BOTH" => 5u,
                        "ARC_NOARROW" => 6u,
                        "ARC_ONEWAY" => 7u,
                        "ARC_BOTH" => 8u,
                        _ => 0u,
                    });
                    data.U32(UInt(start, "subjectIDRef")).U32(UInt(start, "subjectIdx")).U32(UInt(end, "subjectIDRef")).U32(UInt(end, "subjectIdx"));
                    var points = Children(Child(element, "controlPoints"), "point").ToList();
                    data.U32((uint)points.Count);
                    foreach (var point in points)
                    {
                        Point(point);
                        data.U16(Int(point, "type"));
                    }
                    data.U32(0);
                    break;
                }
            case "rect":
                data.U8(Int(element, "ratio"));
                for (var i = 0; i < 4; i++)
                    Point(Child(element, $"pt{i}"));
                break;
            case "ellipse":
                data.U32((Flag(element, "intervalDirty") ? 1u : 0) | (Flag(element, "hasArcPr") ? 2u : 0)
                         | (uint)(Attr(element, "arcType") switch { "PIE" => 1, "CHORD" => 2, _ => 0 }) << 2);
                foreach (var point in (string[])["center", "ax1", "ax2", "start1", "end1", "start2", "end2"])
                    Point(Child(element, point));
                break;
            case "arc":
                data.U8(Attr(element, "type") switch { "PIE" => 1, "CHORD" => 2, _ => 0 });
                foreach (var point in (string[])["center", "ax1", "ax2"])
                    Point(Child(element, point));
                break;
            case "polygon":
                {
                    var points = Children(element, "pt").ToList();
                    data.I32(points.Count);
                    foreach (var point in points)
                        Point(point);
                    break;
                }
            case "curve":
                {
                    // Segments share their ends: the points are the first segment's start and every segment's end.
                    var segments = Children(element, "seg").ToList();
                    data.I32(segments.Count == 0 ? 0 : segments.Count + 1);
                    if (segments.Count > 0)
                    {
                        data.I32(Int(segments[0], "x1")).I32(Int(segments[0], "y1"));
                        foreach (var segment in segments)
                            data.I32(Int(segment, "x2")).I32(Int(segment, "y2"));
                        foreach (var segment in segments)
                            data.U8(Attr(segment, "type") == "CURVE" ? 1 : 0);
                    }
                    data.U32(0);
                    break;
                }
            case "textart":
                {
                    for (var i = 0; i < 4; i++)
                        Point(Child(element, $"pt{i}"));
                    // 한글's HWPX shows the text's line breaks as the symbols for them (␍␊).
                    data.Text((Attr(element, "text") ?? "").Replace('␍', '\r').Replace('␊', '\n'));
                    var shape = Child(element, "textartPr");
                    data.Text(Attr(shape, "fontName")).Text(Attr(shape, "fontStyle") ?? "");
                    break;
                }
        }
        return data;
    }

    /// <summary>
    /// A picture: its outline, the picture's rectangle and crop, inner margin, the BinData it shows, the instance id, no
    /// effects and its original size (imgDim, which the crop is measured against).
    /// </summary>
    private RecordData Picture(XElement element, uint instance)
    {
        var line = Child(element, "lineShape");
        var data = new RecordData().U32(Color(line, "color", 0)).I32(Int(line, "width")).U32(LineAttributes(line));
        var rectangle = Child(element, "imgRect");
        for (var i = 0; i < 4; i++)
        {
            var point = Child(rectangle, $"pt{i}");
            data.I32(Int(point, "x")).I32(Int(point, "y"));
        }
        var clip = Child(element, "imgClip");
        data.I32(Int(clip, "left")).I32(Int(clip, "top")).I32(Int(clip, "right")).I32(Int(clip, "bottom"));
        Margins(data, Child(element, "inMargin"));
        var img = Child(element, "img");
        data.I8(Int(img, "bright")).I8(Int(img, "contrast")).U8(ImageEffect(Attr(img, "effect"))).U16(BinId(Attr(img, "binaryItemIDRef")))
            .U8(0).U32(instance).U32(0);
        var dimension = Child(element, "imgDim");
        data.I32(Int(dimension, "dimwidth")).I32(Int(dimension, "dimheight")).U8(0);
        return data;
    }

    // ───────────────────────── Equations ─────────────────────────

    private void Equation(XElement equation, int level, List<Rec> records)
    {
        _budget.Object();
        records.Add(new Rec(HwpTag.CtrlHeader, level, ObjectCommon("eqed", equation)));
        Caption(equation, level + 1, records);
        records.Add(new Rec(HwpTag.EqEdit, level + 1, new RecordData()
            .U32(Attr(equation, "lineMode") == "LINE" ? 1u : 0).Text(Child(equation, "script")?.Value)
            .U32(UInt(equation, "baseUnit", 1000)).U32(Color(equation, "textColor", 0)).I16(Int(equation, "baseLine"))
            .U16(0).Text(Attr(equation, "version") ?? "Equation Version 60").Text(Attr(equation, "font") ?? "HYhwpEQ")));
    }
}
