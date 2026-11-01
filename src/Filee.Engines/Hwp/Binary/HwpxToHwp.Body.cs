// Section parts → BodyText, the inverse of HwpToHwpx.Body. A paragraph becomes a PARA_HEADER with its text, character
// shape runs, line segments and range tags as records under it, followed by one CTRL_HEADER per extended control in
// its text, in order; a paragraph list (cell, header, note, text box, caption) is a LIST_HEADER followed by its
// paragraphs, the last one marked in its character count.
//
// Every element takes the text positions 한글 gives it (a character control one, an inline or extended control
// eight), so the line segments a package stores keep pointing at the same text. A paragraph with something Filee
// leaves out (an object kind HwpToHwpx doesn't write, tracked deleted text) loses its line segments instead, and 한글
// lays it out again, as it does for paragraphs it saved without them.

using System.Text;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Binary;

internal sealed partial class HwpxToHwp
{
    /// <summary>A record of a stream being built, at its level.</summary>
    private readonly record struct Rec(int Tag, int Level, byte[] Data)
    {
        public Rec(int tag, int level, RecordData data) : this(tag, level, data.ToArray())
        {
        }
    }

    /// <summary>The plain text of the document so far (PrvText when the package has none).</summary>
    private StringBuilder _preview = new();

    private byte[] Section(XElement root, StringBuilder preview)
    {
        _preview = preview;
        var records = new List<Rec>();
        ParagraphList(Children(root, "p").ToList(), 0, records);
        var stream = new RecordStream();
        foreach (var record in records)
            stream.Add(record.Tag, record.Level, record.Data);
        return stream.ToArray();
    }

    // ───────────────────────── Paragraph lists ─────────────────────────

    /// <summary>The paragraphs of a list at <paramref name="level"/>; a list has one empty paragraph at least.</summary>
    private void ParagraphList(IReadOnlyList<XElement> paragraphs, int level, List<Rec> output)
    {
        using (_budget.Enter())
        {
            if (paragraphs.Count == 0)
            {
                Paragraph(null, level, last: true, output);
                return;
            }
            for (var i = 0; i < paragraphs.Count; i++)
                Paragraph(paragraphs[i], level, i == paragraphs.Count - 1, output);
        }
    }

    /// <summary>The paragraphs of an hp:subList (an empty list when there is none).</summary>
    private static List<XElement> SubListParagraphs(XElement? subList) => Children(subList, "p").ToList();

    /// <summary>
    /// The LIST_HEADER of a paragraph list: its paragraph count (four bytes in 한글's files), its attributes (text
    /// direction, line wrap, vertical alignment) and <paramref name="rest"/>, then the paragraphs at the same level.
    /// </summary>
    private void ListHeader(XElement? subList, int level, List<Rec> output, Action<RecordData>? rest = null, uint extra = 0)
    {
        var paragraphs = SubListParagraphs(subList);
        var data = new RecordData().I32(Math.Max(1, paragraphs.Count)).U32(SubListAttributes(subList) | extra);
        rest?.Invoke(data);
        output.Add(new Rec(HwpTag.ListHeader, level, data));
        ParagraphList(paragraphs, level, output);
    }

    private static uint SubListAttributes(XElement? subList) =>
        (Attr(subList, "textDirection") == "VERTICAL" ? 1u : 0)
        | (uint)(Attr(subList, "lineWrap") switch { "SQUEEZE" => 1, "KEEP" => 2, _ => 0 }) << 3
        | (uint)(Attr(subList, "vertAlign") switch { "CENTER" => 1, "BOTTOM" => 2, _ => 0 }) << 5;

    // ───────────────────────── Paragraphs ─────────────────────────

