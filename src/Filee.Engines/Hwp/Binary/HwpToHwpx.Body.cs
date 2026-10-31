// BodyText → section parts. A paragraph is a PARA_HEADER with its text, character shape runs, line segments and range
// tags as records under it, followed by one CTRL_HEADER per extended control in its text, in order.
//
// The text is UTF-16 with control characters below 32 in three kinds: a character control takes one position (line
// break, hyphen, non-breaking and fixed-width spaces, the paragraph end), an inline control takes eight (tab, field
// end, title mark) and an extended control eight with its data in the next CTRL_HEADER (section and column
// definitions, fields, tables and objects, headers and footers, notes, numbers, bookmarks, ...). Every control becomes
// an element, so the positions 한글 stores in the line segments still count the same way in HWPX (see
// HwpxReader.Body). A paragraph list (cell, header, note, text box, caption) is a LIST_HEADER followed by its
// paragraphs, one level below the control that owns it.

using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Binary;

internal sealed partial class HwpToHwpx
{
    /// <summary>Fields open in the current paragraph list (field end closes the last one), as (instance id, control id).</summary>
    private List<(uint Id, uint Control)> _fields = [];

    private void WriteSection(Stream stream, List<HwpRecord> roots)
    {
        using var writer = StartPart(stream, "hs", "sec", Hs);
        _x = writer;
        _fields = [];
        foreach (var record in roots)
        {
            if (record.Tag == HwpTag.ParaHeader)
                Paragraph(record);
        }
        End();
        writer.WriteEndDocument();
    }

    // ───────────────────────── Paragraph lists ─────────────────────────

    /// <summary>
    /// hp:subList with the paragraphs of the LIST_HEADER at <paramref name="index"/> in <paramref name="records"/>:
    /// the PARA_HEADERs that follow it. Returns the index after them.
    /// </summary>
    private int SubList(List<HwpRecord> records, int index, int? textWidth = null, int? textHeight = null)
    {
        var header = records[index].Reader();
        header.Skip(4);
        var attributes = header.U32();
        Start(Hp, "subList");
        Attr("id", "");
        Attr("textDirection", (attributes & 7) == 1 ? "VERTICAL" : "HORIZONTAL");
        Attr("lineWrap", ((attributes >> 3) & 3) switch { 1 => "SQUEEZE", 2 => "KEEP", _ => "BREAK" });
        Attr("vertAlign", ((attributes >> 5) & 3) switch { 1 => "CENTER", 2 => "BOTTOM", _ => "TOP" });
        Attr("linkListIDRef", 0);
        Attr("linkListNextIDRef", 0);
        Attr("textWidth", textWidth ?? 0);
        Attr("textHeight", textHeight ?? 0);
        Attr("hasTextRef", false);
        Attr("hasNumRef", false);
        index++;
        using (_budget.Enter())
        {
            // Fields don't reach into or out of a nested list.
            var fields = _fields;
            _fields = [];
            for (; index < records.Count && records[index].Tag == HwpTag.ParaHeader; index++)
                Paragraph(records[index]);
            _fields = fields;
        }
        End();
        return index;
    }

    /// <summary>The first LIST_HEADER under <paramref name="control"/> as hp:subList (an empty one when there is none).</summary>
    private void SubList(HwpRecord control, int? textWidth = null, int? textHeight = null)
    {
        var index = control.Children.FindIndex(r => r.Tag == HwpTag.ListHeader);
        if (index >= 0)
        {
            SubList(control.Children, index, textWidth, textHeight);
            return;
        }
        Start(Hp, "subList");
        End();
    }

    // ───────────────────────── Paragraphs ─────────────────────────

    /// <summary>What a paragraph's records hold, parsed.</summary>
    private sealed class ParagraphData
    {
        public string Text = "";
        public readonly List<(int Position, uint Shape)> Shapes = [];
        public readonly List<(int Start, int End, uint Tag)> Ranges = [];
        public readonly Queue<HwpRecord> Controls = new();
        public HwpRecord? LineSegments;
    }

