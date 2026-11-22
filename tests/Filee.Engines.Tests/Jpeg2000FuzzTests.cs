// Mutation fuzzing of Filee's JPEG 2000 decoder (Jpeg2000/): the seeds are codestreams Jpeg2000TestFile builds — every
// code-block style with tiles, two layers with SOP / EPH markers and PPT headers, empty packets over levels,
// precincts, three components and progression order changes — and, where Magick.NET writes JPEG 2000, a JP2 file of
// OpenJPEG's with five resolutions and three layers. Every mutated file must decode or be refused with
// InvalidDataException (or the usual limit refusals, or NotSupportedException for Part 2 / Part 15 features), in time
// and on a 1 MB stack. Run by FuzzHarness: a short fixed run normally, FILEE_FUZZ_SECONDS for a longer random one,
// FILEE_FUZZ_SEED to repeat, FILEE_FUZZ_OUT to keep failing inputs.

using Filee.Core.Conversion;
using Filee.Engines.Jpeg2000;
using ImageMagick;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class Jpeg2000FuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Inputs per seed in a normal test run.</summary>
    private const int QuickIterations = 150;

    /// <summary>Small limits, so a mutated size field is refused rather than allocated.</summary>
    private static readonly Jpeg2000Limits Limits = new() { MaxPixels = 4_000_000, MaxSamples = 8_000_000, MaxCodeBlocks = 200_000, MaxPackets = 1_000_000 };

    private static List<byte[]> Seeds()
    {
        var pixels = Jpeg2000DecoderTests.Pixels(45, 37, 1);
        List<byte[]> seeds =
        [
            Jpeg2000TestFile.Grey(45, 37, pixels, new Jpeg2000TestFile.Layout
            {
                Style = Jpeg2000TestFile.Bypass | Jpeg2000TestFile.Causal | Jpeg2000TestFile.Symbols | Jpeg2000TestFile.Reset,
                TileWidth = 32,
                TileHeight = 16,
            }),
            Jpeg2000TestFile.Grey(45, 37, pixels, new Jpeg2000TestFile.Layout { Style = Jpeg2000TestFile.TermAll, TwoLayers = true, SopEph = true, Ppt = true }),
            Jpeg2000TestFile.EmptyPackets(new()
            {
                Width = 90, Height = 70, TileWidth = 90, TileHeight = 70, Levels = 3, Precinct = 4, Components = 3, Layers = 2, Order = 3, PacketBytes = 300,
                Changes = [(0, 0, 1, 4, 3, 2), (0, 0, 2, 4, 3, 4)],
            }),
        ];
        if (MagickFormatInfo.Create(MagickFormat.Jp2) is { SupportsWriting: true })
        {
            using var image = new MagickImage(Jpeg2000DecoderTests.Pixels(40, 30, 3), new PixelReadSettings(40, 30, StorageType.Char, "RGB"));
            image.Settings.SetDefine(MagickFormat.Jp2, "quality", "30,40,50");
            image.Settings.SetDefine(MagickFormat.Jp2, "number-resolutions", "5");
            seeds.Add(image.ToByteArray(MagickFormat.Jp2));
        }
        return seeds;
    }

    [Fact]
    public void Jpeg2000_decoder_reads_or_refuses_every_mutated_file() =>
        new FuzzHarness(fx, QuickIterations, 20261122).Data("jpeg2000", "jp2", Seeds(), FuzzHarness.Bytes, bytes =>
        {
            try
            {
                Jpeg2000Decoder.Decode(bytes, Limits).Rgba();
            }
            catch (Exception ex) when (ex is NotSupportedException or ConversionRefusedException)
            {
                // A Part 2 or Part 15 feature, or past the limits: refused like damage.
                throw new InvalidDataException(ex.Message, ex);
            }
        });
}
