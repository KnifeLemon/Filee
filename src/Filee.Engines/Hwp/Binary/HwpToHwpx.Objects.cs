// Tables, drawing objects (gso: lines, rectangles, ellipses, arcs, polygons, curves, pictures, OLE objects, groups)
// and equations. Each starts with the object properties every object shares (position, size, wrapping, margins,
// description) in its CTRL_HEADER; a caption is a paragraph list before the object's own records. A drawing object
// then has a SHAPE_COMPONENT (its size, flip, rotation and transformation matrices, and for drawn shapes the outline
// and fill) with the record of its kind and, for a text box, a paragraph list under it. A group's members are
// SHAPE_COMPONENTs under the group's one.

namespace Filee.Engines.Hwp.Binary;

internal sealed partial class HwpToHwpx
{
    /// <summary>The properties every object shares (개체 공통 속성).</summary>
    private sealed record ObjectCommon(uint Attributes, int VerticalOffset, int HorizontalOffset, int Width, int Height, int ZOrder,
        short[] Margin, uint Instance, string Description)
    {
        public static ObjectCommon Read(ref RecordReader r)
        {
            var (attributes, vertical, horizontal, width, height, z) = (r.U32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32());
            short[] margin = [r.I16(), r.I16(), r.I16(), r.I16()];
            var instance = r.U32();
            r.Skip(4); // keeps the object from being split over pages
            var description = r.Remaining >= 2 ? r.Text() : "";
            return new ObjectCommon(attributes, vertical, horizontal, width, height, z, margin, instance, description);
        }

        public bool TreatAsChar => (Attributes & 1) != 0;
    }

    /// <summary>Attributes of the object element: id, z-order, numbering, text wrap and flow.</summary>
    private void ObjectAttributes(ObjectCommon common)
    {
        var a = common.Attributes;
        Attr("id", common.Instance);
        Attr("zOrder", common.ZOrder);
        Attr("numberingType", ((a >> 26) & 3) switch { 1 => "PICTURE", 2 => "TABLE", 3 => "EQUATION", _ => "NONE" });
        Attr("textWrap", ((a >> 21) & 7) switch
        {
            1 => "TOP_AND_BOTTOM",
            2 => "BEHIND_TEXT",
            3 => "IN_FRONT_OF_TEXT",
            4 => "TIGHT",
            5 => "THROUGH",
            _ => "SQUARE",
        });
        Attr("textFlow", ((a >> 24) & 3) switch { 1 => "LEFT_ONLY", 2 => "RIGHT_ONLY", 3 => "LARGEST_ONLY", _ => "BOTH_SIDES" });
        Attr("lock", false);
        Attr("dropcapstyle", "None");
    }

    /// <summary>hp:sz, hp:pos and hp:outMargin.</summary>
    private void SizeAndPosition(ObjectCommon common)
    {
        var a = common.Attributes;
        Empty(Hp, "sz", ("width", common.Width), ("widthRelTo", ((a >> 15) & 7) switch
        {
            0 => "PAPER",
            1 => "PAGE",
            2 => "COLUMN",
            3 => "PARA",
            _ => "ABSOLUTE",
        }), ("height", common.Height), ("heightRelTo", ((a >> 18) & 3) switch { 0 => "PAPER", 1 => "PAGE", _ => "ABSOLUTE" }),
            ("protect", (a & (1u << 20)) != 0));
        Empty(Hp, "pos", ("treatAsChar", common.TreatAsChar), ("affectLSpacing", (a & 4) != 0), ("flowWithText", (a & (1u << 13)) != 0),
            ("allowOverlap", (a & (1u << 14)) != 0), ("holdAnchorAndSO", false),
            ("vertRelTo", ((a >> 3) & 3) switch { 0 => "PAPER", 1 => "PAGE", _ => "PARA" }),
            ("horzRelTo", ((a >> 8) & 3) switch { 0 => "PAPER", 1 => "PAGE", 2 => "COLUMN", _ => "PARA" }),
            ("vertAlign", Align((a >> 5) & 7, "TOP", "BOTTOM")), ("horzAlign", Align((a >> 10) & 7, "LEFT", "RIGHT")),
            ("vertOffset", common.VerticalOffset), ("horzOffset", common.HorizontalOffset));
        Margin("outMargin", common.Margin);
    }

