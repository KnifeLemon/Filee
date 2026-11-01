// DocBook (.dbk, .docbook; DocBook 5 and the element names of 4.x) → HDocument, without Pandoc:
//  * book, article, chapter, appendix, preface, part, section, sect1–sect5, simplesect and refsect: headings one
//    level deeper per section; the root's title is the document's title; bridgehead is a heading of its own;
//  * para, simpara, formalpara, blockquote, programlisting / screen / synopsis (code), literallayout (lines),
//    itemizedlist / orderedlist (numeration, startingnumber), variablelist, simplelist, the admonitions (note,
//    warning, tip, important, caution), example, figure, sidebar, epigraph, procedure, equation;
//  * tables in CALS (tgroup, colspec, namest / nameend spans, morerows) and HTML form (tr, td, th, colspan, rowspan);
//  * mediaobject / inlinemediaobject pictures (imagedata fileref, local files only), footnotes, link (xlink:href,
//    linkend), ulink, xref, anchor, and the inline elements: emphasis (role bold / strong / underline /
//    strikethrough), literal and the code-like ones, superscript, subscript, quote, citetitle, firstterm, trademark.
// Unknown elements keep their text. The XML goes through SafeXml: depth and element limits, and no DTD is ever
// processed. A DocBook 4 document's DOCTYPE pointing at the OASIS DTD is left out before the XML is read; one with an
// internal subset (entity declarations) is refused, and so is any entity the DTD would have had to define.

using System.Text;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Reads DocBook into the document model.</summary>
internal sealed class DocBookReader
{
    private static readonly HCharFormat Plain = default;
    private const string XLink = "http://www.w3.org/1999/xlink";

    private static readonly HashSet<string> Sections =
    [
        "book", "article", "chapter", "appendix", "preface", "part", "section", "sect1", "sect2", "sect3", "sect4", "sect5",
        "simplesect", "refentry", "refsection", "refsect1", "refsect2", "refsect3", "refsynopsisdiv", "colophon", "dedication",
        "acknowledgements", "glossary", "bibliography", "index", "setindex", "reference", "topic", "partintro",
    ];

    private static readonly HashSet<string> Code = ["programlisting", "screen", "synopsis", "funcsynopsisinfo", "classsynopsisinfo", "address"];

    private static readonly HashSet<string> Admonitions = ["note", "warning", "tip", "important", "caution", "danger"];

    private static readonly HashSet<string> Skipped =
    [
        "info", "articleinfo", "bookinfo", "chapterinfo", "sectioninfo", "sect1info", "sect2info", "sect3info", "prefaceinfo", "appendixinfo",
        "title", "subtitle", "titleabbrev", "indexterm", "remark", "toc", "lot", "annotation", "alt", "biblioid", "keywordset", "subjectset",
    ];

    /// <summary>Elements that are blocks wherever they stand (in a para, they split it).</summary>
    private static readonly HashSet<string> BlockElements =
    [
        "para", "simpara", "formalpara", "blockquote", "programlisting", "screen", "synopsis", "literallayout", "itemizedlist",
        "orderedlist", "variablelist", "simplelist", "note", "warning", "tip", "important", "caution", "danger", "example",
        "informalexample", "figure", "informalfigure", "table", "informaltable", "mediaobject", "sidebar", "epigraph",
        "procedure", "equation", "informalequation", "bridgehead", "segmentedlist", "glosslist", "calloutlist", "address",
        "qandaset", "task", "msgset", "cmdsynopsis", "funcsynopsis", "classsynopsis", "revhistory", "abstract", "legalnotice",
    ];

    private readonly string _baseFolder;
    private readonly DocumentBudget _budget;
    private readonly HashSet<string> _anchors = new(StringComparer.Ordinal);

    private DocBookReader(string baseFolder, DocumentLimits limits)
    {
        _baseFolder = baseFolder;
        _budget = new DocumentBudget(limits);
    }

