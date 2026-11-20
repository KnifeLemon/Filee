// Mutation fuzzing of the flat OpenDocument reader and writer (Odf/OdfFlat through OdfVariants): the seeds are the
// test document of OdtTests and a spreadsheet as flat files (XmlFuzzer, with tokens for pictures, objects and styles),
// and the test document as a package turned into a flat file (DocumentFuzzer). Every input must be converted or
// refused with InvalidDataException / ConversionRefusedException, in time and on a 1 MB stack, and what a flat file
// becomes must read. Run by FuzzHarness: a short fixed run normally, FILEE_FUZZ_SECONDS for a longer random one,
// FILEE_FUZZ_SEED to repeat, FILEE_FUZZ_OUT to keep failing inputs.

using Filee.Engines.Hwp.Hwpx.Odt;
using Filee.Engines.Odf;
using Filee.Engines.Office;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class OdfVariantFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Inputs per seed in a normal test run.</summary>
    private const int QuickIterations = 40;

    private static readonly string[] Tokens =
    [
        "<office:binary-data>iVBORw0KGgo=</office:binary-data>", "<office:binary-data>@@</office:binary-data>",
        "<draw:frame><draw:image><office:binary-data>R0lGODlhAQABAAAAACw=</office:binary-data></draw:image></draw:frame>",
        "<draw:object><office:document><office:body><office:text><text:p>x</text:p></office:text></office:body></office:document></draw:object>",
        "<office:document>", "</office:document>", " xlink:href=\"Pictures/x.png\"", " xlink:href=\"../../x\"", " xlink:href=\"./Object 1\"",
        "<office:automatic-styles><style:style style:name=\"P1\" style:family=\"paragraph\"/></office:automatic-styles>",
        " office:mimetype=\"application/vnd.oasis.opendocument.spreadsheet\"", " text:style-name=\"P1\"", "<office:meta/>",
        " table:number-columns-repeated=\"100000\"", "&amp;", "<![CDATA[x]]>",
    ];

    [Fact]
    public void Flat_files_convert_or_are_refused_after_any_mutation()
    {
        var folder = fx.NewFolder();
        var odt = OdtTests.DocumentBuilder().Save(Path.Combine(folder, "seed.odt"));
        var fodt = Path.Combine(folder, "seed.fodt");
        OdfVariants.Convert(odt, "odt", fodt, "fodt", cancellationToken: TestContext.Current.CancellationToken);
        new FuzzHarness(fx, QuickIterations, 20261120).Files("fodt", "fodt", [File.ReadAllBytes(fodt)], FuzzHarness.Xml(Tokens), (path, media) =>
        {
            var package = Path.Combine(Path.GetDirectoryName(path)!, "flat.odt");
            OdfVariants.Convert(path, "fodt", package, "odt");
            OdtReader.Read(package, media);
            File.Delete(package);
        });
    }

    [Fact]
    public void Flat_spreadsheets_read_or_are_refused_after_any_mutation()
    {
        const string Sheet =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document " + OdtBuilder.Namespaces + " office:version=\"1.3\" office:mimetype=\"application/vnd.oasis.opendocument.spreadsheet\">" +
            "<office:automatic-styles><style:style style:name=\"ce1\" style:family=\"table-cell\"><style:text-properties fo:font-weight=\"bold\"/></style:style></office:automatic-styles>" +
            "<office:body><office:spreadsheet><table:table table:name=\"Data\"><table:table-row><table:table-cell table:style-name=\"ce1\" office:value-type=\"string\"><text:p>Flat cell</text:p></table:table-cell>" +
            "<table:table-cell office:value-type=\"float\" office:value=\"42\"><text:p>42</text:p></table:table-cell></table:table-row></table:table></office:spreadsheet></office:body></office:document>";
        new FuzzHarness(fx, QuickIterations, 20261120).Files("fods", "fods", [System.Text.Encoding.UTF8.GetBytes(Sheet)], FuzzHarness.Xml(Tokens),
            (path, _) => SpreadsheetConverter.Read(path, "fods"));
    }

    [Fact]
    public void Packages_become_flat_files_or_are_refused_after_any_mutation()
    {
        var seed = File.ReadAllBytes(OdtTests.DocumentBuilder().Save(Path.Combine(fx.NewFolder(), "seed.odt")));
        new FuzzHarness(fx, QuickIterations, 20261120).Files("odt-flat", "odt", [seed], FuzzHarness.Packages, (path, _) =>
        {
            var flat = Path.Combine(Path.GetDirectoryName(path)!, "package.fodt");
            OdfVariants.Convert(path, "odt", flat, "fodt");
            File.Delete(flat);
        });
    }
}