    private static string Align(uint value, string start, string end) => value switch
    {
        1 => "CENTER",
        2 => end,
        3 => "INSIDE",
        4 => "OUTSIDE",
        _ => start,
    };

    private void Margin(string name, short[] margin) =>
        Empty(Hp, name, ("left", margin[0]), ("right", margin[1]), ("top", margin[2]), ("bottom", margin[3]));

    private void ShapeComment(ObjectCommon common)
    {
        if (common.Description.Length == 0)
            return;
        Start(Hp, "shapeComment");
        _x.WriteString(Clean(common.Description));
        End();
    }

    /// <summary>The object's caption: a paragraph list among its records, before the object's own record.</summary>
    private void Caption(HwpRecord control, int before)
    {
        var index = control.Children.FindIndex(c => c.Tag == HwpTag.ListHeader);
        if (index < 0 || index > before)
            return;
        var r = control.Children[index].Reader();
        r.Skip(8);
        var attributes = r.U32();
        var width = r.I32();
        var gap = r.I16();
        var last = r.I32();
        Start(Hp, "caption");
        Attr("side", (attributes & 3) switch { 0 => "LEFT", 1 => "RIGHT", 2 => "TOP", _ => "BOTTOM" });
        Attr("fullSz", (attributes & 4) != 0);
        Attr("width", width);
        Attr("gap", gap);
        Attr("lastWidth", last);
        SubList(control.Children, index);
        End();
    }

    // ───────────────────────── Tables ─────────────────────────

    private void Table(HwpRecord control, ref RecordReader r)
    {
        _budget.Object();
        var common = ObjectCommon.Read(ref r);
        var tableIndex = control.Children.FindIndex(c => c.Tag == HwpTag.Table);
        var t = ReaderOf(tableIndex >= 0 ? control.Children[tableIndex] : null);
        var attributes = t.U32();
        var (rows, columns) = (t.U16(), t.U16());
        var spacing = t.I16();
        short[] inner = [t.I16(), t.I16(), t.I16(), t.I16()];
        t.Skip(2 * rows);
        var borderFill = t.U16();

        // Cells: a paragraph list each, after the TABLE record, row by row.
        var cells = new List<(int Index, int Row)>();
        if (tableIndex >= 0)
        {
            for (var i = tableIndex + 1; i < control.Children.Count; i++)
            {
                if (control.Children[i].Tag != HwpTag.ListHeader)
                    continue;
                var cell = control.Children[i].Reader();
                cell.Skip(10);
                cells.Add((i, cell.U16()));
            }
        }

        Start(Hp, "tbl");
        ObjectAttributes(common);
        Attr("pageBreak", (attributes & 3) switch { 1 => "TABLE", 2 => "CELL", _ => "NONE" });
        Attr("repeatHeader", (attributes & 4) != 0);
        Attr("rowCnt", rows);
        Attr("colCnt", columns);
        Attr("cellSpacing", spacing);
        Attr("borderFillIDRef", borderFill);
        Attr("noAdjust", false);
        SizeAndPosition(common);
        Caption(control, tableIndex < 0 ? control.Children.Count : tableIndex);
        Margin("inMargin", inner);
        foreach (var row in cells.GroupBy(c => c.Row).OrderBy(g => g.Key))
        {
            Start(Hp, "tr");
            foreach (var (index, _) in row)
                Cell(control.Children, index);
            End();
        }
        End();
    }

    /// <summary>A cell: its paragraph list, then address, span, size and margins.</summary>
    private void Cell(List<HwpRecord> records, int index)
    {
        var r = records[index].Reader();
        r.Skip(4);
        var list = r.U32();
        var (column, row, columnSpan, rowSpan) = (r.U16(), r.U16(), r.U16(), r.U16());
        var (width, height) = (r.I32(), r.I32());
        short[] margin = [r.I16(), r.I16(), r.I16(), r.I16()];
        var borderFill = r.U16();
        Start(Hp, "tc");
        Attr("name", "");
        Attr("header", (list & (1u << 18)) != 0);
        Attr("hasMargin", (list & (1u << 16)) != 0);
        Attr("protect", false);
        Attr("editable", (list & (1u << 19)) != 0);
        Attr("dirty", false);
        Attr("borderFillIDRef", borderFill);
        SubList(records, index);
        Empty(Hp, "cellAddr", ("colAddr", column), ("rowAddr", row));
        Empty(Hp, "cellSpan", ("colSpan", Math.Max(1, (int)columnSpan)), ("rowSpan", Math.Max(1, (int)rowSpan)));
        Empty(Hp, "cellSz", ("width", width), ("height", height));
        Margin("cellMargin", margin);
        End();
    }

