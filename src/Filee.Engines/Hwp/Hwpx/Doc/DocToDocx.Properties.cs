// DOC property modifiers (sprms) → WordprocessingML properties: paragraph (pPr), character (rPr) and section (sectPr),
// with the borders (Brc, Brc80), shading (Shd, Shd80) and colours (COLORREF, ico) they use. Later sprms of a grpprl
// win over earlier ones, as Word applies them; elements are written in the schema's order.

using System.Buffers.Binary;
using System.Globalization;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx.Doc;

internal sealed partial class DocToDocx
{
    /// <summary>Properties collected by element name and written in schema order.</summary>
    private sealed class Bag(XName root, string[] order)
    {
        private readonly Dictionary<string, XElement> _elements = [];

        /// <summary>The element <paramref name="name"/>, created empty when missing (attributes merge into it).</summary>
        public XElement Get(string name)
        {
            if (!_elements.TryGetValue(name, out var element))
                _elements[name] = element = new XElement(W + name);
            return element;
        }

        public void Set(string name, params object?[] content) => _elements[name] = new XElement(W + name, content);

        /// <summary>An on/off property: present for on, val="0" for off.</summary>
        public void Toggle(string name, bool on) => Set(name, on ? null : new XAttribute(W + "val", "0"));

        public void Value(string name, object value) => Set(name, new XAttribute(W + "val", Format(value)));

        public bool Has(string name) => _elements.ContainsKey(name);

        public XElement Build()
        {
            var element = new XElement(root);
            foreach (var name in order)
            {
                if (_elements.TryGetValue(name, out var child))
                    element.Add(child);
            }
            return element;
        }
    }

    private static readonly string[] PPrOrder =
    [
        "pStyle", "keepNext", "keepLines", "pageBreakBefore", "framePr", "widowControl", "numPr", "suppressLineNumbers",
        "pBdr", "shd", "tabs", "suppressAutoHyphens", "kinsoku", "wordWrap", "overflowPunct", "topLinePunct",
        "autoSpaceDE", "autoSpaceDN", "bidi", "adjustRightInd", "snapToGrid", "spacing", "ind", "contextualSpacing",
        "mirrorIndents", "suppressOverlap", "jc", "textDirection", "textAlignment", "textboxTightWrap", "outlineLvl",
    ];

    private static readonly string[] RPrOrder =
    [
        "rStyle", "rFonts", "b", "bCs", "i", "iCs", "caps", "smallCaps", "strike", "dstrike", "outline", "shadow",
        "emboss", "imprint", "noProof", "snapToGrid", "vanish", "webHidden", "color", "spacing", "w", "kern",
        "position", "sz", "szCs", "highlight", "u", "effect", "bdr", "shd", "fitText", "vertAlign", "rtl", "cs", "em",
        "lang",
    ];

    private static readonly string[] BorderOrder = ["top", "left", "bottom", "right", "between", "bar"];

    // ───────────────────────── Paragraphs ─────────────────────────

    /// <summary>The pPr of a paragraph of style <paramref name="istd"/> with direct formatting <paramref name="sprms"/>.</summary>
    private XElement ParagraphProperties(int istd, IEnumerable<Sprm> sprms)
    {
        var bag = new Bag(W + "pPr", PPrOrder);
        if (StyleId(istd, StyleKind.Paragraph) is { } style && istd != 0)
            bag.Value("pStyle", style);
        ApplyParagraph(bag, sprms);
        return bag.Build();
    }

