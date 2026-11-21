// Builds small .xls (Excel 97–2003, BIFF8 inside a compound file), .xlsb (Excel binary workbook, BIFF12 inside a ZIP)
// and .ods files for tests, so the built-in XLS, XLSB and ODS readers can be tested without sample documents or
// LibreOffice (see OfficeBuilders.cs for .xlsx).

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Filee.Engines.Tests;

/// <summary>
/// An Excel 97–2003 workbook: shared strings, a fixed set of cell formats and any number of sheets. Records follow
/// [MS-XLS]; only what readers need is written (no INDEX / DBCELL records, no window settings).
/// </summary>
internal sealed class XlsBuilder
{
    // Cell formats (XF indexes):
    public const int Plain = 0;
    public const int Centered = 1;  // centred horizontally
    public const int Won = 2;       // #,##0"원" (custom format 164)
    public const int Date = 3;      // built-in 14
    public const int Percent = 4;   // 0.0% (custom format 165)
    public const int Time = 5;      // built-in 20 (h:mm)
    public const int RightTop = 6;  // right aligned at the top

    /// <summary>(format id, horizontal alignment, vertical alignment) of each XF.</summary>
    private static readonly (int Format, int Horizontal, int Vertical)[] Xfs =
        [(0, 0, 2), (0, 2, 2), (164, 0, 2), (14, 0, 2), (165, 0, 2), (20, 0, 2), (0, 3, 0)];

    private static readonly (int Id, string Code)[] Formats = [(164, "#,##0\"원\""), (165, "0.0%")];

    private readonly List<string> _strings = [];
    private readonly List<XlsSheet> _sheets = [];

    /// <summary>Adds a sheet; fill it through the returned builder.</summary>
    public XlsSheet Sheet(string name, bool hidden = false)
    {
        var sheet = new XlsSheet(this, name, hidden);
        _sheets.Add(sheet);
        return sheet;
    }

    internal int StringIndex(string text)
    {
        var index = _strings.IndexOf(text);
        if (index >= 0)
            return index;
        _strings.Add(text);
        return _strings.Count - 1;
    }

    /// <param name="encrypted">Adds XOR obfuscation with a password nobody knows, as a protected file has.</param>
    public string Save(string path, bool encrypted = false)
    {
        var stream = new MemoryStream();
        Record(stream, 0x0809, Bof(0x0005));
        if (encrypted)
            Record(stream, 0x002F, [0x00, 0x00, 0x34, 0x12, 0x01, 0x00]); // FILEPASS: XOR, key 0x1234, verifier 0x0001
        Record(stream, 0x0042, U16(1200)); // CODEPAGE: UTF-16
        for (var i = 0; i < 5; i++)
            Record(stream, 0x0031, Font(bold: i == 1));
        foreach (var (id, code) in Formats)
            Record(stream, 0x041E, [.. U16(id), .. UnicodeString(code, byteLength: false)]);
        foreach (var (format, horizontal, vertical) in Xfs)
            Record(stream, 0x00E0, Xf(format, horizontal, vertical));

        var sheetOffsets = new List<long>();
        foreach (var sheet in _sheets)
        {
            sheetOffsets.Add(stream.Position + 4);
            Record(stream, 0x0085, [0, 0, 0, 0, (byte)(sheet.Hidden ? 1 : 0), 0, .. UnicodeString(sheet.Name, byteLength: true)]);
        }
        var sst = new List<byte>();
        sst.AddRange(U32(_strings.Count));
        sst.AddRange(U32(_strings.Count));
        foreach (var text in _strings)
            sst.AddRange(UnicodeString(text, byteLength: false));
        Record(stream, 0x00FC, [.. sst]);
        Record(stream, 0x000A, []);

        for (var i = 0; i < _sheets.Count; i++)
        {
            var offset = stream.Position;
            var position = stream.Position;
            stream.Position = sheetOffsets[i];
            stream.Write(U32((int)offset));
            stream.Position = position;
            _sheets[i].Write(stream);
        }

        CompoundFile.Write(path, "Workbook", stream.ToArray());
        return path;
    }

