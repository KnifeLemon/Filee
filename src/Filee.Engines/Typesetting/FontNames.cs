// Other names for the fonts documents name. 한글 documents name Korean fonts by their Korean family names
// ("함초롬바탕"), Word documents usually by the English ones ("Malgun Gothic"); both are in the fonts' name tables, but
// only DirectWrite matches either: fontconfig and CoreText may know just one. Older 한글 documents name the fonts
// built into 한글 (HFT: 한양신명조, 한양중고딕, ...), which are Hanyang's designs that Windows and Office install as
// TrueType fonts (HY신명조, HY중고딕, ...). And documents from other systems name fonts that have metric-compatible
// open replacements (Arial → Liberation Sans), which keep line breaks where they were.

namespace Filee.Engines.Typesetting;

/// <summary>Alternative names for font families.</summary>
internal static class FontNames
{
    /// <summary>Korean and English family names of the same fonts, as their name tables list them.</summary>
    private static readonly (string Korean, string English)[] KoreanNames =
    [
        ("함초롬바탕", "HCR Batang"), ("함초롬돋움", "HCR Dotum"), ("맑은 고딕", "Malgun Gothic"), ("한컴 고딕", "Hancom Gothic"),
        ("바탕", "Batang"), ("바탕체", "BatangChe"), ("궁서", "Gungsuh"), ("궁서체", "GungsuhChe"),
        ("굴림", "Gulim"), ("굴림체", "GulimChe"), ("돋움", "Dotum"), ("돋움체", "DotumChe"),
        ("HY신명조", "HYSinMyeongJo-Medium"), ("HY중고딕", "HYGothic-Medium"), ("HY견명조", "HYMyeongJo-Extra"),
        ("HY견고딕", "HYGothic-Extra"), ("HY궁서B", "HYGungSo-Bold"), ("HY그래픽M", "HYGraphic-Medium"),
        ("HY헤드라인M", "HYHeadLine-Medium"), ("HY엽서L", "HYPost-Light"), ("HY엽서M", "HYPost-Medium"),
        ("HY얕은샘물M", "HYShortSamul-Medium"), ("HY목각파임B", "HYPMokGak-Bold"),
        ("나눔고딕", "NanumGothic"), ("나눔명조", "NanumMyeongjo"), ("나눔바른고딕", "NanumBarunGothic"),
        ("나눔바른펜", "NanumBarunpen"), ("나눔스퀘어", "NanumSquare"), ("나눔스퀘어라운드", "NanumSquareRound"),
        ("나눔손글씨 붓", "Nanum Brush Script"), ("나눔손글씨 펜", "Nanum Pen Script"),
        ("나눔고딕 에코", "NanumGothic Eco"), ("나눔명조 에코", "NanumMyeongjo Eco"),
    ];

    /// <summary>Fonts built into 한글 (HFT) and the TrueType fonts of the same design.</summary>
    private static readonly Dictionary<string, string> HancomBuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["한양신명조"] = "HY신명조",
        ["신명조"] = "HY신명조",
        ["한양중고딕"] = "HY중고딕",
        ["중고딕"] = "HY중고딕",
        ["한양견명조"] = "HY견명조",
        ["견명조"] = "HY견명조",
        ["한양견고딕"] = "HY견고딕",
        ["견고딕"] = "HY견고딕",
        ["한양궁서"] = "HY궁서B",
        ["한양그래픽"] = "HY그래픽M",
        ["한양헤드라인"] = "HY헤드라인M",
    };

    /// <summary>Metric-compatible replacements: same advance widths, so text breaks into the same lines.</summary>
    private static readonly Dictionary<string, string[]> MetricCompatible = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arial"] = ["Liberation Sans", "Arimo"],
        ["Helvetica"] = ["Arial", "Liberation Sans", "Arimo"],
        ["Times New Roman"] = ["Liberation Serif", "Tinos"],
        ["Times"] = ["Times New Roman", "Liberation Serif", "Tinos"],
        ["Courier New"] = ["Liberation Mono", "Cousine"],
        ["Courier"] = ["Courier New", "Liberation Mono", "Cousine"],
        ["Calibri"] = ["Carlito"],
        ["Calibri Light"] = ["Carlito"],
        ["Cambria"] = ["Caladea"],
    };

    private static readonly Dictionary<string, string> Translations = KoreanNames
        .SelectMany(n => new[] { (n.Korean, n.English), (n.English, n.Korean) })
        .ToDictionary(p => p.Item1, p => p.Item2, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Other names to look a family up by, best first: its name in the other language, the TrueType font of a 한글
    /// built-in font (by both names), then metric-compatible replacements.
    /// </summary>
    public static IEnumerable<string> Alternatives(string family)
    {
        if (Translations.TryGetValue(family, out var translated))
            yield return translated;
        if (HancomBuiltIn.TryGetValue(family, out var builtIn))
        {
            yield return builtIn;
            if (Translations.TryGetValue(builtIn, out var english))
                yield return english;
        }
        if (MetricCompatible.TryGetValue(family, out var compatible))
        {
            foreach (var name in compatible)
                yield return name;
        }
    }

    /// <summary>The generic families of CSS and HTML: serif, sans-serif, monospace and the rest.</summary>
    public static bool IsGeneric(string family) => family.Trim().ToLowerInvariant() is "serif" or "sans-serif" or "sans serif"
        or "monospace" or "cursive" or "fantasy" or "system-ui" or "ui-serif" or "ui-sans-serif" or "ui-monospace";

    /// <summary>Monospaced fonts, for the generic "monospace" family, best first.</summary>
    public static IReadOnlyList<string> Monospace { get; } =
        ["Consolas", "Cascadia Mono", "D2Coding", "Courier New", "Menlo", "SF Mono", "DejaVu Sans Mono", "Liberation Mono",
            "Noto Sans Mono", "GulimChe"];

    /// <summary>The generic monospaced family, for code that names no font.</summary>
    public const string MonospaceFamily = "monospace";

    /// <summary>
    /// A family that is monospaced by its name: one of <see cref="Monospace"/>, or a name saying so ("... Mono",
    /// "... Code", Courier, the fixed-width "...Che" faces such as GulimChe). A missing one is replaced by an installed monospaced font.
    /// </summary>
    public static bool IsMonospace(string family)
    {
        var name = family.Trim();
        return Monospace.Contains(name, StringComparer.OrdinalIgnoreCase)
               || name.Contains("Mono", StringComparison.OrdinalIgnoreCase) || name.Contains("Code", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Courier", StringComparison.OrdinalIgnoreCase) || name.Contains("Consol", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("Che", StringComparison.Ordinal);
    }
}