    /// <summary>Applies paragraph sprms to <paramref name="bag"/>.</summary>
    private void ApplyParagraph(Bag bag, IEnumerable<Sprm> sprms)
    {
        foreach (var sprm in sprms)
        {
            switch (sprm.Op)
            {
                case Ops.PJc80 or Ops.PJc:
                    bag.Value("jc", sprm.Byte switch
                    {
                        1 => "center",
                        2 => "right",
                        3 => "both",
                        4 => "distribute",
                        5 => "mediumKashida",
                        7 => "highKashida",
                        8 => "lowKashida",
                        9 => "thaiDistribute",
                        _ => "left",
                    });
                    break;
                case Ops.PFKeep:
                    bag.Toggle("keepLines", sprm.Byte != 0);
                    break;
                case Ops.PFKeepFollow:
                    bag.Toggle("keepNext", sprm.Byte != 0);
                    break;
                case Ops.PFPageBreakBefore:
                    bag.Toggle("pageBreakBefore", sprm.Byte != 0);
                    break;
                case Ops.PFWidowControl:
                    bag.Toggle("widowControl", sprm.Byte != 0);
                    break;
                case Ops.PIlvl:
                    NumPr(bag, "ilvl", Math.Min((int)sprm.Byte, 8));
                    break;
                case Ops.PIlfo:
                    // 1-based LFO; 0 and the "no list" values (0xF801 and above) turn numbering off.
                    NumPr(bag, "numId", sprm.Short is > 0 and var lfo && lfo <= _lfos.Count ? lfo : 0);
                    break;
                case Ops.PChgTabsPapx or Ops.PChgTabs:
                    Tabs(bag, sprm);
                    break;
                case Ops.PDxaLeft or Ops.PDxaLeft80:
                    SetAttribute(bag.Get("ind"), "left", sprm.Short);
                    break;
                case Ops.PDxaRight or Ops.PDxaRight80:
                    SetAttribute(bag.Get("ind"), "right", sprm.Short);
                    break;
                case Ops.PDxaLeft1 or Ops.PDxaLeft180:
                    FirstLine(bag.Get("ind"), sprm.Short, "firstLine", "hanging");
                    break;
                case Ops.PDxcLeft:
                    SetAttribute(bag.Get("ind"), "leftChars", sprm.Short);
                    break;
                case Ops.PDxcRight:
                    SetAttribute(bag.Get("ind"), "rightChars", sprm.Short);
                    break;
                case Ops.PDxcLeft1:
                    FirstLine(bag.Get("ind"), sprm.Short, "firstLineChars", "hangingChars");
                    break;
                case Ops.PDyaLine when sprm.Operand.Length >= 4:
                    {
                        // LSPD: dyaLine, fMultLinespace. Multiple: 240 is single; otherwise at least, or exact when negative.
                        var line = BinaryPrimitives.ReadInt16LittleEndian(sprm.Operand);
                        var multiple = BinaryPrimitives.ReadInt16LittleEndian(sprm.Operand.AsSpan(2)) != 0;
                        var spacing = bag.Get("spacing");
                        SetAttribute(spacing, "line", multiple ? line : Math.Abs((int)line));
                        SetAttribute(spacing, "lineRule", multiple ? "auto" : line < 0 ? "exact" : "atLeast");
                        break;
                    }
                case Ops.PDyaBefore:
                    SetAttribute(bag.Get("spacing"), "before", sprm.UShort);
                    break;
                case Ops.PDyaAfter:
                    SetAttribute(bag.Get("spacing"), "after", sprm.UShort);
                    break;
                case Ops.PDyaBeforeAuto:
                    SetAttribute(bag.Get("spacing"), "beforeAutospacing", sprm.Byte != 0 ? "1" : "0");
                    break;
                case Ops.PDyaAfterAuto:
                    SetAttribute(bag.Get("spacing"), "afterAutospacing", sprm.Byte != 0 ? "1" : "0");
                    break;
                case Ops.PFKinsoku:
                    bag.Toggle("kinsoku", sprm.Byte != 0);
                    break;
                case Ops.PFWordWrap:
                    bag.Toggle("wordWrap", sprm.Byte != 0);
                    break;
                case Ops.PFOverflowPunct:
                    bag.Toggle("overflowPunct", sprm.Byte != 0);
                    break;
                case Ops.PFTopLinePunct:
                    bag.Toggle("topLinePunct", sprm.Byte != 0);
                    break;
                case Ops.PFAutoSpaceDE:
                    bag.Toggle("autoSpaceDE", sprm.Byte != 0);
                    break;
                case Ops.PFAutoSpaceDN:
                    bag.Toggle("autoSpaceDN", sprm.Byte != 0);
                    break;
                case Ops.PFUsePgsuSettings:
                    bag.Toggle("snapToGrid", sprm.Byte != 0);
                    break;
                case Ops.PFBiDi:
                    bag.Toggle("bidi", sprm.Byte != 0);
                    break;
                case Ops.PFContextualSpacing:
                    bag.Toggle("contextualSpacing", sprm.Byte != 0);
                    break;
                case Ops.POutLvl:
                    bag.Value("outlineLvl", Math.Min((int)sprm.Byte, 9));
                    break;
                case Ops.PBrcTop80 or Ops.PBrcTop:
                    ParagraphBorder(bag, "top", sprm);
                    break;
                case Ops.PBrcLeft80 or Ops.PBrcLeft:
                    ParagraphBorder(bag, "left", sprm);
                    break;
                case Ops.PBrcBottom80 or Ops.PBrcBottom:
                    ParagraphBorder(bag, "bottom", sprm);
                    break;
                case Ops.PBrcRight80 or Ops.PBrcRight:
                    ParagraphBorder(bag, "right", sprm);
                    break;
                case Ops.PBrcBetween80 or Ops.PBrcBetween:
                    ParagraphBorder(bag, "between", sprm);
                    break;
                case Ops.PShd80:
                    bag.Set("shd", Shd80(sprm.UShort));
                    break;
                case Ops.PShd when sprm.Operand.Length >= 10:
                    bag.Set("shd", Shd(sprm.Operand));
                    break;
            }
        }
    }

