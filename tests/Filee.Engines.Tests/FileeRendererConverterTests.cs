// Filee's renderer as an engine: off (the default), every route stays as it was; on, documents → PDF take it, and a
// file with what it doesn't draw yet keeps its usual route. The sample files are written here.

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Typesetting;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Filee.Engines.Tests;

[Collection("EngineEnvironment")] // FileeRenderer is process-wide
public sealed class FileeRendererConverterTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    private static readonly string[] Documents = ["hwpx", "hwp", "docx", "txt", "md", "html", "epub"];

    public void Dispose()
    {
        EngineEnvironment.FileeRenderer = false;
        fx.Catalog.Refresh();
    }

    private void Use(bool renderer)
    {
        EngineEnvironment.FileeRenderer = renderer;
        fx.Catalog.Refresh();
    }

    [Fact]
    public void Off_by_default_no_route_takes_it()
    {
        Use(false);
        var planner = fx.Catalog.CreatePlanner();

        Assert.False(fx.Catalog.StatusOf(fx.Converters.Single(c => c.Id == "layout")).IsAvailable);
        foreach (var format in Documents)
            Assert.DoesNotContain(planner.Plan(format, "pdf")!.Steps, s => s.Converter.Id == "layout");
    }

    [Fact]
    public void On_documents_go_to_pdf_through_it_and_sheets_slides_and_pdf_keep_their_routes()
    {
        Use(true);
        var planner = fx.Catalog.CreatePlanner();

        foreach (var format in Documents)
            Assert.Equal("layout", Assert.Single(planner.Plan(format, "pdf")!.Steps).Converter.Id);
        // Pictures of the pages come from its PDF through PDFium.
        Assert.Equal(["layout", "pdfium"], planner.Plan("docx", "png")!.Steps.Select(s => s.Converter.Id));
        foreach (var format in new[] { "xlsx", "pptx" })
            Assert.DoesNotContain(planner.Plan(format, "pdf")!.Steps, s => s.Converter.Id == "layout");
    }

    [Fact]
    public async Task On_a_document_is_written_by_it()
    {
        Use(true);
        var dir = fx.NewFolder();
        var source = Path.Combine(dir, "note.md");
        await File.WriteAllTextAsync(source, "# 제목\n\nFilee renders this paragraph itself. 한글 문단입니다.\n", TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "pdf" });

        Assert.Equal(JobState.Completed, job.State);
        using var pdf = PigDocument.Open(job.Outputs.Single());
        Assert.Contains("Filee", pdf.Information.Producer);
        Assert.Contains("Filee renders this paragraph itself.", pdf.GetPage(1).Text);
    }

    [Theory]
    [InlineData("chart.hwpx", "Contents/section0.xml", "<hs:sec><hp:p><hp:run><hp:chart id=\"1\"/></hp:run></hp:p></hs:sec>", "OLE objects and charts")]
    [InlineData("vertical.hwpx", "Contents/section0.xml", "<hs:sec><hp:secPr textDirection=\"VERTICAL\"/></hs:sec>", "vertical text")]
    [InlineData("form.hwpx", "Contents/section0.xml", "<hs:sec><hp:p><hp:run><hp:checkBtn/></hp:run></hp:p></hs:sec>", "form controls")]
    [InlineData("picture.hwpx", "BinData/image1.emf", "EMF", null)] // drawn by the metafile renderer
    [InlineData("vertical.docx", "word/document.xml", "<w:sectPr><w:textDirection w:val=\"tbRl\"/></w:sectPr>", "vertical text")]
    [InlineData("chart.docx", "word/document.xml", "<w:drawing><c:chart r:id=\"rId5\"/></w:drawing>", "OLE objects and charts")]
    [InlineData("smartart.docx", "word/document.xml", "<w:drawing><dgm:relIds r:dm=\"rId1\"/></w:drawing>", "SmartArt")]
    [InlineData("chart.odt", "content.xml", "<office:text><draw:frame><draw:object xlink:href=\"./Object 1\"/></draw:frame></office:text>", "OLE objects and charts")]
    [InlineData("vertical.odt", "content.xml", "<style:paragraph-properties style:writing-mode=\"tb-rl\"/>", "vertical text")]
    [InlineData("plain.docx", "word/document.xml", "<w:body><w:p><w:r><w:t>text</w:t></w:r></w:p></w:body>", null)]
    public void The_check_names_what_it_cant_draw_yet(string name, string part, string content, string? expected)
    {
        var dir = fx.NewFolder();
        var path = Path.Combine(dir, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry(part).Open());
            writer.Write(content);
        }

        Assert.Equal(expected, new FileeRendererConverter().FirstUnsupported(path, Path.GetExtension(name)[1..]));
    }

    [Fact]
    public async Task On_an_hwp_is_written_by_it_without_rhwp()
    {
        Use(true);
        var source = Path.Combine(fx.NewFolder(), "note.hwp");
        await File.WriteAllBytesAsync(source, new HwpBuilder { Flags = 1 }.PlainDocInfo().SectionStart().Paragraph("Filee lays out this HWP itself.").Build(),
            TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "pdf" });

        Assert.Equal(JobState.Completed, job.State);
        using var pdf = PigDocument.Open(job.Outputs.Single());
        Assert.Contains("Filee", pdf.Information.Producer);
        Assert.Contains("Filee lays out this HWP itself.", pdf.GetPage(1).Text);
    }

    [Fact]
    public void An_hwp_is_checked_as_the_hwpx_package_it_becomes()
    {
        var dir = fx.NewFolder();
        var plain = Path.Combine(dir, "plain.hwp");
        File.WriteAllBytes(plain, new HwpBuilder().PlainDocInfo().SectionStart().Paragraph("본문").Build());
        Assert.Null(new FileeRendererConverter().FirstUnsupported(plain, "hwp"));

        // An OLE object (a chart, a spreadsheet) in a paragraph.
        var builder = new HwpBuilder().PlainDocInfo().SectionStart();
        builder.Section.Add(HwpBuilder.Record(66, 0, HwpBuilder.Bytes(9, 0, (ushort)0, (byte)0, (byte)0, (ushort)1, (ushort)0, (ushort)0, 0, (ushort)0)));
        builder.Section.Add(HwpBuilder.Record(67, 1, HwpBuilder.Bytes(HwpBuilder.Control(11, "gso "), (ushort)13)));
        builder.Section.Add(HwpBuilder.Record(71, 1, HwpBuilder.Bytes(HwpBuilder.Id("gso "), 1u, 0, 0, 7200, 7200, 0, (short)0, (short)0, (short)0, (short)0, 1, 0, "")));
        builder.Section.Add(HwpBuilder.Record(76, 2, HwpBuilder.Bytes(HwpBuilder.Id("$ole"), HwpBuilder.Id("$ole"), 0, 0, (ushort)0, (ushort)1, 7200, 7200, 7200, 7200, 0, (short)0, 0, 0, (ushort)0, new byte[48])));
        builder.Section.Add(HwpBuilder.Record(84, 3, HwpBuilder.Bytes(1, 7200, 7200, (ushort)1)));
        var ole = Path.Combine(dir, "ole.hwp");
        File.WriteAllBytes(ole, builder.Build());
        Assert.Equal("OLE objects and charts", new FileeRendererConverter().FirstUnsupported(ole, "hwp"));

        // A file the reader refuses passes the check: the conversion stops with the reader's message.
        var locked = Path.Combine(dir, "locked.hwp");
        File.WriteAllBytes(locked, new HwpBuilder { Flags = 2 }.PlainDocInfo().SectionStart().Build());
        Assert.Null(new FileeRendererConverter().FirstUnsupported(locked, "hwp"));
    }

    [Fact]
    public void Text_formats_hold_nothing_it_cant_draw()
    {
        var dir = fx.NewFolder();
        var path = Path.Combine(dir, "note.txt");
        File.WriteAllText(path, "<hp:chart/> is just text here");

        Assert.Null(new FileeRendererConverter().FirstUnsupported(path, "txt"));
    }
}
