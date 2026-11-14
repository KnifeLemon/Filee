// LZMA2 encoding (see Lzma2Reader for the chunk layout). Input collects in the match finder's window; whenever enough
// is waiting, one LZMA chunk is coded: up to 2 MiB of input, stopped early when its output nears 64 KiB. A chunk that
// came out no smaller than its input is written as stored chunks instead, and the next LZMA chunk resets the
// state (the decoder never saw the probabilities the discarded attempt left behind).
// The first chunk resets the dictionary and sets the properties; after a first stored chunk (0x01) the decoder needs
// the properties again, so the next LZMA chunk carries them (0xC0).

using System.Numerics;
using Filee.Core.Presets;

namespace Filee.Engines.Archives.Lzma;

/// <summary>Dictionary and search effort for one <see cref="ArchiveLevel"/>.</summary>
internal readonly record struct Lzma2Settings(int Dictionary, LzmaEncoderSettings Encoder, bool Store)
{
    /// <summary>
    /// The settings for <paramref name="level"/>, with the dictionary cut to the input when its size is known. Memory
    /// for the encoder is about 8 × the dictionary with the hash chain (fast: 4 MiB → ~32 MB) and 10–11 × with the binary
    /// tree (normal: 16 MiB → ~180 MB, maximum: 64 MiB → ~660 MB, as xz -9); unpacking needs only the dictionary.
    /// </summary>
    public static Lzma2Settings For(ArchiveLevel level, long expectedSize = -1)
    {
        var settings = level switch
        {
            ArchiveLevel.Store => new Lzma2Settings(1 << 12, default, Store: true),
            ArchiveLevel.Fast => new Lzma2Settings(1 << 22, new LzmaEncoderSettings(Depth: 8, Nice: 32, Optimal: false, Tree: false), false),
            ArchiveLevel.Maximum => new Lzma2Settings(1 << 26, new LzmaEncoderSettings(Depth: 64, Nice: 64, Optimal: true, Tree: true), false),
            _ => new Lzma2Settings(1 << 24, new LzmaEncoderSettings(Depth: 32, Nice: 32, Optimal: true, Tree: true), false),
        };
        if (expectedSize >= 0)
        {
            var fit = (int)Math.Min(1 << 30, BitOperations.RoundUpToPowerOf2((ulong)Math.Max(expectedSize, 1 << 12)));
            settings = settings with { Dictionary = Math.Min(settings.Dictionary, fit) };
        }
        return settings;
    }
}

internal sealed class Lzma2Writer
{
    private const int StoredChunk = 1 << 16;

    // Stop a chunk while a worst-case symbol (about 25 bytes) still fits into the 64 KiB a chunk may hold.
    private const int PackedLimit = Lzma2Reader.MaxChunkPacked - 64;

    // Bytes kept unread at the end of the window until the input is finished, so hashes can read ahead.
    private const int LookAhead = LzmaModel.MatchMaxLength + 4;

    private readonly Stream _output;
    private readonly Lzma2Settings _settings;
    private readonly MatchFinder? _mf;
    private readonly LzmaEncoder? _encoder;
    private readonly RangeEncoder? _rc;
    private readonly byte[] _stored = new byte[StoredChunk];
    private readonly byte[] _header = new byte[6];
    private readonly CancellationToken _cancellationToken;
    private int _storedCount;
    private bool _first = true;
    private bool _needProperties;
    private bool _needStateReset;

    /// <param name="expectedSize">The input size if known (-1 otherwise); only sizes the window's room for input.</param>
    public Lzma2Writer(Stream output, Lzma2Settings settings, long expectedSize, CancellationToken cancellationToken)
    {
        _output = output;
        _settings = settings;
        _cancellationToken = cancellationToken;
        if (settings.Store)
            return;
        var ahead = Math.Max(Lzma2Reader.MaxChunkUnpacked * 2, settings.Dictionary / 4);
        if (expectedSize >= 0)
            ahead = (int)Math.Min(ahead, expectedSize + LookAhead);
        _mf = new MatchFinder(settings.Dictionary, ahead, settings.Encoder.Depth, settings.Encoder.Nice, settings.Encoder.Tree);
        _encoder = new LzmaEncoder(_mf, LzmaProperties.Default, settings.Encoder);
        _rc = new RangeEncoder(Lzma2Reader.MaxChunkPacked + 1024);
    }

