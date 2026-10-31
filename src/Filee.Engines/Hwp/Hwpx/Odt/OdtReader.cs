// ODT (OpenDocument text, LibreOffice Writer) → HDocument, read directly from content.xml, styles.xml and meta.xml
// without Pandoc or LibreOffice: page layouts and master pages (one section per master page, with its headers and
// footers), named and automatic paragraph and text styles, headings, lists (bullets, numbers, nesting, continued
// lists), tables (spans, column widths, row heights, cell fills, borders and padding), text sections with columns,
// frames with pictures or text, simple shapes, footnotes and endnotes, links, bookmarks and page number fields.
//
// Split over partial files: this one walks the package and the text, OdtReader.Styles resolves formatting and
// OdtReader.Objects reads tables, frames and shapes. The package is read within DocumentLimits (Documents/
// DocumentPackage); encrypted (password-protected) packages are refused.

using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Documents;
using static Filee.Engines.Hwp.Hwpx.Odt.Odf;

namespace Filee.Engines.Hwp.Hwpx.Odt;

/// <summary>Reads an .odt file into the document model.</summary>
internal sealed partial class OdtReader
{
    /// <summary>Spaces one text:s may stand for (text:c); more is a damaged or hostile file.</summary>
    private const int MaxSpaces = 10_000;

    private readonly DocumentPackage _package;
    private readonly DocumentBudget _budget;
    private readonly string _mediaFolder;
    private readonly HDocument _document = new();
    private readonly Dictionary<string, Master> _masters = [];
    private readonly Dictionary<string, ListState> _listsById = [];
    private readonly Dictionary<string, ListState> _lastListByStyle = [];
    private readonly Dictionary<HNumbering, (int Left, int FirstLine)?[]> _listIndents = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(HNumbering, int)> _startedLevels = [];
    private HNoteShape _footnotes = HNoteShape.Footnote with { Format = new HNoteFormat("DIGIT", "", "", false) };
    private HNoteShape _endnotes = HNoteShape.Endnote with { Format = new HNoteFormat("ROMAN_SMALL", "", "", false) };

    private HSection? _section;
    private string? _masterName;
    private HColumns _columns = HColumns.Single;
    private HColumns? _pendingColumns;
    private bool _breakAfter;
    private bool _inNote;
    private int _cells;

    private OdtReader(DocumentPackage package, string mediaFolder)
    {
        _package = package;
        _budget = new DocumentBudget(package.Limits);
        _mediaFolder = mediaFolder;
    }

    /// <summary>Reads <paramref name="path"/>; embedded pictures are extracted into <paramref name="mediaFolder"/>.</summary>
    public static HDocument Read(string path, string mediaFolder) => Read(path, mediaFolder, DocumentLimits.Default);

    /// <summary>Reads within other <paramref name="limits"/> than <see cref="DocumentLimits.Default"/> (for tests).</summary>
    internal static HDocument Read(string path, string mediaFolder, DocumentLimits limits)
    {
        using var package = DocumentPackage.Open(path, limits,
            () => new InvalidDataException("The file is not an OpenDocument text (it is not a ZIP package). It may be damaged."));
        return new OdtReader(package, mediaFolder).ReadDocument();
    }

    // ───────────────────────── Package ─────────────────────────

    /// <summary>An XML part, or null when the package has none; damaged XML is invalid data with the part's name.</summary>
    private XDocument? Load(string name)
    {
        if (_package.GetEntry(name) is not { } entry)
            return null;
        try
        {
            return _package.LoadXml(entry, preserveWhitespace: true);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The OpenDocument text cannot be read ({name} is not valid XML). It may be encrypted or damaged. {ex.Message}", ex);
        }
    }

    private void CheckPackage()
    {
        if (_package.GetEntry("mimetype") is { } mimetype && _package.ReadBytes(mimetype, 256) is { } bytes)
        {
            var type = System.Text.Encoding.ASCII.GetString(bytes).Trim();
            if (type.Length > 0 && type is not (MimeType or TemplateMimeType))
                throw new InvalidDataException($"The file is not an OpenDocument text ({type}).");
        }
        // Encrypted packages keep a readable manifest that says how each part is encrypted; the parts are not XML.
        if (Load("META-INF/manifest.xml")?.Descendants(Manifest + "encryption-data").Any() == true)
            throw new InvalidDataException("The document is protected with a password (encrypted). Open it in LibreOffice, remove the password (File → Properties → Security), save it and convert it again.");
    }