    private void Paragraph(HwpRecord header)
    {
        _budget.Paragraph();
        var r = header.Reader();
        r.Skip(8); // text length (the text record says it too) and control mask
        var paraShape = r.U16();
        var style = r.U8();
        var breaks = r.U8();

        var data = new ParagraphData();
        foreach (var child in header.Children)
        {
            switch (child.Tag)
            {
                case HwpTag.ParaText:
                    data.Text = System.Text.Encoding.Unicode.GetString(child.Data, 0, child.Data.Length & ~1);
                    break;
                case HwpTag.ParaCharShape:
                    {
                        var shapes = child.Reader();
                        while (shapes.Remaining >= 8)
                            data.Shapes.Add(((int)Math.Min(shapes.U32(), int.MaxValue), shapes.U32()));
                        break;
                    }
                case HwpTag.ParaRangeTag:
                    {
                        var ranges = child.Reader();
                        while (ranges.Remaining >= 12)
                            data.Ranges.Add(((int)Math.Min(ranges.U32(), int.MaxValue), (int)Math.Min(ranges.U32(), int.MaxValue), ranges.U32()));
                        break;
                    }
                case HwpTag.ParaLineSeg:
                    data.LineSegments = child;
                    break;
                case HwpTag.CtrlHeader:
                    data.Controls.Enqueue(child);
                    break;
            }
        }

        Start(Hp, "p");
        Attr("id", 0);
        Attr("paraPrIDRef", paraShape);
        Attr("styleIDRef", style);
        Attr("pageBreak", (breaks & 4) != 0);
        Attr("columnBreak", (breaks & 8) != 0);
        Attr("merged", false);
        new Runs(this, data).Write();
        LineSegments(data.LineSegments);
        End();
    }

    /// <summary>hp:linesegarray: 한글's line layout of the paragraph, copied field by field.</summary>
    private void LineSegments(HwpRecord? record)
    {
        if (record is null || record.Data.Length < 36)
            return;
        var r = record.Reader();
        Start(Hp, "linesegarray");
        while (r.Remaining >= 36)
        {
            Start(Hp, "lineseg");
            Attr("textpos", r.U32());
            Attr("vertpos", r.I32());
            Attr("vertsize", r.I32());
            Attr("textheight", r.I32());
            Attr("baseline", r.I32());
            Attr("spacing", r.I32());
            Attr("horzpos", r.I32());
            Attr("horzsize", r.I32());
            Attr("flags", r.U32());
            End();
        }
        End();
    }

    /// <summary>Writes a paragraph's text as runs (one per character shape) with its controls in place.</summary>
    private sealed class Runs(HwpToHwpx writer, ParagraphData data)
    {
        private readonly System.Text.StringBuilder _pending = new();
        private bool _run;
        private bool _t;
        private uint _shape;
        private bool _any;

        public void Write()
        {
            var text = data.Text;
            for (var i = 0; i < text.Length;)
            {
                Ranges(i);
                var c = text[i];
                if (c >= 32)
                {
                    Text(i);
                    _pending.Append(c);
                    i++;
                    continue;
                }
                switch (c)
                {
                    case '\r':
                        // The paragraph end: a shape of its own (the paragraph mark's) gets a run of its own.
                        if (_run && ShapeAt(i) != _shape)
                        {
                            CloseRun();
                            writer.Start(Hp, "run");
                            writer.Attr("charPrIDRef", ShapeAt(i));
                            writer.End();
                        }
                        i = text.Length;
                        continue;
                    case '\n':
                        Element(i, "lineBreak");
                        break;
                    case (char)24:
                        Element(i, "hyphen");
                        break;
                    case (char)30:
                        Element(i, "nbSpace");
                        break;
                    case (char)31:
                        Element(i, "fwSpace");
                        break;
                    case (char)9:
                        Tab(i, text);
                        break;
                    case (char)8:
                        Element(i, "titleMark");
                        break;
                    case (char)4:
                        FieldEnd(i);
                        break;
                    default:
                        if (IsExtended(c))
                            Control(i);
                        break;
                }
                i += c is '\n' or (char)24 or (char)30 or (char)31 or (char)0 or (>= (char)25 and <= (char)29) ? 1 : 8;
            }
            Ranges(text.Length);
            CloseRun();
            if (!_any)
            {
                // An empty paragraph: a run with the shape of its paragraph mark.
                writer.Start(Hp, "run");
                writer.Attr("charPrIDRef", ShapeAt(0));
                writer.End();
            }
        }

