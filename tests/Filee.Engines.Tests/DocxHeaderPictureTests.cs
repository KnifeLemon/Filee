// A floating logo in the header of a Word document, through rhwp to PDF (issue #1152).

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;
using UglyToad.PdfPig;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public class DocxHeaderPictureTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const long Width = 1800000, Height = 720000; // 141.7 × 56.7 pt
    private const string PictureId = "rIdLogo";

    private const string Table =
        "<w:tbl><w:tblPr><w:tblW w:w=\"9026\" w:type=\"dxa\"/><w:tblStyle w:val=\"TableGrid\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"9026\"/></w:tblGrid>" +
        "<w:tr><w:tc><w:tcPr><w:tcW w:w=\"9026\" w:type=\"dxa\"/></w:tcPr><w:p><w:r><w:t>Classification</w:t></w:r></w:p></w:tc></w:tr></w:tbl>";

    /// <summary>A picture 1 inch from the paper's left edge and 200000 EMU (15.75 pt) from its top.</summary>
    private static string FloatingPicture() => Anchor(PictureUri, Picture(PictureId, Width, Height), Width, Height,
        "<wp:positionH relativeFrom=\"page\"><wp:posOffset>914400</wp:posOffset></wp:positionH>",
        "<wp:positionV relativeFrom=\"page\"><wp:posOffset>200000</wp:posOffset></wp:positionV>", "<wp:wrapNone/>");

    private async Task<string> PdfAsync(string header)
    {
        var rhwp = fx.Converters.Single(c => c.Id == "rhwp");
        Assert.SkipUnless(fx.Catalog.StatusOf(rhwp).IsAvailable, "rhwp not found (pwsh build/fetch-engines.ps1 -Only rhwp)");

        var docx = new DocxBuilder().Paragraph(P(R("Body")));
        docx.SectionProperties = $"<w:headerReference w:type=\"default\" r:id=\"{docx.Header(header)}\"/>{DefaultPage}";
        var path = docx.Save(Path.Combine(fx.NewFolder(), "source.docx"));
        AddHeaderPicture(path);

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "pdf" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    /// <summary>A header's pictures are related to the header part, not to the document.</summary>
    private static void AddHeaderPicture(string docx)
    {
        using var zip = ZipFile.Open(docx, ZipArchiveMode.Update);
        using (var writer = new StreamWriter(zip.CreateEntry("word/_rels/header1.xml.rels").Open()))
            writer.Write($"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"{PictureId}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/logo.png\"/></Relationships>");
        using var image = new MagickImage("gradient:#1D9E75-#7F77DD", 300, 120) { Format = MagickFormat.Png };
        using var stream = zip.CreateEntry("word/media/logo.png").Open();
        stream.Write(image.ToByteArray());
    }

    [Fact]
    public async Task A_table_after_a_floating_picture_is_drawn()
    {
        using var document = PdfDocument.Open(await PdfAsync(P(FloatingPicture()) + Table + P("")));

        var page = document.GetPage(1);
        Assert.Contains("Classification", page.Text);
        Assert.Single(page.GetImages());
    }

    [Fact]
    public async Task Text_beside_and_after_a_floating_picture_is_drawn()
    {
        using var document = PdfDocument.Open(await PdfAsync(P(FloatingPicture() + R("Beside")) + P(R("After"))));

        var text = document.GetPage(1).Text;
        Assert.Contains("Beside", text);
        Assert.Contains("After", text);
    }

    [Fact]
    public async Task A_floating_picture_stays_where_it_is_on_the_paper()
    {
        using var document = PdfDocument.Open(await PdfAsync(P(FloatingPicture())));

        var page = document.GetPage(1);
        var bounds = Assert.Single(page.GetImages()).Bounds;
        Assert.InRange(bounds.Left, 71, 73);
        Assert.InRange(page.Height - bounds.Top, 15, 17);
    }
}
