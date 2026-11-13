// Mutation fuzzing of Filee's own archive readers (ar / DEB, cpio, RPM, ISO 9660, Unix compress, lzip, ALZ, EGG) and of
// TAR (System.Formats.Tar behind Filee's header walk): seeds are built in code, bytes are flipped, overwritten, cut and
// repeated, and every input must unpack or be refused with InvalidDataException, in time. The loop is FuzzHarness's: a
// short fixed run normally, FILEE_FUZZ_SECONDS for a longer random one, FILEE_FUZZ_SEED to repeat, FILEE_FUZZ_OUT to
// keep failing inputs.

using System.Formats.Tar;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;
using static Filee.Engines.Tests.ArchiveFormatBuilders;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class ArchiveFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const int QuickIterations = 60;

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

    private void Fuzz(string format, List<byte[]> seeds, int mutateFrom = 0)
    {
        var folder = fx.NewFolder();
        new FuzzHarness(fx, QuickIterations, 20261101).Files("archive-" + format, format, seeds,
            (seed, random) => () => Mutate(seed, random, mutateFrom), (input, _) =>
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
            });
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
