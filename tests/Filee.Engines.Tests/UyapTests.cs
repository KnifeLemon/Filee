// UYAP documents (.udf, #1338) → the document model, PDF and DOCX with the built-in reader, and the other ".udf": a
// Universal Disk Format disc image, which still unpacks with Extract and is refused by the document routes with the
// reason. The documents are written here the way the UYAP Document Editor writes them (one content.xml in a ZIP, the
// text in a CDATA block, runs by offset); the disc is written with DiscUtils' UDF builder.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using DiscUtils.Udf;
using DocumentFormat.OpenXml.Packaging;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Uyap;
using ImageMagick;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Filee.Engines.Tests;

[Collection("EngineEnvironment")] // the Filee renderer switch is process-wide
public sealed class UyapTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    public void Dispose()
    {
        EngineEnvironment.FileeRenderer = false;
        fx.Catalog.Refresh();
    }

    // ───────────────────────── Writing UYAP files ─────────────────────────

    /// <summary>A paragraph of a UYAP file: its attributes and runs (text, run attributes).</summary>
    private sealed record Para(string Attributes, params (string Text, string Attributes)[] Runs);

    /// <summary>
    /// Writes a UYAP document: the paragraphs' text joined into the CDATA block, each paragraph ended by "\n" in its
    /// last run, runs pointing into it by offset. <paramref name="extraWithText"/> adds elements after them, with their text.
    /// </summary>
    private static string MakeUdf(string folder, string name, IEnumerable<Para> paragraphs, int orientation = 1,
        Func<StringBuilder, string>? extraWithText = null)
    {
        var text = new StringBuilder();
        var elements = new StringBuilder();
        foreach (var paragraph in paragraphs)
            elements.Append(Paragraph(text, paragraph));
        if (extraWithText is not null)
            elements.Append(extraWithText(text));
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8" ?>

            <template format_id="1.8" >
            <content><![CDATA[{text}]]></content><properties><pageFormat mediaSizeName="1" leftMargin="42.525" rightMargin="42.525" topMargin="42.525" bottomMargin="42.525" paperOrientation="{orientation}" headerFOffset="20.0" footerFOffset="20.0" /></properties>
            <elements resolver="hvl-default" >
            {elements}
            </elements>
            <styles><style name="default" description="Geçerli" family="Dialog" size="12" bold="false" italic="false" foreground="-13421773" /><style name="hvl-default" family="Times New Roman" size="12" description="Gövde" /></styles>
            </template>
            """;
        var path = Path.Combine(folder, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("content.xml").Open(), new UTF8Encoding(false));
        writer.Write(xml);
        return path;
    }

    /// <summary>A paragraph's XML, its text appended to <paramref name="text"/>.</summary>
    private static string Paragraph(StringBuilder text, Para paragraph)
    {
        var xml = new StringBuilder($"<paragraph {paragraph.Attributes}>");
        for (var i = 0; i < paragraph.Runs.Length; i++)
        {
            var (run, attributes) = paragraph.Runs[i];
            if (i == paragraph.Runs.Length - 1)
                run += "\n";
            xml.Append(CultureInfo.InvariantCulture, $"<content {attributes} startOffset=\"{text.Length}\" length=\"{run.Length}\" />");
            text.Append(run);
        }
        return xml.Append("</paragraph>").ToString();
    }

    /// <summary>A petition laid out as UYAP lays one out: a centred bold title, a label with a tab, justified hanging items.</summary>
    private static string MakePetition(string folder) => MakeUdf(folder, "petition.udf",
    [
        new("Alignment=\"1\"", ("Sample Court Office", "bold=\"true\" family=\"Calibri\" size=\"14\"")),
        new("Alignment=\"3\"", ("FILE NO", "bold=\"true\" family=\"Calibri\""), ("\t: 2026/1", "family=\"Calibri\"")),
        new("Alignment=\"3\" LeftIndent=\"47.0\" FirstLineIndent=\"-22.0\" TabSet=\"0.0:0:0\"",
            ("1.\tThe first item of the request, written long enough to run over more than one line of the page so the hanging indent shows.", "family=\"Calibri\"")),
        new("Alignment=\"2\" SpaceAbove=\"6\"", ("Signed ", "family=\"Calibri\" italic=\"true\""), ("Applicant", "family=\"Calibri\" underline=\"true\" foreground=\"-65536\"")),
        new("Alignment=\"0\"", ("Default style text", "")),
    ]);

    // ───────────────────────── Reading ─────────────────────────

    [Fact]
    public void A_petition_reads_with_its_formats_page_and_tabs()
    {
        var folder = fx.NewFolder();
        var document = UyapReader.Read(MakePetition(folder), Path.Combine(folder, "media"));

        var section = Assert.Single(document.Sections);
        // A4, 42.525 pt margins; the header distance (20 pt) is the top of the header area, the body starts at the margin.
        Assert.Equal(new HPage(59528, 84189, 4252, 4252, 2000, 2000, 2252, 2252, 0), section.Page);
        Assert.Equal(7200, document.DefaultTabStop);
        var paragraphs = section.Blocks.Cast<HParagraph>().ToList();
        Assert.Equal(5, paragraphs.Count);

        var title = paragraphs[0];
        Assert.Equal(HAlign.Center, title.Format.Align);
        var titleText = Assert.IsType<HText>(Assert.Single(title.Inlines));
        Assert.Equal("Sample Court Office", titleText.Text); // the "\n" ending the paragraph is gone
        Assert.True(titleText.Format.Bold);
        Assert.Equal(1400, titleText.Format.Size);
        Assert.Equal("Calibri", titleText.Format.Font);
        Assert.False(titleText.Format.Layout?.Kerning); // Java draws without kerning
        // Java's line height of 14 pt Calibri: ascent 10.5 → 11, descent and leading 6.59 → 7 (pt, ×100).
        Assert.Equal(new HLineSpacing(HLineSpacingKind.AtLeast, 1800), title.Format.LineSpacing);

        var label = paragraphs[1];
        Assert.Equal(HAlign.Justify, label.Format.Align);
        Assert.Collection(label.Inlines,
            i => Assert.Equal("FILE NO", Assert.IsType<HText>(i).Text),
            i => Assert.IsType<HTab>(i),
            i => Assert.Equal(": 2026/1", Assert.IsType<HText>(i).Text));
        // 12 pt Calibri: 15 pt lines, as the editor prints them.
        Assert.Equal(new HLineSpacing(HLineSpacingKind.AtLeast, 1500), label.Format.LineSpacing);

        var item = paragraphs[2];
        Assert.Equal(4700, item.Format.Left);
        Assert.Equal(-2200, item.Format.FirstLine);
        Assert.Null(item.Format.Tabs); // the stop at 0 is the hanging indent's own

        var signature = paragraphs[3];
        Assert.Equal(HAlign.Right, signature.Format.Align);
        Assert.Equal(600, signature.Format.Before);
        var signed = signature.Inlines.OfType<HText>().ToList();
        Assert.True(signed[0].Format.Italic);
        Assert.True(signed[1].Format.Underline);
        Assert.Equal("#FF0000", signed[1].Format.Color);

        // A run without attributes takes the elements' style (hvl-default), not the editor's grey Dialog "default".
        var plain = Assert.IsType<HText>(Assert.Single(paragraphs[4].Inlines)).Format;
        Assert.Equal("Times New Roman", plain.Font);
        Assert.Equal(1200, plain.Size);
        Assert.Equal("#000000", plain.Color);
        Assert.False(plain.Bold);
    }

    [Fact]
    public void Tables_pictures_page_breaks_and_landscape_pages_are_read()
    {
        var folder = fx.NewFolder();
        var png = Convert.ToBase64String(File.ReadAllBytes(EngineFixture.MakeImage(folder, "logo.png", MagickFormat.Png, 60, 60)));
        var path = MakeUdf(folder, "table.udf", [new("", ("Before the table", ""))], orientation: 0,
            extraWithText: text =>
            {
                string Cell(string value) => "<cell>" + Paragraph(text, new Para("", (value, ""))) + "</cell>";
                var table = $"""<table tableName="Sabit" columnCount="2" columnSpans="100,300" border="borderCell"><row rowName="row1" rowType="dataRow">{Cell("A1")}{Cell("B1")}</row><row rowName="row2" rowType="dataRow">{Cell("A2")}{Cell("B2")}</row></table>""";
                var picture = $"""<paragraph Alignment="1"><image imageData="{png}" width="40" height="20" startOffset="{text.Length}" length="1" /><content startOffset="{text.Length + 1}" length="1" /></paragraph>""";
                text.Append(" \n");
                return table + "<page-break />" + picture + Paragraph(text, new Para("", ("After the break", "")));
            });

        var document = UyapReader.Read(path, Path.Combine(folder, "media"));

        var section = Assert.Single(document.Sections);
        Assert.True(section.Page!.Landscape);
        var table = Assert.IsType<HTable>(section.Blocks[1]);
        Assert.Equal(2, table.ColumnCount);
        Assert.Equal([0.25, 0.75], table.RelativeWidths);
        Assert.Equal(HBorders.Grid, table.Borders);
        Assert.Equal(["A1", "B1", "A2", "B2"], table.Rows.SelectMany(r => r.Cells)
            .Select(c => Assert.IsType<HText>(Assert.Single(Assert.IsType<HParagraph>(Assert.Single(c.Blocks)).Inlines)).Text));
        var picture = Assert.IsType<HParagraph>(section.Blocks[2]);
        Assert.True(picture.PageBreakBefore);
        var image = Assert.IsType<HImage>(Assert.Single(picture.Inlines));
        Assert.Equal((4000, 2000), (image.Width, image.Height));
        Assert.True(File.Exists(image.Path));
        Assert.False(Assert.IsType<HParagraph>(section.Blocks[3]).PageBreakBefore);
    }

    [Fact]
    public void Runs_pointing_outside_the_text_and_a_file_without_elements_still_read()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "odd.udf");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("content.xml").Open()))
        {
            writer.Write("""
                <template format_id="1.7"><content><![CDATA[Line one
                Line two]]></content><elements resolver="hvl-default"><paragraph><content startOffset="-5" length="999999" /></paragraph></elements></template>
                """);
        }
        var paragraph = Assert.IsType<HParagraph>(Assert.Single(Assert.Single(UyapReader.Read(path, folder).Sections).Blocks));
        Assert.Collection(paragraph.Inlines,
            i => Assert.Equal("Line one", Assert.IsType<HText>(i).Text),
            i => Assert.IsType<HLineBreak>(i),
            i => Assert.Equal("Line two", Assert.IsType<HText>(i).Text));

        var bare = Path.Combine(folder, "bare.udf");
        using (var zip = ZipFile.Open(bare, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("content.xml").Open()))
            writer.Write("<template><content><![CDATA[First\nSecond]]></content></template>");
        var lines = Assert.Single(UyapReader.Read(bare, folder).Sections).Blocks.Cast<HParagraph>()
            .Select(p => string.Concat(p.Inlines.OfType<HText>().Select(t => t.Text))).ToList();
        Assert.Equal(["First", "Second"], lines);
    }

    [Fact]
    public void A_content_part_with_a_dtd_is_refused()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "dtd.udf");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("content.xml").Open()))
            writer.Write("""<?xml version="1.0"?><!DOCTYPE template [<!ENTITY a "aaaaaaaaaa">]><template><content>&a;</content></template>""");

        Assert.Throws<InvalidDataException>(() => UyapReader.Read(path, folder));
        Assert.False(UyapReader.IsUyap(path));
    }

    // ───────────────────────── Telling the two .udf apart ─────────────────────────

    [Fact]
    public void Only_a_zip_with_a_template_content_part_is_a_uyap_document()
    {
        var folder = fx.NewFolder();
        Assert.True(UyapReader.IsUyap(MakePetition(folder)));
        Assert.False(UyapReader.IsUyap(MakeDisc(folder)));

        var other = Path.Combine(folder, "other.udf");
        using (var zip = ZipFile.Open(other, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("content.xml").Open()))
            writer.Write("<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"/>");
        Assert.False(UyapReader.IsUyap(other));
        var empty = Path.Combine(folder, "empty.udf");
        File.WriteAllBytes(empty, []);
        Assert.False(UyapReader.IsUyap(empty));
    }

    /// <summary>A UDF 2.01 disc image named .udf, holding readme.txt.</summary>
    private static string MakeDisc(string folder)
    {
        var builder = new UdfBuilder { VolumeIdentifier = "DISC" };
        builder.AddFile("readme.txt", Encoding.UTF8.GetBytes("on the disc"));
        var path = Path.Combine(folder, "disc.udf");
        builder.Build(path);
        return path;
    }

    [Fact]
    public void Udf_is_a_read_only_document_and_extract_takes_it_too()
    {
        var format = FormatRegistry.Detect("petition.udf");
        Assert.Equal(UyapReader.Format, format?.Id);
        Assert.Equal(FormatCategory.Document, format!.Category);
        Assert.False(format.Writable);
        Assert.Contains(UyapReader.Format, DocumentReaders.BuiltInFormats);

        var planner = fx.Catalog.CreatePlanner();
        Assert.Equal(["docx-writer"], planner.Plan("udf", "docx")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal(["archive"], planner.Plan("udf", FormatRegistry.Folder)!.Steps.Select(s => s.Converter.Id));
        // Only Extract: a UYAP document is no archive to repack.
        Assert.Null(planner.Plan("udf", "7z"));
    }

    [Fact]
    public async Task A_udf_disc_image_unpacks_with_extract_and_is_refused_as_a_document()
    {
        var folder = fx.NewFolder();
        var disc = MakeDisc(folder);

        var extracted = await fx.ConvertAsync([disc], new Preset { TargetFormat = FormatRegistry.Folder });
        Assert.True(extracted.State == JobState.Completed, string.Join("; ", extracted.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var output = Assert.Single(extracted.Outputs);
        Assert.Equal("on the disc", await File.ReadAllTextAsync(Path.Combine(output, "readme.txt"), TestContext.Current.CancellationToken));

        var refused = await fx.ConvertAsync([disc], new Preset { TargetFormat = "docx" });
        Assert.Equal(UyapReader.NotUyapKey, Assert.Single(refused.Files).ErrorKey);
    }

    [Fact]
    public async Task A_uyap_document_unpacks_with_extract_to_its_content_part()
    {
        var folder = fx.NewFolder();
        var job = await fx.ConvertAsync([MakePetition(folder)], new Preset { TargetFormat = FormatRegistry.Folder });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        Assert.True(File.Exists(Path.Combine(Assert.Single(job.Outputs), "content.xml")));
    }

    // ───────────────────────── Converting ─────────────────────────

    [Fact]
    public async Task A_petition_becomes_a_pdf_with_the_filee_renderer()
    {
        EngineEnvironment.FileeRenderer = true;
        fx.Catalog.Refresh();
        var folder = fx.NewFolder();
        Assert.Equal(["layout"], fx.Catalog.CreatePlanner().Plan("udf", "pdf")!.Steps.Select(s => s.Converter.Id));

        var job = await fx.ConvertAsync([MakePetition(folder)], new Preset { TargetFormat = "pdf" });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        using var pdf = PigDocument.Open(job.Outputs.Single());
        Assert.Equal(1, pdf.NumberOfPages);
        var page = pdf.GetPage(1);
        Assert.Equal(595, page.Width, 0);
        var words = page.GetWords().ToList();
        Assert.Contains(words, w => w.Text == "Sample");
        Assert.Contains(words, w => w.Text == "Applicant");
        // The title is centred: its middle on the middle of the text area.
        var title = words.Where(w => w.Text is "Sample" or "Court" or "Office").ToList();
        var middle = (title.Min(w => w.BoundingBox.Left) + title.Max(w => w.BoundingBox.Right)) / 2;
        Assert.InRange(middle, 595.28 / 2 - 6, 595.28 / 2 + 6);
        // The signature is right-aligned at the right margin.
        Assert.InRange(words.Single(w => w.Text == "Applicant").BoundingBox.Right, 595.28 - 42.5 - 4, 595.28 - 42.5 + 1);
    }

    [Fact]
    public async Task A_petition_becomes_a_docx_with_its_formatting()
    {
        var folder = fx.NewFolder();
        var job = await fx.ConvertAsync([MakePetition(folder)], new Preset { TargetFormat = "docx" });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        using var docx = WordprocessingDocument.Open(job.Outputs.Single(), false);
        var paragraphs = docx.MainDocumentPart!.Document!.Body!.Elements<W.Paragraph>().ToList();
        var title = paragraphs.First(p => p.InnerText == "Sample Court Office");
        Assert.Equal(W.JustificationValues.Center, title.ParagraphProperties?.Justification?.Val?.Value);
        Assert.NotNull(title.Descendants<W.Bold>().FirstOrDefault());
        Assert.Contains(paragraphs, p => p.InnerText.StartsWith("1.", StringComparison.Ordinal) && p.Descendants<W.TabChar>().Any());
        var page = docx.MainDocumentPart.Document.Body.Descendants<W.PageSize>().First();
        Assert.Equal(11906u, page.Width!.Value, 2u);
    }
}
