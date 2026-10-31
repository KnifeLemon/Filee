// DocInfo → header.xml. Ids carry over unchanged: fonts, character and paragraph shapes, tab definitions and styles
// count from 0, border fills, numberings and bullets from 1, in both formats.
//
// Paragraph margins, spacing and tab positions are stored at twice their size in HWP (as in the hp:default branch 한글
// writes into HWPX for older readers); the hp:case branch gets the true value, the default branch the stored one.

using System.Globalization;

namespace Filee.Engines.Hwp.Binary;

internal sealed partial class HwpToHwpx
{
    private static readonly string[] Languages = ["HANGUL", "LATIN", "HANJA", "JAPANESE", "OTHER", "SYMBOL", "USER"];

    /// <summary>The BinData items of DocInfo: needed by the sections (pictures) before header.xml is written.</summary>
    private void LoadBinData(List<HwpRecord> docInfo)
    {
        var index = 0;
        foreach (var record in docInfo.Where(r => r.Tag == HwpTag.BinData))
        {
            index++;
            var r = record.Reader();
            var attributes = r.U16();
            var type = attributes & 0xF;
            if (type is not (1 or 2))
                continue; // a link to a file outside the document
            var id = r.U16();
            var extension = new string(r.Text().Where(char.IsAsciiLetterOrDigit).Take(8).ToArray());
            if (extension.Length == 0)
                extension = type == 2 ? "ole" : "bin";
            var compression = (attributes >> 4) & 3;
            var compressed = compression == 0 ? _file.Compressed : compression == 1;
            // Pictures name the BinData record (1-based) they use; the stream is named by the record's own id.
            _bins[index] = new BinItem(id, (type == 1 ? "image" : "ole") + index.ToString(CultureInfo.InvariantCulture), extension, compressed, type == 1);
        }
    }

    private void WriteHeader(Stream stream, List<HwpRecord> docInfo, int sections)
    {
        using var writer = StartPart(stream, "hh", "head", Hh);
        _x = writer;
        Attr("version", "1.5");
        Attr("secCnt", sections);

        var properties = ReaderOf(docInfo.FirstOrDefault(r => r.Tag == HwpTag.DocumentProperties));
        properties.Skip(2);
        Empty(Hh, "beginNum", ("page", Math.Max(1, (int)properties.U16())), ("footnote", Math.Max(1, (int)properties.U16())),
            ("endnote", Math.Max(1, (int)properties.U16())), ("pic", Math.Max(1, (int)properties.U16())),
            ("tbl", Math.Max(1, (int)properties.U16())), ("equation", Math.Max(1, (int)properties.U16())));

        Start(Hh, "refList");
        FontFaces(docInfo);
        Items(docInfo, HwpTag.BorderFill, "borderFills", (record, i) => BorderFill(record, i + 1));
        Items(docInfo, HwpTag.CharShape, "charProperties", CharShape);
        Items(docInfo, HwpTag.TabDef, "tabProperties", TabDef);
        Items(docInfo, HwpTag.Numbering, "numberings", (record, i) => Numbering(record, i + 1));
        Items(docInfo, HwpTag.Bullet, "bullets", (record, i) => Bullet(record, i + 1));
        Items(docInfo, HwpTag.ParaShape, "paraProperties", ParaShape);
        Items(docInfo, HwpTag.Style, "styles", Style);
        End();

        var target = ReaderOf(docInfo.FirstOrDefault(r => r.Tag == HwpTag.CompatibleDocument)).U32();
        Start(Hh, "compatibleDocument");
        Attr("targetProgram", target switch { 1 => "HWP200X", 2 => "MS_WORD", _ => "HWP201X" });
        Empty(Hh, "layoutCompatibility");
        End();
        Start(Hh, "docOption");
        Empty(Hh, "linkinfo", ("path", ""), ("pageInherit", false), ("footnoteInherit", false));
        End();
        End();
        writer.WriteEndDocument();
    }

