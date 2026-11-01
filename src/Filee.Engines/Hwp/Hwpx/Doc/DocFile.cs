// A Word 97–2003 document (.doc, .dot, and WPS Office's .wps, which is the same format) opened from its compound
// file: the WordDocument stream with its File Information Block (FIB), the table stream it names (0Table or 1Table),
// the Data stream, the piece table that maps character positions (CPs) to the text and the formatted disk pages
// (FKPs) that give each run of text its character properties (CHPX) and each paragraph its paragraph properties
// (PAPX), both as lists of single property modifiers (sprms).
//
// What Filee can't read is refused with a message that says what to do, before anything else is read: documents
// protected with a password (RC4 CryptoAPI encryption or XOR obfuscation, nothing is ever decrypted), Word 6.0 / 95
// and older files (another FIB), and compound files that are no Word document (Microsoft Works' .wps).
//
// Layout of the structures as described in Microsoft's published [MS-DOC] specification. Everything points into
// streams that come from anywhere: every offset and length is checked against its stream, and damage is
// InvalidDataException.

using System.Buffers.Binary;
using System.Text;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Hwpx.Doc;

/// <summary>A Word 97–2003 document's streams, text and formatting runs.</summary>
internal sealed class DocFile : IDisposable
{
    /// <summary>wIdent of a Word 97 and later FIB (also of Word 6.0 / 95, which a smaller nFib tells apart).</summary>
    private const ushort WordIdent = 0xA5EC;

    /// <summary>nFib of Word 97; later versions keep it in FibBase and add their own after the blob.</summary>
    private const ushort Word97 = 0x00C1;

    /// <summary>Pairs of FibRgFcLcb97 (the structures Word 97 has; later versions add more after them).</summary>
    private const int FcLcb97Count = 93;

    private const int PageSize = 512;

    private readonly CompoundFile _cfb;

    private DocFile(CompoundFile cfb, byte[] word)
    {
        _cfb = cfb;
        Word = word;
    }

    public DocumentLimits Limits => _cfb.Limits;

    /// <summary>The WordDocument stream (FIB, text, FKPs, section properties).</summary>
    public byte[] Word { get; }

    /// <summary>The table stream (styles, piece table, plexes, lists, drawings).</summary>
    public byte[] Table { get; private set; } = [];

    /// <summary>The Data stream (pictures, large paragraph properties); empty when there is none.</summary>
    public byte[] Data { get; private set; } = [];

    public ushort Flags { get; private set; }

    /// <summary>The document is a template (.dot).</summary>
    public bool Template => (Flags & 0x0001) != 0;

    /// <summary>Lengths of the documents in the CP space, in order: main text, footnotes, headers, comments, endnotes,
    /// text boxes, header text boxes.</summary>
    public int CcpText { get; private set; }
    public int CcpFtn { get; private set; }
    public int CcpHdd { get; private set; }
    public int CcpAtn { get; private set; }
    public int CcpEdn { get; private set; }
    public int CcpTxbx { get; private set; }
    public int CcpHdrTxbx { get; private set; }

    public int FootnoteStart => CcpText;
    public int HeaderStart => CcpText + CcpFtn;
    public int EndnoteStart => HeaderStart + CcpHdd + CcpAtn;
    public int TextBoxStart => EndnoteStart + CcpEdn;
    public int HeaderTextBoxStart => TextBoxStart + CcpTxbx;

    private (uint Fc, uint Lcb)[] _fcLcb = [];

    /// <summary>The pieces of the text: CP range, where the characters are and how they are stored.</summary>
    public List<Piece> Pieces { get; } = [];

    /// <summary>Property modifiers of the piece table (Prc), referred to by the pieces' Prm.</summary>
    public List<byte[]> PieceProperties { get; } = [];

    /// <summary>Character property runs from the CHPX FKPs, by stream offset.</summary>
    public List<FcRun> CharacterRuns { get; } = [];

    /// <summary>Paragraph property runs from the PAPX FKPs, by stream offset; the grpprl starts with the istd.</summary>
    public List<FcRun> ParagraphRuns { get; } = [];

