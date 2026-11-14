// Text of PowerPoint 97–2003 shapes ([MS-PPT] 2.9) as DrawingML text bodies: the characters (TextCharsAtom in UTF-16,
// TextBytesAtom one byte per character), paragraphs split at CR and lines at VT, and StyleTextPropAtom's runs of
// paragraph (TextPFException) and character (TextCFException) formatting, each written only where its mask says the
// run sets it, so everything else comes from the master's styles as in PowerPoint. The master's TextMasterStyleAtoms
// become the slide master's title, body and other styles (lvl1pPr–lvl5pPr); the centred title and body, half and
// quarter body styles, which only change the title and body styles, go into the list style of the shapes that use them.
// A TextRulerAtom's margins and indents apply to its shape's paragraphs.
//
// Units: margins and indents are master units (576 per inch), sizes points, line spacing a percentage or (negative)
// master units. Colours are the slide's colour scheme by index (schemeClr, see PptToPptx.Masters) or RGB.
//
// Every count comes from the file: runs are read while they fit in their record, tab stops only as many as fit, and
// the paragraphs count against DocumentLimits.MaxParagraphs.

using System.Globalization;
using System.Text;
using Filee.Engines.Office.OfficeArt;

namespace Filee.Engines.Office.Ppt;

internal sealed partial class PptToPptx
{
    // Text types (TextHeaderAtom).
    private const int TitleText = 0;
    private const int BodyText = 1;
    private const int NotesTextType = 2;
    private const int OtherText = 4;
    private const int CenterBodyText = 5;
    private const int CenterTitleText = 6;
    private const int HalfBodyText = 7;
    private const int QuarterBodyText = 8;

    /// <summary>Indent levels PowerPoint 97–2003 has.</summary>
    private const int Levels = 5;

    /// <summary>A colour of text (ColorIndexStruct): RGB, or index 0–7 into the colour scheme; 0xFF for none.</summary>
    private readonly record struct TextColor(byte Red, byte Green, byte Blue, byte Index);

    /// <summary>The fields of a TextPFException its masks set.</summary>
    private sealed class ParagraphFormat
    {
        public uint Masks;
        public ushort BulletFlags;
        public char? BulletChar;
        public int? BulletFont;
        public short? BulletSize;
        public TextColor? BulletColor;
        public int? Align;
        public short? LineSpacing, SpaceBefore, SpaceAfter, LeftMargin, Indent, DefaultTab;

        public bool Has(int bit) => (Masks & (1u << bit)) != 0;

        /// <summary>This format over <paramref name="under"/>: what this one sets wins.</summary>
        public ParagraphFormat Over(ParagraphFormat? under)
        {
            if (under is null)
                return this;
            var bulletMask = Masks & 0xF;
            return new ParagraphFormat
            {
                Masks = Masks | under.Masks,
                BulletFlags = (ushort)((BulletFlags & bulletMask) | (under.BulletFlags & ~bulletMask)),
                BulletChar = BulletChar ?? under.BulletChar,
                BulletFont = BulletFont ?? under.BulletFont,
                BulletSize = BulletSize ?? under.BulletSize,
                BulletColor = BulletColor ?? under.BulletColor,
                Align = Align ?? under.Align,
                LineSpacing = LineSpacing ?? under.LineSpacing,
                SpaceBefore = SpaceBefore ?? under.SpaceBefore,
                SpaceAfter = SpaceAfter ?? under.SpaceAfter,
                LeftMargin = LeftMargin ?? under.LeftMargin,
                Indent = Indent ?? under.Indent,
                DefaultTab = DefaultTab ?? under.DefaultTab,
            };
        }
    }

    /// <summary>The fields of a TextCFException its masks set.</summary>
    private sealed class CharacterFormat
    {
        public uint Masks;
        public ushort Style;
        public int? Font, EastAsianFont, SymbolFont;
        public short? Size, Position;
        public TextColor? Color;

        public bool Has(int bit) => (Masks & (1u << bit)) != 0;

