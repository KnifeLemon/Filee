// Symbol fonts (Wingdings, Wingdings 2, Wingdings 3, Webdings, Symbol) draw their own pictures in place of letters:
// a check box picked in Word's Insert → Symbol is stored as a code of the font (w:sym w:font="Wingdings 2"
// w:char="F052"), in the private use area F020–F0FF, and text typed in such a font as the plain byte ("R"). Another
// font shows nothing or a wrong sign for those codes, so the readers write the Unicode character the picture stands
// for (#350): check boxes, check marks and crosses, circles, squares, diamonds and stars (bullets), arrows, pointing
// hands, phone and mail signs, circled numbers, and Symbol's Greek letters and math signs. Codes the tables do not
// know keep the readers' old handling.
//
// Every character here is in the Basic Multilingual Plane and in Segoe UI Symbol, which every Windows has: rhwp
// draws a character its font lacks with the first installed font that has it, so these always come out.
//
// The run keeps the symbol font and the codes as well (HCharLayout.Symbol, see Drawn): where the font is installed,
// Filee's layout draws and measures the codes in it, Word's own glyph at Word's width and line height; elsewhere it
// draws the Unicode character. A name written without its spaces ("Wingdings2", as PDF converters write it) is the
// installed font's ("Wingdings 2"), so the symbol is drawn, not a Cambria "£" as Word would for that name (its font
// table's altName).

using System.Text;

namespace Filee.Engines.Hwp.Hwpx;

