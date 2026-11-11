// The "PDF/A" preset (#1367): any PDF it writes, PDF → PDF too, becomes PDF/A, and settings schema 80 brings it to
// libraries saved before, without a donut slot.

using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class PdfAPresetTests
{
    [Fact]
    public void The_built_in_preset_writes_pdf_a_next_to_the_source()
    {
        var preset = BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.PdfAId);

        Assert.Equal("preset.pdf_a", preset.NameKey);
        Assert.Equal("pdf", preset.TargetFormat);
        Assert.True(preset.Document.PdfA);
        Assert.Equal("{name}_pdfa", preset.Output.FileNamePattern); // a PDF source isn't replaced by its copy
        Assert.False(preset.Pdf.FinishesPdf);
    }

    [Fact]
    public void Version_79_libraries_get_the_preset_after_searchable_pdf_on_no_donut()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var before = presets.FindIndex(p => p.Id == "searchable-pdf");
        presets.RemoveAll(p => p.Id == BuiltInData.PdfAId);

        Assert.True(SettingsMigrations.ApplyToLibrary(79, presets, profiles));

        Assert.Equal(before + 1, presets.FindIndex(p => p.Id == BuiltInData.PdfAId));
        Assert.DoesNotContain(profiles, p => p.PresetIds.Contains(BuiltInData.PdfAId) || p.BackPresetIds?.Contains(BuiltInData.PdfAId) == true);

        // A library already at the current version is left alone, so a preset the user deleted doesn't come back.
        presets.RemoveAll(p => p.Id == BuiltInData.PdfAId);
        Assert.False(SettingsMigrations.ApplyToLibrary(AppSettings.CurrentSchemaVersion, presets, profiles));
        Assert.DoesNotContain(presets, p => p.Id == BuiltInData.PdfAId);
    }

    [Fact]
    public void A_library_that_already_has_the_preset_keeps_its_own()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        presets.Single(p => p.Id == BuiltInData.PdfAId).Output.FileNamePattern = "{name}_archive";

        SettingsMigrations.ApplyToLibrary(79, presets, profiles);

        Assert.Single(presets, p => p.Id == BuiltInData.PdfAId);
        Assert.Equal("{name}_archive", presets.Single(p => p.Id == BuiltInData.PdfAId).Output.FileNamePattern);
    }
}
