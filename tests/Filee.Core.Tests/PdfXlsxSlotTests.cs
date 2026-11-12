// PDF → XLSX (#1343, #1187) works but was on no PDF donut, so people didn't find it. A new library has it on the PDF
// front, after DOCX; an existing library keeps its donuts as they are (#806), and the user adds it from the palette.

using Filee.Core.Presets;
using Filee.Core.Profiles;

namespace Filee.Core.Tests;

public class PdfXlsxSlotTests
{
    private static ToolbarProfile Pdf(List<ToolbarProfile> profiles) => profiles.Single(p => p.Id == "pdf");

    [Fact]
    public void A_new_library_has_XLSX_on_the_PDF_front_after_DOCX()
    {
        var pdf = Pdf(BuiltInData.CreateProfiles());

        Assert.Equal(pdf.PresetIds.IndexOf("to-docx") + 1, pdf.PresetIds.IndexOf("to-xlsx"));
        Assert.DoesNotContain("to-xlsx", pdf.BackPresetIds!);
    }

    [Fact]
    public void The_schema_58_PDF_donut_stays_without_it()
    {
        var pdf = Pdf(BuiltInData.ProfilesSchema58());

        Assert.DoesNotContain("to-xlsx", pdf.PresetIds);
        Assert.DoesNotContain("to-xlsx", pdf.BackPresetIds!);
    }
}
