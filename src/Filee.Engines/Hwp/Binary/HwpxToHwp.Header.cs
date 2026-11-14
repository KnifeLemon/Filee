// header.xml → DocInfo, the inverse of HwpToHwpx.Header. Records are numbered by their place: fonts (per language),
// character and paragraph shapes, tab definitions and styles from 0, border fills, numberings and bullets from 1. The
// header's ids are mapped onto those places (in id order), so a package whose ids have gaps still refers to the right
// item, and an id that names nothing becomes the first item.
//
// Paragraph margins, spacing and tab positions are stored at twice their size in HWP: the hp:case value doubled, or the
// hp:default value as it is (한글 writes the stored value there).

using System.Globalization;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Binary;

internal sealed partial class HwpxToHwp
{
    private static readonly string[] Languages = ["HANGUL", "LATIN", "HANJA", "JAPANESE", "OTHER", "SYMBOL", "USER"];

    /// <summary>An item kind of header.xml: its elements in record order and their ids mapped to the record's number.</summary>
    private sealed class ItemList(int first)
    {
        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);

        public List<XElement> Elements { get; } = [];

        /// <summary>The record number of an id; the first record when the id names nothing.</summary>
        public int this[string? id] => id is not null && _ids.TryGetValue(id, out var index) ? index : first;

        public bool Contains(string? id) => id is not null && _ids.ContainsKey(id);

