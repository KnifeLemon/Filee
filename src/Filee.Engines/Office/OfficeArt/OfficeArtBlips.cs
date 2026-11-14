// OfficeArt pictures ([MS-ODRAW] 2.2.23, 2.2.32): an FBSE in the BStore names a BLIP, stored right after it or, at
// foDelay, in another stream (WordDocument in DOC, Pictures in PPT). A BLIP is one or two 16-byte UIDs and then its
// data: JPEG, PNG and TIFF as they are, a DIB without its BMP file header, and EMF / WMF behind a metafile header,
// usually deflated (zlib). A WMF lacks its placeable header, which is rebuilt from the bounds and size in the header.
//
// Inflating counts every byte it unpacks against the document (the caller's counter: its total, MaxPackageBytes) and
// stops at the size the header gives and MaxPartBytes; a picture that unpacks to more than MaxInflateRatio times its
// packed size (past InflateRatioFloor) is refused with the document limits' error.

using System.Buffers.Binary;
using System.IO.Compression;
using Filee.Engines.Documents;

namespace Filee.Engines.Office.OfficeArt;

/// <summary>A picture taken out of a BLIP, with the file extension of its format.</summary>
internal readonly record struct OfficeArtPicture(byte[] Bytes, string Extension);

/// <summary>Reading FBSEs and BLIPs.</summary>
internal static class OfficeArtBlips
{
    /// <summary>
    /// The BLIP the FBSE <paramref name="fbse"/> of <paramref name="bytes"/> stands for: its own (in
    /// <paramref name="bytes"/>), or else the one at its foDelay in <paramref name="delayStream"/>
    /// (<paramref name="delayed"/> true). Null when <paramref name="fbse"/> isn't an FBSE or neither BLIP fits.
    /// </summary>
    public static OfficeArtRecord? FbseBlip(ReadOnlySpan<byte> bytes, OfficeArtRecord fbse, ReadOnlySpan<byte> delayStream, out bool delayed)
    {
        delayed = false;
        if (fbse.Type != OfficeArtRecords.Fbse || fbse.Length < 36)
            return null;
        var cbName = bytes[fbse.Start + 33];
        if (OfficeArtRecords.At(bytes, fbse.Start + 36 + cbName, fbse.End) is { } embedded)
            return embedded;
        var delay = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(fbse.Start + 28)..]);
        if (delay < delayStream.Length && OfficeArtRecords.At(delayStream, (int)delay, delayStream.Length) is { } stored)
        {
            delayed = true;
            return stored;
        }
        return null;
    }

    /// <summary>
    /// A BLIP record's picture: one UID (16 bytes) or two when the instance is odd, then the data. Null for PICT, for
    /// kinds it doesn't know and for damaged data. What metafiles inflate to goes to <paramref name="count"/> as it
    /// unpacks.
    /// </summary>
    public static OfficeArtPicture? Read(ReadOnlySpan<byte> bytes, OfficeArtRecord blip, DocumentLimits limits, Action<long> count)
    {
        var uids = (blip.Instance & 1) == 1 ? 32 : 16;
        switch (blip.Type)
        {
            case OfficeArtRecords.BlipEmf or OfficeArtRecords.BlipWmf:
                {
                    // Metafile header: uncompressed size, bounds, size in EMU, saved size, compression (0 deflate, 0xFE none).
                    var header = blip.Start + uids;
                    if (header + 34 > blip.End)
                        return null;
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[header..]);
                    var saved = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(header + 28)..]);
                    var compressed = bytes[header + 32] == 0;
                    var data = header + 34;
                    var length = (int)Math.Min(saved, (uint)(blip.End - data));
                    var metafile = compressed ? Inflate(bytes.Slice(data, length), size, limits, count) : bytes.Slice(data, length).ToArray();
                    if (metafile is null)
                        return null;
                    if (blip.Type == OfficeArtRecords.BlipEmf)
                        return new(metafile, "emf");
                    var bounds = bytes.Slice(header + 4, 16);
                    var emu = (BinaryPrimitives.ReadInt32LittleEndian(bytes[(header + 20)..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[(header + 24)..]));
                    return new(Placeable(metafile, bounds, emu), "wmf");
                }
            case OfficeArtRecords.BlipJpeg or OfficeArtRecords.BlipCmykJpeg or OfficeArtRecords.BlipPng or OfficeArtRecords.BlipDib or OfficeArtRecords.BlipTiff:
                {
                    var data = blip.Start + uids + 1; // the tag byte
                    if (data > blip.End)
                        return null;
                    var picture = bytes[data..blip.End].ToArray();
                    return blip.Type switch
                    {
                        OfficeArtRecords.BlipPng => new(picture, "png"),
                        OfficeArtRecords.BlipTiff => new(picture, "tif"),
                        OfficeArtRecords.BlipDib => Bitmap(picture) is { } bmp ? new(bmp, "bmp") : null,
                        _ => new(picture, "jpg"),
                    };
                }
            default:
                return null; // PICT and unknown kinds
        }
    }

    /// <summary>
    /// Inflates a zlib stream, at most to <paramref name="size"/> (the size the header gives) and the part limit. What
    /// it unpacks goes to <paramref name="count"/> chunk by chunk, and a picture that unpacks to far more than its packed
    /// size is refused. Null when the stream is damaged.
    /// </summary>
    public static byte[]? Inflate(ReadOnlySpan<byte> data, uint size, DocumentLimits limits, Action<long> count)
    {
        var limit = Math.Min(size, limits.MaxPartBytes);
        var packed = Math.Max(1, data.Length);
        try
        {
            using var input = new MemoryStream(data.ToArray(), writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while (output.Length < limit && (read = zlib.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - output.Length))) > 0)
            {
                output.Write(buffer, 0, read);
                count(read);
                if (output.Length > limits.InflateRatioFloor && output.Length / packed > limits.MaxInflateRatio)
                    throw DocumentLimits.Exceeded($"A picture of the document unpacks to more than {limits.MaxInflateRatio} times its packed size.");
            }
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>A WMF with the placeable header (bounds in metafile units, units per inch from the size in EMU) it lacks in a BLIP.</summary>
    public static byte[] Placeable(byte[] wmf, ReadOnlySpan<byte> bounds, (int Cx, int Cy) emu)
    {
        if (wmf.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(wmf) == 0x9AC6CDD7)
            return wmf;
        var left = BinaryPrimitives.ReadInt32LittleEndian(bounds);
        var top = BinaryPrimitives.ReadInt32LittleEndian(bounds[4..]);
        var right = BinaryPrimitives.ReadInt32LittleEndian(bounds[8..]);
        var bottom = BinaryPrimitives.ReadInt32LittleEndian(bounds[12..]);
        var width = (long)right - left;
        var inch = width > 0 && emu.Cx > 0 ? Math.Clamp(width * 914400 / emu.Cx, 1, 65535) : 1440;
        var header = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x9AC6CDD7);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(6), (short)Math.Clamp(left, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(8), (short)Math.Clamp(top, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(10), (short)Math.Clamp(right, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(12), (short)Math.Clamp(bottom, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)inch);
        ushort checksum = 0;
        for (var i = 0; i < 20; i += 2)
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(i));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), checksum);
        return [.. header, .. wmf];
    }

    /// <summary>A DIB with the BMP file header in front: where the pixels start after the header and palette.</summary>
    public static byte[]? Bitmap(byte[] dib)
    {
        if (dib.Length < 40)
            return null;
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (headerSize < 12 || headerSize > dib.Length)
            return null;
        var bits = headerSize >= 16 ? BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14)) : 0;
        var compression = headerSize >= 20 ? BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16)) : 0;
        var used = headerSize >= 36 ? BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(32)) : 0;
        var colors = used > 0 ? used : bits <= 8 ? 1 << bits : 0;
        var masks = compression == 3 && headerSize == 40 ? 12 : 0;
        var offset = 14L + headerSize + masks + Math.Min(colors, 256) * 4L;
        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), (int)Math.Min(offset, file.Length));
        dib.CopyTo(file, 14);
        return file;
    }
}