    public static HDocument Read(string path, string media)
    {
        var limits = DocumentLimits.Default;
        if (new FileInfo(path).Length > limits.MaxPartBytes)
            throw DocumentLimits.Exceeded($"The DocBook file is larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
        return Read(File.ReadAllBytes(path), Path.GetDirectoryName(Path.GetFullPath(path))!, limits);
    }

    internal static HDocument Read(byte[] bytes, string baseFolder, DocumentLimits limits)
    {
        XDocument xml;
        try
        {
            xml = SafeXml.Load(new MemoryStream(WithoutDoctype(bytes)), limits.Xml(0, preserveWhitespace: true), DocumentLimits.Exceeded);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException(ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("entity", StringComparison.OrdinalIgnoreCase)
                ? "The DocBook document uses entities from its DTD, which Filee doesn't read."
                : "The file is not a DocBook document (its XML can't be read).", ex);
        }
        if (xml.Root is not { } root)
            throw new InvalidDataException("The file is not a DocBook document.");
        var name = root.Name.LocalName;
        if (root.Name.NamespaceName is not ("" or "http://docbook.org/ns/docbook"))
            throw new InvalidDataException("The file is not a DocBook document.");

        var reader = new DocBookReader(baseFolder, limits);
        var section = new HSection();
        var document = new HDocument { Title = Title(root) is { } title ? Text(title) : null };
        if (Sections.Contains(name))
            reader.Section(root, section.Blocks, 0, isRoot: true);
        else
            reader.Blocks(root, section.Blocks, 0, 0);
        document.Sections.Add(section);
        // Every id became a bookmark; only those a link points to stay.
        HtmlReader.PruneBookmarks(document);
        return document;
    }

    /// <summary>
    /// The bytes without a DOCTYPE that only names an external DTD (DocBook 4's OASIS declaration): it is never read.
    /// A DOCTYPE with an internal subset (entity declarations) stays, so SafeXml refuses the file.
    /// </summary>
    private static byte[] WithoutDoctype(byte[] bytes)
    {
        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        var start = head.IndexOf("<!DOCTYPE", StringComparison.Ordinal);
        if (start < 0)
            return bytes;
        var end = head.IndexOf('>', start);
        if (end < 0 || head.IndexOf('[', start, end - start) >= 0)
            return bytes;
        var prefix = Encoding.UTF8.GetByteCount(head.AsSpan(0, start));
        var length = Encoding.UTF8.GetByteCount(head.AsSpan(start, end + 1 - start));
        return [.. bytes.AsSpan(0, prefix), .. bytes.AsSpan(prefix + length)];
    }

    private static XElement? Title(XElement element) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == "title")
        ?? element.Elements().FirstOrDefault(e => e.Name.LocalName is "info" or "articleinfo" or "bookinfo" or "chapterinfo" or "sectioninfo" or "sect1info" or "sect2info" or "sect3info")
            ?.Elements().FirstOrDefault(e => e.Name.LocalName == "title");

    private static string Text(XElement element) => WhiteSpace(element.Value).Trim();

    /// <summary>xml:id (DocBook 5) or id (4.x).</summary>
    private static string? Id(XElement element) =>
        ((string?)element.Attribute(XNamespace.Xml + "id") ?? (string?)element.Attribute("id")) is { Length: > 0 } id ? id : null;

