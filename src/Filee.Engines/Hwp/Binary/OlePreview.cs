// The picture an embedded OLE object (한글 OLE 개체: a Paint bitmap, an Excel sheet, a chart, ...) shows on the page.
// The object is a compound file (in 한글's BinData, after a 4-byte length), and the program that made it left a
// picture of it there for programs that can't run it (MS-OLEDS 2.3.4, OLEPresentationStream "\x02OlePres000"): a
// Windows metafile (CF_METAFILEPICT) or a device-independent bitmap (CF_DIB). An object without one may still be a
// Paint bitmap whose native data ("\x01Ole10Native") is the BMP file itself. Nothing else of the object is read.

using System.Buffers.Binary;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Binary;

internal static class OlePreview
{
    private const uint MetafilePicture = 3; // CF_METAFILEPICT
    private const uint DeviceIndependentBitmap = 8; // CF_DIB

    /// <summary>
    /// Writes the picture of the OLE object in <paramref name="ole"/> next to <paramref name="target"/> (its extension
    /// replaced by .wmf or .bmp) and returns that file; null when the object holds no picture Filee can draw.
    /// </summary>
    public static string? Extract(string ole, string target, DocumentLimits? limits = null)
    {
        try
        {
            var bytes = File.ReadAllBytes(ole);
            // 한글 keeps the object's length before the compound file.
            var start = HasSignature(bytes, 0) ? 0 : HasSignature(bytes, 4) ? 4 : -1;
            if (start < 0)
                return null;
            using var stream = new MemoryStream(bytes, start, bytes.Length - start, writable: false);
            using var file = CompoundFile.Open(stream, limits);
            if (Presentation(file) is var (data, wmf))
                return Write(target, wmf ? ".wmf" : ".bmp", data);
            if (file.Read("\u0001Ole10Native") is { Length: > 6 } native && native.AsSpan(4, 2).SequenceEqual("BM"u8))
                return Write(target, ".bmp", native.AsSpan(4).ToArray());
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or Core.Conversion.ConversionRefusedException)
        {
            return null;
        }
    }

    private static bool HasSignature(byte[] bytes, int at) =>
        bytes.Length >= at + 8 && bytes.AsSpan(at, 8).SequenceEqual((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);

    /// <summary>The first presentation stream's picture: a metafile (WMF records) or a BMP file made of its DIB.</summary>
    private static (byte[] Data, bool Wmf)? Presentation(CompoundFile file)
    {
        foreach (var name in file.Streams.Where(s => s.StartsWith("\u0002OlePres", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var data = file.Read(name);
            if (data is null || data.Length < 8)
                continue;
            var span = data.AsSpan();
            // AnsiClipboardFormat: 0xFFFFFFFF and a standard format; a format by name (a length) isn't drawn.
            if (BinaryPrimitives.ReadUInt32LittleEndian(span) != 0xFFFFFFFF)
                continue;
            var format = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
            var at = 8;
            // TargetDeviceSize counts itself; a target device follows when it is more than 4.
            if (span.Length < at + 4)
                continue;
            var device = BinaryPrimitives.ReadUInt32LittleEndian(span[at..]);
            if (device < 4 || device > span.Length - at)
                continue;
            at += (int)device;
            // Aspect, Lindex, Advf, Reserved1, Width, Height, Size.
            if (span.Length < at + 28)
                continue;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 24)..]);
            at += 28;
            if (size == 0 || size > span.Length - at)
                continue;
            var picture = span.Slice(at, (int)size);
            if (format == MetafilePicture && Metafiles.WmfHeader.IsMetaHeader(picture, 0))
                return (picture.ToArray(), true);
            if (format == DeviceIndependentBitmap && Bitmap(picture) is { } bmp)
                return (bmp, false);
        }
        return null;
    }

    /// <summary>A BMP file of a packed DIB: the 14-byte file header, then the DIB with its bits after its colour table.</summary>
    private static byte[]? Bitmap(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40)
            return null;
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        if (headerSize < 12 || headerSize > dib.Length)
            return null;
        var bitCount = headerSize == 12 ? BinaryPrimitives.ReadUInt16LittleEndian(dib[10..]) : BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = headerSize >= 40 ? BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]) : 0;
        var used = headerSize >= 40 ? BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]) : 0;
        long colors = bitCount <= 8 ? (used > 0 ? used : 1u << bitCount) : used;
        long entry = headerSize == 12 ? 3 : 4;
        long masks = headerSize == 40 && compression == 3 ? 12 : 0; // BI_BITFIELDS after a BITMAPINFOHEADER
        var offset = 14 + headerSize + masks + colors * entry;
        if (offset > 14 + dib.Length)
            return null;
        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), (uint)offset);
        dib.CopyTo(bmp.AsSpan(14));
        return bmp;
    }

    private static string Write(string target, string extension, byte[] data)
    {
        var path = Path.ChangeExtension(target, extension);
        File.WriteAllBytes(path, data);
        return path;
    }
}
