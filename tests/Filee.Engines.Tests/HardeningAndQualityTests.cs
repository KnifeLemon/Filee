// The LibreOffice profile lock-down, the picture size limit, and the quality fixes: PSD layers with transparency
// (#468), Markdown code blocks and quotes (#406), Word's first-page header and tall headers (#416, #506), PDF page
// backgrounds behind text (#433) and merged pages that came out blank (#377).

using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Magick;
using Filee.Engines.Office;
using Filee.Engines.Pdf;
using ImageMagick;
using ImageMagick.Drawing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

namespace Filee.Engines.Tests;

public class HardeningAndQualityTests
{
    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filee-hardening", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    // ───────────────────────── LibreOffice profile ─────────────────────────

    [Fact]
    public void A_new_profile_gets_every_setting()
    {
        var profile = Folder();

        LibreOfficeSecurity.Apply(profile);

        var document = XDocument.Load(LibreOfficeSecurity.SettingsFile(profile));
        Assert.True(LibreOfficeSecurity.Carries(document));
        Assert.Equal(LibreOfficeSecurity.Settings.Length, document.Root!.Elements("item").Count());
    }

    [Fact]
    public void An_existing_profile_keeps_its_own_items_and_gets_ours_back()
    {
        var profile = Folder();
        var file = LibreOfficeSecurity.SettingsFile(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """
            <?xml version="1.0" encoding="UTF-8"?>
            <oor:items xmlns:oor="http://openoffice.org/2001/registry" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
            <item oor:path="/org.openoffice.Office.Calc/Formula/Calculation"><prop oor:name="OpenCLMinimumDataSize" oor:op="fuse"><value>100</value></prop></item>
            <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>0</value></prop></item>
            </oor:items>
            """);

        LibreOfficeSecurity.Apply(profile);

        var items = XDocument.Load(file).Root!.Elements("item").ToList();
        Assert.Contains(items, i => i.ToString().Contains("OpenCLMinimumDataSize"));
        var level = Assert.Single(items, i => i.ToString().Contains("MacroSecurityLevel"));
        Assert.Equal("3", level.Element("prop")!.Element("value")!.Value);
    }

    // ───────────────────────── Picture limits ─────────────────────────

    [Fact]
    public void A_picture_over_the_limit_is_refused_from_its_header()
    {
        var folder = Folder();
        var path = Path.Combine(folder, "wide.png");
        using (var image = new MagickImage(MagickColors.White, 2000, 1500))
            image.Write(path, MagickFormat.Png);

        var refused = Assert.Throws<ConversionRefusedException>(() => MagickLimits.Check(path, "png", null, maxMegapixels: 2));
        Assert.Equal("error.image_too_large", refused.ErrorKey);
        MagickLimits.Check(path, "png", null, maxMegapixels: 3); // 3 MP is enough
        MagickLimits.Check(path, "png", null, maxMegapixels: 0); // no limit
        MagickLimits.Check(path, "svg", null, maxMegapixels: 1); // drawn at the preset's DPI: not checked
    }

    // ───────────────────────── #468 PSD ─────────────────────────

    [Fact]
    public void A_psd_whose_composite_lacks_its_layers_transparency_is_composed_from_the_layers()
    {
        var folder = Folder();
        var path = Path.Combine(folder, "layers.psd");
        using (var layers = new MagickImageCollection())
        {
            // ImageMagick writes the first image as the composite: opaque white, as a PSD saved without transparency.
            layers.Add(new MagickImage(MagickColors.White, 40, 40));
            var layer = new MagickImage(MagickColors.Transparent, 40, 40);
            new Drawables().FillColor(MagickColors.Red).Rectangle(0, 0, 19, 39).Draw(layer);
            layers.Add(layer);
            layers.Write(path, MagickFormat.Psd);
        }

        using var read = ImageReader.Read(path, "psd", new Preset());

        var picture = Assert.Single(read);
        Assert.True(picture.HasAlpha);
        using var pixels = picture.GetPixels();
        Assert.Equal(0, pixels.GetPixel(30, 20).ToColor()!.A); // right half: transparent in the layer
        Assert.Equal(255, pixels.GetPixel(5, 20).ToColor()!.A); // left half: red
    }

    // ───────────────────────── #406 Markdown ─────────────────────────

    [Fact]
    public void Markdown_code_blocks_and_quotes_become_boxes_and_line_up_with_their_list_item()
    {
        const string markdown = """
            1. Install:

               ```
               npm install filee
               cd project
               ```

            > Quoted text
            """;

        var blocks = MarkdownReader.Read(markdown, Folder()).Sections.Single().Blocks;

        var tables = blocks.OfType<HTable>().ToList();
        Assert.Equal(2, tables.Count);
        var code = tables[0].Rows.Single().Cells.Single();
        Assert.NotNull(code.Fill);
        var lines = code.Blocks.OfType<HParagraph>().ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(0, l.Format.Before));
        Assert.Equal("Consolas", lines[0].Inlines.OfType<HText>().Single().Format.Font);
        Assert.True(tables[0].Indent > 0); // inside the list item