    private static string WhiteSpace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = true;
                continue;
            }
            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        if (space)
            sb.Append(' ');
        return sb.ToString();
    }

    // ───────────────────────── Sections ─────────────────────────

    /// <param name="level">Heading level of the section's title (0 for the root, whose title is the document's).</param>
    private void Section(XElement section, List<HBlock> output, int level, bool isRoot = false)
    {
        if (!isRoot && Title(section) is { } title)
        {
            var heading = Paragraph(title, 0);
            heading.HeadingLevel = Math.Clamp(level, 1, 9);
            if (Id(section) is { } id)
                heading.Inlines.Insert(0, new HBookmark(id));
            output.Add(heading);
        }
        foreach (var child in section.Elements())
        {
            var name = child.Name.LocalName;
            if (Sections.Contains(name))
                Section(child, output, Math.Min(level + 1, 9));
            else if (!Skipped.Contains(name))
                Block(child, output, 0, 0);
        }
    }

    // ───────────────────────── Blocks ─────────────────────────

    /// <summary>The children of <paramref name="parent"/> as blocks.</summary>
    private void Blocks(XElement parent, List<HBlock> output, int indent, int listLevel)
    {
        foreach (var child in parent.Elements())
        {
            if (Sections.Contains(child.Name.LocalName))
                Section(child, output, 1);
            else if (!Skipped.Contains(child.Name.LocalName))
                Block(child, output, indent, listLevel);
        }
    }

    private void Block(XElement element, List<HBlock> output, int indent, int listLevel)
    {
        var name = element.Name.LocalName;
        switch (name)
        {
            case "para" or "simpara":
                Para(element, output, indent, listLevel);
                break;
            case "formalpara":
                if (Title(element) is { } formalTitle)
                {
                    var title = Paragraph(formalTitle, indent);
                    Bold(title.Inlines);
                    output.Add(title);
                }
                Blocks(element, output, indent, listLevel);
                break;
            case "bridgehead":
                {
                    var heading = Paragraph(element, indent);
                    var renderAs = ((string?)element.Attribute("renderas"))?.ToLowerInvariant();
                    heading.HeadingLevel = renderAs is { Length: 5 } && renderAs.StartsWith("sect", StringComparison.Ordinal) && char.IsAsciiDigit(renderAs[4]) ? renderAs[4] - '0' : 3;
                    output.Add(heading);
                    break;
                }
            case var code when Code.Contains(code):
                CodeBlock(element, output, indent);
                break;
            case "literallayout":
                if ((string?)element.Attribute("class") == "monospaced")
                    CodeBlock(element, output, indent);
                else
                    output.Add(Paragraph(element, indent, keepLines: true));
                break;
            case "blockquote" or "epigraph":
                using (_budget.Enter())
                {
                    var quote = Math.Min(indent + MarkupBlocks.QuoteIndent, 8 * MarkupBlocks.QuoteIndent);
                    Blocks(element, output, quote, listLevel);
                    if (element.Elements().FirstOrDefault(e => e.Name.LocalName == "attribution") is { } attribution)
                    {
                        var by = Paragraph(attribution, quote);
                        by.Inlines.Insert(0, new HText("— ", Plain));
                        output.Add(by);
                    }
                }
                break;
            case "attribution":
                break;
            case "itemizedlist" or "orderedlist":
                List(element, output, indent, listLevel);
                break;
            case "variablelist":
                using (_budget.Enter())
                {
                    foreach (var entry in element.Elements().Where(e => e.Name.LocalName == "varlistentry"))
                    {
                        foreach (var term in entry.Elements().Where(e => e.Name.LocalName == "term"))
                        {
                            var paragraph = Paragraph(term, indent);
                            Bold(paragraph.Inlines);
                            output.Add(paragraph);
                        }
                        foreach (var item in entry.Elements().Where(e => e.Name.LocalName == "listitem"))
                            Blocks(item, output, Math.Min(indent + MarkupBlocks.QuoteIndent, 8 * MarkupBlocks.QuoteIndent), listLevel);
                    }
                }
                break;
            case "simplelist":
                {
                    var numbering = MarkupBlocks.Numbering(ordered: false, level: Math.Min(listLevel, 8));
                    foreach (var member in element.Elements().Where(e => e.Name.LocalName == "member"))
                    {
                        var paragraph = Paragraph(member, indent);
                        paragraph.List = new HListRef(numbering, Math.Min(listLevel, 8), Numbered: true);
                        output.Add(paragraph);
                    }
                    break;
                }
            case var admonition when Admonitions.Contains(admonition):
                using (_budget.Enter())
                {
                    var heading = Title(element) is { } t ? Paragraph(t, indent) : TextParagraph(char.ToUpperInvariant(admonition[0]) + admonition[1..], indent);
                    Bold(heading.Inlines);
                    output.Add(heading);
                    Blocks(element, output, Math.Min(indent + MarkupBlocks.QuoteIndent, 8 * MarkupBlocks.QuoteIndent), listLevel);
                }
                break;
            case "example" or "figure" or "sidebar" or "procedure" or "equation" or "task" or "abstract" or "legalnotice" or "qandaset":
                using (_budget.Enter())
                {
                    if (Title(element) is { } caption)
                    {
                        var title = Paragraph(caption, indent);
                        Bold(title.Inlines);
                        output.Add(title);
                    }
                    if (name == "procedure")
                    {
                        // The steps of a procedure are a numbered list.
                        var steps = new XElement(element.Name.Namespace + "orderedlist", element.Elements().Where(e => e.Name.LocalName == "step")
                            .Select(s => new XElement(element.Name.Namespace + "listitem", s.Nodes())));
                        List(steps, output, indent, listLevel);
                        break;
                    }
                    Blocks(element, output, indent, listLevel);
                }
                break;
            case "mediaobject" or "inlinemediaobject":
                {
                    _budget.Paragraph();
                    var paragraph = new HParagraph { Format = indent > 0 ? new HParaFormat(Left: indent) : default };
                    if (Picture(element) is { } picture)
                        paragraph.Inlines.Add(picture);
                    else if (element.Descendants().FirstOrDefault(e => e.Name.LocalName == "phrase") is { } alternative)
                        paragraph.Inlines.Add(new HText(Text(alternative), Plain));
                    if (paragraph.Inlines.Count > 0)
                        output.Add(paragraph);
                    if (element.Elements().FirstOrDefault(e => e.Name.LocalName == "caption") is { } mediaCaption)
                        Blocks(mediaCaption, output, indent, listLevel);
                    break;
                }
            case "table" or "informaltable":
                output.Add(Table(element, indent));
                break;
            case "informalequation" or "informalexample" or "informalfigure":
                Blocks(element, output, indent, listLevel);
                break;
            case "mathphrase":
                {
                    var paragraph = Paragraph(element, indent);
                    output.Add(paragraph);
                    break;
                }
            default:
                // Unknown elements: their blocks, or their text as a paragraph.
                if (element.Elements().Any(e => BlockElements.Contains(e.Name.LocalName) || Sections.Contains(e.Name.LocalName)))
                {
                    using (_budget.Enter())
                        Blocks(element, output, indent, listLevel);
                }
                else if (Text(element).Length > 0)
                {
                    output.Add(Paragraph(element, indent));
                }
                break;
        }
    }

    /// <summary>A program listing: its text as it is, one code line per line.</summary>
    private void CodeBlock(XElement element, List<HBlock> output, int indent)
    {
        MarkupBlocks.SeparateCode(output, _budget);
        var text = MarkupBlocks.Lines(element.Value).Trim('\n');
        MarkupBlocks.CodeLines(text.Split('\n'), output, _budget, indent);
    }

    /// <summary>A para: its text, split around the blocks it holds (DocBook lets lists and tables stand in a para).</summary>
    private void Para(XElement para, List<HBlock> output, int indent, int listLevel)
    {
        var run = new List<XNode>();
        void Flush()
        {
            if (run.Count == 0)
                return;
            var paragraph = NewParagraph(indent);
            var sink = new InlineSink(paragraph.Inlines);
            foreach (var node in run)
                Inline(node, Plain, sink, 0);
            sink.Flush();
            Trim(paragraph.Inlines);
            if (paragraph.Inlines.Count > 0)
                output.Add(paragraph);
            run.Clear();
        }

        foreach (var node in para.Nodes())
        {
            if (node is XElement child && BlockElements.Contains(child.Name.LocalName))
            {
                Flush();
                using (_budget.Enter())
                    Block(child, output, indent, listLevel);
                continue;
            }
            run.Add(node);
        }
        Flush();
        if (Id(para) is { } id && output.Count > 0 && output[^1] is HParagraph last)
            last.Inlines.Insert(0, new HBookmark(id));
    }

    private void List(XElement list, List<HBlock> output, int indent, int listLevel)
    {
        using var _ = _budget.Enter();
        var level = Math.Min(listLevel, 8);
        var ordered = list.Name.LocalName == "orderedlist";
        var start = int.TryParse((string?)list.Attribute("startingnumber"), out var number) ? Math.Clamp(number, 0, 1_000_000) : 1;
        var format = ((string?)list.Attribute("numeration"))?.ToLowerInvariant() switch
        {
            "loweralpha" => "LATIN_SMALL",
            "upperalpha" => "LATIN_CAPITAL",
            "lowerroman" => "ROMAN_SMALL",
            "upperroman" => "ROMAN_CAPITAL",
            _ => "DIGIT",
        };
        var numbering = MarkupBlocks.Numbering(ordered, start, format, level);
        foreach (var item in list.Elements().Where(e => e.Name.LocalName == "listitem"))
        {
            var blocks = new List<HBlock>();
            Blocks(item, blocks, indent, listLevel + 1);
            var first = true;
            foreach (var block in blocks)
            {
                if (block is HParagraph { List: null, HeadingLevel: 0 } paragraph && !MarkupBlocks.IsCode(paragraph))
                {
                    paragraph.List = new HListRef(numbering, level, Numbered: first);
                    first = false;
                }
                else if (block is not HParagraph { List: not null })
                {
                    first = false;
                }
                output.Add(block);
            }
            if (blocks.Count == 0)
            {
                _budget.Paragraph();
                output.Add(new HParagraph { List = new HListRef(numbering, level, Numbered: true) });
            }
        }
    }

    // ───────────────────────── Tables ─────────────────────────

    private HTable Table(XElement element, int indent)
    {
        _budget.Object();
        using var _ = _budget.Enter();
        var table = new HTable { Indent = indent };
        if (Title(element) is { } caption)
            table.Caption.Add(Paragraph(caption, 0));
        var limit = _budget.Limits.MaxTableColumns;
        var tgroups = element.Elements().Where(e => e.Name.LocalName == "tgroup").ToList();
        if (tgroups.Count == 0)
        {
            HtmlTable(element, table, limit);
        }
        foreach (var tgroup in tgroups)
        {
            var columns = int.TryParse((string?)tgroup.Attribute("cols"), out var cols) ? Math.Clamp(cols, 1, limit) : 1;
            // Column names (colspec colname) for the spans of namest / nameend.
            var names = new Dictionary<string, int>(StringComparer.Ordinal);
            var position = 0;
            foreach (var colspec in tgroup.Elements().Where(e => e.Name.LocalName == "colspec"))
            {
                position = int.TryParse((string?)colspec.Attribute("colnum"), out var colnum) ? Math.Clamp(colnum - 1, 0, limit - 1) : position;
                if ((string?)colspec.Attribute("colname") is { Length: > 0 } colname)
                    names.TryAdd(colname, position);
                position++;
            }
            foreach (var part in tgroup.Elements().Where(e => e.Name.LocalName is "thead" or "tbody" or "tfoot"))
            {
                var header = part.Name.LocalName == "thead";
                foreach (var row in part.Elements().Where(e => e.Name.LocalName == "row"))
                {
                    var hRow = new HRow { Header = header };
                    var column = 0;
                    foreach (var entry in row.Elements().Where(e => e.Name.LocalName is "entry" or "entrytbl"))
                    {
                        if (column >= limit)
                            break;
                        var cell = new HCell();
                        if ((string?)entry.Attribute("colname") is { } at && names.TryGetValue(at, out var named) && named >= column)
                            cell.Column = column = named;
                        if ((string?)entry.Attribute("namest") is { } from && names.TryGetValue(from, out var first)
                            && (string?)entry.Attribute("nameend") is { } to && names.TryGetValue(to, out var last) && last >= first)
                        {
                            cell.Column = column = Math.Max(column, first);
                            cell.ColSpan = Math.Clamp(last - first + 1, 1, limit - column);
                        }
                        if (int.TryParse((string?)entry.Attribute("morerows"), out var more))
                            cell.RowSpan = Math.Clamp(more + 1, 1, 10_000);
                        Cell(entry, cell, header);
                        hRow.Cells.Add(cell);
                        column += cell.ColSpan;
                    }
                    table.Rows.Add(hRow);
                }
            }
            table.ColumnCount = Math.Max(table.ColumnCount, columns);
        }
        table.ColumnCount = Math.Max(1, Math.Max(table.ColumnCount, table.Rows.Count == 0 ? 1 : table.Rows.Max(r => r.Cells.Sum(c => c.ColSpan))));
        if (table.Rows.Count == 0)
            table.Rows.Add(new HRow { Cells = { new HCell { Blocks = { new HParagraph() } } } });
        return table;
    }

    /// <summary>An HTML-style table (DocBook 5 allows tr / td / th, also in thead / tbody).</summary>
    private void HtmlTable(XElement element, HTable table, int limit)
    {
        foreach (var row in element.Descendants().Where(e => e.Name.LocalName == "tr"))
        {
            if (row.Ancestors().FirstOrDefault(a => a.Name.LocalName is "table" or "informaltable") != element)
                continue; // a row of a table inside a cell
            var header = row.Parent?.Name.LocalName == "thead" || (row.Elements().Any() && row.Elements().All(e => e.Name.LocalName == "th"));
            var hRow = new HRow { Header = header };
            foreach (var entry in row.Elements().Where(e => e.Name.LocalName is "td" or "th").Take(limit))
            {
                var cell = new HCell
                {
                    ColSpan = int.TryParse((string?)entry.Attribute("colspan"), out var colspan) ? Math.Clamp(colspan, 1, limit) : 1,
                    RowSpan = int.TryParse((string?)entry.Attribute("rowspan"), out var rowspan) ? Math.Clamp(rowspan, 1, 10_000) : 1,
                };
                Cell(entry, cell, header || entry.Name.LocalName == "th");
                hRow.Cells.Add(cell);
            }
            table.Rows.Add(hRow);
        }
    }

    private void Cell(XElement entry, HCell cell, bool header)
    {
        if (entry.Elements().Any(e => BlockElements.Contains(e.Name.LocalName)))
            Blocks(entry, cell.Blocks, 0, 0);
        else
            cell.Blocks.Add(Paragraph(entry, 0));
        if (header)
            foreach (var paragraph in cell.Blocks.OfType<HParagraph>())
                Bold(paragraph.Inlines);
        if (cell.Blocks.Count == 0)
        {
            _budget.Paragraph();
            cell.Blocks.Add(new HParagraph());
        }
    }

    // ───────────────────────── Inlines ─────────────────────────

    private HParagraph NewParagraph(int indent)
    {
        _budget.Paragraph();
        return new HParagraph { Format = indent > 0 ? new HParaFormat(Left: indent) : default };
    }

    private HParagraph TextParagraph(string text, int indent)
    {
        var paragraph = NewParagraph(indent);
        paragraph.Inlines.Add(new HText(text, Plain));
        return paragraph;
    }

    /// <param name="keepLines">literallayout: line ends are line breaks and spaces stay.</param>
    private HParagraph Paragraph(XElement element, int indent, bool keepLines = false)
    {
        var paragraph = NewParagraph(indent);
        var sink = new InlineSink(paragraph.Inlines);
        foreach (var node in element.Nodes())
            Inline(node, Plain, sink, 0, keepLines);
        sink.Flush();
        Trim(paragraph.Inlines);
        return paragraph;
    }

    private static void Bold(List<HInline> inlines)
    {
        for (var i = 0; i < inlines.Count; i++)
            if (inlines[i] is HText text)
                inlines[i] = new HText(text.Text, text.Format with { Bold = true });
    }

    /// <summary>White space at a paragraph's ends is not text; runs of it inside were made one space already.</summary>
    private static void Trim(List<HInline> inlines)
    {
        while (inlines.Count > 0 && inlines[0] is HText first && first.Text.TrimStart().Length == 0)
            inlines.RemoveAt(0);
        if (inlines.Count > 0 && inlines[0] is HText head)
            inlines[0] = new HText(head.Text.TrimStart(), head.Format);
        while (inlines.Count > 0 && inlines[^1] is HText last && last.Text.TrimEnd().Length == 0)
            inlines.RemoveAt(inlines.Count - 1);
        if (inlines.Count > 0 && inlines[^1] is HText tail)
            inlines[^1] = new HText(tail.Text.TrimEnd(), tail.Format);
    }

    private static readonly HashSet<string> CodeInlines =
    [
        "literal", "code", "command", "filename", "option", "userinput", "computeroutput", "varname", "function", "classname",
        "parameter", "envar", "systemitem", "constant", "type", "methodname", "property", "markup", "tag", "token", "uri",
        "prompt", "returnvalue", "exceptionname", "interfacename", "symbol", "sgmltag", "literal", "keycode",
    ];

    private void Inline(XNode node, HCharFormat format, InlineSink sink, int depth, bool keepLines = false)
    {
        switch (node)
        {
            case XText text:
                {
                    if (keepLines)
                    {
                        var lines = MarkupBlocks.Lines(text.Value).Split('\n');
                        for (var i = 0; i < lines.Length; i++)
                        {
                            if (i > 0)
                                sink.Add(new HLineBreak(format));
                            sink.Text(lines[i], format);
                        }
                    }
                    else
                    {
                        sink.Text(format.Shade == HtmlReader.CodeShade ? text.Value : WhiteSpace(text.Value), format);
                    }
                    return;
                }
            case not XElement:
                return;
        }
        var element = (XElement)node;
        var name = element.Name.LocalName;
        if (depth > 64)
        {
            sink.Text(WhiteSpace(element.Value), format);
            return;
        }
        switch (name)
        {
            case "emphasis":
                {
                    var role = ((string?)element.Attribute("role"))?.ToLowerInvariant();
                    var applied = role switch
                    {
                        "bold" or "strong" => format with { Bold = true },
                        "underline" => format with { Underline = true },
                        "strikethrough" or "strike" or "line-through" => format with { Strike = true },
                        "highlight" or "marked" => format with { Shade = HtmlReader.MarkShade },
                        _ => format with { Italic = true },
                    };
                    Children(element, applied, sink, depth, keepLines);
                    return;
                }
            case "superscript":
                Children(element, format with { Superscript = true, Subscript = null }, sink, depth, keepLines);
                return;
            case "subscript":
                Children(element, format with { Subscript = true, Superscript = null }, sink, depth, keepLines);
                return;
            case var code when CodeInlines.Contains(code):
                sink.Text(element.Value, format with { Shade = HtmlReader.CodeShade });
                return;
            case "replaceable" or "citetitle" or "firstterm" or "glossterm" or "foreignphrase" or "wordasword" or "citerefentry":
                Children(element, format with { Italic = true }, sink, depth, keepLines);
                return;
            case "quote":
                sink.Text("“", format);
                Children(element, format, sink, depth, keepLines);
                sink.Text("”", format);
                return;
            case "trademark":
                Children(element, format, sink, depth, keepLines);
                sink.Text(((string?)element.Attribute("class")) switch { "registered" => "®", "service" => "℠", "copyright" => "©", _ => "™" }, format);
                return;
            case "footnote":
                using (_budget.Enter())
                {
                    var note = new HNote(endnote: false);
                    Blocks(element, note.Blocks, 0, 0);
                    if (note.Blocks.Count == 0)
                        note.Blocks.Add(Paragraph(element, 0));
                    sink.Add(note);
                }
                return;
            case "footnoteref":
                return;
            case "inlinemediaobject":
                if (Picture(element) is { } picture)
                    sink.Add(picture);
                else if (element.Descendants().FirstOrDefault(e => e.Name.LocalName == "phrase") is { } alternative)
                    sink.Text(Text(alternative), format);
                return;
            case "link" or "ulink" or "olink":
                {
                    var target = (string?)element.Attribute(XName.Get("href", XLink)) ?? (string?)element.Attribute("url")
                                 ?? ((string?)element.Attribute("linkend") is { Length: > 0 } end ? "#" + end : null);
                    if (string.IsNullOrWhiteSpace(target))
                    {
                        Children(element, format, sink, depth, keepLines);
                        return;
                    }
                    var link = new HLink(target.Trim());
                    var content = new InlineSink(link.Content);
                    if (element.Nodes().Any())
                        Children(element, format, content, depth, keepLines);
                    else
                        content.Text(target.TrimStart('#'), format);
                    content.Flush();
                    sink.Add(link);
                    return;
                }
            case "xref":
                {
                    var end = (string?)element.Attribute("linkend");
                    var shown = (string?)element.Attribute("xreflabel") ?? end ?? "";
                    if (end is { Length: > 0 })
                    {
                        var link = new HLink("#" + end);
                        link.Content.Add(new HText(shown, format));
                        sink.Add(link);
                    }
                    return;
                }
            case "email":
                {
                    var link = new HLink("mailto:" + element.Value.Trim());
                    link.Content.Add(new HText(element.Value.Trim(), format));
                    sink.Add(link);
                    return;
                }
            case "anchor":
                if (Id(element) is { } id)
                    sink.Add(new HBookmark(id));
                return;
            case "indexterm" or "remark" or "alt" or "annotation":
                return;
            default:
                Children(element, format, sink, depth, keepLines);
                return;
        }
    }

    private void Children(XElement element, HCharFormat format, InlineSink sink, int depth, bool keepLines)
    {
        foreach (var child in element.Nodes())
            Inline(child, format, sink, depth + 1, keepLines);
    }

    /// <summary>The first imagedata of a media object that is a local picture.</summary>
    private HImage? Picture(XElement media)
    {
        foreach (var data in media.Descendants().Where(e => e.Name.LocalName == "imagedata"))
        {
            var reference = (string?)data.Attribute("fileref") ?? (string?)data.Attribute(XName.Get("href", XLink));
            if (MarkdownReader.ResolveImage(reference, _baseFolder) is not { } path)
                continue;
            _budget.Object();
            return new HImage(path)
            {
                Width = HwpxUnits.ParseLength((string?)data.Attribute("contentwidth") ?? (string?)data.Attribute("width")),
                Height = HwpxUnits.ParseLength((string?)data.Attribute("contentdepth") ?? (string?)data.Attribute("depth")),
            };
        }
        return null;
    }
}
