using System.Text;
using System.Text.RegularExpressions;
using Filee.Engines.Ebooks;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Translation;

/// <summary>
/// Traverses and translates the in-memory HDocument AST while preserving all structure, tags, styles, images, and tables.
/// </summary>
internal static class DocumentTranslator
{
    private static readonly Regex TagRegex = new(@"<s(\d+)>(.*?)<\/s\1>", RegexOptions.Singleline | RegexOptions.Compiled);

    internal static async Task TranslateBookAsync(Book book, string toLang, string fromLang, ITranslationService service, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(book.Metadata.Title))
        {
            book.Metadata.Title = await service.TranslateTextAsync(book.Metadata.Title, toLang, fromLang, cancellationToken);
        }
        if (!string.IsNullOrWhiteSpace(book.Metadata.Description))
        {
            book.Metadata.Description = await service.TranslateTextAsync(book.Metadata.Description, toLang, fromLang, cancellationToken);
        }
        book.Metadata.Language = TranslationService.NormalizeLanguage(toLang);

        await TranslateDocumentAsync(book.Document, toLang, fromLang, service, progress, cancellationToken);
    }

    internal static async Task TranslateDocumentAsync(HDocument document, string toLang, string fromLang, ITranslationService service, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(document.Title))
        {
            document.Title = await service.TranslateTextAsync(document.Title, toLang, fromLang, cancellationToken);
        }

        var paragraphs = CollectParagraphs(document).ToList();
        if (paragraphs.Count == 0)
        {
            progress?.Report(1.0);
            return;
        }

        // Filter paragraphs that contain actual translatable text
        var workItems = new List<(HParagraph Paragraph, string EncodedText, List<(int Index, HText TextInline)> Map)>();
        foreach (var p in paragraphs)
        {
            var textInlines = new List<(int Index, HText TextInline)>();
            for (var i = 0; i < p.Inlines.Count; i++)
            {
                if (p.Inlines[i] is HText textNode && !string.IsNullOrWhiteSpace(textNode.Text))
                {
                    textInlines.Add((i, textNode));
                }
            }

            if (textInlines.Count == 0)
                continue;

            if (textInlines.Count == 1)
            {
                // Single inline: no tags needed
                workItems.Add((p, textInlines[0].TextInline.Text, textInlines));
            }
            else
            {
                // Multiple inlines: wrap with <s{i}> tags so formatting boundaries are maintained
                var sb = new StringBuilder();
                for (var i = 0; i < p.Inlines.Count; i++)
                {
                    if (p.Inlines[i] is HText textNode)
                    {
                        sb.Append($"<s{i}>{textNode.Text}</s{i}>");
                    }
                }
                workItems.Add((p, sb.ToString(), textInlines));
            }
        }

        if (workItems.Count == 0)
        {
            progress?.Report(1.0);
            return;
        }

        // Translate in chunks of up to 25 paragraphs
        const int chunkSize = 25;
        var total = workItems.Count;
        var done = 0;

        for (var i = 0; i < workItems.Count; i += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = workItems.Skip(i).Take(chunkSize).ToList();
            var texts = chunk.Select(c => c.EncodedText).ToList();

            var translatedBatch = await service.TranslateBatchAsync(texts, toLang, fromLang, cancellationToken);

            for (var j = 0; j < chunk.Count; j++)
            {
                var item = chunk[j];
                var translated = translatedBatch[j];

                if (item.Map.Count == 1)
                {
                    var (idx, node) = item.Map[0];
                    item.Paragraph.Inlines[idx] = new HText(translated, node.Format);
                }
                else
                {
                    // Match back <s{i}> tags
                    var matches = TagRegex.Matches(translated);
                    var matchedIndices = new HashSet<int>();

                    foreach (Match match in matches)
                    {
                        if (int.TryParse(match.Groups[1].Value, out var inlineIdx)
                            && inlineIdx >= 0 && inlineIdx < item.Paragraph.Inlines.Count
                            && item.Paragraph.Inlines[inlineIdx] is HText originalNode)
                        {
                            item.Paragraph.Inlines[inlineIdx] = new HText(match.Groups[2].Value, originalNode.Format);
                            matchedIndices.Add(inlineIdx);
                        }
                    }

                    // Fallback if tags were stripped or altered by the model
                    if (matchedIndices.Count == 0 && item.Map.Count > 0)
                    {
                        var (firstIdx, firstNode) = item.Map[0];
                        // Put full text in first inline, empty subsequent inlines
                        item.Paragraph.Inlines[firstIdx] = new HText(StripTags(translated), firstNode.Format);
                        for (var k = 1; k < item.Map.Count; k++)
                        {
                            var (subIdx, subNode) = item.Map[k];
                            item.Paragraph.Inlines[subIdx] = new HText("", subNode.Format);
                        }
                    }
                }
            }

            done += chunk.Count;
            progress?.Report((double)done / total);
        }

        progress?.Report(1.0);
    }

    private static string StripTags(string text) =>
        Regex.Replace(text, @"<\/?[a-zA-Z0-9]+.*?>", "");

    private static IEnumerable<HParagraph> CollectParagraphs(HDocument doc)
    {
        foreach (var sec in doc.Sections)
        {
            foreach (var p in CollectBlockParagraphs(sec.Blocks))
                yield return p;
        }
    }

    private static IEnumerable<HParagraph> CollectBlockParagraphs(IEnumerable<HBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HParagraph p:
                    yield return p;
                    break;
                case HTable table:
                    foreach (var p in CollectBlockParagraphs(table.Caption))
                        yield return p;
                    foreach (var cell in table.Rows.SelectMany(r => r.Cells))
                    {
                        foreach (var p in CollectBlockParagraphs(cell.Blocks))
                            yield return p;
                    }
                    break;
            }
        }
    }
}
