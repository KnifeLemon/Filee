// Gerber PCB layers are a read-only CAD format, learnt by existing CAD donuts.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class GerberFormatTests
{
    private static readonly string[] Extensions = ["gbr", "ger", "gtl", "gbl", "gto", "gbo", "gts", "gbs", "gtp", "gbp", "gko", "gm1"];

    [Theory]
    [InlineData("board-F_Cu.gbr")]
    [InlineData("R-2R-Dessus.gtl")]
    [InlineData("R-2R-Sérigr_Dessous.GBO")]
    [InlineData("board.gm1")]
    public void Gerber_is_a_read_only_cad_format(string name)
    {
        var format = FormatRegistry.Detect(name)!;
        Assert.Equal(("gerber", FormatCategory.Cad, false), (format.Id, format.Category, format.Writable));
    }

    [Fact]
    public void It_belongs_to_the_cad_field_only() =>
        Assert.Equal(["cad"], FormatFields.Of(FormatRegistry.Get("gerber")).Select(f => f.Id));

    [Fact]
    public void Version_86_cad_donuts_learn_it_without_taking_a_custom_claim()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        foreach (var profile in profiles)
            profile.Extensions.RemoveAll(Extensions.Contains);
        Assert.True(SettingsMigrations.ApplyToLibrary(86, presets, profiles));
        Assert.Superset(Extensions.ToHashSet(), profiles.Single(p => p.Id == "cad").Extensions.ToHashSet());

        profiles = BuiltInData.CreateProfiles();
        foreach (var profile in profiles)
            profile.Extensions.RemoveAll(Extensions.Contains);
        profiles.Insert(0, new ToolbarProfile { Id = "my-brushes", Name = "GIMP brushes", Extensions = ["gbr"] });
        SettingsMigrations.ApplyToLibrary(86, presets, profiles);
        var cad = profiles.Single(p => p.Id == "cad").Extensions;
        Assert.DoesNotContain("gbr", cad);
        Assert.Contains("gtl", cad);
    }
}
