// "Pages per sheet" (#737): the presets in the library (palette only, no donut slot) and how existing libraries get
// them (settings schema 63).

using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class PdfSheetPresetTests
{
    private static readonly string[] SheetIds = ["pdf-nup-2", "pdf-nup-4", "pdf-nup-6", "pdf-nup-8", "pdf-nup-9", "pdf-nup"];

    private static List<string> Slots(IEnumerable<ToolbarProfile> profiles) =>
        [.. profiles.Select(p => $"{p.Id}: {string.Join(",", p.PresetIds)} | {string.Join(",", p.BackPresetIds ?? [])}")];

    [Fact]
    public void A_new_library_has_the_group_after_edit_pdf_and_no_slot()
    {
        var presets = BuiltInData.CreatePresets();
        var edit = presets.FindIndex(p => p.Id == "pdf-edit");
        Assert.Equal(SheetIds, presets.Skip(edit + 1).Take(SheetIds.Length).Select(p => p.Id));

        var group = presets.Single(p => p.Id == "pdf-nup");
        Assert.True(group.IsGroup);
        Assert.Equal(SheetIds[..^1], group.SubPresetIds);
        Assert.True(group.SubPresetIds.Count <= Preset.MaxSubPresets);
        foreach (var n in PdfOptions.SheetLayouts)
        {
            var sheets = presets.Single(p => p.Id == $"pdf-nup-{n}");
            Assert.Equal("pdf", sheets.TargetFormat);
            Assert.Equal(n, sheets.Pdf.PagesPerSheet);
            Assert.True(sheets.Pdf.MakesSheets);
        }
        Assert.All(BuiltInData.CreateProfiles(), p => Assert.Empty(p.PresetIds.Concat(p.BackPresetIds ?? []).Intersect(SheetIds)));
    }

    [Fact]
    public void Schema_63_adds_the_presets_to_the_library_and_no_slot()
    {
        var presets = BuiltInData.CreatePresets().Where(p => !SheetIds.Contains(p.Id)).ToList();
        var profiles = BuiltInData.CreateProfiles();
        var slots = Slots(profiles);

        Assert.True(SettingsMigrations.ApplyToLibrary(62, presets, profiles));

        var edit = presets.FindIndex(p => p.Id == "pdf-edit");
        Assert.Equal(SheetIds, presets.Skip(edit + 1).Take(SheetIds.Length).Select(p => p.Id));
        Assert.Equal(slots, Slots(profiles));
        Assert.False(SettingsMigrations.ApplyToLibrary(AppSettings.CurrentSchemaVersion, presets, profiles));
    }

    [Fact]
    public void Schema_63_puts_the_option_back_when_an_older_filee_dropped_it()
    {
        var presets = BuiltInData.CreatePresets();
        var stripped = presets.Single(p => p.Id == "pdf-nup-6");
        stripped.Pdf.PagesPerSheet = 0;
        stripped.Name = "Handouts";

        Assert.True(SettingsMigrations.ApplyToLibrary(62, presets, BuiltInData.CreateProfiles()));

        Assert.Equal(6, stripped.Pdf.PagesPerSheet);
        Assert.Equal("Handouts", stripped.Name);
        Assert.Single(presets, p => p.Id == "pdf-nup-6");
    }
}