    /// <summary>A piece: CPs [Cp, CpEnd), at <see cref="Fc"/> in the WordDocument stream, one byte per character when
    /// <see cref="Compressed"/>. <see cref="Prm"/> holds property modifiers that apply to all of its text.</summary>
    internal readonly record struct Piece(int Cp, int CpEnd, int Fc, bool Compressed, byte[]? Prm)
    {
        public int Width => Compressed ? 1 : 2;

        public int FcOf(int cp) => Fc + (cp - Cp) * Width;
    }

    /// <summary>A run of bytes [Fc, FcEnd) of the WordDocument stream with its property modifiers.</summary>
    internal readonly record struct FcRun(int Fc, int FcEnd, byte[] Grpprl);

    // ───────────────────────── Opening ─────────────────────────

    /// <summary>Opens <paramref name="path"/>, refusing what Filee doesn't read.</summary>
    public static DocFile Open(string path, DocumentLimits limits)
    {
        if (!CompoundFile.HasSignature(path))
            throw new InvalidDataException("The file is not a Word 97–2003 document (it is not a compound file). It may be damaged or another program's file with a .doc name.");
        var cfb = CompoundFile.Open(path, limits);
        try
        {
            var file = new DocFile(cfb, Refuse(cfb) ?? throw new InvalidDataException("The file is not a Word document."));
            file.Load();
            return file;
        }
        catch
        {
            cfb.Dispose();
            throw;
        }
    }

    public void Dispose() => _cfb.Dispose();

