// What a slice shows and how large its label is: a preset's icon, short name and Show, a donut's default Show and
// Label scale, how they are saved (and read by an older or newer Filee), and the short name on the donut.

using System.Text.Json;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class SliceLabelSettingsTests
{
    private sealed class KeyLocalizer(Dictionary<string, string> strings) : ILocalizer
    {
        public string Language => "en";
        public string this[string key] => strings.TryGetValue(key, out var text) ? text : key;
        public string Format(string key, params object[] args) => key;
        public event EventHandler? LanguageChanged { add { } remove { } }
    }

    private static Preset RoundTrip(Preset preset) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(new List<Preset> { preset }, FileeJsonContext.Default.ListPreset),
            FileeJsonContext.Default.ListPreset)!.Single();

    private static ToolbarProfile RoundTrip(ToolbarProfile profile) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(new List<ToolbarProfile> { profile }, FileeJsonContext.Default.ListToolbarProfile),
            FileeJsonContext.Default.ListToolbarProfile)!.Single();

    [Fact]
    public void A_presets_icon_short_name_and_show_are_saved_and_copied()
    {
        var preset = new Preset { Id = "p", Icon = "crop", ShortName = "Trim", Show = LabelShow.IconOnly };
        var read = RoundTrip(preset);
        Assert.Equal(("crop", "Trim", LabelShow.IconOnly), (read.Icon, read.ShortName, read.Show));
        var copy = preset.Clone();
        Assert.Equal(("crop", "Trim", LabelShow.IconOnly), (copy.Icon, copy.ShortName, copy.Show));
    }

    [Fact]
    public void Unset_values_are_left_out_of_the_file_and_read_back_unset()
    {
        var json = JsonSerializer.Serialize(new List<Preset> { new() { Id = "p" } }, FileeJsonContext.Default.ListPreset);
        Assert.DoesNotContain("\"icon\"", json);
        Assert.DoesNotContain("\"shortName\"", json);
        Assert.DoesNotContain("\"show\"", json);
        var read = JsonSerializer.Deserialize(json, FileeJsonContext.Default.ListPreset)!.Single();
        Assert.Null(read.Icon);
        Assert.Null(read.ShortName);
        Assert.Null(read.Show);
    }

    [Theory]
    [InlineData("\"Sideways\"")] // a newer Filee's choice
    [InlineData("3")]
    [InlineData("{\"a\":1}")]
    [InlineData("null")]
    public void A_show_this_version_doesnt_know_reads_as_unset_and_the_rest_still_loads(string value)
    {
        var json = $$"""[{"id":"p","name":"Mine","targetFormat":"png","show":{{value}},"shortName":"M"}]""";
        var read = JsonSerializer.Deserialize(json, FileeJsonContext.Default.ListPreset)!.Single();
        Assert.Null(read.Show);
        Assert.Equal(("Mine", "M"), (read.Name, read.ShortName));
    }

    [Fact]
    public void A_show_is_read_by_name_whatever_its_case()
    {
        var json = """[{"id":"p","targetFormat":"png","show":"icononly"}]""";
        Assert.Equal(LabelShow.IconOnly, JsonSerializer.Deserialize(json, FileeJsonContext.Default.ListPreset)!.Single().Show);
    }

    [Fact]
    public void A_donuts_label_settings_are_saved_and_copied_and_default_to_auto_at_100_percent()
    {
        var fresh = RoundTrip(new ToolbarProfile { Id = "d" });
        Assert.Null(fresh.LabelShow);
        Assert.Equal(1, fresh.LabelScale);

        var profile = new ToolbarProfile { Id = "d", LabelShow = LabelShow.TextOnly, LabelScale = 1.25 };
        var read = RoundTrip(profile);
        Assert.Equal((LabelShow.TextOnly, 1.25), (read.LabelShow, read.LabelScale));
        var copy = profile.Clone();
        Assert.Equal((LabelShow.TextOnly, 1.25), (copy.LabelShow, copy.LabelScale));
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.25, 1.25)]
    [InlineData(1.27, 1.25)]   // onto a step
    [InlineData(0.5, 0.8)]     // within the range
    [InlineData(3.0, 1.6)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void A_label_scale_is_kept_within_the_slider_on_its_steps(double saved, double expected) =>
        Assert.Equal(expected, ToolbarProfile.ClampLabelScale(saved));

    [Fact]
    public void A_hand_edited_label_scale_is_put_back_in_range_when_the_library_loads()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Profiles[0].LabelScale = 9;
        store.SaveLibrary();
        store.SaveSettings();
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.Equal(1.6, reloaded.Profiles[0].LabelScale);
    }

    [Fact]
    public void The_short_name_is_shown_on_the_donut_and_its_ring_but_not_elsewhere()
    {
        var loc = new KeyLocalizer(new() { ["preset.trim"] = "Trim transparent borders", ["preset.trim.donut"] = "Trim borders" });
        var builtIn = new Preset { NameKey = "preset.trim" };
        Assert.Equal("Trim borders", loc.DonutName(builtIn));

        builtIn.ShortName = "  Trim  ";
        Assert.Equal("Trim", loc.DonutName(builtIn));
        Assert.Equal("Trim", loc.RingName(builtIn));
        Assert.Equal("Trim transparent borders", loc.DisplayName(builtIn));

        var mine = new Preset { Name = "My long preset name", ShortName = "Mine" };
        Assert.Equal("Mine", loc.DonutName(mine));
        Assert.Equal("My long preset name", loc.DisplayName(mine));

        mine.ShortName = " ";
        Assert.Equal("My long preset name", loc.DonutName(mine));
    }
}