    /// <summary>A list element (itemCnt) with one item per record of <paramref name="tag"/>.</summary>
    private void Items(List<HwpRecord> docInfo, int tag, string name, Action<HwpRecord, int> item)
    {
        var records = docInfo.Where(r => r.Tag == tag).ToList();
        if (records.Count == 0)
            return;
        Start(Hh, name);
        Attr("itemCnt", records.Count);
        for (var i = 0; i < records.Count; i++)
            item(records[i], i);
        End();
    }

    // ───────────────────────── Fonts ─────────────────────────

    /// <summary>
    /// FACE_NAME records come language by language (한글, 영문, 한자, 일어, 기타, 기호, 사용자), as many of each as
    /// ID_MAPPINGS counts.
    /// </summary>
    private void FontFaces(List<HwpRecord> docInfo)
    {
        var faces = docInfo.Where(r => r.Tag == HwpTag.FaceName).ToList();
        var mappings = ReaderOf(docInfo.FirstOrDefault(r => r.Tag == HwpTag.IdMappings));
        mappings.Skip(4);
        var counts = new int[Languages.Length];
        for (var i = 0; i < counts.Length; i++)
            counts[i] = Math.Clamp(mappings.I32(), 0, faces.Count);
        if (counts.Sum(c => (long)c) != faces.Count && faces.Count > 0)
        {
            // Counts that don't add up: every language gets the whole list.
            Array.Fill(counts, -1);
        }

        Start(Hh, "fontfaces");
        Attr("itemCnt", Languages.Length);
        var next = 0;
        for (var language = 0; language < Languages.Length; language++)
        {
            var list = counts[language] < 0 ? faces : faces.Skip(next).Take(counts[language]).ToList();
            if (counts[language] >= 0)
                next += counts[language];
            Start(Hh, "fontface");
            Attr("lang", Languages[language]);
            Attr("fontCnt", list.Count);
            for (var i = 0; i < list.Count; i++)
                Font(list[i], i);
            End();
        }
        End();
    }

    private void Font(HwpRecord record, int id)
    {
        var r = record.Reader();
        var attributes = r.U8();
        var name = r.Text();
        Start(Hh, "font");
        Attr("id", id);
        Attr("face", name);
        Attr("type", "TTF");
        Attr("isEmbedded", false);
        if ((attributes & 0x80) != 0)
        {
            var type = r.U8();
            var alternate = r.Text();
            if (alternate.Length > 0)
                Empty(Hh, "substFont", ("face", alternate), ("type", type == 2 ? "HFT" : "TTF"), ("isEmbedded", false), ("binaryItemIDRef", ""));
        }
        if ((attributes & 0x40) != 0)
        {
            Span<byte> panose = stackalloc byte[10];
            for (var i = 0; i < panose.Length; i++)
                panose[i] = r.U8();
            Empty(Hh, "typeInfo", ("familyType", panose[0] switch
            {
                1 => "FCAT_MYUNGJO",
                2 => "FCAT_GOTHIC",
                3 => "FCAT_SSERIF",
                4 => "FCAT_BRUSHSCRIPT",
                5 => "FCAT_DECORATIVE",
                6 => "FCAT_NONRECTMJ",
                7 => "FCAT_NONRECTGT",
                _ => "FCAT_UNKNOWN",
            }), ("weight", panose[2]), ("proportion", panose[3]), ("contrast", panose[4]), ("strokeVariation", panose[5]),
                ("armStyle", panose[6]), ("letterform", panose[7]), ("midline", panose[8]), ("xHeight", panose[9]));
        }
        End();
    }

    // ───────────────────────── Border fills ─────────────────────────

    private void BorderFill(HwpRecord record, int id)
    {
        var r = record.Reader();
        var attributes = r.U16();
        Start(Hh, "borderFill");
        Attr("id", id);
        Attr("threeD", (attributes & 1) != 0);
        Attr("shadow", (attributes & 2) != 0);
        Attr("centerLine", "NONE");
        Attr("breakCellSeparateLine", false);
        Empty(Hh, "slash", ("type", Diagonal((attributes >> 2) & 7)), ("Crooked", (attributes & 0x300) != 0), ("isCounter", (attributes & 0x1000) != 0));
        Empty(Hh, "backSlash", ("type", Diagonal((attributes >> 5) & 7)), ("Crooked", (attributes & 0x400) != 0), ("isCounter", (attributes & 0x2000) != 0));
        foreach (var side in (string[])["leftBorder", "rightBorder", "topBorder", "bottomBorder", "diagonal"])
        {
            var type = r.U8();
            var width = r.U8();
            var color = r.U32();
            Empty(Hh, side, ("type", LineType(type)), ("width", BorderWidth(width)), ("color", Color(color)));
        }
        FillBrush(ref r);
        End();
    }

