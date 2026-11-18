// Localization contract. The Avalonia implementation lives in Filee.App (LocalizationService).
// Translations are JSON files in src/Filee.App/Assets/i18n. See docs/ADDING-A-LANGUAGE.md.

using Filee.Core.Presets;
using Filee.Core.Profiles;

namespace Filee.Core.Localization;

/// <summary>Looks up translated UI strings by key.</summary>
public interface ILocalizer
{
    /// <summary>Current language code, e.g. <c>"ko"</c>.</summary>
    string Language { get; }

    /// <summary>Returns the translation for <paramref name="key"/>, falling back to English, then to the key itself.</summary>
    string this[string key] { get; }

    /// <summary>Translation with <see cref="string.Format(string, object[])"/> style arguments.</summary>
    string Format(string key, params object[] args);

    /// <summary>Raised after the language changed.</summary>
    event EventHandler? LanguageChanged;
}

/// <summary>Helpers that resolve user-facing names of presets and profiles.</summary>
public static class LocalizerExtensions
{
    /// <summary>Kinds named in a <see cref="Conversion.LeftOutNotice"/> at most; more end the list with "and more".</summary>
    public const int MaxLeftOutParts = 4;

    /// <summary>
    /// A file's notice with why in brackets, the reason translated and the system's own words after it:
    /// "The original was kept… (the file is open in another program)". What the engine left out of a document
    /// (<see cref="Conversion.FileResult.LeftOut"/>) and a picture under the preset's minimum size
    /// (<see cref="Conversion.FileResult.UnderMinimumSize"/>, #662) each add their own sentence, or are the notice on
    /// their own.
    /// </summary>
    public static string Notice(this ILocalizer loc, Conversion.FileResult file)
    {
        string? notice = null;
        if (file.NoticeKey is not null)
        {
            var why = file.NoticeReasonKey is { } key ? loc[key] : null;
            if (!string.IsNullOrWhiteSpace(file.NoticeDetail))
                why = why is null ? file.NoticeDetail : $"{why}: {file.NoticeDetail}";
            notice = why is null ? loc[file.NoticeKey] : $"{loc[file.NoticeKey]} ({why})";
        }
        if (file.LeftOut is { } leftOut)
            notice = And(notice, loc.LeftOut(leftOut));
        if (file.UnderMinimumSize)
            notice = And(notice, loc["notice.under_min_size"]);
        return notice ?? string.Empty;

        string And(string? first, string next) => first is null ? next : loc.Format("notice.with_under_min_size", first, next);
    }

    /// <summary>
    /// What an engine left out of a document, in its sentence: "The Filee renderer couldn't draw all of this document:
    /// 2 charts left out, vertical text set horizontally". Each kind once, at most <see cref="MaxLeftOutParts"/>.
    /// </summary>
    public static string LeftOut(this ILocalizer loc, Conversion.LeftOutNotice notice)
    {
        var separator = loc["notice.renderer_left_out.separator"];
        var parts = notice.Parts.Take(MaxLeftOutParts).Select(part => part.Count switch
        {
            <= 0 => loc[part.Key],
            1 => loc[part.Key + "_one"],
            var count => loc.Format(part.Key, count),
        });
        var list = string.Join(separator, parts);
        if (notice.Parts.Count > MaxLeftOutParts)
            list += separator + loc["notice.renderer_left_out.more"];
        return loc.Format(notice.Key, list);
    }

    public static string DisplayName(this ILocalizer loc, Preset preset) =>
        !string.IsNullOrWhiteSpace(preset.Name) ? preset.Name
        : preset.NameKey is not null ? loc[preset.NameKey]
        : preset.TargetFormat.ToUpperInvariant();

    /// <summary>
    /// A compact name on the donut, where the target format is already shown underneath: the preset's
    /// <see cref="Preset.ShortName"/> when it has one.
    /// </summary>
    public static string DonutName(this ILocalizer loc, Preset preset)
    {
        if (!string.IsNullOrWhiteSpace(preset.ShortName))
            return preset.ShortName.Trim();
        if (!string.IsNullOrWhiteSpace(preset.Name) || preset.NameKey is null)
            return loc.DisplayName(preset);
        var key = preset.NameKey + ".donut";
        var text = loc[key];
        return text == key ? loc.DisplayName(preset) : text;
    }

    /// <summary>
    /// The name on a slice of a group's ring, where the group's name already says what it is about: a built-in
    /// preset's short name (its <see cref="Preset.NameKey"/> + <c>.short</c>, "Left" for "Rotate left") when it has one,
    /// otherwise <see cref="DisplayName(ILocalizer, Preset)"/>. The preset's own <see cref="Preset.ShortName"/>
    /// comes first.
    /// </summary>
    public static string RingName(this ILocalizer loc, Preset preset)
    {
        if (!string.IsNullOrWhiteSpace(preset.ShortName))
            return preset.ShortName.Trim();
        if (!string.IsNullOrWhiteSpace(preset.Name) || preset.NameKey is null)
            return loc.DisplayName(preset);
        var key = preset.NameKey + ".short";
        var text = loc[key];
        return text == key ? loc.DisplayName(preset) : text;
    }

    public static string DisplayName(this ILocalizer loc, ToolbarProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.Name) ? profile.Name
        : profile.NameKey is not null ? loc[profile.NameKey]
        : profile.Id;
}