    private HDocument ReadDocument()
    {
        CheckPackage();
        var content = Load("content.xml") ?? throw new InvalidDataException("The file is not an OpenDocument text (no content.xml).");
        var text = content.Root?.Element(Office + "body")?.Element(Office + "text")
                   ?? throw new InvalidDataException("The file is not an OpenDocument text (no office:text).");
        var styles = Load("styles.xml");
        LoadStyles(styles, content);
        NotesConfiguration(styles?.Root?.Element(Office + "styles"));
        _document.Title = Load("meta.xml")?.Root?.Element(Office + "meta")?.Element(Dc + "title")?.Value is { } title && title.Trim().Length > 0 ? title.Trim() : null;

        var body = new Sink([], body: true);
        ReadBlocks(text.Elements(), body);
        Flush(body);
        if (_section is null)
            StartSection(null, null, body);
        return _document;
    }

    private void NotesConfiguration(XElement? styles)
    {
        foreach (var configuration in styles?.Elements(Text + "notes-configuration") ?? [])
        {
            var endnote = Attr(configuration, Text + "note-class") == "endnote";
            var shape = endnote ? _endnotes : _footnotes;
            var format = new HNoteFormat(NumberType(Attr(configuration, Style + "num-format") ?? (endnote ? "i" : "1")) ?? "DIGIT",
                Attr(configuration, Style + "num-prefix") ?? "", Attr(configuration, Style + "num-suffix") ?? "", false);
            if (endnote)
                _endnotes = shape with { Format = format };
            else
                _footnotes = shape with { Format = format };
        }
    }

    // ───────────────────────── Sections and master pages ─────────────────────────

    /// <summary>A master page: its page, columns, headers and footers.</summary>
    private sealed record Master(HPage? Page, HColumns Columns, List<HHeaderFooter> Headers, List<HHeaderFooter> Footers, bool HideFirstHeader, bool HideFirstFooter);

    /// <summary>Where blocks go: the body (whose list moves to each new section) or a cell, note, frame or header.</summary>
    private sealed class Sink(List<HBlock> blocks, bool body)
    {
        public List<HBlock> Blocks { get; set; } = blocks;
        public bool Body { get; } = body;

        /// <summary>Objects anchored outside a paragraph (to the page), put at the start of the next paragraph.</summary>
        public List<HInline> Pending { get; } = [];
    }

    /// <summary>
    /// A block of the body starts with <paramref name="masterPage"/> (from its style): the first one opens the first
    /// section, a different master page (or a page number restart) a new section, the same one a page break.
    /// </summary>
    /// <returns>True when the block starts a new page within the same section.</returns>
    private bool BodyBlock(string? masterPage, string? pageNumber, Sink sink)
    {
        var restart = Int(pageNumber) is { } number ? Math.Clamp(number, 0, 1_000_000) : (int?)null;
        if (_section is null)
        {
            StartSection(masterPage, restart, sink);
            return false;
        }
        if (masterPage is null)
            return false;
        if (masterPage == _masterName && restart is null)
            return true;
        if (_section.Blocks.Count == 0)
        {
            _document.Sections.Remove(_section);
            _section = null;
        }
        StartSection(masterPage, restart, sink);
        return false;
    }

    private void StartSection(string? masterPage, int? startPage, Sink sink)
    {
        var name = masterPage is not null && _masterPages.ContainsKey(masterPage) ? masterPage
            : _masterPages.ContainsKey("Standard") ? "Standard"
            : Attr(_firstMasterPage, Style + "name");
        var master = name is null ? new Master(null, HColumns.Single, [], [], false, false) : MasterOf(name);
        var section = new HSection
        {
            Page = master.Page,
            Columns = master.Columns,
            HideFirstHeader = master.HideFirstHeader,
            HideFirstFooter = master.HideFirstFooter,
            StartPageNumber = startPage,
            Footnotes = _footnotes,
            Endnotes = _endnotes,
        };
        section.Headers.AddRange(master.Headers);
        section.Footers.AddRange(master.Footers);
        _document.Sections.Add(section);
        _section = section;
        _masterName = name;
        _columns = section.Columns;
        _pendingColumns = null;
        sink.Blocks = section.Blocks;
    }

