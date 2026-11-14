// Filee's own bzip2 writer (Archives/Bzip2Writer, Bzip2BlockEncoder, Bzip2Sort): every output unpacks byte for byte
// with Filee's own decoder, SharpCompress and the bundled 7-Zip (skipped when it is missing), across data that tests
// the run-length step, the transform and the block edges; one thread or several give the same bytes; .bz2 and .tar.bz2
// are written without 7-Zip; cancellation stops it. The speed comparison with 7-Zip runs only with
// FILEE_ARCHIVE_BENCHMARK=1 and prints a Markdown table.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using SharpCompress.Compressors.BZip2;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

public class Bzip2WriterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public enum Kind { Empty, One, Text, Random, Zeros, Runs, Periodic, AllBytes, Mixed, Edge }

    /// <summary>Data for <paramref name="kind"/>, about <paramref name="length"/> bytes.</summary>
    internal static byte[] Data(Kind kind, int length) => kind switch
    {
        Kind.Empty => [],
        Kind.One => [0x42],
        Kind.Text => LzmaTests.Prose(length),
        Kind.Random => Noise(length, length),
        Kind.Zeros => new byte[length],
        Kind.Runs => Runs(length),
        Kind.Periodic => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("ab", length / 2))),
        Kind.AllBytes => Enumerable.Range(0, length).Select(i => (byte)(i * 7 + i / 256)).ToArray(),
        Kind.Mixed => Mixed(length),
        _ => Edge(),
    };

    /// <summary>Runs of every length around the run-length step's limits (3, 4, 5, 255, 256, 259, 260 …).</summary>
    private static byte[] Runs(int length)
    {
        var random = new Random(3);
        var bytes = new List<byte>(length);
        int[] lengths = [1, 2, 3, 4, 5, 6, 254, 255, 256, 257, 258, 259, 260, 510, 511, 1000];
        var value = (byte)0;
        while (bytes.Count < length)
        {
            var run = random.Next(4) == 0 ? lengths[random.Next(lengths.Length)] : random.Next(1, 8);
            value = (byte)(random.Next(3) == 0 ? value + 1 : random.Next(256));
            for (var i = 0; i < run && bytes.Count < length; i++)
                bytes.Add(value);
        }
        return [.. bytes];
    }

    /// <summary>Text, noise, long runs and repeats of earlier parts.</summary>
    internal static byte[] Mixed(int length)
    {
        var random = new Random(11);
        var bytes = new byte[length];
        var at = 0;
        while (at < length)
        {
            var run = Math.Min(length - at, random.Next(1, 300_000));
            switch (random.Next(4))
            {
                case 0:
                    LzmaTests.Prose(run).CopyTo(bytes, at);
                    break;
                case 1:
                    random.NextBytes(bytes.AsSpan(at, run));
                    break;
                case 2:
                    bytes.AsSpan(at, run).Fill((byte)random.Next(256));
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

    /// <summary>
    /// Data whose run-length output fills the first block exactly to its limit (899,981 bytes), then a run that would
    /// have crossed it, then more: the block edge falls between two runs.
    /// </summary>
    private static byte[] Edge()
    {
        var bytes = new List<byte>();
        // 179,996 five-byte runs (each "xxxx" + count) = 899,980 bytes after the step, then one plain byte = 899,981.
        for (var i = 0; i < 179_996; i++)
            bytes.AddRange(Enumerable.Repeat((byte)(i % 2 == 0 ? 'x' : 'y'), 4 + i % 200));
        bytes.Add((byte)'z');
        bytes.AddRange(Enumerable.Repeat((byte)'q', 300));
        bytes.AddRange(LzmaTests.Prose(5000));
        return [.. bytes];
    }

    private static byte[] Compress(byte[] data, ArchiveLevel level, int threads = 0)
    {
        using var output = new MemoryStream();
        using (var writer = new Bzip2Writer(output, level, Ct, leaveOpen: true, threads: threads))
        {
            // Odd write sizes, so runs and blocks cross the writes.
            for (var at = 0; at < data.Length; at += 70_001)
                writer.Write(data, at, Math.Min(70_001, data.Length - at));
            writer.Finish();
        }
        return output.ToArray();
    }

    private static byte[] DecodeOwn(byte[] bz2)
    {
        using var output = new MemoryStream();
        Bzip2Decoder.Decompress(new MemoryStream(bz2), output, alzFraming: false, long.MaxValue, Ct);
        return output.ToArray();
    }

    private static byte[] DecodeSharpCompress(byte[] bz2)
    {
        using var output = new MemoryStream();
        using (var bzip2 = new BZip2Stream(new MemoryStream(bz2), SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: false))
            bzip2.CopyTo(output);
        return output.ToArray();
    }

    private async Task<byte[]> Decode7zAsync(byte[] bz2)
    {
        var dir = fx.NewFolder();
        var file = Path.Combine(dir, "data.bin.bz2");
        await File.WriteAllBytesAsync(file, bz2, Ct);
        await Run7zAsync("t", file);
        await Run7zAsync("e", "-y", "-o" + Path.Combine(dir, "out"), file);
        return await File.ReadAllBytesAsync(Path.Combine(dir, "out", "data.bin"), Ct);
    }

    // ───────── Round trips ─────────

    [Theory]
    [InlineData(Kind.Empty, 0, ArchiveLevel.Normal)]
    [InlineData(Kind.One, 1, ArchiveLevel.Normal)]
    [InlineData(Kind.Text, 2, ArchiveLevel.Normal)]
    [InlineData(Kind.Text, 3, ArchiveLevel.Fast)]
    [InlineData(Kind.Text, 200_000, ArchiveLevel.Fast)]
    [InlineData(Kind.Text, 2_500_000, ArchiveLevel.Normal)]
    [InlineData(Kind.Text, 1_000_000, ArchiveLevel.Maximum)]
    [InlineData(Kind.Text, 300_000, ArchiveLevel.Store)]
    [InlineData(Kind.Random, 1_200_000, ArchiveLevel.Normal)]
    [InlineData(Kind.Zeros, 5_000_000, ArchiveLevel.Normal)]
    [InlineData(Kind.Zeros, 4, ArchiveLevel.Normal)]
    [InlineData(Kind.Runs, 1_500_000, ArchiveLevel.Normal)]
    [InlineData(Kind.Periodic, 2_000_000, ArchiveLevel.Normal)]
    [InlineData(Kind.AllBytes, 100_000, ArchiveLevel.Normal)]
    [InlineData(Kind.Mixed, 3_000_000, ArchiveLevel.Normal)]
    [InlineData(Kind.Edge, 0, ArchiveLevel.Normal)]
    public async Task Output_unpacks_byte_for_byte_with_three_decoders(Kind kind, int length, ArchiveLevel level)
    {
        var data = Data(kind, length);
        var bz2 = Compress(data, level);
        Assert.Equal("BZh9"u8.ToArray(), bz2[..4]);
        Assert.Equal(data, DecodeOwn(bz2));
        Assert.Equal(data, DecodeSharpCompress(bz2));
        if (SevenZipExe is not null)
            Assert.Equal(data, await Decode7zAsync(bz2));
        if (kind is Kind.Text or Kind.Zeros or Kind.Runs or Kind.Periodic && length > 1000)
            Assert.True(bz2.Length < data.Length / 2, $"{bz2.Length} bytes for {data.Length}");
    }

    /// <summary>
    /// The transform against sorting every rotation by comparing it whole, for short blocks over small alphabets, where
    /// periodic blocks, repeats and ties are common: the same last column, and the origin row is the unrotated block.
    /// </summary>
    [Fact]
    public void The_transform_matches_a_plain_sort_of_all_rotations()
    {
        var random = new Random(4);
        List<byte[]> blocks = [.. new[] { "banana", "aaaa", "abab", "abcabc", "ba", "ab", "zyxzyx", "abaababaab" }.Select(Encoding.ASCII.GetBytes)];
        for (var i = 0; i < 3000; i++)
        {
            var length = random.Next(1, 120);
            var alphabet = random.Next(1, 4);
            var unit = Enumerable.Range(0, random.Next(1, 8)).Select(_ => (byte)('a' + random.Next(alphabet))).ToArray();
            blocks.Add(random.Next(3) == 0
                ? Enumerable.Range(0, length).Select(k => unit[k % unit.Length]).ToArray()
                : Enumerable.Range(0, length).Select(_ => (byte)('a' + random.Next(alphabet))).ToArray());
        }
        foreach (var block in blocks)
        {
            var n = block.Length;
            byte[] Rotation(int start) => [.. block.AsSpan(start), .. block.AsSpan(0, start)];
            var order = Enumerable.Range(0, n).OrderBy(Rotation, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b))).ToArray();
            var expected = order.Select(start => block[(start + n - 1) % n]).ToArray();
            var output = new byte[n];
            var origin = Bzip2Sort.Transform(block, output, Ct);
            Assert.Equal(expected, output);
            Assert.Equal(block, Rotation(order[origin]));
        }
    }

    [Fact]
    public void One_thread_and_several_write_the_same_bytes()
    {
        var data = Mixed(4_000_000);
        var one = Compress(data, ArchiveLevel.Normal, threads: 1);
        Assert.Equal(one, Compress(data, ArchiveLevel.Normal, threads: 3));
        Assert.Equal(data, DecodeOwn(one));
    }

    // ───────── Through the engine ─────────

    [Theory]
    [InlineData(ArchiveLevel.Fast)]
    [InlineData(ArchiveLevel.Normal)]
    public async Task Tar_bz2_and_bz2_are_written_without_7zip(ArchiveLevel level)
    {
        var dir = fx.NewFolder();
        var tree = MakeTree(dir, "프로젝트 📦");
        File.WriteAllBytes(Path.Combine(tree, "big.bin"), Mixed(2_000_000));
        var expected = Snapshot(tree);

        var tbz2 = Path.Combine(dir, "out.tar.bz2");
        await new ArchivePacker().PackAsync([new PackItem(tree, "프로젝트 📦")], tbz2, "tbz2", level, dir, null, Ct);
        var target = Directory.CreateDirectory(Path.Combine(dir, "in-process")).FullName;
        var unpacked = await new ArchiveExtractor(null).ExtractAsync(tbz2, "tbz2", target, Ct);
        Assert.Equal(expected, Snapshot(Path.Combine(unpacked, "프로젝트 📦")));
        Assert.Equal(KnownTime, File.GetLastWriteTime(Path.Combine(unpacked, "프로젝트 📦", "readme.txt")));

        var single = Path.Combine(tree, "readme.txt");
        var bz2 = Path.Combine(dir, "readme.txt.bz2");
        await new ArchivePacker().PackAsync([new PackItem(single, "readme.txt")], bz2, "bz2", level, dir, null, Ct);
        Assert.Equal(File.GetLastWriteTimeUtc(single), File.GetLastWriteTimeUtc(bz2));
        Assert.Equal(await File.ReadAllBytesAsync(single, Ct), DecodeOwn(await File.ReadAllBytesAsync(bz2, Ct)));

        if (SevenZipExe is null)
            return;
        var theirs = Path.Combine(dir, "7-zip");
        await Run7zAsync("x", "-y", "-o" + theirs, tbz2);
        await Run7zAsync("x", "-y", "-o" + theirs, Path.Combine(theirs, "out.tar"));
        Assert.Equal(expected, Snapshot(Path.Combine(theirs, "프로젝트 📦")));
    }

    [Fact]
    public async Task A_cancelled_write_stops_and_leaves_nothing_behind()
    {
        var data = Mixed(6_000_000);
        using (var cancel = new CancellationTokenSource())
        {
            var output = new MemoryStream();
            var writer = new Bzip2Writer(output, ArchiveLevel.Normal, cancel.Token, threads: 3);
            writer.Write(data, 0, 2_000_000);
            cancel.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() =>
            {
                writer.Write(data, 2_000_000, 4_000_000);
                writer.Finish();
            });
            writer.Dispose(); // waits for the blocks in flight
        }

        var dir = fx.NewFolder();
        var source = Path.Combine(dir, "data.bin");
        await File.WriteAllBytesAsync(source, data, Ct);
        var archive = Path.Combine(dir, "data.bin.bz2");
        using var soon = new CancellationTokenSource();
        var progress = new Progress(p =>
        {
            if (p > 0.2)
                soon.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ArchivePacker().PackAsync([new PackItem(source, "data.bin")], archive, "bz2", ArchiveLevel.Normal, dir, progress, soon.Token));
        Assert.Equal([source], Directory.GetFiles(dir));
    }

    /// <summary>Progress reported on the calling thread (Progress&lt;T&gt; posts it elsewhere, later).</summary>
    private sealed class Progress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    // ───────── Hostile input ─────────

    /// <summary>
    /// Blocks that make rotation sorts that compare bytes slow (long repeats with a period, a Fibonacci word, a long run
    /// broken by single bytes) take no longer than ordinary ones: the transform is linear in the block size.
    /// </summary>
    [Fact]
    public void Blocks_that_are_hard_to_sort_are_quick()
    {
        var unit = Noise(1000, 5);
        var repeats = Enumerable.Range(0, 2_000_000).Select(i => unit[i % unit.Length]).ToArray();
        var fibonacci = new StringBuilder("a");
        var previous = "b";
        while (fibonacci.Length < 2_000_000)
        {
            var next = fibonacci.ToString();
            fibonacci.Append(previous);
            previous = next;
        }
        var broken = new byte[2_000_000];
        for (var i = 0; i < broken.Length; i += 251)
            broken[i] = 1;
        foreach (var data in new[] { repeats, Encoding.ASCII.GetBytes(fibonacci.ToString(0, 2_000_000)), broken })
        {
            var watch = Stopwatch.StartNew();
            var bz2 = Compress(data, ArchiveLevel.Maximum, threads: 1);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"{watch.Elapsed.TotalSeconds:F1} s");
            Assert.Equal(data, DecodeOwn(bz2));
        }
    }

    /// <summary>
    /// Mutated data of many shapes (FuzzHarness, the binary mutator) goes through the writer and back through Filee's
    /// decoder and SharpCompress unchanged; a difference fails the input.
    /// </summary>
    [Fact]
    [Trait("Category", "Fuzz")]
    public void Every_mutated_input_round_trips()
    {
        List<byte[]> seeds = [LzmaTests.Prose(60_000), Runs(50_000), Data(Kind.Periodic, 40_000), Mixed(300_000), [0x42], Noise(20_000, 9)];
        var threads = 0;
        new FuzzHarness(fx, quickIterations: 20, quickSeed: 20261114).Data("bzip2-writer", "bin", seeds, FuzzHarness.Bytes, input =>
        {
            var bz2 = Compress(input, (ArchiveLevel)(input.Length % 4), threads: 1 + threads++ % 3);
            if (!DecodeOwn(bz2).AsSpan().SequenceEqual(input) || !DecodeSharpCompress(bz2).AsSpan().SequenceEqual(input))
                throw new InvalidOperationException($"{input.Length} bytes did not come back unchanged");
        });
    }

    // ───────── Speed ─────────

    /// <summary>
    /// A mixed sample in 1 MB pieces: prose, CSV, x86-like code and noise, so the blocks are as different as real files
    /// in a TAR.
    /// </summary>
    private static byte[] MixedSample(int megabytes)
    {
        var bytes = new byte[megabytes << 20];
        var csv = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(0, 60_000).Select(i =>
            string.Create(CultureInfo.InvariantCulture, $"{i},{i * 37 % 1000},{i * 0.25:F2},항목{i % 97}"))));
        for (var i = 0; i < megabytes; i++)
        {
            var piece = (i % 5) switch
            {
                0 or 1 => LzmaTests.Prose(1 << 20),
                2 => csv,
                3 => SevenZipReaderTests.X86Like(1 << 20, i),
                _ => Noise(1 << 20, i),
            };
            piece.AsSpan(0, Math.Min(piece.Length, 1 << 20)).CopyTo(bytes.AsSpan(i << 20));
        }
        return bytes;
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task Speed_and_size_next_to_7zip()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("FILEE_ARCHIVE_BENCHMARK") == "1", "set FILEE_ARCHIVE_BENCHMARK=1");
        Assert.SkipUnless(SevenZipExe is not null, "7-Zip not found");
        var megabytes = int.TryParse(Environment.GetEnvironmentVariable("FILEE_ARCHIVE_BENCHMARK_MB"), out var mb) ? mb : 30;
        var dir = fx.NewFolder();
        var data = MixedSample(megabytes);
        var source = Path.Combine(dir, "sample.bin");
        await File.WriteAllBytesAsync(source, data, Ct);

        var rows = new List<string>();
        void Row(string writer, string setting, long size, double seconds) =>
            rows.Add(string.Create(CultureInfo.InvariantCulture,
                $"| {writer} | {setting} | {size:N0} | {100.0 * size / data.Length:F2}% | {seconds:F2} s | {data.Length / 1e6 / seconds:F1} MB/s |"));

        foreach (var (level, threads, label) in new[]
        {
            (ArchiveLevel.Fast, 0, "Fast"), (ArchiveLevel.Normal, 0, "Normal"), (ArchiveLevel.Maximum, 0, "Maximum"),
            (ArchiveLevel.Normal, 1, "Normal, 1 thread"),
        })
        {
            var output = Path.Combine(dir, $"ours-{level}-{threads}.bz2");
            var watch = Stopwatch.StartNew();
            await using (var file = File.Create(output))
            await using (var input = File.OpenRead(source))
            {
                using var writer = new Bzip2Writer(file, level, Ct, leaveOpen: true, threads: threads);
                await input.CopyToAsync(writer, Ct);
                writer.Finish();
            }
            Row("Filee", $"{label} ({(threads == 0 ? Bzip2Writer.DefaultThreads : threads)} threads)", new FileInfo(output).Length, watch.Elapsed.TotalSeconds);
            await Run7zAsync("t", output);
        }
        foreach (var switches in new[] { "-mx1", "-mx5", "-mx9", "-mx5 -mmt=1" })
        {
            var output = Path.Combine(dir, $"7z{switches.Replace(' ', '_')}.bz2");
            var watch = Stopwatch.StartNew();
            await Run7zAsync(["a", "-tbzip2", .. switches.Split(' '), output, source]);
            Row("7-Zip", switches, new FileInfo(output).Length, watch.Elapsed.TotalSeconds);
        }
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join('\n',
            [$"{data.Length:N0} bytes, {Environment.ProcessorCount} logical processors", "",
             "| Writer | Setting | Size | Ratio | Time | Speed |", "|---|---|---:|---:|---:|---:|", .. rows]));
    }
}
