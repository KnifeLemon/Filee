// EMF and WMF files written record by record in code for MetafileTests (no sample files in the repository): the
// header, the records the test adds, the end-of-file record. They run on every platform; MetafileGdiTests records
// metafiles with GDI and GDI+ on Windows.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Tests;

/// <summary>Builds an EMF for a 96 dpi reference device, <c>width</c> × <c>height</c> pixels.</summary>
internal sealed class EmfBuilder(int width, int height, int handles = 16)
{
    private readonly List<byte[]> _records = [];

    public const uint NullPen = 0x80000008, NullBrush = 0x80000005, BlackPen = 0x80000007, WhiteBrush = 0x80000000;

    /// <summary>A record of 32-bit fields.</summary>
    public EmfBuilder Add(int type, params int[] fields)
    {
        var payload = new byte[fields.Length * 4];
        for (var i = 0; i < fields.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(i * 4), fields[i]);
        return Raw(type, payload);
    }

    /// <summary>A record with the given bytes after its type and size, padded to 4 bytes.</summary>
    public EmfBuilder Raw(int type, byte[] payload, int? size = null)
    {
        var length = 8 + ((payload.Length + 3) & ~3);
        var record = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(record, type);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), size ?? length);
        payload.CopyTo(record, 8);
        _records.Add(record);
        return this;
    }

    public EmfBuilder Brush(int index, uint colorRef, int style = 0, int hatch = 0) => Add(39, index, style, (int)colorRef, hatch);

    public EmfBuilder Pen(int index, int style, int width, uint colorRef) => Add(38, index, style, width, 0, (int)colorRef);

    public EmfBuilder Select(uint index) => Add(37, (int)index);

    public EmfBuilder Rectangle(int left, int top, int right, int bottom) => Add(43, left, top, right, bottom);

    public EmfBuilder Ellipse(int left, int top, int right, int bottom) => Add(42, left, top, right, bottom);

    /// <summary>EMR_POLYGON with 32-bit points.</summary>
    public EmfBuilder Polygon(params (int X, int Y)[] points)
    {
        var fields = new List<int> { 0, 0, width, height, points.Length };
        foreach (var (x, y) in points)
        {
            fields.Add(x);
            fields.Add(y);
        }
        return Add(3, [.. fields]);
    }

    /// <summary>EMR_EXTCREATEFONTINDIRECTW with an em height (a negative LOGFONT height) and a face name.</summary>
    public EmfBuilder Font(int index, int emHeight, string face, int weight = 400, byte charSet = 1)
    {
        var payload = new byte[4 + 92];
        BinaryPrimitives.WriteInt32LittleEndian(payload, index);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), -emHeight);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(20), weight);
        payload[27] = charSet;
        Encoding.Unicode.GetBytes(face).CopyTo(payload, 32);
        return Raw(82, payload);
    }

    /// <summary>EMR_EXTTEXTOUTW at (x, y) with optional advances per character.</summary>
    public EmfBuilder Text(int x, int y, string text, int[]? dx = null, int options = 0)
    {
        var chars = Encoding.Unicode.GetBytes(text);
        var stringOffset = 76;
        var dxOffset = dx is null ? 0 : stringOffset + ((chars.Length + 3) & ~3);
        var size = (dx is null ? stringOffset + chars.Length : dxOffset + dx.Length * 4) - 8;
        var payload = new byte[size];
        void Int(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(at - 8), value);
        Int(24, 1); // GM_COMPATIBLE
        Int(28, BitConverter.SingleToInt32Bits(1));
        Int(32, BitConverter.SingleToInt32Bits(1));
        Int(36, x);
        Int(40, y);
        Int(44, text.Length);
        Int(48, stringOffset);
        Int(52, options);
        Int(72, dxOffset);
        chars.CopyTo(payload, stringOffset - 8);
        for (var i = 0; dx is not null && i < dx.Length; i++)
            Int(dxOffset + i * 4, dx[i]);
        return Raw(84, payload);
    }

    /// <summary>EMR_STRETCHDIBITS of a 24-bit bottom-up DIB of <paramref name="pixels"/> (rows top-down, 0xRRGGBB).</summary>
    public EmfBuilder StretchDib(int x, int y, int destWidth, int destHeight, uint[,] pixels)
    {
        int rows = pixels.GetLength(0), columns = pixels.GetLength(1);
        var stride = (columns * 3 + 3) & ~3;
        var bits = new byte[stride * rows];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var at = (rows - 1 - r) * stride + c * 3; // bottom-up
                bits[at] = (byte)pixels[r, c];
                bits[at + 1] = (byte)(pixels[r, c] >> 8);
                bits[at + 2] = (byte)(pixels[r, c] >> 16);
            }
        }
        var info = Dib(columns, rows, 24);
        const int fixedSize = 80;
        var payload = new byte[fixedSize - 8 + info.Length + bits.Length];
        void Int(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(at - 8), value);
        Int(24, x);
        Int(28, y);
        Int(40, columns);
        Int(44, rows);
        Int(48, fixedSize);
        Int(52, info.Length);
        Int(56, fixedSize + info.Length);
        Int(60, bits.Length);
        Int(68, 0x00CC0020); // SRCCOPY
        Int(72, destWidth);
        Int(76, destHeight);
        info.CopyTo(payload, fixedSize - 8);
        bits.CopyTo(payload, fixedSize - 8 + info.Length);
        return Raw(81, payload);
    }

    /// <summary>A BITMAPINFOHEADER.</summary>
    public static byte[] Dib(int width, int height, int bitCount)
    {
        var header = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(header, 40);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), height);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(14), (short)bitCount);
        return header;
    }

    /// <summary>An EMR_COMMENT holding an EMF+ header: dual (EMF records follow) or EMF+ only.</summary>
    public EmfBuilder EmfPlusHeader(bool dual)
    {
        var payload = new byte[4 + 4 + 28];
        BinaryPrimitives.WriteInt32LittleEndian(payload, 32); // DataSize
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0x2B464D45); // "EMF+"
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), 0x4001);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10), (ushort)(dual ? 1 : 0));
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(12), 28);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16), 16);
        return Raw(70, payload);
    }

    public byte[] ToArray()
    {
        const int headerSize = 108;
        var eof = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(eof, 14);
        BinaryPrimitives.WriteInt32LittleEndian(eof.AsSpan(4), 20);
        BinaryPrimitives.WriteInt32LittleEndian(eof.AsSpan(12), 16);
        BinaryPrimitives.WriteInt32LittleEndian(eof.AsSpan(16), 20);
        var total = headerSize + _records.Sum(r => r.Length) + eof.Length;

        var header = new byte[headerSize];
        void Int(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(at), value);
        Int(0, 1);
        Int(4, headerSize);
        Int(16, width - 1);
        Int(20, height - 1);
        // The frame in 0.01 mm at 96 dpi: 1 px = 2540 / 96 hundredths of a millimetre.
        Int(32, (int)Math.Round(width * 2540 / 96.0));
        Int(36, (int)Math.Round(height * 2540 / 96.0));
        Int(40, 0x464D4520);
        Int(44, 0x10000);
        Int(48, total);
        Int(52, _records.Count + 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(56), (ushort)handles);
        Int(72, 960); // a 960 × 960 px device of 254 × 254 mm: 96 dpi
        Int(76, 960);
        Int(80, 254);
        Int(84, 254);
        Int(100, 254_000);
        Int(104, 254_000);
        return [.. header, .. _records.SelectMany(r => r), .. eof];
    }
}