    private Master MasterOf(string name)
    {
        if (_masters.TryGetValue(name, out var cached))
            return cached;
        var element = _masterPages[name];
        var layout = _pageLayouts.GetValueOrDefault(Attr(element, Style + "page-layout-name") ?? "");

        bool Shown(XElement? part) => part is not null && Attr(part, Style + "display") != "false";
        var header = element.Element(Style + "header");
        var headerLeft = element.Element(Style + "header-left");
        var headerFirst = element.Element(Style + "header-first");
        var footer = element.Element(Style + "footer");
        var footerLeft = element.Element(Style + "footer-left");
        var footerFirst = element.Element(Style + "footer-first");
        var hasHeader = Shown(header) || Shown(headerLeft);
        var hasFooter = Shown(footer) || Shown(footerLeft);

        var previous = _scope;
        _scope = Scope.Styles;
        try
        {
            var headers = HeadersAndFooters(Shown(header) ? header : null, Shown(headerLeft) ? headerLeft : null);
            var footers = HeadersAndFooters(Shown(footer) ? footer : null, Shown(footerLeft) ? footerLeft : null);
            var master = new Master(PageOf(layout, hasHeader, hasFooter), ColumnsOf(layout?.Element(Style + "page-layout-properties")),
                headers, footers,
                hasHeader && Shown(headerFirst) && IsBlank(Blocks(headerFirst!)),
                hasFooter && Shown(footerFirst) && IsBlank(Blocks(footerFirst!)));
            return _masters[name] = master;
        }
        finally
        {
            _scope = previous;
        }
    }

    /// <summary>Headers or footers of a master page: one for every page, or the right ones (odd) and the left ones (even).</summary>
    private List<HHeaderFooter> HeadersAndFooters(XElement? all, XElement? left)
    {
        var result = new List<HHeaderFooter>();
        void Add(HPageType pages, XElement element)
        {
            var item = new HHeaderFooter(pages);
            item.Blocks.AddRange(Blocks(element));
            result.Add(item);
        }
        if (left is null)
        {
            if (all is not null)
                Add(HPageType.Both, all);
            return result;
        }
        if (all is not null)
            Add(HPageType.Odd, all);
        Add(HPageType.Even, left);
        return result;
    }

    private List<HBlock> Blocks(XElement container)
    {
        var sink = new Sink([], body: false);
        ReadBlocks(container.Elements(), sink);
        Flush(sink);
        return sink.Blocks;
    }

    private static bool IsBlank(List<HBlock> blocks) =>
        blocks.All(b => b is HParagraph p && p.Inlines.All(i => i is HText t && string.IsNullOrWhiteSpace(t.Text) || i is HBookmark));

    /// <summary>
    /// The page of a page layout. ODF puts the header inside the top margin's page area: the margin reaches the header,
    /// whose height and spacing come before the body, as the model has it (Top = edge → header, Header = its area).
    /// </summary>
    private static HPage? PageOf(XElement? layout, bool hasHeader, bool hasFooter)
    {
        var props = layout?.Element(Style + "page-layout-properties");
        if (props is null)
            return null;
        int Get(string name, int fallback)
        {
            var value = Length(Attr(props, Fo + name)) ?? Length(Attr(props, Fo + "margin")) ?? fallback;
            return Math.Clamp(value, 0, 10_000_000);
        }
        var width = Math.Clamp(Length(Attr(props, Fo + "page-width")) ?? 59528, 1000, 10_000_000);
        var height = Math.Clamp(Length(Attr(props, Fo + "page-height")) ?? 84188, 1000, 10_000_000);
        if (Attr(props, Style + "print-orientation") == "landscape" && width < height)
            (width, height) = (height, width);

        static int Area(XElement? style, string spacing)
        {
            var area = style?.Element(Style + "header-footer-properties");
            var minimum = Length(Attr(area, Fo + "min-height")) ?? Length(Attr(area, Svg + "height")) ?? 0;
            return Math.Clamp(minimum, 0, 10_000_000) + Math.Clamp(Length(Attr(area, Fo + spacing)) ?? 0, 0, 10_000_000);
        }
        return new HPage(width, height,
            Left: Get("margin-left", 7200),
            Right: Get("margin-right", 7200),
            Top: Get("margin-top", 7200),
            Bottom: Get("margin-bottom", 7200),
            Header: hasHeader ? Area(layout!.Element(Style + "header-style"), "margin-bottom") : 0,
            Footer: hasFooter ? Area(layout!.Element(Style + "footer-style"), "margin-top") : 0,
            Gutter: 0);
    }