        var quote = tables[1].Rows.Single().Cells.Single();
        Assert.NotEqual(HBorderStyle.None, quote.Borders!.Value.Left.Style);
        Assert.Equal(HBorderStyle.None, quote.Borders!.Value.Right.Style);
        Assert.Equal(0, tables[1].Indent);
    }

    // ───────────────────────── #416 / #506 Word headers ─────────────────────────

    [Fact]
    public void A_first_page_header_of_its_own_is_shown_on_the_first_page_and_a_tall_header_gets_room()
    {
        var folder = Folder();
        var path = Path.Combine(folder, "headers.docx");
        const string w = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        static string P(string text) => $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>";
        OpcWriter.Save(path, new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "<Override PartName=\"/word/header1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml\"/>" +
                "<Override PartName=\"/word/header2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml\"/></Types>",
            ["_rels/.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>",
            ["word/_rels/document.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rH1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\" Target=\"header1.xml\"/>" +
                "<Relationship Id=\"rH2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\" Target=\"header2.xml\"/></Relationships>",
            ["word/header1.xml"] = $"<w:hdr {w}>{P("Line one")}{P("Line two")}{P("Line three")}{P("Line four")}</w:hdr>",
            ["word/header2.xml"] = $"<w:hdr {w}>{P("Letterhead")}</w:hdr>",
            ["word/document.xml"] = $"<w:document {w}><w:body>{P("Body")}" +
                "<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rH1\"/><w:headerReference w:type=\"first\" r:id=\"rH2\"/><w:titlePg/>" +
                "<w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>",
        }, new Dictionary<string, byte[]>());

        var section = DocxReader.Read(path, Path.Combine(folder, "media")).Sections.Single();

        Assert.True(section.HideFirstHeader);
        var box = section.Blocks.OfType<HParagraph>().First().Inlines.OfType<HTextBox>().Single();
        Assert.Contains("Letterhead", string.Concat(box.Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HText>().Select(t => t.Text)));
        // Four lines don't fit the half inch Word's margins leave: the header area grows.
        Assert.True(section.Page!.Header > HwpxUnits.FromTwips(720), $"header area {section.Page.Header}");
    }

    // ───────────────────────── #433 PDF background ─────────────────────────

    [Fact]
    public void A_picture_covering_a_text_page_goes_behind_the_text()
    {
        var folder = Folder();
        var picture = Path.Combine(folder, "background.png");
        using (var image = new MagickImage(new MagickColor("#DDEEFF"), 600, 840))
            image.Write(picture, MagickFormat.Png);
        var builder = new PdfBuilder().NewPage();
        var font = builder.Font();
        var pdf = builder.Picture(picture, 0, 0, 595, 842)
            .Line("Visible text on the background", 14, font, 72, 120)
            .Save(Path.Combine(folder, "background.pdf"));

        var read = PdfDocumentReader.Read(pdf, Path.Combine(folder, "media"));

        var images = read.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HImage>().ToList();
        Assert.Contains(images, i => i.Anchor is { Wrap: "BEHIND_TEXT" });
    }

    // ───────────────────────── #377 merge check ─────────────────────────

    [Fact]
    public void A_merged_page_that_is_blank_while_its_source_is_not_is_found()
    {
        var folder = Folder();
        var source = Path.Combine(folder, "source.pdf");
        Pdf(source, "text on page one", "text on page two");
        var lost = Path.Combine(folder, "lost.pdf");
        Pdf(lost, "text on page one", null);
        var good = Path.Combine(folder, "good.pdf");
        Pdf(good, "text on page one", "text on page two");

        Assert.Equal(1, PdfMergeCheck.FindLostPage([source], lost, CancellationToken.None));
        Assert.Null(PdfMergeCheck.FindLostPage([source], good, CancellationToken.None));

        // A page with a black bar for every non-null entry, a blank page for null (no fonts needed).
        static void Pdf(string path, params string?[] pages)
        {
            using var document = new PdfDocument();
            foreach (var text in pages)
            {
                var page = document.AddPage();
                if (text is null)
                    continue;
                using var graphics = XGraphics.FromPdfPage(page);
                graphics.DrawRectangle(XBrushes.Black, 72, 72, 300, 120);
            }
            document.Save(path);
        }
    }

    [Fact]
    public void A_white_bitmap_is_blank_and_one_dark_pixel_is_not()
    {
        using var bitmap = new SKBitmap(20, 20);
        bitmap.Erase(SKColors.White);
        Assert.True(PdfMergeCheck.IsBlank(bitmap));
        bitmap.SetPixel(10, 10, SKColors.Black);
        Assert.False(PdfMergeCheck.IsBlank(bitmap));
    }
}
