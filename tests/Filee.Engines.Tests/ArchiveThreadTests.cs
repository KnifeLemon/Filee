// Archives coded and decoded on several threads (Lzma2Writer blocks, Lzma2Parallel, XzReader blocks, Bzip2Decoder's
// stream): the same bytes as on one thread, the same errors at the same place, 7-Zip reads what Filee writes (skipped
// when it is missing), and cancellation stops the workers. Block sizes are made small so that little data gives many
// blocks.

using Filee.Core.Presets;
using Filee.Engines.Archives;
using Filee.Engines.Archives.Lzma;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

public class ArchiveThreadTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int SmallBlock = 256 << 10;

    /// <summary>Runs <paramref name="action"/> with LZMA2 blocks of <see cref="SmallBlock"/> and the given threads.</summary>
    private static T WithBlocks<T>(int? threads, Func<T> action)
    {
        Lzma2Writer.TestBlockSize.Value = SmallBlock;
        Lzma2Writer.TestThreads.Value = threads;
        try
        {
            return action();
        }
        finally
        {
            Lzma2Writer.TestBlockSize.Value = null;
            Lzma2Writer.TestThreads.Value = null;
        }
    }

    /// <summary>Runs <paramref name="action"/> with decoding threads off (false) or on for any data (true).</summary>
    private static T Decoding<T>(bool threads, Func<T> action)
    {
        Lzma2Parallel.TestMode.Value = threads;
        try
        {
            return action();
        }
        finally
        {
            Lzma2Parallel.TestMode.Value = null;
        }
    }

    private static byte[] Xz(byte[] data, ArchiveLevel level, int? threads) => WithBlocks(threads, () =>
    {
        using var output = new MemoryStream();
        using (var xz = new XzWriter(output, level, data.Length, Ct, leaveOpen: true))
        {
            for (var at = 0; at < data.Length; at += 100_003)
                xz.Write(data, at, Math.Min(100_003, data.Length - at));
            xz.Finish();
        }
        return output.ToArray();
    });

    /// <summary>Reads all of <paramref name="stream"/> in reads of odd sizes.</summary>
    private static byte[] ReadAll(Stream stream)
    {
        using var output = new MemoryStream();
        var buffer = new byte[70_001];
        var random = new Random(5);
        int read;
        while ((read = stream.Read(buffer, 0, random.Next(1, buffer.Length))) > 0)
            output.Write(buffer, 0, read);
        return output.ToArray();
    }

    private static byte[] Unxz(byte[] xz, bool threads) => Decoding(threads, () =>
    {
        using var reader = new XzReader(new MemoryStream(xz), "test.xz", cancellationToken: Ct);
        return ReadAll(reader);
    });

    /// <summary>The bytes read before the error and the error, of reading <paramref name="xz"/> through.</summary>
    private static (int Read, Exception Error) UnxzFailing(byte[] xz, bool threads) => Decoding(threads, () =>
    {
        using var reader = new XzReader(new MemoryStream(xz), "test.xz", cancellationToken: Ct);
        var buffer = new byte[1 << 16];
        var total = 0;
        try
        {
            int read;
            while ((read = reader.Read(buffer)) > 0)
                total += read;
        }
        catch (Exception ex)
        {
            return (total, ex);
        }
        throw new Xunit.Sdk.XunitException("the damaged .xz read without an error");
    });

    [Theory]
    [InlineData(ArchiveLevel.Store)]
    [InlineData(ArchiveLevel.Fast)]
    [InlineData(ArchiveLevel.Normal)]
    public void Lzma2_blocks_give_the_same_bytes_on_one_thread_or_four(ArchiveLevel level)
    {
        var data = Bzip2WriterTests.Mixed(2_000_000);
        var one = Xz(data, level, threads: 1);
        var four = Xz(data, level, threads: 4);
        Assert.Equal(one, four);
        Assert.Equal(data, Unxz(four, threads: false));
        Assert.Equal(data, Unxz(four, threads: true));
    }

    [Theory]
    [InlineData(ArchiveLevel.Normal)]
    [InlineData(ArchiveLevel.Maximum)]
    public void The_match_finder_on_its_own_thread_gives_the_same_bytes(ArchiveLevel level)
    {
        var data = LzmaTests.Prose(700_000);
        byte[] Coded(bool ahead)
        {
            Lzma2Writer.TestSearchAhead.Value = ahead;
            try
            {
                return Xz(data, level, threads: 1);
            }
            finally
            {
                Lzma2Writer.TestSearchAhead.Value = null;
            }
        }
        var alone = Coded(false);
        Assert.Equal(alone, Coded(true));
        Assert.Equal(data, Unxz(alone, threads: true));
    }

    [Fact]
    public async Task An_xz_of_several_blocks_names_its_sizes_and_opens_in_7zip()
    {
        var data = LzmaTests.Prose(1_500_000);
        var xz = Xz(data, ArchiveLevel.Normal, threads: 3);
        // One block header per 256 KiB block, each with both sizes (flags 0xC0).
        var blocks = 0;
        for (var at = 12; xz[at] != 0;)
        {
            Assert.Equal(0xC0, xz[at + 1]);
            var size = (xz[at] + 1) * 4;
            var packed = 0L;
            var shift = 0;
            var p = at + 2;
            do
            {
                packed |= (long)(xz[p] & 0x7F) << shift;
                shift += 7;
            } while ((xz[p++] & 0x80) != 0);
            at += size + (int)((packed + 3) & ~3L) + 8;
            blocks++;
        }
        Assert.Equal((data.Length + SmallBlock - 1) / SmallBlock, blocks);
        if (SevenZipExe is null)
            return;
        var dir = fx.NewFolder();
        var file = Path.Combine(dir, "data.bin.xz");
        await File.WriteAllBytesAsync(file, xz, Ct);
        await Run7zAsync("t", file);
        await Run7zAsync("e", "-y", "-o" + Path.Combine(dir, "out"), file);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(dir, "out", "data.bin"), Ct));
    }

    [Fact]
    public void Damage_in_a_later_xz_block_is_the_same_error_on_threads()
    {
        var data = LzmaTests.Prose(1_500_000);
        var xz = Xz(data, ArchiveLevel.Fast, threads: 2);
        foreach (var at in new[] { xz.Length / 2, xz.Length * 3 / 4, xz.Length - 40 })
        {
            var damaged = (byte[])xz.Clone();
            damaged[at] ^= 0x20;
            var (oneRead, oneError) = UnxzFailing(damaged, threads: false);
            var (manyRead, manyError) = UnxzFailing(damaged, threads: true);
            Assert.Equal(oneError.GetType(), manyError.GetType());
            Assert.Equal(oneError.Message, manyError.Message);
            // The damaged block is decoded again on the reader's thread: the same bytes come out before the error.
            Assert.Equal(oneRead, manyRead);
        }
        // Cut off in the middle: the same error as well.
        var cut = xz[..(xz.Length * 2 / 3)];
        Assert.Equal(UnxzFailing(cut, threads: false).Error.Message, UnxzFailing(cut, threads: true).Error.Message);
    }

    [Fact]
    public void A_cancelled_xz_read_on_threads_stops()
    {
        var xz = Xz(LzmaTests.Prose(1_200_000), ArchiveLevel.Fast, threads: 2);
        using var cancel = new CancellationTokenSource();
        Lzma2Parallel.TestMode.Value = true;
        try
        {
            using var reader = new XzReader(new MemoryStream(xz), "test.xz", cancellationToken: cancel.Token);
            var buffer = new byte[1 << 16];
            Assert.True(reader.Read(buffer) > 0);
            cancel.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() =>
            {
                while (reader.Read(buffer) > 0)
                {
                }
            });
        }
        finally
        {
            Lzma2Parallel.TestMode.Value = null;
        }
    }

    /// <summary>A 7z of one solid folder whose LZMA2 data has a dictionary reset every <see cref="SmallBlock"/> bytes.</summary>
    private static byte[] SevenZip(params (string Name, byte[] Data)[] files) => WithBlocks(threads: 2, () =>
    {
        using var output = new MemoryStream();
        var writer = new SevenZipWriter(output, ArchiveLevel.Fast, files.Sum(f => (long)f.Data.Length), Ct);
        foreach (var (name, data) in files)
            writer.AddFile(name, new MemoryStream(data), data.Length, DateTime.UtcNow, 0x20);
        writer.Finish();
        return output.ToArray();
    });

    private Dictionary<string, byte[]> Un7z(byte[] archive, bool threads) => Decoding(threads, () =>
    {
        var dir = fx.NewFolder();
        SevenZipReader.Extract(new MemoryStream(archive), "test.7z", dir, Ct);
        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), File.ReadAllBytes);
    });

    [Fact]
    public async Task A_7z_with_dictionary_resets_unpacks_on_threads_like_on_one()
    {
        var files = new[] { ("a.txt", LzmaTests.Prose(900_000)), ("b.bin", Noise(300_000, 3)), ("c.txt", LzmaTests.Prose(700_000)) };
        var archive = SevenZip(files);
        foreach (var threads in new[] { false, true })
        {
            var opened = Lzma2Parallel.Opened;
            var unpacked = Un7z(archive, threads);
            foreach (var (name, data) in files)
                Assert.Equal(data, unpacked[name]);
            if (threads)
                Assert.True(Lzma2Parallel.Opened > opened, "the LZMA2 data was not decoded on threads");
        }
        if (SevenZipExe is null)
            return;
        var dir = fx.NewFolder();
        var file = Path.Combine(dir, "test.7z");
        await File.WriteAllBytesAsync(file, archive, Ct);
        await Run7zAsync("t", file);
    }

    [Fact]
    public void Damage_in_a_later_7z_segment_is_the_same_error_on_threads()
    {
        var archive = SevenZip(("a.txt", LzmaTests.Prose(1_200_000)), ("b.txt", LzmaTests.Prose(600_000)));
        // Past the signature header, somewhere in the second half of the packed data.
        foreach (var at in new[] { archive.Length / 2, archive.Length * 2 / 3 })
        {
            var damaged = (byte[])archive.Clone();
            damaged[at] ^= 0x08;
            Exception Fail(bool threads) => Assert.ThrowsAny<Exception>(() => Un7z(damaged, threads));
            var one = Fail(false);
            var many = Fail(true);
            Assert.Equal(one.GetType(), many.GetType());
            Assert.Equal(one.Message, many.Message);
        }
    }

    // ───────── bzip2 ─────────

    private static byte[] Bz2(byte[] data)
    {
        using var output = new MemoryStream();
        using (var writer = new Bzip2Writer(output, ArchiveLevel.Fast, Ct, leaveOpen: true))
        {
            writer.Write(data);
            writer.Finish();
        }
        return output.ToArray();
    }

    private static byte[] PushDecode(byte[] bz2)
    {
        using var output = new MemoryStream();
        Bzip2Decoder.Decompress(new MemoryStream(bz2), output, alzFraming: false, long.MaxValue, Ct);
        return output.ToArray();
    }

    private static byte[] StreamDecode(byte[] bz2)
    {
        using var stream = Bzip2Decoder.OpenRead(new MemoryStream(bz2), Ct);
        return ReadAll(stream);
    }

    [Theory]
    [InlineData(Bzip2WriterTests.Kind.Empty, 0)]
    [InlineData(Bzip2WriterTests.Kind.One, 1)]
    [InlineData(Bzip2WriterTests.Kind.Text, 1_000_000)]
    [InlineData(Bzip2WriterTests.Kind.Runs, 1_500_000)]
    [InlineData(Bzip2WriterTests.Kind.Mixed, 2_500_000)]
    [InlineData(Bzip2WriterTests.Kind.Edge, 0)]
    public void The_bzip2_stream_reads_what_the_writer_wrote(Bzip2WriterTests.Kind kind, int length)
    {
        var data = Bzip2WriterTests.Data(kind, length);
        var bz2 = Bz2(data);
        Assert.Equal(data, StreamDecode(bz2));
        // Two streams one after the other (as pbzip2 writes, or "cat a.bz2 b.bz2").
        Assert.Equal([.. data, .. data], StreamDecode([.. bz2, .. bz2]));
    }

    [Fact]
    public void Damaged_bzip2_gives_the_same_error_as_the_one_thread_decoder()
    {
        var data = Bzip2WriterTests.Mixed(2_500_000);
        var bz2 = Bz2(data);
        var random = new Random(9);
        for (var i = 0; i < 30; i++)
        {
            var damaged = (byte[])bz2.Clone();
            if (i < 25)
                damaged[random.Next(4, damaged.Length)] ^= (byte)(1 << random.Next(8));
            else
                damaged = damaged[..random.Next(4, damaged.Length)];
            Exception? push = null, pull = null;
            try
            {
                PushDecode(damaged);
            }
            catch (Exception ex)
            {
                push = ex;
            }
            try
            {
                StreamDecode(damaged);
            }
            catch (Exception ex)
            {
                pull = ex;
            }
            Assert.Equal(push?.GetType(), pull?.GetType());
            Assert.Equal(push?.Message, pull?.Message);
        }
    }

    [Fact]
    public void Randomised_bzip2_blocks_go_to_the_fallback_decoder_past_what_was_read()
    {
        // SharpCompress's encoder randomises very repetitive blocks, as bzip2 0.9.0 did; text first, so some blocks
        // come out of Filee's decoder before the randomised one.
        byte[] data = [.. LzmaTests.Prose(2_000_000), .. new byte[3_000_000], .. LzmaTests.Prose(300_000)];
        var bz2 = Bzip2(data);
        var error = Assert.Throws<InvalidDataException>(() => PushDecode(bz2));
        Assert.Contains("randomised", error.Message);
        Stream Fallback() => new SharpCompress.Compressors.BZip2.BZip2Stream(new MemoryStream(bz2),
            SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: true);
        using (var stream = Bzip2Decoder.OpenRead(new MemoryStream(bz2), Ct, Fallback))
            Assert.Equal(data, ReadAll(stream));
        // Without one, the stream refuses it as before.
        using (var stream = Bzip2Decoder.OpenRead(new MemoryStream(bz2), Ct))
            Assert.Contains("randomised", Assert.Throws<InvalidDataException>(() => ReadAll(stream)).Message);
    }

    [Fact]
    public void A_cancelled_bzip2_stream_stops()
    {
        var bz2 = Bz2(LzmaTests.Prose(3_000_000));
        using var cancel = new CancellationTokenSource();
        using var stream = Bzip2Decoder.OpenRead(new MemoryStream(bz2), cancel.Token);
        var buffer = new byte[1 << 16];
        Assert.True(stream.Read(buffer) > 0);
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            while (stream.Read(buffer) > 0)
            {
            }
        });
    }
}
