// What the readers of the lightweight markup languages (Jupyter, OPML, Textile, MediaWiki, reStructuredText, Org,
// DocBook) share: reading the file within DocumentLimits, code blocks as the other readers mark them (shaded lines, an
// empty shaded run for a blank line so a block stays one block), list definitions, and counting what is built against
// a DocumentBudget, so a small file can't expand into millions of paragraphs.

using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Helpers for the markup readers.</summary>
internal static class MarkupBlocks
{
    /// <summary>Indent per block quote level, as the Markdown and HTML readers indent quotes.</summary>
    public const int QuoteIndent = 2000;

    /// <summary>Code, as MarkdownReader and HtmlReader mark it (the writers turn it back into code blocks).</summary>
    public static readonly HCharFormat Code = new(Shade: HtmlReader.CodeShade);

    /// <summary>
    /// The text of a markup file in the encoding its PC used (<see cref="DocumentReaders.DecodeText(byte[])"/>), with
    /// Unix line ends. Files larger than <see cref="DocumentLimits.MaxPartBytes"/> are refused before they are read.
    /// </summary>
    public static string ReadText(string path, DocumentLimits limits, string kind)
    {
        if (new FileInfo(path).Length > limits.MaxPartBytes)
            throw DocumentLimits.Exceeded($"The {kind} file is larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
        return Lines(DocumentReaders.DecodeText(File.ReadAllBytes(path)));
    }

    /// <summary>Text with "\n" line ends.</summary>
    public static string Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>True for a paragraph that is all code (what the writers write as a code block).</summary>
    public static bool IsCode(HBlock? block) =>
        block is HParagraph { List: null, HeadingLevel: 0 } paragraph && paragraph.Inlines.Count > 0
        && paragraph.Inlines.All(i => i is HText { Format.Shade: HtmlReader.CodeShade });

    /// <summary>True for a paragraph without text or objects (a separator).</summary>
    public static bool IsEmpty(HBlock block) =>
        block is HParagraph { List: null, HeadingLevel: 0 } paragraph
        && paragraph.Inlines.All(i => (i is HText text && text.Text.Trim().Length == 0) || i is HBookmark);

    /// <summary>
    /// Code lines as shaded paragraphs, one per line; a blank line gets an empty shaded run, so the writers keep the
    /// block in one piece. Each line counts as a paragraph and is marked as code for the PPTX writer.
    /// </summary>
    public static void CodeLines(IEnumerable<string> lines, List<HBlock> output, DocumentBudget budget, int indent = 0)
    {
        foreach (var line in lines)
        {
            budget.Paragraph();
            var paragraph = new HParagraph { Format = indent > 0 ? new HParaFormat(Left: indent) : default, Role = HBlockRole.Code };
            paragraph.Inlines.Add(new HText(line.Replace("\t", "    ", StringComparison.Ordinal), Code));
            output.Add(paragraph);
        }
    }

    /// <summary>
    /// An empty paragraph between two code blocks, so they stay two blocks (the writers join consecutive code lines).
    /// </summary>
    public static void SeparateCode(List<HBlock> output, DocumentBudget budget)
    {
        if (output.Count > 0 && IsCode(output[^1]))
        {
            budget.Paragraph();
            output.Add(new HParagraph());
        }
    }

    /// <summary>A horizontal rule: a short centred line, as HtmlReader reads &lt;hr&gt;.</summary>
    public static void Rule(List<HBlock> output, DocumentBudget budget)
    {
        budget.Paragraph();
        budget.Object();
        var paragraph = new HParagraph { Format = new HParaFormat(Align: HAlign.Center), Role = HBlockRole.Rule };
        paragraph.Inlines.Add(new HShape(HShapeKind.Line) { Width = 14000, Height = 0, Line = HBorder.Thin with { Color = "#808080" } });
        output.Add(paragraph);
    }

    /// <summary>
    /// A list definition: bullets, or numbers in <paramref name="format"/> (DIGIT, LATIN_SMALL, ROMAN_CAPITAL, ...)
    /// starting at <paramref name="start"/> on <paramref name="level"/>; the other levels use the usual defaults.
    /// </summary>
    public static HNumbering Numbering(bool ordered, int start = 1, string format = "DIGIT", int level = 0)
    {
        string[] bullets = ["●", "○", "■"];
        var numbering = new HNumbering();
        for (var l = 0; l < 9; l++)
        {
            numbering.Levels.Add(ordered
                ? new HNumberingLevel(l == level ? format : "DIGIT", $"^{l + 1}.", l == level ? Math.Max(0, start) : 1, Bullet: false)
                : new HNumberingLevel("DIGIT", bullets[l % 3], 1, Bullet: true));
        }
        return numbering;
    }

    /// <summary>Counts the paragraphs and objects of blocks another reader built (Markdig, HtmlReader).</summary>
    public static void Count(IEnumerable<HBlock> blocks, DocumentBudget budget)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HParagraph paragraph:
                    budget.Paragraph();
                    foreach (var inline in paragraph.Inlines)
                    {
                        if (inline is HObject)
                            budget.Object();
                        else if (inline is HNote note)
                            Count(note.Blocks, budget);
                    }
                    break;
                case HTable table:
                    budget.Object();
                    foreach (var cell in table.Rows.SelectMany(r => r.Cells))
                        Count(cell.Blocks, budget);
                    break;
            }
        }
    }

    /// <summary>Lines of <paramref name="text"/> (at least one).</summary>
    public static int LineCount(string text)
    {
        var count = 1;
        foreach (var ch in text)
            if (ch == '\n')
                count++;
        return count;
    }
}
