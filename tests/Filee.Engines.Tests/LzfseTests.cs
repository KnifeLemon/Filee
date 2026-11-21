// Filee's LZFSE decoder (LzfseDecoder, the chunks of LZFSE-compressed DMGs) on streams LzfseBuilders writes in code:
// every block kind (raw, LZVN, FSE with full and compressed headers), mixed in one stream with matches reaching back
// into earlier blocks; then damaged and hostile streams, which must end in InvalidDataException. The ADC decoder of
// DMG chunks (UdifDisk.AdcDecode) is checked the same way.

using System.Buffers.Binary;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

public class LzfseTests
{
    private static byte[] Mixed() =>
        [.. Words(30_000), .. Noise(9_000, 2), .. new byte[20_000], .. Words(12_000), .. Noise(9_000, 2), .. Words(500)];

    private static byte[] Decode(byte[] stream, int size)
    {
        var output = new byte[size];
        var written = new LzfseDecoder().Decode(stream, output);
        return output[..written];
    }

    [Theory]
    [InlineData(LzfseBlock.Raw)]
    [InlineData(LzfseBlock.Lzvn)]
    [InlineData(LzfseBlock.FseV2)]
    [InlineData(LzfseBlock.FseV1)]
    public void Each_block_kind_decodes_back(LzfseBlock kind)
    {
        foreach (var data in new[] { Mixed(), Words(1), Noise(5, 1), [] })
            Assert.Equal(data, Decode(LzfseBuilders.Encode(data, [kind]), data.Length));
    }

    [Fact]
    public void Blocks_of_every_kind_in_one_stream_reach_back_into_each_other()
    {
        var data = Mixed();
        var stream = LzfseBuilders.Encode(data, [LzfseBlock.FseV2, LzfseBlock.Lzvn, LzfseBlock.Raw, LzfseBlock.FseV1], 4096);
        Assert.Equal(data, Decode(stream, data.Length));
        // Noise twice: the second copy is found again several blocks back, by FSE and LZVN blocks alike.
        byte[] noise = [.. Noise(10_000, 3), .. Noise(10_000, 3)];
        var twice = LzfseBuilders.Encode(noise, [LzfseBlock.FseV2, LzfseBlock.Lzvn], 4096);
        Assert.Equal(noise, Decode(twice, noise.Length));
        Assert.True(twice.Length < 12_000, $"{twice.Length} bytes");
    }

    [Fact]
    public void A_stream_larger_than_its_buffer_is_refused()
    {
        var data = Words(10_000);
        foreach (var kind in new[] { LzfseBlock.Raw, LzfseBlock.Lzvn, LzfseBlock.FseV2 })
            Assert.Throws<InvalidDataException>(() => Decode(LzfseBuilders.Encode(data, [kind]), data.Length - 1));
    }

    [Fact]
    public void Damaged_streams_are_refused()
    {
        var data = Words(6_000);
        var v1 = LzfseBuilders.Encode(data, [LzfseBlock.FseV1]);
        var v2 = LzfseBuilders.Encode(data, [LzfseBlock.FseV2]);
        void Refused(byte[] stream) => Assert.Throws<InvalidDataException>(() => Decode(stream, 1 << 20));

        Refused(v2[..^4]); // no end block
        Refused([.. "bvx?"u8]);
        Refused([.. "bvx-"u8, 100, 0, 0, 0, 1, 2, 3]); // raw block longer than the stream
        // Frequencies that sum to more states than the table has (bvx1's first L frequency).
        var sum = (byte[])v1.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(sum.AsSpan(50), 65);
        Refused(sum);
        // More literals than a block may hold, and a state the table doesn't have.
        var literals = (byte[])v2.Clone();
        literals[8] = 0xFF;
        literals[9] = 0xFF;
        literals[10] |= 0x0F;
        Refused(literals);
        var state = (byte[])v1.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(48), 256);
        Refused(state);
        // Padding atop a bit stream that isn't zero: bvx1's lmd_bits made −7, the last byte of its payload 0xFF.
        var padding = (byte[])v1.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(padding.AsSpan(40), -7);
        padding[^5] = 0xFF;
        Refused(padding);
        // LZVN: an invalid opcode, a match before the first byte, a block whose size disagrees with its opcodes.
        Refused([.. "bvxn"u8, 4, 0, 0, 0, 9, 0, 0, 0, 0x70, 0x06, 0, 0, 0, 0, 0, 0, 0, .. "bvx$"u8]);
        Refused([.. "bvxn"u8, 4, 0, 0, 0, 11, 0, 0, 0, 0x07, 5, 0, 0x06, 0, 0, 0, 0, 0, 0, 0, .. "bvx$"u8]);
        Refused([.. "bvxn"u8, 4, 0, 0, 0, 10, 0, 0, 0, 0xE1, (byte)'a', 0x06, 0, 0, 0, 0, 0, 0, 0, .. "bvx$"u8]);
    }

    [Fact]
    public void Mutated_streams_decode_or_are_refused()
    {
        var seeds = new[]
        {
            LzfseBuilders.Encode(Mixed(), [LzfseBlock.FseV2, LzfseBlock.Lzvn, LzfseBlock.Raw, LzfseBlock.FseV1], 8192),
            LzfseBuilders.Encode(Words(3_000), [LzfseBlock.Lzvn]),
        };
        var random = new Random(20261121);
        var decoder = new LzfseDecoder();
        var output = new byte[1 << 17];
        for (var i = 0; i < 3000; i++)
        {
            var bytes = (byte[])seeds[i % seeds.Length].Clone();
            for (var n = random.Next(1, 6); n > 0; n--)
                bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
            try
            {
                decoder.Decode(bytes, output);
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    [Fact]
    public void Adc_chunks_decode_back_and_damaged_ones_are_refused()
    {
        var data = Mixed();
        var packed = DiskImageBuilders.Adc(data);
        Assert.True(packed.Length < data.Length / 2, $"{packed.Length} bytes");
        var output = new byte[data.Length];
        UdifDisk.AdcDecode(packed, output);
        Assert.Equal(data, output);
        Assert.Throws<InvalidDataException>(() => UdifDisk.AdcDecode([0x00, 0x05], new byte[16])); // before the start
        Assert.Throws<InvalidDataException>(() => UdifDisk.AdcDecode([0x85, 1, 2], new byte[16])); // literals cut short
    }
}