/// <summary>Builds a placeable WMF whose box is <c>width</c> × <c>height</c> units at <c>unitsPerInch</c>.</summary>
internal sealed class WmfBuilder(int width, int height, int unitsPerInch = 96, int objects = 8)
{
    private readonly List<byte[]> _records = [];

    /// <summary>A record of 16-bit parameters, in the order they are stored (often reversed: y before x).</summary>
    public WmfBuilder Add(int function, params int[] parameters)
    {
        var payload = new byte[parameters.Length * 2];
        for (var i = 0; i < parameters.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(i * 2), unchecked((short)parameters[i]));
        return Raw(function, payload);
    }

    public WmfBuilder Raw(int function, byte[] payload, uint? words = null)
    {
        var length = 6 + ((payload.Length + 1) & ~1);
        var record = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, words ?? (uint)(length / 2));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), (ushort)function);
        payload.CopyTo(record, 6);
        _records.Add(record);
        return this;
    }

    /// <summary>META_CREATEBRUSHINDIRECT, solid.</summary>
    public WmfBuilder Brush(uint colorRef) => Add(0x02FC, 0, (int)(colorRef & 0xFFFF), (int)(colorRef >> 16), 0);

    /// <summary>META_CREATEPENINDIRECT.</summary>
    public WmfBuilder Pen(int style, int width, uint colorRef) => Add(0x02FA, style, width, 0, (int)(colorRef & 0xFFFF), (int)(colorRef >> 16));

    public WmfBuilder Select(int index) => Add(0x012D, index);

    /// <summary>META_RECTANGLE (stored bottom, right, top, left).</summary>
    public WmfBuilder Rectangle(int left, int top, int right, int bottom) => Add(0x041B, bottom, right, top, left);

    /// <summary>META_POLYGON.</summary>
    public WmfBuilder Polygon(params (int X, int Y)[] points) =>
        Add(0x0324, [points.Length, .. points.SelectMany(p => new[] { p.X, p.Y })]);

    /// <param name="placeable">With the placeable (Aldus) header; without it the records' window gives the size.</param>
    public byte[] ToArray(bool placeable = true)
    {
        var eof = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(eof, 3);
        var records = _records.SelectMany(r => r).Concat(eof).ToArray();

        var box = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(box, 0x9AC6CDD7);
        BinaryPrimitives.WriteInt16LittleEndian(box.AsSpan(10), (short)width);
        BinaryPrimitives.WriteInt16LittleEndian(box.AsSpan(12), (short)height);
        BinaryPrimitives.WriteUInt16LittleEndian(box.AsSpan(14), (ushort)unitsPerInch);
        ushort checksum = 0;
        for (var i = 0; i < 20; i += 2)
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(box.AsSpan(i));
        BinaryPrimitives.WriteUInt16LittleEndian(box.AsSpan(20), checksum);

        var header = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(header, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 0x0300);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(6), (uint)((header.Length + records.Length) / 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)objects);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)(_records.Count == 0 ? 3 : _records.Max(r => r.Length) / 2));
        return placeable ? [.. box, .. header, .. records] : [.. header, .. records];
    }
}