internal static class SymbolFonts
{
    /// <summary>Unicode text by font (name without spaces) and code (20–FF); a space in a row skips a code.</summary>
    private static readonly Dictionary<string, Dictionary<int, string>> Tables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Wingdings"] = Table(
            (0x21, "✏✂✁"),
            (0x28, "☎✆✉✉"),
            (0x36, "⌛⌨"),
            (0x3E, "✇✍ ✌"),
            (0x45, "☜☞☝☟ ☺ ☹ ☠⚐ ✈☼ ❄ ✞ ✠✡☪☯ ☸♈♉♊♋♌♍♎♏♐♑♒♓"),
            (0x6C, "●❍■□□❑❒⬧⧫◆❖⬥⌧ ⌘"),
            (0x7D, "❝❞"),
            (0x80, "⓪①②③④⑤⑥⑦⑧⑨⑩⓿❶❷❸❹❺❻❼❽❾❿"),
            (0x9E, "·•▪○○ ◉◎ ▪☐ ✦★✶✴✹✵ ⌖⟡⌑ ✪✰"),
            (0xD5, "⌫⌦ ➢"),
            (0xDC, "➲"),
            (0xDF, "←→↑↓↖↗↙↘⬅➡⬆⬇⬉⬈⬋⬊⇦⇨⇧⇩⬄⇳⬁⬀⬃⬂"),
            (0xFB, "✗✓☒☑")),
        ["Wingdings2"] = Table(
            (0x25, "✄✂☏"),
            (0x3E, "☜☞☚☛☜☞☚☛☝☟☝☟☝☟☝☟"),
            (0x4F, "✗✓☒☑☒☒⊗⊗⊘⊘&&&&‽‽‽‽"),
            (0x69, "⓪①②③④⑤⑥⑦⑧⑨⑩⓿❶❷❸❹❺❻❼❽❾❿"),
            (0x80, "☉○☽☾ ✝✝"),
            (0x95, "·•●●○○  ⊙◉▪▪■■☐□□□▣▣▣ ⬩⬩⬩◆◇◈◈◈ ⬪⬪⬪⧫◊ ◖◗◓◒"),
            (0xCD, "✕✕✕✖✖✖✖"),
            (0xE7, "✦✦★★✶✶"),
            (0xF2, "✧☆"),
            (0xF6, "✦ ※⁂")),
        ["Wingdings3"] = Table(
            (0x21, "←→↑↓↖↗↙↘⇤⇥⤒⤓⇱⇲⇞⇟↔↕⇠⇢⇡⇣↯↵↳↰↱"),
            (0x44, "⇆⇅  ⇇⇉⇈⇊"),
            (0x50, "↻↺⎋  ⌥␣  ⇪⇦⇨"),
            (0x66, "←→↑↓↖↗↙↘↔↕▲▼△▽◀▶◁▷◣◢◤◥◂▸▴"),
            (0x80, "▾▴▾◂▸⮜⮞⮝⮟"),
            (0x89, "←→↑↓←→↑↓←→↑↓⬅➡⬆⬇←→↑↓←→↑↓⬅➡⬆⬇←→←→⬅➡"),
            (0xB1, "⬅➡"),
            (0xC5, "⬅➡⬆⬇"),
            (0xD1, "←→↑↓↖↗↙↘←→↑↓↖↗↙↘⬅➡⬆⬇⬉⬈⬋⬊")),
        ["Webdings"] = Table(
            (0x33, "◀▶▲▼⏪⏩⏮⏭⏸■●"),
            (0x59, "♥"),
            (0x61, "✔ ☐   ■ ⓘ    ●"),
            (0x72, "✖"),
            (0xC9, "☎"),
            (0xEA, "△"),
            (0xF1, "✈")),
        ["Symbol"] = Table(
            (0x21, "!∀#∃%&∋()∗+,−./0123456789:;<=>?≅ΑΒΧΔΕΦΓΗΙϑΚΛΜΝΟΠΘΡΣΤΥςΩΞΨΖ[∴]⊥_ αβχδεφγηιϕκλμνοπθρστυϖωξψζ{|}∼"),
            (0xA0, "€ϒ′≤⁄∞ƒ♣♦♥♠↔←↑→↓°±″≥×∝∂•÷≠≡≈…  ↵ℵℑℜ℘⊗⊕∅∩∪⊃⊇⊄⊂⊆∈∉∠∇®©™∏√⋅¬∧∨⇔⇐⇑⇒⇓◊⟨®©™∑"),
            (0xF1, "⟩∫")),
    };

    /// <summary>The installed fonts' names, by table name.</summary>
    private static readonly Dictionary<string, string> Installed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Wingdings2"] = "Wingdings 2",
        ["Wingdings3"] = "Wingdings 3",
    };

    private static Dictionary<int, string> Table(params (int First, string Characters)[] rows)
    {
        var table = new Dictionary<int, string>();
        foreach (var (first, characters) in rows)
        {
            for (var i = 0; i < characters.Length; i++)
            {
                if (characters[i] != ' ')
                    table[first + i] = characters[i].ToString();
            }
        }
        return table;
    }

    private static Dictionary<int, string>? Of(string? font) =>
        font is not null && Tables.TryGetValue(font.Replace(" ", "", StringComparison.Ordinal), out var table) ? table : null;

    /// <summary>Wingdings, Wingdings 2, Wingdings 3, Webdings or Symbol (any case, with or without spaces).</summary>
    public static bool IsSymbolFont(string? font) => Of(font) is not null;

    /// <summary>
    /// The Unicode character code <paramref name="code"/> of a symbol font stands for (F020–F0FF or 20–FF), or null
    /// for another font or a code the tables do not know.
    /// </summary>
    public static string? Map(string? font, int code)
    {
        if (Of(font) is not { } table)
            return null;
        if (code is >= 0xF000 and <= 0xF0FF)
            code -= 0xF000;
        return table.GetValueOrDefault(code);
    }

    /// <summary>A whole text of a symbol font in Unicode, or null unless every character is known.</summary>
    public static string? Map(string? font, string text)
    {
        if (text.Length == 0 || Of(font) is null)
            return null;
        var result = new StringBuilder();
        foreach (var c in text)
        {
            if (Map(font, c) is not { } mapped)
                return null;
            result.Append(mapped);
        }
        return result.ToString();
    }

    /// <summary>
    /// The name a symbol font is installed under ("Wingdings2" is "Wingdings 2"); another font's name as it is.
    /// </summary>
    public static string InstalledName(string font) =>
        Installed.TryGetValue(font.Replace(" ", "", StringComparison.Ordinal), out var name) ? name : font.Trim();

    /// <summary>
    /// The format the Unicode characters read from <paramref name="codes"/> of <paramref name="font"/> are drawn with:
    /// the text font, with the codes kept for the layout to draw in the symbol font where it is installed. Codes of a
    /// symbol font are made F020–F0FF, as Word's w:sym writes them.
    /// </summary>
    public static HCharFormat Drawn(HCharFormat format, string font, string codes)
    {
        var plain = WithoutSymbolFont(format);
        if (IsSymbolFont(font))
            codes = string.Concat(codes.Select(c => c is >= ' ' and <= (char)0xFF ? (char)(0xF000 + c) : c));
        return plain with { Layout = (plain.Layout ?? new HCharLayout()) with { Symbol = new HSymbol(InstalledName(font), codes) } };
    }

    /// <summary>The format without symbol fonts: the Unicode characters are drawn with the text font.</summary>
    public static HCharFormat WithoutSymbolFont(HCharFormat format)
    {
        var eastAsian = IsSymbolFont(format.EastAsianFont) ? null : format.EastAsianFont;
        // Word's own fonts lose the symbol font too, so the mapped characters are not drawn in it.
        string? Plain(string? font) => IsSymbolFont(font) ? null : font;
        var layout = format.Layout is { WordFonts: { } fonts } detail
            ? detail with { WordFonts = fonts with { Ascii = Plain(fonts.Ascii), HighAnsi = Plain(fonts.HighAnsi), EastAsia = Plain(fonts.EastAsia), ComplexScript = Plain(fonts.ComplexScript) } }
            : format.Layout;
        return format with { Font = IsSymbolFont(format.Font) ? eastAsian : format.Font, EastAsianFont = eastAsian, Layout = layout };
    }

    /// <summary>
    /// Text in <paramref name="format"/>: in a symbol font, the characters the tables know become their Unicode
    /// characters without the symbol font, and the others stay as they are.
    /// </summary>
    public static IEnumerable<HText> Runs(string text, HCharFormat format)
    {
        if (!IsSymbolFont(format.Font))
        {
            yield return new HText(text, format);
            yield break;
        }
        var run = new StringBuilder();
        var codes = new StringBuilder();
        var mappedRun = false;
        foreach (var c in text)
        {
            var mapped = Map(format.Font, c);
            if (run.Length > 0 && mappedRun != mapped is not null)
            {
                yield return Text();
                run.Clear();
                codes.Clear();
            }
            mappedRun = mapped is not null;
            run.Append(mapped ?? c.ToString());
            codes.Append(c);
        }
        if (run.Length > 0)
            yield return Text();

        HText Text() => new(run.ToString(), mappedRun ? Drawn(format, format.Font!, codes.ToString()) : format);
    }
}
