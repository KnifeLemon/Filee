// Keyed batch translation with gap-fill recovery, shared by the document/e-book translator and the
// subtitle translator: the texts go to the local model in one request as <sN>…</sN> markers, the answers
// that come back are kept, and only the ones still missing are asked for again — one request per round,
// in slot order, up to four. A round that recovers nothing ends the recovery, and a text that never
// comes back stays null so each caller can apply its own final fallback.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Filee.Engines.Translation;

internal static partial class KeyedTranslation
{
    /// <summary>How many times the texts that are still missing are asked for again (the answers that came back are kept).</summary>
    public const int MaxRecoveryRounds = 4;

    /// <summary>
    /// Translates <paramref name="texts"/> in one keyed request and recovers partial answers: the texts
    /// that came back are kept and only the missing ones are asked for again — one keyed request per
    /// round, in slot order, up to <see cref="MaxRecoveryRounds"/>. An answer with no visible text counts
    /// as a miss, so a text is never emptied by a stray marker, and if a text comes back twice, the first
    /// answer stays. Returns one trimmed, marker-free entry per text; <c>null</c> means it never came
    /// back and the caller applies its own fallback.
    /// </summary>
    public static async Task<IReadOnlyList<string?>> TranslateAsync(
        IReadOnlyList<string> texts, string toLang, string fromLang,
        ITranslationService service, CancellationToken cancellationToken)
    {
        var results = new string?[texts.Count];
        var missing = Enumerable.Range(0, texts.Count).ToHashSet();

        for (var round = 0; round < MaxRecoveryRounds && missing.Count > 0; round++)
        {
            // Slot order on every request: the model reads the runs as one text, so a re-asked run
            // belongs where it was, not at the end of the queue.
            var asked = missing.OrderBy(i => i).ToList();
            var reply = await service.TranslateTextAsync(Keyed(asked, texts), toLang, fromLang, cancellationToken);
            var recovered = 0;
            foreach (Match match in SlotTag().Matches(reply))
            {
                if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    continue;
                if (index < 0 || index >= texts.Count || !missing.Remove(index))
                    continue; // out of range, or already answered in an earlier round
                var clean = StripMarkers(match.Groups[2].Value).Trim();
                if (clean.Length == 0)
                {
                    missing.Add(index); // answered empty: ask for it again
                    continue;
                }
                results[index] = clean;
                recovered++;
            }
            // The model ignored the markers (or answered empty): asking again the same way would only
            // repeat the miss, so at most one identical round is wasted.
            if (recovered == 0 && round > 0)
                break;
        }

        return results;
    }

    /// <summary>Keyed text, one marker per text, in slot order: <c>&lt;s0&gt;…&lt;/s0&gt;\n&lt;s1&gt;…&lt;/s1&gt;</c></summary>
    private static string Keyed(List<int> indices, IReadOnlyList<string> texts)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < indices.Count; i++)
        {
            if (i > 0)
                sb.Append('\n');
            sb.Append("<s").Append(indices[i]).Append('>').Append(texts[indices[i]])
              .Append("</s").Append(indices[i]).Append('>');
        }
        return sb.ToString();
    }

    /// <summary>Removes markers the model echoed back inside the content of a run.</summary>
    private static string StripMarkers(string text) =>
        Regex.Replace(text, @"<\/?s\d+>", "");

    [GeneratedRegex(@"<s(\d+)>(.*?)</s\1>", RegexOptions.Singleline)]
    private static partial Regex SlotTag();
}
