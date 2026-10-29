// The colours a DWG / DXF is drawn in (#411): as drawn, black and white like a monochrome plot, or grayscale. The
// drawing is generated here with coloured lines, text, hatches, a dimension and a block; outputs are read back as
// pixels (PNG) and colour operators (PDF).

using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Cad;
using ImageMagick;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace Filee.Engines.Tests;

public class CadColorTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>A light blue true colour for a solid hatch: brightness 205.</summary>
    private static readonly Color LightFill = new(173, 216, 230);

    private async Task<string> ConvertAsync(string input, string target, CadColors colors)
    {
        var preset = new Preset { TargetFormat = target, Cad = { Colors = colors }, Pdf = { RenderDpi = 100 }, Output = { FileNamePattern = "{name}_" + colors } };
        var job = await fx.ConvertAsync([input], preset);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    /// <summary>
    /// Lines in red, yellow (ACI 2) and a true colour; green text; a cyan pattern hatch; a light blue solid hatch; a
    /// magenta dimension with arrowheads; a block whose BYBLOCK circle takes the insert's orange; a wide polyline.
    /// </summary>
    private static CadDocument Colourful()
    {
        var doc = new CadDocument();
        var layer = new Layer("Colours") { Color = new Color(1) };
        doc.Layers.Add(layer);

        // 1 mm pens, so each line has a core of whole pixels to compare.
        CadBuilder.Add(doc, layer, new Line(new XYZ(0, 0, 0), new XYZ(100, 0, 0)) { LineWeight = LineWeightType.W100 });
        CadBuilder.Add(doc, layer, new Line(new XYZ(0, 10, 0), new XYZ(100, 10, 0)) { Color = new Color(2), LineWeight = LineWeightType.W100 });
        CadBuilder.Add(doc, layer, new Line(new XYZ(0, 20, 0), new XYZ(100, 20, 0)) { Color = new Color(255, 160, 0), LineWeight = LineWeightType.W100 });
        CadBuilder.Add(doc, layer, new TextEntity("MONO") { InsertPoint = new XYZ(0, 30, 0), Height = 8, Color = new Color(3) });

        var pattern = new HatchPattern("ANSI31");
        pattern.Lines.Add(new HatchPattern.Line { Angle = Math.PI / 4, BasePoint = XY.Zero, Offset = new XY(-Math.Sqrt(2), Math.Sqrt(2)) });
        var patterned = new Hatch { IsSolid = false, Pattern = pattern, Color = new Color(4) };
        patterned.Paths.Add(CadBuilder.Rectangle(110, 0, 20, 20));
        CadBuilder.Add(doc, layer, patterned);
        var solid = new Hatch { IsSolid = true, Color = LightFill };
        solid.Paths.Add(CadBuilder.Rectangle(110, 30, 20, 20));
        CadBuilder.Add(doc, layer, solid);

        var dimension = new DimensionAligned
        {
            FirstPoint = new XYZ(0, 50, 0),
            SecondPoint = new XYZ(100, 50, 0),
            Style = new DimensionStyle("Filee") { TextHeight = 3, ArrowSize = 3 },
            Color = new Color(6),
        };
        dimension.Offset = 6;
        CadBuilder.Add(doc, layer, dimension);
        dimension.UpdateBlock();

        var block = new BlockRecord("RING");
        block.Entities.Add(new Circle(new XYZ(0, 0, 0), 5) { Color = Color.ByBlock });
        doc.BlockRecords.Add(block);
        CadBuilder.Add(doc, layer, new Insert(block) { InsertPoint = new XYZ(60, 35, 0), Color = new Color(30) });
        CadBuilder.Add(doc, layer, new LwPolyline(new LwPolyline.Vertex(0, -10), new LwPolyline.Vertex(100, -10)) { ConstantWidth = 1.5, Color = new Color(5) });
        return doc;
    }

    private string Source() => CadBuilder.SaveDxf(Colourful(), fx.NewFolder(), "colours.dxf");

    [Fact]
    public async Task Black_and_white_draws_every_line_and_text_black_and_fills_in_gray()
    {
        var source = Source();
        using var drawn = new MagickImage(await ConvertAsync(source, "png", CadColors.AsDrawn));
        using var mono = new MagickImage(await ConvertAsync(source, "png", CadColors.BlackAndWhite));
        Assert.Equal((drawn.Width, drawn.Height), (mono.Width, mono.Height));
        var before = Rgb(drawn);
        var after = Rgb(mono);

        Assert.True(CountColoured(before) > 5000, "the drawing as drawn is in colour");
        Assert.Equal(0, CountColoured(after));
        // Where the yellow line was, the page is now black (yellow on white was the faintest line of all).
        var yellow = Positions(before, (r, g, b) => r > 220 && g > 220 && b < 20);
        Assert.True(yellow.Count > 200, $"{yellow.Count} yellow pixels");
        Assert.All(yellow, i => Assert.True(after[i] < 60, $"pixel {i / 3} is {after[i]}"));
        // Every other pen colour too: whatever a line, text, arrowhead or hatch line covers (nearly) whole is black or
        // close to it (Skia smooths the edges of black text more than those of green text).
        var ink = Positions(before, (r, g, b) => Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 230);
        Assert.True(ink.Count > 2000, $"{ink.Count} pen pixels");
        Assert.All(ink, i => Assert.True(after[i] < 100, $"pixel {i / 3} is {after[i]}"));
        Assert.True(ink.Count(i => after[i] < 30) > ink.Count * 0.8, "pen colours turn black");
        // The light blue solid hatch is a light gray area of its brightness, not a black block.
        var fill = Positions(before, (r, g, b) => Math.Abs(r - 173) < 3 && Math.Abs(g - 216) < 3 && Math.Abs(b - 230) < 3);
        Assert.True(fill.Count > 10_000, $"{fill.Count} fill pixels");
        Assert.All(fill, i => Assert.InRange(after[i], 202, 208));
        // The page stays white.
        Assert.Equal((255, 255, 255), (after[0], after[1], after[2]));
    }

    [Fact]
    public async Task Grayscale_keeps_shades_and_darkens_light_lines_enough_to_read()
    {
        var source = Source();
        using var drawn = new MagickImage(await ConvertAsync(source, "png", CadColors.AsDrawn));
        using var gray = new MagickImage(await ConvertAsync(source, "png", CadColors.Grayscale));
        var before = Rgb(drawn);
        var after = Rgb(gray);

        Assert.Equal(0, CountColoured(after));
        // Yellow (brightness 226) prints as a mid gray instead of nearly white.
        var yellow = Positions(before, (r, g, b) => r > 220 && g > 220 && b < 20);
        Assert.True(yellow.Count > 200);
        Assert.All(yellow, i => Assert.InRange(after[i], 125, 160));
        // Red stays darker than yellow: the shades keep their order.
        var red = Positions(before, (r, g, b) => r > 245 && g < 10 && b < 10);
        Assert.True(red.Count > 200);
        Assert.All(red, i => Assert.True(after[i] < 60, $"red became {after[i]}"));
        var fill = Positions(before, (r, g, b) => Math.Abs(r - 173) < 3 && Math.Abs(g - 216) < 3 && Math.Abs(b - 230) < 3);
        Assert.All(fill, i => Assert.InRange(after[i], 202, 208));
    }

    [Theory]
    [InlineData(CadColors.BlackAndWhite)]
    [InlineData(CadColors.Grayscale)]
    public async Task A_pdf_in_black_and_white_or_grayscale_uses_gray_colours_only(CadColors colors)
    {
        var source = Source();
        Assert.Contains(PdfColors(await ConvertAsync(source, "pdf", CadColors.AsDrawn)), c => c[0] != c[1] || c[1] != c[2]);

        var pdfColors = PdfColors(await ConvertAsync(source, "pdf", colors));

        Assert.NotEmpty(pdfColors);
        Assert.All(pdfColors, c => Assert.True(c[0] == c[1] && c[1] == c[2], $"colour {string.Join(" ", c)}"));
        if (colors == CadColors.BlackAndWhite)
            Assert.Contains(pdfColors, c => c[0] == 0);
    }

    [Fact]
    public void The_palette_keeps_ink_dark_and_areas_by_brightness()
    {
        var yellow = new SKColor(255, 255, 0);
        var black = SKColors.Black;

        Assert.Equal(yellow, new CadPalette(CadColors.AsDrawn).Ink(yellow));
        Assert.Equal(black, new CadPalette(CadColors.BlackAndWhite).Ink(yellow));
        Assert.Equal(new SKColor(226, 226, 226), new CadPalette(CadColors.BlackAndWhite).Area(yellow));
        Assert.Equal(black, new CadPalette(CadColors.BlackAndWhite).Area(black)); // a black fill stays black
        Assert.Equal(new SKColor(142, 142, 142), new CadPalette(CadColors.Grayscale).Ink(yellow));
        Assert.Equal(new SKColor(226, 226, 226), new CadPalette(CadColors.Grayscale).Area(yellow));
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static byte[] Rgb(MagickImage image)
    {
        using var pixels = image.GetPixels();
        return pixels.ToByteArray(PixelMapping.RGB)!;
    }

    /// <summary>Pixels that are not a gray (allowing for rounding).</summary>
    private static int CountColoured(byte[] rgb)
    {
        var count = 0;
        for (var i = 0; i < rgb.Length; i += 3)
            if (Math.Max(Math.Abs(rgb[i] - rgb[i + 1]), Math.Max(Math.Abs(rgb[i + 1] - rgb[i + 2]), Math.Abs(rgb[i] - rgb[i + 2]))) > 2)
                count++;
        return count;
    }

    /// <summary>Byte offsets of the pixels that match.</summary>
    private static List<int> Positions(byte[] rgb, Func<int, int, int, bool> match)
    {
        var positions = new List<int>();
        for (var i = 0; i < rgb.Length; i += 3)
            if (match(rgb[i], rgb[i + 1], rgb[i + 2]))
                positions.Add(i);
        return positions;
    }

    /// <summary>Every RGB colour the page sets (rg / RG and three-component sc / scn), in PDF units 0–1.</summary>
    private static List<double[]> PdfColors(string pdf)
    {
        using var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        var colors = new List<double[]>();
        foreach (var page in document.Pages)
            Collect(ContentReader.ReadContent(page), colors);
        return colors;
    }

    private static void Collect(CSequence sequence, List<double[]> colors)
    {
        foreach (var item in sequence)
        {
            if (item is CSequence inner)
                Collect(inner, colors);
            else if (item is COperator { OpCode.OpCodeName: OpCodeName.rg or OpCodeName.RG or OpCodeName.sc or OpCodeName.SC or OpCodeName.scn or OpCodeName.SCN } op
                && op.Operands.Count == 3)
                colors.Add([.. op.Operands.Select(o => o switch { CReal r => Math.Round(r.Value, 3), CInteger n => (double)n.Value, _ => double.NaN })]);
        }
    }
}
