// Word's watermarks and WordArt in the layout engine (issue #1313): DOCX built in code, read by DocxReader and composed
// as the renderer composes Word documents. A text watermark is a VML WordArt shape in the header (v:textpath on a
// shape of type _x0000_t136, rotated 315°, half see-through, centred on the margins, behind the text); DrawingML
// WordArt is a text box with a text warp; a picture watermark is a washed-out picture (gain / blacklevel). Laid out
// with the Noto fonts of build/fetch-fonts.ps1 (RepositoryFonts); without them the layout tests skip.

using System.IO.Compression;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Rendering;
using Filee.Engines.Tests.RenderQuality;
using Filee.Engines.Typesetting;
using ImageMagick;
using SkiaSharp;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public sealed class WordWatermarkTests : IDisposable
{
    /// <summary>A4 in points, and the middle of its text area (1 inch margins all round).</summary>
    private const float PaperWidth = 11906 / 20f, PaperHeight = 16838 / 20f;

    private static readonly SKPoint MarginCentre = new(PaperWidth / 2, PaperHeight / 2);

    private const string Office = "xmlns:o=\"urn:schemas-microsoft-com:office:office\"";
    private const string Word2010 = "xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\"";

    /// <summary>Word's WordArt shape type: the text path is on and fits the shape here, not on the shape.</summary>
    private const string TextShapeType =
        "<v:shapetype id=\"_x0000_t136\" coordsize=\"21600,21600\" o:spt=\"136\" adj=\"10800\" path=\"m@7,l@8,m@5,21600l@6,21600e\">" +
        "<v:path textpathok=\"t\" o:connecttype=\"custom\"/><v:textpath on=\"t\" fitshape=\"t\"/><o:lock v:ext=\"edit\" text=\"t\" shapetype=\"t\"/></v:shapetype>";

    /// <summary>The header Word writes for Design → Watermark → "CONFIDENTIAL", semitransparent, diagonal.</summary>
    private const string ConfidentialHeader =
        "<w:sdt><w:sdtPr><w:id w:val=\"-1\"/><w:docPartObj><w:docPartGallery w:val=\"Watermarks\"/><w:docPartUnique/></w:docPartObj></w:sdtPr><w:sdtContent>" +
        $"<w:p><w:r><w:rPr><w:noProof/></w:rPr><w:pict {Office}>{TextShapeType}" +
        "<v:shape id=\"PowerPlusWaterMarkObject1\" o:spid=\"_x0000_s2049\" type=\"#_x0000_t136\" style=\"position:absolute;margin-left:0;margin-top:0;" +
        "width:527.85pt;height:131.95pt;rotation:315;z-index:-251657216;mso-position-horizontal:center;mso-position-horizontal-relative:margin;" +
        "mso-position-vertical:center;mso-position-vertical-relative:margin\" o:allowincell=\"f\" fillcolor=\"silver\" stroked=\"f\">" +
        "<v:fill opacity=\".5\"/><v:textpath style=\"font-family:&quot;Calibri&quot;;font-size:1pt\" string=\"CONFIDENTIAL\"/></v:shape>" +
        "</w:pict></w:r></w:p></w:sdtContent></w:sdt>";

    private readonly RepositoryFonts _fonts = RepositoryFonts.Load();
    private readonly Shaper _shaper = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-word-watermarks").FullName;

    public void Dispose()
    {
        _shaper.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ───────────────────────── Reading ─────────────────────────

    [Fact]
    public void A_vml_watermark_is_wordart_turned_half_see_through_and_centred_behind_the_text()
    {
        var box = Assert.Single(HeaderObjects(Read(WithHeader(ConfidentialHeader))).OfType<HTextBox>());

        Assert.Equal(new HTextArt(Fit: true, Opacity: 0.5), box.TextArt);
        Assert.Equal(315, box.Transform!.Rotation);
        Assert.Equal(-251657216, box.ZOrder);
        Assert.Equal(("PAGE", "CENTER", 0, "PAGE", "CENTER", 0, "BEHIND_TEXT"),
            (box.Anchor!.HorizontalRelativeTo, box.Anchor.HorizontalAlign, box.Anchor.HorizontalOffset,
             box.Anchor.VerticalRelativeTo, box.Anchor.VerticalAlign, box.Anchor.VerticalOffset, box.Anchor.Wrap));
        Assert.Equal((52785, 13195), (box.Width, box.Height));
        var text = Assert.IsType<HText>(Assert.Single(Assert.IsType<HParagraph>(Assert.Single(box.Blocks)).Inlines));
        // "silver", in the font of the text path; the size fills the box for the writers (fitshape comes from the type).
        Assert.Equal(("CONFIDENTIAL", "#C0C0C0", "Calibri"), (text.Text, text.Format.Color, text.Format.Font));
        Assert.True(text.Format.Size > 5000, $"size {text.Format.Size}");
    }

    [Fact]
    public void A_picture_watermark_keeps_its_washout()
    {
        var vml = Read(WithHeader(PictureHeader("gain=\"19661f\" blacklevel=\"22938f\"")), picture: true);
        var picture = Assert.Single(HeaderObjects(vml).OfType<HImage>());
        Assert.Equal(0.3, picture.Effects!.Contrast, 3);
        Assert.Equal(0.35, picture.Effects.Brightness, 3);
        Assert.Equal("BEHIND_TEXT", picture.Anchor!.Wrap);

        // DrawingML's washout (brightness +70 %, contrast -70 %) is the same change.
        var drawing = Anchor(PictureUri, Picture("rIdMark", 2540000, 1270000).Replace("<a:blip r:embed=\"rIdMark\"/>",
                "<a:blip r:embed=\"rIdMark\"><a:lum bright=\"70000\" contrast=\"-70000\"/><a:alphaModFix amt=\"60000\"/></a:blip>", StringComparison.Ordinal),
            2540000, 1270000, "<wp:positionH relativeFrom=\"margin\"><wp:align>center</wp:align></wp:positionH>",
            "<wp:positionV relativeFrom=\"margin\"><wp:align>center</wp:align></wp:positionV>", "<wp:wrapNone/>");
        var effects = Assert.Single(HeaderObjects(Read(WithHeader(P(drawing)), picture: true)).OfType<HImage>()).Effects!;
        Assert.Equal((0.3, 0.35, 0.6), (Math.Round(effects.Contrast, 3), Math.Round(effects.Brightness, 3), Math.Round(effects.Opacity, 3)));
    }

    // ───────────────────────── Drawing ─────────────────────────

    [Fact]
    public void The_vml_watermark_is_drawn_turned_see_through_fitted_and_centred_under_the_body()
    {
        var page = Compose(WithHeader(ConfidentialHeader))[0];

        var items = page.Items.ToList();
        var mark = items.FindIndex(i => i is GlyphItem { Run.Text: var text } && text.Contains("CONFIDENTIAL", StringComparison.Ordinal));
        var body = items.FindIndex(i => i is GlyphItem { Run.Text: var text } && text.Contains("Body", StringComparison.Ordinal));
        Assert.True(mark >= 0 && body >= 0, "the watermark and the body are drawn");
        Assert.True(mark < body, "the watermark lies under the body text");

        var run = ((GlyphItem)items[mark]).Run;
        Assert.Equal(new SKColor(0xC0, 0xC0, 0xC0), run.Color.WithAlpha(255));
        Assert.InRange(run.Color.Alpha, 126, 129);
        AssertFitted(run, rotation: -45, width: 527.85f, height: 131.95f, MarginCentre);
    }

    [Fact]
    public void Drawingml_wordart_is_stretched_over_its_box_turned_and_see_through()
    {
        const long Cx = 5486400, Cy = 1371600; // 432 × 108 pt
        var shape =
            $"<wps:wsp {Word2010}><wps:cNvSpPr txBox=\"1\"/><wps:spPr><a:xfrm rot=\"18900000\"><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Cx}\" cy=\"{Cy}\"/></a:xfrm>" +
            "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></wps:spPr><wps:txbx><w:txbxContent><w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr>" +
            "<w:r><w:rPr><w:color w:val=\"FF0000\"/><w:sz w:val=\"72\"/><w14:textFill><w14:solidFill><w14:srgbClr w14:val=\"FF0000\"><w14:alpha w14:val=\"50000\"/>" +
            "</w14:srgbClr></w14:solidFill></w14:textFill></w:rPr><w:t>DRAFT</w:t></w:r></w:p></w:txbxContent></wps:txbx>" +
            "<wps:bodyPr rot=\"0\" wrap=\"none\" lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\" fromWordArt=\"1\"><a:prstTxWarp prst=\"textPlain\"><a:avLst/></a:prstTxWarp>" +
            "<a:noAutofit/></wps:bodyPr></wps:wsp>";
        var anchor = Anchor(ShapeUri, shape, Cx, Cy, "<wp:positionH relativeFrom=\"margin\"><wp:align>center</wp:align></wp:positionH>",
            "<wp:positionV relativeFrom=\"margin\"><wp:align>center</wp:align></wp:positionV>", "<wp:wrapNone/>").Replace("behindDoc=\"0\"", "behindDoc=\"1\"", StringComparison.Ordinal);
        var docx = new DocxBuilder().Paragraph(P(R("Body")) + P(anchor));

        var box = Assert.Single(Read(docx).Sections[0].Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HTextBox>());
        Assert.Equal(new HTextArt(Fit: true, Opacity: 0.5), box.TextArt);

        var run = Assert.Single(Runs(Compose(docx)[0]), r => r.Text.Contains("DRAFT", StringComparison.Ordinal));
        Assert.Equal(new SKColor(0xFF, 0, 0), run.Color.WithAlpha(255));
        Assert.InRange(run.Color.Alpha, 126, 129);
        AssertFitted(run, rotation: -45, width: 432, height: 108, MarginCentre);
    }

    [Fact]
    public void Text_in_a_turned_text_box_turns_with_it_at_its_own_size()
    {
        const long Cx = 2540000, Cy = 635000; // 200 × 50 pt
        var shape =
            $"<wps:wsp><wps:cNvSpPr txBox=\"1\"/><wps:spPr><a:xfrm rot=\"5400000\"><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Cx}\" cy=\"{Cy}\"/></a:xfrm>" +
            "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></wps:spPr><wps:txbx><w:txbxContent><w:p><w:r><w:t>Sideways</w:t></w:r></w:p></w:txbxContent></wps:txbx>" +
            "<wps:bodyPr/></wps:wsp>";
        var docx = new DocxBuilder().Paragraph(P(R("Body")) + P(Anchor(ShapeUri, shape, Cx, Cy,
            "<wp:positionH relativeFrom=\"margin\"><wp:align>center</wp:align></wp:positionH>",
            "<wp:positionV relativeFrom=\"margin\"><wp:align>center</wp:align></wp:positionV>", "<wp:wrapNone/>")));

        var run = Assert.Single(Runs(Compose(docx)[0]), r => r.Text.Contains("Sideways", StringComparison.Ordinal));

        var direction = run.Transform!.Value.MapVector(1, 0);
        Assert.Equal(90, Math.Atan2(direction.Y, direction.X) * 180 / Math.PI, 1);
        Assert.Equal(1, direction.Length, 3);
        Assert.Equal(10, run.Size, 1); // DocxBuilder's 10 pt
    }

    [Fact]
    public void The_picture_watermark_is_drawn_washed_out()
    {
        var page = Compose(WithHeader(PictureHeader("gain=\"19661f\" blacklevel=\"22938f\"")), picture: true)[0];
        using var bitmap = page.Render(72);

        // Black washed out: (0 - 0.5) × 0.3 + 0.5 + 0.35 = 0.7.
        var middle = bitmap.GetPixel((int)MarginCentre.X, (int)MarginCentre.Y);
        Assert.InRange(middle.Red, 168, 190);
        Assert.Equal((middle.Red, middle.Red), (middle.Green, middle.Blue));
        // The body text is drawn over it.
        var items = page.Items.ToList();
        Assert.True(items.FindIndex(i => i is DrawingItem) < items.FindIndex(i => i is GlyphItem { Run.Text: var text } && text.Contains("Body", StringComparison.Ordinal)));
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// The run's text, from its first glyph's left to its last glyph's advance and over its font's ascent and descent,
    /// lies along <paramref name="rotation"/> degrees (clockwise from the right is positive) and spans about
    /// <paramref name="width"/>, centred on <paramref name="centre"/>; it is stretched no more than its box is high.
    /// </summary>
    private static void AssertFitted(GlyphRun run, double rotation, float width, float height, SKPoint centre)
    {
        var transform = Assert.NotNull(run.Transform);
        var direction = transform.MapVector(1, 0);
        Assert.Equal(rotation, Math.Atan2(direction.Y, direction.X) * 180 / Math.PI, 1);

        using var font = new SKFont(run.Font.Typeface, run.Size) { ScaleX = run.ScaleX, LinearMetrics = true, Subpixel = true };
        var widths = font.GetGlyphWidths(run.Glyphs);
        var left = run.Positions.Min(p => p.X);
        var right = run.Positions.Select((p, i) => p.X + widths[i]).Max();
        var baseline = run.Positions[0].Y;
        var start = transform.MapPoint(left, baseline);
        var end = transform.MapPoint(right, baseline);
        Assert.Equal(width, SKPoint.Distance(start, end), width * 0.01f);

        // Along the text, its middle is the box's; across it, the line's middle is less than half the box away.
        var middle = transform.MapPoint((left + right) / 2, baseline + (font.Metrics.Ascent + font.Metrics.Descent) / 2);
        var unit = new SKPoint(direction.X / direction.Length, direction.Y / direction.Length);
        var offset = middle - centre;
        Assert.Equal(0, offset.X * unit.X + offset.Y * unit.Y, 1.5);
        Assert.InRange(Math.Abs(offset.X * -unit.Y + offset.Y * unit.X), 0, height / 4);
    }

    private static IEnumerable<GlyphRun> Runs(PageContent page) => page.Items.OfType<GlyphItem>().Select(i => i.Run);

    private static IEnumerable<HInline> HeaderObjects(HDocument document) =>
        document.Sections[0].Headers.SelectMany(h => h.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines);

    /// <summary>A Word picture watermark: a VML picture centred on the margins behind the text, with these v:imagedata settings.</summary>
    private static string PictureHeader(string adjustments) =>
        $"<w:p><w:r><w:pict {Office}><v:shape id=\"WordPictureWatermark1\" o:spid=\"_x0000_s2050\" type=\"#_x0000_t75\" " +
        "style=\"position:absolute;margin-left:0;margin-top:0;width:200pt;height:100pt;z-index:-251656192;mso-position-horizontal:center;" +
        "mso-position-horizontal-relative:margin;mso-position-vertical:center;mso-position-vertical-relative:margin\" o:allowincell=\"f\">" +
        $"<v:imagedata r:id=\"rIdMark\" o:title=\"mark\" {adjustments}/></v:shape></w:pict></w:r></w:p>";

    private static DocxBuilder WithHeader(string header)
    {
        var docx = new DocxBuilder().Paragraph(P(R("Body")));
        docx.SectionProperties = $"<w:headerReference w:type=\"default\" r:id=\"{docx.Header(header)}\"/>{DefaultPage}";
        return docx;
    }

    /// <summary>Saves the document, with a black picture related to its header as rIdMark when <paramref name="picture"/>.</summary>
    private string Save(DocxBuilder docx, bool picture)
    {
        var path = docx.Save(Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".docx"));
        if (picture)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
            using (var writer = new StreamWriter(zip.CreateEntry("word/_rels/header1.xml.rels").Open()))
            {
                writer.Write("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdMark\" " +
                             "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/mark.png\"/></Relationships>");
            }
            using var image = new MagickImage(MagickColors.Black, 40, 20) { Format = MagickFormat.Png };
            using var stream = zip.CreateEntry("word/media/mark.png").Open();
            stream.Write(image.ToByteArray());
        }
        return path;
    }

    private HDocument Read(DocxBuilder docx, bool picture = false) => DocxReader.Read(Save(docx, picture), Path.Combine(_folder, "media"));

    private List<PageContent> Compose(DocxBuilder docx, bool picture = false)
    {
        Assert.SkipUnless(_fonts.Has(RepositoryFonts.Latin) && _fonts.Has(RepositoryFonts.Korean),
            "Noto Sans and Noto Sans KR are not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        return new PageComposer(new FontResolver(_fonts), _shaper, styles: StyleResolver.FromTemplate())
            .Compose(Read(docx, picture), TestContext.Current.CancellationToken).Pages;
    }
}
