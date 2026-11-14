// Punctuation and letter widths for rhwp (issues #237 and #1262).

// Punctuation widths for rhwp (issue #237). rhwp lays text out with width tables of its own: Korean fonts and common
// Latin fonts have real ones, every other font (SimSun, Microsoft YaHei, KaiTi, MS Mincho, Noto Serif CJK SC, …) gets
// a guess in which quotes, dashes and CJK punctuation are half a character wide (‘ ’ 0.3). In Chinese and Japanese
// fonts those are full width, and the PDF draws the real glyph at the guessed position: an opening quote or bracket
// (drawn in the right half of its square) lands on the next character, 、 and 。 squeeze the text after them.
//
// In the copy rhwp renders, such characters get a run of their own whose character shape adds letter spacing up to
// the font's real width. rhwp's letter spacing is a percentage of the character's own width, so 100 % doubles it.
//
// Letters (issue #1262, rhwp#8143). rhwp's width tables stop at ASCII and Latin-1 (plus punctuation, math and CJK):
// every other letter — Cyrillic, Greek, Polish, Turkish or Vietnamese letters beyond Latin-1, Armenian, Georgian —
// is laid out at a fixed half-em whatever the font, so "Привет" came out with its letters on top of each other. A
// document with such text goes through LibreOffice when that is installed; without it, rhwp draws it, and the letters
// get the same treatment as the punctuation: a run of their own whose letter spacing takes the half-em up (or down)
// to the font's real advance, in steps of 5 % so that neighbouring letters of about the same width share a run.
// The font that sets the width is the one rhwp draws the letter with: LATIN for Latin Extended (U+0100–024F,
// U+1E00–1EFF), HANGUL for every other script — in Chinese and Japanese text, the East Asian font.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Hwp;

internal static partial class RhwpInput
{
    /// <summary>
    /// Characters rhwp guesses narrower than CJK fonts draw them: the language slot it draws them with and its guess
    /// (in em) for fonts it has no table for. Measured with rhwp 0.8.7.
    /// </summary>
    private static readonly Dictionary<char, (string Slot, double Guess)> GuessedWidths = BuildGuesses();

    private static readonly string[] Slots = ["hangul", "latin", "hanja", "japanese", "other", "symbol", "user"];

    /// <summary>rhwp's width for a letter outside its tables, in em (rhwp#8143), whatever the font.</summary>
    private const double UnmeasuredLetterGuess = 0.5;

    /// <summary>Letter spacing of such a letter is rounded to a multiple of this, in percent.</summary>
    private const int LetterSpacingStep = 5;

    /// <summary>
    /// The language slots rhwp draws such a letter with, the right one first (measured with a different font in each
    /// slot, rhwp 0.8.7): Latin Extended letters come from LATIN, Cyrillic, Greek, Armenian, Georgian and the rest
    /// from HANGUL. OTHER is not used. The second slot only asks when the first one's font gives no width.
    /// </summary>
    private static string[] LetterSlots(char c) =>
        c is >= '\u0100' and <= '\u024F' or >= '\u1E00' and <= '\u1EFF' ? new[] { "LATIN", "HANGUL" } : new[] { "HANGUL", "LATIN" };

    private static Dictionary<char, (string, double)> BuildGuesses()
    {
        var guesses = new Dictionary<char, (string, double)>
        {
            ['‘'] = ("HANGUL", 0.3),
            ['’'] = ("HANGUL", 0.3),
        };
        foreach (var c in "“”—―–‥…•※‰′℃")
            guesses[c] = ("HANGUL", 0.5);
        foreach (var c in "、。〃々〇〈〉《》「」『』【】〔〕〖〗〝〞")
            guesses[c] = ("SYMBOL", 0.5);
        return guesses;
    }

