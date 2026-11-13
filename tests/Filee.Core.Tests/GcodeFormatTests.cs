// G-code toolpaths (.gcode, .gco, .ngc), read by Filee: they open the CAD donut, which existing libraries learn
// without taking a donut of the user's own; they belong to the 3D and CAD fields, and Filee never writes them.

using System.Text.Json;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class GcodeFormatTests
{
    [Theory]
    [InlineData("benchy.gcode")]
    [InlineData("PART.GCO")]
    [InlineData("pocket.ngc")]
    public void G_code_uses_the_cad_donut(string file)
    {
        Assert.Same(FormatRegistry.Get("gcode"), FormatRegistry.Detect(file));
        Assert.Equal("cad", ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);
    }

    [Theory]
    [InlineData("file.nc")] // CNC G-code and NetCDF alike (#562): Filee doesn't guess
    [InlineData("game.tap")] // also cassette images of old home computers
    [InlineData("Expr.g")] // also ANTLR grammars and GAP; RepRapFirmware's macros seldom draw anything
    public void Ambiguous_extensions_are_not_taken(string file)
    {
        Assert.Null(FormatRegistry.Detect(file));
    }

    [Fact]
    public void G_code_is_a_specialist_input_of_the_3d_and_cad_fields()
    {
        var gcode = FormatRegistry.Get("gcode")!;
        Assert.False(gcode.Writable);
        Assert.Equal(FormatGrade.Specialist, FormatFields.GradeOf(gcode));
        Assert.Equal(["model3d", "cad"], FormatFields.Of(gcode).Select(f => f.Id));
        Assert.False(FormatFields.IsShown("gcode", new HashSet<string> { "model3d", "cad" }));
        Assert.True(FormatFields.IsShown("gcode", new HashSet<string> { "cad" }));
    }

    [Fact]
    public void Version_88_libraries_learn_g_code_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var cad = profiles.Single(p => p.Id == "cad");
        string[] extensions = ["gcode", "gco", "ngc"];
        cad.Extensions.RemoveAll(extensions.Contains);
        Assert.True(SettingsMigrations.ApplyToLibrary(88, presets, profiles));
        Assert.All(extensions, e => Assert.Contains(e, cad.Extensions));
        Assert.False(SettingsMigrations.ApplyToLibrary(88, presets, profiles));

        cad.Extensions.RemoveAll(extensions.Contains);
        profiles.Insert(0, new ToolbarProfile { Id = "my-printer", Extensions = ["GCODE"] });
        Assert.True(SettingsMigrations.ApplyToLibrary(88, presets, profiles));
        Assert.DoesNotContain("gcode", cad.Extensions); // the user's own donut has it
        Assert.Contains("ngc", cad.Extensions);
        Assert.Equal("my-printer", ProfileSelector.Select(profiles, ["benchy.gcode"])?.Id);
    }

    [Fact]
    public void The_view_is_saved_by_name_and_read_leniently()
    {
        var preset = new Preset { TargetFormat = "png", Gcode = { View = GcodeView.Thumbnail } };

        var json = JsonSerializer.Serialize(preset, FileeJsonContext.Default.Preset);

        Assert.Contains("\"view\": \"Thumbnail\"", json);
        Assert.Equal(GcodeView.Thumbnail, JsonSerializer.Deserialize(json, FileeJsonContext.Default.Preset)!.Gcode.View);
        var copy = preset.Clone();
        copy.Gcode.View = GcodeView.Isometric;
        Assert.Equal(GcodeView.Thumbnail, preset.Gcode.View); // the copy has its own options

        // A preset saved before the option draws from above; so does a view from a newer Filee.
        Assert.Equal(GcodeView.Top, JsonSerializer.Deserialize("""{"targetFormat":"png"}""", FileeJsonContext.Default.Preset)!.Gcode.View);
        Assert.Equal(GcodeView.Top, JsonSerializer.Deserialize("""{"targetFormat":"png","gcode":{"view":"Side"}}""", FileeJsonContext.Default.Preset)!.Gcode.View);
    }
}
