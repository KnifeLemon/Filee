// Formats only LibreOffice reads (Works / WPS, WordPerfect, AbiWord, Apple iWork, StarOffice, Publisher, CorelDRAW,
// Visio, CGM, ODG): the --convert-to filter of every edge must match the application that opens the source, and
// sample files (AbiWord, ODG written in the test) convert when LibreOffice is installed.

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Office;
using ImageMagick;
using UglyToad.PdfPig;

namespace Filee.Engines.Tests;

public class LibreOfficeFormatTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private IConverter LibreOffice => fx.Converters.Single(c => c.Id == "libreoffice");

    private void SkipUnlessInstalled() =>
        Assert.SkipUnless(fx.Catalog.StatusOf(LibreOffice).IsAvailable, "LibreOffice not installed");

    [Theory]
    [InlineData("wpd", "pdf", null, "pdf:writer_pdf_Export")]
    [InlineData("pages", "docx", null, "docx:MS Word 2007 XML")]
    [InlineData("abw", "odt", null, "odt")]
    [InlineData("wps", "txt", null, "txt:Text (encoded):UTF8")]
    [InlineData("numbers", "xlsx", null, "xlsx:Calc MS Excel 2007 XML")]
    [InlineData("sdc", "csv", null, "csv:Text - txt - csv (StarCalc):44,34,76")]
    [InlineData("et", "pdf", null, "pdf:calc_pdf_Export")]
    [InlineData("key", "pptx", null, "pptx:Impress MS PowerPoint 2007 XML")]
    [InlineData("dps", "odp", null, "odp")]
    [InlineData("sda", "pdf", "sdd", "pdf:impress_pdf_Export")]
    [InlineData("sda", "pdf", "sda", "pdf:draw_pdf_Export")]
    [InlineData("cdr", "pdf", null, "pdf:draw_pdf_Export")]
    [InlineData("vsd", "svg", null, "svg:draw_svg_Export")]
    [InlineData("pub", "png", null, "png:draw_png_Export")]
    [InlineData("odg", "odg", null, "odg:draw8")]
    [InlineData("cgm", "odg", null, "odg:impress8_draw")]
    [InlineData("cgm", "svg", null, "svg:impress_svg_Export")]
    [InlineData("emf", "pdf", null, "pdf:draw_pdf_Export")]
    [InlineData("wmf", "png", null, "png:draw_png_Export")]
    public void Filters_follow_the_application_that_opens_the_file(string from, string to, string? extension, string expected) =>
        Assert.Equal(expected, LibreOfficeConverter.FilterFor(from, to, pdfA: false, extension));

    [Fact]
    public void Every_edge_has_a_filter_of_its_application()
    {
        string[] draw = ["pub", "cdr", "vsd", "odg", "emf", "wmf"];
        string[] impress = ["pptx", "ppt", "odp", "key", "dps", "sda", "cgm"];
        string[] calc = ["xlsx", "xls", "ods", "csv", "et", "numbers", "sdc"];
        foreach (var edge in LibreOffice.Edges)
        {
            var filter = LibreOfficeConverter.FilterFor(edge.From, edge.To, pdfA: false);
            var application = draw.Contains(edge.From) ? "draw" : impress.Contains(edge.From) ? "impress" : calc.Contains(edge.From) ? "calc" : "writer";
            if (edge.To is "pdf" or "svg" or "png")
                Assert.Equal($"{edge.To}:{application}_{edge.To}_Export", filter);
            else if (edge.To is not ("odt" or "ods" or "odp"))
                Assert.Contains(':', filter);
        }
        // Built-in engines stay first: LibreOffice's edges cost more than a normal direct conversion.
        Assert.All(LibreOffice.Edges, e => Assert.True(e.Cost >= 25));
        foreach (var from in new[] { "dot", "wps", "wpd", "lwp", "abw", "pages", "sdw", "et", "numbers", "sdc", "key", "dps", "sda", "pub", "cdr", "cgm", "vsd", "odg" })
            Assert.Contains(LibreOffice.Edges, e => e.From == from && e.To == "pdf");
    }

    [Fact]
    public async Task AbiWord_to_pdf_and_docx()
    {
        SkipUnlessInstalled();
        var abw = Path.Combine(fx.NewFolder(), "note.abw");
        await File.WriteAllTextAsync(abw, """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE abiword PUBLIC "-//ABISOURCE//DTD AWML 1.0 Strict//EN" "http://www.abisource.com/awml.dtd">
            <abiword template="false" xmlns="http://www.abisource.com/awml.dtd" version="3.0.4" fileformat="1.1">
            <section><p>Hello AbiWord</p><p>Second paragraph</p></section>
            </abiword>
            """, TestContext.Current.CancellationToken);

        var pdf = await ConvertAsync(abw, "pdf");
        using (var document = PdfDocument.Open(pdf))
            Assert.Contains("Hello AbiWord", string.Concat(document.GetPages().Select(p => p.Text)));
        var docx = await ConvertAsync(abw, "docx");
        Assert.Contains("Second paragraph", string.Concat(DocxAssert.Paragraphs(DocxAssert.ReadBack(docx).Sections.SelectMany(s => s.Blocks)).Select(DocxAssert.TextOf)));
    }

    [Fact]
    public async Task Drawing_to_svg_png_and_pdf()
    {
        SkipUnlessInstalled();
        var odg = Path.Combine(fx.NewFolder(), "drawing.odg");
        using (var zip = ZipFile.Open(odg, ZipArchiveMode.Create))
        {
            void Write(string name, string text, CompressionLevel level = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(name, level).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
            Write("mimetype", "application/vnd.oasis.opendocument.graphics", CompressionLevel.NoCompression);
            Write("META-INF/manifest.xml",
                "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\">" +
                "<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"1.2\" manifest:media-type=\"application/vnd.oasis.opendocument.graphics\"/>" +
                "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/></manifest:manifest>");
            Write("content.xml",
                "<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" " +
                "xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" office:version=\"1.2\">" +
                "<office:body><office:drawing><draw:page draw:name=\"page1\"><draw:rect svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"8cm\" svg:height=\"4cm\"><text:p>Filee drawing</text:p></draw:rect>" +
                "</draw:page></office:drawing></office:body></office:document-content>");
        }

        Assert.Contains("<svg", await File.ReadAllTextAsync(await ConvertAsync(odg, "svg"), TestContext.Current.CancellationToken));
        var png = new MagickImageInfo(await ConvertAsync(odg, "png"));
        Assert.True(png.Width > 100);
        using var pdf = PdfDocument.Open(await ConvertAsync(odg, "pdf"));
        Assert.Contains("Filee drawing", pdf.GetPage(1).Text);
    }

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }
}
