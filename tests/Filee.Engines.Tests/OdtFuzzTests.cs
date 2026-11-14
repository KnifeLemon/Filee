// Mutation fuzzing of the ODT reader (Hwp/Hwpx/Odt/OdtReader): the seeds are the test document of OdtTests and a
// package with what costs the reader the most (repeated rows and cells, text:s counts, pictures stored in the XML,
// a formula object, links in links, notes and frames in cells), written in code. DocumentFuzzer changes them, and
// every input must be read or refused with InvalidDataException / ConversionRefusedException, in time and on a 1 MB
// stack. Run by FuzzHarness: a short fixed run normally, FILEE_FUZZ_SECONDS for a longer random one,
// FILEE_FUZZ_SEED to repeat, FILEE_FUZZ_OUT to keep failing inputs.

using Filee.Engines.Hwp.Hwpx.Odt;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class OdtFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Inputs per seed in a normal test run.</summary>
    private const int QuickIterations = 50;

    [Fact]
    public void OdtReader_reads_or_refuses_every_mutated_package() =>
        Fuzz([Seed(OdtTests.DocumentBuilder()), Seed(RepeatsBuilder())], (path, media) => OdtReader.Read(path, media));

    /// <summary>Tables with repeated rows, cells and columns, and the inline content that repeats with them.</summary>
    private static OdtBuilder RepeatsBuilder()
    {
        var png = Convert.ToBase64String(DocumentFuzzTests.TinyPng(3, 2));
        var picture = "<draw:frame text:anchor-type=\"as-char\" svg:width=\"1cm\" svg:height=\"0.5cm\"><draw:image><office:binary-data>" +
                      png + "</office:binary-data></draw:image></draw:frame>";
        var formula = "<draw:frame text:anchor-type=\"as-char\" svg:width=\"2cm\" svg:height=\"1cm\"><draw:object xlink:href=\"./Object 1\"/></draw:frame>";
        var builder = new OdtBuilder
        {
            ContentStyles =
                "<style:style style:name=\"T1\" style:family=\"text\"><style:text-properties fo:font-weight=\"bold\"/></style:style>" +
                "<style:style style:name=\"C1\" style:family=\"table-cell\"><style:table-cell-properties fo:background-color=\"#eeeeee\" fo:border=\"0.5pt solid #000000\" fo:padding=\"0.1cm\"/></style:style>" +
                "<style:style style:name=\"Col1\" style:family=\"table-column\"><style:table-column-properties style:column-width=\"2cm\" style:rel-column-width=\"1000*\"/></style:style>",
            Body =
                "<text:h text:outline-level=\"1\">Repeats</text:h>" +
                "<text:p>a<text:s text:c=\"5\"/>b<text:span text:style-name=\"T1\">bold<text:s/>text</text:span>" +
                "<text:a xlink:href=\"https://example.invalid/\">outer <text:a xlink:href=\"#inner\">inner</text:a></text:a>" +
                "<text:note text:note-class=\"footnote\"><text:note-citation>1</text:note-citation><text:note-body><text:p>note</text:p></text:note-body></text:note></text:p>" +
                "<table:table><table:table-column table:style-name=\"Col1\" table:number-columns-repeated=\"4\"/>" +
                "<table:table-header-rows><table:table-row><table:table-cell table:style-name=\"C1\" table:number-columns-repeated=\"4\"><text:p>head</text:p></table:table-cell></table:table-row></table:table-header-rows>" +
                $"<table:table-row table:number-rows-repeated=\"3\"><table:table-cell table:number-columns-spanned=\"2\"><text:p>{picture}<text:s text:c=\"3\"/>x</text:p></table:table-cell>" +
                "<table:covered-table-cell/><table:table-cell table:number-columns-repeated=\"2\"><text:p>y</text:p></table:table-cell></table:table-row>" +
                "<table:table-row table:number-rows-repeated=\"2\"><table:table-cell/><table:table-cell table:number-columns-repeated=\"3\"/></table:table-row>" +
                $"<table:table-row><table:table-cell><text:p>{formula}</text:p></table:table-cell><table:table-cell table:number-columns-repeated=\"3\"/></table:table-row>" +
                "<table:table-row table:number-rows-repeated=\"1000\"><table:table-cell table:number-columns-repeated=\"1024\"/></table:table-row></table:table>" +
                $"<text:p>{picture}{picture}</text:p>",
        };
        builder.Parts["Object 1/content.xml"] = System.Text.Encoding.UTF8.GetBytes(
            "<math xmlns=\"http://www.w3.org/1998/Math/MathML\"><semantics><mrow><mi>a</mi><mo>+</mo><mi>b</mi></mrow>" +
            "<annotation encoding=\"StarMath 5.0\">a + b</annotation></semantics></math>");
        return builder;
    }

    private byte[] Seed(OdtBuilder builder) => File.ReadAllBytes(builder.Save(Path.Combine(fx.NewFolder(), "seed.odt")));

    private void Fuzz(List<byte[]> seeds, Action<string, string> read) =>
        new FuzzHarness(fx, QuickIterations, 20261114).Files("odt", "odt", seeds, FuzzHarness.Packages, read);
}
