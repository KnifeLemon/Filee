// What Filee's renderer says it left out: a document it converts anyway (no other route, or the user's choice) with
// charts, embedded objects, SmartArt, form controls, videos or vertical text gets a note on its result that names each
// kind once with its count, with LibreOffice to install where it would draw the file faithfully. The readers count what
// they meet as they read (no second look through the file); a document without such things gets no note. Slide decks
// (PPTX, PPT, ODP) say the same from what the slide readers met. The files are built here.

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
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

    // ───────────────────────── Text boxes, controls and form fields ─────────────────────────

    /// <summary>A floating text box (wps:wsp) with its text written as <paramref name="vert"/> says.</summary>
    private static string TextBox(string vert, string text) => DocxBuilder.Anchor(DocxBuilder.ShapeUri,
        "<wps:wsp><wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"900000\" cy=\"1800000\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></wps:spPr>" +
        $"<wps:txbx><w:txbxContent>{DocxBuilder.P(DocxBuilder.R(text))}</w:txbxContent></wps:txbx><wps:bodyPr vert=\"{vert}\"/></wps:wsp>",
        900000, 1800000, "<wp:positionH relativeFrom=\"column\"><wp:posOffset>0</wp:posOffset></wp:positionH>",
        "<wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>0</wp:posOffset></wp:positionV>");

    /// <summary>A VML text box written top to bottom (Word's fallback for older readers, or an old document's own).</summary>
    private const string VmlVerticalTextBox =
        "<w:r><w:pict><v:shape id=\"_x0000_s1030\" style=\"width:60pt;height:120pt\"><v:textbox style=\"layout-flow:vertical;mso-layout-flow-alt:bottom-to-top\">" +
        "<w:txbxContent><w:p><w:r><w:t>Up</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape></w:pict></w:r>";

    /// <summary>An ActiveX control as Word writes it: the picture it saved of the control (when given) and the control.</summary>
    private static string ActiveX(string? picture) =>
        "<w:r><w:object xmlns:o=\"urn:schemas-microsoft-com:office:office\" w:dxaOrig=\"1500\" w:dyaOrig=\"400\">" +
        "<v:shape id=\"_x0000_i1030\" style=\"width:75pt;height:20pt\">" + (picture is null ? "" : $"<v:imagedata r:id=\"{picture}\" o:title=\"\"/>") + "</v:shape>" +
        "<w:control r:id=\"rId96\" w:name=\"CheckBox1\" w:shapeid=\"_x0000_i1030\"/></w:object></w:r>";

    /// <summary>A legacy form field (Developer → Legacy Tools): its settings in the begin fldChar, no result text.</summary>
    private static string FormField(string instruction, string formData) =>
        $"<w:r><w:fldChar w:fldCharType=\"begin\"><w:ffData><w:name w:val=\"Field\"/><w:enabled/>{formData}</w:ffData></w:fldChar></w:r>" +
        $"<w:r><w:instrText xml:space=\"preserve\"> {instruction} </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>";

    [Fact]
    public async Task Activex_controls_are_named_and_vertical_text_boxes_and_legacy_form_fields_drawn()
    {
        var builder = new DocxBuilder();
        var picture = builder.Image(DocumentFuzzTests.TinyPng(4, 3));
        builder.Paragraph(DocxBuilder.P(DocxBuilder.R("Boxes") + TextBox("eaVert", "세로") + TextBox("vert270", "Up")))
            .Paragraph(DocxBuilder.P(VmlVerticalTextBox))
            .Paragraph(DocxBuilder.P(DocxBuilder.R("Controls ") + ActiveX(picture) + ActiveX(null)))
            .Paragraph(DocxBuilder.P(DocxBuilder.R("Agree ") +
                FormField("FORMCHECKBOX", "<w:checkBox><w:sizeAuto/><w:default w:val=\"0\"/><w:checked/></w:checkBox>") +
                FormField("FORMCHECKBOX", "<w:checkBox><w:sizeAuto/><w:default w:val=\"0\"/></w:checkBox>") +
                DocxBuilder.R(" Pick ") +
                FormField("FORMDROPDOWN", "<w:ddList><w:result w:val=\"1\"/><w:listEntry w:val=\"Alpha\"/><w:listEntry w:val=\"Beta\"/></w:ddList>")));
        var source = builder.Save(Path.Combine(fx.NewFolder(), "form.docx"));

        var notice = Assert.Single(await ConvertAsync(source, "docx"));

        // The legacy check boxes and drop-down are drawn as Word prints them, and the text boxes written top to bottom
        // turned into their boxes, so the note doesn't name them.
        Assert.Equal(WithLibreOffice, notice.Key);
        Assert.Equal([Part("form_controls", 1), Part("form_controls_pictured", 1)], notice.Parts);
        var document = DocxReader.Read(source, Path.Combine(fx.NewFolder(), "media"));
        var text = string.Concat(document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HText>().Select(t => t.Text));
        Assert.Contains("Agree ☒☐ Pick Beta", text);

        // The check names the controls too, so such a file keeps LibreOffice's route while LibreOffice is there.
        var renderer = new FileeRendererConverter();
        var vertical = new DocxBuilder().Paragraph(DocxBuilder.P(TextBox("vert", "Down"))).Save(Path.Combine(fx.NewFolder(), "vertical.docx"));
        var vml = new DocxBuilder().Paragraph(DocxBuilder.P(VmlVerticalTextBox)).Save(Path.Combine(fx.NewFolder(), "vml.docx"));
        var control = new DocxBuilder().Paragraph(DocxBuilder.P(ActiveX(null))).Save(Path.Combine(fx.NewFolder(), "control.docx"));
        var fields = new DocxBuilder().Paragraph(DocxBuilder.P(FormField("FORMCHECKBOX", "<w:checkBox><w:sizeAuto/><w:default w:val=\"1\"/></w:checkBox>")))
            .Save(Path.Combine(fx.NewFolder(), "fields.docx"));
        var across = new DocxBuilder().Paragraph(DocxBuilder.P(TextBox("horz", "Across"))).Save(Path.Combine(fx.NewFolder(), "across.docx"));
        Assert.Null(renderer.FirstUnsupported(vertical, "docx"));
        Assert.Null(renderer.FirstUnsupported(vml, "docx"));
        Assert.Equal("form controls", renderer.FirstUnsupported(control, "docx"));
        Assert.Null(renderer.FirstUnsupported(fields, "docx"));
        Assert.Null(renderer.FirstUnsupported(across, "docx"));
        Assert.Empty(await ConvertAsync(fields, "docx"));
        Assert.Empty(await ConvertAsync(across, "docx"));
    }

    [Fact]
    public async Task A_chart_in_a_header_several_sections_share_is_counted_once()
    {
        var builder = new DocxBuilder();
        var header = builder.Header(DocxBuilder.P(Chart(1)));
        var reference = $"<w:headerReference w:type=\"default\" r:id=\"{header}\"/>";
        // Three sections on their own pages, each naming the same header: it is read for each of them.
        builder.Paragraph(DocxBuilder.P(DocxBuilder.R("One"), $"<w:sectPr>{reference}{DocxBuilder.DefaultPage}</w:sectPr>"))
            .Paragraph(DocxBuilder.P(DocxBuilder.R("Two"), $"<w:sectPr>{reference}{DocxBuilder.DefaultPage}</w:sectPr>"))
            .Paragraph(DocxBuilder.P(DocxBuilder.R("Three")));
        builder.SectionProperties = reference + DocxBuilder.DefaultPage;
        var source = builder.Save(Path.Combine(fx.NewFolder(), "header.docx"));

        var document = DocxReader.Read(source, Path.Combine(fx.NewFolder(), "media"));
        Assert.Equal(3, document.Sections.Count);
        Assert.All(document.Sections, section => Assert.NotEmpty(section.Headers));

        var notice = Assert.Single(await ConvertAsync(source, "docx"));
        Assert.Equal([Part("charts", 1)], notice.Parts);
    }

    // ───────────────────────── Slides ─────────────────────────

    private static string Frame(SlideDeckBuilder deck, string uri, string content, double x) =>
        $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{deck.NextId()}\" name=\"Frame\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
        $"{SlideDeckBuilder.Xfrm(x, 60, 200, 150, element: "p:xfrm")}<a:graphic><a:graphicData uri=\"{uri}\">{content}</a:graphicData></a:graphic></p:graphicFrame>";

    [Fact]
    public async Task A_powerpoint_deck_names_what_the_slide_renderer_leaves_out_and_draws_another_way()
    {
        var deck = new SlideDeckBuilder();
        var picture = deck.Media(SlideDeckBuilder.TestPicture(40, 30));
        // An OLE object with its picture, a chart PowerPoint 2016 made (chartex, no fallback), ink, a shadowed shape
        // and a preset shape Filee doesn't know; the second slide has the same OLE object again.
        var ole = Frame(deck, "http://schemas.openxmlformats.org/presentationml/2006/ole",
            "<p:oleObj progId=\"Visio.Drawing.15\" r:id=\"rId9\"><p:embed/><p:pic><p:nvPicPr><p:cNvPr id=\"0\" name=\"\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>" +
            "<p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr/></p:pic></p:oleObj>", 20);
        var chartEx = Frame(deck, "http://schemas.microsoft.com/office/drawing/2014/chartex",
            "<cx:chart xmlns:cx=\"http://schemas.microsoft.com/office/drawing/2014/chartex\" r:id=\"rId8\"/>", 240);
        var shadow = deck.Shape("rect", 480, 60, 100, 100, fill: SlideDeckBuilder.Solid("#2050A0") +
            "<a:effectLst><a:outerShdw blurRad=\"40000\" dist=\"20000\" dir=\"5400000\"><a:srgbClr val=\"000000\"/></a:outerShdw></a:effectLst>");
        var unknown = deck.Shape("notAPresetShape", 600, 60, 100, 100, fill: SlideDeckBuilder.Solid("#20A050"));
        deck.Slide(ole + chartEx + "<p:contentPart r:id=\"rId7\"/>" + shadow + unknown, pictures: new Dictionary<string, string> { ["rId2"] = picture })
            .Slide(ole, pictures: new Dictionary<string, string> { ["rId2"] = picture });
        var source = deck.Save(Path.Combine(fx.NewFolder(), "deck.pptx"));

        var notice = Assert.Single(await ConvertAsync(source, "pptx"));

        Assert.Equal(WithLibreOffice, notice.Key);
        Assert.Equal([Part("charts", 1), Part("ink", 1), Part("objects_pictured", 2), Part("shapes", 1), Part("effects")], notice.Parts);
        Assert.Equal(Key, Assert.Single(await ConvertAsync(source, "pptx", libreOffice: true)).Key);
    }

    [Fact]
    public async Task An_opendocument_presentation_names_what_the_slide_renderer_leaves_out_once_per_shape()
    {
        // An OLE object without a picture on the master shows on both pages and is counted once; a 3D scene, a shadow
        // and an object drawn from its replacement picture are on the first page.
        var builder = new OdpBuilder
        {
            ContentStyles = OdpBuilder.GraphicStyle("shadow", "draw:shadow=\"visible\" draw:fill=\"solid\""),
            MasterStyles = "<style:master-page style:name=\"Default\" style:page-layout-name=\"PM1\">" +
                           "<draw:frame svg:x=\"1cm\" svg:y=\"1cm\" svg:width=\"3cm\" svg:height=\"2cm\"><draw:object-ole xlink:href=\"./Object 9\"/></draw:frame></style:master-page>",
            Pages = OdpBuilder.Page("<dr3d:scene svg:x=\"5cm\" svg:y=\"1cm\" svg:width=\"5cm\" svg:height=\"5cm\"/>" +
                                    "<draw:rect draw:style-name=\"shadow\" svg:x=\"11cm\" svg:y=\"1cm\" svg:width=\"5cm\" svg:height=\"5cm\"/>" +
                                    "<draw:frame svg:x=\"17cm\" svg:y=\"1cm\" svg:width=\"5cm\" svg:height=\"4cm\"><draw:object-ole xlink:href=\"./Object 1\"/>" +
                                    "<draw:image xlink:href=\"./ObjectReplacements/Object 1\"/></draw:frame>") +
                    OdpBuilder.Page("<draw:rect svg:x=\"1cm\" svg:y=\"5cm\" svg:width=\"5cm\" svg:height=\"5cm\"/>"),
        };
        builder.Parts["ObjectReplacements/Object 1"] = DocumentFuzzTests.TinyPng(4, 3);
        var source = builder.Save(Path.Combine(fx.NewFolder(), "deck.odp"));

        var notice = Assert.Single(await ConvertAsync(source, "odp"));

        Assert.Equal(WithLibreOffice, notice.Key);
        Assert.Equal([Part("objects", 1), Part("shapes_left_out", 1), Part("objects_pictured", 1), Part("effects")], notice.Parts);
    }

    [Fact]
    public async Task Decks_the_slide_renderer_draws_whole_get_no_note()
    {
        var deck = new SlideDeckBuilder();
        deck.Slide(deck.Shape("rect", 60, 60, 200, 100, fill: SlideDeckBuilder.Solid("#2050A0"), paragraphs: SlideDeckBuilder.Paragraph("Plain")));
        var odp = new OdpBuilder { Pages = OdpBuilder.Page("<draw:rect svg:x=\"1cm\" svg:y=\"1cm\" svg:width=\"5cm\" svg:height=\"5cm\"/>") };
        var folder = fx.NewFolder();

        Assert.Empty(await ConvertAsync(deck.Save(Path.Combine(folder, "plain.pptx")), "pptx"));
        Assert.Empty(await ConvertAsync(odp.Save(Path.Combine(folder, "plain.odp")), "odp"));
    }
}
