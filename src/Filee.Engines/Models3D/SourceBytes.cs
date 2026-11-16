// The bytes of a Source engine model file (.mdl, .vvd, .vtx, .phy, .ani) with bounds-checked little-endian reads: an
// offset or count that points outside the file is a damaged file, never a crash or a read of other memory.

using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Filee.Engines.Models3D;

/// <summary>A Source engine file's bytes with bounds-checked little-endian reads.</summary>
internal sealed class SourceBytes(byte[] data, string file)
{
    /// <summary>The largest Source engine file Filee reads (a damaged or made-up file otherwise).</summary>
    public const long MaxFileSize = 256L * 1024 * 1024;

    public byte[] Data { get; } = data;
    public string File { get; } = file;

    /// <summary>Reads <paramref name="path"/>, refusing files over <see cref="MaxFileSize"/>.</summary>
    public static SourceBytes Read(string path)
    {
        var length = new FileInfo(path).Length;
        if (length > MaxFileSize)
            throw new InvalidDataException($"{Path.GetFileName(path)} is too large to be a Source engine model file.");
        return new SourceBytes(System.IO.File.ReadAllBytes(path), Path.GetFileName(path));
    }

    public InvalidDataException Damaged(string what) => new($"{File} is damaged ({what} points outside the file).");

    private ReadOnlySpan<byte> At(int offset, int length) =>
        offset >= 0 && (long)offset + length <= Data.Length ? Data.AsSpan(offset, length) : throw Damaged("an offset");

    /// <summary>Whether <paramref name="count"/> items of <paramref name="size"/> bytes from <paramref name="offset"/> lie in the file.</summary>
    public bool Fits(long offset, long count, int size) => offset >= 0 && count >= 0 && offset + count * size <= Data.Length;

    public int I32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(At(offset, 4));
    public uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(At(offset, 4));
    public ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(At(offset, 8));
    public int I16(int offset) => BinaryPrimitives.ReadInt16LittleEndian(At(offset, 2));
    public int U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(At(offset, 2));
    public byte Byte(int offset) => At(offset, 1)[0];
    public float F32(int offset) => BinaryPrimitives.ReadSingleLittleEndian(At(offset, 4));
    public float F16(int offset) => (float)BinaryPrimitives.ReadHalfLittleEndian(At(offset, 2));
    public Vector3 Vector(int offset) => new(F32(offset), F32(offset + 4), F32(offset + 8));

    /// <summary>A count at <paramref name="offset"/>, at most <paramref name="max"/>.</summary>
    public int Count(int offset, int max, string what)
    {
        var count = I32(offset);
        if (count < 0 || count > max)
            throw new InvalidDataException($"{File} is damaged (it claims {count} {what}).");
        return count;
    }

    /// <summary>A NUL-terminated name at <paramref name="offset"/>, at most <paramref name="max"/> bytes.</summary>
    public string String(int offset, int max = 1024)
    {
        if (offset < 0 || offset >= Data.Length)
            throw Damaged("a name");
        var span = Data.AsSpan(offset, Math.Min(max, Data.Length - offset));
        var end = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }

    /// <summary>A name at <paramref name="baseOffset"/> + the relative offset stored at <paramref name="field"/>; "" for offset 0.</summary>
    public string RelativeString(int baseOffset, int field, int max = 1024)
    {
        var relative = I32(field);
        return relative == 0 ? "" : String(baseOffset + relative, max);
    }
}
