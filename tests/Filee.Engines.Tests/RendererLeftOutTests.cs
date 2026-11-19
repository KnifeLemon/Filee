// What Filee's renderer says it left out: a document it converts anyway (no other route, or the user's choice) with
// charts, embedded objects, SmartArt, form controls, videos or vertical text gets a note on its result that names each
// kind once with its count, with LibreOffice to install where it would draw the file faithfully. The readers count what
// they meet as they read (no second look through the file); a document without such things gets no note. The files are
// built here.

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Typesetting;

namespace Filee.Engines.Tests;

public sealed class RendererLeftOutTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Key = "notice.renderer_left_out";
    private const string WithLibreOffice = "notice.renderer_left_out_libreoffice";

    /// <summary>Converts <paramref name="source"/> to PDF with the renderer and returns the notes it gave.</summary>
    private static async Task<List<LeftOutNotice>> ConvertAsync(string source, string format, bool libreOffice = false)
    {
        var notices = new List<LeftOutNotice>();
        var output = Path.ChangeExtension(source, ".out.pdf");
        var outputs = await new FileeRendererConverter(() => libreOffice).ConvertAsync(
            new ConversionStep(source, format, "pdf", new Preset(), new HwpxAssert.FixedPath(output), Path.GetDirectoryName(source)!, LeftOut: notices.Add),
            null, TestContext.Current.CancellationToken);
        Assert.True(new FileInfo(Assert.Single(outputs)).Length > 0);
        return notices;
    }

    private static LeftOutPart Part(string kind, int count = 0) => new($"{Key}.{kind}", count);

    private static string Chart(int id) =>
        "<w:r><w:drawing><wp:inline><wp:extent cx=\"3600000\" cy=\"2000000\"/><wp:docPr id=\"" + id + "\" name=\"Chart\"/><a:graphic>" +
        "<a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\">" +
        "<c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"rId90\"/></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

    private const string SmartArt =
        "<w:r><w:drawing><wp:inline><wp:extent cx=\"3600000\" cy=\"2000000\"/><wp:docPr id=\"9\" name=\"Diagram\"/><a:graphic>" +
        "<a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\">" +
        "<dgm:relIds xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" r:dm=\"rId91\" r:lo=\"rId92\" r:qs=\"rId93\" r:cs=\"rId94\"/>" +
        "</a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

    /// <summary>An embedded (OLE) object as Word writes it: its saved picture (when <paramref name="picture"/> is given) and the object.</summary>
    private static string Ole(string? picture) =>
        "<w:r><w:object xmlns:o=\"urn:schemas-microsoft-com:office:office\" w:dxaOrig=\"2000\" w:dyaOrig=\"1000\">" +
        "<v:shape id=\"_x0000_i1025\" style=\"width:100pt;height:50pt\">" + (picture is null ? "" : $"<v:imagedata r:id=\"{picture}\" o:title=\"\"/>") + "</v:shape>" +
        "<o:OLEObject Type=\"Embed\" ProgID=\"Visio.Drawing.15\" ShapeID=\"_x0000_i1025\" DrawAspect=\"Content\" ObjectID=\"_1\" r:id=\"rId95\"/></w:object></w:r>";

    [Fact]
    public async Task A_word_document_names_its_charts_objects_smartart_and_vertical_text()
    {
        var builder = new DocxBuilder();
        var picture = builder.Image(DocumentFuzzTests.TinyPng(4, 3));
        builder.Paragraph(DocxBuilder.P(DocxBuilder.R("Sales") + Chart(1) + Chart(2)))
            .Paragraph(DocxBuilder.P(SmartArt + Ole(picture) + Ole(null)))
            .Paragraph("<w:tbl><w:tblGrid><w:gridCol w:w=\"2000\"/></w:tblGrid><w:tr><w:tc><w:tcPr><w:tcW w:w=\"2000\" w:type=\"dxa\"/><w:textDirection w:val=\"tbRl\"/></w:tcPr>" +
                       DocxBuilder.P(DocxBuilder.R("세로")) + "</w:tc></w:tr></w:tbl>" + DocxBuilder.P(""));
        builder.SectionProperties = DocxBuilder.DefaultPage + "<w:textDirection w:val=\"tbRl\"/>";
        var source = builder.Save(Path.Combine(fx.NewFolder(), "report.docx"));

        var notice = Assert.Single(await ConvertAsync(source, "docx"));

        // Left out first, then what was drawn another way; vertical text once, however many places have it.
        Assert.Equal(WithLibreOffice, notice.Key);
        Assert.Equal([Part("charts", 2), Part("smartart", 1), Part("objects", 1), Part("objects_pictured", 1), Part("vertical_text")], notice.Parts);
    }

    [Fact]
    public async Task With_libreoffice_installed_the_note_doesnt_ask_for_it()
    {
        var builder = new DocxBuilder();
        builder.Paragraph(DocxBuilder.P(Chart(1)));
        var source = builder.Save(Path.Combine(fx.NewFolder(), "chart.docx"));

        var notice = Assert.Single(await ConvertAsync(source, "docx", libreOffice: true));

        Assert.Equal(Key, notice.Key);
        Assert.Equal([Part("charts", 1)], notice.Parts);
    }

    [Fact]
    public async Task An_opendocument_text_names_its_objects_formulas_form_controls_and_vertical_text()
    {
        var builder = new OdtBuilder
        {
            ContentStyles = "<style:style style:name=\"ce1\" style:family=\"table-cell\"><style:table-cell-properties style:writing-mode=\"tb-rl\"/></style:style>",
            Body =
                "<text:p>Chart <draw:frame text:anchor-type=\"as-char\" svg:width=\"4cm\" svg:height=\"2cm\"><draw:object xlink:href=\"./Object 1\"/>" +
                "<draw:image xlink:href=\"./ObjectReplacements/Object 1\"/></draw:frame></text:p>" +
                "<text:p>Formula <draw:frame text:anchor-type=\"as-char\" svg:width=\"2cm\" svg:height=\"1cm\"><draw:object xlink:href=\"./Object 2\"/></draw:frame></text:p>" +
                "<text:p>Lost <draw:frame text:anchor-type=\"as-char\" svg:width=\"2cm\" svg:height=\"1cm\"><draw:object-ole xlink:href=\"./Object 3\"/></draw:frame>" +
                " and a box <draw:control draw:control=\"control1\" svg:width=\"2cm\" svg:height=\"1cm\"/></text:p>" +
                "<table:table><table:table-column/><table:table-row><table:table-cell table:style-name=\"ce1\"><text:p>세로</text:p></table:table-cell></table:table-row></table:table>",
        };
        builder.Parts["ObjectReplacements/Object 1"] = DocumentFuzzTests.TinyPng(4, 3);
        builder.Parts["Object 2/content.xml"] = Encoding.UTF8.GetBytes(
            "<math xmlns=\"http://www.w3.org/1998/Math/MathML\"><semantics><mi>x</mi><annotation encoding=\"StarMath 5.0\">x over y</annotation></semantics></math>");
        var source = builder.Save(Path.Combine(fx.NewFolder(), "report.odt"));

        var notice = Assert.Single(await ConvertAsync(source, "odt"));

        Assert.Equal(WithLibreOffice, notice.Key);
        Assert.Equal([Part("objects", 1), Part("form_controls", 1), Part("objects_pictured", 1), Part("formulas", 1), Part("vertical_text")], notice.Parts);
    }

    [Fact]
    public async Task An_hwpx_names_its_chart_object_video_and_vertical_text_without_asking_for_libreoffice()
    {
        // The 한글-style package has a chart whose OLE fallback has no saved picture; its first section is made
        // vertical and a chart and a video are added.
        var folder = fx.NewFolder();
        var source = Path.Combine(folder, "report.hwpx");
        File.Copy(HwpxReaderTests.HangulStylePackage(folder), source);
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("Contents/section0.xml")!;
            string xml;
            using (var reader = new StreamReader(entry.Open()))
                xml = reader.ReadToEnd();
            entry.Delete();
            var vertical = xml.IndexOf("<hp:secPr id=\"\" textDirection=\"HORIZONTAL\"", StringComparison.Ordinal);
            Assert.True(vertical >= 0);
            xml = xml[..vertical] + "<hp:secPr id=\"\" textDirection=\"VERTICAL\"" + xml[(vertical + "<hp:secPr id=\"\" textDirection=\"HORIZONTAL\"".Length)..];
            var end = xml.IndexOf("</hp:run>", StringComparison.Ordinal);
            xml = xml.Insert(end, "<hp:chart id=\"70\" chartIDRef=\"Chart/chart2.xml\"/><hp:video id=\"71\" videotype=\"Local\" fileIDRef=\"\"/>");
            using var writer = new StreamWriter(zip.CreateEntry("Contents/section0.xml").Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }

        var notice = Assert.Single(await ConvertAsync(source, "hwpx"));

        // No optional engine draws an HWPX better: nothing to install.
        Assert.Equal(Key, notice.Key);
        Assert.Equal([Part("charts", 1), Part("objects", 1), Part("videos", 1), Part("vertical_text")], notice.Parts);
    }

    [Fact]
    public async Task Documents_it_draws_whole_get_no_note()
    {
        var folder = fx.NewFolder();
        var docx = new DocxBuilder();
        docx.Paragraph(DocxBuilder.P(DocxBuilder.R("Plain text") + $"<w:r><w:drawing><wp:inline><wp:extent cx=\"360000\" cy=\"270000\"/><wp:docPr id=\"1\" name=\"P\"/><a:graphic>" +
            "<a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:blipFill>" +
            $"<a:blip r:embed=\"{docx.Image(DocumentFuzzTests.TinyPng(4, 3))}\"/></pic:blipFill></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>"));
        var odt = new OdtBuilder { Body = "<text:p>Plain <draw:frame text:anchor-type=\"as-char\" svg:width=\"1cm\" svg:height=\"1cm\"><draw:image xlink:href=\"Pictures/a.png\"/></draw:frame></text:p>" };
        odt.Parts["Pictures/a.png"] = DocumentFuzzTests.TinyPng(4, 3);
        var hwp = Path.Combine(folder, "plain.hwp");
        await File.WriteAllBytesAsync(hwp, new HwpBuilder { Flags = 1 }.PlainDocInfo().SectionStart().Paragraph("본문").Build(), TestContext.Current.CancellationToken);

        Assert.Empty(await ConvertAsync(docx.Save(Path.Combine(folder, "plain.docx")), "docx"));
        Assert.Empty(await ConvertAsync(odt.Save(Path.Combine(folder, "plain.odt")), "odt"));
        Assert.Empty(await ConvertAsync(hwp, "hwp"));
    }
}