    internal static byte[] Bof(int type) => [.. U16(0x0600), .. U16(type), .. U16(0x0DBB), .. U16(0x07CC), 0, 0, 0, 0, 6, 0, 0, 0];

    private static byte[] Font(bool bold) =>
        [.. U16(220), .. U16(0), .. U16(0x7FFF), .. U16(bold ? 700 : 400), .. U16(0), 0, 0, 0, 0, .. UnicodeString("Arial", byteLength: true)];

    private static byte[] Xf(int format, int horizontal, int vertical)
    {
        var xf = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(xf, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(xf.AsSpan(2), (ushort)format);
        BinaryPrimitives.WriteUInt16LittleEndian(xf.AsSpan(4), 0x0001); // locked cell XF, parent style 0
        xf[6] = (byte)(horizontal | vertical << 4);
        xf[9] = 0xFC; // all attribute groups set by this XF
        return xf;
    }

    /// <summary>A BIFF8 string with a 1-byte (ShortXLUnicodeString) or 2-byte length, stored as UTF-16.</summary>
    internal static byte[] UnicodeString(string text, bool byteLength) =>
        [.. byteLength ? [(byte)text.Length] : U16(text.Length), 0x01, .. Encoding.Unicode.GetBytes(text)];

    internal static void Record(Stream stream, int id, byte[] data)
    {
        stream.Write(U16(id));
        stream.Write(U16(data.Length));
        stream.Write(data);
    }

    internal static byte[] U16(int value) => BitConverter.GetBytes((ushort)value);
    internal static byte[] U32(int value) => BitConverter.GetBytes(value);
}

/// <summary>One sheet of an <see cref="XlsBuilder"/> workbook; rows and columns are 0-based.</summary>
internal sealed class XlsSheet(XlsBuilder book, string name, bool hidden)
{
    private readonly SortedDictionary<(int Row, int Column), byte[]> _cells = [];
    private readonly List<(int Top, int Left, int Bottom, int Right)> _merges = [];
    private readonly List<(int Column, double Width, bool Hidden)> _columns = [];
    private readonly SortedDictionary<int, (double Points, bool Hidden)> _rows = [];

    public string Name => name;
    public bool Hidden => hidden;

    public XlsSheet Text(int row, int column, string text, int xf = XlsBuilder.Plain) =>
        Cell(0x00FD, row, column, xf, XlsBuilder.U32(book.StringIndex(text)));

    public XlsSheet Number(int row, int column, double value, int xf = XlsBuilder.Plain) =>
        Cell(0x0203, row, column, xf, BitConverter.GetBytes(value));

    public XlsSheet Bool(int row, int column, bool value) => Cell(0x0205, row, column, XlsBuilder.Plain, [(byte)(value ? 1 : 0), 0]);

    /// <summary>An error cell: 0x07 = #DIV/0!, 0x2A = #N/A.</summary>
    public XlsSheet Error(int row, int column, byte code) => Cell(0x0205, row, column, XlsBuilder.Plain, [code, 1]);

    public XlsSheet Merge(int top, int left, int bottom, int right)
    {
        _merges.Add((top, left, bottom, right));
        return this;
    }

    /// <summary>A column width in characters (as Excel shows it), or a hidden column.</summary>
    public XlsSheet Width(int column, double characters, bool hide = false)
    {
        _columns.Add((column, characters, hide));
        return this;
    }

    public XlsSheet Height(int row, double points, bool hide = false)
    {
        _rows[row] = (points, hide);
        return this;
    }

    private XlsSheet Cell(int id, int row, int column, int xf, byte[] value)
    {
        _cells[(row, column)] = [.. XlsBuilder.U16(id), .. XlsBuilder.U16(row), .. XlsBuilder.U16(column), .. XlsBuilder.U16(xf), .. value];
        return this;
    }