    /// <summary>The dictionary size the decoder needs (for the filter properties).</summary>
    public int Dictionary => _settings.Dictionary;

    /// <summary>Bytes written to the output so far.</summary>
    public long Written { get; private set; }

    public void Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (_mf is null)
            {
                var count = Math.Min(data.Length, StoredChunk - _storedCount);
                data[..count].CopyTo(_stored.AsSpan(_storedCount));
                _storedCount += count;
                data = data[count..];
                if (_storedCount == StoredChunk)
                    FlushStored();
                continue;
            }
            data = data[_mf.Append(data)..];
            if (_mf.Filled == _mf.Buffer.Length)
            {
                EncodeAvailable(final: false);
                _encoder!.Shifted(_mf.Slide());
            }
        }
    }

    /// <summary>Codes what is left and writes the end byte.</summary>
    public void Finish()
    {
        if (_mf is null)
            FlushStored();
        else
            EncodeAvailable(final: true);
        _output.WriteByte(0x00);
        Written++;
    }

    private void FlushStored()
    {
        if (_storedCount == 0)
            return;
        WriteStored(_stored.AsSpan(0, _storedCount));
        _storedCount = 0;
    }

    private void EncodeAvailable(bool final)
    {
        var mf = _mf!;
        var limit = final ? mf.Filled : mf.Filled - LookAhead;
        while (mf.Position < limit)
            EncodeChunk(Math.Min(limit, mf.Position + Lzma2Reader.MaxChunkUnpacked));
    }

    private void EncodeChunk(int end)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var mf = _mf!;
        var rc = _rc!;
        var start = mf.Position;
        if (_needStateReset)
            _encoder!.ResetState();
        rc.Reset();
        _encoder!.Encode(rc, end, PackedLimit);
        rc.Flush();
        var unpacked = mf.Position - start;
        var packed = rc.Output.Length;

        if (packed + 6 >= unpacked + 3 * ((unpacked + StoredChunk - 1) / StoredChunk))
        {
            // Didn't shrink: store it. The decoder's state doesn't see the attempt, so the next LZMA chunk resets it.
            WriteStored(mf.Buffer.AsSpan(start, unpacked));
            return;
        }

        int control;
        if (_first)
            control = 0xE0;
        else if (_needProperties)
            control = 0xC0;
        else if (_needStateReset)
            control = 0xA0;
        else
            control = 0x80;
        var u = unpacked - 1;
        var p = packed - 1;
        _header[0] = (byte)(control | (u >> 16));
        _header[1] = (byte)(u >> 8);
        _header[2] = (byte)u;
        _header[3] = (byte)(p >> 8);
        _header[4] = (byte)p;
        var headerLength = 5;
        if (control >= 0xC0)
            _header[headerLength++] = LzmaProperties.Default.ToByte();
        _output.Write(_header, 0, headerLength);
        _output.Write(rc.Output);
        Written += headerLength + packed;
        _first = false;
        _needProperties = false;
        _needStateReset = false;
    }

    private void WriteStored(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var count = Math.Min(data.Length, StoredChunk);
            var u = count - 1;
            _header[0] = (byte)(_first ? 0x01 : 0x02);
            _header[1] = (byte)(u >> 8);
            _header[2] = (byte)u;
            _output.Write(_header, 0, 3);
            _output.Write(data[..count]);
            Written += 3 + count;
            if (_first)
                _needProperties = true;
            _first = false;
            _needStateReset = true;
            data = data[count..];
        }
    }
}