    /// <summary>
    /// What Filee doesn't read in a compound file, in a few words ("a Word 6.0 / 95 document"); null when it is a Word
    /// 97–2003 document. For the route planner: such a file takes another engine (LibreOffice) when there is one.
    /// </summary>
    public static string? Unsupported(string path)
    {
        try
        {
            if (!CompoundFile.HasSignature(path))
                return null; // RTF, HTML or DOCX with a .doc name: read by their own readers
            using var cfb = CompoundFile.Open(path);
            if (!cfb.Exists("WordDocument"))
                return cfb.Exists("MatOST") || cfb.Exists("CONTENTS") ? "a Microsoft Works document" : "a compound file without a Word document";
            var word = cfb.Read("WordDocument");
            if (word is null || word.Length < 34 || BinaryPrimitives.ReadUInt16LittleEndian(word) != WordIdent)
                return "an older Word document";
            if (BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(2)) < Word97)
                return "a Word 6.0 / 95 document";
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or Filee.Core.Conversion.ConversionRefusedException)
        {
            return null; // the reader says what is wrong
        }
    }

    /// <summary>The WordDocument stream, or a refusal with the reason.</summary>
    private static byte[]? Refuse(CompoundFile cfb)
    {
        if (!cfb.Exists("WordDocument"))
        {
            if (cfb.Exists("MatOST") || cfb.Exists("CONTENTS"))
                throw new InvalidDataException("The file is a Microsoft Works document, which Filee reads only through LibreOffice. Install LibreOffice in Settings → Engines and convert it again.");
            if (cfb.Exists("EncryptionInfo") || cfb.Exists("EncryptedPackage"))
                throw new InvalidDataException("The Word document is protected with a password (encrypted). Open it in Word, remove the password (File → Info → Protect Document), save it and convert it again.");
            throw new InvalidDataException("The file is not a Word document (it has no WordDocument stream). It may be another program's file with a .doc name.");
        }
        var word = cfb.Read("WordDocument")!;
        if (word.Length < 34 || BinaryPrimitives.ReadUInt16LittleEndian(word) != WordIdent)
            throw new InvalidDataException("The file is a Word document older than Word 6.0, which Filee does not read. Open it in Word, save it as a Word document and convert it again.");
        var nFib = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(2));
        if (nFib < Word97)
            throw new InvalidDataException("The file is a Word 6.0 / 95 document, which Filee reads only through LibreOffice. Install LibreOffice in Settings → Engines, or open it in Word and save it as a Word document, and convert it again.");
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(10));
        if ((flags & 0x0100) != 0)
            throw new InvalidDataException("The Word document is protected with a password (encrypted). Open it in Word, remove the password (File → Info → Protect Document), save it and convert it again.");
        return word;
    }

    private void Load()
    {
        Flags = BinaryPrimitives.ReadUInt16LittleEndian(Word.AsSpan(10));
        // FibBase (32 bytes), csw and FibRgW97, cslw and FibRgLw97, cbRgFcLcb and the FibRgFcLcb blob.
        var at = 32;
        var csw = U16(Word, at, "the FIB");
        at += 2 + csw * 2;
        var cslw = U16(Word, at, "the FIB");
        var lw = at + 2;
        if (cslw < 11 || lw + cslw * 4 > Word.Length)
            throw Damaged("its FIB is cut short");
        int Ccp(int index)
        {
            var value = BinaryPrimitives.ReadInt32LittleEndian(Word.AsSpan(lw + index * 4));
            if (value < 0 || value > Word.Length)
                throw Damaged("its FIB gives impossible text lengths");
            return value;
        }
        CcpText = Ccp(3);
        CcpFtn = Ccp(4);
        CcpHdd = Ccp(5);
        CcpAtn = Ccp(7);
        CcpEdn = Ccp(8);
        CcpTxbx = Ccp(9);
        CcpHdrTxbx = Ccp(10);
        at = lw + cslw * 4;
        var cbRgFcLcb = U16(Word, at, "the FIB");
        at += 2;
        if (at + (long)cbRgFcLcb * 8 > Word.Length)
            throw Damaged("its FIB is cut short");
        _fcLcb = new (uint, uint)[Math.Min(cbRgFcLcb, (ushort)FcLcb97Count)];
        for (var i = 0; i < _fcLcb.Length; i++)
            _fcLcb[i] = (BinaryPrimitives.ReadUInt32LittleEndian(Word.AsSpan(at + i * 8)), BinaryPrimitives.ReadUInt32LittleEndian(Word.AsSpan(at + i * 8 + 4)));

        // fWhichTblStm: the table stream is 1Table or 0Table.
        var tableName = (Flags & 0x0200) != 0 ? "1Table" : "0Table";
        Table = _cfb.Read(tableName) ?? throw new InvalidDataException($"The Word document is incomplete: its {tableName} stream is missing.");
        Data = _cfb.Read("Data") ?? [];

        ReadPieceTable();
        ReadFkps(Blob(FibIndex.PlcfBteChpx), CharacterRuns, paragraphs: false);
        ReadFkps(Blob(FibIndex.PlcfBtePapx), ParagraphRuns, paragraphs: true);
    }

    /// <summary>The document's title from its summary information, or null.</summary>
    public string? Title()
    {
        try
        {
            return PropertySet.Title(_cfb.Read("\u0005SummaryInformation"));
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>A stream of an embedded object's storage (ObjectPool/_1234/\u0003PIC); null when missing or damaged.</summary>
    public byte[]? ObjectStream(string path)
    {
        try
        {
            return _cfb.Read(path);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    // ───────────────────────── FIB blob ─────────────────────────

    /// <summary>The structure at entry <paramref name="index"/> of FibRgFcLcb in the table stream; empty when the
    /// document has none. One that reaches past the stream is damage.</summary>
    public ReadOnlySpan<byte> Blob(int index) => BlobIn(Table, index);

    /// <summary>Like <see cref="Blob"/>, for the structures the FIB places in the WordDocument stream.</summary>
    public ReadOnlySpan<byte> BlobInWord(int index) => BlobIn(Word, index);

    private ReadOnlySpan<byte> BlobIn(byte[] stream, int index)
    {
        if (index >= _fcLcb.Length)
            return [];
        var (fc, lcb) = _fcLcb[index];
        if (lcb == 0)
            return [];
        if (fc > stream.Length || lcb > stream.Length - fc)
            throw Damaged("a structure its FIB points to lies outside its stream");
        return stream.AsSpan((int)fc, (int)lcb);
    }

    /// <summary>The offset of entry <paramref name="index"/> (for structures whose length doesn't cover what follows).</summary>
    public (int Fc, int Lcb) Location(int index) =>
        index < _fcLcb.Length && _fcLcb[index].Fc <= Table.Length ? ((int)_fcLcb[index].Fc, (int)Math.Min(_fcLcb[index].Lcb, (uint)int.MaxValue)) : (0, 0);

    /// <summary>Entries of FibRgFcLcb97 Filee reads.</summary>
    internal static class FibIndex
    {
        public const int Stshf = 1;
        public const int PlcffndRef = 2;
        public const int PlcffndTxt = 3;
        public const int PlcfSed = 6;
        public const int PlcfHdd = 11;
        public const int PlcfBteChpx = 12;
        public const int PlcfBtePapx = 13;
        public const int SttbfFfn = 15;
        public const int SttbfBkmk = 21;
        public const int PlcfBkf = 22;
        public const int PlcfBkl = 23;
        public const int Dop = 31;
        public const int Clx = 33;
        public const int PlcSpaMom = 40;
        public const int PlcSpaHdr = 41;
        public const int PlcfendRef = 46;
        public const int PlcfendTxt = 47;
        public const int DggInfo = 50;
        public const int PlcftxbxTxt = 56;
        public const int PlcfHdrtxbxTxt = 58;
        public const int PlfLst = 73;
        public const int PlfLfo = 74;
    }

    // ───────────────────────── Piece table ─────────────────────────

    /// <summary>The CLX: property modifiers (Prc, clxt 1) and then the piece table (Pcdt, clxt 2).</summary>
    private void ReadPieceTable()
    {
        var clx = Blob(FibIndex.Clx);
        var at = 0;
        while (at < clx.Length && clx[at] == 0x01)
        {
            if (at + 3 > clx.Length)
                throw Damaged("its piece table is cut short");
            var cb = BinaryPrimitives.ReadInt16LittleEndian(clx[(at + 1)..]);
            if (cb < 0 || at + 3 + cb > clx.Length)
                throw Damaged("its piece table is cut short");
            PieceProperties.Add(clx.Slice(at + 3, cb).ToArray());
            at += 3 + cb;
        }
        if (at + 5 > clx.Length || clx[at] != 0x02)
        {
            // No piece table (Word always writes one): the text is the bytes after the FIB, as Word 6 had it.
            throw Damaged("it has no piece table");
        }
        var lcb = BinaryPrimitives.ReadUInt32LittleEndian(clx[(at + 1)..]);
        if (lcb > clx.Length - at - 5)
            throw Damaged("its piece table is cut short");
        var plc = clx.Slice(at + 5, (int)lcb);
        var count = Plc.Count(plc.Length, 8);
        if (count < 1)
            throw Damaged("its piece table is empty");
        var previous = 0;
        for (var i = 0; i < count; i++)
        {
            var cp = Plc.Cp(plc, i);
            var end = Plc.Cp(plc, i + 1);
            if (cp != previous || end < cp)
                throw Damaged("its piece table is out of order");
            previous = end;
            var pcd = Plc.Data(plc, count, 8, i);
            var raw = BinaryPrimitives.ReadUInt32LittleEndian(pcd[2..]);
            var compressed = (raw & 0x40000000) != 0;
            var fc = (long)(raw & 0x3FFFFFFF);
            if (compressed)
                fc /= 2;
            var width = compressed ? 1 : 2;
            if (fc + (long)(end - cp) * width > Word.Length)
                throw Damaged("its text lies outside the WordDocument stream");
            var prm = BinaryPrimitives.ReadUInt16LittleEndian(pcd[6..]);
            // Prm1 (bit 0 set): an index into the Prc list; Prm0 (a single sprm by a table of 128) is left out.
            byte[]? properties = (prm & 1) != 0 && (prm >> 1) < PieceProperties.Count ? PieceProperties[prm >> 1] : null;
            if (end > cp)
                Pieces.Add(new Piece(cp, end, (int)fc, compressed, properties));
        }
    }

    /// <summary>The CP after the last character of every document (main text, notes, headers, ...).</summary>
    public int CpEnd => Pieces.Count > 0 ? Pieces[^1].CpEnd : 0;

    /// <summary>The piece holding <paramref name="cp"/>, or -1.</summary>
    public int PieceAt(int cp)
    {
        int low = 0, high = Pieces.Count - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var piece = Pieces[middle];
            if (cp < piece.Cp)
                high = middle - 1;
            else if (cp >= piece.CpEnd)
                low = middle + 1;
            else
                return middle;
        }
        return -1;
    }

    /// <summary>The text of CPs [<paramref name="start"/>, <paramref name="end"/>); CPs without a piece are left out.</summary>
    public string Text(int start, int end)
    {
        var text = new StringBuilder(Math.Max(0, Math.Min(end - start, 1 << 20)));
        for (var index = Math.Max(0, PieceAt(start)); index >= 0 && index < Pieces.Count; index++)
        {
            var piece = Pieces[index];
            if (piece.Cp >= end)
                break;
            var from = Math.Max(start, piece.Cp);
            var to = Math.Min(end, piece.CpEnd);
            if (to <= from)
                continue;
            var fc = piece.FcOf(from);
            if (piece.Compressed)
            {
                for (var i = 0; i < to - from; i++)
                    text.Append(Ansi(Word[fc + i]));
            }
            else
            {
                text.Append(Encoding.Unicode.GetString(Word, fc, (to - from) * 2));
            }
        }
        return text.ToString();
    }

    /// <summary>A character of a compressed piece: Windows-1252, as [MS-DOC] maps 0x80–0x9F.</summary>
    private static char Ansi(byte value) => value is >= 0x80 and <= 0x9F ? Windows1252[value - 0x80] : (char)value;

    private static readonly char[] Windows1252 =
    [
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
    ];

    // ───────────────────────── FKPs ─────────────────────────

    /// <summary>
    /// The runs of a PlcBteChpx / PlcBtePapx: the FC of each FKP and the page number (pn) of its 512-byte page in the
    /// WordDocument stream. A page that lies outside the stream is damage; pages are read once.
    /// </summary>
    private void ReadFkps(ReadOnlySpan<byte> plc, List<FcRun> runs, bool paragraphs)
    {
        var count = Plc.Count(plc.Length, 4);
        var seen = new HashSet<uint>();
        for (var i = 0; i < count; i++)
        {
            var pn = BinaryPrimitives.ReadUInt32LittleEndian(Plc.Data(plc, count, 4, i)) & 0x3FFFFF;
            if (!seen.Add(pn))
                continue;
            if ((pn + 1L) * PageSize > Word.Length)
                throw Damaged("a page of its formatting lies outside the WordDocument stream");
            var page = Word.AsSpan((int)(pn * PageSize), PageSize);
            if (paragraphs)
                ReadPapxFkp(page, runs);
            else
                ReadChpxFkp(page, runs);
        }
        runs.Sort((a, b) => a.Fc.CompareTo(b.Fc));
    }

    /// <summary>ChpxFkp: crun FCs + 1, then a byte per run (the CHPX's offset in words), crun in the last byte.</summary>
    private static void ReadChpxFkp(ReadOnlySpan<byte> page, List<FcRun> runs)
    {
        var crun = page[PageSize - 1];
        if (crun == 0 || (crun + 1) * 4 + crun > PageSize - 1)
            return;
        for (var i = 0; i < crun; i++)
        {
            var fc = BinaryPrimitives.ReadInt32LittleEndian(page[(i * 4)..]);
            var fcEnd = BinaryPrimitives.ReadInt32LittleEndian(page[((i + 1) * 4)..]);
            var offset = page[(crun + 1) * 4 + i] * 2;
            byte[] grpprl = [];
            if (offset != 0 && offset < PageSize - 1)
            {
                var cb = page[offset];
                grpprl = page.Slice(offset + 1, Math.Min(cb, PageSize - 1 - offset - 1)).ToArray();
            }
            if (fcEnd > fc)
                runs.Add(new FcRun(fc, fcEnd, grpprl));
        }
    }

    /// <summary>PapxFkp: cpara FCs + 1, then 13-byte BX entries (the PAPX's offset in words, a PHE), cpara last.</summary>
    private static void ReadPapxFkp(ReadOnlySpan<byte> page, List<FcRun> runs)
    {
        var cpara = page[PageSize - 1];
        if (cpara == 0 || (cpara + 1) * 4 + cpara * 13 > PageSize - 1)
            return;
        for (var i = 0; i < cpara; i++)
        {
            var fc = BinaryPrimitives.ReadInt32LittleEndian(page[(i * 4)..]);
            var fcEnd = BinaryPrimitives.ReadInt32LittleEndian(page[((i + 1) * 4)..]);
            var offset = page[(cpara + 1) * 4 + i * 13] * 2;
            byte[] grpprl = [0, 0];
            if (offset != 0 && offset < PageSize - 2)
            {
                // PapxInFkp: cb; when cb is 0 the next byte is cb' and the GrpPrlAndIstd is 2 × cb' long, else 2 × cb − 1.
                var cb = page[offset];
                var start = offset + 1;
                var length = cb == 0 ? page[offset + 1] * 2 : cb * 2 - 1;
                if (cb == 0)
                    start++;
                length = Math.Min(length, PageSize - 1 - start);
                if (length >= 2)
                    grpprl = page.Slice(start, length).ToArray();
            }
            if (fcEnd > fc)
                runs.Add(new FcRun(fc, fcEnd, grpprl));
        }
    }

    /// <summary>The run of <paramref name="runs"/> holding stream offset <paramref name="fc"/>, or -1.</summary>
    public static int RunAt(List<FcRun> runs, int fc)
    {
        int low = 0, high = runs.Count - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (runs[middle].Fc <= fc)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return found >= 0 && fc < runs[found].FcEnd ? found : -1;
    }

    /// <summary>The start of the first run after <paramref name="fc"/>, or null.</summary>
    public static int? RunAfter(List<FcRun> runs, int fc)
    {
        int low = 0, high = runs.Count - 1;
        int? found = null;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (runs[middle].Fc > fc)
            {
                found = runs[middle].Fc;
                high = middle - 1;
            }
            else
            {
                low = middle + 1;
            }
        }
        return found;
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static ushort U16(byte[] bytes, int offset, string what) =>
        offset + 2 <= bytes.Length ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset)) : throw Damaged($"{what} is cut short");

    internal static InvalidDataException Damaged(string reason) => new($"The Word document is damaged: {reason}.");
}

