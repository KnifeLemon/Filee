// iPhone ringtones (#1291): M4R is an audio format, the "iPhone ringtone" preset makes one of at most 40 seconds from
// where its Start field says, and settings schema 77 brings the preset and the extension to libraries saved before.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class RingtonePresetTests
{
    [Fact]
    public void M4R_is_a_writable_audio_format_with_its_own_extension()
    {
        var format = FormatRegistry.Get("m4r");

        Assert.Equal(FormatCategory.Audio, format.Category);
        Assert.True(format.Writable);
        Assert.Equal("m4r", format.PrimaryExtension);
        Assert.Equal(FormatGrade.General, FormatFields.GradeOf(format));
        Assert.Equal("m4r", FormatRegistry.FindByExtension("m4r")?.Id);
    }

    [Fact]
    public void The_built_in_preset_makes_an_M4R_of_thirty_seconds_from_the_start()
    {
        var preset = BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.RingtoneId);

        Assert.Equal("preset.iphone_ringtone", preset.NameKey);
        Assert.Equal("m4r", preset.TargetFormat);
        Assert.Equal(0, preset.Media.RingtoneStart);
        Assert.Equal(30, preset.Media.RingtoneLength);
    }

    [Theory]
    [InlineData(0, 30, null, 0, 30)]
    [InlineData(12, 100, null, 12, 40)] // never more than 40 seconds
    [InlineData(12, 0.2, null, 12, 1)] // nor less than one
    [InlineData(-5, 30, null, 0, 30)]
    [InlineData(double.NaN, double.NaN, null, 0, 30)] // a hand-edited preset file may hold anything
    [InlineData(5, 30, 20.0, 5, 15)] // the sound ends sooner
    [InlineData(50, 30, 20.0, 19, 1)] // the start is after the end: the last second is used
    [InlineData(0, 40, 0.5, 0, 0.5)] // a sound shorter than a second is taken as it is
    public void The_window_is_kept_within_the_limits_and_the_sound(double start, double length, double? source, double expectedStart, double expectedLength)
    {
        var media = new MediaOptions { RingtoneStart = start, RingtoneLength = length };

        Assert.Equal((expectedStart, expectedLength), media.RingtoneWindow(source));
    }

    [Fact]
    public void Version_76_libraries_get_the_preset_in_place_and_learn_the_extension_without_a_slot()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var before = presets.FindIndex(p => p.Id == "to-aac");
        presets.RemoveAll(p => p.Id == BuiltInData.RingtoneId);
        var audio = profiles.Single(p => p.Id == "audio").Extensions;
        audio.Remove("m4r");

        Assert.True(SettingsMigrations.ApplyToLibrary(76, presets, profiles));

        Assert.Equal(before + 1, presets.FindIndex(p => p.Id == BuiltInData.RingtoneId)); // right after the AAC preset, as in a new library
        Assert.Contains("m4r", audio);
        Assert.DoesNotContain(profiles, p => p.PresetIds.Contains(BuiltInData.RingtoneId) || p.BackPresetIds?.Contains(BuiltInData.RingtoneId) == true);

        // Run again (a library already at 77), nothing changes; a preset the user deleted doesn't come back.
        Assert.False(SettingsMigrations.ApplyToLibrary(AppSettings.CurrentSchemaVersion, presets, profiles));
        presets.RemoveAll(p => p.Id == BuiltInData.RingtoneId);
        Assert.False(SettingsMigrations.ApplyToLibrary(AppSettings.CurrentSchemaVersion, presets, profiles));
        Assert.DoesNotContain(presets, p => p.Id == BuiltInData.RingtoneId);
    }

    [Fact]
    public void A_library_that_already_has_the_preset_keeps_its_own()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        presets.Single(p => p.Id == BuiltInData.RingtoneId).Media.RingtoneLength = 25;

        SettingsMigrations.ApplyToLibrary(76, presets, profiles);

        Assert.Single(presets, p => p.Id == BuiltInData.RingtoneId);
        Assert.Equal(25, presets.Single(p => p.Id == BuiltInData.RingtoneId).Media.RingtoneLength);
    }

    [Fact]
    public void An_M4R_file_uses_the_audio_donut_and_a_custom_claim_is_kept()
    {
        Assert.Equal("audio", ProfileSelector.Select(BuiltInData.CreateProfiles(), ["/tmp/Marimba.m4r"])?.Id);

        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        profiles.Single(p => p.Id == "audio").Extensions.Remove("m4r");
        profiles.Insert(0, new ToolbarProfile { Id = "my-tones", Name = "Tones", Extensions = ["M4R"] });

        SettingsMigrations.ApplyToLibrary(76, presets, profiles);

        Assert.DoesNotContain("m4r", profiles.Single(p => p.Id == "audio").Extensions); // the user's own donut has it
    }
}