    internal void Write(Stream stream)
    {
        XlsBuilder.Record(stream, 0x0809, XlsBuilder.Bof(0x0010));
        XlsBuilder.Record(stream, 0x0225, [.. XlsBuilder.U16(0), .. XlsBuilder.U16(300)]); // DEFAULTROWHEIGHT: 15 pt
        foreach (var (column, width, hide) in _columns)
        {
            // COLINFO: width in 1/256 characters; fUserSet so readers take the width.
            XlsBuilder.Record(stream, 0x007D,
                [.. XlsBuilder.U16(column), .. XlsBuilder.U16(column), .. XlsBuilder.U16((int)(width * 256)), .. XlsBuilder.U16(15), .. XlsBuilder.U16(hide ? 0x0003 : 0x0002), .. XlsBuilder.U16(0)]);
        }
        var lastRow = _cells.Keys.Select(k => k.Row).Concat(_rows.Keys).DefaultIfEmpty(-1).Max();
        var lastColumn = _cells.Keys.Select(k => k.Column).DefaultIfEmpty(-1).Max();
        XlsBuilder.Record(stream, 0x0200, [.. XlsBuilder.U32(0), .. XlsBuilder.U32(lastRow + 1), .. XlsBuilder.U16(0), .. XlsBuilder.U16(lastColumn + 1), .. XlsBuilder.U16(0)]);

        foreach (var (row, (points, hide)) in _rows)
        {
            // ROW: fDyZero (0x20) hides the row, fUnsynced (0x40) marks a custom height; the high byte must be 1.
            var flags = 0x0100 | 0x0040 | (hide ? 0x0020 : 0);
            XlsBuilder.Record(stream, 0x0208,
                [.. XlsBuilder.U16(row), .. XlsBuilder.U16(0), .. XlsBuilder.U16(lastColumn + 1), .. XlsBuilder.U16((int)(points * 20)), .. XlsBuilder.U16(0), .. XlsBuilder.U16(0), .. XlsBuilder.U16(flags), .. XlsBuilder.U16(15)]);
        }
        foreach (var cell in _cells.Values)
            XlsBuilder.Record(stream, BitConverter.ToUInt16(cell, 0), cell[2..]);
        if (_merges.Count > 0)
        {
            var data = new List<byte>(XlsBuilder.U16(_merges.Count));
            foreach (var (top, left, bottom, right) in _merges)
                data.AddRange([.. XlsBuilder.U16(top), .. XlsBuilder.U16(bottom), .. XlsBuilder.U16(left), .. XlsBuilder.U16(right)]);
            XlsBuilder.Record(stream, 0x00E5, [.. data]);
        }
        XlsBuilder.Record(stream, 0x000A, []);
    }
}

/// <summary>
/// A minimal Excel binary workbook (.xlsb, BIFF12 inside an OPC ZIP package): shared strings, inline strings, a fixed
/// set of cell formats and any number of sheets. Records follow [MS-XLSB].
/// </summary>
internal sealed class XlsbBuilder
{
    // Cell formats (XF indexes, same numbering as XlsBuilder):
    public const int Plain = 0;
    public const int Centered = 1;  // centred horizontally
    public const int Won = 2;       // #,##0"원" (custom format 164)
    public const int Date = 3;      // built-in 14 (yyyy-mm-dd)
    public const int Percent = 4;   // 0.0% (custom format 165)
    public const int Time = 5;      // built-in 20 (h:mm)
    public const int RightTop = 6;  // right aligned at the top

    private static readonly (int Format, int Horizontal, int Vertical)[] Xfs =
        [(0, 0, 2), (0, 2, 2), (164, 0, 2), (14, 0, 2), (165, 0, 2), (20, 0, 2), (0, 3, 0)];

    private static readonly (int Id, string Code)[] Formats = [(164, "#,##0\"원\""), (165, "0.0%")];

    private readonly List<string> _strings = [];
    private readonly List<XlsbSheet> _sheets = [];

    /// <summary>Adds a sheet; fill it through the returned builder.</summary>
    public XlsbSheet Sheet(string name, bool hidden = false)
    {
        var sheet = new XlsbSheet(this, name, hidden);
        _sheets.Add(sheet);
        return sheet;
    }

