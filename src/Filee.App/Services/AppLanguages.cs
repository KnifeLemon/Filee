// The languages Filee ships (Assets/i18n/<code>.json). The installer (src/Filee.Setup) links this file, so a language
// added here is offered by Setup too.

namespace Filee.App.Services;

internal static class AppLanguages
{
    /// <summary>Code → native name (shown in the language pickers of the app and of Setup).</summary>
    public static readonly IReadOnlyList<(string Code, string NativeName)> All =
    [
        ("en", "English"),
        ("de", "Deutsch"),
        ("tr", "Türkçe"),
        ("es", "Español"),
        ("ja", "日本語"),
        ("ko", "한국어"),
        ("zh-CN", "简体中文"),
        ("zh-TW", "繁體中文"),
        ("pl", "Polski"),
        ("hi", "हिन्दी"),
        ("ru", "Русский"),
        ("ar", "العربية"),
        ("pt-BR", "Português (Brasil)"),
        ("fr", "Français"),
        ("ne", "नेपाली"),
        ("th", "ไทย"),
    ];

    /// <summary>
    /// True for a Chinese culture name written in Traditional characters: "zh-TW", "zh-HK", "zh-Hant-TW", "zh-CHT" …
    /// An explicit script wins over the region (macOS has "zh-Hans-TW", Simplified in Taiwan); a bare "zh" is Simplified.
    /// </summary>
    public static bool IsTraditionalChinese(string culture)
    {
        var parts = culture.Trim().Replace('_', '-').Split('-');
        if (!parts[0].Equals("zh", StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = parts.Skip(1).ToArray();
        if (rest.Contains("Hans", StringComparer.OrdinalIgnoreCase))
            return false;
        return rest.Intersect(["Hant", "TW", "HK", "MO", "CHT"], StringComparer.OrdinalIgnoreCase).Any();
    }
}
