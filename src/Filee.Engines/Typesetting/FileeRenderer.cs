// Filee's own document renderer, as far as the layout engine goes today: each section's pages with its paper size and
// margins, paragraphs laid out by ParagraphLayouter and stacked down the body, and a new page where the next line
// doesn't fit or a paragraph asks for one; headings become the PDF outline.
//
// This flow stands in for the page composer (Project 4: "Layout engine: page composer"), which brings headers and
// footers, columns, keep-with-next and widow rules, notes and objects. Until then a table's cells are laid out one
// after another as paragraphs across the text width, so their text is there, and objects are left out.

using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;

namespace Filee.Engines.Typesetting;

internal static class FileeRenderer
{
    /// <summary>A4 with 한글's default margins, for sections without a page of their own.</summary>
    private static readonly HPage DefaultPage = new(59528, 84186, 8504, 8504, 5669, 4252, 4252, 4252, 0);

    /// <summary>The document as a PDF.</summary>
    public static byte[] ToPdf(HDocument document, CancellationToken cancellationToken = default)
    {
        using var shaper = new Shaper();
        var outline = new List<OutlineEntry>();
        var pages = Pages(document, new FontResolver(), shaper, outline, cancellationToken);
        using var output = new MemoryStream();
        PdfPageWriter.Write(output, pages, new PdfDocumentInfo { Title = document.Title, Outline = outline }, cancellationToken: cancellationToken);
        return output.ToArray();
    }

    /// <summary>The document's pages; headings are added to <paramref name="outline"/>.</summary>
    public static List<PageContent> Pages(HDocument document, FontResolver fonts, Shaper shaper, List<OutlineEntry> outline,
        CancellationToken cancellationToken = default)
    {
        var guard = new LayoutGuard(DocumentLimits.Default, cancellationToken);
        var layouter = new ParagraphLayouter(fonts, shaper, document.Kinsoku);
        var numbers = new ListNumbers();
        var pages = new List<PageContent>();
        PageContent? page = null;
        var empty = true;
        float y = 0;

        foreach (var section in document.Sections)
        {
            var size = section.Page ?? DefaultPage;
            var left = (size.Left + size.Gutter) / 100f;
            var top = (size.Top + size.Header) / 100f;
            var bottom = (size.Height - size.Bottom - size.Footer) / 100f;
            var width = size.TextWidth / 100f;

            void NewPage()
            {
                if (page is not null)
                    guard.PageFinished(empty);
                page = new PageContent(size.Width / 100f, size.Height / 100f);
                pages.Add(page);
                y = top;
                empty = true;
            }

            void Paragraph(HParagraph paragraph)
            {
                guard.Paragraph();
                if (paragraph.PageBreakBefore && !empty)
                    NewPage();
                var marker = paragraph.List is { Numbered: true } list ? numbers.Next(list) : null;
                var box = layouter.Layout(paragraph, width, marker);
                y += box.SpaceBefore;
                for (var i = 0; i < box.Lines.Count; i++)
                {
                    var line = box.Lines[i];
                    if (!empty && y + line.Height > bottom + 0.01f)
                        NewPage();
                    if (i == 0 && paragraph.HeadingLevel > 0)
                        outline.Add(new OutlineEntry(Title(paragraph), pages.Count - 1, y, paragraph.HeadingLevel - 1));
                    line.Draw(page!, left, y);
                    y += line.Height;
                    empty = false;
                }
                y += box.SpaceAfter;
            }

            void Blocks(IEnumerable<HBlock> blocks)
            {
                foreach (var block in blocks)
                {
                    switch (block)
                    {
                        case HParagraph paragraph:
                            Paragraph(paragraph);
                            break;
                        case HTable table:
                            if (table.PageBreakBefore && !empty)
                                NewPage();
                            foreach (var cell in table.Rows.SelectMany(r => r.Cells))
                                Blocks(cell.Blocks);
                            Blocks(table.Caption);
                            break;
                    }
                }
            }

            NewPage();
            Blocks(section.Blocks);
        }
        if (page is null)
        {
            page = new PageContent(DefaultPage.Width / 100f, DefaultPage.Height / 100f);
            pages.Add(page);
        }
        guard.PageFinished(empty);
        return pages;
    }

    private static string Title(HParagraph paragraph) =>
        string.Concat(paragraph.Inlines.Select(i => i switch
        {
            HText text => text.Text,
            HLink link => string.Concat(link.Content.OfType<HText>().Select(t => t.Text)),
            HTab => " ",
            _ => "",
        })).Trim();
}