    private static string Diagonal(int value) => value switch
    {
        0 => "NONE",
        2 => "CENTER",
        3 => "CENTER_BELOW",
        6 => "CENTER_ABOVE",
        7 => "ALL",
        _ => "NONE",
    };

    /// <summary>
    /// Fill information (border fills and drawing objects): a solid colour with an optional hatch, a gradation and a
    /// picture, any of them, then the extra properties (the gradation's step centre) and the alpha of each.
    /// </summary>
    /// <param name="write">False reads past the fill without writing it (connectors have none in HWPX).</param>
    private void FillBrush(ref RecordReader r, bool write = true)
    {
        var type = r.U32();
        if (type == 0)
        {
            r.Skip(4); // extra properties: none
            return;
        }
        uint? face = null, hatch = null;
        var pattern = -1;
        if ((type & 1) != 0)
            (face, hatch, pattern) = (r.U32(), r.U32(), r.I32());

        var gradient = (Type: 0, Angle: 0, X: 0, Y: 0, Step: 0, Colors: new List<uint>());
        if ((type & 4) != 0)
        {
            gradient.Type = r.U8();
            (gradient.Angle, gradient.X, gradient.Y, gradient.Step) = (r.I32(), r.I32(), r.I32(), r.I32());
            var count = Math.Clamp(r.I32(), 0, 256);
            if (count > 2)
                r.Skip(4 * count); // where the colours change
            for (var i = 0; i < count; i++)
                gradient.Colors.Add(r.U32());
        }
        var image = (Mode: 0, Bright: 0, Contrast: 0, Effect: 0, Bin: 0);
        if ((type & 2) != 0)
            image = (r.U8(), r.I8(), r.I8(), r.U8(), r.U16());
        var extra = Math.Clamp((int)r.U32(), 0, 1 << 16);
        var stepCenter = (type & 4) != 0 && extra > 0 ? r.U8() : 50;
        r.Skip(extra - ((type & 4) != 0 && extra > 0 ? 1 : 0));
        r.Skip(System.Numerics.BitOperations.PopCount(type & 7)); // the alpha of each fill
        if (!write)
            return;

        Start(Hc, "fillBrush");
        if (face is not null)
        {
            Start(Hc, "winBrush");
            Attr("faceColor", Color(face.Value));
            Attr("hatchColor", Color(hatch!.Value));
            if (pattern is >= 1 and <= 6)
                Attr("hatchStyle", pattern switch { 1 => "HORIZONTAL", 2 => "VERTICAL", 3 => "BACK_SLASH", 4 => "SLASH", 5 => "CROSS", _ => "CROSS_DIAGONAL" });
            Attr("alpha", 0);
            End();
        }
        if ((type & 4) != 0)
        {
            Start(Hc, "gradation");
            Attr("type", gradient.Type switch { 2 => "RADIAL", 3 => "CONICAL", 4 => "SQUARE", _ => "LINEAR" });
            Attr("angle", gradient.Angle);
            Attr("centerX", gradient.X);
            Attr("centerY", gradient.Y);
            Attr("step", gradient.Step);
            Attr("colorNum", gradient.Colors.Count);
            Attr("stepCenter", stepCenter);
            Attr("alpha", 0);
            foreach (var color in gradient.Colors)
                Empty(Hc, "color", ("value", Color(color)));
            End();
        }
        if ((type & 2) != 0)
        {
            Start(Hc, "imgBrush");
            Attr("mode", ImageMode(image.Mode));
            Image(image.Bin, image.Bright, image.Contrast, image.Effect);
            End();
        }
        End();
    }