/// <summary>
/// A PLC ("plex"): n + 1 CPs (4 bytes each) followed by n data elements of a fixed size. A plex whose length doesn't
/// fit that shape is read as far as it does.
/// </summary>
internal static class Plc
{
    /// <summary>Elements of a plex <paramref name="length"/> bytes long with elements of <paramref name="dataSize"/> bytes.</summary>
    public static int Count(int length, int dataSize) => length < 4 ? 0 : (length - 4) / (4 + dataSize);

    /// <summary>CP <paramref name="index"/> (0..count).</summary>
    public static int Cp(ReadOnlySpan<byte> plc, int index) => BinaryPrimitives.ReadInt32LittleEndian(plc[(index * 4)..]);

    /// <summary>Data element <paramref name="index"/> of a plex of <paramref name="count"/> elements.</summary>
    public static ReadOnlySpan<byte> Data(ReadOnlySpan<byte> plc, int count, int dataSize, int index) =>
        plc.Slice((count + 1) * 4 + index * dataSize, dataSize);

    /// <summary>The CPs of a plex without data (n + 1 of them for n elements, all of them here).</summary>
    public static int[] Cps(ReadOnlySpan<byte> plc)
    {
        var cps = new int[plc.Length / 4];
        for (var i = 0; i < cps.Length; i++)
            cps[i] = Cp(plc, i);
        return cps;
    }
}

