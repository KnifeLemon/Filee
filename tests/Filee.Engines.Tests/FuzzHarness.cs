// The mutation fuzz loop every reader's fuzz tests share (DocumentFuzzTests, ArchiveFuzzTests, OdtFuzzTests, ...):
// seeds built in code, a mutator per seed (DocumentFuzzer for packages, HwpFuzzer, DocFuzzer, BinaryFuzzer, TextFuzzer,
// XmlFuzzer, JsonFuzzer, or the test's own), and every input must be read or refused with InvalidDataException /
// ConversionRefusedException, in time and on a 1 MB stack (DocumentFuzzer.Run).
//
// In a normal test run each target gets a short, fixed run (QuickIterations inputs per seed, the same inputs every
// time). The nightly fuzz workflow sets FILEE_FUZZ_SECONDS for a longer run with a new random seed, and FILEE_FUZZ_OUT
// for a folder that keeps the inputs that failed (and, for file readers, the input being read, which a crash that ends
// the process leaves behind). FILEE_FUZZ_SEED repeats a run.

using System.Globalization;

namespace Filee.Engines.Tests;

/// <summary>Runs fuzz targets for one test class.</summary>
/// <param name="quickIterations">Inputs per seed in a normal test run.</param>
/// <param name="quickSeed">The random seed of a normal test run.</param>
internal sealed class FuzzHarness(EngineFixture fx, int quickIterations, int quickSeed)
{
    /// <summary>How long one input may take to read.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Every seed itself must read (not only not fail); off for seeds that are refused on purpose.</summary>
    public bool SeedsMustRead { get; init; } = true;

    /// <summary>The mutator of binary data: flipped bits, overwritten and repeated runs, a cut end.</summary>
    public static Func<byte[], Random, Func<byte[]>> Bytes { get; } = (seed, random) => new BinaryFuzzer(seed, random).Next;

    /// <summary>The mutator of ZIP packages: parts changed as XML or bytes, dropped, swapped.</summary>
    public static Func<byte[], Random, Func<byte[]>> Packages { get; } = (seed, random) => new DocumentFuzzer(seed, random).Next;

    /// <summary>The mutator of a text format, with the format's own tokens.</summary>
    public static Func<byte[], Random, Func<byte[]>> Text(string[] tokens) => (seed, random) => new TextFuzzer(seed, random, tokens).Next;

    /// <summary>The mutator of an XML format in one file: mostly the tree, sometimes the text.</summary>
    public static Func<byte[], Random, Func<byte[]>> Xml(string[] tokens) => (seed, random) => new XmlFuzzer(seed, random, tokens).Next;

    /// <summary>The mutator of a JSON format: mostly the tree, sometimes the text.</summary>
    public static Func<byte[], Random, Func<byte[]>> Json(string[] tokens) => (seed, random) => new JsonFuzzer(seed, random, tokens).Next;

    /// <summary>
    /// Fuzzes a reader of files: each input is written to a file (<c>running-{name}.{extension}</c>) and read with
    /// <paramref name="read"/>(path, media folder), the media folder emptied before each input.
    /// </summary>
    /// <param name="name">Names the kept inputs.</param>
    public void Files(string name, string extension, IReadOnlyList<byte[]> seeds, Func<byte[], Random, Func<byte[]>> mutator,
        Action<string, string> read)
    {
        var folder = fx.NewFolder();
        // With an output folder the input being read lives there: a crash that ends the process (a stack overflow)
        // leaves it behind for the report. A run that ends normally removes it.
        var input = Path.Combine(OutFolder ?? folder, $"running-{name}.{extension}");
        var media = Path.Combine(folder, "media");
        Run(name, extension, seeds, mutator, bytes =>
        {
            File.WriteAllBytes(input, bytes);
            if (Directory.Exists(media))
                Directory.Delete(media, recursive: true);
            return () => read(input, media);
        }, timedOut =>
        {
            if (!timedOut)
                File.Delete(input); // a read that still runs holds it open
        });
    }

    /// <summary>Fuzzes something that reads bytes in memory: each input goes to <paramref name="read"/>.</summary>
    /// <param name="name">Names the kept inputs.</param>
    public void Data(string name, string extension, IReadOnlyList<byte[]> seeds, Func<byte[], Random, Func<byte[]>> mutator, Action<byte[]> read) =>
        Run(name, extension, seeds, mutator, bytes => () => read(bytes), null);

    private static string? OutFolder => Environment.GetEnvironmentVariable("FILEE_FUZZ_OUT") is { Length: > 0 } o ? o : null;

    /// <param name="load">Gets an input ready and returns its read (timed on its own thread).</param>
    /// <param name="finished">Called before the result is asserted; true when a read timed out (and may still run).</param>
    private void Run(string name, string extension, IReadOnlyList<byte[]> seeds, Func<byte[], Random, Func<byte[]>> mutator,
        Func<byte[], Action> load, Action<bool>? finished)
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("FILEE_FUZZ_SECONDS"), out var s) && s > 0 ? s : 0;
        var seed = int.TryParse(Environment.GetEnvironmentVariable("FILEE_FUZZ_SEED"), out var fixedSeed) ? fixedSeed
            : seconds > 0 ? Environment.TickCount : quickSeed;
        var keep = OutFolder ?? fx.NewFolder();
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name}: seed {seed}, {(seconds > 0 ? seconds + " s" : quickIterations + " inputs per seed")}"));

        var random = new Random(seed);
        var fuzzers = seeds.Select(bytes => mutator(bytes, random)).ToList();

        // Every seed itself reads without trouble.
        foreach (var bytes in seeds)
        {
            var outcome = DocumentFuzzer.Run(load(bytes), Timeout);
            if (SeedsMustRead)
                Assert.Equal(DocumentFuzzer.Outcome.Read, outcome);
        }

        var failures = new Dictionary<string, string>();
        var (inputs, refused) = (0, 0);
        var timedOut = false;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (seconds > 0 ? stopwatch.Elapsed < TimeSpan.FromSeconds(seconds) : inputs < quickIterations * seeds.Count)
        {
            var bytes = fuzzers[inputs % fuzzers.Count]();
            inputs++;
            var read = load(bytes);
            try
            {
                if (DocumentFuzzer.Run(read, Timeout) == DocumentFuzzer.Outcome.Refused)
                    refused++;
            }
            catch (DocumentFuzzer.FuzzFailure failure)
            {
                // One input per kind of failure (exception type and the frame it came from).
                var frame = failure.InnerException?.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
                var kind = $"{failure.InnerException?.GetType().Name ?? "Timeout"} {frame}";
                if (!failures.ContainsKey(kind))
                {
                    var kept = Path.Combine(keep, string.Create(CultureInfo.InvariantCulture, $"{name}-{seed}-{inputs}.{extension}"));
                    File.WriteAllBytes(kept, bytes);
                    failures[kind] = $"{failure.Message} (input {kept})\n{failure.InnerException}";
                }
                if (failure.InnerException is null)
                {
                    timedOut = true;
                    break; // a read that hangs keeps running; don't pile them up
                }
            }
        }
        output?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{inputs} inputs, {refused} refused, {inputs - refused - failures.Count} read, {failures.Count} kinds of failure"));
        finished?.Invoke(timedOut);
        Assert.True(failures.Count == 0, $"Seed {seed}: " + string.Join("\n\n", failures.Values));
    }
}
