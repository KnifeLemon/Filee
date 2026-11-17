// "Export for the gallery" (#1530): a preset.json with only the presets one chooses, and one donut if one likes, for a
// submission to the preset gallery. A group brings its members and a donut its slices, so the file is whole. The same
// check the gallery's CI runs is run on it here, so the problems and warnings show before anything is saved.

using System.Text.Json;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Gallery;

/// <summary>The file "Export for the gallery" writes, and what the gallery's check says about it.</summary>
public sealed record GalleryExportFile(byte[] Json, IReadOnlyList<Preset> Presets, PresetCheckResult Check);

public static class GalleryExport
{
    /// <summary>
    /// The bundle of <paramref name="chosen"/> and <paramref name="profile"/> (with the presets their groups and slices
    /// need, in <paramref name="library"/>'s order), as an export writes it, and its check. <paramref name="language"/> is
    /// the app language the names are in, the preset's main language in the gallery.
    /// </summary>
    public static GalleryExportFile Build(IReadOnlyList<Preset> library, IEnumerable<Preset> chosen, ToolbarProfile? profile,
        string? language = null)
    {
        var ids = chosen.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        if (profile is not null)
            ids.UnionWith(profile.PresetIds.Concat(profile.BackPresetIds ?? []));
        foreach (var group in library.Where(p => p.IsGroup && ids.Contains(p.Id)).ToList())
            ids.UnionWith(group.SubPresetIds);
        var presets = library.Where(p => ids.Contains(p.Id)).ToList();
        var json = JsonSerializer.SerializeToUtf8Bytes(new PresetBundle
        {
            SettingsSchema = AppSettings.CurrentSchemaVersion,
            Language = language is not null && SharedPresetFolderCheck.Languages.Contains(language) ? language : null,
            Presets = [.. presets.Select(p => p.ForSharing())],
            Profiles = profile is null ? [] : [profile.Clone()],
        }, FileeJsonContext.Default.PresetBundle);
        return new GalleryExportFile(json, presets, SharedBundleCheck.Check(json));
    }
}
