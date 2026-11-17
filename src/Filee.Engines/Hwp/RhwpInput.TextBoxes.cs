// Text boxes centred or aligned to the bottom (issue #1352). rhwp 0.8.7 takes the height of a text box's text, which
// both need, from the paragraphs' stored lines (hp:linesegarray). Paragraphs Filee writes have none, so the text
// counted as 0 high: a centred slide title came out half a line low, and a bottom-aligned one (the title of
// PowerPoint's Title Slide layout) a whole line low, below its box and over the subtitle.
//
// In the copy rhwp renders, such a box is aligned to the top instead, and the room its text leaves goes into the top
// margin (all of it for the bottom, half for the centre). The text's height is worked out the way rhwp lays it out:
// lines as tall as their largest characters, as far apart as the paragraph's line spacing says, broken where the
// characters' advances (from the installed fonts) fill the box. Boxes with pictures, tables or equations in their text
// are left alone, and so are paragraphs with stored lines (saved by 한글), which rhwp aligns right.

using System.Xml.Linq;

namespace Filee.Engines.Hwp;

internal static partial class RhwpInput
{
    /// <summary>
    /// Centred and bottom-aligned text boxes without stored lines become top-aligned ones whose top margin holds the
    /// room above their text. True when something changed.
    /// </summary>
    internal static bool AlignTextBoxes(XDocument section, XDocument header, Func<string, InstalledFont?> lookup)
    {
        var boxes = section.Descendants()
            .Where(e => e.Name.LocalName == "subList" && e.Parent?.Name.LocalName == "drawText" && (string?)e.Attribute("vertAlign") is "CENTER" or "BOTTOM")
            .ToList();
        if (boxes.Count == 0)
            return false;

        var metrics = new TextMetrics(header, lookup);
        var changed = false;
        foreach (var box in boxes)
        {
            var paragraphs = box.Elements().Where(e => e.Name.LocalName == "p").ToList();
            if (paragraphs.Count == 0 || paragraphs.Any(p => Child(p, "linesegarray") is not null)
                || paragraphs.Any(p => p.Elements().Where(e => e.Name.LocalName == "run").Elements().Any(e => e.Name.LocalName is not ("t" or "ctrl"))))
                continue;
            var margin = Child(box.Parent, "textMargin");
            var size = Child(box.Parent!.Parent, "sz");
            if (margin is null || Number(size, "width") is not { } width || Number(size, "height") is not { } height)
                continue;
            var top = Number(margin, "top") ?? 0;
            var inner = height - top - (Number(margin, "bottom") ?? 0);
            var textWidth = width - (Number(margin, "left") ?? 0) - (Number(margin, "right") ?? 0);
            if (inner <= 0 || textWidth <= 0)
                continue;

            // Text taller than the box keeps to the top, as rhwp draws a top-aligned box.
            var room = Math.Max(0, inner - metrics.Height(paragraphs, textWidth));
            margin.SetAttributeValue("top", top + ((string?)box.Attribute("vertAlign") == "BOTTOM" ? room : room / 2));
            box.SetAttributeValue("vertAlign", "TOP");
            changed = true;
        }
        return changed;
    }

    /// <summary>Character and paragraph shapes of an HWPX header, for laying out text the way rhwp does.</summary>
    private sealed class TextMetrics
    {
        private readonly Dictionary<string, XElement> _characters;
        private readonly Dictionary<string, XElement> _paragraphs;
        private readonly Dictionary<string, Dictionary<string, string>> _faces;
        private readonly Func<string, InstalledFont?> _lookup;

        public TextMetrics(XDocument header, Func<string, InstalledFont?> lookup)
        {
            _lookup = lookup;
            _characters = Shapes(header, "charPr");
            _paragraphs = Shapes(header, "paraPr");
            _faces = header.Descendants().Where(e => e.Name.LocalName == "fontface")
                .GroupBy(e => ((string?)e.Attribute("lang") ?? "").ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First().Elements().Where(f => f.Name.LocalName == "font")
                    .GroupBy(f => (string?)f.Attribute("id") ?? "").ToDictionary(f => f.Key, f => (string?)f.First().Attribute("face") ?? ""));
        }

        private static Dictionary<string, XElement> Shapes(XDocument header, string name) =>
            header.Descendants().Where(e => e.Name.LocalName == name)
                .GroupBy(e => (string?)e.Attribute("id") ?? "").ToDictionary(g => g.Key, g => g.First());

        /// <summary>
        /// Height of the paragraphs in a box <paramref name="width"/> wide: from the top of the first line to the bottom
        /// of the last (rhwp leaves the last line's spacing out).
        /// </summary>
        public int Height(IReadOnlyList<XElement> paragraphs, int width)
        {
            double y = 0, bottom = 0;
            foreach (var paragraph in paragraphs)
            {
                var shape = _paragraphs.GetValueOrDefault((string?)paragraph.Attribute("paraPrIDRef") ?? "");
                var margin = Own(shape, "margin");
                int Margin(string side) => Number(Child(margin, side), "value") ?? 0;
                var spacing = Own(shape, "lineSpacing");
                var kind = (string?)spacing?.Attribute("type") ?? "PERCENT";
                var amount = Number(spacing, "value") ?? 160;

                y += Margin("prev");
                foreach (var line in Lines(paragraph, width - Margin("left") - Margin("right"), Margin("intent")))
                {
                    bottom = y + line;
                    y += kind switch
                    {
                        "FIXED" => amount,
                        "BETWEEN_LINES" => line + amount,
                        "AT_LEAST" => Math.Max(line, amount),
                        _ => line * amount / 100.0,
                    };
                }
                y += Margin("next");
            }
            return (int)Math.Ceiling(bottom);
        }

