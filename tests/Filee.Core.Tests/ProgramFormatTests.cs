// Windows programs and libraries (.exe, .dll; #1188) are read as their icon: they open the images donut, which existing
// libraries learn without taking a donut of the user's own.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class ProgramFormatTests
{
    [Theory]
    [InlineData("setup.exe")]
    [InlineData("SHELL32.DLL")]
    public void A_program_uses_the_image_donut(string file)
    {
        Assert.Same(FormatRegistry.Get(FormatRegistry.Program), FormatRegistry.Detect(file));
        Assert.Equal("images", ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);
    }

    [Fact]
    public void Version_76_libraries_learn_exe_and_dll_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var images = profiles.Single(p => p.Id == "images");
        images.Extensions.RemoveAll(e => e is "exe" or "dll");
        Assert.True(SettingsMigrations.ApplyToLibrary(76, presets, profiles));
        Assert.Contains("exe", images.Extensions);
        Assert.Contains("dll", images.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(76, presets, profiles));

        images.Extensions.RemoveAll(e => e is "exe" or "dll");
        profiles.Insert(0, new ToolbarProfile { Id = "my-icons", Extensions = ["EXE"] });
        Assert.True(SettingsMigrations.ApplyToLibrary(76, presets, profiles));
        Assert.DoesNotContain("exe", images.Extensions); // the user's own donut has it
        Assert.Contains("dll", images.Extensions);
        Assert.Equal("my-icons", ProfileSelector.Select(profiles, ["setup.exe"])?.Id);
    }
}