        private static bool IsExtended(char c) => c is (char)1 or (char)2 or (char)3 or (char)11 or (char)12 or (>= (char)14 and <= (char)18) or (>= (char)21 and <= (char)23);

        private uint ShapeAt(int position)
        {
            var shape = data.Shapes.Count > 0 ? data.Shapes[0].Shape : 0;
            foreach (var (start, id) in data.Shapes)
            {
                if (start > position)
                    break;
                shape = id;
            }
            return shape;
        }

        /// <summary>Highlighter (형광펜) ranges: type 2 in the top byte, the colour in the rest.</summary>
        private void Ranges(int position)
        {
            foreach (var (start, end, tag) in data.Ranges)
            {
                if (tag >> 24 != 2 || start == end)
                    continue;
                if (end == position && start < position)
                {
                    Text(Math.Max(0, position - 1));
                    Flush();
                    writer.Empty(Hp, "markpenEnd");
                }
            }
            foreach (var (start, end, tag) in data.Ranges)
            {
                if (tag >> 24 != 2 || start == end || start != position || position >= data.Text.Length)
                    continue;
                Text(position);
                Flush();
                writer.Empty(Hp, "markpenBegin", ("color", Color(tag & 0xFFFFFF)));
            }
        }

        private void Run(int position)
        {
            var shape = ShapeAt(position);
            if (_run && shape == _shape)
                return;
            CloseRun();
            writer.Start(Hp, "run");
            writer.Attr("charPrIDRef", shape);
            (_run, _shape, _any) = (true, shape, true);
        }

        /// <summary>Opens hp:t (in the run of <paramref name="position"/>) for text and the elements that live in it.</summary>
        private void Text(int position)
        {
            Run(position);
            if (_t)
                return;
            writer.Start(Hp, "t");
            _t = true;
        }

        private void Flush()
        {
            if (_pending.Length == 0)
                return;
            writer._x.WriteString(Clean(_pending.ToString()));
            _pending.Clear();
        }

        /// <summary>Closes hp:t: controls are children of the run.</summary>
        private void CloseText()
        {
            Flush();
            if (_t)
                writer.End();
            _t = false;
        }

        private void CloseRun()
        {
            CloseText();
            if (_run)
                writer.End();
            _run = false;
        }

        private void Element(int position, string name)
        {
            Text(position);
            Flush();
            writer.Empty(Hp, name);
        }

        private void Tab(int position, string text)
        {
            Text(position);
            Flush();
            var width = position + 2 < text.Length ? text[position + 1] | (text[position + 2] << 16) : 0;
            var leader = position + 3 < text.Length ? text[position + 3] & 0xFF : 0;
            var type = position + 3 < text.Length ? text[position + 3] >> 8 : 0;
            writer.Empty(Hp, "tab", ("width", width), ("leader", leader), ("type", type + 1));
        }

        private void FieldEnd(int position)
        {
            if (writer._fields.Count == 0)
                return;
            var (id, control) = writer._fields[^1];
            writer._fields.RemoveAt(writer._fields.Count - 1);
            Run(position);
            CloseText();
            writer.Start(Hp, "ctrl");
            writer.Empty(Hp, "fieldEnd", ("beginIDRef", id), ("fieldid", control));
            writer.End();
        }

        private void Control(int position)
        {
            if (!data.Controls.TryDequeue(out var control))
                return;
            Run(position);
            CloseText();
            writer.Control(control);
        }
    }

    // ───────────────────────── Controls ─────────────────────────