    // ───────────────────────── Drawing objects ─────────────────────────

    /// <summary>A SHAPE_COMPONENT: the object's own geometry and transformation.</summary>
    private sealed record Component(string Kind, int X, int Y, int GroupLevel, int OriginalWidth, int OriginalHeight, int Width, int Height,
        uint Attributes, int Angle, int CenterX, int CenterY, double[] Translation, List<(double[] Scale, double[] Rotation)> Matrices)
    {
        /// <summary>Reads the component; a top-level object's id is written twice.</summary>
        public static Component Read(ref RecordReader r, bool topLevel)
        {
            var id = r.U32();
            if (topLevel)
                r.Skip(4);
            var (x, y) = (r.I32(), r.I32());
            var group = r.U16();
            r.Skip(2); // local file version
            var (originalWidth, originalHeight, width, height) = (r.I32(), r.I32(), r.I32(), r.I32());
            var attributes = r.U32();
            var angle = r.I16();
            var (centerX, centerY) = (r.I32(), r.I32());
            var count = Math.Clamp((int)r.U16(), 0, Math.Max(0, r.Remaining / 96));
            var translation = Matrix(ref r);
            var matrices = new List<(double[], double[])>();
            for (var i = 0; i < count; i++)
                matrices.Add((Matrix(ref r), Matrix(ref r)));
            return new Component(ControlId(id), x, y, group, originalWidth, originalHeight, width, height, attributes, angle, centerX, centerY, translation, matrices);
        }

        private static double[] Matrix(ref RecordReader r)
        {
            var values = new double[6];
            for (var i = 0; i < 6; i++)
                values[i] = r.F64() is var v && double.IsFinite(v) ? v : 0;
            return values;
        }
    }

    private void DrawingObject(HwpRecord control, ref RecordReader r)
    {
        var common = ObjectCommon.Read(ref r);
        var index = control.Children.FindIndex(c => c.Tag == HwpTag.ShapeComponent);
        if (index < 0)
            return;
        // Lines, arcs and curves have no outer margin in 한글's HWPX, whatever the HWP record holds.
        var component = control.Children[index].Data;
        var outer = component.Length >= 4 && ControlId(BitConverter.ToUInt32(component, 0)) is "$lin" or "$arc" or "$cur"
            ? common with { Margin = [0, 0, 0, 0] }
            : common;
        Shape(control.Children[index], common, () =>
        {
            SizeAndPosition(outer);
            ShapeComment(common);
            Caption(control, index);
        });
    }