    private static void NumPr(Bag bag, string name, int value)
    {
        var numPr = bag.Get("numPr");
        numPr.Element(W + name)?.Remove();
        var element = new XElement(W + name, new XAttribute(W + "val", value));
        // ilvl comes before numId.
        if (name == "ilvl")
            numPr.AddFirst(element);
        else
            numPr.Add(element);
    }

    private static void FirstLine(XElement ind, int value, string firstLine, string hanging)
    {
        ind.Attribute(W + firstLine)?.Remove();
        ind.Attribute(W + hanging)?.Remove();
        if (value >= 0)
            SetAttribute(ind, firstLine, value);
        else
            SetAttribute(ind, hanging, -value);
    }

    private void ParagraphBorder(Bag bag, string side, Sprm sprm)
    {
        if (Border(side, sprm.Operand) is not { } border)
            return;
        var pBdr = bag.Get("pBdr");
        pBdr.Element(W + side)?.Remove();
        pBdr.Add(border);
        var sorted = pBdr.Elements().OrderBy(e => Array.IndexOf(BorderOrder, e.Name.LocalName)).ToList();
        pBdr.RemoveNodes();
        pBdr.Add(sorted);
    }

    /// <summary>
    /// sprmPChgTabsPapx: tab stops deleted (positions) and added (positions, then a TBD byte each: kind in bits 0–2,
    /// leader in bits 3–5) against the style's. sprmPChgTabs has a close distance after each deleted position.
    /// </summary>
    private static void Tabs(Bag bag, Sprm sprm)
    {
        var data = sprm.Operand;
        var at = 0;
        if (at >= data.Length)
            return;
        var deleted = data[at++];
        var stops = new List<(int Position, XElement Tab)>();
        for (var i = 0; i < deleted && at + 2 <= data.Length; i++, at += 2)
        {
            var position = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(at));
            stops.Add((position, new XElement(W + "tab", new XAttribute(W + "val", "clear"), new XAttribute(W + "pos", position))));
        }
        if (sprm.Op == Ops.PChgTabs)
            at += deleted * 2; // rgdxaClose
        if (at >= data.Length)
            return;
        var added = data[at++];
        var positions = at;
        var kinds = at + added * 2;
        for (var i = 0; i < added && positions + i * 2 + 2 <= data.Length && kinds + i < data.Length; i++)
        {
            var position = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(positions + i * 2));
            var tbd = data[kinds + i];
            var kind = (tbd & 7) switch
            {
                1 => "center",
                2 => "right",
                3 => "decimal",
                4 => "bar",
                6 => "num",
                _ => "left",
            };
            var leader = ((tbd >> 3) & 7) switch
            {
                1 => "dot",
                2 => "hyphen",
                3 => "underscore",
                4 => "heavy",
                5 => "middleDot",
                _ => "none",
            };
            stops.RemoveAll(s => s.Position == position);
            stops.Add((position, new XElement(W + "tab", new XAttribute(W + "val", kind), new XAttribute(W + "leader", leader), new XAttribute(W + "pos", position))));
        }
        var tabs = bag.Get("tabs");
        foreach (var (position, tab) in stops)
        {
            tabs.Elements(W + "tab").Where(t => (string?)t.Attribute(W + "pos") == Format(position)).Remove();
            tabs.Add(tab);
        }
        var sorted = tabs.Elements().OrderBy(t => int.Parse((string)t.Attribute(W + "pos")!, CultureInfo.InvariantCulture)).ToList();
        tabs.RemoveNodes();
        tabs.Add(sorted);
        if (!tabs.HasElements)
            tabs.Remove();
    }

    // ───────────────────────── Characters ─────────────────────────

    /// <summary>The rPr of a run with <paramref name="sprms"/> in a paragraph of style <paramref name="istd"/>.</summary>
    private XElement RunProperties(List<Sprm> sprms, int istd)
    {
        var bag = new Bag(W + "rPr", RPrOrder);
        // Toggles relative to the style (0x80 "as the style", 0x81 "the style's opposite") follow the paragraph's style
        // and the run's character style.
        var styles = new Dictionary<ushort, bool>(ToggleState(istd));
        if (sprms.LastOrDefault(s => s.Op == Ops.CIstd) is { Operand.Length: >= 2 } characterStyle)
        {
            foreach (var (op, value) in ToggleState(characterStyle.UShort))
                styles[op] = value;
        }
        ApplyCharacter(bag, sprms, styles, direct: true);
        return bag.Build();
    }

    /// <summary>The toggle sprms and their rPr elements.</summary>
    private static readonly Dictionary<ushort, string> Toggles = new()
    {
        [Ops.CFBold] = "b",
        [Ops.CFItalic] = "i",
        [Ops.CFStrike] = "strike",
        [Ops.CFOutline] = "outline",
        [Ops.CFShadow] = "shadow",
        [Ops.CFSmallCaps] = "smallCaps",
        [Ops.CFCaps] = "caps",
        [Ops.CFVanish] = "vanish",
        [Ops.CFImprint] = "imprint",
        [Ops.CFEmboss] = "emboss",
        [Ops.CFBoldBi] = "bCs",
        [Ops.CFItalicBi] = "iCs",
        [Ops.CFDStrike] = "dstrike",
        [Ops.CFNoProof] = "noProof",
    };

    /// <summary>Applies character sprms to <paramref name="bag"/>; <paramref name="styles"/> holds the toggles' values below.</summary>
    private void ApplyCharacter(Bag bag, IEnumerable<Sprm> sprms, IReadOnlyDictionary<ushort, bool> styles, bool direct)
    {
        foreach (var sprm in sprms)
        {
            if (Toggles.TryGetValue(sprm.Op, out var toggle))
            {
                var below = styles.GetValueOrDefault(sprm.Op);
                switch (sprm.Byte)
                {
                    case 0:
                        bag.Toggle(toggle, false);
                        break;
                    case 1:
                        bag.Toggle(toggle, true);
                        break;
                    case 0x81:
                        bag.Toggle(toggle, !below);
                        break;
                    case 0x80 when !direct:
                        bag.Toggle(toggle, below);
                        break;
                }
                continue;
            }
            switch (sprm.Op)
            {
                case Ops.CIstd when direct:
                    if (StyleId(sprm.UShort, StyleKind.Character) is { } style && _styles[sprm.UShort].Sti != 65) // not Default Paragraph Font
                        bag.Value("rStyle", style);
                    break;
                case Ops.CRgFtc0:
                    Font(bag, "ascii", sprm.UShort);
                    break;
                case Ops.CRgFtc1:
                    Font(bag, "eastAsia", sprm.UShort);
                    break;
                case Ops.CRgFtc2:
                    Font(bag, "hAnsi", sprm.UShort);
                    break;
                case Ops.CFtcBi:
                    Font(bag, "cs", sprm.UShort);
                    break;
                case Ops.CKul:
                    bag.Value("u", sprm.Byte switch
                    {
                        0 => "none",
                        2 => "words",
                        3 => "double",
                        4 => "dotted",
                        6 => "thick",
                        7 => "dash",
                        9 => "dotDash",
                        10 => "dotDotDash",
                        11 => "wave",
                        20 => "dottedHeavy",
                        23 => "dashedHeavy",
                        25 => "dashDotHeavy",
                        26 => "dashDotDotHeavy",
                        27 => "wavyHeavy",
                        39 => "dashLong",
                        43 => "wavyDouble",
                        55 => "dashLongHeavy",
                        _ => "single",
                    });
                    break;
                case Ops.CDxaSpace:
                    bag.Value("spacing", sprm.Short);
                    break;
                case Ops.CIco:
                    bag.Value("color", Ico(sprm.Byte) ?? "auto");
                    break;
                case Ops.CCv when sprm.Operand.Length >= 4:
                    bag.Value("color", ColorRef(sprm.Operand) ?? "auto");
                    break;
                case Ops.CHps:
                    bag.Value("sz", Math.Clamp((int)sprm.UShort, 2, 3276));
                    break;
                case Ops.CHpsBi:
                    bag.Value("szCs", Math.Clamp((int)sprm.UShort, 2, 3276));
                    break;
                case Ops.CHpsPos:
                    bag.Value("position", sprm.Short);
                    break;
                case Ops.CIss:
                    bag.Value("vertAlign", sprm.Byte switch
                    {
                        1 => "superscript",
                        2 => "subscript",
                        _ => "baseline",
                    });
                    break;
                case Ops.CCharScale:
                    bag.Value("w", Math.Clamp((int)sprm.UShort, 1, 600));
                    break;
                case Ops.CHighlight:
                    bag.Value("highlight", HighlightName(sprm.Byte));
                    break;
                case Ops.CShd80:
                    bag.Set("shd", Shd80(sprm.UShort));
                    break;
                case Ops.CShd when sprm.Operand.Length >= 10:
                    bag.Set("shd", Shd(sprm.Operand));
                    break;
            }
        }
    }

    private void Font(Bag bag, string slot, int ftc)
    {
        if (FontName(ftc) is { } name)
            SetAttribute(bag.Get("rFonts"), slot, name);
    }

    // ───────────────────────── Sections ─────────────────────────

    private XElement SectionProperties(Section section)
    {
        // Word's defaults for what a SEPX leaves out: Letter paper, 1.25" left and right margins, 1" top and bottom.
        int xaPage = 12240, yaPage = 15840, left = 1800, right = 1800, top = 1440, bottom = 1440, header = 720, footer = 720, gutter = 0;
        int columns = 1, columnSpace = 720, bkc = 2, clm = 0, linePitch = 0, textFlow = 0;
        int? pageStart = null;
        bool restart = false, titlePage = false, separator = false, landscape = false, evenly = true;
        var widths = new Dictionary<int, int>();
        var spaces = new Dictionary<int, int>();
        foreach (var sprm in section.Sprms)
        {
            switch (sprm.Op)
            {
                case Ops.SXaPage:
                    xaPage = sprm.UShort;
                    break;
                case Ops.SYaPage:
                    yaPage = sprm.UShort;
                    break;
                case Ops.SDxaLeft:
                    left = sprm.UShort;
                    break;
                case Ops.SDxaRight:
                    right = sprm.UShort;
                    break;
                case Ops.SDyaTop:
                    top = sprm.Short;
                    break;
                case Ops.SDyaBottom:
                    bottom = sprm.Short;
                    break;
                case Ops.SDyaHdrTop:
                    header = sprm.UShort;
                    break;
                case Ops.SDyaHdrBottom:
                    footer = sprm.UShort;
                    break;
                case Ops.SDzaGutter:
                    gutter = sprm.UShort;
                    break;
                case Ops.SBOrientation:
                    landscape = sprm.Byte == 2;
                    break;
                case Ops.SCcolumns:
                    columns = Math.Clamp(sprm.UShort + 1, 1, 45);
                    break;
                case Ops.SDxaColumns:
                    columnSpace = sprm.UShort;
                    break;
                case Ops.SFEvenlySpaced:
                    evenly = sprm.Byte != 0;
                    break;
                case Ops.SDxaColWidth when sprm.Operand.Length >= 3:
                    widths[sprm.Operand[0]] = BinaryPrimitives.ReadUInt16LittleEndian(sprm.Operand.AsSpan(1));
                    break;
                case Ops.SDxaColSpacing when sprm.Operand.Length >= 3:
                    spaces[sprm.Operand[0]] = BinaryPrimitives.ReadUInt16LittleEndian(sprm.Operand.AsSpan(1));
                    break;
                case Ops.SLBetween:
                    separator = sprm.Byte != 0;
                    break;
                case Ops.SBkc:
                    bkc = sprm.Byte;
                    break;
                case Ops.SFTitlePage:
                    titlePage = sprm.Byte != 0;
                    break;
                case Ops.SFPgnRestart:
                    restart = sprm.Byte != 0;
                    break;
                case Ops.SPgnStart97:
                    pageStart = sprm.UShort;
                    break;
                case Ops.SPgnStart:
                    pageStart = sprm.Int;
                    break;
                case Ops.SClm:
                    clm = sprm.UShort;
                    break;
                case Ops.SDyaLinePitch:
                    linePitch = sprm.Short;
                    break;
                case Ops.STextFlow:
                    textFlow = sprm.UShort;
                    break;
            }
        }

        var sectPr = new XElement(W + "sectPr", HeaderReferences(section.Index));
        if (bkc != 2)
        {
            sectPr.Add(new XElement(W + "type", new XAttribute(W + "val", bkc switch
            {
                0 => "continuous",
                1 => "nextColumn",
                3 => "evenPage",
                4 => "oddPage",
                _ => "nextPage",
            })));
        }
        sectPr.Add(new XElement(W + "pgSz", new XAttribute(W + "w", xaPage), new XAttribute(W + "h", yaPage),
            landscape ? new XAttribute(W + "orient", "landscape") : null));
        sectPr.Add(new XElement(W + "pgMar", new XAttribute(W + "top", top), new XAttribute(W + "right", right),
            new XAttribute(W + "bottom", bottom), new XAttribute(W + "left", left), new XAttribute(W + "header", header),
            new XAttribute(W + "footer", footer), new XAttribute(W + "gutter", gutter)));
        if (restart)
            sectPr.Add(new XElement(W + "pgNumType", new XAttribute(W + "start", Math.Max(0, pageStart ?? 1))));
        var cols = new XElement(W + "cols", new XAttribute(W + "space", columnSpace));
        if (columns > 1)
        {
            cols.Add(new XAttribute(W + "num", columns));
            if (separator)
                cols.Add(new XAttribute(W + "sep", "1"));
            if (!evenly && widths.Count > 0)
            {
                cols.Add(new XAttribute(W + "equalWidth", "0"));
                for (var i = 0; i < columns; i++)
                    cols.Add(new XElement(W + "col", new XAttribute(W + "w", widths.GetValueOrDefault(i, 0)), new XAttribute(W + "space", spaces.GetValueOrDefault(i, 0))));
            }
        }
        sectPr.Add(cols);
        if (titlePage)
            sectPr.Add(new XElement(W + "titlePg"));
        if (textFlow is 1 or 3 or 5)
            sectPr.Add(new XElement(W + "textDirection", new XAttribute(W + "val", textFlow switch { 1 => "tbRl", 3 => "btLr", _ => "tbRlV" })));
        var grid = clm switch
        {
            1 => "linesAndChars",
            2 => "lines",
            3 => "snapToChars",
            _ => null,
        };
        sectPr.Add(new XElement(W + "docGrid", grid is null ? null : new XAttribute(W + "type", grid), linePitch > 0 ? new XAttribute(W + "linePitch", linePitch) : null));
        return sectPr;
    }

    // ───────────────────────── Borders, shading, colours ─────────────────────────

    /// <summary>A Brc80 (4 bytes: width in eighths of a point, type, ico, space) or Brc (8 bytes: COLORREF first).</summary>
    private static XElement? Border(string side, byte[] operand)
    {
        int width, type, space;
        string color;
        if (operand.Length >= 8)
        {
            if (operand.AsSpan(0, 8).IndexOfAnyExcept((byte)0xFF) < 0)
                return new XElement(W + side, new XAttribute(W + "val", "nil"));
            color = ColorRef(operand) ?? "auto";
            (width, type, space) = (operand[4], operand[5], operand[6] & 0x1F);
        }
        else if (operand.Length >= 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(operand) == 0xFFFFFFFF)
                return new XElement(W + side, new XAttribute(W + "val", "nil"));
            (width, type, space) = (operand[0], operand[1], operand[3] & 0x1F);
            color = Ico(operand[2]) ?? "auto";
        }
        else
        {
            return null;
        }
        return new XElement(W + side, new XAttribute(W + "val", BorderType(type)), new XAttribute(W + "sz", width),
            new XAttribute(W + "space", space), new XAttribute(W + "color", color));
    }

    private static string BorderType(int type) => type switch
    {
        0 => "none",
        1 or 5 => "single",
        2 => "thick",
        3 => "double",
        6 => "dotted",
        7 => "dashed",
        8 => "dotDash",
        9 => "dotDotDash",
        10 => "triple",
        11 => "thinThickSmallGap",
        12 => "thickThinSmallGap",
        13 => "thinThickThinSmallGap",
        14 => "thinThickMediumGap",
        15 => "thickThinMediumGap",
        16 => "thinThickThinMediumGap",
        17 => "thinThickLargeGap",
        18 => "thickThinLargeGap",
        19 => "thinThickThinLargeGap",
        20 => "wave",
        21 => "doubleWave",
        22 => "dashSmallGap",
        23 => "dashDotStroked",
        24 => "threeDEmboss",
        25 => "threeDEngrave",
        26 => "outset",
        27 => "inset",
        255 => "nil",
        _ => "single",
    };

    /// <summary>Shd80: foreground ico (bits 0–4), background ico (5–9), pattern (10–15).</summary>
    private static object[] Shd80(ushort shd) =>
    [
        new XAttribute(W + "val", Pattern(shd >> 10)),
        new XAttribute(W + "color", Ico(shd & 0x1F) ?? "auto"),
        new XAttribute(W + "fill", Ico((shd >> 5) & 0x1F) ?? "auto"),
    ];

    /// <summary>Shd: foreground and background COLORREFs, then the pattern.</summary>
    private static object[] Shd(byte[] shd) =>
    [
        new XAttribute(W + "val", Pattern(BinaryPrimitives.ReadUInt16LittleEndian(shd.AsSpan(8)))),
        new XAttribute(W + "color", ColorRef(shd) ?? "auto"),
        new XAttribute(W + "fill", ColorRef(shd.AsSpan(4)) ?? "auto"),
    ];

    private static string Pattern(int ipat) => ipat switch
    {
        0 => "clear",
        1 => "solid",
        2 => "pct5",
        3 => "pct10",
        4 => "pct20",
        5 => "pct25",
        6 => "pct30",
        7 => "pct40",
        8 => "pct50",
        9 => "pct60",
        10 => "pct70",
        11 => "pct75",
        12 => "pct80",
        13 => "pct90",
        14 => "horzStripe",
        15 => "vertStripe",
        16 => "reverseDiagStripe",
        17 => "diagStripe",
        18 => "horzCross",
        19 => "diagCross",
        20 => "thinHorzStripe",
        21 => "thinVertStripe",
        22 => "thinReverseDiagStripe",
        23 => "thinDiagStripe",
        24 => "thinHorzCross",
        25 => "thinDiagCross",
        37 => "pct12",
        38 => "pct15",
        43 => "pct35",
        44 => "pct37",
        46 => "pct45",
        49 => "pct55",
        51 => "pct62",
        52 => "pct65",
        57 => "pct85",
        58 => "pct87",
        61 => "pct95",
        0xFFFF => "nil",
        _ => "clear",
    };

    /// <summary>A COLORREF (red, green, blue, fAuto); null for "automatic".</summary>
    private static string? ColorRef(ReadOnlySpan<byte> cv) =>
        cv.Length < 4 || cv[3] == 0xFF ? null : $"{cv[0]:X2}{cv[1]:X2}{cv[2]:X2}";

    private static readonly string?[] IcoColors =
    [
        null, "000000", "0000FF", "00FFFF", "00FF00", "FF00FF", "FF0000", "FFFF00", "FFFFFF",
        "000080", "008080", "008000", "800080", "800000", "808000", "808080", "C0C0C0",
    ];

    /// <summary>Word 97's 16 colours by their index (0 = automatic).</summary>
    private static string? Ico(int ico) => ico >= 0 && ico < IcoColors.Length ? IcoColors[ico] : null;

    private static string HighlightName(int ico) => ico switch
    {
        1 => "black",
        2 => "blue",
        3 => "cyan",
        4 => "green",
        5 => "magenta",
        6 => "red",
        7 => "yellow",
        8 => "white",
        9 => "darkBlue",
        10 => "darkCyan",
        11 => "darkGreen",
        12 => "darkMagenta",
        13 => "darkRed",
        14 => "darkYellow",
        15 => "darkGray",
        16 => "lightGray",
        _ => "none",
    };

    // ───────────────────────── Helpers ─────────────────────────

    private static void SetAttribute(XElement element, string name, object value) => element.SetAttributeValue(W + name, Format(value));

    private static string Format(object value) => value switch
    {
        int i => i.ToString(CultureInfo.InvariantCulture),
        short s => s.ToString(CultureInfo.InvariantCulture),
        ushort u => u.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