    /// <summary>An extended control: an element of the run (tables, objects, secPr) or one inside hp:ctrl.</summary>
    private void Control(HwpRecord control)
    {
        var r = control.Reader();
        var id = r.U32();
        var name = ControlId(id);
        switch (name)
        {
            case "secd":
                Section(control, ref r);
                return;
            case "tbl ":
                Table(control, ref r);
                return;
            case "gso ":
                DrawingObject(control, ref r);
                return;
            case "eqed":
                Equation(control, ref r);
                return;
            case "tdut":
                Dutmal(ref r);
                return;
            case "tcps":
                Compose(ref r);
                return;
        }

        Start(Hp, "ctrl");
        switch (name)
        {
            case "cold":
                Columns(ref r);
                break;
            case "head" or "foot":
                {
                    var pages = r.U32() & 3;
                    var serial = r.U32();
                    Start(Hp, name == "head" ? "header" : "footer");
                    Attr("id", serial);
                    Attr("applyPageType", pages switch { 1 => "EVEN", 2 => "ODD", _ => "BOTH" });
                    var list = ReaderOf(control.Children.FirstOrDefault(c => c.Tag == HwpTag.ListHeader));
                    list.Skip(8);
                    SubList(control, list.I32(), list.I32());
                    End();
                    break;
                }
            case "fn  " or "en  ":
                {
                    var number = r.U32();
                    var (prefix, suffix) = (r.U16(), r.U16());
                    r.Skip(4);
                    var instance = r.U32();
                    Start(Hp, name == "fn  " ? "footNote" : "endNote");
                    Attr("number", number);
                    if (prefix != 0)
                        Attr("prefixChar", prefix);
                    if (suffix != 0)
                        Attr("suffixChar", suffix);
                    Attr("instId", instance);
                    SubList(control);
                    End();
                    break;
                }
            case "atno":
                {
                    var attributes = r.U32();
                    var number = r.U16();
                    var (user, prefix, suffix) = (r.U16(), r.U16(), r.U16());
                    Start(Hp, "autoNum");
                    Attr("num", number);
                    Attr("numType", NumberType(attributes & 0xF));
                    Empty(Hp, "autoNumFormat", ("type", NumberFormat((int)((attributes >> 4) & 0xFF))), ("userChar", Char(user)),
                        ("prefixChar", Char(prefix)), ("suffixChar", Char(suffix)), ("supscript", (attributes & 0x1000) != 0));
                    End();
                    break;
                }
            case "nwno":
                {
                    var attributes = r.U32();
                    Empty(Hp, "newNum", ("num", r.U16()), ("numType", NumberType(attributes & 0xF)));
                    break;
                }
            case "pghd":
                {
                    var hide = r.U32();
                    Empty(Hp, "pageHiding", ("hideHeader", (hide & 1) != 0), ("hideFooter", (hide & 2) != 0), ("hideMasterPage", (hide & 4) != 0),
                        ("hideBorder", (hide & 8) != 0), ("hideFill", (hide & 0x10) != 0), ("hidePageNum", (hide & 0x20) != 0));
                    break;
                }
            case "pgct":
                Empty(Hp, "pageNumCtrl", ("pageStartsOn", (r.U32() & 3) switch { 1 => "EVEN", 2 => "ODD", _ => "BOTH" }));
                break;
            case "pgnp":
                {
                    var attributes = r.U32();
                    r.Skip(6); // user symbol, prefix and suffix
                    var side = r.U16();
                    Empty(Hp, "pageNum", ("pos", ((attributes >> 8) & 0xF) switch
                    {
                        1 => "TOP_LEFT",
                        2 => "TOP_CENTER",
                        3 => "TOP_RIGHT",
                        4 => "BOTTOM_LEFT",
                        5 => "BOTTOM_CENTER",
                        6 => "BOTTOM_RIGHT",
                        7 => "OUTSIDE_TOP",
                        8 => "OUTSIDE_BOTTOM",
                        9 => "INSIDE_TOP",
                        10 => "INSIDE_BOTTOM",
                        _ => "NONE",
                    }), ("formatType", NumberFormat((int)(attributes & 0xFF))), ("sideChar", Char(side)));
                    break;
                }
            case "bokm":
                Empty(Hp, "bookmark", ("name", ParameterString(control) ?? ""));
                break;
            case "idxm":
                Start(Hp, "indexmark");
                Start(Hp, "firstKey");
                _x.WriteString(Clean(r.Text()));
                End();
                Start(Hp, "secondKey");
                _x.WriteString(Clean(r.Text()));
                End();
                End();
                break;
            case "tcmt":
                Start(Hp, "hiddenComment");
                SubList(control);
                End();
                break;
            default:
                if (name.StartsWith('%'))
                    FieldBegin(control, id, ref r);
                break;
        }
        End();
    }