    internal int StringIndex(string text)
    {
        var index = _strings.IndexOf(text);
        if (index >= 0)
            return index;
        _strings.Add(text);
        return _strings.Count - 1;
    }

    public string Save(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteText(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"bin\" ContentType=\"application/vnd.ms-excel.sheet.binary.macroEnabled.main\"/>" +
            "<Override PartName=\"/xl/workbook.bin\" ContentType=\"application/vnd.ms-excel.sheet.binary.macroEnabled.main\"/>" +
            "<Override PartName=\"/xl/styles.bin\" ContentType=\"application/vnd.ms-excel.styles\"/>" +
            "<Override PartName=\"/xl/sharedStrings.bin\" ContentType=\"application/vnd.ms-excel.sharedStrings\"/>" +
            string.Concat(_sheets.Select((_, i) => $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.bin\" ContentType=\"application/vnd.ms-excel.worksheet\"/>")) +
            "</Types>");
        WriteText(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.bin\"/>" +
            "</Relationships>");
        var rels = string.Concat(_sheets.Select((_, i) =>
            $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.bin\"/>")) +
            $"<Relationship Id=\"rId{_sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.bin\"/>" +
            $"<Relationship Id=\"rId{_sheets.Count + 2}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.bin\"/>";
        WriteText(zip, "xl/_rels/workbook.bin.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{rels}</Relationships>");

        WriteBinary(zip, "xl/workbook.bin", WriteWorkbook);
        WriteBinary(zip, "xl/styles.bin", WriteStyles);
        WriteBinary(zip, "xl/sharedStrings.bin", WriteSharedStrings);
        for (var i = 0; i < _sheets.Count; i++)
        {
            var sheet = _sheets[i];
            WriteBinary(zip, $"xl/worksheets/sheet{i + 1}.bin", sheet.Write);
        }
        return path;
    }

    private void WriteWorkbook(Stream stream)
    {
        Record(stream, 0x0083, []); // BrtBeginBook
        Record(stream, 0x0099, [.. U32(0), .. U32(0), .. WideString("")]); // BrtWbProp (date1904 = 0)
        Record(stream, 0x008F, []); // BrtBeginBundleShs
        for (var i = 0; i < _sheets.Count; i++)
        {
            var sheet = _sheets[i];
            Record(stream, 0x009C, [.. U32(sheet.Hidden ? 1 : 0), .. U32(i + 1), .. WideString($"rId{i + 1}"), .. WideString(sheet.Name)]); // BrtBundleSh
        }
        Record(stream, 0x0090, []); // BrtEndBundleShs
        Record(stream, 0x0084, []); // BrtEndBook
    }

    private static void WriteStyles(Stream stream)
    {
        Record(stream, 0x0116, []); // BrtBeginStyleSheet
        Record(stream, 0x0267, U32(Formats.Length)); // BrtBeginFmts
        foreach (var (id, code) in Formats)
            Record(stream, 0x002C, [.. U16(id), .. WideString(code)]); // BrtFmt
        Record(stream, 0x0268, []); // BrtEndFmts
        Record(stream, 0x0269, U32(Xfs.Length)); // BrtBeginCellXFs
        foreach (var (format, horizontal, vertical) in Xfs)
        {
            var xf = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(xf.AsSpan(0), 0xFFFF);
            BinaryPrimitives.WriteUInt16LittleEndian(xf.AsSpan(2), (ushort)format);
            xf[12] = (byte)((horizontal & 0x07) | ((vertical & 0x07) << 3));
            xf[14] = 0x3F;
            Record(stream, 0x002F, xf); // BrtXF
        }
        Record(stream, 0x026A, []); // BrtEndCellXFs
        Record(stream, 0x0117, []); // BrtEndStyleSheet
    }

    private void WriteSharedStrings(Stream stream)
    {
        Record(stream, 0x009F, [.. U32(_strings.Count), .. U32(_strings.Count)]); // BrtBeginSst
        foreach (var text in _strings)
            Record(stream, 0x0013, [0x00, .. WideString(text)]); // BrtSSTItem (plain string, no rich runs)
        Record(stream, 0x00A0, []); // BrtEndSst
    }

