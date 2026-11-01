// OPML outlines (.opml, 1.0 and 2.0) → HDocument, without Pandoc: each outline is a heading one level deeper than its
// parent (levels past 9 stay at 9), its _note attribute Markdown under it (MarkdownReader), as Pandoc reads OPML. An
// outline of type "link" links its text to its url; a feed of a subscription list (type "rss") links its text to the
// site (htmlUrl) and gets a line with the feed's address (xmlUrl). Text that holds HTML (OPML allows it) is read by
// HtmlReader. head/title is the title. The XML goes through SafeXml (no DTD, so no entity expands; depth and element
// limits) and everything built counts against DocumentLimits.

using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Reads OPML outlines into the document model.</summary>
internal sealed class OpmlReader
{
    private readonly string _baseFolder;
    private readonly string _media;
    private readonly DocumentBudget _budget;
    private readonly List<HBlock> _blocks = [];

    private OpmlReader(string baseFolder, string media, DocumentLimits limits)
    {
        _baseFolder = baseFolder;
        _media = media;
        _budget = new DocumentBudget(limits);
    }

    public static HDocument Read(string path, string media)
    {
        var limits = DocumentLimits.Default;
        if (new FileInfo(path).Length > limits.MaxPartBytes)
            throw DocumentLimits.Exceeded($"The OPML file is larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
        using var stream = File.OpenRead(path);
        return Read(stream, Path.GetDirectoryName(Path.GetFullPath(path))!, media, limits);
    }

    internal static HDocument Read(Stream stream, string baseFolder, string media, DocumentLimits limits)
    {
        XDocument xml;
        try
        {
            xml = SafeXml.Load(stream, limits.Xml(0, preserveWhitespace: false), DocumentLimits.Exceeded);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("The file is not an OPML outline (its XML can't be read).", ex);
        }
        if (xml.Root is not { } root || root.Name.LocalName != "opml")
            throw new InvalidDataException("The file is not an OPML outline.");

        var reader = new OpmlReader(baseFolder, media, limits);
        if (Child(root, "body") is { } body)
            reader.Outlines(body, 1);
        var section = new HSection();
        section.Blocks.AddRange(reader._blocks);
        var title = Child(root, "head") is { } head && Child(head, "title")?.Value.Trim() is { Length: > 0 } t ? t : null;
        var document = new HDocument { Title = title };
        document.Sections.Add(section);
        return document;
    }

    private static XElement? Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    /// <summary>The outlines in <paramref name="parent"/>, depth first; SafeXml bounds the depth.</summary>
    private void Outlines(XElement parent, int level)
    {
        foreach (var outline in parent.Elements().Where(e => e.Name.LocalName == "outline"))
        {
            Outline(outline, level);
            Outlines(outline, level + 1);
        }
    }

    private void Outline(XElement outline, int level)
    {
        var text = (string?)outline.Attribute("text") ?? (string?)outline.Attribute("title") ?? "";
        var type = ((string?)outline.Attribute("type"))?.Trim().ToLowerInvariant();
        var link = type switch
        {
            "link" or "include" => (string?)outline.Attribute("url"),
            "rss" => (string?)outline.Attribute("htmlUrl") ?? (string?)outline.Attribute("xmlUrl"),
            _ => null,
        };

        if (text.Trim().Length > 0 || link is not null)
        {
            _budget.Paragraph();
            var heading = new HParagraph { HeadingLevel = Math.Min(level, 9) };
            var inlines = Inlines(text);
            if (!string.IsNullOrWhiteSpace(link))
            {
                var hLink = new HLink(link.Trim());
                hLink.Content.AddRange(inlines.Count > 0 ? inlines : [new HText(link.Trim(), default)]);
                heading.Inlines.Add(hLink);
            }
            else
            {
                heading.Inlines.AddRange(inlines);
            }
            _blocks.Add(heading);
        }

        if (type == "rss" && ((string?)outline.Attribute("xmlUrl"))?.Trim() is { Length: > 0 } feed)
        {
            _budget.Paragraph();
            var paragraph = new HParagraph();
            paragraph.Inlines.Add(new HText("Feed: ", default));
            var feedLink = new HLink(feed);
            feedLink.Content.Add(new HText(feed, default));
            paragraph.Inlines.Add(feedLink);
            _blocks.Add(paragraph);
        }

        if (((string?)outline.Attribute("_note"))?.Trim() is { Length: > 0 } note)
        {
            if (_budget.Paragraphs + (long)MarkupBlocks.LineCount(note) > _budget.Limits.MaxParagraphs)
                throw DocumentLimits.Exceeded($"The document has more than {DocumentLimits.Count(_budget.Limits.MaxParagraphs)} paragraphs.");
            var blocks = MarkdownReader.Read(MarkupBlocks.Lines(note), _baseFolder).Sections.SelectMany(s => s.Blocks).ToList();
            MarkupBlocks.Count(blocks, _budget);
            _blocks.AddRange(blocks);
        }
    }

    /// <summary>The text of an outline: plain, or read as HTML when it holds tags or entities.</summary>
    private List<HInline> Inlines(string text)
    {
        text = text.Replace('\n', ' ').Replace('\r', ' ');
        if (text.Trim().Length == 0)
            return [];
        if (!text.Contains('<', StringComparison.Ordinal) && !text.Contains('&', StringComparison.Ordinal))
            return [new HText(text.Trim(), default)];
        var content = HtmlReader.Parse(text, new HtmlReadOptions { BaseFolder = _baseFolder, MediaFolder = _media });
        var inlines = new List<HInline>();
        foreach (var paragraph in content.Blocks.OfType<HParagraph>())
        {
            if (inlines.Count > 0)
                inlines.Add(new HText(" ", default));
            inlines.AddRange(paragraph.Inlines.Where(i => i is not HImage and not HBookmark));
        }
        return inlines;
    }
}
