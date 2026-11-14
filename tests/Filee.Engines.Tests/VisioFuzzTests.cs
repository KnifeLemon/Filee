// Mutation fuzzing of the Visio reader (Visio/VisioReader) and the slide renderer drawing what it read: the seed is a
// drawing written in code (VsdxBuilder) with what costs the reader the most (masters and their inheritance, style
// chains, every geometry row kind, NURBS and polyline data, groups, text with character and paragraph rows, a
// background page, layers, a picture, a theme with variants, gradients, shadows, text backgrounds and rounding). DocumentFuzzer changes the package, and every input must be read or refused
// with InvalidDataException / ConversionRefusedException, in time and on a 1 MB stack. Run by FuzzHarness: a short
// fixed run normally, FILEE_FUZZ_SECONDS for a longer random one, FILEE_FUZZ_SEED to repeat, FILEE_FUZZ_OUT to keep
// failing inputs.

using Filee.Engines.Slides;
using Filee.Engines.Visio;
using static Filee.Engines.Tests.VsdxBuilder;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class VisioFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Inputs per seed in a normal test run.</summary>
    private const int QuickIterations = 50;

    [Fact]
    public void VisioReader_reads_or_refuses_every_mutated_drawing() =>
        new FuzzHarness(fx, QuickIterations, 20261114).Files("vsdx", "vsdx", [Seed()], FuzzHarness.Packages, (path, _) =>
        {
            VisioReader.FirstUnsupported(path);
            var deck = VisioReader.Read(path, CancellationToken.None);
            using var shaper = new Typesetting.Shaper();
            using var renderer = new SlideRenderer(new Typesetting.FontResolver(), shaper);
            foreach (var slide in deck.Slides)
            {
                using var bitmap = renderer.Render(deck, slide).Render(12);
            }
        });

    /// <summary>A drawing with every kind of thing the reader reads.</summary>
    internal static byte[] Seed()
    {
        var builder = new VsdxBuilder { Theme = VisioThemeTests.Theme };
        builder.StyleSheets += "<StyleSheet ID='2' NameU='Theme' LineStyle='0' FillStyle='0' TextStyle='0'>" + Cell("FillForegnd", "Themed") +
                               Cell("FillPattern", "Themed") + Cell("LineColor", "Themed") + Cell("LineWeight", "Themed") + Cell("LinePattern", "Themed") +
                               Cell("QuickStyleFillColor", 100) + Cell("QuickStyleFillMatrix", 101) + Cell("QuickStyleLineMatrix", 2) +
                               Cell("QuickStyleEffectsMatrix", 2) + Cell("VariationColorIndex", 65534) +
                               "<Section N='Character'><Row IX='0'>" + Cell("Font", "Themed") + Cell("Color", "Themed") + "</Row></Section></StyleSheet>";
        builder.StyleSheets += "<StyleSheet ID='1' NameU='Blue' LineStyle='0' FillStyle='0' TextStyle='0'>" + Cell("FillForegnd", "4") +
                               Cell("FillPattern", 7) + Cell("LinePattern", 2) + "</StyleSheet>";
        var master = builder.Master(
            Shape(5, 0.5, 0.5, 1, 1, Rectangle(1, 1) + Cell("FillForegnd", "#3366CC") + TextBlock("<cp IX='0'/>Master")) +
            Shape(6, 1.5, 0.5, 1, 1, "<Shapes>" + Shape(7, 0.25, 0.25, 0.5, 0.5, Rectangle(0.5, 0.5)) + "</Shapes>", "Type='Group'"));
        var picture = DocumentFuzzTests.TinyPng(4, 3);
        var media = builder.Media(picture);
        var rows = Geometry(
            Row("MoveTo", 1, ("X", 0), ("Y", 0)) + Row("LineTo", 2, ("X", 1), ("Y", 0)) + Row("ArcTo", 3, ("X", 1), ("Y", 1), ("A", 0.2)) +
            Row("EllipticalArcTo", 4, ("X", 0), ("Y", 1), ("A", 0.5), ("B", 1.2), ("C", 0.3), ("D", 1.5)) +
            Row("RelCubBezTo", 5, ("X", 0), ("Y", 0.5), ("A", -0.2), ("B", 0.9), ("C", 0.2), ("D", 0.6)) +
            Row("RelQuadBezTo", 6, ("X", 0), ("Y", 0), ("A", 0.3), ("B", 0.2)) +
            "<Row T='NURBSTo' IX='7'>" + Cell("X", 1) + Cell("Y", 0) + Cell("A", 1) + Cell("B", 1) + Cell("C", 0) + Cell("D", 1) +
            "<Cell N='E' V='0' F='NURBS(1, 3, 0, 0, 0.1, 0.9, 0, 1, 0.6, 0.8, 0.5, 1)'/></Row>" +
            "<Row T='PolylineTo' IX='8'>" + Cell("X", 0) + Cell("Y", 0) + "<Cell N='A' V='0' F='POLYLINE(0, 0, 0.5, 0.2, 0.2, 0.1)'/></Row>") +
            Geometry(Row("Ellipse", 1, ("X", 0.5), ("Y", 0.5), ("A", 0.8), ("B", 0.5), ("C", 0.5), ("D", 0.7))
                     + Row("RelMoveTo", 2, ("X", 0.1), ("Y", 0.1)) + Row("RelLineTo", 3, ("X", 0.9), ("Y", 0.9)), 1, Cell("NoFill", 1));
        var text = "<Section N='Character'><Row IX='0'>" + Cell("Color", "#C00000") + Cell("Size", 0.2) + Cell("Style", 7) + "</Row>" +
                   "<Row IX='1'>" + Cell("Font", "Arial") + Cell("Pos", 1) + Cell("Case", 1) + "</Row></Section>" +
                   "<Section N='Paragraph'><Row IX='0'>" + Cell("HorzAlign", 2) + Cell("SpLine", -1.2) + Cell("Bullet", 1) + Cell("IndLeft", 0.1) + "</Row></Section>" +
                   "<Section N='Field'><Row IX='0'>" + Cell("Value", "field") + "</Row></Section>" +
                   TextBlock("<cp IX='0'/><pp IX='0'/>Visio 도형\t<cp IX='1'/>text<fld IX='0'/>\nline\u2028break\n");
        var background = builder.Page(Shape(1, 2, 1.5, 3, 2, Rectangle(3, 2) + Cell("FillForegnd", "#EEEEEE")), 4, 3, background: true);
        var layers = "<Section N='Layer'><Row IX='0'>" + Cell("Visible", 0) + "</Row></Section>";
        builder.Page(
            Shape(1, 1, 1, 1, 1, rows + text + Cell("Angle", 0.3) + Cell("FlipY", 1), "Type='Shape' LineStyle='1' FillStyle='1' TextStyle='0'") +
            $"<Shape ID='2' Type='Shape' Master='{master}' MasterShape='5'>" + Cell("PinX", 3) + Cell("PinY", 2) + Cell("Width", 1.5) + "</Shape>" +
            $"<Shape ID='3' Type='Group' Master='{master}' MasterShape='6'>" + Cell("PinX", 2) + Cell("PinY", 0.5) + "</Shape>" +
            Shape(4, 3, 1, 1, 0.5, "<ForeignData ForeignType='Bitmap'><Rel r:id='rId1' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'/></ForeignData>", "Type='Foreign'") +
            Shape(5, 2, 2.5, 1, 0, Geometry(Row("MoveTo", 1, ("X", 0), ("Y", 0)) + Row("LineTo", 2, ("X", 1), ("Y", 0))) + Cell("EndArrow", 4) + Cell("LayerMember", "0")) +
            Shape(6, 1, 2.5, 1, 0.5, Rectangle(1, 0.5) + Cell("Rounding", 0.1) + Cell("ShdwPattern", 1) + Cell("TextBkgnd", 3) + TextBlock("theme"),
                "Type='Shape' LineStyle='2' FillStyle='2' TextStyle='2'") +
            Shape(7, 3, 2.5, 1, 0.5, Rectangle(1, 0.5) + Cell("FillGradientEnabled", 1) + Cell("FillGradientAngle", 0.7) +
                "<Section N='FillGradient'><Row IX='0'>" + Cell("GradientStopColor", "#FF0000") + Cell("GradientStopPosition", 0) + "</Row>" +
                "<Row IX='1'>" + Cell("GradientStopColor", "Themed") + Cell("GradientStopPosition", 1) + "</Row></Section>"),
            4, 3, layers, backPage: background, pictures: new Dictionary<string, string> { ["rId1"] = media });
        return builder.ToBytes();
    }
}