        public CharacterFormat Over(CharacterFormat? under)
        {
            if (under is null)
                return this;
            var styleMask = Masks & 0xFFFF;
            return new CharacterFormat
            {
                Masks = Masks | under.Masks,
                Style = (ushort)((Style & styleMask) | (under.Style & ~styleMask)),
                Font = Font ?? under.Font,
                EastAsianFont = EastAsianFont ?? under.EastAsianFont,
                SymbolFont = SymbolFont ?? under.SymbolFont,
                Size = Size ?? under.Size,
                Position = Position ?? under.Position,
                Color = Color ?? under.Color,
            };
        }
    }

    /// <summary>The paragraph and character formats of one level of a master text style.</summary>
    private sealed record LevelStyle(ParagraphFormat Paragraph, CharacterFormat Character);

    /// <summary>A shape's text: its type, characters, runs, ruler and fields.</summary>
    private sealed class ShapeText
    {
        public int Type = OtherText;
        public string Characters = "";
        public List<(int Count, int Level, ParagraphFormat Format)> Paragraphs { get; } = [];
        public List<(int Count, CharacterFormat Format)> Runs { get; } = [];
        public short?[] RulerMargins { get; } = new short?[Levels];
        public short?[] RulerIndents { get; } = new short?[Levels];

        /// <summary>Characters that stand for fields: "slidenum" or "datetime".</summary>
        public Dictionary<int, string> Fields { get; } = [];
    }

    // ───────────────────────── Reading ─────────────────────────

    /// <summary>The text of a shape's text records (from its client text box, or the outline the slide list keeps).</summary>
    private ShapeText? ReadText(IEnumerable<OfficeArtRecord> records)
    {
        var bytes = _file.Stream;
        var text = new ShapeText();
        var found = false;
        OfficeArtRecord? style = null;
        foreach (var record in records)
        {
            switch (record.Type)
            {
                case PptRecords.TextHeaderAtom when record.Length >= 4:
                    text.Type = (int)Math.Min(PptRecords.U32(bytes, record.Start), 0xFF);
                    break;
                case PptRecords.TextCharsAtom:
                    text.Characters = Encoding.Unicode.GetString(bytes, record.Start, record.Length & ~1);
                    found = true;
                    break;
                case PptRecords.TextBytesAtom:
                    text.Characters = Encoding.Latin1.GetString(bytes, record.Start, record.Length);
                    found = true;
                    break;
                case PptRecords.StyleTextPropAtom:
                    style = record;
                    break;
                case PptRecords.TextRulerAtom:
                    ReadRuler(record, text);
                    break;
                case PptRecords.SlideNumberMetaCharAtom when record.Length >= 4:
                    text.Fields.TryAdd(PptRecords.I32(bytes, record.Start), "slidenum");
                    break;
                case PptRecords.DateTimeMetaCharAtom when record.Length >= 4:
                    text.Fields.TryAdd(PptRecords.I32(bytes, record.Start), "datetime1");
                    break;
            }
        }
        if (!found)
            return null;
        if (style is { } atom)
            ReadStyles(atom, text);
        return text;
    }

    /// <summary>StyleTextPropAtom: paragraph runs, then character runs, each covering the text and its final CR.</summary>
    private void ReadStyles(OfficeArtRecord atom, ShapeText text)
    {
        var bytes = _file.Stream;
        var total = text.Characters.Length + 1;
        var at = atom.Start;
        var covered = 0;
        while (covered < total && at + 6 <= atom.End)
        {
            var count = (int)Math.Min(PptRecords.U32(bytes, at), int.MaxValue);
            var level = PptRecords.U16(bytes, at + 4);
            at += 6;
            if (ReadParagraphFormat(bytes, ref at, atom.End) is not { } format)
                return;
            text.Paragraphs.Add((count, Math.Min((int)level, Levels - 1), format));
            covered = count > total - covered ? total : covered + count;
        }
        covered = 0;
        while (covered < total && at + 4 <= atom.End)
        {
            var count = (int)Math.Min(PptRecords.U32(bytes, at), int.MaxValue);
            at += 4;
            if (ReadCharacterFormat(bytes, ref at, atom.End) is not { } format)
                return;
            text.Runs.Add((count, format));
            covered = count > total - covered ? total : covered + count;
        }
    }