    /// <summary>
    /// One drawing object. <paramref name="placement"/> writes the size, position and caption of a top-level object;
    /// group members have none (their place is their offset in the group).
    /// </summary>
    private void Shape(HwpRecord record, ObjectCommon? common, Action? placement)
    {
        _budget.Object();
        var r = record.Reader();
        var shape = Component.Read(ref r, common is not null);
        var kind = record.Children.FirstOrDefault(c => c.Tag is >= HwpTag.ShapeLine and <= HwpTag.ShapeContainer or HwpTag.ShapeTextArt);
        var name = ShapeName(shape.Kind);
        if (name is null)
            return; // charts, video and form objects: not written
        // A group of nothing Filee writes is left out: 한글 does not open a file with an empty group.
        if (name == "container" && !record.Children.Any(c => c.Tag == HwpTag.ShapeComponent && c.Data.Length >= 4 && ShapeName(ControlId(BitConverter.ToUInt32(c.Data, 0))) is not null))
            return;

        Start(Hp, name);
        if (common is not null)
            ObjectAttributes(common);
        else
        {
            Attr("id", 0);
            Attr("zOrder", 0);
            Attr("numberingType", "NONE");
            Attr("textWrap", "TOP_AND_BOTTOM");
            Attr("textFlow", "BOTH_SIDES");
            Attr("lock", false);
            Attr("dropcapstyle", "None");
        }
        Attr("href", "");
        Attr("groupLevel", shape.GroupLevel);
        Attr("instid", 0);
        var kindReader = ReaderOf(kind);
        switch (name)
        {
            case "rect":
                Attr("ratio", kindReader.U8());
                break;
            case "arc":
                Attr("type", kindReader.U8() switch { 1 => "PIE", 2 => "CHORD", _ => "NORMAL" });
                break;
            case "pic":
                Attr("reverse", false);
                break;
            case "line":
                Attr("isReverseHV", false);
                break;
            case "connectLine":
                {
                    var connector = new RecordReader(kind is null ? [] : kind.Data);
                    connector.Skip(16);
                    Attr("type", connector.U32() switch
                    {
                        1 => "STRAIGHT_ONEWAY",
                        2 => "STRAIGHT_BOTH",
                        3 => "STROKE_NOARROW",
                        4 => "STROKE_ONEWAY",
                        5 => "STROKE_BOTH",
                        6 => "ARC_NOARROW",
                        7 => "ARC_ONEWAY",
                        8 => "ARC_BOTH",
                        _ => "STRAIGHT_NOARROW",
                    });
                    break;
                }
            case "textart":
                {
                    // 글맵시: its corners, then the text (the rest is its font and shape).
                    var corners = new RecordReader(kind is null ? [] : kind.Data);
                    corners.Skip(32);
                    // 한글's HWPX shows its line breaks as the symbols for them (␍␊).
                    Attr("text", corners.Text().Replace('\r', '␍').Replace('\n', '␊'));
                    break;
                }
        }

        Empty(Hp, "offset", ("x", shape.X), ("y", shape.Y));
        Empty(Hp, "orgSz", ("width", shape.OriginalWidth), ("height", shape.OriginalHeight));
        // 한글 writes 0 for a current size that is the original one.
        Empty(Hp, "curSz", ("width", shape.Width == shape.OriginalWidth ? 0 : shape.Width), ("height", shape.Height == shape.OriginalHeight ? 0 : shape.Height));
        Empty(Hp, "flip", ("horizontal", (shape.Attributes & 1) != 0), ("vertical", (shape.Attributes & 2) != 0));
        Empty(Hp, "rotationInfo", ("angle", shape.Angle), ("centerX", shape.CenterX), ("centerY", shape.CenterY), ("rotateimage", true));
        Start(Hp, "renderingInfo");
        MatrixElement("transMatrix", shape.Translation);
        foreach (var (scale, rotation) in shape.Matrices)
        {
            MatrixElement("scaMatrix", scale);
            MatrixElement("rotMatrix", rotation);
        }
        End();

        switch (name)
        {
            case "pic":
                Picture(kind);
                placement?.Invoke();
                break;
            case "ole":
                OleObject(kind);
                placement?.Invoke();
                break;
            case "container":
                {
                    using (_budget.Enter())
                    {
                        foreach (var member in record.Children.Where(c => c.Tag == HwpTag.ShapeComponent))
                            Shape(member, null, null);
                    }
                    placement?.Invoke();
                    break;
                }
            default:
                {
                    // Drawn shapes: outline, fill and shadow follow the matrices in the component.
                    LineShapeElement(r.U32(), r.I32(), r.U32(), r.U8());
                    FillBrush(ref r, write: name != "connectLine");
                    var (shadowType, shadowColor, shadowX, shadowY) = (r.U32(), r.U32(), r.I32(), r.I32());
                    Empty(Hp, "shadow", ("type", shadowType == 0 ? "NONE" : "PARALLEL_RIGHTBOTTOM"), ("color", Color(shadowColor)),
                        ("offsetX", shadowX), ("offsetY", shadowY), ("alpha", 0));
                    var text = record.Children.FindIndex(c => c.Tag == HwpTag.ListHeader);
                    if (text >= 0)
                        DrawText(record.Children, text);
                    Geometry(name, ref kindReader);
                    placement?.Invoke();
                    break;
                }
        }
        End();
    }

    /// <summary>The OWPML element of a drawing object's kind; null for kinds not written.</summary>
    private static string? ShapeName(string kind) => kind switch
    {
        "$lin" => "line",
        "$rec" => "rect",
        "$ell" => "ellipse",
        "$arc" => "arc",
        "$pol" => "polygon",
        "$cur" => "curve",
        "$pic" => "pic",
        "$ole" => "ole",
        "$con" => "container",
        "$tat" => "textart",
        "$col" => "connectLine",
        _ => null,
    };

    private void MatrixElement(string name, double[] values) =>
        Empty(Hc, name, ("e1", values[0]), ("e2", values[1]), ("e3", values[2]), ("e4", values[3]), ("e5", values[4]), ("e6", values[5]));