    internal static void Record(Stream stream, int id, byte[] data)
    {
        WriteVarInt(stream, id);
        WriteVarInt(stream, data.Length);
        stream.Write(data);
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        do
        {
            var b = value & 0x7F;
            value >>= 7;
            if (value != 0)
                b |= 0x80;
            stream.WriteByte((byte)b);
        }
        while (value != 0);
    }

    internal static byte[] WideString(string text) => [.. U32(text.Length), .. Encoding.Unicode.GetBytes(text)];
    internal static byte[] U16(int value) => BitConverter.GetBytes((ushort)value);
    internal static byte[] U32(int value) => BitConverter.GetBytes(value);

    private static void WriteText(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WriteBinary(ZipArchive zip, string name, Action<Stream> write)
    {
        using var stream = zip.CreateEntry(name).Open();
        write(stream);
    }
}

/// <summary>One sheet of an <see cref="XlsbBuilder"/> workbook; rows and columns are 0-based.</summary>
internal sealed class XlsbSheet(XlsbBuilder book, string name, bool hidden)
{
    private readonly SortedDictionary<int, SortedDictionary<int, (int Id, byte[] Payload)>> _rows = [];
    private readonly List<(int Top, int Left, int Bottom, int Right)> _merges = [];

    public string Name => name;
    public bool Hidden => hidden;

    /// <summary>A cell storing a shared string (BrtCellIsst).</summary>
    public XlsbSheet Text(int row, int column, string text, int xf = XlsbBuilder.Plain) =>
        Cell(row, column, 0x0007, xf, XlsbBuilder.U32(book.StringIndex(text)));

    /// <summary>A cell storing an inline string (BrtCellSt).</summary>
    public XlsbSheet InlineText(int row, int column, string text, int xf = XlsbBuilder.Plain) =>
        Cell(row, column, 0x0006, xf, XlsbBuilder.WideString(text));

    /// <summary>A cell storing a floating-point number (BrtCellReal).</summary>
    public XlsbSheet Number(int row, int column, double value, int xf = XlsbBuilder.Plain) =>
        Cell(row, column, 0x0005, xf, BitConverter.GetBytes(value));

    /// <summary>A cell storing a boolean value (BrtCellBool).</summary>
    public XlsbSheet Bool(int row, int column, bool value) =>
        Cell(row, column, 0x0004, XlsbBuilder.Plain, [(byte)(value ? 1 : 0)]);

    /// <summary>A cell storing an error code (BrtCellError): 0x07 = #DIV/0!, 0x2A = #N/A.</summary>
    public XlsbSheet Error(int row, int column, byte code) =>
        Cell(row, column, 0x0003, XlsbBuilder.Plain, [code]);

    /// <summary>A blank cell (BrtCellBlank).</summary>
    public XlsbSheet Blank(int row, int column, int xf = XlsbBuilder.Plain) =>
        Cell(row, column, 0x0001, xf, []);

    public XlsbSheet Merge(int top, int left, int bottom, int right)
    {
        _merges.Add((top, left, bottom, right));
        return this;
    }

    private XlsbSheet Cell(int row, int column, int id, int xf, byte[] value)
    {
        if (!_rows.TryGetValue(row, out var cells))
            _rows[row] = cells = [];
        cells[column] = (id, [.. XlsbBuilder.U32(column), .. XlsbBuilder.U32(xf & 0x00FFFFFF), .. value]);
        return this;
    }

