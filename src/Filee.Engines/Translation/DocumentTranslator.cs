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

    /// <summary>How many times the dropped runs of a paragraph are asked for again (the answers that came back are kept).</summary>
    private const int MaxRecoveryRounds = 4;

    private sealed class TextSlot(Func<string> get, Action<string> set, HCharFormat format)
    {
        public string Text => get();
        public void SetText(string value) => set(value);
        public HCharFormat Format => format;
    }

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

        // Collect all paragraphs with translatable text slots
        var workItems = new List<(HParagraph Paragraph, List<TextSlot> Slots)>();
        foreach (var p in paragraphs)
        {
            var slots = CollectSlots(p);
            if (slots.Count > 0)
                workItems.Add((p, slots));
        }

        if (workItems.Count == 0)
        {
            progress?.Report(1.0);
            return;
        }

        // Translate one paragraph at a time sequentially to avoid queuing inside Ollama and timeout failures
        var total = workItems.Count;
        for (var i = 0; i < workItems.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (_, slots) = workItems[i];

            if (slots.Count == 1)
            {
                var origText = slots[0].Text;
                var translated = await service.TranslateTextAsync(origText, toLang, fromLang, cancellationToken);
                slots[0].SetText(PreserveWhitespace(origText, StripSlotMarkers(translated)));
            }
            else
            {
                var sb = new StringBuilder();
                for (var k = 0; k < slots.Count; k++)
                    sb.Append($"<s{k}>{slots[k].Text}</s{k}>");

                var missing = new List<int>(Enumerable.Range(0, slots.Count));
                var reply = await service.TranslateTextAsync(sb.ToString(), toLang, fromLang, cancellationToken);
                ApplyRuns(reply, slots, missing);

                // If the model dropped some markers, the runs that came back are kept and only the missing
                // ones are asked for again — one keyed request per round, up to MaxRecoveryRounds.
                for (var round = 1; round < MaxRecoveryRounds && missing.Count > 0; round++)
                {
                    reply = await service.TranslateTextAsync(Keyed(slots, missing), toLang, fromLang, cancellationToken);
                    if (ApplyRuns(reply, slots, missing) == 0)
                        break; // the model ignored the markers: asking again the same way repeats the miss
                }

                // A run the model never returned is translated on its own so no text stays in the source
                // language and no inline formatting is lost
                foreach (var k in missing)
                {
                    var origText = slots[k].Text;
                    var slotTranslated = await service.TranslateTextAsync(origText.Trim(), toLang, fromLang, cancellationToken);
                    slots[k].SetText(PreserveWhitespace(origText, StripSlotMarkers(slotTranslated)));
                }
            }

            progress?.Report((double)(i + 1) / total);
        }

        progress?.Report(1.0);
    }

    private static List<TextSlot> CollectSlots(HParagraph p)
    {
        var slots = new List<TextSlot>();
        for (var i = 0; i < p.Inlines.Count; i++)
        {
            var inline = p.Inlines[i];
            // Skip HEquation (inherits from HText) to avoid sending formula linear text and corrupting equations
            if (inline is HText t and not HEquation)
            {
                if (!string.IsNullOrWhiteSpace(t.Text))
                {
                    var idx = i;
                    var orig = t;
                    slots.Add(new TextSlot(() => orig.Text, s => p.Inlines[idx] = new HText(s, orig.Format), orig.Format));
                }
            }
            else if (inline is HLink link)
            {
                for (var j = 0; j < link.Content.Count; j++)
                {
                    var linkInline = link.Content[j];
                    if (linkInline is HText lt and not HEquation && !string.IsNullOrWhiteSpace(lt.Text))
                    {
                        var linkIdx = j;
                        var orig = lt;
                        slots.Add(new TextSlot(() => orig.Text, s => link.Content[linkIdx] = new HText(s, orig.Format), orig.Format));
                    }
                }
            }
        }
        return slots;
    }

    /// <summary>
    /// Applies every in-range run the model returned to <paramref name="slots"/>, removes it from
    /// <paramref name="missing"/> and returns how many runs came back. An answer with no visible text
    /// counts as a miss, so a run is never emptied by a stray marker; if a run comes back twice, the
    /// first answer stays.
    /// </summary>
    private static int ApplyRuns(string reply, List<TextSlot> slots, List<int> missing)
    {
        var recovered = 0;
        foreach (Match match in TagRegex.Matches(reply))
        {
            if (!int.TryParse(match.Groups[1].Value, out var slotIdx) || slotIdx < 0 || slotIdx >= slots.Count)
                continue;
            if (!missing.Remove(slotIdx))
                continue; // already answered in an earlier round
            var clean = StripSlotMarkers(match.Groups[2].Value);
            if (clean.Trim().Length == 0)
            {
                missing.Add(slotIdx); // answered empty: ask for it again
                continue;
            }
            var origText = slots[slotIdx].Text;
            slots[slotIdx].SetText(PreserveWhitespace(origText, clean));
            recovered++;
        }
        return recovered;
    }

    /// <summary>Keyed text of the runs that are still missing, in slot order: <c>&lt;s0&gt;…&lt;/s0&gt;…</c></summary>
    private static string Keyed(List<TextSlot> slots, List<int> missing)
    {
        var sb = new StringBuilder();
        foreach (var i in missing)
            sb.Append($"<s{i}>{slots[i].Text}</s{i}>");
        return sb.ToString();
    }

    private static string StripSlotMarkers(string text) =>
        Regex.Replace(text, @"<\/?s\d+>", "");

    private static string PreserveWhitespace(string original, string translated)
    {
        var leading = new string(original.TakeWhile(char.IsWhiteSpace).ToArray());
        var trailing = new string(original.Reverse().TakeWhile(char.IsWhiteSpace).Reverse().ToArray());
        return leading + translated.Trim() + trailing;
    }

    private static IEnumerable<HParagraph> CollectParagraphs(HDocument doc)
    {
        foreach (var sec in doc.Sections)
        {
            foreach (var header in sec.Headers)
            {
                foreach (var p in CollectBlockParagraphs(header.Blocks))
                    yield return p;
            }
            foreach (var footer in sec.Footers)
            {
                foreach (var p in CollectBlockParagraphs(footer.Blocks))
                    yield return p;
            }
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
                    foreach (var inline in p.Inlines)
                    {
                        if (inline is HNote note)
                        {
                            foreach (var np in CollectBlockParagraphs(note.Blocks))
                                yield return np;
                        }
                        else if (inline is HTextBox box)
                        {
                            foreach (var bp in CollectBlockParagraphs(box.Blocks))
                                yield return bp;
                        }
                    }
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
