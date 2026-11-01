// HDocument → OPML 2.0, as Pandoc writes it: each heading becomes an outline inside the outline of the heading above
// it, the text between two headings the outline's _note in Markdown (MarkdownWriter, pictures linked into the
// "<name>_files" folder). A heading that is one link becomes an outline of type "link". Text before the first heading
// goes into an outline without text. The title is head/title.

using System.Text;
using System.Xml;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Writes the document model as an OPML outline.</summary>
internal static class OpmlWriter
{
    private sealed class Outline(int level)
    {
        public int Level { get; } = level;
        public string Text { get; set; } = "";
        public string? Url { get; set; }
        public List<HBlock> Note { get; } = [];
        public List<Outline> Children { get; } = [];
    }

    /// <param name="pictures">Maps a picture file to the link written into a note, or null to leave it out.</param>
    public static string Write(HDocument document, Func<string, string?> pictures)
    {
        var root = new Outline(0);
        var stack = new List<Outline> { root };
        foreach (var block in document.Sections.SelectMany(s => s.Blocks))
        {
            if (block is HParagraph { HeadingLevel: > 0 } heading)
            {
                while (stack.Count > 1 && stack[^1].Level >= heading.HeadingLevel)
                    stack.RemoveAt(stack.Count - 1);
                var outline = new Outline(heading.HeadingLevel) { Text = Text(heading.Inlines) };
                var parts = heading.Inlines.Where(i => i is not HBookmark).ToList();
                if (parts is [HLink { Target: var target } link] && !target.StartsWith('#'))
                {
                    outline.Url = target;
                    outline.Text = Text(link.Content);
                }
                stack[^1].Children.Add(outline);
                stack.Add(outline);
                continue;
            }
            var current = stack[^1];
            if (current == root)
            {
                // Before the first heading: an outline without text holds it.
                if (root.Children.Count == 0 || root.Children[^1].Text.Length > 0 || root.Children[^1].Url is not null)
                    root.Children.Add(new Outline(1));
                current = root.Children[^1];
            }
            current.Note.Add(block);
        }

        var sb = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineHandling = NewLineHandling.Entitize,
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(false),
        };
        using (var writer = XmlWriter.Create(sb, settings))
        {
            writer.WriteStartElement("opml");
            writer.WriteAttributeString("version", "2.0");
            writer.WriteStartElement("head");
            writer.WriteElementString("title", Clean(document.Title?.Trim() ?? ""));
            writer.WriteEndElement();
            writer.WriteStartElement("body");
            foreach (var outline in root.Children)
                Write(writer, outline, pictures);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"UTF-8\"", StringComparison.Ordinal) + "\n";
    }

    private static void Write(XmlWriter writer, Outline outline, Func<string, string?> pictures)
    {
        writer.WriteStartElement("outline");
        writer.WriteAttributeString("text", Clean(outline.Text));
        if (outline.Url is { } url)
        {
            writer.WriteAttributeString("type", "link");
            writer.WriteAttributeString("url", Clean(url));
        }
        if (outline.Note.Count > 0)
        {
            var section = new HSection();
            section.Blocks.AddRange(outline.Note);
            var note = new HDocument();
            note.Sections.Add(section);
            var markdown = MarkdownWriter.Write(note, pictures).Trim();
            if (markdown.Length > 0)
                writer.WriteAttributeString("_note", Clean(markdown));
        }
        foreach (var child in outline.Children)
            Write(writer, child, pictures);
        writer.WriteEndElement();
    }

    /// <summary>
    /// An outline's text: plain, or as HTML (which OPML allows and OpmlReader reads) when it has emphasis or code.
    /// </summary>
    private static string Text(List<HInline> inlines)
    {
        var runs = HDocumentWalker.InlinesOf(inlines).OfType<HText>().ToList();
        if (runs.All(r => r.Format is { Bold: not true, Italic: not true, Underline: not true, Strike: not true, Superscript: not true, Subscript: not true }
                          && r.Format.Shade != HtmlReader.CodeShade))
        {
            // Plain text, unless it has what OpmlReader would take for HTML: then it is HTML that shows it.
            var plain = HDocumentWalker.Text(inlines);
            return plain.Contains('<', StringComparison.Ordinal) || plain.Contains('&', StringComparison.Ordinal)
                ? System.Net.WebUtility.HtmlEncode(plain)
                : plain;
        }
        var sb = new StringBuilder();
        foreach (var inline in HDocumentWalker.InlinesOf(inlines))
        {
            if (inline is HLineBreak or HTab)
                sb.Append(' ');
            if (inline is not HText run)
                continue;
            var text = System.Net.WebUtility.HtmlEncode(run.Text);
            var format = run.Format;
            if (format.Shade == HtmlReader.CodeShade)
                text = $"<code>{text}</code>";
            if (format.Superscript is true)
                text = $"<sup>{text}</sup>";
            else if (format.Subscript is true)
                text = $"<sub>{text}</sub>";
            if (format.Underline is true)
                text = $"<u>{text}</u>";
            if (format.Strike is true)
                text = $"<s>{text}</s>";
            if (format.Italic is true)
                text = $"<i>{text}</i>";
            if (format.Bold is true)
                text = $"<b>{text}</b>";
            sb.Append(text);
        }
        return sb.ToString().Trim();
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