    /// <summary>TextRuler: the margins and indents of the five levels, where its masks set them.</summary>
    private void ReadRuler(OfficeArtRecord ruler, ShapeText text)
    {
        var bytes = _file.Stream;
        var at = ruler.Start;
        if (at + 4 > ruler.End)
            return;
        var masks = PptRecords.U32(bytes, at);
        at += 4;
        bool Skip(int count)
        {
            at += count;
            return at <= ruler.End;
        }
        if ((masks & 0x2) != 0 && !Skip(2)) // cLevels
            return;
        if ((masks & 0x1) != 0 && !Skip(2)) // defaultTabSize
            return;
        if ((masks & 0x4) != 0 && !SkipTabs(bytes, ref at, ruler.End))
            return;
        for (var level = 0; level < Levels; level++)
        {
            if ((masks & (1u << (3 + level))) != 0)
            {
                if (at + 2 > ruler.End)
                    return;
                text.RulerMargins[level] = PptRecords.I16(bytes, at);
                at += 2;
            }
            if ((masks & (1u << (8 + level))) != 0)
            {
                if (at + 2 > ruler.End)
                    return;
                text.RulerIndents[level] = PptRecords.I16(bytes, at);
                at += 2;
            }
        }
    }

    /// <summary>A TabStops structure: a count, then as many 4-byte stops; false when it doesn't fit.</summary>
    private static bool SkipTabs(byte[] bytes, ref int at, int end)
    {
        if (at + 2 > end)
            return false;
        var count = PptRecords.U16(bytes, at);
        if ((long)at + 2 + count * 4L > end)
            return false;
        at += 2 + count * 4;
        return true;
    }

