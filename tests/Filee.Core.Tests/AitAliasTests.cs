// Adobe Illustrator templates (.ait, #1726) open as AI on the vector donut and stay read-only.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;
using Filee.Core.Web;

namespace Filee.Core.Tests;

public class AitAliasTests
{
    [Theory]
    [InlineData("template.ait")]
    [InlineData("template.AIT")]
    public void Ait_is_an_ai_and_uses_the_vector_donut(string file)
    {
        Assert.Same(FormatRegistry.Get("ai"), FormatRegistry.Detect(file));
        Assert.Equal("vector", ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);
    }

    [Fact]
    public void Ai_stays_read_only_with_ai_as_primary_extension()
    {
        var ai = FormatRegistry.Get("ai");
        Assert.False(ai.Writable);
        Assert.Equal("ai", ai.PrimaryExtension);
    }

    [Fact]
    public void Web_sniffing_keeps_ait_name_for_pdf_compatible_templates() =>
        Assert.Equal("template.ait", WebFormats.NameFor("template.ait", "%PDF-1.6"u8, null));

    [Fact]
    public void Version_102_libraries_learn_ait_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var vector = profiles.Single(p => p.Id == "vector");
        vector.Extensions.Remove("ait");
        Assert.True(SettingsMigrations.ApplyToLibrary(102, presets, profiles));
        Assert.Contains("ait", vector.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(102, presets, profiles));

        vector.Extensions.Remove("ait");
        profiles.Insert(0, new ToolbarProfile { Id = "my-templates", Extensions = ["AIT"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(102, presets, profiles));
        Assert.DoesNotContain("ait", vector.Extensions);
        Assert.Equal("my-templates", ProfileSelector.Select(profiles, ["template.ait"])?.Id);
    }
}
