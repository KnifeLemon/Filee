// Mutation fuzzing of the XPS reader (Xps/) and the drawing of what it read: the seed is a document written in code
// (XpsBuilder) with what costs the reader the most (an obfuscated embedded font with Indices and cluster maps, sideways
// glyphs, PNG, JPEG, TIFF and JPEG XR pictures, a picture and colours read through an ICC profile, gradients, tiled
// image and visual brushes, resources inline and in a remote dictionary, clips, opacity masks, path geometry with every
// segment kind, triangle and round caps, links and link targets). DocumentFuzzer changes the
// package, and every input must be read or refused with InvalidDataException / ConversionRefusedException, in time
// and on a 1 MB stack. Run by FuzzHarness: a short fixed run normally, FILEE_FUZZ_SECONDS for a longer random one,
// FILEE_FUZZ_SEED to repeat, FILEE_FUZZ_OUT to keep failing inputs.

using Filee.Engines.Xps;
using ImageMagick;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class XpsFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Inputs per seed in a normal test run.</summary>
    private const int QuickIterations = 50;

    [Fact]
    public void XpsReader_reads_or_refuses_every_mutated_document() =>
        new FuzzHarness(fx, QuickIterations, 20261119).Files("xps", "xps", [Seed(openXps: false), Seed(openXps: true)], FuzzHarness.Packages, (path, _) =>
        {
            using var document = XpsDocument.Open(path, CancellationToken.None);
            var pages = new List<Rendering.PageContent>();
            for (var i = 0; i < document.Count; i++)
            {
                pages.Add(document.Page(i, CancellationToken.None));
                using var bitmap = pages[^1].Render(12);
            }
            // The PDF writer subsets the embedded fonts.
            using var pdf = new MemoryStream();
            Rendering.PdfPageWriter.Write(pdf, pages);
        });

    /// <summary>A document with every kind of thing the reader reads.</summary>
    internal static byte[] Seed(bool openXps)
    {
        var builder = new XpsBuilder { OpenXps = openXps, Title = "Fuzz", LinkTargets = { "Target" } };
        var font = builder.Font(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "FileeTest-TrueType.ttf")));
        var png = builder.Image(DocumentFuzzTests.TinyPng(4, 3), "png");
        string jpeg, tiff;
        using (var image = new MagickImage(MagickColors.Orange, 4, 4))
        {
            jpeg = builder.Image(image.ToByteArray(MagickFormat.Jpeg), "jpg");
            tiff = builder.Image(image.ToByteArray(MagickFormat.Tiff), "tif");
        }
        var jpegXr = builder.Image(JpegXrTestFile.DcOnlyGrey(), "wdp");
        var profile = builder.Part("Resources/srgb.icc", Pdf.SrgbProfile.Bytes);
        var remote = builder.Dictionary("<SolidColorBrush x:Key='Remote' Color='sc#1,0.2,0.4,0.6'/><PathGeometry x:Key='Shape' Figures='M 0,0 L 10,0 10,10 Z'/>");
        var stops = "<GradientStop Color='#FF0000' Offset='-0.5'/><GradientStop Color='#8000FF00' Offset='0.4'/><GradientStop Color='#0000FF' Offset='1.5'/>";
        builder.Page(
            "<FixedPage.Resources><ResourceDictionary>" +
            $"<LinearGradientBrush x:Key='Linear' MappingMode='Absolute' StartPoint='0,0' EndPoint='100,20' SpreadMethod='Reflect'><LinearGradientBrush.GradientStops>{stops}</LinearGradientBrush.GradientStops></LinearGradientBrush>" +
            "<MatrixTransform x:Key='Turn' Matrix='0.8,0.6,-0.6,0.8,40,10'/>" +
            "</ResourceDictionary></FixedPage.Resources>" +
            $"<Canvas RenderTransform='{{StaticResource Turn}}' Clip='M 0,0 L 300,0 300,200 0,200 Z' Opacity='0.8'>" +
            $"<Canvas.Resources><ResourceDictionary Source='{remote}'/></Canvas.Resources>" +
            "<Path Data='{StaticResource Shape}' Fill='{StaticResource Remote}' Stroke='{StaticResource Linear}' StrokeThickness='2' StrokeDashArray='1 0.5 2' StrokeLineJoin='Round' StrokeStartLineCap='Round'/>" +
            "<Path Data='F1 M 10,10 h 30 v 30 H 10 Z m 50,0 c 0,10 10,10 10,0 s 10,-10 10,0 q 5,5 10,0 a 10,5 30 1 0 20,0 z' Fill='{StaticResource Linear}'>" +
            "<Path.OpacityMask><RadialGradientBrush MappingMode='Absolute' Center='50,30' GradientOrigin='40,20' RadiusX='60' RadiusY='30'>" +
            $"<RadialGradientBrush.GradientStops>{stops}</RadialGradientBrush.GradientStops></RadialGradientBrush></Path.OpacityMask></Path>" +
            "</Canvas>" +
            $"<Path Data='M 0,100 L 100,100 100,160 0,160 Z' Name='Target'><Path.Fill><ImageBrush ImageSource='{png}' Viewbox='0,0,4,3' Viewport='0,100,20,15' TileMode='FlipXY' Transform='1,0.2,0,1,0,0'/></Path.Fill></Path>" +
            $"<Path Data='M 110,100 L 160,100 160,160 110,160 Z' FixedPage.NavigateUri='https://filee.sh/'><Path.Fill><ImageBrush ImageSource='{jpeg}' Viewbox='1,1,2,2' Viewport='110,100,50,60'/></Path.Fill></Path>" +
            $"<Path Data='M 170,100 L 220,100 220,160 170,160 Z' FixedPage.NavigateUri='#Target'><Path.Fill><ImageBrush ImageSource='{{ColorConvertedBitmap {tiff} /profile.icc}}' Viewbox='0,0,4,4' Viewport='170,100,50,60' Opacity='0.5'/></Path.Fill></Path>" +
            "<Path Stroke='#000000' StrokeThickness='3'><Path.Data><PathGeometry FillRule='NonZero' Transform='1,0,0,1,0,170'>" +
            "<PathFigure StartPoint='0,0' IsClosed='true' IsFilled='false'><PolyLineSegment Points='20,0 20,20'/><PolyBezierSegment Points='10,30 0,30 0,20' IsStroked='false'/>" +
            "<PolyQuadraticBezierSegment Points='5,10 0,0'/><ArcSegment Point='30,0' Size='15,10' RotationAngle='45' IsLargeArc='true' SweepDirection='Clockwise'/></PathFigure>" +
            "</PathGeometry></Path.Data><Path.Fill><VisualBrush Viewbox='0,0,10,10' Viewport='0,0,8,8' TileMode='Tile'><VisualBrush.Visual>" +
            "<Canvas><Path Data='M 0,0 L 5,0 5,5 Z' Fill='#336699'/></Canvas></VisualBrush.Visual></VisualBrush></Path.Fill></Path>" +
            $"<Glyphs FontUri='{font}' FontRenderingEmSize='16' OriginX='10' OriginY='240' UnicodeString='Filee Hamburg' Indices='(2:1)41;,60;;,,10,5' Fill='#000000' StyleSimulations='BoldItalicSimulation'/>" +
            $"<Glyphs FontUri='{font}' FontRenderingEmSize='16' OriginX='300' OriginY='260' UnicodeString='Filee' BidiLevel='1' Clip='M 200,240 L 320,250 300,270 Z'>" +
            "<Glyphs.Fill><LinearGradientBrush MappingMode='Absolute' StartPoint='200,0' EndPoint='300,0'><LinearGradientBrush.GradientStops>" +
            $"{stops}</LinearGradientBrush.GradientStops></LinearGradientBrush></Glyphs.Fill></Glyphs>" +
            "<Glyphs FontUri='/Resources/missing.odttf' FontRenderingEmSize='12' OriginX='10' OriginY='280' UnicodeString='{}{fallback}' Fill='#FF0000'/>" +
            $"<Glyphs FontUri='{font}' FontRenderingEmSize='14' OriginX='380' OriginY='20' UnicodeString='Filee' IsSideways='true' RenderTransform='0,1,-1,0,380,20' Fill='ContextColor {profile} 1,0.2,0.4,0.6'/>" +
            $"<Path Data='M 230,100 L 270,100 270,140 230,140 Z' Fill='ContextColor /missing.icc 0.8,0,1,0,0,0.5'><Path.Stroke><ImageBrush ImageSource='{{ColorConvertedBitmap {png} {profile}}}' Viewbox='0,0,4,3' Viewport='0,0,8,6' TileMode='Tile'/></Path.Stroke></Path>" +
            $"<Path Data='M 280,100 L 330,100 330,150 280,150 Z'><Path.Fill><ImageBrush ImageSource='{jpegXr}' Viewbox='0,0,16,16' Viewport='280,100,50,50'/></Path.Fill></Path>" +
            "<Path Data='M 300,200 L 380,220' Stroke='#80000000' StrokeThickness='9' StrokeStartLineCap='Triangle' StrokeEndLineCap='Round' StrokeDashArray='2 1' StrokeDashCap='Triangle'/>" +
            "<mc:AlternateContent xmlns:mc='http://schemas.openxmlformats.org/markup-compatibility/2006'><mc:Choice Requires='x'/>" +
            "<mc:Fallback><Path Data='M 0,290 L 50,290' Stroke='#00FF00' RenderOptions.EdgeMode='Aliased'/></mc:Fallback></mc:AlternateContent>",
            400, 300);
        builder.Page("<Path Data='M 0,0 L 10,10' Stroke='#000000'/>", 200, 100);
        return builder.ToBytes();
    }
}