/// <summary>A single property modifier: the operation (sgc, spra and the property) and its operand.</summary>
internal readonly record struct Sprm(ushort Op, byte[] Operand)
{
    /// <summary>The kind of property: 1 paragraph, 2 character, 3 picture, 4 section, 5 table.</summary>
    public int Group => (Op >> 10) & 7;

    public byte Byte => Operand.Length > 0 ? Operand[0] : (byte)0;

    public short Short => Operand.Length >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(Operand) : (short)Byte;

    public ushort UShort => Operand.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(Operand) : Byte;

    public int Int => Operand.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(Operand) : Short;

    /// <summary>
    /// The sprms of a grpprl. The operand size comes from spra (bits 13–15 of the operation): 1, 1, 2, 4, 2, 2,
    /// variable (a length byte; sprmTDefTable a length word one more than its bytes) or 3 bytes. A sprm cut short ends
    /// the list.
    /// </summary>
    public static List<Sprm> Parse(ReadOnlySpan<byte> grpprl)
    {
        var sprms = new List<Sprm>();
        var at = 0;
        while (at + 2 <= grpprl.Length)
        {
            var op = BinaryPrimitives.ReadUInt16LittleEndian(grpprl[at..]);
            at += 2;
            int size;
            switch (op >> 13)
            {
                case 0 or 1:
                    size = 1;
                    break;
                case 2 or 4 or 5:
                    size = 2;
                    break;
                case 3:
                    size = 4;
                    break;
                case 7:
                    size = 3;
                    break;
                default:
                    if (op == Ops.TDefTable)
                    {
                        if (at + 2 > grpprl.Length)
                            return sprms;
                        size = Math.Max(0, BinaryPrimitives.ReadUInt16LittleEndian(grpprl[at..]) - 1);
                        at += 2;
                    }
                    else if (op == Ops.PChgTabs && at < grpprl.Length && grpprl[at] == 255)
                    {
                        // cb 255: the size follows from the deleted and added tab stops (PChgTabsDelClose, PChgTabsAdd).
                        var del = at + 1 < grpprl.Length ? grpprl[at + 1] : 0;
                        var add = at + 2 + del * 4 < grpprl.Length ? grpprl[at + 2 + del * 4] : 0;
                        size = 1 + del * 4 + 1 + add * 3;
                        at++;
                    }
                    else
                    {
                        if (at >= grpprl.Length)
                            return sprms;
                        size = grpprl[at];
                        at++;
                    }
                    break;
            }
            if (at + size > grpprl.Length)
                return sprms;
            sprms.Add(new Sprm(op, grpprl.Slice(at, size).ToArray()));
            at += size;
        }
        return sprms;
    }
}