    private void Paragraph(XElement? p, int level, bool last, List<Rec> output)
    {
        _budget.Paragraph();
        var text = new ParagraphText(this, level);
        XElement? lines = null;
        foreach (var child in Elements(p))
        {
            switch (child.Name.LocalName)
            {
                case "run":
                    text.Run(child);
                    break;
                case "linesegarray":
                    lines = child;
                    break;
            }
        }
        text.End();
        _preview.Append(text.Plain).Append("\r\n");

        var segments = lines is null || _estimatedLines ? [] : Children(lines, "lineseg").ToList();
        // The stored lines count positions in the package's text: they follow what was left out or written longer,
        // and a line that starts inside something left out leaves the paragraph to 한글's layout.
        var positions = segments.Select(s => text.Map(Int(s, "textpos"))).ToList();
        if (positions.Any(p => p is null))
            segments = [];
        var chars = text.Chars.ToString();
        var breaks = (text.Section ? 1 : 0) | (text.Columns ? 2 : 0) | (Flag(p, "pageBreak") ? 4 : 0) | (Flag(p, "columnBreak") ? 8 : 0);
        output.Add(new Rec(HwpTag.ParaHeader, level, new RecordData()
            .U32((uint)chars.Length | (last ? 0x80000000 : 0)).U32(text.Mask)
            .U16(_paraShapes[Attr(p, "paraPrIDRef")]).U8(_styles[Attr(p, "styleIDRef")]).U8(breaks)
            .U16(text.Shapes.Count).U16(text.Ranges.Count).U16(segments.Count)
            .U32(0).U16(0)));
        // A paragraph of nothing but its end has no text record, as 한글 writes it.
        if (chars.Length > 1)
            output.Add(new Rec(HwpTag.ParaText, level + 1, Encoding.Unicode.GetBytes(chars)));
        var shapes = new RecordData();
        foreach (var (position, shape) in text.Shapes)
            shapes.U32((uint)position).U32((uint)shape);
        output.Add(new Rec(HwpTag.ParaCharShape, level + 1, shapes));
        if (segments.Count > 0)
        {
            var data = new RecordData();
            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                data.U32((uint)positions[i]!.Value).I32(Int(segment, "vertpos")).I32(Int(segment, "vertsize")).I32(Int(segment, "textheight"))
                    .I32(Int(segment, "baseline")).I32(Int(segment, "spacing")).I32(Int(segment, "horzpos")).I32(Int(segment, "horzsize"))
                    .U32(UInt(segment, "flags"));
            }
            output.Add(new Rec(HwpTag.ParaLineSeg, level + 1, data));
        }
        if (text.Ranges.Count > 0)
        {
            var data = new RecordData();
            foreach (var (start, end, tag) in text.Ranges)
                data.U32((uint)start).U32((uint)end).U32(tag);
            output.Add(new Rec(HwpTag.ParaRangeTag, level + 1, data));
        }
        output.AddRange(text.Controls);
    }

    /// <summary>
    /// The text of one paragraph as HWP stores it: characters, the paragraph's controls as control characters, the
    /// character shape of each run and the highlighter ranges, with the controls' records.
    /// </summary>
    private sealed class ParagraphText(HwpxToHwp writer, int level)
    {
        public StringBuilder Chars { get; } = new();
        public StringBuilder Plain { get; } = new();
        public List<(int Position, int Shape)> Shapes { get; } = [];
        public List<(int Start, int End, uint Tag)> Ranges { get; } = [];
        public List<Rec> Controls { get; } = [];
        public uint Mask { get; private set; }
        public bool Section { get; private set; }
        public bool Columns { get; private set; }

        /// <summary>
        /// Where the text differs in length from the package's: at a position of the package's text, so many units
        /// there became so many here (deleted text and objects not written: none; a tab character: a tab control).
        /// </summary>
        private readonly List<(int Source, int Length, int Output)> _shifts = [];

        private (int Start, uint Color)? _markpen;
        private bool _deleted;

        public void Run(XElement run)
        {
            Shape(writer._charShapes[Attr(run, "charPrIDRef")]);
            foreach (var item in Elements(run))
            {
                switch (item.Name.LocalName)
                {
                    case "t":
                        Text(item);
                        break;
                    case "secPr":
                        Section = true;
                        Extended(2, "secd", records => writer.SectionDefinition(item, level + 1, records));
                        break;
                    case "ctrl":
                        foreach (var control in Elements(item))
                            Control(control);
                        break;
                    case "tbl":
                        Extended(11, "tbl ", records => writer.Table(item, level + 1, records));
                        break;
                    case "equation":
                        Extended(11, "eqed", records => writer.Equation(item, level + 1, records));
                        break;
                    case "dutmal":
                        Extended(23, "tdut", records => records.Add(new Rec(HwpTag.CtrlHeader, level + 1, Dutmal(item))));
                        break;
                    case "compose":
                        Extended(23, "tcps", records => records.Add(new Rec(HwpTag.CtrlHeader, level + 1, writer.Compose(item))));
                        break;
                    case "colPr" or "header" or "footer" or "footNote" or "endNote" or "fieldBegin" or "fieldEnd" or "bookmark" or "autoNum" or "newNum" or "pageNum":
                        Control(item); // controls outside hp:ctrl (other producers)
                        break;
                    default:
                        if (ShapeKinds.ContainsKey(item.Name.LocalName))
                            Extended(11, "gso ", records => writer.DrawingObject(item, level + 1, records));
                        else
                            Shift(8, 0); // charts, video, form objects: not written
                        break;
                }
            }
        }

        /// <summary>The run's character shape from the current position on.</summary>
        private void Shape(int shape)
        {
            if (Shapes.Count > 0 && Shapes[^1].Position == Chars.Length)
                Shapes.RemoveAt(Shapes.Count - 1);
            if (Shapes.Count == 0 || Shapes[^1].Shape != shape)
                Shapes.Add((Chars.Length, shape));
        }

        private void Text(XElement t)
        {
            foreach (var node in t.Nodes())
            {
                if (node is XText xtext)
                {
                    var value = xtext.Value;
                    // Line ends in hp:t are the indentation of pretty-printed files (hp:lineBreak is a line break).
                    if (value.Contains('\n') || value.Contains('\r'))
                    {
                        if (string.IsNullOrWhiteSpace(value))
                            continue;
                        value = value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
                    }
                    if (_deleted)
                    {
                        Shift(value.Length, 0);
                        continue;
                    }
                    foreach (var c in value)
                    {
                        if (c == '\t')
                        {
                            Shift(1, 8);
                            Tab(0, 0, 0);
                        }
                        else if (c >= 32 && c is not ('￾' or '￿'))
                        {
                            Chars.Append(c);
                            Plain.Append(c);
                        }
                        else
                        {
                            Shift(1, 0);
                        }
                    }
                    continue;
                }
                if (node is not XElement element)
                    continue;
                switch (element.Name.LocalName)
                {
                    case "tab":
                        if (_deleted)
                            Shift(8, 0);
                        else
                            Tab(Int(element, "width"), Leader(Attr(element, "leader")), Math.Max(0, Int(element, "type", 1)));
                        continue;
                    case "lineBreak":
                        Character(10);
                        continue;
                    case "hyphen":
                        Character(24);
                        continue;
                    case "nbSpace":
                        Character(30);
                        continue;
                    case "fwSpace":
                        Character(31);
                        continue;
                    case "titleMark":
                        Inline(8, 0, 0, 0);
                        continue;
                    case "markpenBegin":
                        _markpen = (Chars.Length, Color(element, "color", 0x0000FFFF) & 0xFFFFFF);
                        continue;
                    case "markpenEnd":
                        CloseMarkpen();
                        continue;
                    case "deleteBegin":
                        _deleted = true;
                        continue;
                    case "deleteEnd":
                        _deleted = false;
                        continue;
                }
                // Other marks (inserted text, ...) take no place in the text.
            }
        }

        private void Shift(int length, int output)
        {
            _shifts.Add((Chars.Length + _shifts.Sum(s => s.Length - s.Output), length, output));
        }

        /// <summary>A position of the package's text in this text; null when it falls inside something left out.</summary>
        public int? Map(int position)
        {
            var mapped = position;
            foreach (var (source, length, output) in _shifts)
            {
                if (position >= source + length)
                    mapped += output - length;
                else if (position > source)
                    return null;
            }
            return mapped;
        }

        private static int Leader(string? leader) =>
            int.TryParse(leader, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
                ? Math.Clamp(number, 0, 255)
                : LineType(leader);

        private void CloseMarkpen()
        {
            if (_markpen is var (start, color) && Chars.Length > start)
                Ranges.Add((start, Chars.Length, 0x02000000 | color));
            _markpen = null;
        }

        private void Control(XElement control)
        {
            var name = control.Name.LocalName;
            switch (name)
            {
                case "colPr":
                    Columns = true;
                    Extended(2, "cold", records => records.Add(new Rec(HwpTag.CtrlHeader, level + 1, ColumnDefinition(control))));
                    break;
                case "header" or "footer":
                    Extended(16, name == "header" ? "head" : "foot", records => writer.HeaderFooter(control, level + 1, records));
                    break;
                case "footNote" or "endNote":
                    Extended(17, name == "footNote" ? "fn  " : "en  ", records => writer.Note(control, level + 1, records));
                    break;
                case "autoNum":
                    Extended(18, "atno", records => records.Add(new Rec(HwpTag.CtrlHeader, level + 1, AutoNumber(control))));
                    break;
                case "newNum":
                    Extended(21, "nwno", records => records.Add(new Rec(HwpTag.CtrlHeader, level + 1,
                        new RecordData().U32(ControlId("nwno")).U32((uint)NumberType(Attr(control, "numType"))).U16(Int(control, "num", 1)))));
                    break;
                case "pageNumCtrl":
                    Extended(21, "pgct", records => records.Add(new Rec(HwpTag.CtrlHeader, level + 1,
                        new RecordData().U32(ControlId("pgct")).U32(Attr(control, "pageStartsOn") switch { "EVEN" => 1u, "ODD" => 2u, _ => 0u }))));
                    break;
                case "pageHiding":
                    Extended(21, "pghd", records => records.Add(new Rec(HwpTag.CtrlHeader, level + 1, new RecordData().U32(ControlId("pghd")).U32(
                        (Flag(control, "hideHeader") ? 1u : 0) | (Flag(control, "hideFooter") ? 2u : 0) | (Flag(control, "hideMasterPage") ? 4u : 0)
                        | (Flag(control, "hideBorder") ? 8u : 0) | (Flag(control, "hideFill") ? 0x10u : 0) | (Flag(control, "hidePageNum") ? 0x20u : 0)))));
                    break;
                case "pageNum":
                    Extended(21, "pgnp", records => records.Add(new Rec(HwpTag.CtrlHeader, level + 1, PageNumber(control))));
                    break;
                case "bookmark":
                    Extended(22, "bokm", records =>
                    {
                        records.Add(new Rec(HwpTag.CtrlHeader, level + 1, new RecordData().U32(ControlId("bokm"))));
                        records.Add(new Rec(HwpTag.CtrlData, level + 2, NameParameter(Attr(control, "name"))));
                    });
                    break;
                case "indexmark":
                    Extended(22, "idxm", records => records.Add(new Rec(HwpTag.CtrlHeader, level + 1, new RecordData().U32(ControlId("idxm"))
                        .Text(Child(control, "firstKey")?.Value).Text(Child(control, "secondKey")?.Value))));
                    break;
                case "hiddenComment":
                    Extended(23, "tcmt", records =>
                    {
                        records.Add(new Rec(HwpTag.CtrlHeader, level + 1, new RecordData().U32(ControlId("tcmt"))));
                        writer.ListHeader(Child(control, "subList"), level + 2, records, data => data.Zeros(8));
                    });
                    break;
                case "fieldBegin":
                    {
                        var id = FieldId(control);
                        Extended(3, ControlName(id), records => writer.FieldBegin(control, id, level + 1, records));
                        break;
                    }
                case "fieldEnd":
                    {
                        // 한글 keeps the field's id without its leading '%' in the field end.
                        var id = UInt(control, "fieldid") is var known && known >> 24 == '%' ? known : ControlId("%unk");
                        Inline(4, (ushort)(id & 0xFFFF), (ushort)((id >> 16) & 0xFF), 0);
                        break;
                    }
                default:
                    Shift(8, 0);
                    break;
            }
        }

        /// <summary>An extended control: eight positions (code, id, code) and its records after the paragraph's.</summary>
        private void Extended(int code, string id, Action<List<Rec>> records)
        {
            var packed = ControlId(id);
            Chars.Append((char)code).Append((char)(packed & 0xFFFF)).Append((char)(packed >> 16)).Append('\0', 4).Append((char)code);
            Mask |= 1u << code;
            records(Controls);
        }

        /// <summary>An inline control: eight positions, its data in the six between the codes.</summary>
        private void Inline(int code, ushort a, ushort b, ushort c, ushort filler = 0)
        {
            Chars.Append((char)code).Append((char)a).Append((char)b).Append((char)c).Append((char)filler, 3).Append((char)code);
            Mask |= 1u << code;
        }

        /// <summary>A tab: its width, leader and kind, then the spaces 한글 fills the rest with.</summary>
        private void Tab(int width, int leader, int type)
        {
            Inline(9, (ushort)(width & 0xFFFF), (ushort)((uint)width >> 16), (ushort)((leader & 0xFF) | ((type & 0xFF) << 8)), ' ');
            Plain.Append('\t');
        }

        private void Character(int code)
        {
            if (_deleted)
            {
                Shift(1, 0);
                return;
            }
            Chars.Append((char)code);
            Mask |= 1u << code;
            Plain.Append(code switch { 10 => '\n', 30 or 31 => ' ', _ => '-' });
        }

        /// <summary>The paragraph end (with the shape of the paragraph mark: the last run's).</summary>
        public void End()
        {
            CloseMarkpen();
            if (Shapes.Count == 0)
                Shapes.Add((0, 0));
            Chars.Append('\r');
        }

        private static uint FieldId(XElement field)
        {
            var id = UInt(field, "fieldid");
            if (id >> 24 == '%' && ((id >> 16) & 0xFF) is >= 0x20 and < 0x7F)
                return id;
            return ControlId(Attr(field, "type") switch
            {
                "HYPERLINK" => "%hlk",
                "CLICK_HERE" => "%clk",
                "BOOKMARK" => "%bmk",
                "DATE" => "%dte",
                "DOC_DATE" => "%ddt",
                "PATH" => "%pat",
                "CROSSREF" => "%xrf",
                "FORMULA" => "%fmu",
                "MAILMERGE" => "%mmg",
                "SUMMERY" or "SUMMARY" => "%smr",
                "USER_INFO" => "%usr",
                "MEMO" => "%mem",
                "PRIVATE_INFO_SECURITY" => "%cpr",
                "TABLE_OF_CONTENTS" => "%toc",
                _ => "%unk",
            });
        }

        private static string ControlName(uint id) =>
            new([(char)((id >> 24) & 0xFF), (char)((id >> 16) & 0xFF), (char)((id >> 8) & 0xFF), (char)(id & 0xFF)]);
    }

    /// <summary>A parameter set holding one string (a bookmark's or a field's name), as CTRL_DATA keeps it.</summary>
    private static RecordData NameParameter(string? name) =>
        new RecordData().U16(0x021B).I16(1).U16(0).U16(0x4000).U16(1).Text(name);

    private static int NumberType(string? type) => type switch
    {
        "FOOTNOTE" => 1,
        "ENDNOTE" => 2,
        "PICTURE" => 3,
        "TABLE" => 4,
        "EQUATION" => 5,
        "TOTAL_PAGE" => 6,
        _ => 0,
    };

    /// <summary>A note's or number's character field: a character code in HWPX ("41"), or the character itself.</summary>
    private static ushort CodeOrChar(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var code)
            ? (ushort)Math.Clamp(code, 0, 0xFFFF)
            : Char(value);

    // ───────────────────────── Controls ─────────────────────────

    private static RecordData AutoNumber(XElement control)
    {
        var format = Child(control, "autoNumFormat");
        var attributes = (uint)NumberType(Attr(control, "numType")) | (uint)(NumberFormat(Attr(format, "type")) & 0xFF) << 4
                         | (Flag(format, "supscript") ? 0x1000u : 0);
        return new RecordData().U32(ControlId("atno")).U32(attributes).U16(Int(control, "num", 1))
            .U16(Char(Attr(format, "userChar"))).U16(Char(Attr(format, "prefixChar"))).U16(Char(Attr(format, "suffixChar")));
    }

    private static RecordData PageNumber(XElement control) =>
        new RecordData().U32(ControlId("pgnp")).U32((uint)(NumberFormat(Attr(control, "formatType")) & 0xFF) | (uint)(Attr(control, "pos") switch
        {
            "TOP_LEFT" => 1,
            "TOP_CENTER" => 2,
            "TOP_RIGHT" => 3,
            "BOTTOM_LEFT" => 4,
            "BOTTOM_CENTER" => 5,
            "BOTTOM_RIGHT" => 6,
            "OUTSIDE_TOP" => 7,
            "OUTSIDE_BOTTOM" => 8,
            "INSIDE_TOP" => 9,
            "INSIDE_BOTTOM" => 10,
            _ => 0,
        }) << 8).U16(0).U16(0).U16(0).U16(Char(Attr(control, "sideChar")));

    private void HeaderFooter(XElement control, int level, List<Rec> records)
    {
        records.Add(new Rec(HwpTag.CtrlHeader, level, new RecordData().U32(ControlId(control.Name.LocalName == "header" ? "head" : "foot"))
            .U32(Attr(control, "applyPageType") switch { "EVEN" => 1u, "ODD" => 2u, _ => 0u }).U32(UInt(control, "id"))));
        var subList = Child(control, "subList");
        ListHeader(subList, level + 1, records, data => data.I32(Int(subList, "textWidth")).I32(Int(subList, "textHeight")).Zeros(18));
    }

    private void Note(XElement control, int level, List<Rec> records)
    {
        var footnote = control.Name.LocalName == "footNote";
        records.Add(new Rec(HwpTag.CtrlHeader, level, new RecordData().U32(ControlId(footnote ? "fn  " : "en  "))
            .U32(UInt(control, "number", 1)).U16(CodeOrChar(Attr(control, "prefixChar"))).U16(CodeOrChar(Attr(control, "suffixChar")))
            .U32(footnote ? 0u : 3u).U32(UInt(control, "instId"))));
        ListHeader(Child(control, "subList"), level + 1, records, data => data.Zeros(8));
    }

    private void FieldBegin(XElement field, uint id, int level, List<Rec> records)
    {
        var parameters = Child(field, "parameters");
        var command = Descendants(parameters, "stringParam").FirstOrDefault(p => Attr(p, "name") == "Command")?.Value ?? "";
        var prop = Descendants(parameters, "integerParam").FirstOrDefault(p => Attr(p, "name") == "Prop")?.Value;
        var extra = int.TryParse(prop, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;
        records.Add(new Rec(HwpTag.CtrlHeader, level, new RecordData().U32(id)
            .U32((Flag(field, "editable") ? 1u : 0) | (Flag(field, "dirty") ? 0x8000u : 0)).U8(extra).Text(command)
            .U32(UInt(field, "id")).U32(0)));
        if (Attr(field, "name") is { Length: > 0 } name)
            records.Add(new Rec(HwpTag.CtrlData, level + 1, NameParameter(name)));
    }

    /// <summary>덧말 (ruby text): the main text and the small text above or below it.</summary>
    private static RecordData Dutmal(XElement element) =>
        new RecordData().U32(ControlId("tdut")).Text(Child(element, "mainText")?.Value).Text(Child(element, "subText")?.Value)
            .U32(Attr(element, "posType") switch { "BOTTOM" => 1u, "CENTER" => 2u, _ => 0u }).U32(UInt(element, "szRatio"))
            .U32(UInt(element, "option")).U32(UInt(element, "styleIDRef"))
            .U32(Attr(element, "align") switch
            {
                "LEFT" => 1u,
                "RIGHT" => 2u,
                "CENTER" => 3u,
                "DISTRIBUTE" => 4u,
                "DISTRIBUTE_SPACE" => 5u,
                _ => 0u,
            });

    /// <summary>글자 겹침: a number in a circle is stored as the circled digit (③), as 한글 does.</summary>
    private RecordData Compose(XElement element)
    {
        var circle = Attr(element, "circleType") switch
        {
            "CHAR" => 0,
            "SHAPE_CIRCLE" => 1,
            "SHAPE_REVERSAL_CIRCLE" => 2,
            "SHAPE_RECTANGLE" => 3,
            "SHAPE_REVERSAL_RECTANGLE" => 4,
            "SHAPE_TRIANGLE" => 5,
            "SHAPE_REVERSAL_TIRANGLE" => 6,
            "SHAPE_LIGHT" => 7,
            "SHAPE_RHOMBUS" => 8,
            "SHAPE_REVERSAL_RHOMBUS" => 9,
            "SHAPE_ROUNDED_RECTANGLE" => 10,
            "SHAPE_EMPTY_CIRCULATE_TRIANGLE" => 11,
            "SHAPE_THIN_CIRCULATE_TRIANGLE" => 12,
            _ => 1,
        };
        var text = Attr(element, "composeText") ?? "";
        if (circle == 1 && int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 20)
            text = ((char)('①' + number - 1)).ToString();
        var shapes = Children(element, "charPr").Take(255).ToList();
        var data = new RecordData().U32(ControlId("tcps")).Text(text).U8(circle).I8(Int(element, "charSz"))
            .U8(Attr(element, "composeType") == "OVERLAP" ? 1 : 0).U8(shapes.Count);
        foreach (var shape in shapes)
            data.U32((uint)_charShapes[Attr(shape, "prIDRef")]);
        return data;
    }

    // ───────────────────────── Sections and columns ─────────────────────────

    /// <summary>
    /// A section definition (secd) and its records: page (PAGE_DEF), footnote and endnote shapes, the three page
    /// borders and, as paragraph lists, the master pages (both, even, odd pages).
    /// </summary>
    private void SectionDefinition(XElement secPr, int level, List<Rec> records)
    {
        var grid = Child(secPr, "grid");
        var start = Child(secPr, "startNum");
        var visibility = Child(secPr, "visibility");

        var masters = new XElement?[3];
        foreach (var reference in Children(secPr, "masterPage"))
        {
            var root = Attr(reference, "idRef") is { Length: > 0 } id
                ? LoadXml(_items.GetValueOrDefault(id) ?? $"Contents/{id}.xml", required: false)?.Root
                : reference;
            var slot = Attr(root, "type") switch { "BOTH" => 0, "EVEN" => 1, "ODD" => 2, _ => -1 };
            if (slot >= 0)
                masters[slot] ??= root;
        }

        var attributes = (Flag(visibility, "hideFirstHeader") ? 1u : 0) | (Flag(visibility, "hideFirstFooter") ? 2u : 0)
                         | (Flag(visibility, "hideFirstMasterPage") ? 4u : 0)
                         | (Attr(visibility, "border") switch { "HIDE_FIRST" => 8u, "SHOW_FIRST" => 0x100u, _ => 0u })
                         | (Attr(visibility, "fill") switch { "HIDE_FIRST" => 0x10u, "SHOW_FIRST" => 0x200u, _ => 0u })
                         | (Flag(visibility, "hideFirstPageNum") ? 0x20u : 0)
                         | (Attr(secPr, "textDirection") == "VERTICAL" ? 1u << 16 : 0)
                         | (Flag(visibility, "hideFirstEmptyLine") ? 1u << 19 : 0)
                         | (uint)(Attr(start, "pageStartsOn") switch { "EVEN" => 1, "ODD" => 2, _ => 0 }) << 20
                         | (Flag(grid, "wonggojiFormat") ? 1u << 22 : 0);
        for (var i = 0; i < 3; i++)
        {
            if (masters[i] is not null)
                attributes |= 1u << (29 + i);
        }

        var outline = Attr(secPr, "outlineShapeIDRef");
        records.Add(new Rec(HwpTag.CtrlHeader, level, new RecordData().U32(ControlId("secd")).U32(attributes)
            .U16(Int(secPr, "spaceColumns")).U16(Int(grid, "lineGrid")).U16(Int(grid, "charGrid")).U32(UInt(secPr, "tabStop", 8000))
            .U16(_numberings.Contains(outline) ? _numberings[outline] : 0)
            .U16(Int(start, "page")).U16(Int(start, "pic")).U16(Int(start, "tbl")).U16(Int(start, "equation"))
            .Zeros(6).U32(_memoShapes.Contains(Attr(secPr, "memoShapeIDRef")) ? (uint)_memoShapes[Attr(secPr, "memoShapeIDRef")] : 0).Zeros(9)));

        var page = Child(secPr, "pagePr");
        var margin = Child(page, "margin");
        records.Add(new Rec(HwpTag.PageDef, level + 1, new RecordData()
            .I32(Int(page, "width", 59528)).I32(Int(page, "height", 84188))
            .I32(Int(margin, "left", 8504)).I32(Int(margin, "right", 8504)).I32(Int(margin, "top", 5668)).I32(Int(margin, "bottom", 4252))
            .I32(Int(margin, "header", 4252)).I32(Int(margin, "footer", 4252)).I32(Int(margin, "gutter"))
            .U32((Attr(page, "landscape") == "NARROWLY" ? 1u : 0) | (uint)(Attr(page, "gutterType") switch { "LEFT_RIGHT" => 1, "TOP_BOTTOM" => 2, _ => 0 }) << 1)));
        records.Add(new Rec(HwpTag.FootnoteShape, level + 1, NoteShape(Child(secPr, "footNotePr"), endnote: false)));
        records.Add(new Rec(HwpTag.FootnoteShape, level + 1, NoteShape(Child(secPr, "endNotePr"), endnote: true)));

        var borders = new XElement?[3];
        foreach (var border in Children(secPr, "pageBorderFill"))
        {
            var slot = Attr(border, "type") switch { "EVEN" => 1, "ODD" => 2, _ => 0 };
            borders[slot] ??= border;
        }
        foreach (var border in borders)
        {
            var offset = Child(border, "offset");
            var fill = Attr(border, "borderFillIDRef");
            records.Add(new Rec(HwpTag.PageBorderFill, level + 1, new RecordData()
                .U32((Attr(border, "textBorder") is null or "PAPER" ? 1u : 0) | (Flag(border, "headerInside") ? 2u : 0) | (Flag(border, "footerInside") ? 4u : 0)
                     | (uint)(Attr(border, "fillArea") switch { "PAGE" => 1, "BORDER" => 2, _ => 0 }) << 3)
                .I16(Int(offset, "left", 1417)).I16(Int(offset, "right", 1417)).I16(Int(offset, "top", 1417)).I16(Int(offset, "bottom", 1417))
                .U16(_borderFills.Contains(fill) ? _borderFills[fill] : _borderFills.Elements.Count > 0 ? 1 : 0)));
        }

        foreach (var master in masters)
        {
            if (master is null)
                continue;
            var subList = Child(master, "subList");
            ListHeader(subList, level + 1, records, data => data.I32(Int(subList, "textWidth")).I32(Int(subList, "textHeight")).Zeros(18));
        }
    }

    /// <summary>A footnote or endnote shape; 한글's defaults when the section has none.</summary>
    private static RecordData NoteShape(XElement? shape, bool endnote)
    {
        var format = Child(shape, "autoNumFormat");
        var line = Child(shape, "noteLine");
        var spacing = Child(shape, "noteSpacing");
        var numbering = Child(shape, "numbering");
        var placement = Child(shape, "placement");
        var place = Attr(placement, "place");
        var attributes = (uint)(NumberFormat(Attr(format, "type") ?? (endnote ? "ROMAN_SMALL" : "DIGIT")) & 0xFF)
                         | (uint)(endnote
                             ? place == "END_OF_SECTION" ? 1 : 0
                             : place switch { "MERGED_COLUMN" => 1, "RIGHT_MOST_COLUMN" => 2, _ => 0 }) << 8
                         | (uint)(Attr(numbering, "type") switch { "ON_SECTION" => 1, "ON_PAGE" => 2, _ => 0 }) << 10
                         | (Flag(format, "supscript", true) ? 0x1000u : 0) | (Flag(placement, "beneathText") ? 0x2000u : 0);
        return new RecordData().U32(attributes)
            .U16(Char(Attr(format, "userChar"))).U16(Char(Attr(format, "prefixChar"))).U16(Char(Attr(format, "suffixChar")))
            .U16(Math.Max(1, Int(numbering, "newNum", 1)))
            .I32(Int(line, "length", -1))
            .U16(Int(spacing, "aboveLine", 850)).U16(Int(spacing, "belowLine", 567)).U16(Int(spacing, "betweenNotes", endnote ? 0 : 283))
            .U8(LineType(Attr(line, "type") ?? "SOLID")).U8(BorderWidth(Attr(line, "width") ?? "0.12 mm")).U32(Color(line, "color", 0));
    }

    /// <summary>A column definition (cold).</summary>
    private static RecordData ColumnDefinition(XElement colPr)
    {
        var count = Math.Clamp(Int(colPr, "colCount", 1), 1, 255);
        var same = Flag(colPr, "sameSz", true);
        var sizes = Children(colPr, "colSz").ToList();
        var attributes = Attr(colPr, "type") switch { "BALANCED_NEWSPAPER" => 1, "PARALLEL" => 2, _ => 0 }
                         | count << 2
                         | (Attr(colPr, "layout") switch { "RIGHT" => 1, "MIRROR" => 2, _ => 0 }) << 10
                         | (same || sizes.Count == 0 ? 0x1000 : 0);
        var data = new RecordData().U32(ControlId("cold")).U16(attributes).U16(same || sizes.Count == 0 ? Int(colPr, "sameGap") : 0);
        if (!(same || sizes.Count == 0) && count > 1)
        {
            for (var i = 0; i < count; i++)
                data.U16(Int(i < sizes.Count ? sizes[i] : null, "width")).U16(Int(i < sizes.Count ? sizes[i] : null, "gap"));
        }
        var line = Child(colPr, "colLine");
        data.U16(0).U8(LineType(Attr(line, "type"))).U8(BorderWidth(Attr(line, "width"))).U32(Color(line, "color", 0));
        return data;
    }
}
