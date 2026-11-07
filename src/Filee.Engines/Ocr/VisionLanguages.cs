// Which languages of the preset (PdfOptions.OcrLanguage, Tesseract codes from OcrLanguages) Apple Vision reads, as
// the language identifiers Vision takes. Vision says at run time which it reads (more with each macOS version); a
// language it doesn't is read by Tesseract instead (VisionConverter).

namespace Filee.Engines.Ocr;

internal static class VisionLanguages
{
    /// <summary>Tesseract code → Vision identifiers it may go by, the usual one first.</summary>
    private static readonly Dictionary<string, string[]> ByCode = new()
    {
        ["eng"] = ["en-US"],
        ["deu"] = ["de-DE"],
        ["fra"] = ["fr-FR"],
        ["spa"] = ["es-ES"],
        ["por"] = ["pt-BR"],
        ["ita"] = ["it-IT"],
        ["nld"] = ["nl-NL"],
        ["pol"] = ["pl-PL"],
        ["ces"] = ["cs-CZ"],
        ["ron"] = ["ro-RO"],
        ["swe"] = ["sv-SE"],
        ["dan"] = ["da-DK"],
        ["nor"] = ["nb-NO", "no-NO"],
        ["tur"] = ["tr-TR"],
        ["ind"] = ["id-ID"],
        ["msa"] = ["ms-MY"],
        ["rus"] = ["ru-RU"],
        ["ukr"] = ["uk-UA"],
        ["ara"] = ["ar-SA"],
        ["tha"] = ["th-TH"],
        ["vie"] = ["vi-VT", "vi-VN"],
        ["kor"] = ["ko-KR"],
        ["jpn"] = ["ja-JP"],
        ["chi_sim"] = ["zh-Hans"],
        ["chi_tra"] = ["zh-Hant"],
    };

    /// <summary>The languages Vision reads on every macOS Filee runs on (13 and later), when it can't be asked.</summary>
    internal static readonly IReadOnlyList<string> Fallback =
        ["en-US", "fr-FR", "it-IT", "de-DE", "es-ES", "pt-BR", "zh-Hans", "zh-Hant", "ko-KR", "ja-JP", "ru-RU", "uk-UA"];

    /// <summary>The languages to ask Vision for when the text is in <paramref name="code"/>; see the other overload.</summary>
    public static IReadOnlyList<string>? For(string code, IReadOnlyCollection<string> supported) => For([code], supported);

    /// <summary>
    /// The languages to ask Vision for when the text is in <paramref name="codes"/>: those languages in their order, the
    /// main one first, and English last unless it is the main one (scans in other languages often have English words in
    /// them, so it is there even when not chosen), or null when Vision doesn't read one of them. Vision is handed them in
    /// another order (<see cref="InVisionOrder"/>).
    /// </summary>
    /// <param name="supported">What this Mac's Vision reads (<see cref="ITextRecognizer.Languages"/>).</param>
    public static IReadOnlyList<string>? For(IReadOnlyList<string> codes, IReadOnlyCollection<string> supported)
    {
        var languages = new List<string>();
        foreach (var code in codes)
        {
            if (!ByCode.TryGetValue(code, out var candidates))
                return null;
            var match = candidates.Select(c => supported.FirstOrDefault(s => string.Equals(s, c, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(s => s is not null);
            if (match is null)
                return null;
            languages.Add(match);
        }
        if (supported.FirstOrDefault(s => string.Equals(s, "en-US", StringComparison.OrdinalIgnoreCase)) is { } english && languages.FirstOrDefault() != english)
        {
            languages.Remove(english);
            languages.Add(english);
        }
        return languages;
    }

    /// <summary>
    /// The order Vision is asked in: Chinese, Japanese and Korean ahead of the others, each group in its own order. With
    /// English first, Vision drops a Korean line altogether, while the other way around it reads both.
    /// </summary>
    public static IReadOnlyList<string> InVisionOrder(IEnumerable<string> languages) =>
        [.. languages.OrderBy(l => IsCjk(l) ? 0 : 1)]; // a stable sort

    private static bool IsCjk(string language) =>
        language.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)
        || language.StartsWith("ja-", StringComparison.OrdinalIgnoreCase)
        || language.StartsWith("ko-", StringComparison.OrdinalIgnoreCase);
}
