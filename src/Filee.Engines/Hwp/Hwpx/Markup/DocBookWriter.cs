// HDocument → DocBook 5 (an article in the DocBook namespace, as Pandoc's docbook5 writer makes one): headings become
// nested sections, paragraphs para (with line breaks: literallayout), quotes blockquote, lists itemizedlist /
// orderedlist with their numeration and nested lists inside the items, code programlisting, tables CALS informaltable
// with colspecs, namest / nameend and morerows spans and a thead for header rows, pictures mediaobject /
// inlinemediaobject (linked into the "<name>_files" folder), footnotes, links (xlink:href), bookmarks as anchors,
// emphasis (roles bold, underline, strikethrough), code, superscript and subscript. The title is the article's info.

using System.Globalization;
using System.Text;
using System.Xml;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Writes the document model as DocBook 5.</summary>
internal sealed class DocBookWriter
{
    private const string DocBook = "http://docbook.org/ns/docbook";
    private const string XLink = "http://www.w3.org/1999/xlink";

    private readonly XmlWriter _xml;
    private readonly Func<string, string?> _pictures;
    private readonly ListNumbers _numbers = new();

    private DocBookWriter(XmlWriter xml, Func<string, string?> pictures)
    {
        _xml = xml;
        _pictures = pictures;
    }