    private static string ImageMode(int value) => value switch
    {
        0 => "TILE",
        1 => "TILE_HORZ_TOP",
        2 => "TILE_HORZ_BOTTOM",
        3 => "TILE_VERT_LEFT",
        4 => "TILE_VERT_RIGHT",
        5 => "TOTAL",
        6 => "CENTER",
        7 => "CENTER_TOP",
        8 => "CENTER_BOTTOM",
        9 => "LEFT_CENTER",
        10 => "LEFT_TOP",
        11 => "LEFT_BOTTOM",
        12 => "RIGHT_CENTER",
        13 => "RIGHT_TOP",
        14 => "RIGHT_BOTTOM",
        _ => "ZOOM",
    };

    /// <summary>hc:img: a picture by its BinData record, with its brightness, contrast and effect.</summary>
    private void Image(int bin, int bright, int contrast, int effect)
    {
        Start(Hc, "img");
        Attr("binaryItemIDRef", _bins.TryGetValue(bin, out var item) ? item.ItemId : "");
        Attr("bright", bright);
        Attr("contrast", contrast);
        Attr("effect", effect switch { 1 => "GRAY_SCALE", 2 => "BLACK_WHITE", _ => "REAL_PIC" });
        Attr("alpha", 0);
        End();
    }

    // ───────────────────────── Character shapes ─────────────────────────

    private void CharShape(HwpRecord record, int id)
    {
        var r = record.Reader();
        Span<int> faces = stackalloc int[7], ratios = stackalloc int[7], spacings = stackalloc int[7], sizes = stackalloc int[7], offsets = stackalloc int[7];
        for (var i = 0; i < 7; i++)
            faces[i] = r.U16();
        for (var i = 0; i < 7; i++)
            ratios[i] = r.U8();
        for (var i = 0; i < 7; i++)
            spacings[i] = r.I8();
        for (var i = 0; i < 7; i++)
            sizes[i] = r.U8();
        for (var i = 0; i < 7; i++)
            offsets[i] = r.I8();
        var height = r.I32();
        var attributes = r.U32();
        var (shadowX, shadowY) = (r.I8(), r.I8());
        var (text, underline, shade, shadow) = (r.U32(), r.U32(), r.U32(), r.U32());
        int? borderFill = r.Remaining >= 2 ? r.U16() : null;
        var strike = r.Remaining >= 4 ? r.U32() : 0;

        Start(Hh, "charPr");
        Attr("id", id);
        Attr("height", height);
        Attr("textColor", Color(text));
        Attr("shadeColor", Color(shade));
        Attr("useFontSpace", (attributes & (1u << 25)) != 0);
        Attr("useKerning", (attributes & (1u << 30)) != 0);
        Attr("symMark", ((attributes >> 21) & 0xF) switch
        {
            0 => "NONE",
            1 => "DOT_ABOVE",
            2 => "RING_ABOVE",
            3 => "CARON",
            4 => "TILDE",
            5 => "SIDE",
            6 => "COLON",
            7 => "GRAVE_ACCENT",
            8 => "ACUTE_ACCENT",
            9 => "CIRCUMFLEX",
            10 => "MACRON",
            11 => "HOOK_ABOVE",
            _ => "DOT_BELOW",
        });
        if (borderFill is not null)
            Attr("borderFillIDRef", borderFill.Value);
        Slots("fontRef", faces);
        Slots("ratio", ratios);
        Slots("spacing", spacings);
        Slots("relSz", sizes);
        Slots("offset", offsets);
        if ((attributes & 1) != 0)
            Empty(Hh, "italic");
        if ((attributes & 2) != 0)
            Empty(Hh, "bold");
        Empty(Hh, "underline", ("type", ((attributes >> 2) & 3) switch { 1 => "BOTTOM", 2 => "CENTER", 3 => "TOP", _ => "NONE" }),
            ("shape", LineShape((int)((attributes >> 4) & 0xF))), ("color", Color(underline)));
        Empty(Hh, "strikeout", ("shape", ((attributes >> 18) & 7) == 0 ? "NONE" : LineShape((int)((attributes >> 26) & 0xF))), ("color", Color(strike)));
        Empty(Hh, "outline", ("type", ((attributes >> 8) & 7) switch
        {
            1 => "SOLID",
            2 => "DOT",
            3 => "THICK",
            4 => "DASH",
            5 => "DASH_DOT",
            6 => "DASH_DOT_DOT",
            _ => "NONE",
        }));
        Empty(Hh, "shadow", ("type", ((attributes >> 11) & 3) switch { 1 => "DROP", 2 => "CONTINUOUS", _ => "NONE" }),
            ("color", Color(shadow)), ("offsetX", shadowX), ("offsetY", shadowY));
        if ((attributes & (1u << 13)) != 0)
            Empty(Hh, "emboss");
        if ((attributes & (1u << 14)) != 0)
            Empty(Hh, "engrave");
        if ((attributes & (1u << 15)) != 0)
            Empty(Hh, "supscript");
        if ((attributes & (1u << 16)) != 0)
            Empty(Hh, "subscript");
        End();
    }

