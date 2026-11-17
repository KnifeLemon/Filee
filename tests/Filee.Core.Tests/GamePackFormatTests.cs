// Minecraft packs and Modrinth modpacks (#824, #898): ZIP archives under their own extensions, in the Games field, which a
// new install has off; existing libraries get their presets in the palette and the archives donut learns them.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class GamePackFormatTests
{
    private static readonly string[] Packs = ["mcpack", "mcaddon", "mcworld", "mctemplate", "mrpack"];

    [Fact]
    public void Packs_are_games_archives_hidden_until_games_are_on()
    {
        var newInstall = FormatFields.All.Where(f => f.CanTurnOff && !f.OnByDefault).Select(f => f.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in Packs)
        {
            var format = FormatRegistry.Get(pack);
            Assert.Equal(FormatCategory.Archive, format.Category);
            Assert.True(format.Writable);
            Assert.Equal(["game"], FormatFields.Of(format).Select(f => f.Id));
            Assert.False(FormatFields.IsShown(pack, newInstall));
            Assert.True(FormatFields.IsShown(pack, new HashSet<string>()));
        }
    }

    [Fact]
    public void Version_61_libraries_get_the_pack_presets_after_tar_on_no_donut_and_learn_the_extensions()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => Packs.Contains(p.TargetFormat));
        var profiles = BuiltInData.CreateProfiles();
        foreach (var profile in profiles)
            profile.Extensions.RemoveAll(Packs.Contains);
        profiles.Insert(0, new ToolbarProfile { Id = "my-mods", Name = "Mods", Extensions = ["mrpack"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(61, presets, profiles));

        var tar = presets.FindIndex(p => p.Id == "to-tar");
        Assert.Equal(Packs.Select(p => "to-" + p), presets.Skip(tar + 1).Take(Packs.Length).Select(p => p.Id));
        Assert.DoesNotContain(profiles, p => p.PresetIds.Concat(p.BackPresetIds ?? []).Any(id => id.StartsWith("to-mc", StringComparison.Ordinal)));
        var archives = profiles.Single(p => p.Id == "archives").Extensions;
        Assert.All(Packs.Where(p => p != "mrpack"), p => Assert.Contains(p, archives));
        Assert.DoesNotContain("mrpack", archives); // the user's own donut has it
        Assert.False(SettingsMigrations.ApplyToLibrary(61, presets, profiles));
    }
}