    /// <summary>Newspaper columns of page layout or section properties (style:columns).</summary>
    private static HColumns ColumnsOf(XElement? props)
    {
        var columns = props?.Element(Style + "columns");
        var count = Int(Attr(columns, Fo + "column-count")) ?? 1;
        if (columns is null || count <= 1)
            return HColumns.Single;
        var gap = Length(Attr(columns, Fo + "column-gap"));
        if (gap is null)
        {
            // Columns of their own widths: the gap is the space either side of the first column boundary.
            var first = columns.Elements(Style + "column").Take(2).ToList();
            gap = first.Count == 2 ? (Length(Attr(first[0], Fo + "end-indent")) ?? 0) + (Length(Attr(first[1], Fo + "start-indent")) ?? 0) : 0;
        }
        var separator = columns.Element(Style + "column-sep") is { } sep && Attr(sep, Style + "style") != "none"
                        && (Length(Attr(sep, Style + "width")) ?? 1) > 0;
        return new HColumns(Math.Min(count, 255), Math.Clamp(gap.Value, 0, 1_000_000), separator);
    }

    // ───────────────────────── Blocks ─────────────────────────

    private void ReadBlocks(IEnumerable<XElement> elements, Sink sink)
    {
        foreach (var element in elements)
        {
            var name = element.Name;
            if (name == Text + "p" || name == Text + "h")
            {
                ReadParagraph(element, sink, null);
            }
            else if (name == Text + "list")
            {
                ReadList(element, sink, 0, null);
            }
            else if (name == Text + "numbered-paragraph")
            {
                ReadNumberedParagraph(element, sink);
            }
            else if (name == Table + "table")
            {
                ReadTable(element, sink);
            }
            else if (name == Text + "section")
            {
                ReadSection(element, sink);
            }
            else if (name.Namespace == Text && name.LocalName is "table-of-content" or "illustration-index" or "table-index" or "object-index"
                         or "user-index" or "alphabetical-index" or "bibliography")
            {
                // An index keeps the text it showed when it was last updated.
                using var level = _budget.Enter();
                ReadBlocks(element.Element(Text + "index-body")?.Elements() ?? [], sink);
            }
            else if (name == Text + "index-title")
            {
                using var level = _budget.Enter();
                ReadBlocks(element.Elements(), sink);
            }
            else if (name.Namespace == Draw)
            {
                // Objects anchored to the page are children of the text itself: they go with the next paragraph.
                sink.Pending.AddRange(Objects(element, new TextProps(), null));
            }
            // Otherwise: declarations, forms, tracked changes (the deleted text), soft page breaks: nothing to show.
        }
    }

    /// <summary>Puts objects still waiting for a paragraph into one of their own.</summary>
    private void Flush(Sink sink)
    {
        if (sink.Pending.Count == 0)
            return;
        if (sink.Body && _section is null)
            StartSection(null, null, sink);
        Add(sink, EmptyParagraph());
    }

    /// <summary>A paragraph with the default style's format, for objects and column changes that need one.</summary>
    private HParagraph EmptyParagraph()
    {
        var style = ParagraphStyleOf(null);
        return new HParagraph { Format = ParaFormat(style, null), MarkFormat = CharFormat(style.Text) };
    }

    private void Add(Sink sink, HBlock block)
    {
        if (sink.Body && _pendingColumns is { } columns)
        {
            _pendingColumns = null;
            if (block is HParagraph { ColumnsChange: null } first)
                first.ColumnsChange = columns;
            else
            {
                var empty = EmptyParagraph();
                empty.ColumnsChange = columns;
                sink.Blocks.Add(empty);
            }
        }
        if (sink.Pending.Count > 0)
        {
            if (block is not HParagraph target)
                sink.Blocks.Add(target = EmptyParagraph());
            target.Inlines.InsertRange(0, sink.Pending);
            sink.Pending.Clear();
        }
        sink.Blocks.Add(block);
    }

    /// <summary>A text:section: its columns (when it has its own) change the page's for its content.</summary>
    private void ReadSection(XElement section, Sink sink)
    {
        if (Attr(section, Text + "display") == "none")
            return;
        var columns = ColumnsOf(Find("section", Attr(section, Text + "style-name"))?.Element(Style + "section-properties"));
        if (!sink.Body || columns.Count <= 1 || columns == _columns)
        {
            ReadBlocks(section.Elements(), sink);
            return;
        }
        var outer = _columns;
        _pendingColumns = columns;
        _columns = columns;
        var sectionStart = _section;
        ReadBlocks(section.Elements(), sink);
        if (_section != sectionStart)
            return; // a new page style inside the section started a section with the page's columns
        _pendingColumns = _pendingColumns == columns ? null : outer;
        _columns = outer;
    }

