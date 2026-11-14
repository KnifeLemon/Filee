// Fuzzing the ODP writer: mutated ODP packages (OdfDrawTests' decks) and mutated PowerPoint decks (OdpWriterTests'
// sample, through PptxSlideReader and its notes) are read; whatever reads is written as ODP and read back by Filee's
// ODP reader. Reading may refuse an input (InvalidDataException / ConversionRefusedException); a deck that was read
// must be written and read again. Run by FuzzHarness: a short fixed run normally, FILEE_FUZZ_SECONDS for a longer
// random one, FILEE_FUZZ_SEED to repeat, FILEE_FUZZ_OUT to keep failing inputs.

using Filee.Engines.Odf.Draw;
using Filee.Engines.Slides;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class OdpWriterFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Inputs per seed in a normal test run.</summary>
    private const int QuickIterations = 30;

    [Fact]
    public void Every_odp_that_reads_is_written_and_reads_back() =>
        new FuzzHarness(fx, QuickIterations, 20261114).Files("odp-writer", "odp",
            [Seed(OdfDrawTests.TextDeck(), "seed.odp"), Seed(OdfDrawTests.ShapeDeck(), "seed.odp")], FuzzHarness.Packages,
            (path, media) => WriteBack(OdfDrawReader.Read(path), null, media));

    [Fact]
    public void Every_pptx_that_reads_is_written_with_its_notes_and_reads_back() =>
        new FuzzHarness(fx, QuickIterations, 20261114).Files("odp-writer-pptx", "pptx",
            [File.ReadAllBytes(OdpWriterTests.Sample().Save(Path.Combine(fx.NewFolder(), "seed.pptx")))], FuzzHarness.Packages,
            (path, media) => WriteBack(PptxSlideReader.Read(path), PptxNotes.Read(path), media));

    private byte[] Seed(OdpBuilder builder, string name) => File.ReadAllBytes(builder.Save(Path.Combine(fx.NewFolder(), name)));

    private static void WriteBack(SlideDeck deck, IReadOnlyList<IReadOnlyList<string>>? notes, string folder)
    {
        Directory.CreateDirectory(folder);
        var output = Path.Combine(folder, "written.odp");
        OdpWriter.Write(deck, output, notes);
        var back = OdfDrawReader.Read(output);
        Assert.Equal(deck.Slides.Count, back.Slides.Count);
    }
}
