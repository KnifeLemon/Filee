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
    public void An_excel_binary_workbook_finds_the_sheet_studio_on_the_back_of_its_donut()
    {
        // The studio is on the spreadsheets donut's back (since schema 42), and XLSB is on that donut (since 106):
        // no step of its own is needed, in a new library or after the migrations.
        var donut = ProfileSelector.Select(BuiltInData.CreateProfiles(), ["Budget.xlsb"]);
        Assert.Contains("tool-sheet-studio", donut!.BackPresetIds!);
        Assert.Contains(BuiltInData.CreatePresets(), p => p.Id == "tool-sheet-studio");

        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var spreadsheets = profiles.Single(p => p.Id == "spreadsheets");
        spreadsheets.Extensions.Remove("xlsb");
        spreadsheets.BackPresetIds!.Remove("tool-sheet-studio");
        presets.RemoveAll(p => p.Id == "tool-sheet-studio");
        Assert.True(SettingsMigrations.ApplyToLibrary(41, presets, profiles));
        Assert.Equal("spreadsheets", ProfileSelector.Select(profiles, ["Budget.xlsb"])?.Id);
        Assert.Contains("tool-sheet-studio", spreadsheets.BackPresetIds!);
    }

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
