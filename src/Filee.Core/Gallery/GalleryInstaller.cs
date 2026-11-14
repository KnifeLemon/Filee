// Installs a preset from the preset gallery (#1530). Unlike Settings → General → Import, which replaces presets with
// the same id, an install only adds: every preset and the donut get new ids, and a name the library already has gets
// " (2)". The file is checked again here, with the same rules the gallery's CI used, and must be exactly the file
// index.json lists (its SHA-256): the app trusts neither the server nor the network in between.

using System.Security.Cryptography;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Gallery;

/// <summary>What an install added, or why nothing was.</summary>
public sealed record GalleryInstallResult(
    IReadOnlyList<Preset> Presets,
    ToolbarProfile? Profile,
    IReadOnlyList<PresetCheckError> Errors)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>Adds a shared preset.json to the library as new presets (and a new donut).</summary>
public static class GalleryInstaller
{
    /// <summary>
    /// Checks <paramref name="json"/> against <paramref name="sha256"/> and the gallery's rules, then adds what it holds
    /// to <paramref name="store"/> and saves the library. <paramref name="presetName"/> and <paramref name="profileName"/>
    /// give the names the user sees (a built-in name in their language), to tell a clash.
    /// </summary>
    /// <param name="gallery">The gallery entry's id, kept on each preset (<see cref="Preset.Gallery"/>).</param>
    public static GalleryInstallResult Install(UserDataStore store, ReadOnlySpan<byte> json, string sha256,
        Func<Preset, string> presetName, Func<ToolbarProfile, string> profileName, string? gallery = null)
    {
        var actual = Convert.ToHexStringLower(SHA256.HashData(json));
        if (!string.Equals(actual, sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            return Failed("install.sha256", "is not the file the gallery lists (its SHA-256 differs)");

        var check = SharedBundleCheck.Check(json);
        if (!check.Ok || check.Bundle is not { } bundle)
            return new GalleryInstallResult([], null, check.Errors);

        // As Import: no path to a picture of someone else's, and the back faces a bundle from before them lacks.
        foreach (var preset in bundle.Presets)
            preset.Image.ForgetBackdropPictures();
        SettingsMigrations.AddBackFaces(bundle.Presets, bundle.Profiles);

        var taken = store.Presets.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var preset in bundle.Presets)
        {
            string id;
            do
                id = Preset.NewId();
            while (!taken.Add(id));
            ids[preset.Id] = id;
            preset.Id = id;
        }
        string Map(string id) => ids.GetValueOrDefault(id, id);

        var names = store.Presets.Select(presetName).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        foreach (var preset in bundle.Presets)
        {
            // Kept as shared, settings and all: the window showed its warnings before the user chose to install it.
            preset.Gallery = gallery;
            preset.SubPresetIds = [.. preset.SubPresetIds.Select(Map)];
            var name = Unique(presetName(preset), names);
            if (name != presetName(preset))
            {
                preset.Name = name;
                preset.NameKey = null;
            }
            names.Add(name);
        }

        var profile = bundle.Profiles.FirstOrDefault();
        if (profile is not null)
        {
            var profileIds = store.Profiles.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            do
                profile.Id = Guid.NewGuid().ToString("N")[..12];
            while (profileIds.Contains(profile.Id));
            profile.PresetIds = [.. profile.PresetIds.Select(Map)];
            profile.BackPresetIds = [.. (profile.BackPresetIds ?? []).Select(Map)];
            var name = Unique(profileName(profile), store.Profiles.Select(profileName).ToHashSet(StringComparer.CurrentCultureIgnoreCase));
            if (name != profileName(profile))
            {
                profile.Name = name;
                profile.NameKey = null;
            }
        }

        store.Presets.AddRange(bundle.Presets);
        // After every donut of the user's: a file type one of theirs takes keeps opening theirs (the first that has
        // it), so the new donut changes nothing until the user gives it a file type (Settings → Toolbar).
        if (profile is not null)
            store.Profiles.Add(profile);
        store.SaveLibrary();
        return new GalleryInstallResult(bundle.Presets, profile, []);
    }

    /// <summary><paramref name="name"/>, or with " (2)", " (3)" … when the library has it already.</summary>
    internal static string Unique(string name, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(name))
            return name;
        for (var n = 2; ; n++)
        {
            var candidate = $"{name} ({n})";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    private static GalleryInstallResult Failed(string code, string message) =>
        new([], null, [new PresetCheckError(code, SharedBundleCheck.FileName, "", message)]);
}