/// <summary>The sprms Filee reads, by their [MS-DOC] names.</summary>
internal static class Ops
{
    // Paragraphs
    public const ushort PIstd = 0x4600;
    public const ushort PJc80 = 0x2403;
    public const ushort PJc = 0x2461;
    public const ushort PFKeep = 0x2405;
    public const ushort PFKeepFollow = 0x2406;
    public const ushort PFPageBreakBefore = 0x2407;
    public const ushort PIlvl = 0x260A;
    public const ushort PIlfo = 0x460B;
    public const ushort PChgTabsPapx = 0xC60D;
    public const ushort PChgTabs = 0xC615;
    public const ushort PDxaRight80 = 0x840E;
    public const ushort PDxaLeft80 = 0x840F;
    public const ushort PDxaLeft180 = 0x8411;
    public const ushort PDxaRight = 0x845D;
    public const ushort PDxaLeft = 0x845E;
    public const ushort PDxaLeft1 = 0x8460;
    public const ushort PDxcRight = 0x4455;
    public const ushort PDxcLeft = 0x4456;
    public const ushort PDxcLeft1 = 0x4457;
    public const ushort PDyaLine = 0x6412;
    public const ushort PDyaBefore = 0xA413;
    public const ushort PDyaAfter = 0xA414;
    public const ushort PFInTable = 0x2416;
    public const ushort PFTtp = 0x2417;
    public const ushort PFWidowControl = 0x2431;
    public const ushort PFKinsoku = 0x2433;
    public const ushort PFWordWrap = 0x2434;
    public const ushort PFOverflowPunct = 0x2435;
    public const ushort PFTopLinePunct = 0x2436;
    public const ushort PFAutoSpaceDE = 0x2437;
    public const ushort PFAutoSpaceDN = 0x2438;
    public const ushort PBrcTop80 = 0x6424;
    public const ushort PBrcLeft80 = 0x6425;
    public const ushort PBrcBottom80 = 0x6426;
    public const ushort PBrcRight80 = 0x6427;
    public const ushort PBrcBetween80 = 0x6428;
    public const ushort PShd80 = 0x442D;
    public const ushort POutLvl = 0x2640;
    public const ushort PFBiDi = 0x2441;
    public const ushort PFInnerTableCell = 0x244B;
    public const ushort PFInnerTtp = 0x244C;
    public const ushort PShd = 0xC64D;
    public const ushort PBrcTop = 0xC64E;
    public const ushort PBrcLeft = 0xC64F;
    public const ushort PBrcBottom = 0xC650;
    public const ushort PBrcRight = 0xC651;
    public const ushort PBrcBetween = 0xC652;
    public const ushort PDyaBeforeAuto = 0x245B;
    public const ushort PDyaAfterAuto = 0x245C;
    public const ushort PFContextualSpacing = 0x246D;
    public const ushort PHugePapx = 0x6646;
    public const ushort PItap = 0x6649;
    public const ushort PFUsePgsuSettings = 0x2447;

