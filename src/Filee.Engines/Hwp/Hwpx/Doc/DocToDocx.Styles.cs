// The DOC stylesheet, fonts and lists → styles.xml and numbering.xml.
//
//  * SttbfFfn: the font table, an STTB of FFN records (flags, weight, charset, PANOSE, signature, then the name).
//  * STSH: the stylesheet header (STSHI, with the standard fonts) and one STD per style: its kind, base style, name
//    and property exceptions (UPX: a paragraph style's istd + PAPX grpprl and CHPX grpprl, a character style's CHPX,
//    a table style's TAPX first).
//  * PlfLst: list definitions (LSTF) followed by their levels (LVL: LVLF, PAPX and CHPX grpprls, the number text);
//    PlfLfo: list instances (LFO, what paragraphs' sprmPIlfo names) with their overrides (LFOLVL).

using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx.Doc;

internal sealed partial class DocToDocx
{
    private enum StyleKind
    {
        Paragraph = 1,
        Character = 2,
        Table = 3,
        Numbering = 4,
    }

    private sealed class Std
    {
        public int Istd;
        public int Sti;
        public StyleKind Kind;
        public int Base = 0xFFF;
        public string Name = "";
        public string Id = "";
        public List<Sprm> Papx = [];
        public List<Sprm> Chpx = [];
        public List<Sprm> Tapx = [];
    }

    private readonly List<string?> _fonts = [];
    private readonly Dictionary<int, Std> _styles = [];
    private (int Ascii, int EastAsia, int Other) _standardFonts;
    private readonly Dictionary<int, Dictionary<ushort, bool>> _toggleStates = [];

    private string? FontName(int ftc) => ftc >= 0 && ftc < _fonts.Count ? _fonts[ftc] : null;

    /// <summary>SttbfFfn: cData, cbExtra, then each FFN with its length first; the name starts at byte 40 of the FFN.</summary>
    private void ReadFonts()
    {
        var sttb = _doc.Blob(DocFile.FibIndex.SttbfFfn);
        if (sttb.Length < 4)
            return;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(sttb);
        var at = 4;
        for (var i = 0; i < count && at < sttb.Length; i++)
        {
            var length = sttb[at];
            var ffn = sttb.Slice(at + 1, Math.Min(length, sttb.Length - at - 1));
            at += 1 + length;
            string? name = null;
            if (ffn.Length > 40)
            {
                // The FFN after its length byte: flags, weight, charset, alternate name index, PANOSE, signature, name.
                var chars = Utf16(ffn[39..]);
                var end = chars.IndexOf('\0');
                name = Clean(end >= 0 ? chars[..end] : chars).Trim();
            }
            _fonts.Add(string.IsNullOrEmpty(name) ? null : name);
        }
    }

    private static string Utf16(ReadOnlySpan<byte> bytes) => Encoding.Unicode.GetString(bytes[..(bytes.Length & ~1)]);

    // ───────────────────────── Styles ─────────────────────────

    /// <summary>Built-in style names by sti, for files that don't store them (fStdStylenamesWritten off).</summary>
    private static string? BuiltInName(int sti) => sti switch
    {
        0 => "Normal",
        >= 1 and <= 9 => $"heading {sti}",
        >= 10 and <= 18 => $"index {sti - 9}",
        >= 19 and <= 27 => $"toc {sti - 18}",
        28 => "Normal Indent",
        29 => "footnote text",
        30 => "annotation text",
        31 => "header",
        32 => "footer",
        33 => "index heading",
        34 => "caption",
        38 => "footnote reference",
        39 => "annotation reference",
        40 => "line number",
        41 => "page number",
        42 => "endnote reference",
        43 => "endnote text",
        47 => "List",
        48 => "List Bullet",
        49 => "List Number",
        62 => "Title",
        65 => "Default Paragraph Font",
        66 => "Body Text",
        74 => "Subtitle",
        85 => "Hyperlink",
        86 => "FollowedHyperlink",
        87 => "Strong",
        88 => "Emphasis",
        105 => "Table Normal",
        107 => "No List",
        _ => null,
    };

