// PDF/A (#1367): a PDF/A preset for PDFs needs Ghostscript, which rewrites them after the route, so without it the
// slice says which engine to get. Other files still convert (LibreOffice writes PDF/A itself, or a notice says it isn't).

using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using FakeConverter = Filee.App.Tests.PresetAvailabilityTests.FakeConverter;

namespace Filee.App.Tests;

public class PdfAAvailabilityTests
{
    [AvaloniaFact]
    public void Pdf_a_of_pdfs_waits_for_ghostscript_and_other_files_do_not()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<ILocalizer>();
        var pdf = FormatRegistry.Get("pdf");
        var docx = FormatRegistry.Get("docx");
        var preset = BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.PdfAId);
        var ghostscript = new FakeConverter("ghostscript", ("eps", "pdf")) { Available = false };
        var catalog = new ConverterCatalog([new FakeConverter("pdfsharp", ("pdf", "pdf")), new FakeConverter("ooxml", ("docx", "pdf")), ghostscript]);
        var package = new EnginePackageState(new EnginePackage("ghostscript", [], 0, ["ghostscript"]), loc) { Status = EnginePackageStatus.NotInstalled };
        var availability = new PresetAvailability(catalog, loc, [package]);

        Assert.Equal(loc.Format("engine.reason.needs_package", package.Name), availability.Check(preset, [pdf]));
        Assert.Equal(loc["error.no_route"], new PresetAvailability(catalog, loc, []).Check(preset, [pdf]));
        Assert.Null(availability.Check(preset, [docx]));
        Assert.Null(availability.Check(preset, [pdf, docx]));
        Assert.Null(availability.Check(new Preset { TargetFormat = "pdf" }, [pdf])); // without the box, a plain copy

        ghostscript.Available = true;
        catalog.Refresh();
        Assert.Null(availability.Check(preset, [pdf]));
    }
}
