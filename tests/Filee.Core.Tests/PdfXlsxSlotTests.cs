// PDF → XLSX (#1343, #1187) works but was on no PDF donut, so people didn't find it. A new library has it on the PDF
// front, after DOCX; settings schema 80 gives it to a PDF donut face that is still exactly as Filee made it.

using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class PdfXlsxSlotTests
{
    private const string Xlsx = "to-xlsx";

    private static ToolbarProfile Pdf(List<ToolbarProfile> profiles) => profiles.Single(p => p.Id == "pdf");

    internal static List<string> Schema58PdfBack() => Pdf(BuiltInData.ProfilesSchema58()).BackPresetIds!;

    /// <summary>The schema 58 PDF back face as schema 80 leaves it: XLSX after "Compress PDF".</summary>
    internal static List<string> Schema58PdfBackAt80()
    {
        var back = Schema58PdfBack();
        back.Insert(back.IndexOf("compress-pdf") + 1, Xlsx);
        return back;
    }

    /// <summary>The schema 58 donuts with nothing left for schema 80 to do, for tests of older steps that expect no other change.</summary>
    internal static List<ToolbarProfile> Schema58ProfilesAt80()
    {
        var profiles = BuiltInData.ProfilesSchema58();
        Pdf(profiles).BackPresetIds = Schema58PdfBackAt80();
        return profiles;
    }

    [Fact]
    public void A_new_library_has_XLSX_on_the_PDF_front_after_DOCX()
    {
        var front = Pdf(BuiltInData.CreateProfiles()).PresetIds;

        Assert.Equal(front.IndexOf("to-docx") + 1, front.IndexOf(Xlsx));
        Assert.DoesNotContain(Xlsx, Pdf(BuiltInData.CreateProfiles()).BackPresetIds!);
    }

    [Fact]
    public void A_version_79_library_from_the_slot_cleanup_gets_it_on_the_front_like_a_new_one()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var pdf = Pdf(profiles);
        pdf.PresetIds.Remove(Xlsx);
        var back = pdf.BackPresetIds!.ToList();

        Assert.True(SettingsMigrations.ApplyToLibrary(79, presets, profiles));

        Assert.Equal(Pdf(BuiltInData.CreateProfiles()).PresetIds, pdf.PresetIds);
        Assert.Equal(back, pdf.BackPresetIds);
        Assert.False(SettingsMigrations.ApplyToLibrary(AppSettings.CurrentSchemaVersion, presets, profiles));
    }

    [Fact]
    public void An_older_library_whose_front_is_full_gets_it_on_the_back_after_Compress_PDF()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.ProfilesSchema58();
        var pdf = Pdf(profiles);
        var front = pdf.PresetIds.ToList();
        Assert.Equal(ToolbarProfile.MaxSlices, front.Count);

        Assert.True(SettingsMigrations.ApplyToLibrary(79, presets, profiles));

        Assert.Equal(front, pdf.PresetIds);
        Assert.Equal(pdf.BackPresetIds!.IndexOf("compress-pdf") + 1, pdf.BackPresetIds.IndexOf(Xlsx));
        Assert.Equal(ToolbarProfile.MaxSlices, pdf.BackPresetIds.Count);
        Assert.False(SettingsMigrations.ApplyToLibrary(79, presets, profiles)); // once only
    }

    [Fact]
    public void A_face_the_user_changed_stays_as_it_is()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.ProfilesSchema58();
        var pdf = Pdf(profiles);
        pdf.BackPresetIds!.Remove("to-cbz");
        var back = pdf.BackPresetIds.ToList();

        Assert.False(SettingsMigrations.ApplyToLibrary(79, presets, profiles));
        Assert.Equal(back, pdf.BackPresetIds);

        profiles = BuiltInData.CreateProfiles();
        pdf = Pdf(profiles);
        pdf.PresetIds.Remove(Xlsx);
        pdf.PresetIds.Remove("to-hwp");
        var front = pdf.PresetIds.ToList();

        Assert.False(SettingsMigrations.ApplyToLibrary(79, presets, profiles));
        Assert.Equal(front, pdf.PresetIds);
    }

    [Fact]
    public void A_donut_that_already_has_it_or_a_library_without_the_preset_is_left_alone()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.ProfilesSchema58();
        var pdf = Pdf(profiles);
        pdf.PresetIds[^1] = Xlsx; // the user put it on the front in place of "Merge PDF"
        var back = pdf.BackPresetIds!.ToList();

        Assert.False(SettingsMigrations.ApplyToLibrary(79, presets, profiles));
        Assert.Equal(back, pdf.BackPresetIds);

        profiles = BuiltInData.ProfilesSchema58();
        presets.RemoveAll(p => p.Id == Xlsx);

        Assert.False(SettingsMigrations.ApplyToLibrary(79, presets, profiles));
        Assert.DoesNotContain(Xlsx, Pdf(profiles).BackPresetIds!);
    }

    [Fact]
    public void A_library_without_a_PDF_donut_gets_none()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.ProfilesSchema58();
        profiles.RemoveAll(p => p.Id == "pdf");

        Assert.False(SettingsMigrations.ApplyToLibrary(79, presets, profiles));
        Assert.DoesNotContain(profiles, p => p.Id == "pdf");
    }
}
