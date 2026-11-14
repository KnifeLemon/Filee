// Mutation fuzzing of the ODP / ODG reader (Odf/Draw): the seeds are OdfDrawTests' text and shape decks and a
// package with what costs the reader the most (masters with placeholders and shapes, groups, enhanced geometry with
// equations, modifiers and every path command, polygons and SVG paths, tables with repeats and spans, lists, fields,
// gradients, hatches, markers, pictures in parts and in the XML), written in code, as packages (DocumentFuzzer) and
// as flat XML (XmlFuzzer). Every input must be read or refused with InvalidDataException / ConversionRefusedException,
// in time and on a 1 MB stack; what is read is checked for what it can't draw and drawn small. Run by FuzzHarness: a
// short fixed run normally, FILEE_FUZZ_SECONDS for a longer random one, FILEE_FUZZ_SEED to repeat, FILEE_FUZZ_OUT to
// keep failing inputs.

using Filee.Engines.Odf.Draw;
using Filee.Engines.Slides;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class OdfDrawFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Inputs per seed in a normal test run.</summary>
    private const int QuickIterations = 40;

    private static readonly string[] Tokens =
    [
        "<draw:g>", "</draw:g>", "<draw:rect svg:width=\"1cm\" svg:height=\"1cm\"/>", " draw:transform=\"rotate (1e308) translate (1cm 1cm)\"",
        " draw:enhanced-path=\"M 0 0 A ?f0 ?f0 $0 $1 0 0 0 0 G 1 1 0 720 X 5 5 Y 0 0 T 1 1 1 1 0 0 Z N\"", " draw:formula=\"?f0/0\"",
        " draw:modifiers=\"-1e309 NaN\"", " svg:viewBox=\"0 0 0 0\"", " svg:d=\"M0 0a1 1 0 1 1 0 0z\"", " draw:points=\"0,0 1e300,1e300\"",
        "<text:list>", "</text:list>", "<text:list-item><text:p>x</text:p></text:list-item>", "<text:s text:c=\"99999\"/>",
        " presentation:class=\"outline\"", " presentation:placeholder=\"true\"", " table:number-columns-repeated=\"100000\"",
        " table:number-rows-repeated=\"100000\"", "<office:binary-data>iVBORw0KGgo=</office:binary-data>", "&amp;", "<![CDATA[x]]>",
    ];

    [Fact]
    public void OdfDrawReader_reads_or_refuses_every_mutated_package() =>
        new FuzzHarness(fx, QuickIterations, 20261114).Files("odp", "odp",
            [Seed(OdfDrawTests.TextDeck()), Seed(OdfDrawTests.ShapeDeck()), Seed(Costly())], FuzzHarness.Packages, Read);

    [Fact]
    public void OdfDrawReader_reads_or_refuses_every_mutated_flat_file() =>
        new FuzzHarness(fx, QuickIterations, 20261114).Files("fodp", "fodp",
            [System.Text.Encoding.UTF8.GetBytes(Costly(flat: true).Flat())], FuzzHarness.Xml(Tokens), Read);

    private static void Read(string path, string media)
    {
        var deck = OdfDrawReader.Read(path);
        OdfDrawReader.FirstUnsupported(path);
        SlideDeckText.Build(deck, media);
        if (deck.Slides.Count > 0)
        {
            foreach (var picture in SlideOutput.ToBitmaps(deck, 12, [0]))
                picture.Dispose();
        }
    }

    private byte[] Seed(OdpBuilder builder) => File.ReadAllBytes(builder.Save(Path.Combine(fx.NewFolder(), "seed.odp")));

    /// <summary>What costs the reader the most, in one deck.</summary>
    internal static OdpBuilder Costly(bool flat = false)
    {
        var png = Convert.ToBase64String(OdpBuilder.Png(4, 4, SkiaSharp.SKColors.Teal));
        var picture = flat
            ? $"<draw:frame svg:x=\"1cm\" svg:y=\"1cm\" svg:width=\"2cm\" svg:height=\"2cm\"><draw:image><office:binary-data>{png}</office:binary-data></draw:image></draw:frame>"
            : "<draw:frame svg:x=\"1cm\" svg:y=\"1cm\" svg:width=\"2cm\" svg:height=\"2cm\"><draw:image xlink:href=\"Pictures/p.png\"/></draw:frame>";
        var geometry = "<draw:custom-shape draw:style-name=\"gr1\" svg:width=\"6cm\" svg:height=\"4cm\" draw:transform=\"rotate (0.5) translate (8cm 2cm)\"><text:p>shape</text:p>" +
                       "<draw:enhanced-geometry svg:viewBox=\"0 0 21600 21600\" draw:modifiers=\"5400 3000\" draw:text-areas=\"?f0 0 21600 ?f1\" draw:mirror-horizontal=\"true\" " +
                       "draw:enhanced-path=\"M ?f0 0 L 21600 ?f1 C 10 10 20 20 30 30 Q 1 1 2 2 A 0 0 21600 21600 0 10800 21600 10800 B 0 0 100 100 0 0 100 100 " +
                       "V 0 0 50 50 0 0 50 50 W 0 0 50 50 50 50 0 0 T 10800 10800 100 100 0 90 U 5000 5000 100 200 0 360 X 100 100 200 200 Y 300 300 " +
                       "G 100 100 0 90 F S H I J K Z N M 0 0 L 1 1 N\">" +
                       "<draw:equation draw:name=\"f0\" draw:formula=\"$0 * sin(pi / 4) + if(?f1 - 1, abs(-3), sqrt(16)) - logwidth / 100\"/>" +
                       "<draw:equation draw:name=\"f1\" draw:formula=\"min($1, max(height, atan2(1, 2) * 100)) + cos(0) * tan(0.1) + atan(1)\"/>" +
                       "</draw:enhanced-geometry></draw:custom-shape>";
        var table = "<draw:frame svg:x=\"1cm\" svg:y=\"8cm\" svg:width=\"10cm\" svg:height=\"3cm\"><table:table>" +
                    "<table:table-column table:style-name=\"co1\" table:number-columns-repeated=\"3\"/>" +
                    "<table:table-row table:number-rows-repeated=\"3\"><table:table-cell table:style-name=\"ce1\" table:number-columns-spanned=\"2\"><text:p>cell</text:p></table:table-cell>" +
                    "<table:covered-table-cell/><table:table-cell table:number-columns-repeated=\"2\"><text:p>x</text:p></table:table-cell></table:table-row></table:table></draw:frame>";
        var builder = new OdpBuilder
        {
            Styles = "<style:default-style style:family=\"graphic\"><style:graphic-properties draw:fill-color=\"#729fcf\"/><style:text-properties fo:font-size=\"18pt\"/></style:default-style>" +
                     "<draw:gradient draw:name=\"g1\" draw:style=\"radial\" draw:cx=\"30%\" draw:cy=\"40%\" draw:start-color=\"#ff0000\" draw:end-color=\"#00ff00\" draw:border=\"10%\"/>" +
                     "<draw:hatch draw:name=\"h1\" draw:style=\"single\" draw:rotation=\"450\" draw:color=\"#000000\"/>" +
                     "<draw:marker draw:name=\"Circle\" svg:viewBox=\"0 0 10 10\" svg:d=\"M0 0h10v10h-10z\"/>" +
                     "<draw:stroke-dash draw:name=\"d1\" draw:style=\"round\" draw:dots1=\"2\" draw:dots1-length=\"200%\" draw:distance=\"0.1cm\"/>" +
                     "<style:style style:name=\"Default-outline1\" style:family=\"presentation\"><style:graphic-properties><text:list-style style:name=\"L1\">" +
                     "<text:list-level-style-number text:level=\"1\" style:num-format=\"I\" style:num-suffix=\".\"/><text:list-level-style-bullet text:level=\"2\" text:bullet-char=\"-\"/>" +
                     "</text:list-style></style:graphic-properties></style:style>" +
                     "<style:style style:name=\"Default-outline2\" style:family=\"presentation\" style:parent-style-name=\"Default-outline1\"/>",
            StylesAutomatic = "<style:style style:name=\"Mdp1\" style:family=\"drawing-page\"><style:drawing-page-properties draw:fill=\"gradient\" draw:fill-gradient-name=\"g1\"/></style:style>",
            ContentStyles = OdpBuilder.GraphicStyle("gr1", "draw:fill=\"hatch\" draw:fill-hatch-name=\"h1\" draw:stroke=\"dash\" draw:stroke-dash=\"d1\" draw:marker-start=\"Circle\" draw:textarea-vertical-align=\"middle\"") +
                            "<style:style style:name=\"dp1\" style:family=\"drawing-page\"><style:drawing-page-properties presentation:display-footer=\"true\" presentation:display-page-number=\"true\"/></style:style>" +
                            "<style:style style:name=\"co1\" style:family=\"table-column\"><style:table-column-properties style:column-width=\"3cm\"/></style:style>" +
                            "<style:style style:name=\"ce1\" style:family=\"table-cell\"><style:graphic-properties draw:fill=\"solid\" draw:fill-color=\"#eeeeee\"/><style:paragraph-properties fo:border=\"0.5pt solid #000000\"/></style:style>",
            MasterStyles = "<style:master-page style:name=\"Default\" style:page-layout-name=\"PM1\" draw:style-name=\"Mdp1\">" +
                           "<draw:frame presentation:class=\"outline\" presentation:style-name=\"Default-outline1\" svg:x=\"1cm\" svg:y=\"3cm\" svg:width=\"20cm\" svg:height=\"8cm\"><draw:text-box/></draw:frame>" +
                           "<draw:frame presentation:class=\"page-number\" svg:x=\"20cm\" svg:y=\"14cm\" svg:width=\"5cm\" svg:height=\"1cm\"><draw:text-box><text:p><text:page-number/>/<text:page-count/></text:p></draw:text-box></draw:frame>" +
                           "<draw:frame presentation:class=\"footer\" svg:x=\"1cm\" svg:y=\"14cm\" svg:width=\"5cm\" svg:height=\"1cm\"><draw:text-box><text:p><presentation:footer/></text:p></draw:text-box></draw:frame>" +
                           "<draw:ellipse svg:x=\"25cm\" svg:y=\"0cm\" svg:width=\"3cm\" svg:height=\"3cm\" draw:kind=\"cut\" draw:start-angle=\"30\" draw:end-angle=\"300\"/>" +
                           "</style:master-page>",
            Pages = "<presentation:footer-decl presentation:name=\"f1\">footer</presentation:footer-decl>" +
                    "<draw:page draw:name=\"a\" draw:master-page-name=\"Default\" draw:style-name=\"dp1\" presentation:use-footer-name=\"f1\">" +
                    "<draw:frame presentation:class=\"outline\" presentation:style-name=\"Default-outline1\"><draw:text-box><text:list>" +
                    "<text:list-item><text:p>one <text:span>span</text:span><text:tab/><text:line-break/><text:a xlink:href=\"https://example.invalid/\">link</text:a></text:p>" +
                    "<text:list><text:list-item><text:p>two</text:p></text:list-item></text:list></text:list-item>" +
                    "<text:list-item text:start-value=\"5\"><text:p>five<text:s text:c=\"3\"/>!</text:p></text:list-item></text:list></draw:text-box></draw:frame>" +
                    "<draw:g>" + geometry + "<draw:g><draw:polygon svg:width=\"2cm\" svg:height=\"2cm\" svg:viewBox=\"0 0 10 10\" draw:points=\"0,0 10,0 5,10\"/>" +
                    "<draw:path svg:x=\"3cm\" svg:width=\"2cm\" svg:height=\"2cm\" svg:viewBox=\"0 0 10 10\" svg:d=\"M0 0 C1 1 2 2 3 3 S5 5 6 6 Q7 7 8 8 T9 9 A2 3 30 1 0 5 5 H1 V1 Z m1 1 l2 2 z\"/></draw:g></draw:g>" +
                    "<draw:line draw:style-name=\"gr1\" svg:x1=\"1cm\" svg:y1=\"12cm\" svg:x2=\"9cm\" svg:y2=\"13cm\"/>" +
                    "<draw:connector svg:x1=\"1cm\" svg:y1=\"1cm\" svg:x2=\"5cm\" svg:y2=\"5cm\"/>" +
                    picture + table + "</draw:page>" +
                    "<draw:page draw:name=\"b\" draw:master-page-name=\"Default\"><draw:custom-shape svg:width=\"3cm\" svg:height=\"3cm\"><draw:enhanced-geometry draw:type=\"star5\"/></draw:custom-shape></draw:page>",
        };
        if (!flat)
            builder.Parts["Pictures/p.png"] = OdpBuilder.Png(4, 4, SkiaSharp.SKColors.Teal);
        return builder;
    }
}