    // Characters
    public const ushort CFRMarkDel = 0x0800;
    public const ushort CFFldVanish = 0x0802;
    public const ushort CPicLocation = 0x6A03;
    public const ushort CFData = 0x0806;
    public const ushort CFOle2 = 0x080A;
    public const ushort CHighlight = 0x2A0C;
    public const ushort CFSpec = 0x0855;
    public const ushort CFObj = 0x0856;
    public const ushort CSymbol = 0x6A09;
    public const ushort CIstd = 0x4A30;
    public const ushort CFBold = 0x0835;
    public const ushort CFItalic = 0x0836;
    public const ushort CFStrike = 0x0837;
    public const ushort CFOutline = 0x0838;
    public const ushort CFShadow = 0x0839;
    public const ushort CFSmallCaps = 0x083A;
    public const ushort CFCaps = 0x083B;
    public const ushort CFVanish = 0x083C;
    public const ushort CKul = 0x2A3E;
    public const ushort CDxaSpace = 0x8840;
    public const ushort CIco = 0x2A42;
    public const ushort CHps = 0x4A43;
    public const ushort CHpsPos = 0x4845;
    public const ushort CIss = 0x2A48;
    public const ushort CRgFtc0 = 0x4A4F;
    public const ushort CRgFtc1 = 0x4A50;
    public const ushort CRgFtc2 = 0x4A51;
    public const ushort CCharScale = 0x4852;
    public const ushort CFDStrike = 0x2A53;
    public const ushort CFImprint = 0x0854;
    public const ushort CFEmboss = 0x0858;
    public const ushort CFBoldBi = 0x085C;
    public const ushort CFItalicBi = 0x085D;
    public const ushort CFtcBi = 0x4A5E;
    public const ushort CHpsBi = 0x4A61;
    public const ushort CShd80 = 0x4866;
    public const ushort CCv = 0x6870;
    public const ushort CShd = 0xCA71;
    public const ushort CRgLid0 = 0x4873;
    public const ushort CRgLid1 = 0x4874;
    public const ushort CFNoProof = 0x0875;