    internal void Write(Stream stream)
    {
        XlsbBuilder.Record(stream, 0x0081, []); // BrtBeginSheet
        if (_rows.Count > 0)
        {
            var firstRow = _rows.Keys.First();
            var lastRow = _rows.Keys.Last();
            var firstCol = _rows.Values.SelectMany(c => c.Keys).Min();
            var lastCol = _rows.Values.SelectMany(c => c.Keys).Max();
            XlsbBuilder.Record(stream, 0x0094, [.. XlsbBuilder.U32(firstRow), .. XlsbBuilder.U32(lastRow), .. XlsbBuilder.U32(firstCol), .. XlsbBuilder.U32(lastCol)]); // BrtWsDim
        }
        XlsbBuilder.Record(stream, 0x0091, []); // BrtBeginSheetData
        foreach (var (row, cells) in _rows)
        {
            var header = new byte[17];
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0), row);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 300); // 15 pt default row height
            XlsbBuilder.Record(stream, 0x0000, header); // BrtRowHdr
            foreach (var (id, payload) in cells.Values)
                XlsbBuilder.Record(stream, id, payload);
        }
        XlsbBuilder.Record(stream, 0x0092, []); // BrtEndSheetData
        if (_merges.Count > 0)
        {
            XlsbBuilder.Record(stream, 0x00B1, XlsbBuilder.U32(_merges.Count)); // BrtBeginMergeCells
            foreach (var (top, left, bottom, right) in _merges)
                XlsbBuilder.Record(stream, 0x00B0, [.. XlsbBuilder.U32(top), .. XlsbBuilder.U32(bottom), .. XlsbBuilder.U32(left), .. XlsbBuilder.U32(right)]); // BrtMergeCell
            XlsbBuilder.Record(stream, 0x00B2, []); // BrtEndMergeCells
        }
        XlsbBuilder.Record(stream, 0x0082, []); // BrtEndSheet
    }
}

/// <summary>
/// Writes a compound file (the OLE2 container of .xls, .doc and .ppt) holding one stream. Like Office, streams under
/// 4096 bytes go into the mini stream (64-byte sectors kept in the root entry).
/// </summary>
internal static class CompoundFile
{
    private const int SectorSize = 512;
    private const int MiniSectorSize = 64;
    private const int MiniStreamCutoff = 4096;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;
    private const uint FatSectorMark = 0xFFFFFFFD;
    private const uint NoStream = 0xFFFFFFFF;

    public static void Write(string path, string streamName, byte[] data)
    {
        var mini = data.Length < MiniStreamCutoff;
        var miniSectors = (data.Length + MiniSectorSize - 1) / MiniSectorSize;
        // Mini streams: sectors 0.. hold the mini stream, then one sector of mini FAT.
        var content = mini ? [.. data, .. new byte[miniSectors * MiniSectorSize - data.Length]] : data;
        var dataSectors = (content.Length + SectorSize - 1) / SectorSize;
        var miniFatSector = mini ? dataSectors : -1;
        var directorySector = dataSectors + (mini ? 1 : 0);
        var fatSectors = 1;
        while (fatSectors * (SectorSize / 4) < directorySector + 1 + fatSectors)
            fatSectors++;

        var fat = Enumerable.Repeat(FreeSector, fatSectors * (SectorSize / 4)).ToArray();
        for (var i = 0; i < dataSectors; i++)
            fat[i] = i + 1 < dataSectors ? (uint)(i + 1) : EndOfChain;
        if (mini)
            fat[miniFatSector] = EndOfChain;
        fat[directorySector] = EndOfChain;
        for (var i = 0; i < fatSectors; i++)
            fat[directorySector + 1 + i] = FatSectorMark;

        using var file = File.Create(path);
        var header = new byte[SectorSize];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(24), 0x003E); // minor version
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), 0x0003); // major version 3: 512-byte sectors
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), 0xFFFE); // little endian
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30), 9);      // sector size 2^9
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 6);      // mini sector size 2^6
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), (uint)fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48), (uint)directorySector);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56), MiniStreamCutoff);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(60), mini ? (uint)miniFatSector : EndOfChain);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(64), mini ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68), EndOfChain);
        for (var i = 0; i < 109; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76 + i * 4), i < fatSectors ? (uint)(directorySector + 1 + i) : FreeSector);
        file.Write(header);

        file.Write(content);
        file.Write(new byte[dataSectors * SectorSize - content.Length]);
        if (mini)
        {
            var miniFat = Enumerable.Repeat(FreeSector, SectorSize / 4).ToArray();
            for (var i = 0; i < miniSectors; i++)
                miniFat[i] = i + 1 < miniSectors ? (uint)(i + 1) : EndOfChain;
            foreach (var entry in miniFat)
                file.Write(BitConverter.GetBytes(entry));
        }

        var directory = new byte[SectorSize];
        Entry(directory.AsSpan(0, 128), "Root Entry", type: 5, child: 1, start: mini ? 0 : EndOfChain, size: mini ? content.Length : 0);
        Entry(directory.AsSpan(128, 128), streamName, type: 2, child: NoStream, start: 0, size: data.Length);
        Entry(directory.AsSpan(256, 128), "", type: 0, child: NoStream, start: 0, size: 0);
        Entry(directory.AsSpan(384, 128), "", type: 0, child: NoStream, start: 0, size: 0);
        file.Write(directory);

        foreach (var entry in fat)
            file.Write(BitConverter.GetBytes(entry));
    }

    private static void Entry(Span<byte> entry, string name, byte type, uint child, uint start, int size)
    {
        if (name.Length > 0)
        {
            Encoding.Unicode.GetBytes(name).CopyTo(entry);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[64..], (ushort)((name.Length + 1) * 2));
        }
        entry[66] = type;
        entry[67] = 1; // black node of the red-black tree
        BinaryPrimitives.WriteUInt32LittleEndian(entry[68..], NoStream); // left sibling
        BinaryPrimitives.WriteUInt32LittleEndian(entry[72..], NoStream); // right sibling
        BinaryPrimitives.WriteUInt32LittleEndian(entry[76..], child);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[116..], start);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[120..], (uint)size);
    }
}

