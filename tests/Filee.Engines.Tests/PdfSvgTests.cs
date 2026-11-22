// PDF → SVG with Filee's own PDF interpreter (PdfSvgConverter / PdfSvgWriter). The PDFs are built in code: raw content
// streams through PDFsharp's objects (paths, strokes, clipping, shadings, soft masks, pictures, Helvetica text) and an
// embedded TrueType font through PdfPig's writer. The SVG is read as XML for what it holds, and drawn back with
// Filee's SVG renderer (Svg.Skia, SvgRenderer) to compare pixels with PDFium's rendering of the PDF (PdfiumConverter).
// Text is compared by its ink, not pixel by pixel: PDFium and Filee substitute other fonts for Helvetica.

using System.Text;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Pdf;
using Filee.Engines.Vector;
using ImageMagick;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using Sharp = PdfSharp.Pdf;

#pragma warning disable CA1416 // PDFtoImage: desktop platforms only, all covered

namespace Filee.Engines.Tests;

public class PdfSvgTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const double Width = 612, Height = 792;

    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    // ───────────────────────── Building PDFs ─────────────────────────

    /// <summary>A PDF of one page per content stream (US Letter), Helvetica as /F1; <paramref name="resources"/> adds more.</summary>
    private static string RawPdf(string path, string[] pages, Action<Sharp.PdfDocument, Sharp.PdfPage, int>? resources = null)
    {
        using var document = new Sharp.PdfDocument();
        for (var i = 0; i < pages.Length; i++)
        {
            var page = document.AddPage();
            page.Elements.SetRectangle("/MediaBox", new Sharp.PdfRectangle(new PdfSharp.Drawing.XPoint(0, 0), new PdfSharp.Drawing.XPoint(Width, Height)));
            var font = new Sharp.PdfDictionary(document);
            font.Elements.SetName("/Type", "/Font");
            font.Elements.SetName("/Subtype", "/Type1");
            font.Elements.SetName("/BaseFont", "/Helvetica");
            font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
            Resource(document, page, "/Font", "/F1", font);
            resources?.Invoke(document, page, i);
            page.Contents.AppendContent().CreateStream(Encoding.Latin1.GetBytes(pages[i]));
        }
        document.Save(path);
        return path;
    }

    private static string RawPdf(string path, string content, Action<Sharp.PdfDocument, Sharp.PdfPage>? resources = null) =>
        RawPdf(path, [content], resources is null ? null : (document, page, _) => resources(document, page));

    private static void Resource(Sharp.PdfDocument document, Sharp.PdfPage page, string kind, string name, Sharp.PdfDictionary value)
    {
        if (!value.IsIndirect)
            document.Internals.AddObject(value);
        if (page.Resources.Elements.GetDictionary(kind) is not { } group)
            page.Resources.Elements[kind] = group = new Sharp.PdfDictionary(document);
        group.Elements.SetReference(name, value);
    }

    private static Sharp.PdfArray Numbers(Sharp.PdfDocument document, params double[] values) =>
        new(document, [.. values.Select(v => (Sharp.PdfItem)new Sharp.PdfReal(v))]);

    /// <summary>An axial (type 2) or radial (type 3) shading from one RGB colour to another.</summary>
    private static Sharp.PdfDictionary Shading(Sharp.PdfDocument document, int type, double[] coords, double[] from, double[] to, bool extend = true)
    {
        var function = new Sharp.PdfDictionary(document);
        function.Elements.SetInteger("/FunctionType", 2);
        function.Elements["/Domain"] = Numbers(document, 0, 1);
        function.Elements["/C0"] = Numbers(document, from);
        function.Elements["/C1"] = Numbers(document, to);
        function.Elements.SetInteger("/N", 1);
        var shading = new Sharp.PdfDictionary(document);
        shading.Elements.SetInteger("/ShadingType", type);
        shading.Elements.SetName("/ColorSpace", "/DeviceRGB");
        shading.Elements["/Coords"] = Numbers(document, coords);
        shading.Elements["/Function"] = function;
        shading.Elements["/Extend"] = new Sharp.PdfArray(document, new Sharp.PdfBoolean(extend), new Sharp.PdfBoolean(extend));
        document.Internals.AddObject(shading);
        return shading;
    }

    private static Sharp.PdfDictionary Picture(Sharp.PdfDocument document, int width, int height, string space, byte[] data, string? filter = null)
    {
        var picture = new Sharp.PdfDictionary(document);
        picture.Elements.SetName("/Type", "/XObject");
        picture.Elements.SetName("/Subtype", "/Image");
        picture.Elements.SetInteger("/Width", width);
        picture.Elements.SetInteger("/Height", height);
        picture.Elements.SetInteger("/BitsPerComponent", 8);
        picture.Elements.SetName("/ColorSpace", space);
        if (filter is not null)
            picture.Elements.SetName("/Filter", filter);
        picture.CreateStream(data);
        document.Internals.AddObject(picture);
        return picture;
    }

    private static byte[] Rgb(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var data = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                (data[(y * width + x) * 3], data[(y * width + x) * 3 + 1], data[(y * width + x) * 3 + 2]) = pixel(x, y);
        return data;
    }

    private static Sharp.PdfDictionary State(Sharp.PdfDocument document, Action<Sharp.PdfDictionary> set)
    {
        var state = new Sharp.PdfDictionary(document);
        state.Elements.SetName("/Type", "/ExtGState");
        set(state);
        return state;
    }

    /// <summary>A luminosity soft mask whose group paints <paramref name="shading"/> ("sh") over the page.</summary>
    private static Sharp.PdfDictionary MaskState(Sharp.PdfDocument document, Sharp.PdfDictionary shading)
    {
        var group = new Sharp.PdfDictionary(document);
        group.Elements.SetName("/Type", "/XObject");
        group.Elements.SetName("/Subtype", "/Form");
        group.Elements["/BBox"] = Numbers(document, 0, 0, Width, Height);
        var transparency = new Sharp.PdfDictionary(document);
        transparency.Elements.SetName("/S", "/Transparency");
        transparency.Elements.SetName("/CS", "/DeviceRGB");
        group.Elements["/Group"] = transparency;
        var resources = new Sharp.PdfDictionary(document);
        var named = new Sharp.PdfDictionary(document);
        named.Elements.SetReference("/S0", shading);
        resources.Elements["/Shading"] = named;
        group.Elements["/Resources"] = resources;
        group.CreateStream("/S0 sh"u8.ToArray());
        document.Internals.AddObject(group);
        var mask = new Sharp.PdfDictionary(document);
        mask.Elements.SetName("/Type", "/Mask");
        mask.Elements.SetName("/S", "/Luminosity");
        mask.Elements.SetReference("/G", group);
        return State(document, s => s.Elements["/SMask"] = mask);
    }

    // ───────────────────────── Reading the SVG ─────────────────────────

    private static string ToSvg(string pdf, DocumentLimits? limits = null, int page = 0)
    {
        var output = Path.ChangeExtension(pdf, ".svg");
        var written = PdfSvgWriter.Write(pdf, _ => [page], (_, _) => output, cancellationToken: Token, limits: limits);
        return Assert.Single(written);
    }

    private static XDocument Read(string svg) => XDocument.Load(svg);

    private static IEnumerable<XElement> All(XDocument svg, string name) => svg.Descendants(Svg + name);

    private static string? Attr(XElement element, string name) => (string?)element.Attribute(name);

    /// <summary>The element a use refers to.</summary>
    private static XElement Referenced(XDocument svg, XElement use)
    {
        var id = Attr(use, XLink + "href")!.TrimStart('#');
        return svg.Descendants().Single(e => Attr(e, "id") == id);
    }

    private static string? Attr(XElement element, XName name) => (string?)element.Attribute(name);

    /// <summary>The PDF's page as PDFium renders it for PDF → PNG, at 72 dpi on white.</summary>
    private static SKBitmap RenderPdf(string pdf, int page = 0)
    {
        var bytes = File.ReadAllBytes(pdf);
        var options = new RenderOptions(Dpi: 72, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White);
        return Conversion.ToImages(bytes, [page], null, options).Single();
    }

    /// <summary>The SVG as Filee's SVG renderer draws it, the page's size in pixels at 72 dpi, on white.</summary>
    private static SKBitmap RenderSvg(string svg, int width = (int)Width, int height = (int)Height)
    {
        using var drawing = SvgRenderer.Load(svg);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Scale(width / drawing.Bounds.Width, height / drawing.Bounds.Height);
        canvas.Translate(-drawing.Bounds.Left, -drawing.Bounds.Top);
        canvas.DrawPicture(drawing.Picture);
        return bitmap;
    }

    private static void Near(SKColor expected, SKColor actual, int slack = 30, string? where = null) =>
        Assert.True(Math.Abs(actual.Red - expected.Red) <= slack && Math.Abs(actual.Green - expected.Green) <= slack && Math.Abs(actual.Blue - expected.Blue) <= slack,
            $"{where}: expected {expected}, got {actual}");

    /// <summary>The SVG's pixel at a page point (y from the top) is the colour expected and PDFium's there.</summary>
    private static void Same(SKBitmap pdf, SKBitmap svg, int x, int y, SKColor expected, int slack = 30)
    {
        Near(expected, pdf.GetPixel(x, y), slack, $"PDF at {x},{y}");
        Near(pdf.GetPixel(x, y), svg.GetPixel(x, y), slack, $"SVG at {x},{y}");
    }

    // ───────────────────────── Paths ─────────────────────────

    private const string Shapes = """
        1 0 0 rg 72 600 144 100 re f
        q 0 0 1 RG 6 w [12 12] 0 d 1 J 2 j 300 700 m 500 700 l 500 600 l S Q
        q 0 0.5 0 rg 72 300 m 172 500 272 500 372 300 c h f Q
        q 1 0 1 rg 400 200 m 550 200 l 475 330 l h 400 250 m 550 250 l 475 120 l h f* Q
        q 2 0 0 2 50 50 cm 0 0 0 RG 0.5 w 10 M 0 0 m 40 20 l 0 40 l S Q
        """;

    [Fact]
    public void Paths_keep_their_outline_fill_rule_colours_and_strokes()
    {
        var svg = Read(ToSvg(RawPdf(Path.Combine(fx.NewFolder(), "shapes.pdf"), Shapes)));
        var root = svg.Root!;
        Assert.Equal("612pt", Attr(root, "width"));
        Assert.Equal("0 0 612 792", Attr(root, "viewBox"));
        var paths = All(svg, "path").Where(p => p.Parent?.Name != Svg + "defs" && p.Parent?.Name != Svg + "clipPath").ToList();

        // The rectangle in page coordinates, y down: from 92 to 192 points below the top.
        var red = Assert.Single(paths, p => Attr(p, "fill") == "#ff0000");
        Assert.Equal("M72 192L216 192L216 92L72 92Z", Attr(red, "d"));

        var dashed = Assert.Single(paths, p => Attr(p, "stroke") == "#0000ff");
        Assert.Equal("none", Attr(dashed, "fill"));
        Assert.Equal("6", Attr(dashed, "stroke-width"));
        Assert.Equal("12 12", Attr(dashed, "stroke-dasharray"));
        Assert.Equal("round", Attr(dashed, "stroke-linecap"));
        Assert.Equal("bevel", Attr(dashed, "stroke-linejoin"));
        Assert.Equal("M300 92L500 92L500 192", Attr(dashed, "d"));

        // The curve stays a curve.
        var curve = Assert.Single(paths, p => Attr(p, "fill") == "#008000");
        Assert.Equal("M72 492C172 292 272 292 372 492Z", Attr(curve, "d"));

        Assert.Equal("evenodd", Attr(Assert.Single(paths, p => Attr(p, "fill") == "#ff00ff"), "fill-rule"));

        // Drawn under "2 0 0 2 50 50 cm": the line is 1 point wide on the page, its miter limit the PDF's.
        var scaled = Assert.Single(paths, p => Attr(p, "stroke") == "#000000");
        Assert.Equal("1", Attr(scaled, "stroke-width"));
        Assert.Equal("10", Attr(scaled, "stroke-miterlimit"));
        Assert.Equal("M50 742L130 702L50 662", Attr(scaled, "d"));
    }

    [Fact]
    public void Paths_drawn_back_match_the_pdfs_rendering()
    {
        var pdf = RawPdf(Path.Combine(fx.NewFolder(), "shapes.pdf"), Shapes);
        using var expected = RenderPdf(pdf);
        using var actual = RenderSvg(ToSvg(pdf));
        Same(expected, actual, 144, 142, SKColors.Red);
        Same(expected, actual, 222, 412, new SKColor(0, 128, 0));
        // The star's two triangles overlap in a hole (even-odd), each alone is magenta.
        Same(expected, actual, 475, 567, SKColors.White);
        Same(expected, actual, 475, 492, SKColors.Magenta);
        // A dash of the blue line and the gap after it.
        Same(expected, actual, 306, 92, SKColors.Blue);
        Same(expected, actual, 318, 92, SKColors.White); // the round caps reach 3 points into the gap
        Same(expected, actual, 400, 300, SKColors.White);
    }

    [Fact]
    public void Clipping_paths_cut_what_is_painted_through_them()
    {
        // A rectangle, then a triangle inside it (even-odd), fill the page green; a red bar outside both.
        var pdf = RawPdf(Path.Combine(fx.NewFolder(), "clip.pdf"), """
            q 100 100 300 300 re W n 100 100 m 400 100 l 250 400 l h W* n 0 1 0 rg 0 0 612 792 re f Q
            1 0 0 rg 450 100 50 50 re f
            """);
        var file = ToSvg(pdf);
        var svg = Read(file);
        var clip = Assert.Single(All(svg, "clipPath"));
        Assert.Equal("userSpaceOnUse", Attr(clip, "clipPathUnits"));
        var group = Assert.Single(svg.Descendants(Svg + "g"), g => Attr(g, "clip-path") == $"url(#{Attr(clip, "id")})");
        Assert.Equal("#00ff00", Attr(group.Elements(Svg + "path").Single(), "fill"));

        using var expected = RenderPdf(pdf);
        using var actual = RenderSvg(file);
        Same(expected, actual, 250, 600, SKColors.Lime); // inside the triangle
        Same(expected, actual, 120, 420, SKColors.White); // inside the rectangle, outside the triangle
        Same(expected, actual, 50, 50, SKColors.White); // outside both
        Same(expected, actual, 475, 667, SKColors.Red);
    }

    // ───────────────────────── Shadings, transparency ─────────────────────────

    private static string Shaded(string folder) => RawPdf(Path.Combine(folder, "shaded.pdf"), """
        q 50 500 250 200 re W n /Ax sh Q
        q /Pattern cs /Rd scn 350 500 200 200 re f Q
        q /Half gs 0 0 1 rg 50 250 250 150 re f Q
        q /Soft gs 1 0 0 rg 350 250 200 150 re f Q
        """, (document, page) =>
        {
            Resource(document, page, "/Shading", "/Ax", Shading(document, 2, [50, 0, 300, 0], [1, 0, 0], [0, 0, 1]));
            var pattern = new Sharp.PdfDictionary(document);
            pattern.Elements.SetName("/Type", "/Pattern");
            pattern.Elements.SetInteger("/PatternType", 2);
            pattern.Elements.SetReference("/Shading", Shading(document, 3, [450, 600, 0, 450, 600, 100], [1, 1, 0], [0, 0.5, 0]));
            Resource(document, page, "/Pattern", "/Rd", pattern);
            Resource(document, page, "/ExtGState", "/Half", State(document, s => s.Elements["/ca"] = new Sharp.PdfReal(0.5)));
            // Black at the left, white at the right of the red bar: the mask fades it in.
            Resource(document, page, "/ExtGState", "/Soft", MaskState(document, Shading(document, 2, [350, 0, 550, 0], [0, 0, 0], [1, 1, 1])));
        });

    [Fact]
    public void Shadings_become_gradients_and_transparency_opacity_and_masks()
    {
        var pdf = Shaded(fx.NewFolder());
        var file = ToSvg(pdf);
        var svg = Read(file);

        var linear = All(svg, "linearGradient").First(g => g.Elements(Svg + "stop").First().Attribute("stop-color")?.Value == "#ff0000");
        Assert.Equal("userSpaceOnUse", Attr(linear, "gradientUnits"));
        // A linear function: its two ends are the only stops.
        Assert.Equal(["#ff0000", "#0000ff"], linear.Elements(Svg + "stop").Select(s => Attr(s, "stop-color")));
        var radial = Assert.Single(All(svg, "radialGradient"));
        Assert.Equal("100", Attr(radial, "r"));
        Assert.Contains(paths(svg), p => Attr(p, "fill") == $"url(#{Attr(radial, "id")})");
        Assert.Contains(paths(svg), p => Attr(p, "fill") == "#0000ff" && Attr(p, "fill-opacity") == "0.502"); // ca 0.5, as a byte
        var mask = Assert.Single(All(svg, "mask"));
        Assert.Contains(svg.Descendants(Svg + "g"), g => Attr(g, "mask") == $"url(#{Attr(mask, "id")})");

        using var expected = RenderPdf(pdf);
        using var actual = RenderSvg(file);
        Same(expected, actual, 60, 192, new SKColor(245, 0, 10), 35); // the axial shading's red end
        Same(expected, actual, 175, 192, new SKColor(128, 0, 128), 35); // its middle
        Same(expected, actual, 290, 192, new SKColor(10, 0, 245), 35);
        Same(expected, actual, 450, 192, SKColors.Yellow, 35); // the radial pattern's centre
        Same(expected, actual, 175, 467, new SKColor(128, 128, 255), 20); // blue at half alpha
        Same(expected, actual, 360, 467, new SKColor(255, 245, 245), 35); // the masked bar, nearly clear at the left
        Same(expected, actual, 540, 467, new SKColor(255, 13, 13), 35); // and nearly red at the right

        static IEnumerable<XElement> paths(XDocument svg) => svg.Descendants(Svg + "path");
    }

    // ───────────────────────── Pictures ─────────────────────────

    [Fact]
    public void Pictures_are_embedded_once_a_jpeg_as_it_is()
    {
        var folder = fx.NewFolder();
        using (var photo = new MagickImage("gradient:#FF8800-#0044AA", 64, 48))
            photo.Write(Path.Combine(folder, "photo.jpg"), MagickFormat.Jpeg);
        var jpeg = File.ReadAllBytes(Path.Combine(folder, "photo.jpg"));
        var pdf = RawPdf(Path.Combine(folder, "pictures.pdf"), """
            q 128 0 0 96 50 600 cm /Jp Do Q
            q 128 0 0 96 300 600 cm /Jp Do Q
            q 100 0 0 100 50 400 cm /Sm Do Q
            q 0 0 1 rg 100 0 0 100 300 400 cm /St Do Q
            """, (document, page) =>
            {
                Resource(document, page, "/XObject", "/Jp", Picture(document, 64, 48, "/DeviceRGB", jpeg, "/DCTDecode"));
                // Red, its left half clear (a soft mask).
                var soft = Picture(document, 16, 16, "/DeviceRGB", Rgb(16, 16, (_, _) => (255, 0, 0)));
                var alpha = Picture(document, 16, 16, "/DeviceGray", [.. Enumerable.Range(0, 256).Select(i => (byte)(i % 16 < 8 ? 0 : 255))]);
                soft.Elements.SetReference("/SMask", alpha);
                Resource(document, page, "/XObject", "/Sm", soft);
                // A stencil mask: a checkerboard of 2 × 2 painted in the fill colour.
                var stencil = new Sharp.PdfDictionary(document);
                stencil.Elements.SetName("/Type", "/XObject");
                stencil.Elements.SetName("/Subtype", "/Image");
                stencil.Elements.SetInteger("/Width", 2);
                stencil.Elements.SetInteger("/Height", 2);
                stencil.Elements.SetInteger("/BitsPerComponent", 1);
                stencil.Elements["/ImageMask"] = new Sharp.PdfBoolean(true);
                stencil.CreateStream([0b0100_0000, 0b1000_0000]);
                Resource(document, page, "/XObject", "/St", stencil);
            });
        var file = ToSvg(pdf);
        var svg = Read(file);

        var images = All(svg, "image").ToList();
        var embedded = Assert.Single(images, i => Attr(i, XLink + "href")!.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal));
        Assert.Equal(jpeg, Convert.FromBase64String(Attr(embedded, XLink + "href")!["data:image/jpeg;base64,".Length..]));
        Assert.Equal(2, svg.Descendants(Svg + "use").Count(u => Attr(u, XLink + "href") == "#" + Attr(embedded, "id")));
        Assert.Equal(3, images.Count); // the JPEG, the soft-masked picture and the stencil, each once

        using var expected = RenderPdf(pdf);
        using var actual = RenderSvg(file);
        Same(expected, actual, 114, 144, expected.GetPixel(114, 144), 25); // the middle of each JPEG
        Same(expected, actual, 364, 144, expected.GetPixel(114, 144), 25);
        Same(expected, actual, 75, 342, SKColors.White); // the soft mask's clear half
        Same(expected, actual, 125, 342, SKColors.Red);
        Same(expected, actual, 325, 317, SKColors.Blue); // the stencil's top-left quarter paints (a sample of 0)
        Same(expected, actual, 375, 317, SKColors.White);
        Same(expected, actual, 325, 367, SKColors.White);
        Same(expected, actual, 375, 367, SKColors.Blue);
    }

    // ───────────────────────── Text ─────────────────────────

    [Fact]
    public void Text_is_drawn_as_glyph_outlines_and_kept_as_selectable_text()
    {
        var pdf = RawPdf(Path.Combine(fx.NewFolder(), "text.pdf"), """
            BT /F1 24 Tf 72 700 Td (Hello SVG) Tj ET
            BT /F1 24 Tf 250 700 Td (world) Tj ET
            BT 1 0 0 rg /F1 18 Tf 0 1 -1 0 500 300 Tm (Turned) Tj ET
            BT 3 Tr /F1 12 Tf 72 100 Td (Hidden layer) Tj ET
            """);
        var file = ToSvg(pdf);
        var svg = Read(file);

        // Each letter is a use of its outline, each outline defined once ("l" twice in "Hello").
        var uses = svg.Descendants(Svg + "use").Where(u => Referenced(svg, u).Name == Svg + "path").ToList();
        Assert.Equal("HelloSVGworldTurned".Length, uses.Count);
        Assert.Equal("HeloSVGwrdTun".Length, uses.Select(u => Attr(u, XLink + "href")).Distinct().Count());
        Assert.Contains(uses, u => Attr(u, "fill") == "#ff0000");

        // The text on top, transparent: words apart on a line get a space, turned text its own line, hidden text too.
        var layer = Assert.Single(svg.Descendants(Svg + "g"), g => Attr(g, "opacity") == "0");
        var texts = layer.Elements(Svg + "text").ToList();
        Assert.Equal(["Hello SVG world", "Turned", "Hidden layer"], texts.Select(t => t.Value));
        Assert.StartsWith("matrix(1 0 0 1 72 92)", Attr(texts[0], "transform"));
        Assert.Equal("24", Attr(texts[0], "font-size"));
        Assert.Equal("0", Attr(texts[0], "x")!.Split(' ')[0]);
        // "world" starts 178 points after "Hello".
        Assert.Equal(178, double.Parse(Attr(texts[0], "x")!.Split(' ')[10], System.Globalization.CultureInfo.InvariantCulture), 1);
        Assert.StartsWith("matrix(0 -1 1 0 500 492)", Attr(texts[1], "transform"));

        // The letters' ink where PDFium draws them (another font for Helvetica, so only about as much).
        using var expected = RenderPdf(pdf);
        using var actual = RenderSvg(file);
        static int Ink(SKBitmap bitmap, SKRectI box)
        {
            var dark = 0;
            for (var y = box.Top; y < box.Bottom; y++)
                for (var x = box.Left; x < box.Right; x++)
                    dark += bitmap.GetPixel(x, y).Red < 128 ? 1 : 0;
            return dark;
        }
        var line = new SKRectI(60, 60, 340, 100);
        Assert.InRange(Ink(actual, line), Ink(expected, line) * 0.6, Ink(expected, line) * 1.4);
        Assert.Equal(0, Ink(actual, new SKRectI(60, 680, 300, 700))); // the hidden text stays hidden
    }

    [Fact]
    public void Text_in_an_embedded_font_is_drawn_with_its_own_outlines()
    {
        var font = FontFile("NotoSans-Regular.ttf");
        Assert.SkipUnless(font is not null, "Noto Sans is not in src/Filee.App/Assets/Fonts");
        var builder = new PdfDocumentBuilder();
        var noto = builder.AddTrueTypeFont(File.ReadAllBytes(font));
        var page = builder.AddPage(Width, Height);
        page.AddText("Embedded Noto 123", 30, new PdfPoint(72, 600), noto);
        var pdf = Path.Combine(fx.NewFolder(), "embedded.pdf");
        File.WriteAllBytes(pdf, builder.Build());

        var file = ToSvg(pdf);
        var svg = Read(file);
        Assert.Contains("Embedded Noto 123", svg.Descendants(Svg + "text").Select(t => t.Value));
        using var expected = RenderPdf(pdf);
        using var actual = RenderSvg(file);
        // The same font program on both sides: the ink of the line matches closely, column by column.
        var (same, inked) = (0, 0);
        for (var y = 160; y < 200; y++)
        {
            for (var x = 60; x < 360; x++)
            {
                var (a, b) = (expected.GetPixel(x, y).Red < 128, actual.GetPixel(x, y).Red < 128);
                inked += a || b ? 1 : 0;
                same += a && b ? 1 : 0;
            }
        }
        Assert.True(inked > 500 && same > 0.8 * inked, $"{same} of {inked}");
    }

    private static string? FontFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Filee.slnx")))
            {
                var path = Path.Combine(dir.FullName, "src", "Filee.App", "Assets", "Fonts", name);
                return File.Exists(path) ? path : null;
            }
        }
        return null;
    }

    // ───────────────────────── The route and its files ─────────────────────────

    [Fact]
    public void Pdf_to_svg_is_filees_own_and_pictures_are_still_traced()
    {
        var planner = fx.Catalog.CreatePlanner(["libreoffice", "ghostscript"]);
        Assert.Equal("pdf-svg", Assert.Single(planner.Plan("pdf", "svg")!.Steps).Converter.Id);
        Assert.Equal(["ghostscript", "pdf-svg"], planner.Plan("eps", "svg")!.Steps.Select(s => s.Converter.Id));
        // A photo is traced, never put into a PDF to come back as a picture inside an SVG.
        Assert.DoesNotContain(planner.Plan("png", "svg")!.Steps, s => s.Converter.Id == "pdf-svg");
        var edge = Assert.Single(fx.Converters.Single(c => c.Id == "pdf-svg").Edges);
        Assert.True(edge.NotForImageSources);
        // LibreOffice stays the fallback, at a higher cost.
        var fallback = fx.Converters.Single(c => c.Id == "libreoffice").Edges.Single(e => e is { From: "pdf", To: "svg" });
        Assert.True(edge.Cost < fallback.Cost && fallback.NotForImageSources);
    }

    [Fact]
    public async Task Every_page_becomes_an_svg_named_as_pdf_to_png_names_its_pictures()
    {
        var folder = fx.NewFolder();
        var pdf = RawPdf(Path.Combine(folder, "pages.pdf"), [.. Enumerable.Range(1, 3).Select(n => Inv($"BT /F1 30 Tf 72 700 Td (Page {n}) Tj ET"))]);

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "svg" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        Assert.Equal(["pages_p1.svg", "pages_p2.svg", "pages_p3.svg"], job.Outputs.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var output in job.Outputs)
        {
            var n = Path.GetFileNameWithoutExtension(output)[^1];
            Assert.Equal($"Page {n}", Read(output).Descendants(Svg + "text").Single().Value);
        }

        // One page of the range keeps the name as it is.
        var second = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "svg", Pdf = { PageRange = "2" } });
        var single = Assert.Single(second.Outputs);
        Assert.Equal("pages.svg", Path.GetFileName(single));
        Assert.Equal("Page 2", Read(single).Descendants(Svg + "text").Single().Value);
    }

    // ───────────────────────── Hostile pages ─────────────────────────

    [Fact]
    public void A_picture_drawn_thousands_of_times_is_read_and_embedded_once()
    {
        var content = new StringBuilder();
        for (var i = 0; i < 20_000; i++)
            content.Append(Inv($"q 20 0 0 20 {i % 30 * 20} {i / 30 % 39 * 20} cm /Ph Do Q\n"));
        for (var i = 0; i < 20_000; i++)
            content.Append(Inv($"q {i % 100} {i / 100 % 100} 300 300 re W n 0 0 1 rg {i % 600} {i / 600 * 20} 1 1 re f Q\n"));
        var pdf = RawPdf(Path.Combine(fx.NewFolder(), "many.pdf"), content.ToString(), (document, page) =>
            Resource(document, page, "/XObject", "/Ph", Picture(document, 512, 512, "/DeviceRGB", Rgb(512, 512, (x, y) => ((byte)x, (byte)y, 128)))));

        // The picture's stream is read from the file once, not for each "Do" (PdfPig reads it again each time).
        using (var document = UglyToad.PdfPig.PdfDocument.Open(pdf))
        {
            document.AddPageFactory<PdfSvgPage, PdfSvgPageFactory>();
            using var page = document.GetPage<PdfSvgPage>(1);
            Assert.Equal(1, page.XObjectReads);
            Assert.Equal(40_000, page.Drawing.Paints.Count);
        }
        var file = ToSvg(pdf);
        var svg = Read(file);
        Assert.Single(All(svg, "image"));
        Assert.Equal(20_000, svg.Descendants(Svg + "use").Count());
        Assert.True(new FileInfo(file).Length < 32L * 1024 * 1024, new FileInfo(file).Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_page_past_the_limits_is_refused_as_too_large()
    {
        var content = new StringBuilder();
        for (var i = 0; i < 5000; i++)
            content.Append(Inv($"0 0 1 rg {i % 600} {i / 600 * 20} 1 1 re f\n"));
        var pdf = RawPdf(Path.Combine(fx.NewFolder(), "big.pdf"), [content.ToString(), "0 0 1 rg 0 0 10 10 re f"]);

        var refused = Assert.Throws<ConversionRefusedException>(() => ToSvg(pdf, DocumentLimits.Default with { MaxPdfStreamBytes = 50_000 }));
        Assert.Equal(DocumentLimits.TooLargeKey, refused.ErrorKey);
        Assert.False(File.Exists(Path.ChangeExtension(pdf, ".svg"))); // the part written is gone

        var pages = Assert.Throws<ConversionRefusedException>(() =>
            PdfSvgWriter.Write(pdf, count => [.. Enumerable.Range(0, count)], (p, _) => Path.ChangeExtension(pdf, $".{p}.svg"), cancellationToken: Token,
                limits: DocumentLimits.Default with { MaxPages = 1 }));
        Assert.Equal(DocumentLimits.TooLargeKey, pages.ErrorKey);
    }
}