    /// <summary>hp:lineShape from an outline: colour, width, and the packed style, ends and arrows.</summary>
    private void LineShapeElement(uint color, int width, uint attributes, int outline)
    {
        static string Arrow(uint value) => value switch
        {
            1 => "ARROW",
            2 => "SPEAR",
            3 => "CONCAVE_ARROW",
            4 => "EMPTY_DIAMOND",
            5 => "EMPTY_CIRCLE",
            6 => "EMPTY_BOX",
            7 => "FILLED_DIAMOND",
            8 => "FILLED_CIRCLE",
            9 => "FILLED_BOX",
            _ => "NORMAL",
        };
        static string Size(uint value) => value switch
        {
            1 => "SMALL_MEDIUM",
            2 => "SMALL_LARGE",
            3 => "MEDIUM_SMALL",
            4 => "MEDIUM_MEDIUM",
            5 => "MEDIUM_LARGE",
            6 => "LARGE_SMALL",
            7 => "LARGE_MEDIUM",
            8 => "LARGE_LARGE",
            _ => "SMALL_SMALL",
        };
        Empty(Hp, "lineShape", ("color", Color(color)), ("width", width), ("style", LineType((int)(attributes & 0x3F))),
            ("endCap", ((attributes >> 6) & 0xF) == 1 ? "FLAT" : "ROUND"),
            ("headStyle", Arrow((attributes >> 10) & 0x3F)), ("tailStyle", Arrow((attributes >> 16) & 0x3F)),
            ("headfill", (attributes & (1u << 30)) != 0), ("tailfill", (attributes & (1u << 31)) != 0),
            ("headSz", Size((attributes >> 22) & 0xF)), ("tailSz", Size((attributes >> 26) & 0xF)),
            ("outlineStyle", outline switch { 1 => "OUTER", 2 => "INNER", _ => "NORMAL" }), ("alpha", 0));
    }

    /// <summary>hp:drawText: a text box's paragraphs and inner margins.</summary>
    private void DrawText(List<HwpRecord> records, int index)
    {
        var r = records[index].Reader();
        r.Skip(8);
        short[] margin = [r.I16(), r.I16(), r.I16(), r.I16()];
        var last = r.I32();
        Start(Hp, "drawText");
        Attr("lastWidth", last);
        Attr("name", "");
        Attr("editable", false);
        SubList(records, index);
        Margin("textMargin", margin);
        End();
    }

    /// <summary>The points of a line, rectangle, ellipse, arc, polygon or curve (in the shape's original size).</summary>
    private void Geometry(string name, ref RecordReader r)
    {
        void Point(string element, ref RecordReader reader) => Empty(Hc, element, ("x", reader.I32()), ("y", reader.I32()));
        switch (name)
        {
            case "line":
                Point("startPt", ref r);
                Point("endPt", ref r);
                break;
            case "connectLine":
                {
                    // The ends, the type, the objects each end is attached to and the bend points (x, y, kind).
                    var (startX, startY, endX, endY) = (r.I32(), r.I32(), r.I32(), r.I32());
                    r.Skip(4);
                    var (startSubject, startIndex, endSubject, endIndex) = (r.U32(), r.U32(), r.U32(), r.U32());
                    Empty(Hp, "startPt", ("x", startX), ("y", startY), ("subjectIDRef", startSubject), ("subjectIdx", startIndex));
                    Empty(Hp, "endPt", ("x", endX), ("y", endY), ("subjectIDRef", endSubject), ("subjectIdx", endIndex));
                    var count = Math.Clamp((int)r.U32(), 0, r.Remaining / 10);
                    Start(Hp, "controlPoints");
                    for (var i = 0; i < count; i++)
                        Empty(Hp, "point", ("x", r.I32()), ("y", r.I32()), ("type", r.U16()));
                    End();
                    break;
                }
            case "rect" or "textart":
                for (var i = 0; i < 4; i++)
                    Point($"pt{i}", ref r);
                break;
            case "ellipse":
                r.Skip(4);
                foreach (var element in (string[])["center", "ax1", "ax2", "start1", "end1", "start2", "end2"])
                    Point(element, ref r);
                break;
            case "arc":
                foreach (var element in (string[])["center", "ax1", "ax2"])
                    Point(element, ref r);
                break;
            case "polygon":
                {
                    var count = Math.Clamp(r.I32(), 0, r.Remaining / 8);
                    for (var i = 0; i < count; i++)
                        Point("pt", ref r);
                    break;
                }
            case "curve":
                {
                    var count = Math.Clamp(r.I32(), 0, r.Remaining / 8);
                    var points = new (int X, int Y)[count];
                    for (var i = 0; i < count; i++)
                        points[i] = (r.I32(), r.I32());
                    for (var i = 0; i + 1 < count; i++)
                    {
                        Empty(Hp, "seg", ("type", r.U8() == 1 ? "CURVE" : "LINE"), ("x1", points[i].X), ("y1", points[i].Y),
                            ("x2", points[i + 1].X), ("y2", points[i + 1].Y));
                    }
                    break;
                }
        }
    }