    private static string NumberType(uint value) => value switch
    {
        1 => "FOOTNOTE",
        2 => "ENDNOTE",
        3 => "PICTURE",
        4 => "TABLE",
        5 => "EQUATION",
        6 => "TOTAL_PAGE",
        _ => "PAGE",
    };

    /// <summary>
    /// The first string of the parameter set in a control's CTRL_DATA (a bookmark's or a field's name): a set id, an
    /// item count and the items, each an id, a type (1 = string) and its value.
    /// </summary>
    private static string? ParameterString(HwpRecord control)
    {
        if (control.Children.FirstOrDefault(c => c.Tag == HwpTag.CtrlData) is not { } data)
            return null;
        var r = data.Reader();
        r.Skip(2);
        var count = r.I16();
        r.Skip(2);
        if (count < 1 || r.Remaining < 4)
            return null;
        r.Skip(2); // item id
        // Other types have sizes of their own: only a leading string is read.
        return r.U16() == 1 ? r.Text() : null;
    }

    // ───────────────────────── Fields ─────────────────────────

    private void FieldBegin(HwpRecord control, uint id, ref RecordReader r)
    {
        var attributes = r.U32();
        var extra = r.U8();
        var command = r.Text();
        var instance = r.U32();
        _budget.Fields(_fields.Count + 1);
        _fields.Add((instance, id));

        Start(Hp, "fieldBegin");
        Attr("id", instance);
        Attr("type", ControlId(id) switch
        {
            "%hlk" => "HYPERLINK",
            "%clk" => "CLICK_HERE",
            "%bmk" => "BOOKMARK",
            "%dte" => "DATE",
            "%ddt" => "DOC_DATE",
            "%pat" => "PATH",
            "%xrf" => "CROSSREF",
            "%fmu" => "FORMULA",
            "%mmg" => "MAILMERGE",
            "%smr" => "SUMMERY",
            "%usr" => "USER_INFO",
            "%mem" => "MEMO",
            "%cpr" => "PRIVATE_INFO_SECURITY",
            "%toc" => "TABLE_OF_CONTENTS",
            _ => "UNKNOWN",
        });
        Attr("name", ParameterString(control) ?? "");
        Attr("editable", (attributes & 1) != 0);
        Attr("dirty", (attributes & 0x8000) != 0);
        Attr("zorder", -1);
        Attr("fieldid", id);
        Start(Hp, "parameters");
        Attr("cnt", 2);
        Attr("name", "");
        Start(Hp, "integerParam");
        Attr("name", "Prop");
        _x.WriteString(extra.ToString(System.Globalization.CultureInfo.InvariantCulture));
        End();
        Start(Hp, "stringParam");
        Attr("name", "Command");
        _x.WriteString(Clean(command));
        End();
        End();
        End();
    }

    // ───────────────────────── Text decorations ─────────────────────────

    /// <summary>덧말 (ruby text): the main text and the small text above or below it.</summary>
    private void Dutmal(ref RecordReader r)
    {
        var main = r.Text();
        var sub = r.Text();
        var position = r.U32();
        var ratio = r.U32();
        var option = r.U32();
        var style = r.U32();
        var align = r.U32();
        Start(Hp, "dutmal");
        Attr("posType", position switch { 1 => "BOTTOM", 2 => "CENTER", _ => "TOP" });
        Attr("szRatio", ratio);
        Attr("option", option);
        Attr("styleIDRef", style);
        Attr("align", align switch { 1 => "LEFT", 2 => "RIGHT", 3 => "CENTER", 4 => "DISTRIBUTE", 5 => "DISTRIBUTE_SPACE", _ => "JUSTIFY" });
        Start(Hp, "mainText");
        _x.WriteString(Clean(main));
        End();
        Start(Hp, "subText");
        _x.WriteString(Clean(sub));
        End();
        End();
    }