    // Sections
    public const ushort SBkc = 0x3009;
    public const ushort SFTitlePage = 0x300A;
    public const ushort SCcolumns = 0x500B;
    public const ushort SDxaColumns = 0x900C;
    public const ushort SFEvenlySpaced = 0x3005;
    public const ushort SDxaColWidth = 0xF203;
    public const ushort SDxaColSpacing = 0xF204;
    public const ushort SFPgnRestart = 0x3011;
    public const ushort SPgnStart97 = 0x501C;
    public const ushort SPgnStart = 0x7044;
    public const ushort SDyaHdrTop = 0xB017;
    public const ushort SDyaHdrBottom = 0xB018;
    public const ushort SLBetween = 0x3019;
    public const ushort SBOrientation = 0x301D;
    public const ushort SXaPage = 0xB01F;
    public const ushort SYaPage = 0xB020;
    public const ushort SDxaLeft = 0xB021;
    public const ushort SDxaRight = 0xB022;
    public const ushort SDyaTop = 0x9023;
    public const ushort SDyaBottom = 0x9024;
    public const ushort SDzaGutter = 0xB025;
    public const ushort SDyaLinePitch = 0x9031;
    public const ushort SClm = 0x5032;
    public const ushort STextFlow = 0x5033;

    // Tables
    public const ushort TJc90 = 0x5400;
    public const ushort TFCantSplit90 = 0x3403;
    public const ushort TTableHeader = 0x3404;
    public const ushort TTableBorders80 = 0xD605;
    public const ushort TDxaLeft = 0x9601;
    public const ushort TDxaGapHalf = 0x9602;
    public const ushort TDyaRowHeight = 0x9407;
    public const ushort TDefTable = 0xD608;
    public const ushort TDefTableShd80 = 0xD609;
    public const ushort TDefTableShd3rd = 0xD60C;
    public const ushort TDefTableShd = 0xD612;
    public const ushort TTableBorders = 0xD613;
    public const ushort TDefTableShd2nd = 0xD616;
    public const ushort TSetBrc80 = 0xD620;
    public const ushort TVertMerge = 0xD62B;
    public const ushort TVertAlign = 0xD62C;
    public const ushort TSetBrc = 0xD62F;
    public const ushort TCellPadding = 0xD632;
    public const ushort TCellPaddingDefault = 0xD634;
    public const ushort TIstd = 0x563A;
    public const ushort TFCantSplit = 0x3644;
    public const ushort TJc = 0x548A;
    public const ushort TDefTableShdRaw = 0xD670;
    public const ushort TDefTableShdRaw2nd = 0xD671;
    public const ushort TDefTableShdRaw3rd = 0xD672;
}
