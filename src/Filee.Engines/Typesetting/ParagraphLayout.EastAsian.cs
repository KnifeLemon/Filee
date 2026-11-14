// East Asian spacing in a paragraph's lines (issue #280).
//
// - Auto-spacing (Word w:autoSpaceDE and w:autoSpaceDN, 한글 autoSpacing eAsianEng and eAsianNum): where a Han, kana,
//   bopomofo or Hangul character touches a Latin, Greek or Cyrillic letter (DE, eAsianEng) or a digit (DN, eAsianNum),
//   a quarter of the East Asian character's size goes between the two, whichever side the letter is on. It counts in the
//   line's width when lines are broken, and a line that breaks there leaves it out. Full-width letters and digits are
//   East Asian text and get none; nor does punctuation on either side. 한글's stored lines are drawn as 한글 laid them
//   out, without it.
// - Justification of Word's paragraphs: a justified line spreads its room over its spaces and over the gaps beside each
//   Chinese or Japanese character (Han, kana, bopomofo, full-width punctuation), each the same share, so a Chinese line
//   holding a few spaces ("共 3 个 GPU 节点") doesn't put all of it into them. Latin words keep their letters together.
//   Korean puts spaces between its words, and Word justifies Korean text (Hangul, and Han in a Korean paragraph) at them
//   like Latin text. 한글 justifies at the spaces only (HLineBreaking.SpreadEastAsian is Word's).

using System.Text;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Typesetting;

internal sealed partial class ParagraphLayouter
{
    /// <summary>Auto-spacing as a share of the East Asian character's size.</summary>
    private const float AutoSpaceShare = 0.25f;

    /// <summary>What a character is on either side of an auto-spacing.</summary>
    private enum AutoSpaceSide
    {
        None,
        EastAsian,
        Letter,
        Digit,
    }

    /// <summary>
    /// Per UTF-16 index, the auto-spacing after the cluster that starts there (0 elsewhere); null when the paragraph has
    /// it off or has nowhere to put it.
    /// </summary>
    private static float[]? AutoSpace(ParagraphText source, List<Item> items, bool[] starts, HLineBreaking? breaking)
    {
        var (latin, digits) = (breaking?.AutoSpaceLatin == true, breaking?.AutoSpaceDigits == true);
        if (!latin && !digits)
            return null;
        var text = source.Text;
        var clusters = new List<(int Index, Item Item)>();
        foreach (var item in items.OrderBy(i => i.Start))
        {
            for (var i = item.Start; i < item.End; i++)
            {
                if (starts[i])
                    clusters.Add((i, item));
            }
        }

        float[]? result = null;
        for (var k = 0; k + 1 < clusters.Count; k++)
        {
            var (a, first) = clusters[k];
            var (b, second) = clusters[k + 1];
            if (first.Kind != ItemKind.Text || second.Kind != ItemKind.Text)
                continue;
            var (left, right) = (SideOf(text, a), SideOf(text, b));
            bool Applies(AutoSpaceSide side) => side == AutoSpaceSide.Letter ? latin : side == AutoSpaceSide.Digit && digits;
            var size = (left, right) switch
            {
                (AutoSpaceSide.EastAsian, _) when Applies(right) => first.Style.Size,
                (_, AutoSpaceSide.EastAsian) when Applies(left) => second.Style.Size,
                _ => 0,
            };
            if (size <= 0)
                continue;
            result ??= new float[text.Length];
            result[a] = size * AutoSpaceShare;
        }
        return result;
    }

    private static AutoSpaceSide SideOf(string text, int index)
    {
        var codePoint = UnicodeData.CodePointAt(text, index);
        if (codePoint is >= '0' and <= '9')
            return AutoSpaceSide.Digit;
        var script = UnicodeData.GetScript(codePoint);
        if (script is UnicodeScript.Han or UnicodeScript.Hiragana or UnicodeScript.Katakana or UnicodeScript.KatakanaOrHiragana
            or UnicodeScript.Bopomofo or UnicodeScript.Hangul)
            return AutoSpaceSide.EastAsian;
        // Full-width letters (U+FF21 …) are East Asian text.
        if (script is UnicodeScript.Latin or UnicodeScript.Greek or UnicodeScript.Cyrillic && codePoint < 0x2E80
            && Rune.IsLetter(new Rune(codePoint)))
            return AutoSpaceSide.Letter;
        return AutoSpaceSide.None;
    }

    /// <summary>
    /// The clusters of a justified line in a Word paragraph whose gap after them takes a share of the line's room: its
    /// spaces, and every cluster beside a Chinese or Japanese character (not before a space, which takes its own share).
    /// </summary>
    /// <param name="clusters">The line's clusters in text order, up to its last character before any spaces at its end.</param>
    private static List<int> JustifyGaps(string text, List<int> clusters, string language)
    {
        var korean = language == "ko";
        var gaps = new List<int>();
        for (var k = 0; k + 1 < clusters.Count; k++)
        {
            var (a, b) = (clusters[k], clusters[k + 1]);
            if (IsStretchable(text[a]))
            {
                gaps.Add(a);
                continue;
            }
            if (KindOf(text[a]) != ItemKind.Text || KindOf(text[b]) != ItemKind.Text || IsSpace(text[b]))
                continue;
            if (Spreads(text, a, korean) || Spreads(text, b, korean))
                gaps.Add(a);
        }
        return gaps;
    }

    /// <summary>A character Word's East Asian justification spreads: Chinese and Japanese text and punctuation.</summary>
    private static bool Spreads(string text, int index, bool korean)
    {
        if (korean)
            return false;
        var codePoint = UnicodeData.CodePointAt(text, index);
        var script = UnicodeData.GetScript(codePoint);
        if (script is UnicodeScript.Han or UnicodeScript.Hiragana or UnicodeScript.Katakana or UnicodeScript.KatakanaOrHiragana
            or UnicodeScript.Bopomofo)
            return true;
        // CJK punctuation (the ideographic space is stretchable already), full-width forms, the middle dot and long vowel mark.
        return codePoint is >= 0x3001 and <= 0x303F or >= 0xFF01 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or 0x30FB or 0x30FC;
    }
}