    // ───────────────────────── Paragraphs ─────────────────────────

    private void ReadParagraph(XElement element, Sink sink, HListRef? list)
    {
        _budget.Paragraph();
        var style = ParagraphStyleOf(Attr(element, Text + "style-name"));
        var newPage = sink.Body && BodyBlock(style.MasterPage, style.PageNumber, sink);
        var heading = element.Name == Text + "h"
            ? Math.Clamp(Int(Attr(element, Text + "outline-level")) ?? style.OutlineLevel ?? 1, 1, 9)
            : 0;
        var paragraph = new HParagraph
        {
            Format = ParaFormat(style, list is null ? null : ListIndent(list)),
            MarkFormat = CharFormat(style.Text),
            HeadingLevel = heading,
            List = list,
            PageBreakBefore = style.Para.BreakBefore == "page" || newPage || _breakAfter && sink.Body,
            ColumnBreakBefore = style.Para.BreakBefore == "column",
        };
        if (sink.Body)
            _breakAfter = style.Para.BreakAfter == "page";

        var state = new TextState();
        Inlines(element, style.Text, paragraph.Inlines, state);
        Merge(paragraph.Inlines);
        Add(sink, paragraph);
    }

    /// <summary>White space as ODF collapses it: runs of spaces, tabs and line feeds become one space, none at the start.</summary>
    private sealed class TextState
    {
        public bool Space { get; set; } = true;
    }

    private void Inlines(XElement container, TextProps text, List<HInline> output, TextState state)
    {
        if (text.Hidden)
            return;
        foreach (var node in container.Nodes())
        {
            if (node is XText xmlText)
            {
                AddText(output, xmlText.Value, text, state);
                continue;
            }
            if (node is not XElement element)
                continue;
            var name = element.Name;
            if (name.Namespace == Draw)
            {
                output.AddRange(Objects(element, text, null));
                state.Space = false;
                continue;
            }
            if (name.Namespace == Office)
                continue; // annotations (comments) and their ends
            if (name.Namespace != Text)
            {
                Inlines(element, text, output, state); // extensions: their text
                continue;
            }
            switch (name.LocalName)
            {
                case "s":
                    output.Add(new HText(new string(' ', Math.Clamp(Int(Attr(element, Text + "c")) ?? 1, 1, MaxSpaces)), CharFormat(text)));
                    state.Space = false;
                    break;
                case "tab":
                    output.Add(new HTab(CharFormat(text)));
                    state.Space = false;
                    break;
                case "line-break":
                    output.Add(new HLineBreak(CharFormat(text)));
                    state.Space = false;
                    break;
                case "span":
                    Inlines(element, SpanProps(text, Attr(element, Text + "style-name")), output, state);
                    break;
                case "a":
                    {
                        var inner = SpanProps(text, Attr(element, Text + "style-name"));
                        if (Attr(element, XLink + "href") is { Length: > 0 } href)
                        {
                            var link = new HLink(href);
                            Inlines(element, inner, link.Content, state);
                            output.Add(link);
                        }
                        else
                        {
                            Inlines(element, inner, output, state);
                        }
                        break;
                    }
                case "bookmark" or "bookmark-start":
                    if (Attr(element, Text + "name") is { Length: > 0 } bookmark)
                        output.Add(new HBookmark(bookmark));
                    break;
                case "note":
                    if (Note(element, text) is { } note)
                        output.Add(note);
                    state.Space = false;
                    break;
                case "page-number":
                    output.Add(new HField(HFieldKind.PageNumber, CharFormat(text)));
                    state.Space = false;
                    break;
                case "page-count":
                    output.Add(new HField(HFieldKind.TotalPages, CharFormat(text)));
                    state.Space = false;
                    break;
                case "ruby":
                    Inlines(element.Element(Text + "ruby-base") ?? new XElement("none"), text, output, state);
                    break;
                case "bookmark-end" or "soft-page-break" or "change" or "change-start" or "change-end" or "reference-mark"
                    or "reference-mark-start" or "reference-mark-end" or "toc-mark" or "toc-mark-start" or "toc-mark-end"
                    or "alphabetical-index-mark" or "alphabetical-index-mark-start" or "alphabetical-index-mark-end"
                    or "user-index-mark" or "user-index-mark-start" or "user-index-mark-end" or "ruby-text":
                    break;
                default:
                    // Fields (dates, titles, sequence numbers, references ...) and text:meta: the text they show.
                    Inlines(element, text, output, state);
                    break;
            }
        }
    }

