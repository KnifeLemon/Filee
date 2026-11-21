// DOCX (Office Open XML) → HDocument, read directly instead of through Pandoc so the layout survives:
// page size and margins per section, columns, headers and footers with page numbers, text boxes, floating
// pictures, paragraph and character formatting, tables with borders, shading and merged cells, lists,
// footnotes and endnotes, bookmarks and hyperlinks (including the links of a Word table of contents).
//
// Split over partial files: this one walks the package and the text, DocxReader.Styles resolves formatting,
// DocxReader.Tables and DocxReader.Drawing handle tables and graphic objects, DocxReader.Revisions comments and
// tracked changes. The package is read within
// DocumentLimits (Documents/DocumentPackage); embedded fonts (word/fonts) are not read.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Infrastructure;
using Filee.Engines.Typesetting;

namespace Filee.Engines.Hwp.Hwpx.Docx;

/// <summary>Reads a .docx file into the HWPX document model.</summary>
internal sealed partial class DocxReader
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private static readonly XNamespace Wps = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private static readonly XNamespace Wpg = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    /// <summary>Styles followed through basedOn at the most (real chains are a few long; damaged files loop).</summary>
    private const int MaxStyleChain = 20;

    private readonly DocumentPackage _package;
    private readonly DocumentBudget _budget;
    private readonly string _mediaFolder;
    private readonly UnpackLimit _unpack;
    private readonly Dictionary<string, Part?> _parts = [];
    private readonly Dictionary<string, string?> _media = [];
    private readonly Dictionary<string, XElement> _footnotes = [];
    private readonly Dictionary<string, XElement> _endnotes = [];
    private readonly List<FieldState> _fields = [];
    private readonly HLeftOut _leftOut = new();

    /// <summary>Header and footer parts read so far (see <see cref="HeaderFooterBlocks"/>).</summary>
    private readonly HashSet<string> _headerFooterParts = new(StringComparer.OrdinalIgnoreCase);

    private Part? _footnotesPart;
    private Part? _endnotesPart;
    private bool _evenAndOddHeaders;

    /// <summary>Page borders measured from the text go around the header and the footer (Word's default; settings say otherwise).</summary>
    private bool _bordersAroundHeader = true;
    private bool _bordersAroundFooter = true;

    /// <summary>
    /// Word's HTML paragraph spacing (settings without w:doNotUseHTMLParagraphAutoSpacing): neighbouring paragraphs keep
    /// the larger of their spaces between them (HDocument.CollapseParagraphSpacing), and automatic spacing is HTML's.
    /// </summary>
    private bool _collapseSpacing = true;

    /// <summary>
    /// Word's compatibility mode (settings w:compatSetting compatibilityMode): 15 for Word 2013 and later, 14 or less for
    /// documents kept in the way of Word 2010 and older, 12 (Word 2007) when the settings don't say.
    /// </summary>
    private int _compatibilityMode = 12;

    /// <summary>Width of each section's text column (HWPUNIT), for equations that don't fit a line.</summary>
    private readonly List<int> _columnWidths = [];

    /// <summary>Line pitch (twips) of each section's document grid, 0 = none; see <see cref="SnapToGrid"/>.</summary>
    private readonly List<int> _linePitches = [];
    private int _sectionIndex;
    private int _textBoxDepth;
    private int _tableDepth;

    /// <summary>The document's main part (word/document.xml): the layout snaps its lines to the grid, not other parts'.</summary>
    private Part? _body;

    /// <summary>Lines in table cells snap to the grid too (settings w:compat/w:adjustLineHeightInTable).</summary>
    private bool _gridInTables;

    /// <summary>The font table's other names (HDocument.FontAlternates), for fonts that stand for a symbol font.</summary>
    private IReadOnlyDictionary<string, string> _fontAlternates = new Dictionary<string, string>();

    /// <summary>Reading the header for the first page only, which becomes a text box (see <see cref="HSection.FirstPageHeader"/>).</summary>
    private bool _inFirstPageHeader;

    /// <summary>
    /// The block list whose next block starts a new page: a paragraph ended right after its page break, and Word keeps
    /// such a paragraph's mark on the old page.
    /// </summary>
    private List<HBlock>? _pageBreakFor;

    /// <summary>The paragraph marks left out so for the next block (<see cref="HBlock.BreakParagraphsBefore"/>).</summary>
    private int _pageBreakMarks;

    /// <summary>Table cells, text boxes and notes around the blocks being read (see <see cref="ReadNestedBlocks"/>).</summary>
    private int _depth;

    /// <summary>Inside a note: references to notes there are not followed (Word has no notes in notes).</summary>
    private bool _inNote;

    /// <summary>Complex fields begun while <see cref="Nesting.MaxDepth"/> others were open, which are not tracked.</summary>
    private int _untrackedFields;

    /// <summary>The part being read; relationship ids (images, links) are resolved against it.</summary>
    private Part _part;

    private DocxReader(DocumentPackage package, string mediaFolder, UnpackLimit unpack)
    {
        _package = package;
        _budget = new DocumentBudget(package.Limits);
        _mediaFolder = mediaFolder;
        _unpack = unpack;
        var mainPath = RelationshipsOf("")
            .FirstOrDefault(r => r.Value.Type.EndsWith("/officeDocument", StringComparison.Ordinal)).Value.Target ?? "word/document.xml";
        _part = LoadPart(mainPath) ?? throw new InvalidDataException("The file is not a Word document (no word/document.xml).");
    }

    /// <summary>Reads <paramref name="path"/>; embedded pictures are extracted into <paramref name="mediaFolder"/>.</summary>
    public static HDocument Read(string path, string mediaFolder) => Read(path, mediaFolder, DocumentLimits.Default, UnpackLimit.For(path));

    /// <summary>Reads within other <paramref name="limits"/> than <see cref="DocumentLimits.Default"/> (for tests).</summary>
    internal static HDocument Read(string path, string mediaFolder, DocumentLimits limits) =>
        Read(path, mediaFolder, limits, UnpackLimit.For(path));

    /// <summary><see cref="Read(string, string)"/> with another limit on what the pictures unpack to (for tests).</summary>
    internal static HDocument Read(string path, string mediaFolder, UnpackLimit unpack) =>
        Read(path, mediaFolder, DocumentLimits.Default, unpack);

    /// <summary>Reads on a thread with a large stack (<see cref="Nesting.OnLargeStack{T}"/>): the reader recurses per level.</summary>
    private static HDocument Read(string path, string mediaFolder, DocumentLimits limits, UnpackLimit unpack) => Nesting.OnLargeStack(() =>
    {
        using var package = DocumentPackage.Open(path, limits,
            () => new InvalidDataException("The file is not a Word document (it is not a ZIP package). It may be damaged or protected by document security (DRM) software."));
        return new DocxReader(package, mediaFolder, unpack).ReadDocument();
    });

    /// <summary>An XML part; damaged XML is invalid data with the part's name.</summary>
    private XDocument LoadXml(ZipArchiveEntry entry, bool preserveWhitespace)
    {
        try
        {
            return _package.LoadXml(entry, preserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The Word document cannot be read ({entry.FullName} is not valid XML). It may be encrypted or damaged. {ex.Message}", ex);
        }
    }

    // ───────────────────────── Package ─────────────────────────

    private sealed class Part(string path, XDocument xml, Dictionary<string, Relationship> relationships)
    {
        public string Path { get; } = path;
        public XDocument Xml { get; } = xml;
        public Dictionary<string, Relationship> Relationships { get; } = relationships;
    }

    private readonly record struct Relationship(string Type, string Target, bool External);

    private Part? LoadPart(string path)
    {
        if (_parts.TryGetValue(path, out var cached))
            return cached;
        Part? part = null;
        if (_package.GetEntry(path) is { } entry)
        {
            // Whitespace matters: <w:t xml:space="preserve"> </w:t> is a real space.
            part = new Part(path, LoadXml(entry, preserveWhitespace: true), RelationshipsOf(path));
        }
        _parts[path] = part;
        return part;
    }

    /// <summary>Relationships of a part ("" = the package), with targets resolved to zip paths.</summary>
    private Dictionary<string, Relationship> RelationshipsOf(string partPath)
    {
        var folder = partPath.Contains('/') ? partPath[..partPath.LastIndexOf('/')] : "";
        var relsPath = partPath.Length == 0 ? "_rels/.rels" : $"{folder}/_rels/{partPath[(partPath.LastIndexOf('/') + 1)..]}.rels".TrimStart('/');
        var result = new Dictionary<string, Relationship>();
        if (_package.GetEntry(relsPath) is not { } entry)
            return result;
        foreach (var rel in LoadXml(entry, preserveWhitespace: false).Root?.Elements(PackageRelationships + "Relationship") ?? [])
        {
            var id = (string?)rel.Attribute("Id") ?? "";
            var target = (string?)rel.Attribute("Target") ?? "";
            var external = (string?)rel.Attribute("TargetMode") == "External";
            result[id] = new Relationship((string?)rel.Attribute("Type") ?? "", external ? target : ResolvePath(folder, target), external);
        }
        return result;
    }

    private static string ResolvePath(string folder, string target)
    {
        if (target.StartsWith('/'))
            return target.TrimStart('/');
        var parts = new List<string>(folder.Length == 0 ? [] : folder.Split('/'));
        foreach (var segment in Uri.UnescapeDataString(target).Split('/'))
        {
            if (segment == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
            }
            else if (segment is not ("." or ""))
            {
                parts.Add(segment);
            }
        }
        return string.Join('/', parts);
    }

    private Part? RelatedPart(Part from, string typeSuffix) =>
        from.Relationships.Values.FirstOrDefault(r => !r.External && r.Type.EndsWith(typeSuffix, StringComparison.Ordinal)) is { Target.Length: > 0 } rel
            ? LoadPart(rel.Target)
            : null;

    /// <summary>Runs <paramref name="read"/> with relationship ids resolved against <paramref name="part"/>.</summary>
    private T WithPart<T>(Part part, Func<T> read)
    {
        var previous = _part;
        _part = part;
        try
        {
            return read();
        }
        finally
        {
            _part = previous;
        }
    }

    // ───────────────────────── Document and sections ─────────────────────────

    private HDocument ReadDocument()
    {
        var main = _part;
        LoadStyles(RelatedPart(main, "/styles"));
        DefaultFonts();
        var theme = RelatedPart(main, "/theme");
        LoadTheme(theme);
        LoadThemeColors(theme);
        LoadNumbering(RelatedPart(main, "/numbering"));
        var settings = RelatedPart(main, "/settings")?.Xml.Root;
        _evenAndOddHeaders = settings?.Element(W + "evenAndOddHeaders") is { } evenOdd && Val(evenOdd) is not ("0" or "false");
        _bordersAroundHeader = !(settings?.Element(W + "bordersDoNotSurroundHeader") is { } noHeader && Val(noHeader) is not ("0" or "false"));
        _bordersAroundFooter = !(settings?.Element(W + "bordersDoNotSurroundFooter") is { } noFooter && Val(noFooter) is not ("0" or "false"));
        _collapseSpacing = !(settings?.Element(W + "compat")?.Element(W + "doNotUseHTMLParagraphAutoSpacing") is { } sum && Val(sum) is not ("0" or "false"));
        _compatibilityMode = CompatibilityMode(settings);
        _mathWrap = MathWrap(settings?.Element(M + "mathPr"));
        _body = main;
        _gridInTables = settings?.Element(W + "compat")?.Element(W + "adjustLineHeightInTable") is { } inTables && Val(inTables) is not ("0" or "false");
        _footnotesPart = RelatedPart(main, "/footnotes");
        _endnotesPart = RelatedPart(main, "/endnotes");
        foreach (var note in _footnotesPart?.Xml.Root?.Elements(W + "footnote") ?? [])
            _footnotes[Attr(note, "id") ?? ""] = note;
        foreach (var note in _endnotesPart?.Xml.Root?.Elements(W + "endnote") ?? [])
            _endnotes[Attr(note, "id") ?? ""] = note;
        LoadComments(main);

        var document = new HDocument { Title = Title(), LeftOut = _leftOut, WordLineHeights = true, CollapseParagraphSpacing = _collapseSpacing };
        Kinsoku(settings, document);
        FontAlternates(RelatedPart(main, "/fontTable")?.Xml.Root, document);
        _fontAlternates = document.FontAlternates;
        // Word's default tab stops: every half inch unless the settings say otherwise.
        document.DefaultTabStop = HwpxUnits.FromTwips(Twips(settings?.Element(W + "defaultTabStop"), "val") is > 0 and var interval ? interval : 720);
        var body = main.Xml.Root?.Element(W + "body") ?? throw new InvalidDataException("word/document.xml has no body.");

        // A section ends with the paragraph whose properties hold its sectPr; the last one is the body's own sectPr.
        // Paragraphs come before their sectPr, so collect the document grids first.
        foreach (var element in Flatten(body.Elements()))
        {
            if (element.Name == W + "p" && element.Element(W + "pPr")?.Element(W + "sectPr") is { } sectionEnd)
            {
                _linePitches.Add(LinePitch(sectionEnd));
                _columnWidths.Add(ColumnWidth(sectionEnd));
            }
        }
        _linePitches.Add(LinePitch(body.Element(W + "sectPr")));
        _columnWidths.Add(ColumnWidth(body.Element(W + "sectPr")));

        var blocks = new List<HBlock>();
        var headers = new Dictionary<string, List<HBlock>>();
        var footers = new Dictionary<string, List<HBlock>>();
        foreach (var element in Flatten(body.Elements()))
        {
            if (element.Name == W + "sectPr")
                continue;
            ReadBlock(element, blocks);
            if (element.Name == W + "p" && element.Element(W + "pPr")?.Element(W + "sectPr") is { } sectPr)
            {
                AddSection(document, blocks, sectPr, headers, footers);
                blocks = [];
                _sectionIndex++;
            }
        }
        AddSection(document, blocks, body.Element(W + "sectPr"), headers, footers);
        return document;
    }

    private string? Title()
    {
        var core = _package.GetEntry("docProps/core.xml");
        if (core is null)
            return null;
        return NullIfEmpty(LoadXml(core, preserveWhitespace: false).Descendants(Dc + "title").FirstOrDefault()?.Value);
    }

    private void AddSection(HDocument document, List<HBlock> blocks, XElement? sectPr,
        Dictionary<string, List<HBlock>> headers, Dictionary<string, List<HBlock>> footers)
    {
        var page = Page(sectPr);
        var columns = Columns(sectPr);
        var flow = TextFlow(sectPr);
        var previous = document.Sections.LastOrDefault();

        // Sections without their own header or footer reference inherit the previous section's.
        var headerReferences = sectPr?.Elements(W + "headerReference").ToList() ?? [];
        var footerReferences = sectPr?.Elements(W + "footerReference").ToList() ?? [];
        foreach (var reference in headerReferences)
            headers[Attr(reference, "type") ?? "default"] = HeaderFooterBlocks(reference);
        foreach (var reference in footerReferences)
            footers[Attr(reference, "type") ?? "default"] = HeaderFooterBlocks(reference);

        // Word uses continuous section breaks to change the number of columns mid-page. 한글 does that with a
        // column definition inside the section, so keep one section when the paper does not change.
        if (previous is not null && Val(sectPr?.Element(W + "type")) == "continuous" && previous.Page == page && previous.TextFlow == flow)
        {
            if (blocks.FirstOrDefault() is not HParagraph first)
                blocks.Insert(0, first = new HParagraph());
            first.ColumnsChange = columns;
            previous.Blocks.AddRange(blocks);

            // Its header or footer starts on the next page in Word, on the page of this paragraph in 한글.
            if (headerReferences.Count > 0)
                Add(headers, previous.Headers, first);
            if (footerReferences.Count > 0)
                Add(footers, previous.Footers, first);
            return;
        }

        var section = new HSection
        {
            Page = page,
            Columns = columns,
            // A section written vertically (w:textDirection tbRl, btLr, ...) is drawn turned.
            TextFlow = flow,
            Grid = Grid(sectPr),
            HeaderMovesBody = Twips(sectPr?.Element(W + "pgMar"), "top") is not < 0,
            FooterMovesBody = Twips(sectPr?.Element(W + "pgMar"), "bottom") is not < 0,
            FooterGrowsUp = true,
            // Word numbers notes 1, 2, 3 and i, ii, iii without 한글's ")" after them; the number is in the note's text
            // where Word has it (HNoteNumber).
            Footnotes = HNoteShape.Footnote with { Format = new HNoteFormat("DIGIT", "", "", false) },
            Endnotes = HNoteShape.Endnote with { Format = new HNoteFormat("ROMAN_SMALL", "", "", false) },
        };
        section.Blocks.AddRange(blocks);
        PageBorders(sectPr, section);

        void Add(Dictionary<string, List<HBlock>> source, List<HHeaderFooter> target, HParagraph? startsAt = null)
        {
            if (_evenAndOddHeaders)
            {
                if (source.GetValueOrDefault("default") is { } odd)
                    AddHeaderFooter(target, HPageType.Odd, odd, startsAt);
                if (source.GetValueOrDefault("even") is { } even)
                    AddHeaderFooter(target, HPageType.Even, even, startsAt);
            }
            else if (source.GetValueOrDefault("default") is { } both)
            {
                AddHeaderFooter(target, HPageType.Both, both, startsAt);
            }
        }
        Add(headers, section.Headers);
        Add(footers, section.Footers);

        // "Different first page": 한글 can only hide the header/footer on the first page, which is what an empty
        // (or missing) first-page header means in Word. A header or footer with content is kept for the first page.
        if (sectPr?.Element(W + "titlePg") is { } titlePg && Val(titlePg) is not ("0" or "false"))
        {
            section.HideFirstHeader = IsBlank(headers.GetValueOrDefault("first"));
            section.HideFirstFooter = IsBlank(footers.GetValueOrDefault("first"));
            if (!section.HideFirstHeader)
                section.FirstPageHeader = headers["first"];
            if (!section.HideFirstFooter)
                section.FirstPageFooter = footers["first"];
        }
        if (int.TryParse(Attr(sectPr?.Element(W + "pgNumType"), "start"), out var start))
            section.StartPageNumber = start;

        document.Sections.Add(section);
    }

    private static void AddHeaderFooter(List<HHeaderFooter> target, HPageType pages, List<HBlock> blocks, HParagraph? startsAt)
    {
        var item = new HHeaderFooter(pages) { StartsAt = startsAt };
        item.Blocks.AddRange(blocks);
        target.Add(item);
    }

    private List<HBlock> HeaderFooterBlocks(XElement reference)
    {
        var blocks = new List<HBlock>();
        if (Attr(reference, "id", R) is { } id && _part.Relationships.TryGetValue(id, out var rel) && LoadPart(rel.Target) is { } part)
        {
            _inFirstPageHeader = reference.Name == W + "headerReference" && Attr(reference, "type") == "first";
            // A header or footer is read again for every section that names it; what it leaves out is counted once.
            _leftOut.Muted = !_headerFooterParts.Add(rel.Target);
            try
            {
                WithPart(part, () =>
                {
                    foreach (var element in Flatten(part.Xml.Root?.Elements() ?? []))
                        ReadBlock(element, blocks);
                    return 0;
                });
            }
            finally
            {
                _inFirstPageHeader = false;
                _leftOut.Muted = false;
            }
        }
        return blocks;
    }

    private static bool IsBlank(List<HBlock>? blocks) =>
        blocks is null || blocks.All(b => b is HParagraph p && p.Inlines.All(i => i is HText t && string.IsNullOrWhiteSpace(t.Text) || i is HBookmark));

    private static HPage Page(XElement? sectPr)
    {
        var size = sectPr?.Element(W + "pgSz");
        var margin = sectPr?.Element(W + "pgMar");
        int Get(XElement? element, string name, int fallback) => Math.Abs(Twips(element, name) ?? fallback);

        var width = Get(size, "w", 11906);
        var height = Get(size, "h", 16838);
        if (Attr(size, "orient") == "landscape" && width < height)
            (width, height) = (height, width);

        // Word: top = paper edge → body, header = paper edge → header text.
        // 한글: top = paper edge → header, header = header area height (body starts at top + header).
        var top = Get(margin, "top", 1440);
        var bottom = Get(margin, "bottom", 1440);
        var headerDistance = Math.Min(Get(margin, "header", 720), top);
        var footerDistance = Math.Min(Get(margin, "footer", 720), bottom);
        return new HPage(
            Width: HwpxUnits.FromTwips(width),
            Height: HwpxUnits.FromTwips(height),
            Left: HwpxUnits.FromTwips(Get(margin, "left", 1440)),
            Right: HwpxUnits.FromTwips(Get(margin, "right", 1440)),
            Top: HwpxUnits.FromTwips(headerDistance),
            Bottom: HwpxUnits.FromTwips(footerDistance),
            Header: HwpxUnits.FromTwips(top - headerDistance),
            Footer: HwpxUnits.FromTwips(bottom - footerDistance),
            Gutter: HwpxUnits.FromTwips(Get(margin, "gutter", 0)));
    }

    /// <summary>Width of a section's text column (HWPUNIT).</summary>
    private static int ColumnWidth(XElement? sectPr)
    {
        var page = Page(sectPr);
        var columns = Columns(sectPr);
        var text = page.Width - page.Left - page.Right - page.Gutter;
        return columns.Count <= 1 ? text : (text - columns.Gap * (columns.Count - 1)) / columns.Count;
    }

    /// <summary>Line pitch of a "lines" document grid (twips), or 0.</summary>
    private static int LinePitch(XElement? sectPr)
    {
        var grid = sectPr?.Element(W + "docGrid");
        return Attr(grid, "type") is "lines" or "linesAndChars" or "snapToChars" ? Twips(grid, "linePitch") ?? 0 : 0;
    }

    /// <summary>
    /// Word's document grid ("lines") puts every line on a multiple of the grid pitch: a single-spaced 10 pt line
    /// with an 18 pt grid is 18 pt high. 한글 has no such grid for body text, so the paragraph gets an "at least"
    /// line spacing of the snapped height (larger content such as pictures can still grow the line). The layout gets the
    /// pitch (HLineHeight.GridPitch) for the body's paragraphs, in table cells only where the settings say so
    /// (w:adjustLineHeightInTable): headers, footers, notes and text boxes keep their lines.
    /// </summary>
    private void SnapToGrid(HParagraph paragraph, ParaProps props)
    {
        var pitch = _textBoxDepth > 0 || _sectionIndex >= _linePitches.Count ? 0 : _linePitches[_sectionIndex];
        if (pitch <= 0 || props.SnapToGrid == false || props.LineRule is "exact" or "atLeast")
            return;
        var size = MaxFontSize(paragraph.Inlines) ?? paragraph.MarkFormat.Size ?? 1000;
        var cell = HwpxUnits.FromTwips(pitch);
        var natural = size * SingleLinePercent(paragraph) / 100.0; // 1/100 pt = HWPUNIT
        var lines = Math.Max(1, Math.Ceiling(natural / cell));
        var multiple = (props.Line ?? 240) / 240.0;
        // The line rule itself stays Word's multiple, with the grid its lines snap to.
        var layout = paragraph.Format.Layout ?? new HParaLayout();
        paragraph.Format = paragraph.Format with
        {
            LineSpacing = new HLineSpacing(HLineSpacingKind.AtLeast, (int)Math.Round(lines * cell * multiple)),
            Layout = layout with
            {
                LineHeight = (layout.LineHeight ?? new HLineHeight(HLineHeightRule.Multiple, props.Line ?? 240)) with
                {
                    GridPitch = ReferenceEquals(_part, _body) && (_tableDepth == 0 || _gridInTables) ? cell : 0,
                },
            },
        };
    }

    /// <summary>The section's document grid (w:docGrid); null without one.</summary>
    private static HDocumentGrid? Grid(XElement? sectPr)
    {
        var grid = sectPr?.Element(W + "docGrid");
        var kind = Attr(grid, "type") switch
        {
            "lines" => HGridKind.Lines,
            "linesAndChars" => HGridKind.LinesAndChars,
            "snapToChars" => HGridKind.SnapToChars,
            _ => HGridKind.None,
        };
        return kind == HGridKind.None ? null : new HDocumentGrid(kind, HwpxUnits.FromTwips(Twips(grid, "linePitch") ?? 0), CharSpace: Twips(grid, "charSpace") ?? 0);
    }

    /// <summary>
    /// The compatibility mode the settings give (w:compat/w:compatSetting name="compatibilityMode", Word's own uri), or
    /// 12 when they give none: Word opens such a document as a Word 2007 one.
    /// </summary>
    private static int CompatibilityMode(XElement? settings)
    {
        foreach (var setting in settings?.Element(W + "compat")?.Elements(W + "compatSetting") ?? [])
        {
            if (Attr(setting, "name") == "compatibilityMode" && int.TryParse(Attr(setting, "val"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mode))
                return Math.Clamp(mode, 0, 1000);
        }
        return 12;
    }

    /// <summary>Custom East Asian line breaking characters of the document (settings: w:noLineBreaksBefore / After).</summary>
    private static void Kinsoku(XElement? settings, HDocument document)
    {
        var rules = new Dictionary<string, (string? Start, string? End)>();
        foreach (var element in settings?.Elements() ?? [])
        {
            var before = element.Name == W + "noLineBreaksBefore";
            if (!before && element.Name != W + "noLineBreaksAfter")
                continue;
            var language = Attr(element, "lang") ?? "";
            var (start, end) = rules.GetValueOrDefault(language);
            // "No line break before" a character: it cannot start a line; "after": it cannot end one.
            rules[language] = before ? (Val(element), end) : (start, Val(element));
        }
        foreach (var (language, (start, end)) in rules)
            document.Kinsoku.Add(new HKinsoku(language.Length > 0 ? language : null, start, end));
    }

    /// <summary>
    /// A symbol font's name for <paramref name="font"/>: the font, or its other name in the font table when that is a
    /// symbol font and the font isn't ("Wingdings-Regular" with altName Wingdings, as PDF converters write them).
    /// </summary>
    private string? SymbolFont(string? font) =>
        font is not null && !SymbolFonts.IsSymbolFont(font) && _fontAlternates.TryGetValue(font.Trim(), out var alternate) && SymbolFonts.IsSymbolFont(alternate)
            ? alternate
            : font;

    /// <summary>The run's format with its font's symbol-font other name in its place (see <see cref="SymbolFont"/>).</summary>
    private HCharFormat SymbolAlternate(HCharFormat format) =>
        SymbolFont(format.Font) is { } font && !ReferenceEquals(font, format.Font) ? format with { Font = font } : format;

    /// <summary>The font table's other names (w:altName), which Word draws with when a font is not installed.</summary>
    private static void FontAlternates(XElement? fonts, HDocument document)
    {
        foreach (var font in fonts?.Elements(W + "font") ?? [])
        {
            if (Attr(font, "name")?.Trim() is { Length: > 0 } name && Val(font.Element(W + "altName"))?.Trim() is { Length: > 0 } alternate
                && !alternate.Equals(name, StringComparison.OrdinalIgnoreCase))
                document.FontAlternates.TryAdd(name, alternate);
        }
    }

    /// <summary>Style and contextual-spacing flag of the paragraphs read so far, to compare a paragraph with the one before it.</summary>
    private readonly Dictionary<HParagraph, (string? StyleId, bool Contextual)> _paragraphStyles = [];

    private void RememberParagraphStyle(HParagraph paragraph, ParaProps props) =>
        _paragraphStyles[paragraph] = (props.StyleId, props.ContextualSpacing == true);

    /// <summary>
    /// Word's "Don't add space between paragraphs of the same style" (w:contextualSpacing, set by the List Bullet and
    /// List Number styles): a paragraph with it loses the space above when the paragraph before has its style, and the
    /// space below when the next one does. Without this every list item kept its 10 pt gap and the pages ran long (#1194).
    /// </summary>
    private void ApplyContextualSpacing(HParagraph previous, HParagraph current, ParaProps props)
    {
        if (current.PageBreakBefore || current.ColumnBreakBefore
            || !_paragraphStyles.TryGetValue(previous, out var before) || before.StyleId != props.StyleId)
            return;
        if (before.Contextual)
            previous.Format = previous.Format with { After = 0 };
        if (props.ContextualSpacing == true)
            current.Format = current.Format with { Before = 0 };
    }

    /// <summary>
    /// Word's automatic space before or after a paragraph (w:beforeAutospacing / w:afterAutospacing), in twips: HTML's
    /// 14 pt, or with w:doNotUseHTMLParagraphAutoSpacing the fixed 5 pt before and 10 pt after the standard gives.
    /// </summary>
    private int AutoSpace(bool before) => _collapseSpacing ? 280 : before ? 100 : 200;

    /// <summary>
    /// Gives a paragraph with "multiple" line spacing the height its fonts have in Word: 한글's percentage is of the font
    /// size, and a single Calibri line is 122% of it where one in Times New Roman is 115%.
    /// </summary>
    private static void FitLineSpacing(HParagraph paragraph, ParaProps props)
    {
        if (props.LineRule is "exact" or "atLeast")
            return;
        var percent = (int)Math.Round((props.Line ?? 240) / 240.0 * SingleLinePercent(paragraph));
        paragraph.Format = paragraph.Format with { LineSpacing = new HLineSpacing(HLineSpacingKind.Percent, percent) };
    }

    /// <summary>
    /// A paragraph's single line height as a percentage of its largest font size: its tallest text decides, as on a line
    /// in Word. A paragraph without text has the height of its mark, or of the equation or picture in it: 100%, since
    /// 한글's percentage of a tall object would leave a third of its height empty above it.
    /// </summary>
    private static double SingleLinePercent(HParagraph paragraph)
    {
        var (size, height, inlineObject) = (0, 0.0, false);
        Measure(paragraph.Inlines);
        if (size == 0 && inlineObject)
            return 100;
        if (size == 0 && paragraph.MarkFormat is var mark)
            Add("", mark);
        return height / size;

        void Measure(IEnumerable<HInline> inlines)
        {
            foreach (var inline in inlines)
            {
                if (inline is HEquation or HImage { Anchor: null })
                    inlineObject = true;
                else if (inline is HText { Text.Length: > 0 } text)
                    Add(text.Text, text.Format);
                else if (inline is HLink link)
                    Measure(link.Content);
            }
        }

        void Add(string text, HCharFormat format)
        {
            var points = format.Size ?? 1000;
            size = Math.Max(size, points);
            height = Math.Max(height, points * TextLinePercent(text, format));
        }
    }

    /// <summary>The single line height (percent of the font size) of a run: the East Asian font draws its CJK characters, the other font the rest.</summary>
    private static double TextLinePercent(string text, HCharFormat format)
    {
        var (cjk, other) = (false, text.Length == 0);
        foreach (var c in text)
        {
            if (c is >= 'ᄀ' and <= 'ᇿ' or >= '⺀' and <= '퟿' or >= '豈' and <= '﫿' or >= '︰' and <= '﹏' or >= '＀' and <= '￯')
                cjk = true;
            else
                other = true;
        }
        var percent = 0.0;
        if (cjk)
            percent = FontLinePercent(format.EastAsianFont ?? format.Font);
        if (other)
            percent = Math.Max(percent, FontLinePercent(format.Font ?? format.EastAsianFont));
        return percent;
    }

    private static double FontLinePercent(string? font) =>
        font is not null && SingleLineHeights.TryGetValue(font, out var height) ? height : WordLineToHwpPercent;

    private static int? MaxFontSize(IEnumerable<HInline> inlines)
    {
        int? max = null;
        foreach (var inline in inlines)
        {
            var size = inline switch
            {
                HText text => text.Format.Size,
                HLink link => MaxFontSize(link.Content),
                HField field => field.Format.Size,
                _ => null,
            };
            if (size > (max ?? 0))
                max = size;
        }
        return max;
    }

    private static HColumns Columns(XElement? sectPr)
    {
        var cols = sectPr?.Element(W + "cols");
        // Columns of their own widths (equalWidth="0") are the w:col elements, each with its width and the space after it.
        var own = cols is not null && Bool(cols, "equalWidth") == false
            ? cols.Elements(W + "col").Take(256).ToList()
            : [];
        var count = own.Count > 1 ? own.Count
            : int.TryParse(Attr(cols, "num"), out var n) ? n : cols?.Elements(W + "col").Count() ?? 1;
        if (count <= 1)
            return HColumns.Single;
        List<HColumnSize>? sizes = own.Count > 1
            ? [.. own.Select((col, i) => new HColumnSize(HwpxUnits.FromTwips(Twips(col, "w") ?? 0), i + 1 < own.Count ? HwpxUnits.FromTwips(Math.Max(0, Twips(col, "space") ?? 0)) : 0))]
            : null;
        // A right-to-left section (w:bidi) fills its columns from the right.
        return HColumns.Of(count, HwpxUnits.FromTwips(Twips(cols, "space") ?? 720), Bool(cols, "sep") == true,
            sectPr is not null && Toggle(sectPr, "bidi") == true, sizes);
    }

    /// <summary>
    /// A section's page border (w:pgBorders): each side's line, w:space points from the paper's edge (offsetFrom="page")
    /// or out from the text, on all pages, the first only or all but the first (w:display).
    /// </summary>
    private void PageBorders(XElement? sectPr, HSection section)
    {
        if (sectPr?.Element(W + "pgBorders") is not { } borders)
            return;
        var (left, leftSpace) = PageBorderLine(borders.Element(W + "left"));
        var (right, rightSpace) = PageBorderLine(borders.Element(W + "right"));
        var (top, topSpace) = PageBorderLine(borders.Element(W + "top"));
        var (bottom, bottomSpace) = PageBorderLine(borders.Element(W + "bottom"));
        var border = new HPageBorder(HPageType.Both, new HBorders(left, right, top, bottom), Fill: null, Picture: null,
            new HInsets(leftSpace, rightSpace, topSpace, bottomSpace),
            AroundBody: Attr(borders, "offsetFrom") != "page",
            HeaderInside: _bordersAroundHeader, FooterInside: _bordersAroundFooter, HPageFillArea.Paper)
        {
            // Over the text unless the border says it goes behind it.
            InFront = Attr(borders, "zOrder") != "back",
        };
        if (border.IsEmpty)
            return;
        section.PageBorders.Add(border);
        section.PageBorderShows = Attr(borders, "display") switch
        {
            "firstPage" => HFirstPageRule.OnlyFirst,
            "notFirstPage" => HFirstPageRule.HideFirst,
            _ => HFirstPageRule.All,
        };
    }

    /// <summary>
    /// One side of a page border and how far its line's middle lies from the paper's edge or the text (HWPUNIT). Line
    /// styles the layout doesn't draw become the nearest it does: multi-line ones a double line, picture borders
    /// (w:val="apples" ...) a single line.
    /// </summary>
    private static (HBorder Line, int Offset) PageBorderLine(XElement? edge)
    {
        if (edge is null)
            return (HBorder.None, 0);
        var value = Val(edge);
        var (style, art) = value switch
        {
            null or "nil" or "none" => (HBorderStyle.None, false),
            "single" or "thick" or "wave" or "threeDEmboss" or "threeDEngrave" or "outset" or "inset" => (HBorderStyle.Solid, false),
            "dotted" => (HBorderStyle.Dot, false),
            "dashed" or "dashSmallGap" => (HBorderStyle.Dash, false),
            "dotDash" or "dotDotDash" or "dashDotStroked" => (HBorderStyle.DashDot, false),
            "double" or "triple" or "doubleWave" or "thinThickSmallGap" or "thickThinSmallGap" or "thinThickThinSmallGap"
                or "thinThickMediumGap" or "thickThinMediumGap" or "thinThickThinMediumGap" or "thinThickLargeGap"
                or "thickThinLargeGap" or "thinThickThinLargeGap" => (HBorderStyle.Double, false),
            _ => (HBorderStyle.Solid, true),
        };
        if (style == HBorderStyle.None)
            return (HBorder.None, 0);
        // w:sz is in eighths of a point (2-96) for lines; a double line is two such lines with one's width between them.
        var points = art ? 1.5 : Math.Clamp(Twips(edge, "sz") ?? 4, 2, 96) / 8.0;
        if (style == HBorderStyle.Double)
            points *= 3;
        var color = Attr(edge, "color") is { Length: 6 } hex ? "#" + hex.ToUpperInvariant() : "#000000";
        var space = Math.Clamp(Twips(edge, "space") ?? 0, 0, 31);
        return (new HBorder(style, points * 25.4 / 72, color), HwpxUnits.FromPoints(space + points / 2));
    }

    // ───────────────────────── Blocks ─────────────────────────

    /// <summary>Paragraphs and tables, looking through content controls, custom XML and tracked insertions.</summary>
    private static IEnumerable<XElement> Flatten(IEnumerable<XElement> elements)
    {
        foreach (var element in elements)
        {
            var name = element.Name;
            if (name == W + "sdt")
            {
                foreach (var inner in Flatten(element.Element(W + "sdtContent")?.Elements() ?? []))
                    yield return inner;
            }
            else if (name == W + "customXml" || name == W + "ins" || name == W + "moveTo")
            {
                foreach (var inner in Flatten(element.Elements()))
                    yield return inner;
            }
            else if (name == W + "p" || name == W + "tbl" || name == W + "sectPr")
            {
                yield return element;
            }
        }
    }

    private void ReadBlocks(IEnumerable<XElement> elements, List<HBlock> output)
    {
        foreach (var element in Flatten(elements))
            ReadBlock(element, output);
    }

    /// <summary>
    /// The blocks of a table cell, text box or note, one level deeper. Beyond <see cref="Nesting.MaxDepth"/> levels
    /// only their text is read, as plain paragraphs: a crafted file may nest tables thousands of levels deep.
    /// </summary>
    private void ReadNestedBlocks(XElement container, List<HBlock> output)
    {
        if (_depth >= Nesting.MaxDepth)
        {
            output.AddRange(Nesting.PlainParagraphs(container, "p", "t"));
            return;
        }
        _depth++;
        try
        {
            ReadBlocks(container.Elements(), output);
        }
        finally
        {
            _depth--;
        }
    }

    private void ReadBlock(XElement element, List<HBlock> output)
    {
        var count = output.Count;
        var pageBreak = ReferenceEquals(_pageBreakFor, output);
        if (element.Name == W + "p" && PageFrame(element) is { } frame)
            ReadFramed(element, frame, output);
        else if (element.Name == W + "p")
            ReadParagraph(element, output);
        else if (element.Name == W + "tbl")
        {
            _tableDepth++;
            output.Add(ReadTable(element));
            _tableDepth--;
        }
        if (pageBreak && output.Count > count)
        {
            if (ReferenceEquals(_pageBreakFor, output))
                _pageBreakFor = null;
            output[count].BreakParagraphsBefore = _pageBreakMarks;
            _pageBreakMarks = 0;
            switch (output[count])
            {
                case HParagraph paragraph:
                    paragraph.PageBreakBefore = true;
                    break;
                case HTable table:
                    table.PageBreakBefore = true;
                    break;
            }
        }
    }

    // ───────────────────────── Frames ─────────────────────────

    /// <summary>The frame paragraphs are being read into, while the next paragraph with the same w:framePr joins it.</summary>
    private (XElement Properties, HTextBox Box, List<HBlock> Output, int Count)? _frame;

    /// <summary>
    /// The w:framePr of a paragraph Word sets in a frame of a given size at a place on the page or within the margins
    /// (a verification code at the bottom of a form); null for other paragraphs, drop caps and frames that move with
    /// the text, which stay in the text.
    /// </summary>
    private static XElement? PageFrame(XElement paragraph)
    {
        var frame = paragraph.Element(W + "pPr")?.Element(W + "framePr");
        if (frame is null || Attr(frame, "dropCap") is "drop" or "margin" || Attr(frame, "vAnchor") is not ("page" or "margin")
            || Twips(frame, "w") is not > 0 || Twips(frame, "h") is not (> 0 or < 0))
            return null;
        return frame;
    }

    /// <summary>
    /// A paragraph in a frame: a box without border or padding at the frame's place, floating at the paragraph before
    /// it (the frame takes no room in the text). Consecutive paragraphs with the same frame share the box.
    /// </summary>
    private void ReadFramed(XElement paragraph, XElement properties, List<HBlock> output)
    {
        HTextBox box;
        if (_frame is { } open && ReferenceEquals(open.Output, output) && open.Count == output.Count && XNode.DeepEquals(open.Properties, properties))
        {
            box = open.Box;
        }
        else
        {
            var height = Math.Abs(Twips(properties, "h") ?? 0);
            box = new TextBoxBuilder(HwpxUnits.FromTwips(Twips(properties, "w") ?? 0), HwpxUnits.FromTwips(height), FrameAnchor(properties))
            {
                Line = HBorder.None,
                Padding = new HInsets(0, 0, 0, 0),
            }.Build();
            if (output.LastOrDefault() is HParagraph host)
            {
                host.Inlines.Add(box);
            }
            else
            {
                var empty = new HParagraph();
                empty.Inlines.Add(box);
                output.Add(empty);
            }
        }
        _textBoxDepth++;
        try
        {
            ReadParagraph(paragraph, box.Blocks);
        }
        finally
        {
            _textBoxDepth--;
        }
        _frame = (properties, box, output, output.Count);
    }

    /// <summary>Where w:framePr puts its frame: x/xAlign from the page, margin or column, y/yAlign from the page or margin.</summary>
    private static HAnchor FrameAnchor(XElement frame) => new(
        HorizontalRelativeTo: Attr(frame, "hAnchor") switch
        {
            "page" => "PAPER",
            "margin" => "PAGE",
            _ => "COLUMN",
        },
        HorizontalAlign: Attr(frame, "xAlign") switch
        {
            "center" => "CENTER",
            "right" or "outside" => "RIGHT",
            _ => "LEFT",
        },
        HorizontalOffset: Attr(frame, "xAlign") is null ? HwpxUnits.FromTwips(Twips(frame, "x") ?? 0) : 0,
        VerticalRelativeTo: Attr(frame, "vAnchor") == "page" ? "PAPER" : "PAGE",
        VerticalAlign: Attr(frame, "yAlign") switch
        {
            "center" => "CENTER",
            "bottom" or "outside" => "BOTTOM",
            _ => "TOP",
        },
        VerticalOffset: Attr(frame, "yAlign") is null ? HwpxUnits.FromTwips(Twips(frame, "y") ?? 0) : 0,
        Wrap: Attr(frame, "wrap") switch
        {
            "through" => "IN_FRONT_OF_TEXT",
            "none" or "notBeside" => "TOP_AND_BOTTOM",
            _ => "SQUARE",
        },
        AllowOverlap: true);

    // ───────────────────────── Paragraphs ─────────────────────────

    /// <summary>
    /// Builds the paragraph(s) for one w:p. A page or column break inside the text starts a new 한글 paragraph
    /// (breaks are paragraph attributes there), so one Word paragraph can become several.
    /// </summary>
    private sealed class ParagraphBuilder
    {
        private readonly DocxReader _reader;
        private readonly List<HBlock> _output;
        private readonly HParaFormat _format;
        private readonly HCharFormat _mark;
        private readonly int _heading;
        private readonly HListRef? _list;
        private readonly ParaProps _props;
        private bool _pageBroken;
        private readonly Stack<List<HInline>> _sinks = new();

        public ParagraphBuilder(DocxReader reader, List<HBlock> output, ParaProps props, RunProps mark, XElement? pPr)
        {
            _reader = reader;
            _props = props;
            _output = output;
            _format = reader.ParaFormat(props, mark.Size);
            _mark = reader.CharFormat(mark);
            _heading = reader.HeadingLevel(props, pPr);
            var ilvl = Math.Clamp(props.Ilvl ?? 0, 0, 8);
            _list = reader.Numbering(props.NumId) is { } numbering
                ? new HListRef(numbering, ilvl, Numbered: true) { MarkerFormat = reader.MarkerFormat(props.NumId, ilvl, mark) }
                : null;
            Current = NewParagraph(_list);
            Current.PageBreakBefore = props.PageBreakBefore == true;

            // A hyperlink field that started in an earlier paragraph (Word's table of contents) continues here.
            if (reader._fields.LastOrDefault(f => f.Result && f.Target is not null) is { } field)
                OpenLink(field.Target!);
        }

        public HParagraph Current { get; private set; }

        public List<HInline> Sink => _sinks.Count > 0 ? _sinks.Peek() : Current.Inlines;

        public void Add(HInline inline) => Sink.Add(inline);

        public void OpenLink(string target)
        {
            // Links in links (fields can open any number) beyond the limit keep their text in the link around them.
            if (_sinks.Count >= Nesting.MaxDepth)
            {
                _sinks.Push(Sink);
                return;
            }
            var link = new HLink(target);
            Sink.Add(link);
            _sinks.Push(link.Content);
        }

        public void CloseLink()
        {
            if (_sinks.Count > 0)
                _sinks.Pop();
        }

        /// <summary>Inlines go into <paramref name="sink"/> (a deletion's content) until <see cref="CloseSink"/>.</summary>
        public void OpenSink(List<HInline> sink) => _sinks.Push(sink);

        /// <summary>Ends <paramref name="sink"/> and the links opened in it and left open.</summary>
        public void CloseSink(List<HInline> sink)
        {
            if (!_sinks.Contains(sink))
                return;
            while (_sinks.Count > 0 && !ReferenceEquals(_sinks.Pop(), sink))
            {
            }
        }

        /// <summary>Continues in a new paragraph that starts on a new page or column.</summary>
        public void Break(bool page)
        {
            if (Current.Inlines.Count > 0)
            {
                Emit();
                // The rest of a list item is not numbered again.
                Current = NewParagraph(_list is null ? null : _list with { Numbered = false });
                _sinks.Clear();
            }
            if (page)
                Current.PageBreakBefore = _pageBroken = true;
            else
                Current.ColumnBreakBefore = true;
        }

        /// <summary>Continues in a new paragraph right under this one (the next line of a long equation).</summary>
        public void NextRow()
        {
            var format = Current.Format;
            Current.Format = format with { After = 0 };
            Emit();
            Current = NewParagraph(_list is null ? null : _list with { Numbered = false });
            Current.Format = format with { Before = 0 };
            _sinks.Clear();
        }

        /// <param name="endsSection">The paragraph holds a section's properties (its section ends here).</param>
        public void Finish(bool endsSection = false)
        {
            // Word keeps the mark of a paragraph that ends right after a page break on the old page: the new page starts
            // with the next paragraph, not with an empty line and the paragraph's space after.
            if (_pageBroken && Current.Inlines.Count == 0 && Current.List is not { Numbered: true } && !endsSection)
            {
                if (!ReferenceEquals(_reader._pageBreakFor, _output))
                    _reader._pageBreakMarks = 0;
                _reader._pageBreakFor = _output;
                _reader._pageBreakMarks++;
                return;
            }
            Emit();
        }

        private void Emit()
        {
            FitLineSpacing(Current, _props);
            _reader.SnapToGrid(Current, _props);
            if (_output.LastOrDefault() is HParagraph previous)
            {
                JoinBorders(previous, Current);
                _reader.ApplyContextualSpacing(previous, Current, _props);
            }
            _reader.RememberParagraphStyle(Current, _props);
            _output.Add(Current);
        }

        private HParagraph NewParagraph(HListRef? list) =>
            new() { Format = _format, MarkFormat = _mark, HeadingLevel = _heading, List = list };
    }

    private void ReadParagraph(XElement p, List<HBlock> output)
    {
        _budget.Paragraph();
        var pPr = p.Element(W + "pPr");
        var (props, mark) = ParagraphProperties(pPr);
        var builder = new ParagraphBuilder(this, output, props, mark, pPr);
        // Run formatting starts from the paragraph style, not from the paragraph mark's direct formatting.
        var styleRun = ParagraphStyle(Val(pPr?.Element(W + "pStyle")) ?? _defaultParagraphStyle).Run;
        ParagraphContent(p.Elements(), builder, styleRun);
        builder.Finish(endsSection: pPr?.Element(W + "sectPr") is not null);
    }

    private void ParagraphContent(IEnumerable<XElement> elements, ParagraphBuilder builder, RunProps styleRun)
    {
        foreach (var element in elements)
        {
            var name = element.Name.LocalName;
            if (element.Name.Namespace == M)
            {
                if (name is "oMath" or "oMathPara" && _deletion is null)
                    Equations(element, builder, styleRun);
                continue;
            }
            if (element.Name.Namespace != W)
            {
                if (element.Name == Mc + "AlternateContent")
                    ParagraphContent(ChooseAlternative(element), builder, styleRun);
                continue;
            }
            switch (name)
            {
                case "r":
                    Run(element, builder, styleRun);
                    break;
                case "hyperlink":
                    {
                        var target = Attr(element, "id", R) is { } id && _part.Relationships.TryGetValue(id, out var rel) ? rel.Target
                            : Attr(element, "anchor") is { } anchor ? "#" + anchor
                            : null;
                        if (target is not null)
                            builder.OpenLink(target);
                        ParagraphContent(element.Elements(), builder, styleRun);
                        if (target is not null)
                            builder.CloseLink();
                        break;
                    }
                case "fldSimple":
                    {
                        var field = new FieldState();
                        field.Instruction.Append(Attr(element, "instr"));
                        _budget.Fields(_fields.Count + 1);
                        _fields.Add(field);
                        StartFieldResult(field, builder, CharFormat(styleRun));
                        ParagraphContent(element.Elements(), builder, styleRun);
                        EndField(builder);
                        break;
                    }
                case "bookmarkStart":
                    if (Attr(element, "name") is { Length: > 0 } bookmark && bookmark != "_GoBack")
                        builder.Add(new HBookmark(bookmark));
                    break;
                case "ins" or "moveTo":
                    Inserted(element, builder, styleRun);
                    break;
                case "del" or "moveFrom":
                    Deleted(element, builder, styleRun);
                    break;
                case "commentRangeStart":
                    if (_deletion is null && Comment(element) is { } comment)
                        builder.Add(new HCommentStart(comment));
                    break;
                case "smartTag" or "customXml":
                    ParagraphContent(element.Elements(), builder, styleRun);
                    break;
                case "dir" or "bdo":
                    {
                        // Text in an explicit direction: embedded (w:dir) or overridden (w:bdo) right to left or left
                        // to right, between marks the layout reads as the Unicode controls.
                        var rightToLeft = Val(element) == "rtl";
                        HDirectionMark? mark = (name, Val(element)) switch
                        {
                            (_, not ("rtl" or "ltr")) => null,
                            ("dir", _) => new HDirectionMark(rightToLeft ? HDirectionKind.RightToLeft : HDirectionKind.LeftToRight),
                            _ => new HDirectionMark(rightToLeft ? HDirectionKind.OverrideRightToLeft : HDirectionKind.OverrideLeftToRight),
                        };
                        if (mark is not null)
                            builder.Add(mark);
                        ParagraphContent(element.Elements(), builder, styleRun);
                        if (mark is not null)
                            builder.Add(new HDirectionMark(HDirectionKind.End));
                        break;
                    }
                case "sdt":
                    ParagraphContent(element.Element(W + "sdtContent")?.Elements() ?? [], builder, styleRun);
                    break;
                default:
                    break; // pPr, proofErr, permStart, commentRangeEnd ...: no visible content
            }
        }
    }

    private void Run(XElement r, ParagraphBuilder builder, RunProps styleRun)
    {
        var props = RunProperties(r.Element(W + "rPr"), styleRun);
        if (props.Hidden == true)
            return;
        var format = WithRunStyle(CharFormat(props), r.Element(W + "rPr"));

        foreach (var child in r.Elements())
        {
            if (child.Name == Mc + "AlternateContent")
            {
                foreach (var chosen in ChooseAlternative(child))
                    RunChild(chosen, builder, format);
                continue;
            }
            RunChild(child, builder, format);
        }
    }

    private void RunChild(XElement child, ParagraphBuilder builder, HCharFormat format)
    {
        if (child.Name.Namespace != W)
            return;
        // Deleted text keeps its text, tabs and line breaks; a page break or a note in it went with it.
        if (_deletion is not null && (!KeptInDeletion(child.Name.LocalName) || child.Name.LocalName == "br" && Attr(child, "type") is "page" or "column"))
            return;
        switch (child.Name.LocalName)
        {
            case "commentReference":
                if (_deletion is null && Comment(child) is { } comment)
                    builder.Add(comment);
                break;
            case "t" or "delText":
                if (child.Name.LocalName == "delText" && _deletion is null)
                    break; // deleted text outside a deletion
                if (ShowsText())
                {
                    // Text typed in Wingdings & co. ("þ" for ☑) becomes the Unicode characters it shows.
                    foreach (var text in SymbolFonts.Runs(child.Value, SymbolAlternate(format)))
                        builder.Add(text);
                }
                else if (NumberResult() is { } number && (number.Shown?.Length ?? 0) < 32)
                {
                    number.Shown += child.Value;
                    if (number.Stored is not null)
                        number.Stored += child.Value;
                }
                break;
            case "tab" or "ptab":
                if (ShowsText())
                    builder.Add(new HTab(format));
                break;
            case "br":
                if (!ShowsText())
                    break;
                switch (Attr(child, "type"))
                {
                    case "page":
                        builder.Break(page: true);
                        break;
                    case "column":
                        builder.Break(page: false);
                        break;
                    default:
                        builder.Add(new HLineBreak(format));
                        break;
                }
                break;
            case "cr":
                if (ShowsText())
                    builder.Add(new HLineBreak(format));
                break;
            case "noBreakHyphen":
                if (ShowsText())
                    builder.Add(new HText("-", format));
                break;
            case "sym":
                // Insert → Symbol: a code of the font in w:font (☑ is F052 in Wingdings 2), in Unicode where known.
                // The font stays with the run, for the layout to draw the code in where it is installed.
                if (ShowsText() && int.TryParse(Attr(child, "char"), System.Globalization.NumberStyles.HexNumber, null, out var code) && code is > 0 and <= 0xFFFF)
                {
                    var symbolFont = SymbolFont(Attr(child, "font"));
                    var codeText = ((char)code).ToString();
                    var text = SymbolFonts.Map(symbolFont, code) ?? BulletText(codeText);
                    builder.Add(new HText(text, symbolFont is { Length: > 0 } && text.Length == 1 ? SymbolFonts.Drawn(format, symbolFont, codeText) : format));
                }
                break;
            case "fldChar":
                FieldChar(child, builder, format);
                break;
            case "instrText" or "delInstrText":
                if (_fields.LastOrDefault() is { Result: false } field)
                    field.Instruction.Append(child.Value);
                break;
            case "drawing":
                if (ShowsText())
                {
                    foreach (var inline in Drawing(child))
                        builder.Add(inline);
                }
                break;
            case "pict" or "object":
                if (ShowsText())
                {
                    // An embedded (OLE) object is drawn from the picture Word saved with it, when there is one.
                    var pictured = false;
                    foreach (var inline in Vml(child, format))
                    {
                        pictured |= inline is HImage;
                        builder.Add(inline);
                    }
                    if (child.Element(O + "OLEObject") is not null)
                        _leftOut.Add(pictured ? HLeftOutKind.ObjectPictured : HLeftOutKind.ObjectLeftOut);
                    // An ActiveX control (w:control): only the picture Word saved of it is drawn.
                    else if (child.Element(W + "control") is not null)
                        _leftOut.Add(pictured ? HLeftOutKind.FormControlPictured : HLeftOutKind.FormControl);
                }
                break;
            case "footnoteReference":
                if (Note(child, _footnotes, _footnotesPart, endnote: false) is { } footnote)
                    builder.Add(footnote);
                break;
            case "endnoteReference":
                if (Note(child, _endnotes, _endnotesPart, endnote: true) is { } endnote)
                    builder.Add(endnote);
                break;
            case "footnoteRef" or "endnoteRef":
                // The note's own number, where its text has it: "(1)" keeps the number between its brackets.
                if (ShowsText())
                    builder.Add(new HNoteNumber(format));
                break;
            default:
                break; // separator, lastRenderedPageBreak, delText ...
        }
    }

    /// <summary>How the document's display equations break into lines (m:mathPr); null for Word's defaults.</summary>
    private HMathWrap? _mathWrap;

    /// <summary>
    /// Word's line breaking of display equations from the settings' m:mathPr: m:brkBin (before, after, repeat),
    /// m:brkBinSub (--, -+, +-), m:wrapIndent (twips) and m:wrapRight; null when the settings say nothing of them.
    /// </summary>
    internal static HMathWrap? MathWrap(XElement? mathPr)
    {
        if (mathPr is null)
            return null;
        string? Of(string name) => (string?)mathPr.Element(M + name)?.Attribute(M + "val");
        var (breaking, subtraction, indent, right) = (Of("brkBin"), Of("brkBinSub"), Of("wrapIndent"), mathPr.Element(M + "wrapRight"));
        if (breaking is null && subtraction is null && indent is null && right is null)
            return null;
        var wrap = new HMathWrap
        {
            Break = breaking switch
            {
                "after" => HMathBreak.After,
                "repeat" => HMathBreak.Repeat,
                _ => HMathBreak.Before,
            },
            Subtraction = subtraction is "--" or "-+" or "+-" ? subtraction : "--",
            Right = right is not null && (string?)right.Attribute(M + "val") is null or "1" or "on" or "true",
        };
        if (int.TryParse(indent, NumberStyles.Integer, CultureInfo.InvariantCulture, out var twips) && twips >= 0)
            wrap = wrap with { Indent = HwpxUnits.FromTwips(Math.Min(twips, 31_680)) };
        return wrap;
    }

    /// <summary>
    /// Word math (m:oMath, or m:oMathPara with one or more lines) as equations in the size of its runs. A display
    /// equation alone in its paragraph is aligned like Word shows it (centred unless m:jc says otherwise).
    /// </summary>
    private void Equations(XElement math, ParagraphBuilder builder, RunProps styleRun)
    {
        _budget.Object();
        var rPr = math.Descendants(M + "r").Select(r => r.Element(W + "rPr")).FirstOrDefault(p => p is not null);
        var format = CharFormat(RunProperties(rPr, styleRun)) with { Italic = true };
        var display = math.Name.LocalName == "oMathPara";
        var lines = display ? math.Elements(M + "oMath").ToList() : [math];

        if (math.Name.LocalName == "oMathPara" && math.Parent is { } paragraph
            && !paragraph.Descendants(W + "t").Any(t => t.Value.Trim().Length > 0 && !t.Ancestors(M + "oMathPara").Any()))
        {
            var align = Attr(math.Element(M + "oMathParaPr")?.Element(M + "jc"), "val", M) switch
            {
                "left" => HAlign.Left,
                "right" => HAlign.Right,
                _ => HAlign.Center,
            };
            builder.Current.Format = builder.Current.Format with { Align = align };
        }

        // Width of the line in the body; in a cell or a text box the width isn't known here.
        var inBody = _textBoxDepth == 0 && !math.Ancestors(W + "tc").Any() && _sectionIndex < _columnWidths.Count;
        var width = inBody ? _columnWidths[_sectionIndex] - (builder.Current.Format.Left ?? 0) - (builder.Current.Format.Right ?? 0) : 0;

        var first = true;
        foreach (var line in lines)
        {
            var equation = OmmlReader.Read(line);
            if (equation is MathRow { Items.Count: 0 })
                continue;
            if (!first)
                builder.Add(new HLineBreak(format));
            first = false;

            var (body, number) = OmmlReader.SplitNumber(equation);
            if (number is not null)
            {
                var text = new HText(EquationScript.Linear(number), format with { Italic = false });
                var numbered = new HEquation(body, format) { Display = display, Wrap = display ? _mathWrap : null };
                if (width > 0)
                {
                    builder.Add(NumberBox(numbered, text, width));
                    builder.Add(numbered);
                }
                else
                {
                    builder.Add(numbered);
                    builder.Add(new HText(" ", format));
                    builder.Add(text);
                }
                continue;
            }

            // Whole: the layout breaks a display equation too wide for its column the way Word does (with the
            // document's m:mathPr), and the HWPX writer breaks it into paragraphs for 한글, which doesn't.
            builder.Add(new HEquation(body, format) { Display = display, Wrap = _mathWrap });
        }
    }

    /// <summary>
    /// The number of a centred equation as a box at the right margin, level with the middle of the equation: rhwp's tabs
    /// don't count an equation's width, and it clips a tall equation in a table cell.
    /// </summary>
    private static HTextBox NumberBox(HEquation equation, HText number, int width)
    {
        var size = number.Format.Size ?? 1000;
        var boxWidth = Math.Min(width, (int)HwpxWriter.TextWidth(number.Text, size) + 400);
        var boxHeight = size * 3 / 2;
        // rhwp starts the paragraph a third of a tall equation's height above the equation.
        var middle = (int)EquationScript.Measure(equation.Math, equation.Format.Size ?? size).Height * 5 / 6;
        var box = new HTextBox
        {
            Width = boxWidth,
            Height = boxHeight,
            Line = HBorder.None,
            Padding = new HInsets(0, 0, 0, 0),
            Anchor = new HAnchor("COLUMN", "LEFT", width - boxWidth, "PARA", "TOP", Math.Max(0, middle - boxHeight / 2), "IN_FRONT_OF_TEXT", true),
            EquationNumber = true,
        };
        var paragraph = new HParagraph { Format = new HParaFormat(Align: HAlign.Right, Before: 0, After: 0) };
        paragraph.Inlines.Add(number);
        box.Blocks.Add(paragraph);
        return box;
    }

    private HNote? Note(XElement reference, Dictionary<string, XElement> notes, Part? part, bool endnote)
    {
        // A note referring to itself (or two notes to each other) would be read again and again.
        if (_inNote || part is null || !notes.TryGetValue(Attr(reference, "id") ?? "", out var element))
            return null;
        var note = new HNote(endnote);
        _inNote = true;
        // A note referenced from a table cell is its own story: the table's style doesn't reach it.
        var tableStyleLayer = _tableStyleLayer;
        _tableStyleLayer = null;
        using (_budget.Enter())
        {
            try
            {
                WithPart(part, () =>
                {
                    ReadNestedBlocks(element, note.Blocks);
                    return 0;
                });
            }
            finally
            {
                _inNote = false;
                _tableStyleLayer = tableStyleLayer;
            }
        }
        // Word notes start with the reference mark and a space; 한글 writes its own number, and so does the layout in
        // front of the text when the mark starts it.
        if (note.Blocks.FirstOrDefault() is HParagraph first && first.Inlines.FindIndex(i => i is not HNoteNumber) is var index and >= 0
            && first.Inlines[index] is HText { Text: var text } leading)
        {
            var trimmed = text.TrimStart();
            first.Inlines[index] = new HText(trimmed, leading.Format);
        }
        return note;
    }

    /// <summary>
    /// How a section's or cell's text runs (w:textDirection; the strict names too): top to bottom East Asian style, or
    /// bottom to top.
    /// </summary>
    private static HTextFlow TextFlow(XElement? properties) => Val(properties?.Element(W + "textDirection")) switch
    {
        "tbRl" or "tbRlV" or "tbLrV" or "tb" or "tbV" => HTextFlow.Vertical,
        "btLr" => HTextFlow.Up,
        _ => HTextFlow.Horizontal,
    };

    /// <summary>How a text box's text runs (wps:bodyPr vert), as DrawingML names it.</summary>
    internal static HTextFlow DrawingTextFlow(string? vert) => vert switch
    {
        "vert" => HTextFlow.Down,
        "vert270" => HTextFlow.Up,
        "eaVert" or "mongolianVert" => HTextFlow.Vertical,
        "wordArtVert" or "wordArtVertRtl" => HTextFlow.Stacked,
        _ => HTextFlow.Horizontal,
    };

    /// <summary>
    /// mc:AlternateContent: the Choice when it is DrawingML we understand (an Office 2016 chart when ChartReader reads its
    /// part; else its fallback picture is drawn and named), else the (VML) Fallback.
    /// </summary>
    private IEnumerable<XElement> ChooseAlternative(XElement alternate)
    {
        if (Charts.ChartReader.ChartExChoice(alternate) is { } chartEx)
        {
            if (chartEx.RelationshipId is { } id && _part.Relationships.TryGetValue(id, out var rel) && !rel.External
                && Charts.ChartReader.Read(LoadPart(rel.Target)?.Xml.Root, _chartColors ??= Slides.DrawingColors.FromTheme(_theme), Charts.ChartReader.ThemeFont(_theme)).Chart is not null)
                return chartEx.Choice.Elements();
            if (alternate.Element(Mc + "Fallback") is { } fallback)
            {
                _leftOut.Add(HLeftOutKind.ChartPictured);
                return fallback.Elements();
            }
        }
        var choice = alternate.Elements(Mc + "Choice").FirstOrDefault(c => (string?)c.Attribute("Requires") is "wps" or "wpg" or "wp14" or "w14" or "a14" or "wpc");
        return (choice ?? alternate.Element(Mc + "Fallback") ?? alternate.Elements(Mc + "Choice").FirstOrDefault())?.Elements() ?? [];
    }

    // ───────────────────────── Fields ─────────────────────────

    /// <summary>A complex field (w:fldChar begin … separate … end) that may span runs and paragraphs.</summary>
    private sealed class FieldState
    {
        public readonly StringBuilder Instruction = new();
        public bool Result;
        public bool Suppress;
        public string? Target;

        /// <summary>
        /// The page number field whose result text is kept as <see cref="HField.Shown"/>, and as
        /// <see cref="HField.Stored"/> where that isn't null.
        /// </summary>
        public HField? Number;

        /// <summary>A table of contents (TOC): its entries don't take the Hyperlink character style's look.</summary>
        public bool TableOfContents;

        /// <summary>A legacy form field's settings (w:ffData in the begin w:fldChar): a check box's state, a drop-down's entries.</summary>
        public XElement? FormData;
    }

    /// <summary>Text is in the entries of a table of contents.</summary>
    private bool InTableOfContents => _fields.Any(f => f.Result && f.TableOfContents);

    /// <summary>Field result text is shown unless we are inside an instruction or a field we replace (PAGE ...).</summary>
    private bool ShowsText() => _fields.All(f => f.Result && !f.Suppress);

    /// <summary>The page number whose result text is being read, when only that field hides it.</summary>
    private HField? NumberResult() =>
        _fields.Count > 0 && _fields[^1] is { Result: true, Number: { } number } && _fields.Take(_fields.Count - 1).All(f => f.Result && !f.Suppress)
            ? number
            : null;

    private void FieldChar(XElement fldChar, ParagraphBuilder builder, HCharFormat format)
    {
        switch (Attr(fldChar, "fldCharType"))
        {
            case "begin":
                // Every run looks at all open fields, so fields begun without an end must not pile up: past the
                // budget's limit the document is refused (the untracked count below is a backstop).
                _budget.Fields(_fields.Count + 1);
                if (_fields.Count >= Nesting.MaxDepth)
                    _untrackedFields++;
                else
                    _fields.Add(new FieldState { FormData = fldChar.Element(W + "ffData") });
                break;
            case "separate":
                if (_untrackedFields == 0 && _fields.LastOrDefault() is { Result: false } field)
                    StartFieldResult(field, builder, format);
                break;
            case "end":
                if (_untrackedFields > 0)
                {
                    _untrackedFields--;
                    break;
                }
                if (_fields.LastOrDefault() is { Result: false } withoutResult)
                    StartFieldResult(withoutResult, builder, format);
                EndField(builder);
                break;
        }
    }

    private void StartFieldResult(FieldState field, ParagraphBuilder builder, HCharFormat format)
    {
        field.Result = true;
        var words = SplitInstruction(field.Instruction.ToString());
        switch (words.FirstOrDefault()?.ToUpperInvariant())
        {
            case "PAGE" or "NUMPAGES" or "SECTIONPAGES" when _inFirstPageHeader && _tableDepth > 0:
                {
                    // rhwp leaves a page number in a table in a text box blank: the HWPX writer writes Word's result,
                    // which comes next, as text. The layout engine counts the pages, as for any other page number.
                    var number = new HField(words[0].Equals("PAGE", StringComparison.OrdinalIgnoreCase) ? HFieldKind.PageNumber : HFieldKind.TotalPages, format) { Stored = "" };
                    builder.Add(number);
                    field.Number = number;
                    field.Suppress = true;
                    break;
                }
            case "TOC":
                field.TableOfContents = true;
                break;
            case "HYPERLINK":
                {
                    // HYPERLINK "url" [\l "bookmark"] or HYPERLINK \l "bookmark"
                    string? url = null, anchor = null;
                    for (var i = 1; i < words.Count; i++)
                    {
                        if (words[i] == "\\l" && i + 1 < words.Count)
                            anchor = words[++i];
                        else if (!words[i].StartsWith('\\') && url is null)
                            url = words[i];
                        else if (words[i].StartsWith('\\') && i + 1 < words.Count && !words[i + 1].StartsWith('\\'))
                            i++; // other switch with an argument (\o "tooltip", \t "frame")
                    }
                    field.Target = anchor is not null ? (url ?? "") + "#" + anchor : url;
                    if (field.Target is not null)
                        builder.OpenLink(field.Target);
                    break;
                }
            case "PAGE" or "NUMPAGES" or "SECTIONPAGES":
                {
                    var number = new HField(words[0].Equals("PAGE", StringComparison.OrdinalIgnoreCase) ? HFieldKind.PageNumber : HFieldKind.TotalPages, format);
                    builder.Add(number);
                    field.Number = number;
                    field.Suppress = true;
                    break;
                }
            case "FORMCHECKBOX" or "FORMDROPDOWN" when ShowsText() && FormFieldText(field.FormData) is { } shown:
                // A legacy check box or drop-down has no result text: Word prints the box (checked or not) and the
                // chosen entry.
                builder.Add(new HText(shown, format));
                field.Suppress = true;
                break;
        }
    }

    /// <summary>
    /// What a legacy form field shows (w:ffData): a check box as ☒ or ☐ (w:checked, else its default), a drop-down as
    /// its chosen entry (w:result, else w:default, else the first); null for other fields.
    /// </summary>
    private static string? FormFieldText(XElement? formData)
    {
        if (formData?.Element(W + "checkBox") is { } checkBox)
        {
            var state = checkBox.Element(W + "checked") ?? checkBox.Element(W + "default");
            var on = state is not null && Val(state) is not ("0" or "false" or "off")
                     && (state.Name.LocalName == "checked" || Val(state) is "1" or "true" or "on");
            return on ? "☒" : "☐";
        }
        if (formData?.Element(W + "ddList") is { } list)
        {
            var entries = list.Elements(W + "listEntry").Select(Val).ToList();
            if (entries.Count == 0)
                return null;
            var chosen = int.TryParse(Val(list.Element(W + "result")) ?? Val(list.Element(W + "default")), out var index) ? index : 0;
            return entries[chosen >= 0 && chosen < entries.Count ? chosen : 0] ?? "";
        }
        return null;
    }

    private void EndField(ParagraphBuilder builder)
    {
        if (_fields.Count == 0)
            return;
        var field = _fields[^1];
        _fields.RemoveAt(_fields.Count - 1);
        if (field.Target is not null)
            builder.CloseLink();
    }

    /// <summary>Splits a field instruction into words, keeping quoted arguments together.</summary>
    private static List<string> SplitInstruction(string instruction)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var ch in instruction)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0)
                    words.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0)
            words.Add(current.ToString());
        return words;
    }
}
