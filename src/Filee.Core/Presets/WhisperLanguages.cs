// The spoken languages recognized by OpenAI Whisper (whisper.cpp).
// Code is the ISO-639 / whisper language identifier; Name is the English language name.

using System.Globalization;

namespace Filee.Core.Presets;

/// <summary>A spoken language recognized by Whisper AI.</summary>
/// <param name="Code">Language code passed to whisper.cpp (<c>-l &lt;code&gt;</c>).</param>
/// <param name="Name">The English language name from whisper.cpp.</param>
public sealed record WhisperLanguage(string Code, string Name)
{
    /// <summary>Title-cased display name (e.g. "English", "Hindi", "French").</summary>
    public string DisplayName => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Name);
}

/// <summary>The spoken languages supported by Whisper AI.</summary>
public static class WhisperLanguages
{
    /// <summary>Auto-detect the spoken language from audio (whisper.cpp default).</summary>
    public const string Auto = "auto";

    /// <summary>All languages known to whisper.cpp.</summary>
    public static readonly IReadOnlyList<WhisperLanguage> All =
    [
        new("en", "english"),
        new("zh", "chinese"),
        new("de", "german"),
        new("es", "spanish"),
        new("ru", "russian"),
        new("ko", "korean"),
        new("fr", "french"),
        new("ja", "japanese"),
        new("pt", "portuguese"),
        new("tr", "turkish"),
        new("pl", "polish"),
        new("ca", "catalan"),
        new("nl", "dutch"),
        new("ar", "arabic"),
        new("sv", "swedish"),
        new("it", "italian"),
        new("id", "indonesian"),
        new("hi", "hindi"),
        new("fi", "finnish"),
        new("vi", "vietnamese"),
        new("he", "hebrew"),
        new("uk", "ukrainian"),
        new("el", "greek"),
        new("ms", "malay"),
        new("cs", "czech"),
        new("ro", "romanian"),
        new("da", "danish"),
        new("hu", "hungarian"),
        new("ta", "tamil"),
        new("no", "norwegian"),
        new("th", "thai"),
        new("ur", "urdu"),
        new("hr", "croatian"),
        new("bg", "bulgarian"),
        new("lt", "lithuanian"),
        new("la", "latin"),
        new("mi", "maori"),
        new("ml", "malayalam"),
        new("cy", "welsh"),
        new("sk", "slovak"),
        new("te", "telugu"),
        new("fa", "persian"),
        new("lv", "latvian"),
        new("bn", "bengali"),
        new("sr", "serbian"),
        new("az", "azerbaijani"),
        new("sl", "slovenian"),
        new("kn", "kannada"),
        new("et", "estonian"),
        new("mk", "macedonian"),
        new("br", "breton"),
        new("eu", "basque"),
        new("is", "icelandic"),
        new("hy", "armenian"),
        new("ne", "nepali"),
        new("mn", "mongolian"),
        new("bs", "bosnian"),
        new("kk", "kazakh"),
        new("sq", "albanian"),
        new("sw", "swahili"),
        new("gl", "galician"),
        new("mr", "marathi"),
        new("pa", "punjabi"),
        new("si", "sinhala"),
        new("km", "khmer"),
        new("sn", "shona"),
        new("yo", "yoruba"),
        new("so", "somali"),
        new("af", "afrikaans"),
        new("oc", "occitan"),
        new("ka", "georgian"),
        new("be", "belarusian"),
        new("tg", "tajik"),
        new("sd", "sindhi"),
        new("gu", "gujarati"),
        new("am", "amharic"),
        new("yi", "yiddish"),
        new("lo", "lao"),
        new("uz", "uzbek"),
        new("fo", "faroese"),
        new("ht", "haitian creole"),
        new("ps", "pashto"),
        new("tk", "turkmen"),
        new("nn", "nynorsk"),
        new("mt", "maltese"),
        new("sa", "sanskrit"),
        new("lb", "luxembourgish"),
        new("my", "myanmar"),
        new("bo", "tibetan"),
        new("tl", "tagalog"),
        new("mg", "malagasy"),
        new("as", "assamese"),
        new("tt", "tatar"),
        new("haw", "hawaiian"),
        new("ln", "lingala"),
        new("ha", "hausa"),
        new("ba", "bashkir"),
        new("jw", "javanese"),
        new("su", "sundanese"),
        new("yue", "cantonese"),
    ];

    /// <summary>Finds a language by its code (case-insensitive).</summary>
    public static WhisperLanguage? Find(string code) =>
        All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
}