    /// <summary>One value per language slot (hangul, latin, ...).</summary>
    private void Slots(string name, ReadOnlySpan<int> values)
    {
        Start(Hh, name);
        for (var i = 0; i < Languages.Length; i++)
            Attr(Languages[i].ToLowerInvariant(), values[i]);
        End();
    }

    // ───────────────────────── Tab definitions ─────────────────────────

    private void TabDef(HwpRecord record, int id)
    {
        var r = record.Reader();
        var attributes = r.U32();
        var count = Math.Clamp(r.I32(), 0, r.Remaining / 8);
        var stops = new List<(int Position, int Type, int Leader)>();
        for (var i = 0; i < count; i++)
        {
            var position = r.I32();
            var type = r.U8();
            var leader = r.U8();
            r.Skip(2);
            stops.Add((position, type, leader));
        }

        Start(Hh, "tabPr");
        Attr("id", id);
        Attr("autoTabLeft", (attributes & 1) != 0);
        Attr("autoTabRight", (attributes & 2) != 0);
        // One switch per tab stop, as 한글 writes them.
        foreach (var stop in stops)
        {
            Start(Hp, "switch");
            Start(Hp, "case");
            _x.WriteAttributeString("hp", "required-namespace", Hp, HwpUnitChar);
            TabItem(stop, exact: true);
            End();
            Start(Hp, "default");
            TabItem(stop, exact: false);
            End();
            End();
        }
        End();
    }

    private void TabItem((int Position, int Type, int Leader) stop, bool exact)
    {
        Start(Hh, "tabItem");
        Attr("pos", exact ? Half(stop.Position) : stop.Position);
        Attr("type", stop.Type switch { 1 => "RIGHT", 2 => "CENTER", 3 => "DECIMAL", _ => "LEFT" });
        Attr("leader", LineType(stop.Leader));
        if (exact)
            Attr("unit", "HWPUNIT");
        End();
    }

    /// <summary>A length HWP stores at twice its size.</summary>
    private static int Half(int value) => value / 2;

    // ───────────────────────── Numbering and bullets ─────────────────────────

    /// <summary>Paragraph head information: alignment, width and distance settings and the marker's character shape.</summary>
    private readonly record struct ParaHead(uint Attributes, int WidthAdjust, int TextOffset, uint CharShape)
    {
        public static ParaHead Read(ref RecordReader r) => new(r.U32(), r.I16(), r.I16(), r.U32());
    }