    /// <summary>글자 겹침 (overlapping characters).</summary>
    private void Compose(ref RecordReader r)
    {
        var text = r.Text();
        var circle = r.U8();
        var size = r.I8();
        var spread = r.U8();
        var count = Math.Min((int)r.U8(), r.Remaining / 4);
        // Characters in a shape keep the shape in front of them in HWP (▲12) or as a circled digit (③); 한글's HWPX has
        // the characters alone (12, 3), the shape being circleType.
        if (circle != 0 && text is [>= '①' and <= '⑳'])
            text = (text[0] - '①' + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        else if (circle != 0 && text is [>= '■' and <= '◿', _, ..])
            text = text[1..];
        Start(Hp, "compose");
        Attr("circleType", circle switch
        {
            0 => "CHAR",
            1 => "SHAPE_CIRCLE",
            2 => "SHAPE_REVERSAL_CIRCLE",
            3 => "SHAPE_RECTANGLE",
            4 => "SHAPE_REVERSAL_RECTANGLE",
            5 => "SHAPE_TRIANGLE",
            6 => "SHAPE_REVERSAL_TIRANGLE",
            7 => "SHAPE_LIGHT",
            8 => "SHAPE_RHOMBUS",
            9 => "SHAPE_REVERSAL_RHOMBUS",
            10 => "SHAPE_ROUNDED_RECTANGLE",
            11 => "SHAPE_EMPTY_CIRCULATE_TRIANGLE",
            12 => "SHAPE_THIN_CIRCULATE_TRIANGLE",
            _ => "SHAPE_CIRCLE",
        });
        Attr("charSz", size);
        Attr("composeType", spread == 0 ? "SPREAD" : "OVERLAP");
        Attr("charPrCnt", count);
        Attr("composeText", text);
        for (var i = 0; i < count; i++)
            Empty(Hp, "charPr", ("prIDRef", r.U32()));
        End();
    }

    // ───────────────────────── Sections and columns ─────────────────────────

    /// <summary>
    /// hp:secPr from a section definition (secd) and its records: page (PAGE_DEF), footnote and endnote shapes,
    /// page borders and, as paragraph lists, the master pages its attributes announce (both, even, odd pages).
    /// </summary>
    private void Section(HwpRecord control, ref RecordReader r)
    {
        var attributes = r.U32();
        var columnGap = r.U16();
        var lineGrid = r.U16();
        var charGrid = r.U16();
        var tabStop = r.U32();
        var outline = r.U16();
        var page = r.U16();
        var (picture, table, equation) = (r.U16(), r.U16(), r.U16());

        // Master pages first: each becomes a part of its own the secPr refers to.
        var masters = new List<string>();
        var kinds = new List<string>();
        foreach (var (bit, kind) in (ReadOnlySpan<(int, string)>)[(29, "BOTH"), (30, "EVEN"), (31, "ODD")])
        {
            if ((attributes & (1u << bit)) != 0)
                kinds.Add(kind);
        }
        var lists = control.Children.Select((c, i) => (c, i)).Where(x => x.c.Tag == HwpTag.ListHeader).Select(x => x.i).ToList();
        for (var i = 0; i < lists.Count && i < kinds.Count; i++)
            masters.Add(MasterPage(control.Children, lists[i], kinds[i]));

        Start(Hp, "secPr");
        Attr("id", "");
        Attr("textDirection", ((attributes >> 16) & 7) == 1 ? "VERTICAL" : "HORIZONTAL");
        Attr("spaceColumns", columnGap);
        Attr("tabStop", tabStop);
        Attr("tabStopVal", tabStop / 2);
        Attr("tabStopUnit", "HWPUNIT");
        Attr("outlineShapeIDRef", outline);
        Attr("memoShapeIDRef", 0);
        Attr("textVerticalWidthHead", false);
        Attr("masterPageCnt", masters.Count);
        Empty(Hp, "grid", ("lineGrid", lineGrid), ("charGrid", charGrid), ("wonggojiFormat", (attributes & (1u << 22)) != 0));
        Empty(Hp, "startNum", ("pageStartsOn", ((attributes >> 20) & 3) switch { 1 => "EVEN", 2 => "ODD", _ => "BOTH" }),
            ("page", page), ("pic", picture), ("tbl", table), ("equation", equation));
        Empty(Hp, "visibility", ("hideFirstHeader", (attributes & 1) != 0), ("hideFirstFooter", (attributes & 2) != 0),
            ("hideFirstMasterPage", (attributes & 4) != 0),
            ("border", (attributes & 8) != 0 ? "HIDE_FIRST" : (attributes & 0x100) != 0 ? "SHOW_FIRST" : "SHOW_ALL"),
            ("fill", (attributes & 0x10) != 0 ? "HIDE_FIRST" : (attributes & 0x200) != 0 ? "SHOW_FIRST" : "SHOW_ALL"),
            ("hideFirstPageNum", (attributes & 0x20) != 0), ("hideFirstEmptyLine", (attributes & (1u << 19)) != 0), ("showLineNumber", false));
        Empty(Hp, "lineNumberShape", ("restartType", 0), ("countBy", 0), ("distance", 0), ("startNumber", 0));

        if (control.Children.FirstOrDefault(c => c.Tag == HwpTag.PageDef) is { } pageDef)
            PagePr(pageDef);
        var notes = control.Children.Where(c => c.Tag == HwpTag.FootnoteShape).ToList();
        if (notes.Count > 0)
            NoteShape(notes[0], "footNotePr");
        if (notes.Count > 1)
            NoteShape(notes[1], "endNotePr");
        var borders = control.Children.Where(c => c.Tag == HwpTag.PageBorderFill).ToList();
        for (var i = 0; i < borders.Count && i < 3; i++)
            PageBorderFill(borders[i], i switch { 1 => "EVEN", 2 => "ODD", _ => "BOTH" });
        foreach (var id in masters)
            Empty(Hp, "masterPage", ("idRef", id));
        End();
    }

    private void PagePr(HwpRecord record)
    {
        var r = record.Reader();
        var (width, height) = (r.I32(), r.I32());
        var (left, right, top, bottom, header, footer, gutter) = (r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32());
        var attributes = r.U32();
        Start(Hp, "pagePr");
        Attr("landscape", (attributes & 1) != 0 ? "NARROWLY" : "WIDELY");
        Attr("width", width);
        Attr("height", height);
        Attr("gutterType", ((attributes >> 1) & 3) switch { 1 => "LEFT_RIGHT", 2 => "TOP_BOTTOM", _ => "LEFT_ONLY" });
        Empty(Hp, "margin", ("header", header), ("footer", footer), ("gutter", gutter), ("left", left), ("right", right), ("top", top), ("bottom", bottom));
        End();
    }

    private void NoteShape(HwpRecord record, string name)
    {
        var r = record.Reader();
        var attributes = r.U32();
        var (user, prefix, suffix) = (r.U16(), r.U16(), r.U16());
        var start = r.U16();
        // 한글 stores the separator's length in 4 bytes (a 28-byte record); Hancom's document in 2 (26 bytes). rhwp
        // writes 28 bytes with the 2-byte length and two more bytes before the gap between notes; its default length
        // (-1, as 0xFFFF followed by the space above, never 0xFFFF) tells its layout apart.
        int length;
        ushort above, below, between;
        if (record.Data.Length < 28)
        {
            length = r.I16();
            (above, below, between) = (r.U16(), r.U16(), r.U16());
        }
        else if (record.Data[12] == 0xFF && record.Data[13] == 0xFF && !(record.Data[14] == 0xFF && record.Data[15] == 0xFF))
        {
            length = r.I16();
            (above, below) = (r.U16(), r.U16());
            r.Skip(2);
            between = r.U16();
        }
        else
        {
            length = r.I32();
            (above, below, between) = (r.U16(), r.U16(), r.U16());
        }
        var (type, width) = (r.U8(), r.U8());
        var color = r.U32();
        var endnote = name == "endNotePr";
        Start(Hp, name);
        Empty(Hp, "autoNumFormat", ("type", NumberFormat((int)(attributes & 0xFF))), ("userChar", Char(user)), ("prefixChar", Char(prefix)),
            ("suffixChar", Char(suffix)), ("supscript", (attributes & 0x1000) != 0));
        Empty(Hp, "noteLine", ("length", length), ("type", LineType(type)), ("width", BorderWidth(width)), ("color", Color(color)));
        Empty(Hp, "noteSpacing", ("betweenNotes", between), ("belowLine", below), ("aboveLine", above));
        Empty(Hp, "numbering", ("type", ((attributes >> 10) & 3) switch { 1 => "ON_SECTION", 2 => "ON_PAGE", _ => "CONTINUOUS" }), ("newNum", Math.Max(1, (int)start)));
        var place = (attributes >> 8) & 3;
        Empty(Hp, "placement", ("place", endnote
                ? place == 1 ? "END_OF_SECTION" : "END_OF_DOCUMENT"
                : place switch { 1 => "MERGED_COLUMN", 2 => "RIGHT_MOST_COLUMN", _ => "EACH_COLUMN" }),
            ("beneathText", (attributes & 0x2000) != 0));
        End();
    }

    private void PageBorderFill(HwpRecord record, string type)
    {
        var r = record.Reader();
        var attributes = r.U32();
        var (left, right, top, bottom) = (r.I16(), r.I16(), r.I16(), r.I16());
        var fill = r.U16();
        Start(Hp, "pageBorderFill");
        Attr("type", type);
        Attr("borderFillIDRef", fill);
        Attr("textBorder", (attributes & 1) != 0 ? "PAPER" : "CONTENT");
        Attr("headerInside", (attributes & 2) != 0);
        Attr("footerInside", (attributes & 4) != 0);
        Attr("fillArea", ((attributes >> 3) & 3) switch { 1 => "PAGE", 2 => "BORDER", _ => "PAPER" });
        Empty(Hp, "offset", ("left", left), ("right", right), ("top", top), ("bottom", bottom));
        End();
    }

    /// <summary>A master page (바탕쪽) as a part of its own; returns its manifest id.</summary>
    private string MasterPage(List<HwpRecord> records, int index, string type)
    {
        var id = $"masterpage{_masterPages.Count}";
        var list = records[index].Reader();
        list.Skip(8);
        var (width, height) = (list.I32(), list.I32());
        var outer = _x;
        using var stream = new MemoryStream();
        using (var writer = System.Xml.XmlWriter.Create(stream, Settings))
        {
            _x = writer;
            writer.WriteStartDocument(standalone: true);
            writer.WriteStartElement("masterPage");
            foreach (var (prefix, uri) in Namespaces)
                writer.WriteAttributeString("xmlns", prefix, null, uri);
            Attr("id", id);
            Attr("type", type);
            Attr("pageNumber", 0);
            Attr("pageDuplicate", false);
            Attr("pageFront", false);
            SubList(records, index, width, height);
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
        _x = outer;
        _masterPages.Add((id, stream.ToArray()));
        return id;
    }

    /// <summary>hp:colPr from a column definition (cold).</summary>
    private void Columns(ref RecordReader r)
    {
        var attributes = r.U16();
        var gap = r.U16();
        var count = Math.Max(1, (attributes >> 2) & 0xFF);
        var same = (attributes & 0x1000) != 0;
        var widths = new List<(int Width, int Gap)>();
        if (!same && count > 1)
        {
            // Each column's width and the gap after it, when they differ.
            for (var i = 0; i < count && r.Remaining >= 4 + 8; i++)
                widths.Add((r.U16(), r.U16()));
        }
        r.Skip(2); // attributes, bits 16-32
        var (lineType, lineWidth) = (r.U8(), r.U8());
        var lineColor = r.U32();

        Start(Hp, "colPr");
        Attr("id", "");
        Attr("type", (attributes & 3) switch { 1 => "BALANCED_NEWSPAPER", 2 => "PARALLEL", _ => "NEWSPAPER" });
        Attr("layout", ((attributes >> 10) & 3) switch { 1 => "RIGHT", 2 => "MIRROR", _ => "LEFT" });
        Attr("colCount", count);
        Attr("sameSz", same);
        Attr("sameGap", same ? gap : 0);
        if (lineType != 0)
            Empty(Hp, "colLine", ("type", LineType(lineType)), ("width", BorderWidth(lineWidth)), ("color", Color(lineColor)));
        foreach (var (width, after) in widths)
            Empty(Hp, "colSz", ("width", width), ("gap", after));
        End();
    }
}
