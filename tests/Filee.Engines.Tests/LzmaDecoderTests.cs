// Filee's LZMA decoder (Archives/Lzma/LzmaDecoder.cs) against inputs that reach its edges: every lc / lp / pb, small
// dictionaries whose window wraps many times, matches that overlap themselves or cross the end of the window, reads
// of every size (so matches stay pending across calls), and input streams that hand out a few bytes at a time (so the
// decoder's input margin runs out in the middle of symbols and the buffer is topped up or read with checks). The
// streams come from Filee's own encoder and, where the bundled 7-Zip is present, from 7-Zip.

using Filee.Engines.Archives;
using Filee.Engines.Archives.Lzma;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

public class LzmaDecoderTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Text, noise, byte runs, short-period repeats (overlapping matches) and far copies with a few changes.</summary>
    private static byte[] Structured(int length, int seed)
    {
        var random = new Random(seed);
        var bytes = new byte[length];
        var at = 0;
        while (at < length)
        {
            var run = Math.Min(length - at, random.Next(1, 12_000));
            switch (random.Next(5))
            {
                case 0:
                    random.NextBytes(bytes.AsSpan(at, run));
                    break;
                case 1:
                    Words(run).CopyTo(bytes, at);
                    break;
                case 2:
                    bytes.AsSpan(at, run).Fill((byte)random.Next(256));
                    break;
                case 3:
                    var period = random.Next(1, 24);
                    for (var i = 0; i < run; i++)
                        bytes[at + i] = i < period ? (byte)random.Next(256) : bytes[at + i - period];
                    break;
                default:
                    var from = at == 0 ? 0 : random.Next(at);
                    for (var i = 0; i < run; i++)
                        bytes[at + i] = at == 0 ? (byte)i : bytes[from + i % (at - from)];
                    for (var k = random.Next(4); k > 0; k--)
                        bytes[at + random.Next(run)] ^= (byte)random.Next(1, 256);
                    break;
            }
            at += run;
        }
        return bytes;
    }

    /// <summary>Raw LZMA (no end marker) from Filee's encoder with the given properties and dictionary.</summary>
    private static byte[] EncodeRaw(byte[] data, LzmaProperties properties, int dictionary, bool optimal)
    {
        var settings = optimal ? new LzmaEncoderSettings(32, 64, Optimal: true, Tree: true) : new LzmaEncoderSettings(8, 32, Optimal: false, Tree: false);
        var finder = new MatchFinder(dictionary, data.Length + 1024, settings.Depth, settings.Nice, settings.Tree);
        Assert.Equal(data.Length, finder.Append(data));
        var encoder = new LzmaEncoder(finder, properties, settings);
        var rc = new RangeEncoder(data.Length + data.Length / 8 + 1024);
        encoder.Encode(rc, data.Length, long.MaxValue);
        rc.Flush();
        return rc.Output.ToArray();
    }

    /// <summary>Reads <paramref name="stream"/> to its end in pieces of random size (1 byte … 200 KB).</summary>
    private static byte[] ReadInPieces(Stream stream, int seed)
    {
        var random = new Random(seed);
        var buffer = new byte[200_000];
        using var output = new MemoryStream();
        while (true)
        {
            var want = random.Next(4) switch
            {
                0 => random.Next(1, 4),
                1 => random.Next(1, 300),
                2 => random.Next(1, 70_000),
                _ => buffer.Length,
            };
            var n = stream.Read(buffer, 0, want);
            if (n == 0)
                return output.ToArray();
            output.Write(buffer, 0, n);
        }
    }

    /// <summary>A stream that hands out at most <c>most</c> bytes per read.</summary>
    private sealed class Trickle(byte[] data, int most) : Stream
    {
        private int _at;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, most), data.Length - _at);
            data.AsSpan(_at, n).CopyTo(buffer.AsSpan(offset));
            _at += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _at; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(3, 0, 2)]
    [InlineData(0, 0, 0)]
    [InlineData(8, 0, 0)]
    [InlineData(0, 4, 4)]
    [InlineData(1, 3, 0)]
    [InlineData(4, 0, 4)]
    [InlineData(8, 4, 4)]
    public void Raw_lzma_with_any_properties_decodes_through_any_read_and_input_size(int lc, int lp, int pb)
    {
        var properties = new LzmaProperties(lc, lp, pb);
        var data = Structured(150_000, lc * 25 + lp * 5 + pb);
        // 4 KiB: the window wraps some 35 times; 64 KiB: the window grows, then wraps; 1 MiB: it only grows.
        foreach (var dictionary in new[] { 1 << 12, 1 << 16, 1 << 20 })
        {
            var packed = EncodeRaw(data, properties, dictionary, optimal: dictionary == 1 << 16);
            foreach (var most in new[] { int.MaxValue, 7, 65 })
            {
                using var reader = new LzmaRawReader(most == int.MaxValue ? new MemoryStream(packed) : new Trickle(packed, most),
                    properties, (uint)dictionary, data.Length);
                var decoded = ReadInPieces(reader, dictionary + most);
                Assert.True(data.AsSpan().SequenceEqual(decoded), $"lc={lc} lp={lp} pb={pb} dictionary={dictionary} reads of {most}");
            }
        }
    }

    [Fact]
    public void Lzma2_with_a_small_dictionary_decodes_through_any_read_size()
    {
        var data = Structured(600_000, 7);
        foreach (var settings in new[]
        {
            new Lzma2Settings(1 << 12, new LzmaEncoderSettings(8, 32, Optimal: false, Tree: false), Store: false),
            new Lzma2Settings(1 << 15, new LzmaEncoderSettings(32, 64, Optimal: true, Tree: true), Store: false),
        })
        {
            using var packed = new MemoryStream();
            var writer = new Lzma2Writer(packed, settings, data.Length, CancellationToken.None);
            writer.Write(data);
            writer.Finish();
            foreach (var expected in new long[] { -1, data.Length })
            {
                var reader = new Lzma2Reader(new ByteInput(new MemoryStream(packed.ToArray())), (uint)settings.Dictionary, expected, Ct);
                using var output = new MemoryStream();
                var random = new Random(settings.Dictionary);
                var buffer = new byte[100_000];
                int n;
                while ((n = reader.Read(buffer.AsSpan(0, random.Next(1, buffer.Length)))) > 0)
                    output.Write(buffer, 0, n);
                Assert.True(data.AsSpan().SequenceEqual(output.ToArray()), $"dictionary {settings.Dictionary}, expected size {expected}");
            }
        }
    }

    [Fact]
    public void A_stream_that_ends_inside_a_symbol_is_damaged_however_it_is_read()
    {
        var data = Structured(100_000, 3);
        var packed = EncodeRaw(data, LzmaProperties.Default, 1 << 16, optimal: false);
        foreach (var cut in new[] { 5, 60, 64, 65, packed.Length / 2, packed.Length - 70, packed.Length - 1 })
        {
            foreach (var most in new[] { int.MaxValue, 3, 64 })
            {
                var part = packed[..cut];
                Assert.Throws<InvalidDataException>(() =>
                {
                    using var reader = new LzmaRawReader(most == int.MaxValue ? new MemoryStream(part) : new Trickle(part, most),
                        LzmaProperties.Default, 1 << 16, data.Length);
                    ReadInPieces(reader, cut);
                });
            }
        }
    }

    [Fact]
    public async Task Seven_zip_lzma_with_other_properties_and_small_dictionaries_decodes()
    {
        Assert.SkipUnless(SevenZipExe is not null, "7-Zip not found");
        var dir = fx.NewFolder();
        var source = Path.Combine(dir, "in");
        Directory.CreateDirectory(source);
        var data = Structured(700_000, 11);
        await File.WriteAllBytesAsync(Path.Combine(source, "data.bin"), data, Ct);
        string[] methods =
        [
            "-m0=LZMA:d=4k:lc=8:lp=0:pb=0", "-m0=LZMA:d=64k:lc=0:lp=4:pb=4", "-m0=LZMA:d=1m:lc=4:lp=2:pb=1:a=0",
            "-m0=LZMA2:d=4k:lc=1:lp=3:pb=0", "-m0=LZMA2:d=256k:lc=0:lp=0:pb=4:fb=273", "-m0=LZMA:mf=hc4:d=32k",
        ];
        foreach (var method in methods)
        {
            var archive = Path.Combine(dir, $"{methods.ToList().IndexOf(method)}.7z");
            await Run7zAsync("a", "-t7z", "-mmt=off", method, archive, Path.Combine(source, "data.bin"));
            var target = Path.Combine(dir, "out-" + Path.GetFileNameWithoutExtension(archive));
            using (var file = File.OpenRead(archive))
                SevenZipReader.Extract(file, Path.GetFileName(archive), target, Ct);
            var decoded = await File.ReadAllBytesAsync(Path.Combine(target, "data.bin"), Ct);
            Assert.True(data.AsSpan().SequenceEqual(decoded), method);
        }
    }
}