    private void Numbering(HwpRecord record, int id)
    {
        var r = record.Reader();
        var heads = new List<(ParaHead Head, string Format)>();
        for (var i = 0; i < 7; i++)
        {
            var head = ParaHead.Read(ref r);
            heads.Add((head, r.Text()));
        }
        var start = r.U16();
        var starts = new List<uint>();
        if (r.Remaining >= 28)
        {
            for (var i = 0; i < 7; i++)
                starts.Add(r.U32());
        }
        // Levels 8 to 10 (5.1 and later).
        var extended = new List<(ParaHead Head, string Format)>();
        for (var i = 0; i < 3 && r.Remaining >= 10; i++)
        {
            var head = ParaHead.Read(ref r);
            extended.Add((head, r.Text()));
        }
        for (var i = 0; i < 3 && r.Remaining >= 4; i++)
            starts.Add(r.U32());
        heads.AddRange(extended);
        // Older files have seven levels; 한글 gives them the three newer ones with its defaults.
        while (heads.Count < 10)
            heads.Add((default, ""));

        Start(Hh, "numbering");
        Attr("id", id);
        Attr("start", start);
        for (var level = 0; level < heads.Count; level++)
        {
            var levelStart = level < starts.Count && starts[level] > 0 ? starts[level] : 1;
            WriteParaHead(heads[level].Head, level + 1, levelStart, heads[level].Format);
        }
        End();
    }

    private void WriteParaHead(ParaHead head, int level, long start, string? text)
    {
        Start(Hh, "paraHead");
        Attr("start", start);
        Attr("level", level);
        Attr("align", (head.Attributes & 3) switch { 1 => "CENTER", 2 => "RIGHT", _ => "LEFT" });
        Attr("useInstWidth", (head.Attributes & 4) != 0);
        Attr("autoIndent", (head.Attributes & 8) != 0);
        Attr("widthAdjust", head.WidthAdjust);
        Attr("textOffsetType", (head.Attributes & 0x10) != 0 ? "HWPUNIT" : "PERCENT");
        Attr("textOffset", head.TextOffset);
        Attr("numFormat", NumberFormat((int)((head.Attributes >> 5) & 0xF)));
        Attr("charPrIDRef", head.CharShape);
        Attr("checkable", false);
        if (!string.IsNullOrEmpty(text))
            _x.WriteString(Clean(text));
        End();
    }

    private void Bullet(HwpRecord record, int id)
    {
        var r = record.Reader();
        var head = ParaHead.Read(ref r);
        var symbol = r.U16();
        var image = r.I32();
        var (contrast, bright, effect, bin) = (r.I8(), r.I8(), r.U8(), r.U8());
        var check = r.U16();

        Start(Hh, "bullet");
        Attr("id", id);
        Attr("char", Char(symbol));
        if (check != 0)
            Attr("checkedChar", Char(check));
        Attr("useImage", image != 0);
        if (image != 0)
            Image(bin, bright, contrast, effect);
        WriteParaHead(head, 0, 1, null);
        End();
    }

    // ───────────────────────── Paragraph shapes ─────────────────────────