    /// <summary>A TextPFException at <paramref name="at"/>; null when it doesn't fit before <paramref name="end"/>.</summary>
    private static ParagraphFormat? ReadParagraphFormat(byte[] bytes, ref int at, int end)
    {
        if (at + 4 > end)
            return null;
        var format = new ParagraphFormat { Masks = PptRecords.U32(bytes, at) };
        at += 4;
        var position = at;
        bool Fits(int size) => position + size <= end;
        short Next()
        {
            if (!Fits(2))
                throw new EndOfStreamException();
            var value = PptRecords.I16(bytes, position);
            position += 2;
            return value;
        }
        short? Short(int bit) => format.Has(bit) ? Next() : null;
        try
        {
            if ((format.Masks & 0xF) != 0)
                format.BulletFlags = (ushort)Next();
            if (format.Has(7))
                format.BulletChar = (char)(ushort)Short(7)!;
            if (format.Has(4))
                format.BulletFont = (ushort)Short(4)!;
            format.BulletSize = Short(6);
            if (format.Has(5))
            {
                if (!Fits(4))
                    return null;
                format.BulletColor = new TextColor(bytes[position], bytes[position + 1], bytes[position + 2], bytes[position + 3]);
                position += 4;
            }
            if (format.Has(11))
                format.Align = (ushort)Short(11)!;
            format.LineSpacing = Short(12);
            format.SpaceBefore = Short(13);
            format.SpaceAfter = Short(14);
            format.LeftMargin = Short(8);
            format.Indent = Short(10);
            format.DefaultTab = Short(15);
            if (format.Has(20) && !SkipTabs(bytes, ref position, end))
                return null;
            if (format.Has(16))
                Short(16); // fontAlign
            if ((format.Masks & (0x7u << 17)) != 0)
                Short(17); // wrapFlags
            if (format.Has(21))
                Short(21); // textDirection
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        at = position;
        return format;
    }

    /// <summary>A TextCFException at <paramref name="at"/>; null when it doesn't fit before <paramref name="end"/>.</summary>
    private static CharacterFormat? ReadCharacterFormat(byte[] bytes, ref int at, int end)
    {
        if (at + 4 > end)
            return null;
        var format = new CharacterFormat { Masks = PptRecords.U32(bytes, at) };
        var position = at + 4;
        int? Word(bool present)
        {
            if (!present)
                return null;
            if (position + 2 > end)
                throw new EndOfStreamException();
            var value = PptRecords.U16(bytes, position);
            position += 2;
            return value;
        }
        try
        {
            // fontStyle when any of bold, italic, underline, shadow, fehint, kumi, emboss or the pp9rt bits is set.
            if ((format.Masks & 0x3EB7) != 0)
                format.Style = (ushort)Word(true)!;
            format.Font = Word(format.Has(16));
            format.EastAsianFont = Word(format.Has(21));
            Word(format.Has(22)); // ansiFontRef
            format.SymbolFont = Word(format.Has(23));
            format.Size = (short?)Word(format.Has(17));
            if (format.Has(18))
            {
                if (position + 4 > end)
                    return null;
                format.Color = new TextColor(bytes[position], bytes[position + 1], bytes[position + 2], bytes[position + 3]);
                position += 4;
            }
            format.Position = (short?)Word(format.Has(19));
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        at = position;
        return format;
    }

    /// <summary>
    /// A TextMasterStyleAtom's levels: a count, then each level's paragraph and character format (the centred, half
    /// and quarter styles, types 5 and up, say which level each is). Levels that don't fit are left out.
    /// </summary>
    private LevelStyle?[] ReadMasterStyle(OfficeArtRecord atom)
    {
        var bytes = _file.Stream;
        var levels = new LevelStyle?[Levels];
        var at = atom.Start;
        if (at + 2 > atom.End)
            return levels;
        var count = PptRecords.U16(bytes, at);
        at += 2;
        for (var i = 0; i < count && i < Levels; i++)
        {
            var level = i;
            if (atom.Instance >= CenterBodyText)
            {
                if (at + 2 > atom.End)
                    break;
                level = Math.Min((int)PptRecords.U16(bytes, at), Levels - 1);
                at += 2;
            }
            if (ReadParagraphFormat(bytes, ref at, atom.End) is not { } paragraph || ReadCharacterFormat(bytes, ref at, atom.End) is not { } character)
                break;
            levels[level] = new LevelStyle(paragraph, character);
        }
        return levels;
    }

    // ───────────────────────── Writing ─────────────────────────

    /// <summary>The p:txBody of <paramref name="text"/>: body properties, list style, paragraphs.</summary>
    private void WriteTextBody(StringBuilder xml, ShapeText text, string bodyProperties, MasterStyles styles, bool placeholder)
    {
        xml.Append("<p:txBody>").Append(bodyProperties).Append("<a:lstStyle>");
        // Centred and partial styles change the title or body style for these placeholders; text of a title or body
        // type outside a placeholder has the whole style of its type.
        if (placeholder && text.Type is CenterBodyText or CenterTitleText or HalfBodyText or QuarterBodyText)
            WriteLevels(xml, styles.Own(text.Type), full: false);
        else if (!placeholder && text.Type is TitleText or BodyText or CenterBodyText or CenterTitleText or HalfBodyText or QuarterBodyText)
            WriteLevels(xml, styles.Resolved(text.Type), full: true);
        xml.Append("</a:lstStyle>");

        var chars = text.Characters;
        var paragraphStart = 0;
        var paragraphRun = 0;
        var paragraphRunEnd = text.Paragraphs.Count > 0 ? text.Paragraphs[0].Count : int.MaxValue;
        var characterRun = 0;
        var characterRunEnd = text.Runs.Count > 0 ? text.Runs[0].Count : int.MaxValue;
        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _budget.Paragraph();
            var end = chars.IndexOf('\r', paragraphStart);
            if (end < 0)
                end = chars.Length;
            while (paragraphRun < text.Paragraphs.Count - 1 && paragraphStart >= paragraphRunEnd)
            {
                paragraphRun++;
                paragraphRunEnd = Add(paragraphRunEnd, text.Paragraphs[paragraphRun].Count);
            }
            var (_, level, paragraph) = text.Paragraphs.Count > 0 ? text.Paragraphs[paragraphRun] : (0, 0, new ParagraphFormat());

            xml.Append("<a:p>");
            WriteParagraphProperties(xml, paragraph, level, text, styles);
            CharacterFormat? last = null;
            var at = paragraphStart;
            while (at < end)
            {
                while (characterRun < text.Runs.Count - 1 && at >= characterRunEnd)
                {
                    characterRun++;
                    characterRunEnd = Add(characterRunEnd, text.Runs[characterRun].Count);
                }
                var format = text.Runs.Count > 0 ? text.Runs[characterRun].Format : null;
                last = format;
                var stop = Math.Min(end, characterRun < text.Runs.Count - 1 ? Math.Max(characterRunEnd, at + 1) : end);
                // Line breaks and fields end a run.
                var piece = at;
                while (piece < stop && chars[piece] != '\v' && !text.Fields.ContainsKey(piece))
                    piece++;
                if (piece > at)
                {
                    xml.Append("<a:r>");
                    WriteRunProperties(xml, "a:rPr", format);
                    xml.Append("<a:t>").Append(Escape(chars[at..piece])).Append("</a:t></a:r>");
                    at = piece;
                }
                else if (chars[at] == '\v')
                {
                    xml.Append("<a:br>");
                    WriteRunProperties(xml, "a:rPr", format);
                    xml.Append("</a:br>");
                    at++;
                }
                else
                {
                    var type = text.Fields[at];
                    xml.Append("<a:fld id=\"{").Append(FieldId(at)).Append("}\" type=\"").Append(type).Append("\">");
                    WriteRunProperties(xml, "a:rPr", format);
                    xml.Append("<a:t>").Append(type == "slidenum" ? "‹#›" : Escape(chars[at..(at + 1)])).Append("</a:t></a:fld>");
                    at++;
                }
            }
            // The paragraph mark's own format (the size of an empty line).
            while (characterRun < text.Runs.Count - 1 && end >= characterRunEnd)
            {
                characterRun++;
                characterRunEnd = Add(characterRunEnd, text.Runs[characterRun].Count);
            }
            WriteRunProperties(xml, "a:endParaRPr", text.Runs.Count > 0 ? text.Runs[characterRun].Format : last);
            xml.Append("</a:p>");
            if (end >= chars.Length)
                break;
            paragraphStart = end + 1;
        }
        xml.Append("</p:txBody>");
    }

    private static int Add(int a, int b) => b > int.MaxValue - a ? int.MaxValue : a + b;

    private static string FieldId(int at) => $"B6F15528-21DE-4FAA-801E-{at & 0xFFFFFF:X12}";

    /// <summary>a:pPr of a paragraph: what its run sets, the ruler's and its own margins.</summary>
    private void WriteParagraphProperties(StringBuilder xml, ParagraphFormat format, int level, ShapeText text, MasterStyles styles)
    {
        var margin = format.LeftMargin ?? text.RulerMargins[level];
        var indent = format.Indent ?? text.RulerIndents[level];
        if (margin is not null || indent is not null)
        {
            // marL and indent together: the one not set here comes from the master's style of the level.
            var master = styles.Resolved(text.Type)[level]?.Paragraph;
            format = new ParagraphFormat
            {
                Masks = (1u << 8) | (1u << 10),
                LeftMargin = margin ?? master?.LeftMargin ?? 0,
                Indent = indent ?? master?.Indent ?? 0,
            }.Over(format);
        }
        WriteParagraphFormat(xml, "a:pPr", format, level, null);
    }

    /// <summary>A paragraph's properties (a:pPr or a:lvlNpPr) with the character defaults inside when given.</summary>
    private void WriteParagraphFormat(StringBuilder xml, string element, ParagraphFormat format, int level, CharacterFormat? defaults)
    {
        xml.Append('<').Append(element);
        if (format.LeftMargin is { } margin && format.Has(8))
            xml.Append(" marL=\"").Append(Emu(Math.Max((short)0, margin))).Append('"');
        if (element == "a:pPr" && level > 0)
            xml.Append(" lvl=\"").Append(level).Append('"');
        if (format.Indent is { } indent && format.Has(10))
            xml.Append(" indent=\"").Append(Emu(indent - (format.LeftMargin ?? 0))).Append('"');
        if (format.Align is { } align)
        {
            xml.Append(" algn=\"").Append(align switch
            {
                1 => "ctr",
                2 => "r",
                3 => "just",
                4 => "dist",
                5 => "thaiDist",
                6 => "justLow",
                _ => "l",
            }).Append('"');
        }
        if (format.DefaultTab is > 0 and var tab)
            xml.Append(" defTabSz=\"").Append(Emu(tab)).Append('"');
        xml.Append('>');
        if (format.LineSpacing is { } line)
            Spacing(xml, "a:lnSpc", line);
        if (format.SpaceBefore is { } before)
            Spacing(xml, "a:spcBef", before);
        if (format.SpaceAfter is { } after)
            Spacing(xml, "a:spcAft", after);
        WriteBullet(xml, format);
        if (defaults is not null)
            WriteRunProperties(xml, "a:defRPr", defaults);
        xml.Append("</").Append(element).Append('>');
    }

    /// <summary>Line spacing or space before / after: a percentage, or (negative) master units.</summary>
    private static void Spacing(StringBuilder xml, string element, short value)
    {
        xml.Append('<').Append(element).Append('>');
        if (value >= 0)
            xml.Append("<a:spcPct val=\"").Append(Math.Min(value, (short)13200) * 1000).Append("\"/>");
        else
            xml.Append("<a:spcPts val=\"").Append(Math.Min(-value * 100 / 8, 158400)).Append("\"/>");
        xml.Append("</").Append(element).Append('>');
    }

    /// <summary>The bullet a paragraph format sets: on (with its font, colour, size and character) or off.</summary>
    private void WriteBullet(StringBuilder xml, ParagraphFormat format)
    {
        if (!format.Has(0))
            return;
        if ((format.BulletFlags & 0x1) == 0)
        {
            xml.Append("<a:buNone/>");
            return;
        }
        if (format.Has(2))
        {
            if ((format.BulletFlags & 0x4) != 0 && format.BulletColor is { } color && ColorXml(color) is { } value)
                xml.Append("<a:buClr>").Append(value).Append("</a:buClr>");
            else
                xml.Append("<a:buClrTx/>");
        }
        if (format.Has(3))
        {
            if ((format.BulletFlags & 0x8) != 0 && format.BulletSize is { } size and not 0)
            {
                if (size > 0)
                    xml.Append("<a:buSzPct val=\"").Append(Math.Clamp((int)size, 25, 400) * 1000).Append("\"/>");
                else
                    xml.Append("<a:buSzPts val=\"").Append(Math.Min(-size * 100, 400000)).Append("\"/>");
            }
            else
            {
                xml.Append("<a:buSzTx/>");
            }
        }
        if (format.Has(1))
        {
            if ((format.BulletFlags & 0x2) != 0 && format.BulletFont is { } font && FontName(font) is { } face)
                xml.Append("<a:buFont typeface=\"").Append(Escape(face)).Append("\"/>");
            else
                xml.Append("<a:buFontTx/>");
        }
        var bullet = format.BulletChar is { } c && IsXmlChar(c) && c != ' ' ? c : '•';
        xml.Append("<a:buChar char=\"").Append(Escape(bullet.ToString())).Append("\"/>");
    }

    /// <summary>Run properties (a:rPr, a:endParaRPr or a:defRPr) of what a character format sets.</summary>
    private void WriteRunProperties(StringBuilder xml, string element, CharacterFormat? format)
    {
        xml.Append('<').Append(element).Append(" lang=\"en-US\"");
        if (format is null)
        {
            xml.Append(" dirty=\"0\"/>");
            return;
        }
        if (format.Size is > 0 and var size)
            xml.Append(" sz=\"").Append(Math.Clamp(size * 100, 100, 400000)).Append('"');
        if (format.Has(0))
            xml.Append(" b=\"").Append((format.Style & 0x1) != 0 ? 1 : 0).Append('"');
        if (format.Has(1))
            xml.Append(" i=\"").Append((format.Style & 0x2) != 0 ? 1 : 0).Append('"');
        if (format.Has(2))
            xml.Append(" u=\"").Append((format.Style & 0x4) != 0 ? "sng" : "none").Append('"');
        if (format.Position is { } position and not 0)
            xml.Append(" baseline=\"").Append(Math.Clamp((int)position, -100, 100) * 1000).Append('"');
        xml.Append(" dirty=\"0\">");
        if (format.Color is { } color && ColorXml(color) is { } value)
            xml.Append("<a:solidFill>").Append(value).Append("</a:solidFill>");
        if (format.Font is { } font && FontName(font) is { } latin)
            xml.Append("<a:latin typeface=\"").Append(Escape(latin)).Append("\"/>");
        if (format.EastAsianFont is { } ea && FontName(ea) is { } eastAsian)
            xml.Append("<a:ea typeface=\"").Append(Escape(eastAsian)).Append("\"/>");
        if (format.SymbolFont is { } sym && FontName(sym) is { } symbol)
            xml.Append("<a:sym typeface=\"").Append(Escape(symbol)).Append("\"/>");
        xml.Append("</").Append(element).Append('>');
    }

    /// <summary>The font collection's face of index <paramref name="index"/>.</summary>
    private string? FontName(int index) => _file.Fonts.TryGetValue(index, out var face) && face.Length > 0 ? face : null;

    /// <summary>lvl1pPr–lvl5pPr of a master style's levels.</summary>
    private void WriteLevels(StringBuilder xml, LevelStyle?[] levels, bool full)
    {
        for (var level = 0; level < Levels; level++)
        {
            if (levels[level] is not { } style)
                continue;
            var paragraph = style.Paragraph;
            if (full && (paragraph.LeftMargin is null || paragraph.Indent is null))
                paragraph = paragraph.Over(new ParagraphFormat { Masks = (1u << 8) | (1u << 10), LeftMargin = 0, Indent = 0 });
            WriteParagraphFormat(xml, $"a:lvl{level + 1}pPr", paragraph, level, style.Character);
        }
    }

    /// <summary>A text colour: scheme index 0–7 as the slide's scheme colour, 0xFE as RGB; null for none.</summary>
    private static string? ColorXml(TextColor color) => color.Index switch
    {
        0xFE => $"<a:srgbClr val=\"{color.Red:X2}{color.Green:X2}{color.Blue:X2}\"/>",
        < 8 => $"<a:schemeClr val=\"{SchemeSlots[color.Index]}\"/>",
        _ => null,
    };

    // ───────────────────────── Master styles ─────────────────────────

    /// <summary>A main master's text styles by text type, as stored and with the base type's style under them.</summary>
    private sealed class MasterStyles
    {
        private readonly Dictionary<int, LevelStyle?[]> _own = [];
        private readonly Dictionary<int, LevelStyle?[]> _resolved = [];

        public void Add(int type, LevelStyle?[] levels) => _own.TryAdd(type, levels);

        /// <summary>The levels stored for a type (nothing for types the master doesn't have).</summary>
        public LevelStyle?[] Own(int type) => _own.TryGetValue(type, out var levels) ? levels : new LevelStyle?[Levels];

        /// <summary>
        /// The levels of a type with what they inherit: a level from the level above it, the centred title from the
        /// title, the centred, half and quarter body from the body.
        /// </summary>
        public LevelStyle?[] Resolved(int type)
        {
            if (_resolved.TryGetValue(type, out var cached))
                return cached;
            var own = Own(type);
            var baseType = type switch
            {
                CenterTitleText => TitleText,
                CenterBodyText or HalfBodyText or QuarterBodyText => BodyText,
                _ => -1,
            };
            var under = baseType >= 0 ? Resolved(baseType) : new LevelStyle?[Levels];
            var result = new LevelStyle?[Levels];
            for (var level = 0; level < Levels; level++)
            {
                var below = under[level] ?? (level > 0 ? result[level - 1] : null);
                result[level] = own[level] is { } style
                    ? new LevelStyle(style.Paragraph.Over(below?.Paragraph), style.Character.Over(below?.Character))
                    : below;
                if (result[level] is null && level > 0)
                    result[level] = result[level - 1];
            }
            return _resolved[type] = result;
        }
    }

    // ───────────────────────── Escaping ─────────────────────────

    private static bool IsXmlChar(char c) => c is '\t' or '\n' or '\r' || (c >= 0x20 && c < 0xD800) || (c > 0xDFFF && c < 0xFFFE);

    /// <summary>Text as XML character data and attribute values: markup escaped, characters XML can't hold left out.</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c).Append(text[++i]);
                continue;
            }
            if (!IsXmlChar(c))
                continue;
            sb.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    private static string Emu(double masterUnits) => ((long)Math.Round(masterUnits * EmuPerMasterUnit)).ToString(CultureInfo.InvariantCulture);
}
