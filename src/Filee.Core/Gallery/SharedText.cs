// Checks of the texts in a shared preset (#1530): every text is free of links, personal paths and hidden characters,
// and the texts that become file or folder names or are read as codes have the one form the app reads.

using System.Text.RegularExpressions;
using Filee.Core.Presets;

namespace Filee.Core.Gallery;

/// <summary>Text checks for shared presets. Each returns why a value is refused, or null when it is fine.</summary>
internal static partial class SharedText
{
    /// <summary>The only addresses a shared text may contain: Filee's own.</summary>
    public static readonly IReadOnlyList<string> AllowedUrls = ["https://filee.sh/", "https://github.com/KnifeLemon/"];

    /// <summary>The checks every shared text gets, whatever the field: the problem's code and why, or null.</summary>
    public static (string Code, string Message)? Common(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsControl(c))
                return ("text.character", "has a control character");
            // Bidirectional overrides and isolates, and zero-width characters, can make a text read as something else.
            if (c is '\u200B' or '\u200C' or '\u200D' or '\u200E' or '\u200F' or '\u2060' or '\uFEFF'
                || c is >= '\u202A' and <= '\u202E' || c is >= '\u2066' and <= '\u2069')
                return ("text.character", "has an invisible or direction-changing character");
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            else if (char.IsSurrogate(c))
                return ("text.character", "is not valid text");
        }
        foreach (Match match in Link().Matches(text))
        {
            var rest = text[match.Index..];
            if (!AllowedUrls.Any(allowed => rest.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)))
                return ("text.url", "has a link (only links to filee.sh are allowed)");
        }
        if (PersonalPath().IsMatch(text))
            return ("text.personal_path", "has a path of a computer's folders (C:\\Users\\…, /home/…)");
        return null;
    }

    /// <summary>A link: a scheme, "www." or a mail address.</summary>
    [GeneratedRegex(@"(?i)(\b[a-z][a-z0-9+.\-]*://|\bwww\.|\bmailto:|\bfile:|\b[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}\b)", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    /// <summary>
    /// A path that tells whose computer it is or where their files are: a drive (C:\, D:/), a network share (\\server),
    /// a home folder (/home/, /Users/, ~/), an environment variable (%USERPROFILE%, $HOME).
    /// </summary>
    [GeneratedRegex(@"(?i)(\b[a-z]:[\\/]|\\\\[^\\\s]|(^|[\s""'(=])/(home|users|root|mnt|media|volumes|private|var|tmp|etc)\b|(^|[\s""'(=])~[\\/]|%[a-z_]+%|\$home\b|\$\{?env|\\users\\)", RegexOptions.CultureInvariant)]
    private static partial Regex PersonalPath();

    /// <summary>An id: letters, digits, "-", "_" and "." (preset ids, format ids, tool names, icon keys).</summary>
    public static string? Id(string text) =>
        IdPattern().IsMatch(text) ? null : "may only have letters, digits, '-', '_' and '.'";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]*$|^\*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>A built-in name's translation key (<c>preset.merge_pdf</c>, <c>profile.images</c>).</summary>
    public static string? NameKey(string text) =>
        NameKeyPattern().IsMatch(text) ? null : "is not a name key of Filee's";

    [GeneratedRegex(@"^(preset|profile|group|tool)\.[a-z0-9_.]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NameKeyPattern();

    /// <summary>A file extension without its dot.</summary>
    public static string? Extension(string text) =>
        ExtensionPattern().IsMatch(text) ? null : "is not a file extension";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_\-~]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionPattern();

    /// <summary>"#RRGGBB" or "#RGB".</summary>
    public static string? HexColour(string text) =>
        HexPattern().IsMatch(text) ? null : "is not a colour like #RRGGBB";

    [GeneratedRegex(@"^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex HexPattern();

    /// <summary>A colour or "transparent" (<see cref="ImageOptions.JoinBackground"/>).</summary>
    public static string? JoinBackground(string text) =>
        text == ImageOptions.TransparentJoin ? null : HexColour(text);

    /// <summary>Pages as the editor takes them ("1-3,5"), or empty.</summary>
    public static string? PageRange(string text) =>
        Conversion.PageRange.Check(text, null).IsValid ? null : "is not a page range like 1-3,5";

    /// <summary>Tesseract languages, "+" between them ("eng", "chi_sim+eng").</summary>
    public static string? OcrLanguages(string text) =>
        OcrPattern().IsMatch(text) ? null : "is not a list of OCR languages like eng or chi_sim+eng";

    [GeneratedRegex(@"^[a-z]{3}(_[a-z]{3,4})?(\+[a-z]{3}(_[a-z]{3,4})?){0,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex OcrPattern();

    /// <summary>"auto" or a language Whisper knows (<see cref="WhisperLanguages"/>).</summary>
    public static string? SpokenLanguage(string text) =>
        text == WhisperLanguages.Auto || WhisperLanguages.Find(text) is not null ? null : "is not a spoken language Filee knows";

    /// <summary>"auto" or a language code ("en", "pt-BR", "zh-Hant").</summary>
    public static string? LanguageCode(string text) =>
        LanguagePattern().IsMatch(text) ? null : "is not a language code like en or pt-BR";

    [GeneratedRegex(@"^(auto|[a-z]{2,3}([_\-][A-Za-z]{2,4})?)$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguagePattern();

    /// <summary>An upscaling model's id (the app downloads only the curated ones, by id).</summary>
    public static string? ModelId(string text) =>
        ModelPattern().IsMatch(text) ? null : "is not a model id";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelPattern();

    /// <summary>An Ollama model name ("llama3.1:8b", "library/qwen2.5"): never an address.</summary>
    public static string? OllamaModel(string text) =>
        OllamaPattern().IsMatch(text) && !text.Contains("//", StringComparison.Ordinal) ? null : "is not a model name like llama3.1:8b";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/\-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex OllamaPattern();

    /// <summary>One metadata field, "Group:Tag" (<see cref="MetadataEdits.IsFieldKey"/>).</summary>
    public static string? MetadataField(string text) =>
        MetadataEdits.IsFieldKey(text) ? null : "is not a metadata field like XMP-dc:Title";

    /// <summary>
    /// True for a metadata field that clearly names a person, a place or a copyright owner (author, artist, creator,
    /// copyright and rights holder, by-line, owner, credit, GPS and location fields, contact details): a shared preset may
    /// only remove it, never give it a value. A document's title or name is not one of them.
    /// </summary>
    public static bool IsPersonalField(string field) => PersonalField().IsMatch(field);

    [GeneratedRegex(@"(?i)(author|artist|creator|copyright|rights|by-?line|owner|credit|contact|e-?mail|phone|address|gps|latitude|longitude|altitude|location|city|country|province)", RegexOptions.CultureInvariant)]
    private static partial Regex PersonalField();

    /// <summary>"auto", a ratio ("16:9") or empty (<see cref="VideoCrop"/>).</summary>
    public static string? CropFrame(string text) =>
        text.Length == 0 || VideoCrop.Crops(text) ? null : "is not a frame like 16:9 or auto";

    private static readonly char[] Unsafe = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// A file name pattern: text and the placeholders of <see cref="SharedPresetRules.Placeholders"/>, never a
    /// separator or "..", so the name can't leave the folder it is saved in.
    /// </summary>
    public static string? FileNamePattern(string text)
    {
        foreach (Match match in Braces().Matches(text))
        {
            if (!SharedPresetRules.Placeholders.Contains(match.Groups[1].Value.ToLowerInvariant()))
                return $"has an unknown placeholder {match.Value} (use {string.Join(", ", SharedPresetRules.Placeholders.Select(p => "{" + p + "}"))})";
        }
        var literal = Braces().Replace(text, "");
        if (literal.Contains('{') || literal.Contains('}'))
            return "has an unclosed placeholder";
        if (text.Contains(".."))
            return "may not have '..'";
        return text.IndexOfAny(Unsafe) >= 0 ? "may not have \\ / : * ? \" < > |" : null;
    }

    [GeneratedRegex(@"\{([^{}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Braces();

    /// <summary>
    /// What a rename step puts in: no separator, so the name stays one name ("$1" of a regular expression inserts a part
    /// of the name, which has none).
    /// </summary>
    public static string? RenameReplace(string text) =>
        text.IndexOfAny(Unsafe) >= 0 ? "may not have \\ / : * ? \" < > |" : null;
}