    /// <summary>
    /// STSH: cbStshi and the STSHI (cstd, cbSTDBaseInFile, flags, ..., rgftcStandardChpStsh at byte 12), then per
    /// style cbStd and the STD: StdfBase (sti, stk, istdBase, cupx), StdfPost2000 when cbSTDBaseInFile says so, the
    /// name (a length and UTF-16 characters, null-terminated) and cupx UPXs, each a length and its bytes padded to
    /// an even length.
    /// </summary>
    private void ReadStyles()
    {
        var stsh = _doc.Blob(DocFile.FibIndex.Stshf);
        if (stsh.Length < 2)
            return;
        var cbStshi = BinaryPrimitives.ReadUInt16LittleEndian(stsh);
        if (2 + cbStshi > stsh.Length || cbStshi < 4)
            return;
        var stshi = stsh.Slice(2, cbStshi);
        var cstd = BinaryPrimitives.ReadUInt16LittleEndian(stshi);
        var cbBase = BinaryPrimitives.ReadUInt16LittleEndian(stshi[2..]);
        if (stshi.Length >= 18)
        {
            _standardFonts = (BinaryPrimitives.ReadUInt16LittleEndian(stshi[12..]), BinaryPrimitives.ReadUInt16LittleEndian(stshi[14..]),
                BinaryPrimitives.ReadUInt16LittleEndian(stshi[16..]));
        }
        var at = 2 + cbStshi;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var istd = 0; istd < cstd && at + 2 <= stsh.Length; istd++)
        {
            var cbStd = BinaryPrimitives.ReadUInt16LittleEndian(stsh[at..]);
            at += 2;
            if (cbStd == 0)
                continue;
            if (at + cbStd > stsh.Length)
                break;
            var std = stsh.Slice(at, cbStd);
            at += cbStd;
            if (ReadStd(std, cbBase, istd) is not { } style)
                continue;
            var id = new string(style.Name.Where(char.IsLetterOrDigit).ToArray());
            if (id.Length == 0)
                id = "Style" + istd;
            var unique = id;
            for (var n = 2; !ids.Add(unique); n++)
                unique = id + n;
            style.Id = unique;
            _styles[istd] = style;
        }
    }

    private Std? ReadStd(ReadOnlySpan<byte> std, int cbBase, int istd)
    {
        if (std.Length < 10)
            return null;
        var style = new Std
        {
            Istd = istd,
            Sti = BinaryPrimitives.ReadUInt16LittleEndian(std) & 0x0FFF,
            Kind = (StyleKind)(BinaryPrimitives.ReadUInt16LittleEndian(std[2..]) & 0x0F),
            Base = BinaryPrimitives.ReadUInt16LittleEndian(std[2..]) >> 4,
        };
        if (style.Kind is < StyleKind.Paragraph or > StyleKind.Numbering)
            return null;
        var cupx = BinaryPrimitives.ReadUInt16LittleEndian(std[4..]) & 0x0F;
        var at = Math.Max(10, cbBase);
        if (at + 2 <= std.Length)
        {
            var cch = BinaryPrimitives.ReadUInt16LittleEndian(std[at..]);
            if (at + 2 + cch * 2 <= std.Length)
            {
                style.Name = Clean(Encoding.Unicode.GetString(std.Slice(at + 2, cch * 2)));
                at += 2 + cch * 2 + 2;
            }
            else
            {
                at = std.Length;
            }
        }
        if (style.Name.Length == 0)
            style.Name = BuiltInName(style.Sti) ?? $"Style {istd}";
        var upxs = new List<byte[]>();
        for (var i = 0; i < cupx && at + 2 <= std.Length; i++)
        {
            var cb = BinaryPrimitives.ReadUInt16LittleEndian(std[at..]);
            at += 2;
            if (at + cb > std.Length)
                break;
            upxs.Add(std.Slice(at, cb).ToArray());
            at += cb + (cb & 1);
        }
        // Paragraph styles: PAPX (istd first) and CHPX; character styles: CHPX; tables: TAPX, PAPX, CHPX; lists: PAPX.
        byte[]? Upx(int index) => index < upxs.Count ? upxs[index] : null;
        switch (style.Kind)
        {
            case StyleKind.Paragraph:
                style.Papx = Upx(0) is { Length: >= 2 } papx ? Sprm.Parse(papx.AsSpan(2)) : [];
                style.Chpx = Upx(1) is { } chpx ? Sprm.Parse(chpx) : [];
                break;
            case StyleKind.Character:
                style.Chpx = Upx(0) is { } characters ? Sprm.Parse(characters) : [];
                break;
            case StyleKind.Table:
                style.Tapx = Upx(0) is { } tapx ? Sprm.Parse(tapx) : [];
                style.Papx = Upx(1) is { Length: >= 2 } tablePapx ? Sprm.Parse(tablePapx.AsSpan(2)) : [];
                style.Chpx = Upx(2) is { } tableChpx ? Sprm.Parse(tableChpx) : [];
                break;
            case StyleKind.Numbering:
                style.Papx = Upx(0) is { Length: >= 2 } listPapx ? Sprm.Parse(listPapx.AsSpan(2)) : [];
                break;
        }
        return style;
    }

    /// <summary>The style id of style <paramref name="istd"/> when it is of <paramref name="kind"/>.</summary>
    private string? StyleId(int istd, StyleKind kind) =>
        _styles.TryGetValue(istd, out var style) && style.Kind == kind ? style.Id : null;

    /// <summary>The values of the toggle properties a style ends up with, through its base styles.</summary>
    private Dictionary<ushort, bool> ToggleState(int istd) => ToggleState(istd, 0);

    private Dictionary<ushort, bool> ToggleState(int istd, int depth)
    {
        if (_toggleStates.TryGetValue(istd, out var cached))
            return cached;
        var state = new Dictionary<ushort, bool>();
        _toggleStates[istd] = state; // a base chain that loops ends here
        if (!_styles.TryGetValue(istd, out var style))
            return state;
        if (style.Base != 0xFFF && style.Base != istd && depth < 20)
        {
            foreach (var (op, value) in ToggleState(style.Base, depth + 1))
                state[op] = value;
        }
        foreach (var sprm in style.Chpx)
        {
            if (!Toggles.ContainsKey(sprm.Op))
                continue;
            var below = state.GetValueOrDefault(sprm.Op);
            state[sprm.Op] = sprm.Byte switch
            {
                0 => false,
                1 => true,
                0x81 => !below,
                _ => below,
            };
        }
        return state;
    }

    private XElement StylesXml()
    {
        var styles = new XElement(W + "styles", Namespaces());
        var fonts = new XElement(W + "rFonts");
        if (FontName(_standardFonts.Ascii) is { } ascii)
            fonts.Add(new XAttribute(W + "ascii", ascii));
        if (FontName(_standardFonts.EastAsia) is { } eastAsia)
            fonts.Add(new XAttribute(W + "eastAsia", eastAsia));
        if (FontName(_standardFonts.Other) is { } other)
            fonts.Add(new XAttribute(W + "hAnsi", other));
        // Word 97's character defaults: the standard fonts at 10 pt; paragraphs start from nothing.
        styles.Add(new XElement(W + "docDefaults",
            new XElement(W + "rPrDefault", new XElement(W + "rPr", fonts.HasAttributes ? fonts : null,
                new XElement(W + "sz", new XAttribute(W + "val", 20)), new XElement(W + "szCs", new XAttribute(W + "val", 20)))),
            new XElement(W + "pPrDefault")));
        foreach (var style in _styles.Values.OrderBy(s => s.Istd))
        {
            var element = new XElement(W + "style",
                new XAttribute(W + "type", style.Kind switch
                {
                    StyleKind.Character => "character",
                    StyleKind.Table => "table",
                    StyleKind.Numbering => "numbering",
                    _ => "paragraph",
                }),
                new XAttribute(W + "styleId", style.Id));
            // The defaults: Normal (istd 0), Default Paragraph Font (sti 65), Table Normal (105), No List (107).
            if (style.Istd == 0 || style.Sti is 65 or 105 or 107)
                element.Add(new XAttribute(W + "default", "1"));
            element.Add(new XElement(W + "name", new XAttribute(W + "val", style.Name)));
            if (style.Base != 0xFFF && _styles.TryGetValue(style.Base, out var baseStyle) && baseStyle.Kind == style.Kind && style.Base != style.Istd)
                element.Add(new XElement(W + "basedOn", new XAttribute(W + "val", baseStyle.Id)));
            if (style.Kind is StyleKind.Paragraph or StyleKind.Table or StyleKind.Numbering)
            {
                var pPr = new Bag(W + "pPr", PPrOrder);
                ApplyParagraph(pPr, style.Papx);
                // Word's heading styles have their outline level from their sti.
                if (style.Kind == StyleKind.Paragraph && style.Sti is >= 1 and <= 9 && !pPr.Has("outlineLvl"))
                    pPr.Value("outlineLvl", style.Sti - 1);
                if (pPr.Build() is { HasElements: true } paragraph)
                    element.Add(paragraph);
            }
            if (style.Kind != StyleKind.Numbering)
            {
                var rPr = new Bag(W + "rPr", RPrOrder);
                var below = style.Base != 0xFFF && style.Base != style.Istd ? ToggleState(style.Base) : [];
                ApplyCharacter(rPr, style.Chpx, below, direct: false);
                if (rPr.Build() is { HasElements: true } run)
                    element.Add(run);
            }
            if (style.Kind == StyleKind.Table && TableStyleProperties(style.Tapx) is { HasElements: true } tblPr)
                element.Add(tblPr);
            styles.Add(element);
        }
        return styles;
    }

    // ───────────────────────── Lists ─────────────────────────

    private sealed class ListLevel
    {
        public int Start = 1;
        public int Nfc;
        public int Jc;
        public int Follow;
        public string Text = "";
        public List<Sprm> Papx = [];
        public List<Sprm> Chpx = [];
    }

    private sealed record ListDefinition(int Lsid, ListLevel[] Levels);

    private sealed record ListOverride(int Level, int? Start, ListLevel? Formatting);

    private sealed record ListInstance(int Lsid, List<ListOverride> Overrides);

    private readonly List<ListDefinition> _lists = [];
    private readonly List<ListInstance> _lfos = [];

    private void ReadLists()
    {
        var (fc, _) = _doc.Location(DocFile.FibIndex.PlfLst);
        var table = _doc.Table;
        if (_doc.Blob(DocFile.FibIndex.PlfLst).Length >= 2)
        {
            var count = BinaryPrimitives.ReadInt16LittleEndian(table.AsSpan(fc));
            var at = fc + 2;
            var lstfs = new List<(int Lsid, bool Simple)>();
            for (var i = 0; i < count && at + 28 <= table.Length; i++, at += 28)
                lstfs.Add((BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(at)), (table[at + 26] & 1) != 0));
            // The levels of every list follow the LSTFs (lcbPlfLst does not count them).
            foreach (var (lsid, simple) in lstfs)
            {
                var levels = new ListLevel[9];
                for (var level = 0; level < 9; level++)
                    levels[level] = new ListLevel { Text = (char)level + "." };
                for (var level = 0; level < (simple ? 1 : 9); level++)
                {
                    if (ReadLevel(table, ref at) is not { } read)
                        break;
                    levels[level] = read;
                }
                _lists.Add(new ListDefinition(lsid, levels));
            }
        }

        var lfo = _doc.Blob(DocFile.FibIndex.PlfLfo);
        if (lfo.Length < 4)
            return;
        var (lfoFc, _) = _doc.Location(DocFile.FibIndex.PlfLfo);
        var lfoMac = BinaryPrimitives.ReadInt32LittleEndian(lfo);
        var position = lfoFc + 4;
        var instances = new List<(int Lsid, int Levels)>();
        for (var i = 0; i < lfoMac && position + 16 <= table.Length; i++, position += 16)
            instances.Add((BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(position)), table[position + 12]));
        // LFOData per LFO: a cp, then clfolvl LFOLVLs (iStartAt, flags: level, fStartAt, fFormatting), each with an LVL when formatted.
        foreach (var (lsid, levels) in instances)
        {
            var overrides = new List<ListOverride>();
            if (levels > 0 && position + 4 <= table.Length)
            {
                position += 4;
                for (var k = 0; k < Math.Min(levels, 9) && position + 8 <= table.Length; k++)
                {
                    var start = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(position));
                    var flags = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(position + 4));
                    position += 8;
                    ListLevel? formatting = null;
                    if ((flags & 0x20) != 0 && (formatting = ReadLevel(table, ref position)) is null)
                        break;
                    overrides.Add(new ListOverride((int)(flags & 0x0F), (flags & 0x10) != 0 ? start : null, formatting));
                }
            }
            _lfos.Add(new ListInstance(lsid, overrides));
        }
    }

    /// <summary>LVL: LVLF (28 bytes), its PAPX and CHPX grpprls, then the number text (an Xst: length and UTF-16).</summary>
    private ListLevel? ReadLevel(byte[] table, ref int at)
    {
        if (at + 28 > table.Length)
            return null;
        var lvlf = table.AsSpan(at, 28);
        var level = new ListLevel
        {
            Start = BinaryPrimitives.ReadInt32LittleEndian(lvlf),
            Nfc = lvlf[4],
            Jc = lvlf[5] & 3,
            Follow = lvlf[15],
        };
        var cbChpx = lvlf[24];
        var cbPapx = lvlf[25];
        at += 28;
        if (at + cbPapx + cbChpx + 2 > table.Length)
            return null;
        level.Papx = Sprm.Parse(table.AsSpan(at, cbPapx));
        at += cbPapx;
        level.Chpx = Sprm.Parse(table.AsSpan(at, cbChpx));
        at += cbChpx;
        var cch = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(at));
        at += 2;
        if (at + cch * 2 > table.Length)
            return null;
        level.Text = Encoding.Unicode.GetString(table, at, cch * 2);
        at += cch * 2;
        return level;
    }

    private XElement NumberingXml()
    {
        var numbering = new XElement(W + "numbering", Namespaces());
        for (var i = 0; i < _lists.Count; i++)
            numbering.Add(new XElement(W + "abstractNum", new XAttribute(W + "abstractNumId", i), _lists[i].Levels.Select((level, index) => LevelXml(level, index))));
        for (var i = 0; i < _lfos.Count; i++)
        {
            var list = _lists.FindIndex(l => l.Lsid == _lfos[i].Lsid);
            if (list < 0)
                continue;
            var num = new XElement(W + "num", new XAttribute(W + "numId", i + 1), new XElement(W + "abstractNumId", new XAttribute(W + "val", list)));
            foreach (var over in _lfos[i].Overrides.Where(o => o.Level is >= 0 and <= 8))
            {
                num.Add(new XElement(W + "lvlOverride", new XAttribute(W + "ilvl", over.Level),
                    over.Start is { } start ? new XElement(W + "startOverride", new XAttribute(W + "val", start)) : null,
                    over.Formatting is { } formatting ? LevelXml(formatting, over.Level) : null));
            }
            numbering.Add(num);
        }
        return numbering;
    }

    private XElement LevelXml(ListLevel level, int index)
    {
        // The number text holds the levels' numbers as characters 0–8: "\u0000." is "%1.".
        var text = new StringBuilder();
        foreach (var c in level.Text)
        {
            if (c < 9)
                text.Append('%').Append((char)('1' + c));
            else if (c >= ' ')
                text.Append(c);
        }
        var pPr = new Bag(W + "pPr", PPrOrder);
        ApplyParagraph(pPr, level.Papx.Where(s => s.Op is not (Ops.PIlfo or Ops.PIlvl)));
        var rPr = new Bag(W + "rPr", RPrOrder);
        ApplyCharacter(rPr, level.Chpx, new Dictionary<ushort, bool>(), direct: false);
        return new XElement(W + "lvl", new XAttribute(W + "ilvl", index),
            new XElement(W + "start", new XAttribute(W + "val", level.Start)),
            new XElement(W + "numFmt", new XAttribute(W + "val", NumberFormat(level.Nfc))),
            new XElement(W + "suff", new XAttribute(W + "val", level.Follow switch { 1 => "space", 2 => "nothing", _ => "tab" })),
            new XElement(W + "lvlText", new XAttribute(W + "val", text.ToString())),
            new XElement(W + "lvlJc", new XAttribute(W + "val", level.Jc switch { 1 => "center", 2 => "right", _ => "left" })),
            pPr.Build() is { HasElements: true } paragraph ? paragraph : null,
            rPr.Build() is { HasElements: true } run ? run : null);
    }

    /// <summary>An nfc (MSONFC) as a WordprocessingML number format.</summary>
    private static string NumberFormat(int nfc) => nfc switch
    {
        0 => "decimal",
        1 => "upperRoman",
        2 => "lowerRoman",
        3 => "upperLetter",
        4 => "lowerLetter",
        5 => "ordinal",
        6 => "cardinalText",
        7 => "ordinalText",
        8 => "hex",
        9 => "chicago",
        10 => "ideographDigital",
        11 => "japaneseCounting",
        12 => "aiueo",
        13 => "iroha",
        14 => "decimalFullWidth",
        15 => "decimalHalfWidth",
        16 => "japaneseLegal",
        17 => "japaneseDigitalTenThousand",
        18 => "decimalEnclosedCircle",
        19 => "decimalFullWidth2",
        20 => "aiueoFullWidth",
        21 => "irohaFullWidth",
        22 => "decimalZero",
        23 => "bullet",
        24 => "ganada",
        25 => "chosung",
        26 => "decimalEnclosedFullstop",
        27 => "decimalEnclosedParen",
        28 => "decimalEnclosedCircleChinese",
        29 => "ideographEnclosedCircle",
        30 => "ideographTraditional",
        31 => "ideographZodiac",
        32 => "ideographZodiacTraditional",
        33 => "taiwaneseCounting",
        34 => "ideographLegalTraditional",
        35 => "taiwaneseCountingThousand",
        36 => "taiwaneseDigital",
        37 => "chineseCounting",
        38 => "chineseLegalSimplified",
        39 => "chineseCountingThousand",
        40 => "koreanDigital",
        41 => "koreanCounting",
        42 => "koreanLegal",
        43 => "koreanDigital2",
        44 => "vietnameseCounting",
        45 => "russianLower",
        46 => "russianUpper",
        47 => "none",
        48 => "numberInDash",
        49 => "hebrew1",
        50 => "hebrew2",
        51 => "arabicAlpha",
        52 => "arabicAbjad",
        53 => "hindiVowels",
        54 => "hindiConsonants",
        55 => "hindiNumbers",
        56 => "hindiCounting",
        57 => "thaiLetters",
        58 => "thaiNumbers",
        59 => "thaiCounting",
        255 => "none",
        _ => "decimal",
    };
}
