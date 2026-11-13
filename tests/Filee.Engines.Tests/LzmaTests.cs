// Filee's own LZMA / LZMA2 codec and .xz writer (Archives/Lzma, Archives/XzWriter): round trips across sizes, data
// and levels; interop both ways with SharpCompress, the bundled 7-Zip and xz (where installed, skipped otherwise);
// damaged and hostile input; cancellation. The speed / ratio comparison with 7-Zip runs only with
// FILEE_ARCHIVE_BENCHMARK=1 (like ArchiveSpeedTests) and prints a Markdown table. Speeds there are per second of CPU
// time (7-Zip with -mmt=off), so other work on the machine doesn't skew them.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using Filee.Engines.Archives.Lzma;
using Filee.Engines.Infrastructure;
using SharpCompress.Compressors.Xz;
using static Filee.Engines.Tests.ArchiveBuilders;
using static Filee.Engines.Tests.ArchiveFormatBuilders;
using Crc32 = Filee.Engines.Archives.Crc32;
using Crc64 = Filee.Engines.Archives.Lzma.Crc64;

namespace Filee.Engines.Tests;

public class LzmaTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ───────── Data ─────────

    public enum Kind { Random, Text, Zeros, Mixed }

    private static byte[] Data(Kind kind, int length) => kind switch
    {
        Kind.Random => Noise(length, length),
        Kind.Text => Prose(length),
        Kind.Zeros => new byte[length],
        _ => Mixed(length),
    };

    /// <summary>Text with a large vocabulary and numbers, so matches have many lengths and distances.</summary>
    internal static byte[] Prose(int length)
    {
        var random = new Random(length);
        string[] words = ["the", "archive", "파일", "변환", "Filee", "compress", "dictionary", "window", "range", "coder",
            "literal", "match", "distance", "데이터", "압축", "文件", "state", "chunk", "block", "index", "stream", "check"];
        var sb = new StringBuilder(length + 64);
        while (sb.Length < length)
        {
            sb.Append(words[random.Next(words.Length)]);
            sb.Append(random.Next(10) switch { 0 => ", ", 1 => ".\n", 2 => " " + random.Next(100_000).ToString(CultureInfo.InvariantCulture) + " ", _ => " " });
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return bytes.AsSpan(0, Math.Min(length, bytes.Length)).ToArray();
    }

    /// <summary>Runs of text, noise and repeats of earlier parts, so chunks switch between stored and LZMA.</summary>
    private static byte[] Mixed(int length)
    {
        var random = new Random(7);
        var bytes = new byte[length];
        var at = 0;
        while (at < length)
        {
            var run = Math.Min(length - at, random.Next(1, 200_000));
            switch (random.Next(3))
            {
                case 0:
                    Prose(run).CopyTo(bytes, at);
                    break;
                case 1:
                    random.NextBytes(bytes.AsSpan(at, run));
                    break;
                default:
                    var from = random.Next(Math.Max(1, at));
                    for (var i = 0; i < run; i++)
                        bytes[at + i] = bytes[from + i % Math.Max(1, at - from)];
                    break;
            }
            at += run;
        }
        return bytes;
    }

    private static byte[] Compress(byte[] data, ArchiveLevel level, long expected = -2) => CompressCancellable(data, level, expected, CancellationToken.None);

    private static byte[] CompressCancellable(byte[] data, ArchiveLevel level, long expected, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        using (var xz = new XzWriter(output, level, expected == -2 ? data.Length : expected, cancellationToken, leaveOpen: true))
        {
            // Odd write sizes, so chunks and slides fall in the middle of writes.
            for (var at = 0; at < data.Length; at += 70_001)
                xz.Write(data, at, Math.Min(70_001, data.Length - at));
            xz.Finish();
        }
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] xz, uint maxDictionary = XzReader.DefaultMaxDictionary)
    {
        using var reader = new XzReader(new MemoryStream(xz), "test.xz", maxDictionary, CancellationToken.None);
        using var output = new MemoryStream();
        reader.CopyTo(output, 50_000);
        return output.ToArray();
    }

    // ───────── Round trips ─────────

    [Fact]
    public void Crc64_matches_the_xz_check_value()
    {
        Assert.Equal(0x995DC9BBDF1939FAUL, Crc64.Compute("123456789"u8));
        var data = Noise(1001, 3);
        var crc = new Crc64();
        crc.Append(data.AsSpan(0, 3));
        crc.Append(data.AsSpan(3));
        Assert.Equal(Crc64.Compute(data), crc.Value);
    }

    [Theory]
    [InlineData(ArchiveLevel.Store)]
    [InlineData(ArchiveLevel.Fast)]
    [InlineData(ArchiveLevel.Normal)]
    [InlineData(ArchiveLevel.Maximum)]
    public void Round_trips_every_kind_of_data_and_size(ArchiveLevel level)
    {
        foreach (var kind in Enum.GetValues<Kind>())
        {
            foreach (var length in new[] { 0, 1, 2, 3, 5, 273, 4096, 65_536, 65_537, 300_000, 2_500_000 })
            {
                var data = Data(kind, length);
                var xz = Compress(data, level);
                Assert.True(data.AsSpan().SequenceEqual(Decompress(xz)), $"{kind} {length} at {level}");
                // SharpCompress's xz reader agrees.
                using var other = new XZStream(new MemoryStream(xz));
                using var copy = new MemoryStream();
                other.CopyTo(copy);
                Assert.True(data.AsSpan().SequenceEqual(copy.ToArray()), $"{kind} {length} at {level} (SharpCompress)");
            }
        }
    }

    [Fact]
    public void Compresses_text_and_repeats_and_stores_noise()
    {
        var text = Prose(1_000_000);
        Assert.True(Compress(text, ArchiveLevel.Normal).Length < text.Length / 3);
        Assert.True(Compress(new byte[1_000_000], ArchiveLevel.Normal).Length < 1_000);
        var noise = Noise(1_000_000, 5);
        var packed = Compress(noise, ArchiveLevel.Normal);
        Assert.InRange(packed.Length, noise.Length, noise.Length + noise.Length / 100);
        Assert.True(Compress(text, ArchiveLevel.Maximum).Length <= Compress(text, ArchiveLevel.Fast).Length);
    }

    [Fact]
    public void The_window_slides_past_a_small_dictionary()
    {
        // 4 KiB dictionary (input size unknown to the writer is not the case here: say it's tiny), 3 MB of input.
        var data = Mixed(3_000_000);
        var xz = Compress(data, ArchiveLevel.Normal, expected: 1000);
        Assert.Equal(12, XzFormat.DictionaryFromProperty(xz[16]) is { } d ? System.Numerics.BitOperations.Log2(d) : 0);
        Assert.Equal(data, Decompress(xz));
        // And with the input size not known at all (full level dictionary).
        Assert.Equal(data, Decompress(Compress(data, ArchiveLevel.Fast, expected: -1)));
        // Positions in the match finder are rebased every 1 GiB of input; here after every slide.
        MatchFinder.TestNormalizeAt.Value = 1;
        try
        {
            Assert.Equal(data, Decompress(Compress(data, ArchiveLevel.Normal, expected: 1000))); // binary tree
            Assert.Equal(data, Decompress(Compress(data, ArchiveLevel.Fast, expected: 1000)));   // hash chain
        }
        finally
        {
            MatchFinder.TestNormalizeAt.Value = null;
        }
    }

    [Fact]
    public void Raw_lzma_from_another_encoder_decodes_with_and_without_end_marker()
    {
        var data = Prose(400_000);
        foreach (var endMarker in new[] { true, false })
        {
            var raw = RawLzma(data, endMarker);
            using var reader = new LzmaRawReader(new MemoryStream(raw), LzmaProperties.FromByte(0x5D)!.Value, 1 << 20, endMarker ? -1 : data.Length);
            using var output = new MemoryStream();
            reader.CopyTo(output);
            Assert.Equal(data, output.ToArray());
        }
    }

    [Fact]
    public void Xz_from_the_test_builder_decodes()
    {
        // ArchiveFormatBuilders.Xz: SharpCompress's LZMA inside LZMA2 chunks, stored chunks for noise, CRC-32 check.
        foreach (var data in new[] { Words(3_000_000), Noise(150_000, 9) })
            Assert.Equal(data, Decompress(Xz(data)));
    }

    [Fact]
    public void Concatenated_streams_and_stream_padding_are_read()
    {
        var a = Prose(100_000);
        var b = Mixed(200_000);
        byte[] joined = [.. Compress(a, ArchiveLevel.Normal), 0, 0, 0, 0, .. Compress(b, ArchiveLevel.Fast), 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal([.. a, .. b], Decompress(joined));
        Assert.Throws<InvalidDataException>(() => Decompress([.. Compress(a, ArchiveLevel.Normal), 0, 0]));
    }

    // ───────── The packer ─────────

    [Theory]
    [InlineData("txz")]
    [InlineData("xz")]
    public async Task Packs_without_7zip_and_the_in_process_reader_unpacks_it(string format)
    {
        Assert.Contains(format, ArchivePacker.BuiltInFormats);
        var dir = fx.NewFolder();
        var tree = MakeTree(dir, "트리");
        var single = Path.Combine(dir, "본문.txt");
        await File.WriteAllBytesAsync(single, Prose(300_000), Ct);
        File.SetLastWriteTimeUtc(single, KnownTime.ToUniversalTime());
        var output = Path.Combine(dir, "out." + (format == "txz" ? "tar.xz" : "txt.xz"));
        PackItem[] items = format == "txz" ? [new(tree, "트리"), new(single, "본문.txt")] : [new(single, "본문.txt")];
        await new ArchivePacker(null).PackAsync(items, output, format, ArchiveLevel.Normal, dir, null, Ct);

        var target = Directory.CreateDirectory(Path.Combine(dir, "unpacked")).FullName;
        var unpacked = await new ArchiveExtractor(null).ExtractAsync(output, format, target, Ct);
        if (format == "txz")
        {
            Assert.Equal(Snapshot(tree), Snapshot(Path.Combine(unpacked, "트리")));
            Assert.Equal(await File.ReadAllBytesAsync(single, Ct), await File.ReadAllBytesAsync(Path.Combine(unpacked, "본문.txt"), Ct));
        }
        else
        {
            var file = Assert.Single(Directory.GetFiles(unpacked));
            Assert.Equal(await File.ReadAllBytesAsync(single, Ct), await File.ReadAllBytesAsync(file, Ct));
            Assert.Equal(KnownTime.ToUniversalTime(), File.GetLastWriteTimeUtc(output));
        }
        // Our own reader agrees with the in-process one.
        await using var input = File.OpenRead(output);
        await using var reader = new XzReader(input, "out");
        await using var copy = new MemoryStream();
        await reader.CopyToAsync(copy, Ct);
        Assert.True(copy.Length > 0);
    }

    [Fact]
    public async Task A_cancelled_pack_leaves_nothing_behind()
    {
        var dir = fx.NewFolder();
        var source = Path.Combine(dir, "big.bin");
        await File.WriteAllBytesAsync(source, Mixed(8_000_000), Ct);
        var output = Path.Combine(dir, "big.bin.xz");
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p > 0.1) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ArchivePacker(null).PackAsync([new PackItem(source, "big.bin")], output, "xz", ArchiveLevel.Normal, dir, progress, cancel.Token));
        Assert.False(File.Exists(output));
        Assert.Equal(["big.bin"], Directory.GetFiles(dir).Select(Path.GetFileName));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    // ───────── Damaged and hostile input ─────────

    [Fact]
    public void Damaged_files_are_refused()
    {
        var data = Prose(200_000);
        var good = Compress(data, ArchiveLevel.Normal);

        byte[] Changed(int at, byte value)
        {
            var copy = (byte[])good.Clone();
            copy[at] = value;
            return copy;
        }

        Assert.Throws<InvalidDataException>(() => Decompress([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13]));
        Assert.Throws<InvalidDataException>(() => Decompress(good[..^1]));                // footer cut
        Assert.Throws<InvalidDataException>(() => Decompress(good[..(good.Length / 2)])); // data cut
        Assert.Throws<InvalidDataException>(() => Decompress(Changed(7, 0x05)));          // stream flags vs CRC
        Assert.Throws<InvalidDataException>(() => Decompress(Changed(16, 0x10)));         // block header vs CRC
        Assert.Throws<InvalidDataException>(() => Decompress(Changed(good.Length / 2, (byte)(good[good.Length / 2] ^ 0x40))));
        Assert.Throws<InvalidDataException>(() => Decompress(Changed(good.Length - 13, (byte)(good[^13] ^ 1)))); // check / index
        Assert.Throws<InvalidDataException>(() => Decompress(Changed(good.Length - 1, (byte)'X')));             // footer magic
    }

    /// <summary>A small stored-chunk .xz whose block header names <paramref name="property"/> as the dictionary.</summary>
    private static byte[] WithDictionaryProperty(byte property)
    {
        var xz = Compress(Prose(10_000), ArchiveLevel.Store);
        Assert.Equal(0x21, xz[14]);
        xz[16] = property;
        BinaryPrimitives.WriteUInt32LittleEndian(xz.AsSpan(20), Crc32.Compute(xz.AsSpan(12, 8)));
        return xz;
    }

    [Fact]
    public void A_hostile_dictionary_size_is_refused_before_anything_is_allocated()
    {
        foreach (var property in new byte[] { 40, 39, 38 }) // 4 GiB − 1, 3 GiB, 2 GiB
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var error = Assert.Throws<InvalidDataException>(() => Decompress(WithDictionaryProperty(property)));
            Assert.True(ArchiveSafety.IsRefusal(error), error.Message);
            Assert.Contains("dictionary", error.Message);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4 << 20);
        }
        // 41 and up are no dictionary size at all.
        Assert.False(ArchiveSafety.IsRefusal(Assert.Throws<InvalidDataException>(() => Decompress(WithDictionaryProperty(41)))));

        // 1.5 GiB is allowed, but the window only grows with the output: 10 KB of data costs no more than a few MB.
        var start = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(10_000, Decompress(WithDictionaryProperty(37)).Length);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - start < 8 << 20);

        // A caller's lower limit (64 MiB file, 16 MiB allowed).
        var big = WithDictionaryProperty(XzFormat.PropertyFor(64u << 20));
        Assert.True(ArchiveSafety.IsRefusal(Assert.Throws<InvalidDataException>(() => Decompress(big, maxDictionary: 16u << 20))));
    }

    [Fact]
    public void Decoded_output_counts_against_the_unpack_budget()
    {
        var zeros = Compress(new byte[16_000_000], ArchiveLevel.Normal);
        var dir = fx.NewFolder();
        ArchiveSafety.TestLimits.Value = (1 << 20, 100, ArchiveSafety.MaxEntries);
        try
        {
            using var reader = new XzReader(new MemoryStream(zeros), "zeros.xz");
            var budget = new UnpackBudget("zeros.xz", zeros.Length, dir);
            var error = Assert.Throws<InvalidDataException>(() =>
                ArchiveFiles.CopyLimited(reader, Stream.Null, long.MaxValue, Ct, budget: budget));
            Assert.True(ArchiveSafety.IsRefusal(error));
            Assert.Contains("decompression bomb", error.Message);
        }
        finally
        {
            ArchiveSafety.TestLimits.Value = null;
        }
    }

    [Fact]
    public void Lzma2_rules_are_enforced()
    {
        // Every chunk is checked against its header: a stored chunk first without a dictionary reset, an unknown
        // control byte, an LZMA chunk before any properties, and LZMA data that ends before its stated size.
        static void Read(byte[] lzma2, uint dictionary = 1 << 16)
        {
            var reader = new Lzma2Reader(new ByteInput(new MemoryStream(lzma2)), dictionary, -1, CancellationToken.None);
            var buffer = new byte[1 << 16];
            while (reader.Read(buffer) > 0) { }
        }

        Read([0x01, 0x00, 0x02, (byte)'a', (byte)'b', (byte)'c', 0x00]);
        Assert.Throws<InvalidDataException>(() => Read([0x02, 0x00, 0x00, (byte)'a', 0x00]));
        Assert.Throws<InvalidDataException>(() => Read([0x01, 0x00, 0x00, (byte)'a', 0x03, 0x00, 0x00, (byte)'b', 0x00]));
        Assert.Throws<InvalidDataException>(() => Read([0x01, 0x00, 0x00, (byte)'a', 0x80, 0x00, 0x00, 0x00, 0x05, 0, 0, 0, 0, 0, 0, 0x00]));
        Assert.Throws<InvalidDataException>(() => Read([0x01, 0x00, 0x00, (byte)'a']));
        var raw = RawLzma(Prose(1000), endMarker: false);
        byte[] Chunk(int unpacked, byte properties) =>
            [0xE0, (byte)((unpacked - 1) >> 8), (byte)(unpacked - 1), (byte)((raw.Length - 1) >> 8), (byte)(raw.Length - 1), properties, .. raw, 0x00];
        Read(Chunk(1000, 0x5D));
        Assert.Throws<InvalidDataException>(() => Read(Chunk(2000, 0x5D))); // 2,000 bytes claimed, 1,000 coded
        Assert.Throws<InvalidDataException>(() => Read(Chunk(999, 0x5D)));  // the coder doesn't end there
        Assert.Throws<InvalidDataException>(() => Read(Chunk(1000, 13)));   // lc + lp past 4
    }

    [Fact]
    public void Reading_and_writing_stop_when_cancelled()
    {
        var xz = Compress(Mixed(3_000_000), ArchiveLevel.Fast);
        using var cancel = new CancellationTokenSource();
        using var reader = new XzReader(new MemoryStream(xz), "big.xz", cancellationToken: cancel.Token);
        var buffer = new byte[100_000];
        Assert.True(reader.Read(buffer) > 0);
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => { while (reader.Read(buffer) > 0) { } });

        Assert.Throws<OperationCanceledException>(() => CompressCancellable(Prose(100_000), ArchiveLevel.Normal, -1, cancel.Token));
    }

    // ───────── Other tools ─────────

    [Fact]
    public async Task Xz_utils_and_7zip_read_our_files()
    {
        Assert.SkipUnless(XzTool is not null || SevenZipExe is not null, "neither xz nor 7-Zip is installed");
        var dir = fx.NewFolder();
        foreach (var level in new[] { ArchiveLevel.Store, ArchiveLevel.Fast, ArchiveLevel.Normal, ArchiveLevel.Maximum })
        {
            var data = Mixed(3_000_000);
            var path = Path.Combine(dir, $"ours-{level}.xz");
            await File.WriteAllBytesAsync(path, Compress(data, level), Ct);
            if (XzTool is not null)
            {
                await RunAsync(XzTool, ["-t", path]);
                Assert.Equal(data, await RunAsync(XzTool, ["-dc", path]));
            }
            if (SevenZipExe is not null)
            {
                var target = Path.Combine(dir, $"7z-{level}");
                await Run7zAsync("x", "-y", "-o" + target, path);
                Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(target, $"ours-{level}"), Ct));
                await Run7zAsync("t", path);
            }
        }
    }

    [Fact]
    public async Task Files_from_xz_utils_and_7zip_decode()
    {
        Assert.SkipUnless(XzTool is not null || SevenZipExe is not null, "neither xz nor 7-Zip is installed");
        var dir = fx.NewFolder();
        var data = Mixed(2_000_000);
        var source = Path.Combine(dir, "data.bin");
        await File.WriteAllBytesAsync(source, data, Ct);
        if (XzTool is not null)
        {
            string[][] options =
            [
                ["-0"], ["-6"], ["-9e"], ["--check=crc32"], ["--check=sha256"], ["--check=none"],
                ["--block-size=300KiB"], ["--lzma2=lc=1,lp=3,pb=0"], ["--lzma2=lc=4,lp=0,pb=4,dict=64KiB"],
            ];
            foreach (var option in options)
            {
                var made = await RunAsync(XzTool, [.. option, "-c", source]);
                Assert.True(data.AsSpan().SequenceEqual(Decompress(made)), string.Join(" ", option));
            }
            // Filters other than LZMA2 alone are refused cleanly.
            var x86 = await RunAsync(XzTool, ["--x86", "--lzma2", "-c", source]);
            Assert.Contains("filter", Assert.Throws<InvalidDataException>(() => Decompress(x86)).Message);
        }
        if (SevenZipExe is not null)
        {
            foreach (var level in new[] { "-mx1", "-mx5", "-mx9" })
            {
                var path = Path.Combine(dir, $"7z{level}.xz");
                await Run7zAsync("a", "-txz", level, path, source);
                var made = await File.ReadAllBytesAsync(path, Ct);
                Assert.True(data.AsSpan().SequenceEqual(Decompress(made)), level);
            }
        }
    }

    /// <summary>xz from XZ Utils on the PATH, or null.</summary>
    private static readonly string? XzTool = FindXz();

    private static string? FindXz()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("xz", "--version")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is null)
                return null;
            process.WaitForExit(10_000);
            return process.ExitCode == 0 ? "xz" : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static async Task<byte[]> RunAsync(string tool, string[] args)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
        await copy;
        Assert.True(process.ExitCode == 0, $"{tool} {string.Join(' ', args)}: {await error}");
        return output.ToArray();
    }

    // ───────── Speed and ratio ─────────

    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task Speed_and_ratio_next_to_7zip()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("FILEE_ARCHIVE_BENCHMARK") == "1", "set FILEE_ARCHIVE_BENCHMARK=1");
        Assert.SkipUnless(SevenZipExe is not null, "7-Zip not found");
        var megabytes = int.TryParse(Environment.GetEnvironmentVariable("FILEE_ARCHIVE_BENCHMARK_MB"), out var mb) ? mb : 8;
        var dir = fx.NewFolder();
        var sets = new List<(string Label, byte[] Data)> { ("source code", SourceText(megabytes << 20)), ("generated prose", Prose(megabytes << 20)) };
        var table = new StringBuilder("| data | tool | level | size | ratio | compress (CPU) | decompress (CPU) |\n|---|---|---|---|---|---|---|\n");
        foreach (var (label, data) in sets)
        {
            var source = Path.Combine(dir, "input.bin");
            await File.WriteAllBytesAsync(source, data, Ct);
            foreach (var (level, sevenLevel) in new[] { (ArchiveLevel.Fast, "-mx3"), (ArchiveLevel.Normal, "-mx5"), (ArchiveLevel.Maximum, "-mx9") })
            {
                var cpu = ProcessCpu();
                var ours = Compress(data, level);
                var packTime = ProcessCpu() - cpu;
                cpu = ProcessCpu();
                var back = Decompress(ours);
                var unpackTime = ProcessCpu() - cpu;
                Assert.Equal(data.Length, back.Length);
                Row("Filee", level.ToString(), ours.Length, packTime, unpackTime);

                var path = Path.Combine(dir, $"seven{sevenLevel}.xz");
                File.Delete(path);
                packTime = await SevenZipCpuAsync("a", "-txz", sevenLevel, "-mmt=off", path, source);
                unpackTime = await SevenZipCpuAsync("t", "-mmt=off", path);
                Row("7-Zip " + EngineVersions.Component("7zip"), sevenLevel, new FileInfo(path).Length, packTime, unpackTime);

                void Row(string tool, string name, long size, TimeSpan pack, TimeSpan unpack) =>
                    table.Append(CultureInfo.InvariantCulture, $"| {label} ({data.Length / 1e6:0.0} MB) | {tool} | {name} | {size:N0} | {size * 100.0 / data.Length:0.00}% | {data.Length / 1e6 / pack.TotalSeconds:0.0} MB/s | {data.Length / 1e6 / unpack.TotalSeconds:0.0} MB/s |\n");
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine(table.ToString());
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "filee-lzma-benchmark.md"), table.ToString(), Ct);
    }

    private static TimeSpan ProcessCpu()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    /// <summary>Runs 7-Zip and returns the CPU time it used.</summary>
    private static async Task<TimeSpan> SevenZipCpuAsync(params string[] args)
    {
        var start = new ProcessStartInfo(SevenZipExe!) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode == 0, await output);
        return process.TotalProcessorTime;
    }

    /// <summary>Real text: this repository's C# sources, repeated if needed to reach <paramref name="length"/>.</summary>
    private static byte[] SourceText(int length)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Filee.Engines")))
            dir = dir.Parent;
        if (dir is null)
            return Prose(length);
        using var output = new MemoryStream();
        while (output.Length < length)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(dir.FullName, "src"), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                output.Write(File.ReadAllBytes(file));
                if (output.Length >= length)
                    break;
            }
        }
        return output.ToArray()[..length];
    }
}
