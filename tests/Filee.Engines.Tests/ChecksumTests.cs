// The archive checksums: CRC-32 (Archives/Crc32, System.IO.Hashing underneath) and LHA's CRC-16 (Crc16Stream) give
// the same values as the one-table-lookup-per-byte loops they replaced (kept here as the reference), for random
// lengths up to 1 MB, starts at any alignment and data fed in pieces of random size. The check values are those of
// the CRC catalogues ("123456789").

using System.Text;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

public class ChecksumTests
{
    private const int Rounds = 80;
    private const int MaxLength = 1 << 20;

    private static readonly uint[] ReferenceCrc32Table = ReflectedTable(0xEDB88320);
    private static readonly uint[] ReferenceCrc16Table = ReflectedTable(0xA001);

    [Fact]
    public void Check_values_match_the_catalogues()
    {
        var check = Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xCBF43926u, Crc32.Compute(check));
        Assert.Equal(0xBB3D, Crc16(check));
        Assert.Equal(0u, Crc32.Compute([]));
        Assert.Equal(0, Crc16([]));
    }

    [Fact]
    public void Crc32_matches_the_byte_table_whole_and_in_pieces()
    {
        var random = new Random(20261115);
        var pool = Noise(MaxLength + 64, 1);
        foreach (var (data, pieces) in Cases(random, pool))
        {
            var expected = ~Reflected(ReferenceCrc32Table, 0xFFFFFFFF, data.Span);
            Assert.Equal(expected, Crc32.Compute(data.Span));
            var crc = new Crc32();
            var reference = 0xFFFFFFFFu;
            foreach (var piece in pieces)
            {
                var part = data.Span.Slice(piece.Start, piece.Length);
                crc.Append(part);
                reference = Reflected(ReferenceCrc32Table, reference, part);
                Assert.Equal(~reference, crc.Value);
            }
            Assert.Equal(expected, crc.Value);
        }
    }

    [Fact]
    public void Crc16_matches_the_byte_table_whole_and_through_reads_of_any_size()
    {
        var random = new Random(20261116);
        var pool = Noise(MaxLength + 64, 2);
        foreach (var (data, pieces) in Cases(random, pool))
        {
            var expected = (ushort)Reflected(ReferenceCrc16Table, 0, data.Span);
            Assert.Equal(expected, Crc16(data.Span));

            ushort chained = 0;
            foreach (var piece in pieces)
                chained = Crc16Stream.Update(chained, data.Span.Slice(piece.Start, piece.Length));
            Assert.Equal(expected, chained);

            // As LhaReader uses it: reads at unaligned buffer offsets from a stream that returns short reads.
            using var stream = new Crc16Stream(new ChunkedStream(data.ToArray(), random));
            var buffer = new byte[64 * 1024 + 3];
            int read;
            do
            {
                var offset = random.Next(4);
                read = stream.Read(buffer, offset, random.Next(1, buffer.Length - offset + 1));
            }
            while (read > 0);
            Assert.Equal(expected, stream.Value);
        }
    }

    /// <summary>
    /// Data of random length (the first 40 are 0 to 39 bytes, around the 16-byte step) at a random offset into
    /// <paramref name="pool"/>, with a random split into consecutive pieces.
    /// </summary>
    private static IEnumerable<(ReadOnlyMemory<byte> Data, List<(int Start, int Length)> Pieces)> Cases(Random random, byte[] pool)
    {
        for (var round = 0; round < Rounds; round++)
        {
            var length = round < 40 ? round : random.Next(MaxLength + 1);
            var start = random.Next(64);
            var pieces = new List<(int Start, int Length)>();
            for (var at = 0; at < length;)
            {
                var piece = Math.Min(length - at, random.Next(4) switch
                {
                    0 => random.Next(1, 17),
                    1 => random.Next(1, 200),
                    2 => random.Next(1, 70_000),
                    _ => random.Next(1, length + 1),
                });
                pieces.Add((at, piece));
                at += piece;
            }
            yield return (pool.AsMemory(start, length), pieces);
        }
    }

    private static ushort Crc16(ReadOnlySpan<byte> data) => Crc16Stream.Update(0, data);

    private static uint Reflected(uint[] table, uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] ReflectedTable(uint polynomial)
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? polynomial ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>A read-only stream that hands out fewer bytes than asked, a random number each time.</summary>
    private sealed class ChunkedStream(byte[] data, Random random) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(Math.Min(buffer.Length, random.Next(1, 70_000)), data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
