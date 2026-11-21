// Mutation fuzzing of Filee's JPEG XR decoder (JpegXr/): the seeds are a picture of Windows' own encoder (on Windows:
// frequency order, overlap filtering, interleaved alpha, every band) and the minimal codestream of JpegXrTestFile.
// Every mutated file must decode or be refused with InvalidDataException, in time and on a 1 MB stack. Run by
// FuzzHarness: a short fixed run normally, FILEE_FUZZ_SECONDS for a longer random one, FILEE_FUZZ_SEED to repeat,
// FILEE_FUZZ_OUT to keep failing inputs.

using Filee.Engines.JpegXr;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class JpegXrFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Inputs per seed in a normal test run.</summary>
    private const int QuickIterations = 200;

    [Fact]
    public void JpegXr_decoder_reads_or_refuses_every_mutated_file() =>
        new FuzzHarness(fx, QuickIterations, 20261121).Data("jpegxr", "jxr", [JpegXrDecoderTests.Seed(), JpegXrTestFile.DcOnlyGrey()], FuzzHarness.Bytes, bytes =>
        {
            try
            {
                JpegXrDecoder.Decode(bytes, 1 << 22);
            }
            catch (NotSupportedException ex)
            {
                // A later codestream version or more components than Filee reads: refused like damage.
                throw new InvalidDataException(ex.Message, ex);
            }
        });
}