/// <summary>Writes an .ods package from hand-written content.xml (and optionally styles.xml / settings.xml).</summary>
internal static class OdsBuilder
{
    public const string Namespaces =
        "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
        "xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" " +
        "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" " +
        "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" " +
        "xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" " +
        "xmlns:number=\"urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0\" " +
        "xmlns:config=\"urn:oasis:names:tc:opendocument:xmlns:config:1.0\" " +
        "xmlns:calcext=\"urn:org:documentfoundation:names:experimental:calc:xmlns:calcext:1.0\"";

    /// <param name="automaticStyles">Children of office:automatic-styles in content.xml.</param>
    /// <param name="tables">table:table elements.</param>
    /// <param name="styles">Children of office:styles in styles.xml.</param>
    /// <param name="settings">settings.xml content (without the declaration), or null.</param>
    /// <param name="encrypted">Declares content.xml as encrypted in the manifest, as password-protected files do.</param>
    public static string Save(string path, string automaticStyles, string tables, string styles = "", string? settings = null, bool encrypted = false)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "mimetype", "application/vnd.oasis.opendocument.spreadsheet", CompressionLevel.NoCompression);
        Add(zip, "content.xml", OpcWriter.Declaration +
            $"<office:document-content {Namespaces} office:version=\"1.3\"><office:automatic-styles>{automaticStyles}</office:automatic-styles>" +
            $"<office:body><office:spreadsheet>{tables}</office:spreadsheet></office:body></office:document-content>");
        Add(zip, "styles.xml", OpcWriter.Declaration + $"<office:document-styles {Namespaces} office:version=\"1.3\"><office:styles>{styles}</office:styles></office:document-styles>");
        if (settings is not null)
            Add(zip, "settings.xml", OpcWriter.Declaration + settings);
        Add(zip, "META-INF/manifest.xml", OpcWriter.Declaration +
            "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.3\">" +
            "<manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.spreadsheet\"/>" +
            "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\">" +
            (encrypted ? "<manifest:encryption-data manifest:checksum-type=\"SHA1/1K\" manifest:checksum=\"AAAA\"><manifest:algorithm manifest:algorithm-name=\"Blowfish CFB\" manifest:initialisation-vector=\"AAAA\"/></manifest:encryption-data>" : "") +
            "</manifest:file-entry></manifest:manifest>");
        return path;
    }

    private static void Add(ZipArchive zip, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