    /// <param name="pictures">Maps a picture file to the link written into the document, or null to leave it out.</param>
    public static string Write(HDocument document, Func<string, string?> pictures)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.Replace };
        using (var xml = XmlWriter.Create(sb, settings))
        {
            var writer = new DocBookWriter(xml, pictures);
            xml.WriteStartElement("article", DocBook);
            xml.WriteAttributeString("xmlns", "xlink", null, XLink);
            xml.WriteAttributeString("version", "5.0");
            if (!string.IsNullOrWhiteSpace(document.Title))
            {
                xml.WriteStartElement("info", DocBook);
                xml.WriteElementString("title", DocBook, Clean(document.Title.Trim()));
                xml.WriteEndElement();
            }
            writer.Body(document.Sections.SelectMany(s => s.Blocks).ToList());
            xml.WriteEndElement();
        }
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"UTF-8\"", StringComparison.Ordinal) + "\n";
    }

    /// <summary>The blocks with each heading opening a section that ends at the next heading of its level or above.</summary>
    private void Body(List<HBlock> blocks)
    {
        var open = new List<int>();
        var content = new List<HBlock>();
        void Flush()
        {
            Blocks(content);
            content.Clear();
        }

        foreach (var block in blocks)
        {
            if (block is HParagraph { HeadingLevel: > 0 } heading)
            {
                Flush();
                while (open.Count > 0 && open[^1] >= heading.HeadingLevel)
                {
                    _xml.WriteEndElement();
                    open.RemoveAt(open.Count - 1);
                }
                _xml.WriteStartElement("section", DocBook);
                _xml.WriteStartElement("title", DocBook);
                Inlines(heading.Inlines);
                _xml.WriteEndElement();
                open.Add(heading.HeadingLevel);
                continue;
            }
            content.Add(block);
        }
        Flush();
        for (var i = 0; i < open.Count; i++)
            _xml.WriteEndElement();
    }

    private void Blocks(List<HBlock> blocks)
    {
        for (var i = 0; i < blocks.Count;)
        {
            var block = blocks[i];
            if (MarkupBlocks.IsCode(block))
            {
                var lines = new List<string>();
                var quote = Quote((HParagraph)block);
                for (; i < blocks.Count && MarkupBlocks.IsCode(blocks[i]); i++)
                    lines.Add(string.Concat(((HParagraph)blocks[i]).Inlines.OfType<HText>().Select(t => t.Text)));
                Quoted(quote, () =>
                {
                    _xml.WriteStartElement("programlisting", DocBook);
                    _xml.WriteString(Clean(string.Join("\n", lines)));
                    _xml.WriteEndElement();
                });
                continue;
            }
            if (block is HParagraph { List: not null })
            {
                var start = i;
                while (i < blocks.Count && blocks[i] is HParagraph { List: not null })
                    i++;
                List(blocks, start, i, 0);
                continue;
            }
            i++;
            switch (block)
            {
                case HParagraph { HeadingLevel: > 0 } heading:
                    _xml.WriteStartElement("bridgehead", DocBook);
                    Inlines(heading.Inlines);
                    _xml.WriteEndElement();
                    break;
                case HParagraph paragraph when MarkupBlocks.IsEmpty(paragraph):
                    break;
                case HParagraph paragraph:
                    Quoted(Quote(paragraph), () => Paragraph(paragraph));
                    break;
                case HTable table when table.Rows.Count > 0:
                    Table(table);
                    break;
            }
        }
    }

    private static int Quote(HParagraph paragraph) =>
        paragraph.List is null ? Math.Clamp((paragraph.Format.Left ?? 0) / MarkupBlocks.QuoteIndent, 0, 8) : 0;

    private void Quoted(int levels, Action write)
    {
        for (var q = 0; q < levels; q++)
            _xml.WriteStartElement("blockquote", DocBook);
        write();
        for (var q = 0; q < levels; q++)
            _xml.WriteEndElement();
    }

    private void Paragraph(HParagraph paragraph)
    {
        // A picture alone is a mediaobject; a paragraph with line breaks keeps them in a literallayout.
        if (paragraph.Inlines.Where(i => i is not HBookmark).ToList() is [HImage image] && _pictures(image.Path) is { } link)
        {
            MediaObject("mediaobject", link);
            return;
        }
        _xml.WriteStartElement(paragraph.Inlines.Any(i => i is HLineBreak) ? "literallayout" : "para", DocBook);
        Inlines(paragraph.Inlines);
        _xml.WriteEndElement();
    }

    private void List(List<HBlock> blocks, int start, int end, int level)
    {
        var first = (HParagraph)blocks[start];
        var definition = first.List!.Numbering.Levels.Count == 0 ? null : first.List.Numbering.Levels[Math.Min(first.List.Level, first.List.Numbering.Levels.Count - 1)];
        var ordered = definition is { Bullet: false };
        _xml.WriteStartElement(ordered ? "orderedlist" : "itemizedlist", DocBook);
        if (ordered)
        {
            var numeration = definition!.Format switch
            {
                "LATIN_SMALL" => "loweralpha",
                "LATIN_CAPITAL" => "upperalpha",
                "ROMAN_SMALL" => "lowerroman",
                "ROMAN_CAPITAL" => "upperroman",
                _ => "arabic",
            };
            _xml.WriteAttributeString("numeration", numeration);
            if (definition.Start != 1)
                _xml.WriteAttributeString("startingnumber", definition.Start.ToString(CultureInfo.InvariantCulture));
        }
        var baseLevel = first.List.Level;
        var itemOpen = false;
        for (var i = start; i < end;)
        {
            var paragraph = (HParagraph)blocks[i];
            var item = paragraph.List!;
            if (item.Level > baseLevel)
            {
                // A nested list inside the open item.
                if (!itemOpen)
                {
                    _xml.WriteStartElement("listitem", DocBook);
                    itemOpen = true;
                }
                var nestedEnd = i;
                while (nestedEnd < end && ((HParagraph)blocks[nestedEnd]).List!.Level > baseLevel)
                    nestedEnd++;
                // Items of a different list at the nested level start their own list.
                var runStart = i;
                for (var k = i + 1; k <= nestedEnd; k++)
                {
                    if (k == nestedEnd || (((HParagraph)blocks[k]).List!.Level == ((HParagraph)blocks[runStart]).List!.Level
                                           && !ReferenceEquals(((HParagraph)blocks[k]).List!.Numbering, ((HParagraph)blocks[runStart]).List!.Numbering)
                                           && ((HParagraph)blocks[k]).List!.Numbered))
                    {
                        List(blocks, runStart, k, level + 1);
                        runStart = k;
                    }
                }
                i = nestedEnd;
                continue;
            }
            if (item.Numbered || !itemOpen)
            {
                if (itemOpen)
                    _xml.WriteEndElement();
                _xml.WriteStartElement("listitem", DocBook);
                itemOpen = true;
                if (item.Numbered)
                    _numbers.Next(item);
            }
            Paragraph(paragraph);
            i++;
        }
        if (itemOpen)
            _xml.WriteEndElement();
        _xml.WriteEndElement();
    }

    private void Table(HTable table)
    {
        var columns = Math.Max(1, Math.Max(table.ColumnCount, table.Rows.Max(r => r.Cells.Sum(c => Math.Max(1, c.ColSpan)))));
        columns = Math.Min(columns, Documents.DocumentLimits.Default.MaxTableColumns);
        _xml.WriteStartElement("informaltable", DocBook);
        _xml.WriteStartElement("tgroup", DocBook);
        _xml.WriteAttributeString("cols", columns.ToString(CultureInfo.InvariantCulture));
        for (var c = 0; c < columns; c++)
        {
            _xml.WriteStartElement("colspec", DocBook);
            _xml.WriteAttributeString("colname", $"c{c + 1}");
            _xml.WriteEndElement();
        }
        var header = 0;
        while (header < table.Rows.Count && table.Rows[header].Header)
            header++;
        if (header == table.Rows.Count)
            header = 0;
        var covered = new HashSet<(int Row, int Column)>();
        for (var part = 0; part < 2; part++)
        {
            var from = part == 0 ? 0 : header;
            var to = part == 0 ? header : table.Rows.Count;
            if (from == to)
                continue;
            _xml.WriteStartElement(part == 0 ? "thead" : "tbody", DocBook);
            for (var r = from; r < to; r++)
            {
                _xml.WriteStartElement("row", DocBook);
                var column = 0;
                foreach (var cell in table.Rows[r].Cells)
                {
                    while (column < columns && covered.Contains((r, column)))
                        column++;
                    if (column >= columns)
                        break;
                    var colSpan = Math.Clamp(cell.ColSpan, 1, columns - column);
                    // Spans stay in their part (thead or tbody).
                    var rowSpan = Math.Clamp(cell.RowSpan, 1, to - r);
                    _xml.WriteStartElement("entry", DocBook);
                    if (colSpan > 1)
                    {
                        _xml.WriteAttributeString("namest", $"c{column + 1}");
                        _xml.WriteAttributeString("nameend", $"c{column + colSpan}");
                    }
                    else if (column > 0 && covered.Contains((r, column - 1)))
                    {
                        _xml.WriteAttributeString("colname", $"c{column + 1}");
                    }
                    if (rowSpan > 1)
                        _xml.WriteAttributeString("morerows", (rowSpan - 1).ToString(CultureInfo.InvariantCulture));
                    Cell(cell, r < header);
                    _xml.WriteEndElement();
                    for (var rr = r; rr < r + rowSpan; rr++)
                        for (var cc = column; cc < column + colSpan; cc++)
                            covered.Add((rr, cc));
                    column += colSpan;
                }
                _xml.WriteEndElement();
            }
            _xml.WriteEndElement();
        }
        _xml.WriteEndElement();
        _xml.WriteEndElement();
    }

    private void Cell(HCell cell, bool header)
    {
        var blocks = cell.Blocks;
        if (header)
        {
            // Header cells are bold by nature.
            blocks = [.. blocks.Select(b => b is HParagraph p ? Unbold(p) : b)];
        }
        if (blocks.All(b => b is HParagraph { List: null } p && !MarkupBlocks.IsCode(p) && Quote(p) == 0) && blocks.Count(b => !MarkupBlocks.IsEmpty(b)) <= 1)
        {
            if (blocks.FirstOrDefault(b => !MarkupBlocks.IsEmpty(b)) is HParagraph only)
                Inlines(only.Inlines);
            return;
        }
        Blocks(blocks);
    }

    private static HParagraph Unbold(HParagraph paragraph)
    {
        var copy = new HParagraph { Format = paragraph.Format, List = paragraph.List, HeadingLevel = paragraph.HeadingLevel };
        copy.Inlines.AddRange(paragraph.Inlines.Select(i => i is HText t && t is not HEquation ? new HText(t.Text, t.Format with { Bold = null }) : i));
        return copy;
    }

    // ───────────────────────── Inlines ─────────────────────────

    private void Inlines(List<HInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HText text:
                    Run(text.Text, text.Format);
                    break;
                case HLineBreak:
                    _xml.WriteString("\n");
                    break;
                case HTab:
                    _xml.WriteString("\t");
                    break;
                case HLink link when link.Target.StartsWith('#'):
                    _xml.WriteStartElement("link", DocBook);
                    _xml.WriteAttributeString("linkend", Clean(link.Target[1..]));
                    Inlines(link.Content);
                    _xml.WriteEndElement();
                    break;
                case HLink link:
                    _xml.WriteStartElement("link", DocBook);
                    _xml.WriteAttributeString("href", XLink, Clean(link.Target));
                    Inlines(link.Content);
                    _xml.WriteEndElement();
                    break;
                case HBookmark bookmark:
                    _xml.WriteStartElement("anchor", DocBook);
                    _xml.WriteAttributeString("xml", "id", null, XmlId(bookmark.Name));
                    _xml.WriteEndElement();
                    break;
                case HImage image:
                    if (_pictures(image.Path) is { } picture)
                        MediaObject("inlinemediaobject", picture);
                    break;
                case HNote note:
                    _xml.WriteStartElement("footnote", DocBook);
                    var any = false;
                    foreach (var paragraph in note.Blocks.OfType<HParagraph>().Where(p => !MarkupBlocks.IsEmpty(p)))
                    {
                        _xml.WriteStartElement("para", DocBook);
                        Inlines(paragraph.Inlines);
                        _xml.WriteEndElement();
                        any = true;
                    }
                    if (!any)
                        _xml.WriteElementString("para", DocBook, "");
                    _xml.WriteEndElement();
                    break;
                case HTextBox box:
                    foreach (var paragraph in box.Blocks.OfType<HParagraph>())
                        Inlines(paragraph.Inlines);
                    break;
            }
        }
    }

    /// <summary>A run: emphasis, code, superscript and subscript elements around its text.</summary>
    private void Run(string text, HCharFormat format)
    {
        var open = 0;
        void Start(string name, string? role = null)
        {
            _xml.WriteStartElement(name, DocBook);
            if (role is not null)
                _xml.WriteAttributeString("role", role);
            open++;
        }

        if (format.Bold is true)
            Start("emphasis", "bold");
        if (format.Italic is true)
            Start("emphasis");
        if (format.Underline is true)
            Start("emphasis", "underline");
        if (format.Strike is true)
            Start("emphasis", "strikethrough");
        if (format.Shade == HtmlReader.MarkShade)
            Start("emphasis", "highlight");
        if (format.Superscript is true)
            Start("superscript");
        else if (format.Subscript is true)
            Start("subscript");
        if (format.Shade == HtmlReader.CodeShade)
            Start("code");
        _xml.WriteString(Clean(text));
        for (; open > 0; open--)
            _xml.WriteEndElement();
    }

    private void MediaObject(string element, string link)
    {
        _xml.WriteStartElement(element, DocBook);
        _xml.WriteStartElement("imageobject", DocBook);
        _xml.WriteStartElement("imagedata", DocBook);
        _xml.WriteAttributeString("fileref", Clean(link));
        _xml.WriteEndElement();
        _xml.WriteEndElement();
        _xml.WriteEndElement();
    }

    /// <summary>A bookmark name as an xml:id (an XML name: letters, digits, "-", "_", ".").</summary>
    private static string XmlId(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        if (sb.Length == 0 || !(char.IsLetter(sb[0]) || sb[0] == '_'))
            sb.Insert(0, '_');
        return sb.ToString();
    }

    /// <summary>Text without the characters XML 1.0 can't hold.</summary>
    private static string Clean(string text)
    {
        if (text.All(XmlConvert.IsXmlChar))
            return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (XmlConvert.IsXmlChar(text[i]))
                sb.Append(text[i]);
            else if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], text[i]))
                sb.Append(text, i++, 2);
        }
        return sb.ToString();
    }
}