    private void ParaShape(HwpRecord record, int id)
    {
        var r = record.Reader();
        var attributes = r.U32();
        var (left, right, indent, before, after, oldSpacing) = (r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32());
        var (tabs, heading, borderFill) = (r.U16(), r.U16(), r.U16());
        var (borderLeft, borderRight, borderTop, borderBottom) = (r.I16(), r.I16(), r.I16(), r.I16());
        var attributes2 = r.U32();
        var hasSpacing = r.Remaining >= 8;
        var attributes3 = r.U32();
        var spacing = r.U32();
        var level = (int)((attributes >> 25) & 7);
        if (r.Remaining >= 4)
            level = Math.Clamp(r.I32(), 0, 9); // the level of outlines with up to ten levels (5.1 and later)

        // The newer type (attributes 3) wins; files that set only the older one (rhwp's) keep that.
        var (spacingType, spacingValue) = hasSpacing
            ? ((int)(attributes3 & 0x1F) is var type && type == 0 ? (int)(attributes & 3) : type, (int)spacing)
            : ((int)(attributes & 3), oldSpacing);

        Start(Hh, "paraPr");
        Attr("id", id);
        Attr("tabPrIDRef", tabs);
        Attr("condense", (attributes >> 9) & 0x7F);
        Attr("fontLineHeight", (attributes & (1u << 22)) != 0);
        Attr("snapToGrid", (attributes & (1u << 8)) != 0);
        Attr("suppressLineNumbers", false);
        Attr("checked", false);
        Empty(Hh, "align", ("horizontal", ((attributes >> 2) & 7) switch
        {
            1 => "LEFT",
            2 => "RIGHT",
            3 => "CENTER",
            4 => "DISTRIBUTE",
            5 => "DISTRIBUTE_SPACE",
            _ => "JUSTIFY",
        }), ("vertical", ((attributes >> 20) & 3) switch { 1 => "TOP", 2 => "CENTER", 3 => "BOTTOM", _ => "BASELINE" }));
        Empty(Hh, "heading", ("type", ((attributes >> 23) & 3) switch { 1 => "OUTLINE", 2 => "NUMBER", 3 => "BULLET", _ => "NONE" }),
            ("idRef", heading), ("level", level));
        Empty(Hh, "breakSetting",
            ("breakLatinWord", ((attributes >> 5) & 3) switch { 1 => "HYPHENATION", 2 => "BREAK_WORD", _ => "KEEP_WORD" }),
            // Bit 7 set (글자 in Hancom's document) is what 한글 writes as KEEP_WORD (see HwpxReader.Header).
            ("breakNonLatinWord", (attributes & 0x80) != 0 ? "KEEP_WORD" : "BREAK_WORD"),
            ("widowOrphan", (attributes & (1u << 16)) != 0), ("keepWithNext", (attributes & (1u << 17)) != 0),
            ("keepLines", (attributes & (1u << 18)) != 0), ("pageBreakBefore", (attributes & (1u << 19)) != 0),
            ("lineWrap", (attributes2 & 3) switch { 1 => "SQUEEZE", 2 => "KEEP", _ => "BREAK" }));
        Empty(Hh, "autoSpacing", ("eAsianEng", (attributes2 & 0x10) != 0), ("eAsianNum", (attributes2 & 0x20) != 0));

        Start(Hp, "switch");
        Start(Hp, "case");
        _x.WriteAttributeString("hp", "required-namespace", Hp, HwpUnitChar);
        Margins(indent, left, right, before, after, spacingType, spacingValue, exact: true);
        End();
        Start(Hp, "default");
        Margins(indent, left, right, before, after, spacingType, spacingValue, exact: false);
        End();
        End();

        Empty(Hh, "border", ("borderFillIDRef", borderFill), ("offsetLeft", borderLeft), ("offsetRight", borderRight),
            ("offsetTop", borderTop), ("offsetBottom", borderBottom),
            ("connect", (attributes & (1u << 28)) != 0), ("ignoreMargin", (attributes & (1u << 29)) != 0));
        End();
    }

    private void Margins(int indent, int left, int right, int before, int after, int spacingType, int spacing, bool exact)
    {
        int Value(int stored) => exact ? Half(stored) : stored;
        Start(Hh, "margin");
        foreach (var (name, value) in (ReadOnlySpan<(string, int)>)[("intent", indent), ("left", left), ("right", right), ("prev", before), ("next", after)])
            Empty(Hc, name, ("value", Value(value)), ("unit", "HWPUNIT"));
        End();
        Empty(Hh, "lineSpacing",
            ("type", spacingType switch { 1 => "FIXED", 2 => "BETWEEN_LINES", 3 => "AT_LEAST", _ => "PERCENT" }),
            ("value", spacingType == 0 ? spacing : Value(spacing)), ("unit", "HWPUNIT"));
    }

    // ───────────────────────── Styles ─────────────────────────

    private void Style(HwpRecord record, int id)
    {
        var r = record.Reader();
        var name = r.Text();
        var english = r.Text();
        var attributes = r.U8();
        var next = r.U8();
        var language = r.I16();
        var (paraShape, charShape) = (r.U16(), r.U16());
        Empty(Hh, "style", ("id", id), ("type", (attributes & 7) == 1 ? "CHAR" : "PARA"), ("name", name), ("engName", english),
            ("paraPrIDRef", paraShape), ("charPrIDRef", charShape), ("nextStyleIDRef", next),
            ("langID", language.ToString(CultureInfo.InvariantCulture)), ("lockForm", false));
    }

    /// <summary>The bytes of a record that may be missing (an empty reader then: every field reads as zero).</summary>
    private static RecordReader ReaderOf(HwpRecord? record) => record is null ? new RecordReader([]) : record.Reader();
}
