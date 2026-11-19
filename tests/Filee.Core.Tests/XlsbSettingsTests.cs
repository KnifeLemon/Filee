using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class XlsbSettingsTests
{
    [Fact]
    public void An_excel_binary_workbook_has_the_spreadsheets_donut_in_a_new_library() =>
        Assert.Equal("spreadsheets", ProfileSelector.Select(BuiltInData.CreateProfiles(), ["Budget.xlsb"])?.Id);

    [Fact]
    public void Version_106_donuts_learn_xlsb_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var donut = profiles.Single(p => p.Id == "spreadsheets").Extensions;
        donut.Remove("xlsb");

        Assert.True(SettingsMigrations.ApplyToLibrary(105, presets, profiles));
        Assert.Contains("xlsb", donut);
        Assert.False(SettingsMigrations.ApplyToLibrary(105, presets, profiles));

        donut.Remove("xlsb");
        profiles.Insert(0, new ToolbarProfile { Id = "my-sheets", Name = "Binary sheets", Extensions = ["XLSB"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(105, presets, profiles));
        Assert.DoesNotContain("xlsb", donut); // the user's own donut has it
    }
}
