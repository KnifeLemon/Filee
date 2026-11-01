// Mutation fuzzing of Filee's own archive readers (ar / DEB, cpio, RPM, ISO 9660, Unix compress, lzip, ALZ, EGG):
// seeds are built in code, bytes are flipped, overwritten, cut and repeated, and every input must unpack or be
// refused with InvalidDataException, in time. Same switches as DocumentFuzzTests: a short fixed run normally,
// FILEE_FUZZ_SECONDS for a longer random one, FILEE_FUZZ_SEED to repeat, FILEE_FUZZ_OUT to keep failing inputs.

using System.Globalization;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;
using static Filee.Engines.Tests.ArchiveFormatBuilders;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class ArchiveFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const int QuickIterations = 60;

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private static List<TestEntry> Seed() =>
    [
        new("폴더/", null),
        new("폴더/a.txt", Words(3_000)),
        new("b.bin", Noise(2_000, 4)),
        new("c.txt", "small"u8.ToArray()),
    ];

    [Fact]
    public void Ar_reader_unpacks_or_refuses_every_mutated_archive() =>
        Fuzz("deb", [Deb(Gzip(Tar(Seed())), Words(1000)), Ar([("a-member-with-a-long-name.txt", Words(500)), ("x", [1])])]);

    [Fact]
    public void Cpio_reader_unpacks_or_refuses_every_mutated_archive() =>
        Fuzz("cpio", [Cpio(Seed()), Cpio(Seed(), CpioLayout.Odc), Cpio(Seed(), CpioLayout.Binary), Cpio(Seed(), CpioLayout.Crc), CpioWithHardLinks(Words(100))]);

    [Fact]
    public void Rpm_reader_unpacks_or_refuses_every_mutated_package() =>
        Fuzz("rpm", [Rpm(Gzip(Cpio(Seed()))), Rpm(Cpio(Seed()))]);

    [Fact]
    public void Iso_reader_unpacks_or_refuses_every_mutated_image() =>
        Fuzz("iso", [Iso(Seed(), IsoNames.Joliet), Iso(Seed(), IsoNames.RockRidge, symlink: "a"), Iso(Seed(), IsoNames.Plain)], mutateFrom: 16 * 2048);

    [Fact]
    public void Unix_compress_reader_unpacks_or_refuses_every_mutated_file() =>
        Fuzz("z", [UnixCompress(Words(20_000)), UnixCompress(Noise(5_000, 1), maxBits: 9)]);

    [Fact]
    public void Lzip_alz_and_egg_readers_unpack_or_refuse_every_mutated_file()
    {
        Fuzz("lz", [Lzip(Words(20_000))]);
        var folder = fx.NewFolder();
        var alz = Path.Combine(folder, "seed.alz");
        WriteAlz(alz, [new("폴더\\", null), new("폴더\\a.txt", Words(2000), Method: 2), new("b.txt", Words(1000), Method: 1, Packed: ToAlzBzip2(Bzip2(Words(1000))))]);
        Fuzz("alz", [File.ReadAllBytes(alz)]);
        var egg = Path.Combine(folder, "seed.egg");
        WriteEgg(egg, [new("a.txt", false, new EggBlock(Words(2000), 1)), new("b.txt", false, new EggBlock(Words(2000), 4, EggLzma(RawLzma(Words(2000), endMarker: false))))]);
        Fuzz("egg", [File.ReadAllBytes(egg)]);
    }

    private void Fuzz(string format, List<byte[]> seeds, int mutateFrom = 0)
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("FILEE_FUZZ_SECONDS"), out var s) && s > 0 ? s : 0;
        var seed = int.TryParse(Environment.GetEnvironmentVariable("FILEE_FUZZ_SEED"), out var fixedSeed) ? fixedSeed
            : seconds > 0 ? Environment.TickCount : 20261101;
        var outFolder = Environment.GetEnvironmentVariable("FILEE_FUZZ_OUT") is { Length: > 0 } o ? o : null;
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"Seed {seed}, {(seconds > 0 ? seconds + " s" : QuickIterations + " inputs per seed")}");
        var random = new Random(seed);
        var folder = fx.NewFolder();
        var input = Path.Combine(outFolder ?? folder, $"running-archive.{format}");

        void Unpack()
        {
            var scratch = Path.Combine(folder, "scratch-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(scratch);
            try
            {
                new ArchiveExtractor(null).ExtractAsync(input, format, scratch, CancellationToken.None).GetAwaiter().GetResult();
            }
            finally
            {
                ArchiveFiles.TryDeleteFolder(scratch);
            }
        }

        foreach (var bytes in seeds)
        {
            File.WriteAllBytes(input, bytes);
            Assert.Equal(DocumentFuzzer.Outcome.Read, DocumentFuzzer.Run(Unpack, ReadTimeout));
        }

        var failures = new Dictionary<string, string>();
        var (inputs, refused) = (0, 0);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (seconds > 0 ? stopwatch.Elapsed < TimeSpan.FromSeconds(seconds) : inputs < QuickIterations * seeds.Count)
        {
            var bytes = Mutate(seeds[inputs % seeds.Count], random, mutateFrom);
            inputs++;
            File.WriteAllBytes(input, bytes);
            try
            {
                if (DocumentFuzzer.Run(Unpack, ReadTimeout) == DocumentFuzzer.Outcome.Refused)
                    refused++;
            }
            catch (DocumentFuzzer.FuzzFailure failure)
            {
                var frame = failure.InnerException?.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
                var kind = $"{failure.InnerException?.GetType().Name ?? "Timeout"} {frame}";
                if (!failures.ContainsKey(kind))
                {
                    var kept = Path.Combine(outFolder ?? folder, $"archive-{format}-{seed}-{inputs}.{format}");
                    File.WriteAllBytes(kept, bytes);
                    failures[kind] = $"{failure.Message} (input {kept})\n{failure.InnerException}";
                }
                if (failure.InnerException is null)
                    break;
            }
        }
        output?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{inputs} inputs, {refused} refused, {failures.Count} kinds of failure"));
        Assert.True(failures.Count == 0, $"Seed {seed}: " + string.Join("\n\n", failures.Values));
    }

    /// <summary>One to four changes: flipped bits, overwritten runs (zeros, 0xFF, random), a cut end, a repeated slice.</summary>
    private static byte[] Mutate(byte[] seed, Random random, int from)
    {
        var bytes = (byte[])seed.Clone();
        for (var n = random.Next(1, 5); n > 0; n--)
        {
            var at = from + random.Next(Math.Max(1, bytes.Length - from));
            at = Math.Min(at, bytes.Length - 1);
            switch (random.Next(5))
            {
                case 0:
                    bytes[at] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    {
                        var value = random.Next(3) switch { 0 => (byte)0, 1 => (byte)0xFF, _ => (byte)random.Next(256) };
                        for (var i = at; i < Math.Min(bytes.Length, at + random.Next(1, 9)); i++)
                            bytes[i] = value;
                        break;
                    }
                case 2:
                    bytes = bytes[..Math.Max(1, at)];
                    break;
                case 3:
                    {
                        var length = Math.Min(bytes.Length - at, random.Next(1, 64));
                        bytes = [.. bytes[..at], .. bytes.AsSpan(at, length), .. bytes[at..]];
                        break;
                    }
                default:
                    for (var i = at; i < Math.Min(bytes.Length, at + 4); i++)
                        bytes[i] = (byte)random.Next(256);
                    break;
            }
        }
        return bytes;
    }
}