    /// <summary>
    /// Gives the punctuation above its font's real width in rhwp's layout (see the file comment). True when something
    /// changed.
    /// </summary>
    internal static bool WidenPunctuation(XDocument header, IReadOnlyList<XDocument> sections, Func<string, InstalledFont?> lookup)
    {
        var charProperties = header.Descendants().FirstOrDefault(e => e.Name.LocalName == "charProperties");
        if (charProperties is null)
            return false;
        var shapes = charProperties.Elements().Where(e => e.Name.LocalName == "charPr").ToDictionary(e => (string?)e.Attribute("id") ?? "");
        var faces = header.Descendants().Where(e => e.Name.LocalName == "fontface")
            .ToDictionary(e => (string?)e.Attribute("lang") ?? "", e => e.Elements().Where(f => f.Name.LocalName == "font")
                .ToDictionary(f => (string?)f.Attribute("id") ?? "", f => (string?)f.Attribute("face") ?? ""));
        var nextId = shapes.Keys.Select(k => int.TryParse(k, out var id) ? id : 0).DefaultIfEmpty(-1).Max() + 1;
        var spacings = new Dictionary<(string Id, char Character), int?>();
        var widened = new Dictionary<(string Id, int Spacing), string>();

        // The letter spacing that brings a character in this shape to its real width, or null when rhwp's is right.
        int? Spacing(string id, char c)
        {
            if (spacings.TryGetValue((id, c), out var known))
                return known;
            int? result = null;
            if (GuessedWidths.TryGetValue(c, out var guess) && shapes.TryGetValue(id, out var shape))
            {
                var slot = guess.Slot.ToLowerInvariant();
                var fontId = (string?)Child(shape, "fontRef")?.Attribute(slot);
                var face = fontId is not null && faces.TryGetValue(guess.Slot, out var list) ? list.GetValueOrDefault(fontId) : null;
                if (face is { Length: > 0 } && lookup(face) is { Usable: true, Advance: { } advance } font && Guesses(face, font)
                    && advance(c) is { } real && real > guess.Guess + 0.05)
                {
                    var current = Number(Child(shape, "spacing"), slot) ?? 0;
                    result = Math.Clamp((int)Math.Round(real / guess.Guess * (100 + current)) - 100, -50, 500);
                }
            }
            else if (char.IsLetter(c) && ComplexScripts.IsUnmeasuredLetter(c) && shapes.TryGetValue(id, out var letterShape))
            {
                result = LetterSpacing(letterShape, c);
            }
            return spacings[(id, c)] = result;
        }

        // The letter spacing that brings a letter rhwp has no width for to the advance its font gives it, or null
        // when the guess is already right (within a step) or no installed font tells.
        int? LetterSpacing(XElement shape, char c)
        {
            foreach (var slot in LetterSlots(c))
            {
                var attribute = slot.ToLowerInvariant();
                var fontId = (string?)Child(shape, "fontRef")?.Attribute(attribute);
                var face = fontId is not null && faces.TryGetValue(slot, out var list) ? list.GetValueOrDefault(fontId) : null;
                var font = face is { Length: > 0 } ? lookup(face) : null;
                if (font is { Usable: true, Advance: { } advance } && advance(c) is { } real && real > 0)
                {
                    var current = Number(Child(shape, "spacing"), attribute) ?? 0;
                    var exact = real / UnmeasuredLetterGuess * (100 + current) - 100;
                    var stepped = Math.Clamp((int)Math.Round(exact / LetterSpacingStep) * LetterSpacingStep, -50, 200);
                    return Math.Abs(stepped - current) < LetterSpacingStep ? null : stepped;
                }
            }
            return null;
        }

        string Widened(string id, int spacing)
        {
            if (widened.TryGetValue((id, spacing), out var existing))
                return existing;
            var copy = new XElement(shapes[id]);
            var newId = (nextId++).ToString(CultureInfo.InvariantCulture);
            copy.SetAttributeValue("id", newId);
            if (Child(copy, "spacing") is not { } element)
            {
                element = new XElement(copy.Name.Namespace + "spacing");
                (Child(copy, "ratio") ?? Child(copy, "fontRef"))?.AddAfterSelf(element);
                if (element.Parent is null)
                    copy.AddFirst(element);
            }
            // The run holds only the widened characters, so every slot can take the same spacing.
            foreach (var slot in Slots)
                element.SetAttributeValue(slot, spacing);
            charProperties.Add(copy);
            return widened[(id, spacing)] = newId;
        }

        var changed = false;
        foreach (var section in sections)
        {
            foreach (var run in section.Descendants().Where(e => e.Name.LocalName == "run").ToList())
            {
                var id = (string?)run.Attribute("charPrIDRef") ?? "";
                if (!run.Elements().Any(e => e.Name.LocalName == "t" && e.Value.Any(c => Spacing(id, c) is not null)))
                    continue;
                run.ReplaceWith(SplitRun(run, c => Spacing(id, c) is { } spacing ? Widened(id, spacing) : null));
                changed = true;
            }
        }
        if (changed)
            charProperties.SetAttributeValue("itemCnt", charProperties.Elements().Count(e => e.Name.LocalName == "charPr"));
        return changed;
    }

    /// <summary>
    /// rhwp has no width table for the font (it guesses): Chinese and Japanese fonts. Korean fonts (with Hangul) and
    /// Latin fonts (without Han) have tables.
    /// </summary>
    private static bool Guesses(string face, InstalledFont font) =>
        RhwpFonts.Script(face) is "zh-Hans" or "zh-Hant" or "ja" || font.Has('一') && !font.Has('가');

    /// <summary>
    /// The run split so that the characters <paramref name="shapeOf"/> gives a character shape for are in runs of that
    /// shape; everything else (other text, tabs, controls) stays in the run's own shape, in order.
    /// </summary>
    private static List<XElement> SplitRun(XElement run, Func<char, string?> shapeOf)
    {
        var baseId = (string?)run.Attribute("charPrIDRef") ?? "";
        var pieces = new List<XElement>();
        XElement? current = null;
        string? currentId = null;
        XElement? text = null; // the hp:t of the current piece being filled, if any

        XElement Piece(string id)
        {
            if (current is null || currentId != id)
            {
                current = new XElement(run.Name, run.Attributes().Where(a => a.Name.LocalName != "charPrIDRef"));
                current.SetAttributeValue("charPrIDRef", id);
                currentId = id;
                pieces.Add(current);
                text = null;
            }
            return current;
        }

        foreach (var node in run.Nodes())
        {
            if (node is not XElement { Name.LocalName: "t" } t)
            {
                Piece(baseId).Add(node is XElement element ? new XElement(element) : node);
                text = null;
                continue;
            }
            XElement Text(string id)
            {
                var piece = Piece(id);
                if (text is null)
                {
                    text = new XElement(t.Name, t.Attributes());
                    piece.Add(text);
                }
                return text;
            }
            foreach (var part in t.Nodes())
            {
                if (part is XText value)
                {
                    // Consecutive characters of the same shape stay together ("……", "——").
                    var chars = value.Value;
                    for (var start = 0; start < chars.Length;)
                    {
                        var id = shapeOf(chars[start]) ?? baseId;
                        var end = start + 1;
                        while (end < chars.Length && (shapeOf(chars[end]) ?? baseId) == id)
                            end++;
                        Text(id).Add(chars[start..end]);
                        start = end;
                    }
                }
                else
                {
                    Text(baseId).Add(part is XElement element ? new XElement(element) : part);
                }
            }
            if (!t.Nodes().Any())
                Text(baseId);
            text = null;
        }
        return pieces;
    }
}