    /// <summary>
    /// A picture: its outline, the picture's rectangle and crop, inner margin, the BinData it shows and, in newer files,
    /// its effects and original size (imgDim, which the crop is measured against).
    /// </summary>
    private void Picture(HwpRecord? record)
    {
        var r = ReaderOf(record);
        var (color, width, attributes) = (r.U32(), r.I32(), r.U32());
        var points = new (int X, int Y)[4];
        for (var i = 0; i < 4; i++)
            points[i] = (r.I32(), r.I32());
        var (left, top, right, bottom) = (r.I32(), r.I32(), r.I32(), r.I32());
        short[] margin = [r.I16(), r.I16(), r.I16(), r.I16()];
        var (bright, contrast, effect, bin) = (r.I8(), r.I8(), r.U8(), r.U16());
        r.Skip(1); // outline alpha
        r.Skip(4); // instance id
        (int Width, int Height)? dimension = null;
        if (record is not null && r.Remaining >= 12)
        {
            // Effects (shadow, glow, soft edge, reflection) of their own sizes come first; with none the size follows
            // their flags, with some it ends the record.
            if (r.U32() == 0)
            {
                dimension = (r.I32(), r.I32());
            }
            else
            {
                var tail = new RecordReader(record.Data.AsSpan(record.Data.Length - 8));
                dimension = (tail.I32(), tail.I32());
            }
            // A size the crop doesn't fit in is no size (a layout this reader doesn't know).
            if (dimension is var (w, h) && (w < right || h < bottom))
                dimension = null;
        }

        Image(bin, bright, contrast, effect);
        LineShapeElement(color, width, attributes, 0);
        Start(Hp, "imgRect");
        for (var i = 0; i < 4; i++)
            Empty(Hc, $"pt{i}", ("x", points[i].X), ("y", points[i].Y));
        End();
        Empty(Hp, "imgClip", ("left", left), ("right", right), ("top", top), ("bottom", bottom));
        Margin("inMargin", margin);
        if (dimension is { Width: > 0, Height: > 0 } size)
            Empty(Hp, "imgDim", ("dimwidth", size.Width), ("dimheight", size.Height));
    }

    /// <summary>An OLE object: its extent (the reader leaves OLE objects out; their BinData storage is kept).</summary>
    private void OleObject(HwpRecord? record)
    {
        var r = ReaderOf(record);
        r.Skip(4);
        Empty(Hc, "extent", ("x", r.I32()), ("y", r.I32()));
    }

    // ───────────────────────── Equations ─────────────────────────

    private void Equation(HwpRecord control, ref RecordReader r)
    {
        _budget.Object();
        var common = ObjectCommon.Read(ref r);
        var record = control.Children.FirstOrDefault(c => c.Tag == HwpTag.EqEdit);
        var e = ReaderOf(record);
        var attributes = e.U32();
        var script = e.Text();
        var size = e.U32();
        var color = e.U32();
        var baseline = e.I16();
        e.Skip(2);
        var version = e.Text();
        var font = e.Text();
        Start(Hp, "equation");
        ObjectAttributes(common);
        Attr("version", version.Length > 0 ? version : "Equation Version 60");
        Attr("baseLine", baseline);
        Attr("textColor", Color(color));
        Attr("baseUnit", size);
        Attr("lineMode", (attributes & 1) != 0 ? "LINE" : "CHAR");
        Attr("font", font.Length > 0 ? font : "HYhwpEQ");
        SizeAndPosition(common);
        ShapeComment(common);
        Caption(control, record is null ? control.Children.Count : control.Children.IndexOf(record));
        Start(Hp, "script");
        _x.WriteString(Clean(script));
        End();
        End();
    }
}
