// Mutation fuzzing of Filee's EMF / WMF renderer (Metafiles/): seeds written in code (shapes, pens and brushes, a mono
// pattern brush, clip rectangles and a clip path, a bitmap, text; triangle gradients; a WMF with a clip rectangle), changed by
// BinaryFuzzer. Every input must be drawn and rasterized, or refused with InvalidDataException,
// ConversionRefusedException or NotSupportedException (EMF+ only), within the time limit and on a 1 MB stack. Same
// switches as DocumentFuzzTests: a short fixed run normally, FILEE_FUZZ_SECONDS for a longer random one, FILEE_FUZZ_SEED
// to repeat, FILEE_FUZZ_OUT to keep failing inputs.

using System.Globalization;
using Filee.Engines.Metafiles;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class MetafileFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const int QuickIterations = 150;
    private const uint Red = 0x0000FF, Green = 0x00FF00, Blue = 0xFF0000; // COLORREF: 0x00BBGGRR

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void Emf_player_draws_or_refuses_every_mutated_file() =>
        Fuzz("emf", [RichEmf(), MetafileTests.GradientTriangles(96, 4)]);

    [Fact]
    public void Wmf_player_draws_or_refuses_every_mutated_file() =>
        Fuzz("wmf", [RichWmf()]);

    /// <summary>An EMF with a record of most kinds the player draws.</summary>
    private static byte[] RichEmf()
    {
        var emf = new EmfBuilder(192, 96)
            .Pen(1, 0, 2, Blue).Select(1)
            .Brush(2, Red).Select(2)
            .Rectangle(4, 4, 60, 60)
            .Ellipse(64, 4, 120, 60)
            .Polygon((130, 4), (188, 30), (140, 60));
        emf = MetafileTests.MonoBrush(emf, 3, 8, 8, y => (byte)(y % 2 == 0 ? 0x55 : 0xAA))
            .Select(3)
            .Add(24, (int)Green) // SETTEXTCOLOR
            .Add(33) // SAVEDC
            .Add(30, 0, 60, 96, 96) // INTERSECTCLIPRECT
            .Add(29, 10, 70, 20, 80) // EXCLUDECLIPRECT
            .Rectangle(0, 60, 96, 96)
            .Add(34, -1) // RESTOREDC
            .Add(59).Ellipse(100, 64, 140, 92).Add(60) // BEGINPATH … ENDPATH
            .Add(67, 1) // SELECTCLIPPATH, RGN_AND
            .Brush(4, Green).Select(4)
            .Rectangle(96, 60, 192, 96)
            .StretchDib(150, 64, 16, 16, new uint[,] { { 0xFF0000, 0x00FF00 }, { 0x0000FF, 0xFFFFFF } })
            .Font(5, 12, "Arial").Select(5)
            .Text(4, 80, "Filee");
        return emf.ToArray();
    }

    /// <summary>A WMF with shapes, a pen and brush, and a clip rectangle.</summary>
    private static byte[] RichWmf() => new WmfBuilder(192, 96)
        .Pen(0, 2, Blue).Select(0)
        .Brush(Red).Select(1)
        .Rectangle(8, 8, 88, 88)
        .Polygon((100, 8), (180, 40), (110, 88))
        .Add(0x0416, 60, 140, 20, 100) // INTERSECTCLIPRECT (bottom, right, top, left)
        .Add(0x0418, 60, 140, 20, 100) // ELLIPSE
        .ToArray();

    /// <summary>Drawn as for a PNG at 96 dpi, and rasterized when the (perhaps damaged) frame is a sensible size.</summary>
    private static void Draw(byte[] data)
    {
        try
        {
            using var drawing = Metafile.Draw(data, 96f / 72, cancellationToken: CancellationToken.None);
            if (drawing.Width * drawing.Height < 1_000_000)
                Metafile.Rasterize(drawing, 96f / 72).Dispose();
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidDataException("Refused: EMF+ only.", ex);
        }
    }

    private void Fuzz(string format, List<byte[]> seeds)
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("FILEE_FUZZ_SECONDS"), out var s) && s > 0 ? s : 0;
        var seed = int.TryParse(Environment.GetEnvironmentVariable("FILEE_FUZZ_SEED"), out var fixedSeed) ? fixedSeed
            : seconds > 0 ? Environment.TickCount : 20261114;
        var outFolder = Environment.GetEnvironmentVariable("FILEE_FUZZ_OUT") is { Length: > 0 } o ? o : null;
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"Seed {seed}, {(seconds > 0 ? seconds + " s" : QuickIterations + " inputs per seed")}");
        var random = new Random(seed);
        var fuzzers = seeds.Select(bytes => new BinaryFuzzer(bytes, random)).ToList();

        // Every seed itself draws without trouble.
        foreach (var bytes in seeds)
            Assert.Equal(DocumentFuzzer.Outcome.Read, DocumentFuzzer.Run(() => Draw(bytes), ReadTimeout));

        var failures = new Dictionary<string, string>();
        var (inputs, refused) = (0, 0);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (seconds > 0 ? stopwatch.Elapsed < TimeSpan.FromSeconds(seconds) : inputs < QuickIterations * seeds.Count)
        {
            var bytes = fuzzers[inputs % fuzzers.Count].Next();
            inputs++;
            try
            {
                if (DocumentFuzzer.Run(() => Draw(bytes), ReadTimeout) == DocumentFuzzer.Outcome.Refused)
                    refused++;
            }
            catch (DocumentFuzzer.FuzzFailure failure)
            {
                // One input per kind of failure (exception type and the frame it came from).
                var frame = failure.InnerException?.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
                var kind = $"{failure.InnerException?.GetType().Name ?? "Timeout"} {frame}";
                if (!failures.ContainsKey(kind))
                {
                    var kept = Path.Combine(outFolder ?? fx.NewFolder(), $"metafile-{seed}-{inputs}.{format}");
                    File.WriteAllBytes(kept, bytes);
                    failures[kind] = $"{failure.Message} (input {kept})\n{failure.InnerException}";
                }
                if (failure.InnerException is null)
                    break; // a draw that hangs keeps running; don't pile them up
            }
        }
        output?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{inputs} inputs, {refused} refused, {failures.Count} kinds of failure"));
        Assert.True(failures.Count == 0, $"Seed {seed}: " + string.Join("\n\n", failures.Values));
    }
}