        public void Load(IEnumerable<XElement> elements)
        {
            var sorted = elements
                .Select((e, i) => (Element: e, Order: i, Id: long.TryParse(Attr(e, "id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : long.MaxValue))
                .OrderBy(x => x.Id)
                .ThenBy(x => x.Order)
                .ToList();
            foreach (var (element, _, _) in sorted)
            {
                if (Attr(element, "id") is { } id && !_ids.ContainsKey(id))
                    _ids[id] = Elements.Count + first;
                Elements.Add(element);
            }
        }
    }

    private readonly ItemList[] _fonts = [.. Languages.Select(_ => new ItemList(0))];
    private readonly ItemList _borderFills = new(1);
    private readonly ItemList _charShapes = new(0);
    private readonly ItemList _tabDefs = new(0);
    private readonly ItemList _numberings = new(1);
    private readonly ItemList _bullets = new(1);
    private readonly ItemList _paraShapes = new(0);
    private readonly ItemList _styles = new(0);
    private readonly ItemList _memoShapes = new(1);

    private void LoadHeader(XElement head)
    {
        var refList = Child(head, "refList");
        foreach (var fontface in Descendants(Child(refList, "fontfaces"), "fontface"))
        {
            var language = Array.IndexOf(Languages, Attr(fontface, "lang") ?? "");
            if (language >= 0 && _fonts[language].Elements.Count == 0)
                _fonts[language].Load(Children(fontface, "font"));
        }
        // 한글 does not open a document with a language that has no fonts (character shapes name a font for each):
        // such a language gets the fonts of the first one that has some.
        if (_fonts.FirstOrDefault(f => f.Elements.Count > 0) is { } fallback)
        {
            foreach (var fonts in _fonts.Where(f => f.Elements.Count == 0))
                fonts.Load(fallback.Elements);
        }
        _borderFills.Load(Children(Child(refList, "borderFills"), "borderFill"));
        _charShapes.Load(Children(Child(refList, "charProperties"), "charPr"));
        _tabDefs.Load(Children(Child(refList, "tabProperties"), "tabPr"));
        _numberings.Load(Children(Child(refList, "numberings"), "numbering"));
        _bullets.Load(Children(Child(refList, "bullets"), "bullet"));
        _paraShapes.Load(Children(Child(refList, "paraProperties"), "paraPr"));
        _styles.Load(Children(Child(refList, "styles"), "style"));
        _memoShapes.Load(Children(Child(refList, "memoProperties"), "memoPr"));
    }

    private byte[] DocInfo(XElement head, int sections)
    {
        // The items first: picture fills and bullets register the binaries the BIN_DATA records list.
        var items = new List<(int Tag, RecordData Data)>();
        foreach (var fonts in _fonts)
            items.AddRange(fonts.Elements.Select(font => (HwpTag.FaceName, Font(font))));
        items.AddRange(_borderFills.Elements.Select(fill => (HwpTag.BorderFill, BorderFill(fill))));
        items.AddRange(_charShapes.Elements.Select(shape => (HwpTag.CharShape, CharShape(shape))));
        items.AddRange(_tabDefs.Elements.Select(tabs => (HwpTag.TabDef, TabDef(tabs))));
        items.AddRange(_numberings.Elements.Select(numbering => (HwpTag.Numbering, Numbering(numbering))));
        items.AddRange(_bullets.Elements.Select(bullet => (HwpTag.Bullet, Bullet(bullet))));
        items.AddRange(_paraShapes.Elements.Select(shape => (HwpTag.ParaShape, ParaShape(shape))));
        items.AddRange(_styles.Elements.Select(style => (HwpTag.Style, Style(style))));
        items.AddRange(_memoShapes.Elements.Select(memo => (MemoShapeTag, MemoShape(memo))));

        var records = new RecordStream();
        var begin = Child(head, "beginNum");
        records.Add(HwpTag.DocumentProperties, 0, new RecordData().U16(sections)
            .U16(Int(begin, "page", 1)).U16(Int(begin, "footnote", 1)).U16(Int(begin, "endnote", 1))
            .U16(Int(begin, "pic", 1)).U16(Int(begin, "tbl", 1)).U16(Int(begin, "equation", 1))
            .U32(0).U32(0).U32(0));

        var mappings = new RecordData().I32(_bins.Count);
        foreach (var fonts in _fonts)
            mappings.I32(fonts.Elements.Count);
        foreach (var list in (ItemList[])[_borderFills, _charShapes, _tabDefs, _numberings, _bullets, _paraShapes, _styles, _memoShapes])
            mappings.I32(list.Elements.Count);
        mappings.I32(0).I32(0); // change tracking: none
        records.Add(HwpTag.IdMappings, 0, mappings);

        for (var i = 0; i < _bins.Count; i++)
        {
            var bin = _bins[i];
            // Embedded (1) or an OLE storage (2), compressed as the document says (bits 4-5 left 0).
            var data = new RecordData().U16(bin.Ole ? 2 : 1).U16(i + 1).Text(bin.Extension);
            records.Add(HwpTag.BinData, 1, data);
        }
        foreach (var (tag, data) in items)
            records.Add(tag, 1, data);

        var compatible = Child(head, "compatibleDocument");
        records.Add(HwpTag.CompatibleDocument, 0, new RecordData().U32(Attr(compatible, "targetProgram") switch
        {
            "HWP200X" => 1u,
            "MS_WORD" => 2u,
            _ => 0u,
        }));
        records.Add(LayoutCompatibilityTag, 1, new RecordData().Zeros(20));
        if ((Child(head, "trackchageConfig") ?? Child(head, "trackChangeConfig")) is { } track)
            records.Add(TrackChangeTag, 1, new RecordData().U32(UInt(track, "flags")).Zeros(1028));
        return records.ToArray();
    }

    private const int LayoutCompatibilityTag = 31;
    private const int TrackChangeTag = 32;
    private const int MemoShapeTag = 92;

    // ───────────────────────── Fonts ─────────────────────────

    private static RecordData Font(XElement font)
    {
        var subst = Child(font, "substFont");
        var type = Child(font, "typeInfo");
        var attributes = Attr(font, "type") switch { "TTF" => 1, "HFT" => 2, _ => 0 };
        if (subst is not null && (Attr(subst, "face") ?? "").Length > 0)
            attributes |= 0x80;
        if (type is not null)
            attributes |= 0x40;
        var data = new RecordData().U8(attributes).Text(Attr(font, "face"));
        if ((attributes & 0x80) != 0)
            data.U8(Attr(subst, "type") == "HFT" ? 2 : 1).Text(Attr(subst, "face"));
        if (type is not null)
        {
            data.U8(Attr(type, "familyType") switch
            {
                "FCAT_MYUNGJO" => 1,
                "FCAT_GOTHIC" => 2,
                "FCAT_SSERIF" => 3,
                "FCAT_BRUSHSCRIPT" => 4,
                "FCAT_DECORATIVE" => 5,
                "FCAT_NONRECTMJ" => 6,
                "FCAT_NONRECTGT" => 7,
                _ => 0,
            }).U8(0);
            foreach (var name in (string[])["weight", "proportion", "contrast", "strokeVariation", "armStyle", "letterform", "midline", "xHeight"])
                data.U8(Int(type, name));
        }
        return data;
    }

    // ───────────────────────── Border fills ─────────────────────────

    private RecordData BorderFill(XElement fill)
    {
        var slash = Child(fill, "slash");
        var backSlash = Child(fill, "backSlash");
        var attributes = (Flag(fill, "threeD") ? 1 : 0) | (Flag(fill, "shadow") ? 2 : 0)
                         | Diagonal(Attr(slash, "type")) << 2 | Diagonal(Attr(backSlash, "type")) << 5
                         | (Flag(slash, "Crooked") ? 0x100 : 0) | (Flag(backSlash, "Crooked") ? 0x400 : 0)
                         | (Flag(slash, "isCounter") ? 0x1000 : 0) | (Flag(backSlash, "isCounter") ? 0x2000 : 0);
        var data = new RecordData().U16(attributes);
        foreach (var side in (string[])["leftBorder", "rightBorder", "topBorder", "bottomBorder"])
        {
            var edge = Child(fill, side);
            data.U8(LineType(Attr(edge, "type"))).U8(BorderWidth(Attr(edge, "width"))).U32(Color(edge, "color", 0));
        }
        // The diagonal counts its line types without "none" and its widths one up (한글 writes SOLID 0.1 mm as 0, 1).
        var diagonal = Child(fill, "diagonal");
        data.U8(LineShape(Attr(diagonal, "type") ?? "SOLID")).U8(Math.Min(15, BorderWidth(Attr(diagonal, "width")) + 1)).U32(Color(diagonal, "color", 0));
        FillBrush(data, Child(fill, "fillBrush"));
        return data;
    }

    private static int Diagonal(string? type) => type switch
    {
        "CENTER" => 2,
        "CENTER_BELOW" => 3,
        "CENTER_ABOVE" => 6,
        "ALL" => 7,
        _ => 0,
    };

    /// <summary>
    /// Fill information (border fills and drawn shapes): which fills there are, a solid colour with its hatch, a
    /// gradation, a picture, the extra properties (the gradation's step centre) and the alpha of each fill.
    /// </summary>
    private void FillBrush(RecordData data, XElement? brush)
    {
        var solid = Child(brush, "winBrush");
        var gradation = Child(brush, "gradation");
        var image = Child(brush, "imgBrush");
        var type = (solid is not null ? 1u : 0) | (image is not null ? 2u : 0) | (gradation is not null ? 4u : 0);
        data.U32(type);
        if (type == 0)
        {
            data.U32(0); // no extra properties
            return;
        }
        if (solid is not null)
        {
            data.U32(Color(solid, "faceColor")).U32(Color(solid, "hatchColor", 0)).I32(Attr(solid, "hatchStyle") switch
            {
                "HORIZONTAL" => 1,
                "VERTICAL" => 2,
                "BACK_SLASH" => 3,
                "SLASH" => 4,
                "CROSS" => 5,
                "CROSS_DIAGONAL" => 6,
                _ => -1,
            });
        }
        if (gradation is not null)
        {
            var colors = Children(gradation, "color").Take(256).Select(c => Color(c, "value", 0)).ToList();
            data.U8(Attr(gradation, "type") switch { "RADIAL" => 2, "CONICAL" => 3, "SQUARE" => 4, _ => 1 })
                .I32(Int(gradation, "angle")).I32(Int(gradation, "centerX")).I32(Int(gradation, "centerY")).I32(Int(gradation, "step", 50))
                .I32(colors.Count);
            if (colors.Count > 2)
            {
                // Where each colour starts, evenly spread.
                for (var i = 0; i < colors.Count; i++)
                    data.I32(colors.Count == 1 ? 0 : i * 100 / (colors.Count - 1));
            }
            foreach (var color in colors)
                data.U32(color);
        }
        if (image is not null)
        {
            var img = Child(image, "img");
            data.U8(ImageMode(Attr(image, "mode"))).I8(Int(img, "bright")).I8(Int(img, "contrast")).U8(ImageEffect(Attr(img, "effect")))
                .U16(BinId(Attr(img, "binaryItemIDRef")));
        }
        if (gradation is not null)
            data.U32(1).U8(Int(gradation, "stepCenter", 50));
        else
            data.U32(0);
        // The alpha of each fill (opaque).
        for (var i = 0; i < System.Numerics.BitOperations.PopCount(type); i++)
            data.U8(0);
    }

    private static int ImageMode(string? mode) => mode switch
    {
        "TILE" => 0,
        "TILE_HORZ_TOP" => 1,
        "TILE_HORZ_BOTTOM" => 2,
        "TILE_VERT_LEFT" => 3,
        "TILE_VERT_RIGHT" => 4,
        "TOTAL" => 5,
        "CENTER" => 6,
        "CENTER_TOP" => 7,
        "CENTER_BOTTOM" => 8,
        "LEFT_CENTER" => 9,
        "LEFT_TOP" => 10,
        "LEFT_BOTTOM" => 11,
        "RIGHT_CENTER" => 12,
        "RIGHT_TOP" => 13,
        "RIGHT_BOTTOM" => 14,
        _ => 15, // ZOOM
    };

    private static int ImageEffect(string? effect) => effect switch { "GRAY_SCALE" => 1, "BLACK_WHITE" => 2, _ => 0 };

    // ───────────────────────── Character shapes ─────────────────────────

    private RecordData CharShape(XElement charPr)
    {
        var data = new RecordData();
        var fontRef = Child(charPr, "fontRef");
        for (var i = 0; i < Languages.Length; i++)
            data.U16(_fonts[i][Attr(fontRef, Languages[i].ToLowerInvariant())]);
        Slots(data, Child(charPr, "ratio"), 100, unsigned: true);
        Slots(data, Child(charPr, "spacing"), 0, unsigned: false);
        Slots(data, Child(charPr, "relSz"), 100, unsigned: true);
        Slots(data, Child(charPr, "offset"), 0, unsigned: false);

        var underline = Child(charPr, "underline");
        var strike = Child(charPr, "strikeout");
        var outline = Child(charPr, "outline");
        var shadow = Child(charPr, "shadow");
        var strikeShape = Attr(strike, "shape");
        var struck = strikeShape is not (null or "NONE" or "3D");
        var attributes = (Child(charPr, "italic") is not null ? 1u : 0) | (Child(charPr, "bold") is not null ? 2u : 0)
                         | (uint)(Attr(underline, "type") switch { "BOTTOM" => 1, "CENTER" => 2, "TOP" => 3, _ => 0 }) << 2
                         | (uint)LineShape(Attr(underline, "shape")) << 4
                         | (uint)(Attr(outline, "type") switch
                         {
                             "SOLID" => 1,
                             "DOT" => 2,
                             "THICK" => 3,
                             "DASH" => 4,
                             "DASH_DOT" => 5,
                             "DASH_DOT_DOT" => 6,
                             _ => 0,
                         }) << 8
                         | (uint)(Attr(shadow, "type") switch { "DROP" => 1, "CONTINUOUS" => 2, _ => 0 }) << 11
                         | (Child(charPr, "emboss") is not null ? 1u << 13 : 0) | (Child(charPr, "engrave") is not null ? 1u << 14 : 0)
                         | (Child(charPr, "supscript") is not null ? 1u << 15 : 0) | (Child(charPr, "subscript") is not null ? 1u << 16 : 0)
                         | (struck ? 1u << 18 : 0)
                         | (uint)SymbolMark(Attr(charPr, "symMark")) << 21
                         | (Flag(charPr, "useFontSpace") ? 1u << 25 : 0)
                         | (struck ? (uint)LineShape(strikeShape) << 26 : 0)
                         | (Flag(charPr, "useKerning") ? 1u << 30 : 0);
        data.I32(Int(charPr, "height", 1000)).U32(attributes)
            .I8(Int(shadow, "offsetX", 10)).I8(Int(shadow, "offsetY", 10))
            .U32(Color(charPr, "textColor", 0)).U32(Color(underline, "color", 0)).U32(Color(charPr, "shadeColor"))
            .U32(Color(shadow, "color", 0x00B2B2B2))
            .U16(_borderFills.Contains(Attr(charPr, "borderFillIDRef")) ? _borderFills[Attr(charPr, "borderFillIDRef")] : 0)
            .U32(Color(strike, "color", 0));
        return data;
    }

    /// <summary>One value per language slot (hangul, latin, ...).</summary>
    private static void Slots(RecordData data, XElement? element, int fallback, bool unsigned)
    {
        foreach (var language in Languages)
        {
            var value = Int(element, language.ToLowerInvariant(), fallback);
            if (unsigned)
                data.U8(Math.Clamp(value, 0, 255));
            else
                data.I8(value);
        }
    }

    private static int SymbolMark(string? mark) => mark switch
    {
        "DOT_ABOVE" => 1,
        "RING_ABOVE" => 2,
        "CARON" => 3,
        "TILDE" => 4,
        "SIDE" => 5,
        "COLON" => 6,
        "GRAVE_ACCENT" => 7,
        "ACUTE_ACCENT" => 8,
        "CIRCUMFLEX" => 9,
        "MACRON" => 10,
        "HOOK_ABOVE" => 11,
        "DOT_BELOW" => 12,
        _ => 0,
    };

    // ───────────────────────── Tab definitions ─────────────────────────

    private static RecordData TabDef(XElement tabPr)
    {
        var stops = new List<(int Position, int Type, int Leader)>();
        foreach (var child in tabPr.Elements())
        {
            IEnumerable<XElement> items;
            bool exact;
            if (child.Name.LocalName == "switch")
            {
                var (branch, branchExact) = Branch(child);
                (items, exact) = (branch?.Elements() ?? [], branchExact);
            }
            else if (child.Name.LocalName == "tabItem")
            {
                (items, exact) = ([child], Attr(child, "unit") == "HWPUNIT");
            }
            else
            {
                continue;
            }
            foreach (var item in items.Where(e => e.Name.LocalName == "tabItem"))
            {
                var position = Int(item, "pos");
                stops.Add((exact ? Doubled(position) : position, Attr(item, "type") switch
                {
                    "RIGHT" => 1,
                    "CENTER" => 2,
                    "DECIMAL" => 3,
                    _ => 0,
                }, LineType(Attr(item, "leader"))));
            }
        }
        var data = new RecordData().U32((Flag(tabPr, "autoTabLeft") ? 1u : 0) | (Flag(tabPr, "autoTabRight") ? 2u : 0)).I32(stops.Count);
        foreach (var (position, type, leader) in stops)
            data.I32(position).U8(type).U8(leader).U16(0);
        return data;
    }

    /// <summary>A length HWP stores at twice its size.</summary>
    private static int Doubled(int value) => (int)Math.Clamp(value * 2L, int.MinValue, int.MaxValue);

    // ───────────────────────── Numbering and bullets ─────────────────────────

    /// <summary>Paragraph head information: alignment, width and distance settings and the marker's character shape.</summary>
    private void ParaHead(RecordData data, XElement? head)
    {
        var attributes = (uint)(Attr(head, "align") switch { "CENTER" => 1, "RIGHT" => 2, _ => 0 })
                         | (Flag(head, "useInstWidth") ? 4u : 0) | (Flag(head, "autoIndent") ? 8u : 0)
                         | (Attr(head, "textOffsetType") == "HWPUNIT" ? 0x10u : 0)
                         | (uint)(NumberFormat(Attr(head, "numFormat")) & 0xF) << 5;
        var shape = Attr(head, "charPrIDRef");
        data.U32(attributes).I16(Int(head, "widthAdjust")).I16(Int(head, "textOffset"))
            .U32(shape is null || !_charShapes.Contains(shape) ? 0xFFFFFFFF : (uint)_charShapes[shape]);
    }

    private RecordData Numbering(XElement numbering)
    {
        var heads = new XElement?[10];
        foreach (var head in Children(numbering, "paraHead"))
        {
            var level = Int(head, "level", 1);
            if (level is >= 1 and <= 10)
                heads[level - 1] ??= head;
        }
        var data = new RecordData();
        for (var i = 0; i < 7; i++)
        {
            ParaHead(data, heads[i]);
            data.Text(heads[i]?.Value);
        }
        data.U16(Int(numbering, "start", 1));
        for (var i = 0; i < 7; i++)
            data.U32((uint)Math.Max(1, Int(heads[i], "start", 1)));
        for (var i = 7; i < 10; i++)
        {
            ParaHead(data, heads[i]);
            data.Text(heads[i]?.Value);
        }
        for (var i = 7; i < 10; i++)
            data.U32((uint)Math.Max(1, Int(heads[i], "start", 1)));
        return data;
    }

    private RecordData Bullet(XElement bullet)
    {
        var data = new RecordData();
        ParaHead(data, Child(bullet, "paraHead"));
        var image = Flag(bullet, "useImage");
        var img = Child(bullet, "img");
        data.U16(Char(Attr(bullet, "char"))).I32(image ? 1 : 0)
            .I8(Int(img, "contrast")).I8(Int(img, "bright")).U8(ImageEffect(Attr(img, "effect")))
            .U8(image ? Math.Min(255, BinId(Attr(img, "binaryItemIDRef"))) : 0)
            .U16(Char(Attr(bullet, "checkedChar")));
        return data;
    }

    // ───────────────────────── Paragraph shapes ─────────────────────────

    private RecordData ParaShape(XElement paraPr)
    {
        var align = Child(paraPr, "align");
        var heading = Child(paraPr, "heading");
        var breaks = Child(paraPr, "breakSetting");
        var autoSpacing = Child(paraPr, "autoSpacing");
        var border = Child(paraPr, "border");
        var (intent, left, right, before, after, spacingType, spacing) = Spacing(paraPr);

        var headingType = Attr(heading, "type") switch { "OUTLINE" => 1, "NUMBER" or "NUMBERING" => 2, "BULLET" => 3, _ => 0 };
        var level = Math.Clamp(Int(heading, "level"), 0, 9);
        var attributes = (uint)(spacingType is 1 or 2 ? spacingType : 0)
                         | (uint)(Attr(align, "horizontal") switch
                         {
                             "LEFT" => 1,
                             "RIGHT" => 2,
                             "CENTER" => 3,
                             "DISTRIBUTE" => 4,
                             "DISTRIBUTE_SPACE" => 5,
                             _ => 0,
                         }) << 2
                         | (uint)(Attr(breaks, "breakLatinWord") switch { "HYPHENATION" => 1, "BREAK_WORD" => 2, _ => 0 }) << 5
                         // KEEP_WORD is what 한글 writes for bit 7 set (see HwpToHwpx.ParaShape).
                         | (Attr(breaks, "breakNonLatinWord") == "KEEP_WORD" ? 0x80u : 0)
                         | (Flag(paraPr, "snapToGrid", true) ? 0x100u : 0)
                         | (uint)Math.Clamp(Int(paraPr, "condense"), 0, 0x7F) << 9
                         | (Flag(breaks, "widowOrphan") ? 1u << 16 : 0) | (Flag(breaks, "keepWithNext") ? 1u << 17 : 0)
                         | (Flag(breaks, "keepLines") ? 1u << 18 : 0) | (Flag(breaks, "pageBreakBefore") ? 1u << 19 : 0)
                         | (uint)(Attr(align, "vertical") switch { "TOP" => 1, "CENTER" => 2, "BOTTOM" => 3, _ => 0 }) << 20
                         | (Flag(paraPr, "fontLineHeight") ? 1u << 22 : 0)
                         | (uint)headingType << 23
                         | (uint)Math.Min(level, 6) << 25
                         | (Flag(border, "connect") ? 1u << 28 : 0) | (Flag(border, "ignoreMargin") ? 1u << 29 : 0);

        var headingId = headingType switch
        {
            3 => _bullets.Contains(Attr(heading, "idRef")) ? _bullets[Attr(heading, "idRef")] : 0,
            1 or 2 => _numberings.Contains(Attr(heading, "idRef")) ? _numberings[Attr(heading, "idRef")] : 0,
            _ => 0,
        };
        var attributes2 = (uint)(Attr(breaks, "lineWrap") switch { "SQUEEZE" => 1, "KEEP" => 2, _ => 0 })
                          | 0x8 // set in every paragraph shape 한글 writes
                          | (Flag(autoSpacing, "eAsianEng") ? 0x10u : 0) | (Flag(autoSpacing, "eAsianNum") ? 0x20u : 0);

        return new RecordData().U32(attributes)
            .I32(left).I32(right).I32(intent).I32(before).I32(after).I32(spacing)
            .U16(_tabDefs[Attr(paraPr, "tabPrIDRef")]).U16(headingId)
            .U16(_borderFills.Contains(Attr(border, "borderFillIDRef")) ? _borderFills[Attr(border, "borderFillIDRef")] : 0)
            .I16(Int(border, "offsetLeft")).I16(Int(border, "offsetRight")).I16(Int(border, "offsetTop")).I16(Int(border, "offsetBottom"))
            .U32(attributes2).U32((uint)spacingType).I32(spacing).I32(level);
    }

    /// <summary>
    /// Margins and line spacing as HWP stores them (lengths doubled): from the hp:case branch doubled, else as hp:default
    /// (or an older file without a switch) has them. Returns the spacing kind (0 percent, 1 fixed, 2 between lines, 3
    /// at least) and its value.
    /// </summary>
    private static (int Intent, int Left, int Right, int Before, int After, int SpacingType, int Spacing) Spacing(XElement paraPr)
    {
        XElement? margin = null, line = null;
        bool marginExact = false, lineExact = false;
        foreach (var child in paraPr.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "switch":
                    {
                        var (branch, exact) = Branch(child);
                        if (branch?.Elements().FirstOrDefault(e => e.Name.LocalName == "margin") is { } m)
                            (margin, marginExact) = (m, exact);
                        if (branch?.Elements().FirstOrDefault(e => e.Name.LocalName == "lineSpacing") is { } l)
                            (line, lineExact) = (l, exact);
                        break;
                    }
                case "margin" when margin is null:
                    margin = child;
                    break;
                case "lineSpacing" when line is null:
                    line = child;
                    break;
            }
        }

        int Length(string name, string attribute)
        {
            var element = margin?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
            var value = element is not null ? Int(element, "value") : Int(margin, attribute);
            return marginExact ? Doubled(value) : value;
        }

        var type = Attr(line, "type") switch
        {
            "FIXED" => 1,
            "BETWEEN_LINES" or "SPACE_ONLY" or "SPACEONLY" => 2,
            "AT_LEAST" or "MINIMUM" => 3,
            _ => 0,
        };
        var value = Int(line, "value", 160);
        var spacing = type == 0 ? value : lineExact ? Doubled(value) : value;
        return (Length("intent", "indent"), Length("left", "left"), Length("right", "right"), Length("prev", "prev"), Length("next", "next"), type, spacing);
    }

    // ───────────────────────── Styles ─────────────────────────

    private RecordData Style(XElement style) =>
        new RecordData().Text(Attr(style, "name")).Text(Attr(style, "engName"))
            .U8(Attr(style, "type") == "CHAR" ? 1 : 0).U8(_styles[Attr(style, "nextStyleIDRef")])
            .I16(Int(style, "langID", 1042)).U16(_paraShapes[Attr(style, "paraPrIDRef")]).U16(_charShapes[Attr(style, "charPrIDRef")])
            .U16(0);

    /// <summary>A memo shape (메모 모양): width, line and colours.</summary>
    private static RecordData MemoShape(XElement memo) =>
        new RecordData().U32(UInt(memo, "width", 15024)).U8(Int(memo, "lineWidth", 1)).U8(LineType(Attr(memo, "lineType") ?? "SOLID"))
            .U32(Color(memo, "lineColor", 0)).U32(Color(memo, "fillColor", 0x00FFFFFF)).U32(Color(memo, "activeColor", 0x00FFFFFF))
            .U32(Attr(memo, "memoType") == "USER_INSERT" ? 1u : 0);
}