    private static void AddText(List<HInline> output, string value, TextProps text, TextState state)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is ' ' or '\t' or '\r' or '\n')
            {
                if (!state.Space)
                    sb.Append(' ');
                state.Space = true;
            }
            else
            {
                sb.Append(ch);
                state.Space = false;
            }
        }
        if (sb.Length > 0)
            output.Add(new HText(sb.ToString(), CharFormat(text)));
    }

    /// <summary>Joins neighbouring text of one format (ODF splits it at spaces, spans and soft page breaks).</summary>
    private static void Merge(List<HInline> inlines)
    {
        for (var i = 0; i < inlines.Count; i++)
        {
            if (inlines[i] is HLink link)
            {
                Merge(link.Content);
                continue;
            }
            if (inlines[i] is not HText text || text is HEquation)
                continue;
            if (text.Text.Length == 0)
            {
                inlines.RemoveAt(i--);
                continue;
            }
            if (i > 0 && inlines[i - 1] is HText previous and not HEquation && previous.Format.SameLayout(text.Format))
            {
                inlines[i - 1] = new HText(previous.Text + text.Text, previous.Format);
                inlines.RemoveAt(i--);
            }
        }
    }

    private HNote? Note(XElement element, TextProps text)
    {
        // ODF has no notes in notes; a note that held one would be read again and again.
        if (_inNote || element.Element(Text + "note-body") is not { } body)
            return null;
        var note = new HNote(Attr(element, Text + "note-class") == "endnote") { Format = CharFormat(text) };
        using var level = _budget.Enter();
        _inNote = true;
        try
        {
            var sink = new Sink(note.Blocks, body: false);
            ReadBlocks(body.Elements(), sink);
            Flush(sink);
        }
        finally
        {
            _inNote = false;
        }
        return note;
    }

    // ───────────────────────── Lists ─────────────────────────

    /// <summary>One list as it numbers: continued lists (text:continue-list, text:continue-numbering) share it.</summary>
    private sealed record ListState(HNumbering Numbering, string? StyleName);

    private void ReadList(XElement list, Sink sink, int level, ListState? outer)
    {
        using var nesting = _budget.Enter();
        var state = outer ?? TopLevelList(list);
        var modelLevel = Math.Min(level, 8);
        foreach (var child in list.Elements())
        {
            if (child.Name == Text + "list-header")
            {
                ListItem(child, sink, level, state, numbered: false);
            }
            else if (child.Name == Text + "list-item")
            {
                if (Int(Attr(child, Text + "start-value")) is { } start && _startedLevels.Add((state.Numbering, modelLevel)))
                {
                    var levels = state.Numbering.Levels;
                    if (modelLevel < levels.Count)
                        levels[modelLevel] = levels[modelLevel] with { Start = Math.Clamp(start, 0, 1_000_000) };
                }
                ListItem(child, sink, level, state, numbered: true);
            }
        }
    }

    private void ListItem(XElement item, Sink sink, int level, ListState state, bool numbered)
    {
        var first = numbered;
        foreach (var element in item.Elements())
        {
            if (element.Name == Text + "p" || element.Name == Text + "h")
            {
                if (first)
                    _startedLevels.Add((state.Numbering, Math.Min(level, 8)));
                ReadParagraph(element, sink, new HListRef(state.Numbering, Math.Min(level, 8), first));
                first = false;
            }
            else if (element.Name == Text + "list")
            {
                ReadList(element, sink, level + 1, state);
                first = false;
            }
            else
            {
                ReadBlocks([element], sink);
            }
        }
    }

    /// <summary>text:numbered-paragraph: a paragraph of a list, given by its level and list id.</summary>
    private void ReadNumberedParagraph(XElement element, Sink sink)
    {
        var id = Attr(element, Text + "list-id") ?? "";
        if (!_listsById.TryGetValue(id, out var state))
            _listsById[id] = state = NewList(Attr(element, Text + "style-name") ?? FirstParagraphListStyle(element));
        var level = Math.Clamp((Int(Attr(element, Text + "level")) ?? 1) - 1, 0, 8);
        foreach (var paragraph in element.Elements().Where(e => e.Name == Text + "p" || e.Name == Text + "h"))
            ReadParagraph(paragraph, sink, new HListRef(state.Numbering, level, Numbered: true));
    }

    private ListState TopLevelList(XElement list)
    {
        var styleName = Attr(list, Text + "style-name");
        ListState? state = null;
        if (Attr(list, Text + "continue-list") is { } continued)
            state = _listsById.GetValueOrDefault(continued);
        styleName ??= state?.StyleName ?? FirstParagraphListStyle(list);
        if (state is null && Attr(list, Text + "continue-numbering") == "true")
            state = _lastListByStyle.GetValueOrDefault(styleName ?? "");
        state ??= NewList(styleName);
        if (Attr(list, XNamespace.Xml + "id") is { } id)
            _listsById[id] = state;
        _lastListByStyle[styleName ?? ""] = state;
        return state;
    }

    /// <summary>The list style a list takes from its first paragraph's style when it names none itself.</summary>
    private string? FirstParagraphListStyle(XElement list)
    {
        var paragraph = list.Descendants().FirstOrDefault(e => e.Name == Text + "p" || e.Name == Text + "h");
        return paragraph is null ? null : ParagraphStyleOf(Attr(paragraph, Text + "style-name")).ListStyle;
    }

    private ListState NewList(string? styleName)
    {
        var numbering = new HNumbering();
        var indents = new (int, int)?[9];
        var style = FindListStyle(styleName);
        for (var level = 1; level <= 9; level++)
        {
            var definition = style?.Elements().FirstOrDefault(e => Int(Attr(e, Text + "level")) == level);
            numbering.Levels.Add(NumberingLevel(definition, level));
            indents[level - 1] = LevelIndent(definition);
        }
        _listIndents[numbering] = indents;
        return new ListState(numbering, styleName);
    }

    private static HNumberingLevel NumberingLevel(XElement? definition, int level)
    {
        if (definition is null)
            return new HNumberingLevel("DIGIT", "•", 1, Bullet: true) { AutoIndent = true };
        if (definition.Name == Text + "list-level-style-number")
        {
            var prefix = Attr(definition, Style + "num-prefix") ?? "";
            var suffix = Attr(definition, Style + "num-suffix") ?? "";
            var start = Math.Clamp(Int(Attr(definition, Text + "start-value")) ?? 1, 0, 1_000_000);
            var type = NumberType(Attr(definition, Style + "num-format"));
            if (type is null)
                return new HNumberingLevel("DIGIT", prefix + suffix, start, Bullet: false) { AutoIndent = true };
            var shown = Math.Clamp(Int(Attr(definition, Text + "display-levels")) ?? 1, 1, level);
            var numbers = string.Join('.', Enumerable.Range(level - shown + 1, shown).Select(l => $"^{l}"));
            return new HNumberingLevel(type, prefix + numbers + suffix, start, Bullet: false) { AutoIndent = true };
        }
        var bullet = definition.Name == Text + "list-level-style-bullet" ? Attr(definition, Text + "bullet-char") : null;
        return new HNumberingLevel("DIGIT", bullet is { Length: > 0 } ? bullet : "•", 1, Bullet: true) { AutoIndent = true };
    }

    /// <summary>The indent a list level gives its paragraphs: their left margin and first line (negative: hanging).</summary>
    private static (int Left, int FirstLine)? LevelIndent(XElement? definition)
    {
        var props = definition?.Element(Style + "list-level-properties");
        if (props is null)
            return null;
        if (Attr(props, Text + "list-level-position-and-space-mode") == "label-alignment")
        {
            var alignment = props.Element(Style + "list-level-label-alignment");
            return (Length(Attr(alignment, Fo + "margin-left")) ?? 0, Length(Attr(alignment, Fo + "text-indent")) ?? 0);
        }
        // The older mode: the label starts after space-before and takes min-label-width.
        var before = Length(Attr(props, Text + "space-before")) ?? 0;
        var width = Length(Attr(props, Text + "min-label-width")) ?? 0;
        return (before + width, -width);
    }

    private (int Left, int FirstLine)? ListIndent(HListRef list) =>
        _listIndents.TryGetValue(list.Numbering, out var indents) && list.Level < indents.Length ? indents[list.Level] : null;
}
