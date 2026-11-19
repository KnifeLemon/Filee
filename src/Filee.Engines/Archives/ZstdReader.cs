// Reads Zstandard data (.zst, the outer layer of .tar.zst, zstd RPM payloads) with SharpCompress's decoder (MIT).
// Frames follow one another (what "zstd -c a b > ab.zst" makes), and skippable frames (magic 0x184D2A5?) are passed
// over. Filee checks the first frame's magic itself, so a file that isn't zstd is "not an archive" instead of an empty
// result, and turns the decoder's errors into Filee's messages. A frame whose window is larger than 128 MiB (made with
// "zstd --long=28" or more) is left to 7-Zip, as the zstd tool refuses it without --memory: the decoder would set the
// window aside before a single byte shows the file is real.

using System.Buffers.Binary;
using SharpCompress.Compressors.ZStandard;
using SharpCompress.Compressors.ZStandard.Unsafe;

namespace Filee.Engines.Archives;

/// <summary>A read-only stream of the data inside a zstd file.</summary>
internal sealed class ZstdReader : Stream
{
    private const uint FrameMagic = 0xFD2FB528;
    private const uint SkippableMagic = 0x184D2A50;

    private readonly DecompressionStream _decoder;
    private readonly string _name;

    /// <summary>Reads zstd frames from the current position of <paramref name="input"/> (which stays open).</summary>
    public ZstdReader(Stream input, string name)
    {
        _name = name;
        Span<byte> magic = stackalloc byte[4];
        var start = input.Position;
        if (input.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) < magic.Length || !IsFrameStart(magic))
            throw ArchiveSafety.NotAnArchive(name);
        input.Position = start;
        _decoder = new DecompressionStream(new NonClosingStream(input), leaveOpen: false);
    }

    /// <summary>True for the magic of a zstd frame or a skippable frame.</summary>
    internal static bool IsFrameStart(ReadOnlySpan<byte> magic)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(magic);
        return value == FrameMagic || (value & 0xFFFFFFF0) == SkippableMagic;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return _decoder.Read(buffer);
        }
        catch (ZstdException ex) when (ex.Code == ZSTD_ErrorCode.ZSTD_error_frameParameter_windowTooLarge)
        {
            throw new NotSupportedException("The zstd window is larger than 128 MiB (made with zstd --long).", ex);
        }
        catch (ZstdException ex)
        {
            throw ArchiveSafety.Damaged(_name, "zstd: " + ex.Message, ex);
        }
        catch (EndOfStreamException ex)
        {
            throw ArchiveSafety.Damaged(_name, "the zstd data ends early", ex);
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _decoder.Dispose();
        base.Dispose(disposing);
    }
}