        /// <summary>
        /// A paragraph's own element of a paragraph shape: the one in hp:case when there is a switch (hp:default holds
        /// margins doubled).
        /// </summary>
        private static XElement? Own(XElement? shape, string name) =>
            shape?.Descendants().FirstOrDefault(e => e.Name.LocalName == name && !e.Ancestors().Any(a => a.Name.LocalName == "default"));

        /// <summary>
        /// Heights of the lines a paragraph takes in <paramref name="width"/>: words stay whole unless longer than a line,
        /// Chinese, Japanese and Korean characters break anywhere, a line break starts a new line.
        /// </summary>
        private List<double> Lines(XElement paragraph, int width, int indent)
        {
            var lines = new List<double>();
            double lineWidth = 0, lineHeight = 0, fallback = 0;
            var filled = false;
            double Limit() => width - Math.Max(0, lines.Count == 0 ? indent : -indent);

            void Place(double w, double h)
            {
                lineWidth += w;
                lineHeight = Math.Max(lineHeight, h);
                filled = true;
            }

            void Break(double h)
            {
                lines.Add(lineHeight > 0 ? lineHeight : h);
                (lineWidth, lineHeight, filled) = (0, 0, false);
            }

            var word = new List<(double Width, double Height)>();
            void PlaceWord()
            {
                if (word.Count == 0)
                    return;
                var total = word.Sum(c => c.Width);
                if (filled && lineWidth + total > Limit())
                    Break(0);
                if (total <= Limit())
                    Place(total, word.Max(c => c.Height));
                else
                    foreach (var (w, h) in word)
                    {
                        if (filled && lineWidth + w > Limit())
                            Break(0);
                        Place(w, h);
                    }
                word.Clear();
            }

            foreach (var run in paragraph.Elements().Where(e => e.Name.LocalName == "run"))
            {
                var shape = _characters.GetValueOrDefault((string?)run.Attribute("charPrIDRef") ?? "");
                var height = Number(shape, "height") ?? 1000;
                if (fallback == 0)
                    fallback = height;
                foreach (var node in run.Elements().Where(e => e.Name.LocalName == "t").Nodes())
                {
                    if (node is XElement element)
                    {
                        PlaceWord();
                        if (element.Name.LocalName == "lineBreak")
                            Break(height);
                        else
                            Place(element.Name.LocalName == "tab" ? height * 2 : Advance(' ', shape, height), height);
                        continue;
                    }
                    if (node is not XText text)
                        continue;
                    foreach (var c in text.Value)
                    {
                        if (char.IsWhiteSpace(c))
                        {
                            PlaceWord();
                            Place(Advance(c, shape, height), height);
                        }
                        else if (Wide(c))
                        {
                            PlaceWord();
                            word.Add((Advance(c, shape, height), height));
                            PlaceWord();
                        }
                        else
                        {
                            word.Add((Advance(c, shape, height), height));
                        }
                    }
                }
            }
            PlaceWord();
            Break(fallback > 0 ? fallback : 1000);
            return lines;
        }

        /// <summary>Advance of <paramref name="c"/> in HWPUNIT: its font's width, scaled and spaced as the shape says.</summary>
        private double Advance(char c, XElement? shape, int height)
        {
            var slot = Slot(c);
            int Value(string element, int fallback) => Number(Child(shape, element), slot) ?? fallback;
            var fontId = (string?)Child(shape, "fontRef")?.Attribute(slot);
            var face = fontId is not null && _faces.TryGetValue(slot, out var list) ? list.GetValueOrDefault(fontId) : null;
            var em = face is { Length: > 0 } && _lookup(face) is { Advance: { } advance } && advance(c) is { } real
                ? real
                : Wide(c) ? 1.0 : 0.5;
            var size = height * Value("relSz", 100) / 100.0;
            return em * size * Value("ratio", 100) / 100.0 * (100 + Value("spacing", 0)) / 100.0;
        }

        /// <summary>The language slot of a character shape a character is drawn with.</summary>
        private static string Slot(char c) => c switch
        {
            >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏' or >= '가' and <= '힯' => "hangul",
            >= '぀' and <= 'ヿ' => "japanese",
            >= '㐀' and <= '䶿' or >= '一' and <= '鿿' or >= '豈' and <= '﫿' => "hanja",
            < 'ɐ' or >= 'Ḁ' and <= 'ỿ' or >= ' ' and <= '⁯' => "latin",
            _ => "hangul",
        };

        /// <summary>Full-width characters: Chinese, Japanese and Korean, which lines break between.</summary>
        private static bool Wide(char c) => c is >= 'ᄀ' and <= 'ᇿ' or >= '⺀' and <= '꓏' or >= '가' and <= '힯'
            or >= '豈' and <= '﫿' or >= '＀' and <= '｠';
    }
}
