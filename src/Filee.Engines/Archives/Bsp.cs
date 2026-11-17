// Source engine maps (.bsp): the files packed into a map (custom textures, models and sounds; the "pakfile" lump, made by
// VBSP and tools such as VIDE or bspzip) are a ZIP stored inside the map. Filee copies that ZIP out and 7-Zip unpacks it.
//
//   "VBSP" + version i32 (19/20 in Garry's Mod, up to 21), then 64 lumps of 16 bytes and the map revision;
//   a lump is offset i32, length i32, version i32 and a four-byte code (its uncompressed size when it is compressed).
//   Left 4 Dead 2 writes version, offset, length instead; the order that points inside the file is used.
//   Lump 40 is the pakfile.

using System.Buffers.Binary;

namespace Filee.Engines.Archives;

/// <summary>Takes the packed files out of Source engine maps.</summary>
internal static class Bsp
{
    private const int LumpCount = 64;
    private const int PakfileLump = 40;

    /// <summary>"VBSP", the version and the lump directory (the map revision follows).</summary>
    private const int HeaderSize = 8 + LumpCount * 16 + 4;

    /// <summary>An empty ZIP: just its end record.</summary>
    private const int EmptyZip = 22;

    /// <summary>Where the pakfile lump of <paramref name="map"/> is.</summary>
    internal static (long Offset, long Length) Pakfile(Stream map, string mapName)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        if (map.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) < HeaderSize || !header.StartsWith("VBSP"u8))
            throw new InvalidDataException($"{mapName} is not a Source engine map (VBSP).");
        var version = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        var lump = header.Slice(8 + PakfileLump * 16, 16);
        long a = BinaryPrimitives.ReadInt32LittleEndian(lump);
        long b = BinaryPrimitives.ReadInt32LittleEndian(lump[4..]);
        long c = BinaryPrimitives.ReadInt32LittleEndian(lump[8..]);
        var compressed = BinaryPrimitives.ReadInt32LittleEndian(lump[12..]) != 0;

        bool Inside(long offset, long length) => length == 0 || offset >= HeaderSize && length > 0 && offset + length <= map.Length;
        (long Offset, long Length) found;
        if (Inside(a, b))
            found = (a, b);
        else if (version >= 21 && Inside(b, c)) // Left 4 Dead 2: version, offset, length
            found = (b, c);
        else
            throw ArchiveSafety.Damaged(mapName, "the packed files point outside the map");

        if (found.Length <= EmptyZip)
            throw new InvalidDataException($"{mapName} has no files packed into the map; its textures and models come from the game or an addon.");
        if (compressed)
            throw new InvalidDataException($"{mapName} keeps its packed files compressed (LZMA lump), which Filee does not unpack.");
        return found;
    }

    /// <summary>Copies the packed files' ZIP of <paramref name="map"/> to <paramref name="zipPath"/>.</summary>
    public static void ExtractPakfile(string map, string zipPath, CancellationToken cancellationToken)
    {
        var mapName = Path.GetFileName(map);
        using var stream = new FileStream(map, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var (offset, length) = Pakfile(stream, mapName);
        stream.Position = offset;
        using var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        if (ArchiveFiles.CopyLimited(new BoundedStream(stream, length), output, length, cancellationToken) != length)
            throw ArchiveSafety.Damaged(mapName, "the packed files are cut off");
    }
}
