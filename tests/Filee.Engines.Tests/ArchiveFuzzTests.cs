// Mutation fuzzing of Filee's own archive readers (ar / DEB, cpio, RPM, ISO 9660, Unix compress, lzip, ALZ, EGG, 7z,
// and the LZMA / LZMA2 / .xz decoder) and of TAR (System.Formats.Tar behind Filee's header walk): seeds are built in code, bytes are flipped, overwritten, cut and
// repeated, and every input must unpack or be refused with InvalidDataException, in time. Same switches as DocumentFuzzTests: a short fixed run normally,
// FILEE_FUZZ_SECONDS for a longer random one, FILEE_FUZZ_SEED to repeat, FILEE_FUZZ_OUT to keep failing inputs.

using System.Buffers.Binary;
using System.Formats.Tar;
using System.Globalization;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using Filee.Engines.Archives.Lzma;
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
    public void Tar_reader_unpacks_or_refuses_every_mutated_archive() =>
        Fuzz("tar", [Tar(Seed()), GnuTar()]);

    /// <summary>A GNU TAR: a long name and a long hard link target (GNU 'L' and 'K' headers) and a symbolic link.</summary>
    private static byte[] GnuTar()
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, TarEntryFormat.Gnu, leaveOpen: true))
        {
            var name = "폴더/" + new string('n', 120) + ".txt";
            writer.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "폴더/"));
            writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Words(1_500)) });
            writer.WriteEntry(new GnuTarEntry(TarEntryType.HardLink, "hard.txt") { LinkName = name });
            writer.WriteEntry(new GnuTarEntry(TarEntryType.SymbolicLink, "soft") { LinkName = "../outside" });
        }
        return output.ToArray();
    }

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

    /// <summary>
    /// Filee's own .xz decoder (XzReader → Lzma2Reader → LzmaDecoder): seeds from Filee's writer at every level, with
    /// text, noise and a long run, and the test builder's xz (another LZMA encoder inside LZMA2 chunks). Half the
    /// mutations go past the headers, into the LZMA2 data. Output goes through the usual UnpackBudget.
    /// </summary>
    [Fact]
    public void Xz_decoder_unpacks_or_refuses_every_mutated_file()
    {
        static byte[] Ours(byte[] data, ArchiveLevel level)
        {
            using var output = new MemoryStream();
            using (var xz = new XzWriter(output, level, data.Length, CancellationToken.None, leaveOpen: true))
            {
                xz.Write(data);
                xz.Finish();
            }
            return output.ToArray();
        }

        byte[] mixed = [.. Words(30_000), .. Noise(3_000, 2), .. new byte[20_000], .. Words(5_000)];
        List<byte[]> seeds = [Ours(mixed, ArchiveLevel.Normal), Ours(Words(40_000), ArchiveLevel.Fast), Ours(mixed, ArchiveLevel.Store),
            Ours(mixed, ArchiveLevel.Maximum), Xz(Words(20_000))];
        Fuzz("xz", seeds, read: DecodeXz);
        Fuzz("xz", seeds, mutateFrom: 24, read: DecodeXz);
    }

    /// <summary>Raw LZMA with an end marker (as in lzip and .lzma), decoded by Filee's LzmaRawReader.</summary>
    [Fact]
    public void Raw_lzma_decoder_decodes_or_refuses_every_mutated_stream() =>
        Fuzz("lzma-raw", [RawLzma(Words(30_000), endMarker: true), RawLzma([.. Noise(2_000, 3), .. Words(4_000)], endMarker: true)],
            read: (input, scratch) =>
            {
                using var file = File.OpenRead(input);
                using var reader = new LzmaRawReader(file, LzmaProperties.Default, 1 << 20, -1);
                ArchiveFiles.CopyLimited(reader, Stream.Null, long.MaxValue, CancellationToken.None,
                    budget: new UnpackBudget("fuzz.lzma", file.Length, scratch));
            });

    /// <summary>
    /// Filee's own 7z reader (SevenZipReader → SevenZipCoders): seeds from Filee's writer (LZMA2 blocks and the Copy
    /// coder, packed header), the test builder's (LZMA and Copy, plain header) and one of only empty entries. The
    /// second round goes past the signature header and puts the header CRCs right after each change, so changed headers
    /// reach the parser instead of stopping at the checksum.
    /// </summary>
    [Fact]
    public void Seven_zip_reader_unpacks_or_refuses_every_mutated_archive()
    {
        static byte[] Ours(ArchiveLevel level, long? blockLimit = null)
        {
            using var output = new MemoryStream();
            var writer = new SevenZipWriter(output, level, cancellationToken: CancellationToken.None, blockLimit: blockLimit);
            writer.AddFolder("폴더", KnownTime.ToUniversalTime(), 0x10);
            writer.AddFile("폴더/a.txt", new MemoryStream(Words(6_000)), 6_000, KnownTime.ToUniversalTime(), 0x20);
            writer.AddFile("b.bin", new MemoryStream(Noise(2_000, 4)), 2_000, null, 0x20);
            writer.AddFile("빈.txt", new MemoryStream(), 0, null, 0x20);
            writer.AddFile("c.txt", new MemoryStream(Words(3_000)), 3_000, null, 0x20);
            writer.Finish();
            return output.ToArray();
        }

        List<byte[]> seeds = [Ours(ArchiveLevel.Normal), Ours(ArchiveLevel.Fast, blockLimit: 4_000), Ours(ArchiveLevel.Store),
            SevenZip(Seed()), SevenZip(Seed(), lzma: false), SevenZip([new("a/", null), new("a/b.txt", [])])];
        Fuzz("7z", seeds, read: (input, scratch) => Read7z(input, scratch, fixCrcs: false));
        Fuzz("7z", seeds, mutateFrom: 32, read: (input, scratch) => Read7z(input, scratch, fixCrcs: true));
    }

    /// <summary>Unpacks with SevenZipReader alone; a method it does not decode counts as refused.</summary>
    private static void Read7z(string input, string scratch, bool fixCrcs)
    {
        if (fixCrcs)
        {
            var bytes = File.ReadAllBytes(input);
            if (bytes.Length >= 32)
            {
                var offset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(12));
                var size = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(20));
                if (offset <= (ulong)bytes.Length - 32 && size <= (ulong)bytes.Length - 32 - offset)
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), Crc32.Compute(bytes.AsSpan(32 + (int)offset, (int)size)));
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), Crc32.Compute(bytes.AsSpan(12, 20)));
                File.WriteAllBytes(input, bytes);
            }
        }
        using var file = File.OpenRead(input);
        try
        {
            SevenZipReader.Extract(file, "fuzz.7z", scratch, CancellationToken.None);
        }
        catch (SevenZipUnsupportedException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    private static void DecodeXz(string input, string scratch)
    {
        using var file = File.OpenRead(input);
        using var reader = new XzReader(file, "fuzz.xz");
        ArchiveFiles.CopyLimited(reader, Stream.Null, long.MaxValue, CancellationToken.None,
            budget: new UnpackBudget("fuzz.xz", file.Length, scratch));
    }

    /// <param name="read">Reads the input file (path, scratch folder); the archive extractor for <paramref name="format"/> when null.</param>
    private void Fuzz(string format, List<byte[]> seeds, int mutateFrom = 0, Action<string, string>? read = null)
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
                if (read is not null)
                    read(input, scratch);
                else
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
