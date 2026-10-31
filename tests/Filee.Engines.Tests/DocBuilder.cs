// Word 97–2003 (DOC) files built in code for the DOC reader's tests: stories of paragraphs and runs with their sprms,
// laid out the way Word saves them: a FIB, the text in Unicode or 8-bit pieces, CHPX and PAPX FKPs, a stylesheet,
// fonts, sections (SEPX), headers and footers, footnotes and endnotes, lists, bookmarks, inline pictures (PICF and an
// OfficeArt BLIP in the Data stream) and text boxes (OfficeArt drawing, SPA and text box story), packed as a compound
// file with the HWP builder's writer. Flags make files the reader refuses (encrypted, Word 6 / 95).

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Tests;

internal sealed class DocBuilder
{
    private const int TextStart = 1024;
    private const int Page = 512;

    /// <summary>A run of text with its character sprms.</summary>
    public sealed record Run(string Text, byte[]? Chpx = null);

    /// <summary>A style: its name, sti, kind (1 paragraph, 2 character, 3 table, 4 list), base istd and property exceptions.</summary>
    public sealed record Style(string Name, int Sti, int Kind, int Base, byte[] Papx, byte[] Chpx, byte[]? Tapx = null);

    /// <summary>A list level: number format (nfc), number text (characters 0–8 for the levels' numbers), start, sprms.</summary>
    public sealed record Level(int Nfc, string Text, int Start = 1, byte[]? Papx = null, byte[]? Chpx = null);

    private sealed class Story
    {
        public StringBuilder Text { get; } = new();
        public List<(int Start, int End, byte[] Chpx)> Runs { get; } = [];
        public List<(int Mark, int Istd, byte[] Papx)> Paragraphs { get; } = [];
        public int Length => Text.Length;
    }

    private readonly Story _main = new(), _footnotes = new(), _headers = new(), _endnotes = new(), _textBoxes = new();
    private readonly List<(byte[] Chpx, int Start)> _footnoteRefs = [], _endnoteRefs = [];
    private readonly List<(int End, byte[] Sepx)> _sections = [];
    private readonly Dictionary<(int Section, int Kind), Run[]> _headerStories = [];
    private readonly List<(int Lsid, Level[] Levels, bool Simple)> _lists = [];
    private readonly List<(int Lsid, (int Level, int Start)[] Overrides)> _lfos = [];
    private readonly List<(string Name, int Start, int End)> _bookmarks = [];
    private readonly MemoryStream _data = new();
    private readonly List<(byte[] Chpx, int Spid, int Left, int Top, int Right, int Bottom, ushort Flags)> _anchors = [];
    private readonly List<(int Spid, int Type, byte[] Properties, int? TextStory, byte[]? Png)> _shapes = [];
    private readonly List<(int Start, int Spid)> _textBoxStories = [];

    public DocBuilder()
    {
        _data.Write(new byte[4]); // offset 0 is "no picture"
    }

    /// <summary>Styles by istd (null = an empty slot). Normal, heading 1, the defaults at 10–12, as Word writes them.</summary>
    public List<Style?> Styles { get; } =
    [
        new("Normal", 0, 1, 0xFFF, [], []),
        new("heading 1", 1, 1, 0, [.. Sprm(0x2406, (byte)1), .. Sprm(0xA413, (short)240), .. Sprm(0xA414, (short)60)], [.. Sprm(0x0835, (byte)1), .. Sprm(0x4A43, (ushort)32)]),
        null, null, null, null, null, null, null, null,
        new("Default Paragraph Font", 65, 2, 0xFFF, [], []),
        new("Table Normal", 105, 3, 0xFFF, [], [], [.. Sprm(0xD634, (byte)0, (byte)1, (byte)0x0F, (byte)3, (ushort)0), .. Sprm(0xD634, (byte)0, (byte)1, (byte)0x0A, (byte)3, (ushort)108)]),
        new("No List", 107, 4, 0xFFF, [], []),
    ];

    public List<string> Fonts { get; } = ["Times New Roman", "Symbol", "Arial", "바탕"];

    /// <summary>The standard fonts (ascii, East Asian, other) of the stylesheet.</summary>
    public (int Ascii, int EastAsia, int Other) StandardFonts { get; set; } = (0, 3, 0);

    /// <summary>SEPX of the last section.</summary>
    public byte[] LastSection { get; set; } = [];

    /// <summary>Text in 8-bit pieces wherever it is Windows-1252 (and Unicode pieces elsewhere), as Word does it.</summary>
    public bool Compressed { get; set; }

    public bool FacingPages { get; set; }

    public string? Title { get; set; }

    public ushort NFib { get; set; } = 0x00C1;

    /// <summary>FibBase flags to add: 0x0100 encrypted, 0x8000 obfuscated, 0x0001 template.</summary>
    public ushort ExtraFlags { get; set; }

    /// <summary>Changes a stream once it is laid out (stream name, bytes → the bytes stored), to damage a file on purpose.</summary>
    public Func<string, byte[], byte[]>? EditStream { get; set; }

    /// <summary>Declared sizes of streams that differ from their lengths.</summary>
    public Dictionary<string, long> DeclaredSizes { get; } = [];

    /// <summary>Where the structures went: FibRgFcLcb index → offset and length in the table stream.</summary>
    public Dictionary<int, (int Fc, int Lcb)> Locations { get; } = [];

    // ───────────────────────── Sprms ─────────────────────────

    /// <summary>A sprm: the operation and its operand; a variable operand (spra 6) gets its length byte.</summary>
    public static byte[] Sprm(ushort op, params object[] operand)
    {
        var bytes = HwpBuilder.Bytes(operand);
        var output = new List<byte> { (byte)op, (byte)(op >> 8) };
        if (op >> 13 == 6)
        {
            if (op == 0xD608)
                output.AddRange(BitConverter.GetBytes((ushort)(bytes.Length + 1)));
            else
                output.Add((byte)bytes.Length);
        }
        output.AddRange(bytes);
        return [.. output];
    }

    /// <summary>sprmTDefTable for cells between <paramref name="centers"/>, each TC80 with the given flags and no borders.</summary>
    public static byte[] DefTable(short[] centers, ushort[]? flags = null, uint[]? borders = null)
    {
        var data = new List<byte> { (byte)(centers.Length - 1) };
        foreach (var center in centers)
            data.AddRange(BitConverter.GetBytes(center));
        for (var i = 0; i < centers.Length - 1; i++)
        {
            data.AddRange(BitConverter.GetBytes(flags?[i] ?? (ushort)0));
            data.AddRange(BitConverter.GetBytes((ushort)(centers[i + 1] - centers[i])));
            for (var side = 0; side < 4; side++)
                data.AddRange(BitConverter.GetBytes(borders?[i] ?? 0u));
        }
        return Sprm(0xD608, data.ToArray());
    }

    /// <summary>A Brc80: width in eighths of a point, type, ico, space.</summary>
    public static uint Brc80(byte width, byte type, byte ico = 1) => (uint)(width | type << 8 | ico << 16);

    // ───────────────────────── Text ─────────────────────────

    public static Run R(string text, params byte[][] sprms) => new(text, sprms.SelectMany(s => s).ToArray());

    public DocBuilder Paragraph(params Run[] runs) => Paragraph(0, [], runs);

    public DocBuilder Paragraph(int istd, byte[] papx, params Run[] runs) => Add(_main, istd, papx, '\r', runs);

    /// <summary>A paragraph ending with <paramref name="mark"/> (13, 7 for a cell or row, 12 for a section end).</summary>
    private DocBuilder Add(Story story, int istd, byte[] papx, char mark, Run[] runs, byte[]? markChpx = null)
    {
        foreach (var run in runs)
        {
            var start = story.Length;
            story.Text.Append(run.Text);
            if (run.Text.Length > 0)
                story.Runs.Add((start, story.Length, run.Chpx ?? []));
        }
        story.Paragraphs.Add((story.Length, istd, papx));
        story.Runs.Add((story.Length, story.Length + 1, markChpx ?? []));
        story.Text.Append(mark);
        return this;
    }

    /// <summary>Ends the section with a paragraph holding <paramref name="runs"/> and the section mark.</summary>
    public DocBuilder EndSection(byte[] sepx, params Run[] runs)
    {
        Add(_main, 0, [], '\f', runs);
        _sections.Add((_main.Length, sepx));
        return this;
    }

    /// <summary>A table row: each cell one paragraph (sprmPFInTable, a cell mark), then the row end with <paramref name="tap"/>.</summary>
    public DocBuilder Row(byte[] tap, params Run[][] cells)
    {
        foreach (var cell in cells)
            Add(_main, 0, Sprm(0x2416, (byte)1), '\a', cell);
        Add(_main, 0, [.. Sprm(0x2416, (byte)1), .. Sprm(0x2417, (byte)1), .. tap], '\a', []);
        return this;
    }

    /// <summary>A row of a table nested at <paramref name="depth"/> (sprmPItap, inner cell and row ends with paragraph marks).</summary>
    public DocBuilder NestedRow(int depth, byte[] tap, params Run[][] cells)
    {
        byte[] Depth() => [.. Sprm(0x2416, (byte)1), .. Sprm(0x6649, depth)];
        foreach (var cell in cells)
            Add(_main, 0, [.. Depth(), .. Sprm(0x244B, (byte)1)], '\r', cell);
        Add(_main, 0, [.. Depth(), .. Sprm(0x244C, (byte)1), .. tap], '\r', []);
        return this;
    }

    /// <summary>Paragraphs of a header or footer story of section <paramref name="section"/>: 0 even header, 1 odd header,
    /// 2 even footer, 3 odd footer, 4 first header, 5 first footer.</summary>
    public DocBuilder Header(int section, int kind, params Run[] runs)
    {
        _headerStories[(section, kind)] = runs;
        return this;
    }

    /// <summary>A run with a footnote (or endnote) reference, an auto-numbered special character 2, whose note says <paramref name="text"/>.</summary>
    public Run Footnote(string text, bool endnote = false)
    {
        var story = endnote ? _endnotes : _footnotes;
        var start = story.Length;
        Add(story, 0, [], '\r', [new Run("\u0002", Sprm(0x0855, (byte)1)), new Run(" " + text)]);
        // The run's sprms tell the reference apart when the main text is laid out.
        var chpx = Sprm(0x0855, (byte)1);
        (endnote ? _endnoteRefs : _footnoteRefs).Add((chpx, start));
        return new Run("\u0002", chpx);
    }

    /// <summary>A bookmark around the next <paramref name="length"/> characters of the main text.</summary>
    public DocBuilder Bookmark(string name, int length)
    {
        _bookmarks.Add((name, _main.Length, _main.Length + length));
        return this;
    }

    /// <summary>A run with an inline PNG picture (PICF, OfficeArt shape and BLIP in the Data stream).</summary>
    public Run Picture(byte[] png, short widthTwips, short heightTwips)
    {
        var at = (int)_data.Position;
        var blip = Blip(png);
        var fbse = Fbse(blip);
        var shape = Record(0xF, 0, 0xF004, [.. Record(2, 75, 0xF00A, HwpBuilder.Bytes(1025, 0xA00)), .. Record(3, 1, 0xF00B, HwpBuilder.Bytes((ushort)0x4104, 1))]);
        var picf = new byte[68];
        BinaryPrimitives.WriteInt32LittleEndian(picf, 68 + shape.Length + fbse.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(picf.AsSpan(4), 68);
        BinaryPrimitives.WriteInt16LittleEndian(picf.AsSpan(6), 0x64);
        BinaryPrimitives.WriteInt16LittleEndian(picf.AsSpan(28), widthTwips);
        BinaryPrimitives.WriteInt16LittleEndian(picf.AsSpan(30), heightTwips);
        BinaryPrimitives.WriteUInt16LittleEndian(picf.AsSpan(32), 1000);
        BinaryPrimitives.WriteUInt16LittleEndian(picf.AsSpan(34), 1000);
        _data.Write(picf);
        _data.Write(shape);
        _data.Write(fbse);
        return new Run("\u0001", [.. Sprm(0x0855, (byte)1), .. Sprm(0x6A03, at)]);
    }

    /// <summary>A run anchoring a floating text box (shape id <paramref name="spid"/>) at a page position, with its text.</summary>
    public Run TextBox(int spid, string text, int left = 1440, int top = 1440, int width = 2880, int height = 1440)
    {
        _textBoxStories.Add((_textBoxes.Length, spid));
        Add(_textBoxes, 0, [], '\r', [new Run(text)]);
        Add(_textBoxes, 0, [], '\r', []); // the story's own end, as Word writes it
        var chpx = Sprm(0x0855, (byte)1);
        _anchors.Add((chpx, spid, left, top, left + width, top + height, (ushort)(1 << 1 | 1 << 3 | 2 << 5)));
        // Fill colour (pid 385) light blue, line width 12700 EMU.
        _shapes.Add((spid, 202, [.. HwpBuilder.Bytes((ushort)0x0181, 0x00FFEEDD), .. HwpBuilder.Bytes((ushort)0x01CB, 12700)], _textBoxStories.Count, null));
        return new Run("\u0008", chpx);
    }

    /// <summary>A run anchoring a floating PNG picture (shape id <paramref name="spid"/>) relative to the paragraph.</summary>
    public Run FloatingPicture(int spid, byte[] png, int left, int top, int width, int height)
    {
        var chpx = Sprm(0x0855, (byte)1);
        _anchors.Add((chpx, spid, left, top, left + width, top + height, (ushort)(2 << 1 | 2 << 3 | 1 << 5)));
        _shapes.Add((spid, 75, [], null, png));
        return new Run("\u0008", chpx);
    }

    /// <summary>A list definition (9 levels unless <paramref name="simple"/>); returns its 1-based LFO for sprmPIlfo.</summary>
    public int List(int lsid, Level[] levels, bool simple = false)
    {
        _lists.Add((lsid, levels, simple));
        return Instance(lsid);
    }

    /// <summary>Another instance (LFO) of list <paramref name="lsid"/>, with start overrides; returns its 1-based number.</summary>
    public int Instance(int lsid, params (int Level, int Start)[] overrides)
    {
        _lfos.Add((lsid, overrides));
        return _lfos.Count;
    }

    // ───────────────────────── Building ─────────────────────────

    public byte[] Build()
    {
        // The CP space: main text, footnotes, headers, endnotes, text boxes.
        BuildHeaders();
        var stories = new[] { _main, _footnotes, _headers, _endnotes, _textBoxes };
        var offsets = new int[stories.Length];
        var text = new StringBuilder();
        var runs = new List<(int Start, int End, byte[] Chpx)>();
        var paragraphs = new List<(int Mark, int Istd, byte[] Papx)>();
        for (var s = 0; s < stories.Length; s++)
        {
            offsets[s] = text.Length;
            text.Append(stories[s].Text);
            runs.AddRange(stories[s].Runs.Select(r => (r.Start + offsets[s], r.End + offsets[s], r.Chpx)));
            paragraphs.AddRange(stories[s].Paragraphs.Select(p => (p.Mark + offsets[s], p.Istd, p.Papx)));
        }
        var all = text.ToString();

        // Pieces: contiguous in the stream from TextStart, 8-bit where every character is Windows-1252 when Compressed.
        var cp1252 = CodePagesEncoding();
        var pieces = new List<(int Cp, int End, int Fc, bool Compressed)>();
        var word = new MemoryStream();
        word.Write(new byte[TextStart]);
        for (var cp = 0; cp < all.Length;)
        {
            var compressed = Compressed && Ansi(all[cp], cp1252);
            var end = cp + 1;
            while (end < all.Length && (Compressed && Ansi(all[end], cp1252)) == compressed)
                end++;
            pieces.Add((cp, end, (int)word.Position, compressed));
            word.Write(compressed ? cp1252.GetBytes(all[cp..end]) : Encoding.Unicode.GetBytes(all[cp..end]));
            cp = end;
        }
        var textEnd = (int)word.Position;
        int Fc(int cp)
        {
            if (cp >= all.Length)
                return textEnd;
            var piece = pieces.First(p => cp >= p.Cp && cp < p.End);
            return piece.Fc + (cp - piece.Cp) * (piece.Compressed ? 1 : 2);
        }
        Pad(word, Page);

        // CHPX FKPs over the runs, PAPX FKPs over the paragraphs (from after the previous mark through the mark).
        var chpxRuns = runs.OrderBy(r => r.Start).Select(r => (Fc(r.Start), Fc(r.End), r.Chpx)).ToList();
        var papxRuns = new List<(int, int, byte[])>();
        var previous = 0;
        foreach (var (mark, istd, papx) in paragraphs.OrderBy(p => p.Mark))
        {
            papxRuns.Add((Fc(previous), Fc(mark + 1), [.. BitConverter.GetBytes((ushort)istd), .. papx]));
            previous = mark + 1;
        }
        var bteChpx = Fkps(word, chpxRuns, paragraphs: false);
        var btePapx = Fkps(word, papxRuns, paragraphs: true);

        // Section properties.
        var sections = _sections.Append((End: _main.Length, Sepx: LastSection)).ToList();
        var seds = new List<int>();
        foreach (var (_, sepx) in sections)
        {
            Pad(word, 2);
            seds.Add((int)word.Position);
            word.Write(BitConverter.GetBytes((short)sepx.Length));
            word.Write(sepx);
        }

        var table = new MemoryStream();
        var blob = new (int Fc, int Lcb)[93];
        void Put(int index, byte[] bytes)
        {
            if (bytes.Length == 0)
                return;
            Pad(table, 2);
            blob[index] = ((int)table.Position, bytes.Length);
            table.Write(bytes);
        }
        Put(1, Stylesheet());
        Put(2, NoteReferences(_footnoteRefs));
        Put(3, NoteTexts(_footnoteRefs, _footnotes.Length));
        Put(46, NoteReferences(_endnoteRefs));
        Put(47, NoteTexts(_endnoteRefs, _endnotes.Length));
        Put(6, Plex([0, .. _sections.Select(s => s.End), _main.Length + 0], seds.Select(fc => HwpBuilder.Bytes((short)0, fc, (short)0, -1)).ToList(), sections.Count));
        Put(11, HeaderPlex);
        Put(12, bteChpx);
        Put(13, btePapx);
        Put(15, FontTable());
        Put(21, Bookmarks());
        Put(22, Plex([.. _bookmarks.Select(b => b.Start), _main.Length], _bookmarks.Select((_, i) => HwpBuilder.Bytes((ushort)i, (ushort)0)).ToList(), _bookmarks.Count));
        Put(23, _bookmarks.Count == 0 ? [] : Plex([.. _bookmarks.Select(b => b.End), _main.Length], [], _bookmarks.Count));
        var dop = new byte[500];
        dop[0] = (byte)(FacingPages ? 1 : 0);
        Put(31, dop);
        Put(33, PieceTable(pieces));
        Put(40, Plex([.. _anchors.Select(a => _main.Runs.First(r => ReferenceEquals(r.Chpx, a.Chpx)).Start), _main.Length], _anchors.Select(a => HwpBuilder.Bytes(a.Spid, a.Left, a.Top, a.Right, a.Bottom, a.Flags, 0)).ToList(), _anchors.Count));
        Put(50, Drawings());
        // Field plexes of each story (Word writes them; LibreOffice needs them to see fields, Filee reads the characters).
        Put(16, Fields(_main));
        Put(17, Fields(_headers));
        Put(18, Fields(_footnotes));
        Put(48, Fields(_endnotes));
        Put(57, Fields(_textBoxes));
        Put(56, Plex([.. _textBoxStories.Select(t => t.Start), _textBoxes.Length], _textBoxStories.Select(t => HwpBuilder.Bytes(1, 0, (short)0, 0, t.Spid, 0)).ToList(), _textBoxStories.Count));
        if (_lists.Count > 0)
        {
            var (lists, levels) = ListTables();
            Put(73, lists);
            table.Write(levels); // the LVLs follow the PlfLst, outside its length
            Put(74, Lfos());
        }

        var fib = Fib(blob, textEnd, pieces.Count > 1);
        var wordBytes = word.ToArray();
        fib.CopyTo(wordBytes, 0);
        for (var i = 0; i < blob.Length; i++)
        {
            if (blob[i].Lcb > 0)
                Locations[i] = blob[i];
        }
        var streams = new List<(string Name, byte[] Data)> { ("WordDocument", wordBytes), ("1Table", table.ToArray()) };
        if (_data.Length > 4)
            streams.Add(("Data", _data.ToArray()));
        if (Title is not null)
            streams.Add(("\u0005SummaryInformation", Summary(Title)));
        if (EditStream is { } edit)
            streams = [.. streams.Select(s => (s.Name, edit(s.Name, s.Data)))];
        // Laid out as the specification asks, so LibreOffice opens it too (DocReaderTests check that when it is installed).
        return HwpBuilder.Cfb(streams, declaredSizes: DeclaredSizes, standard: DeclaredSizes.Count == 0);
    }

    private static Encoding CodePagesEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static bool Ansi(char c, Encoding cp1252)
    {
        try
        {
            return cp1252.GetBytes([c]).Length == 1;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private byte[] Fib((int Fc, int Lcb)[] blob, int textEnd, bool complex)
    {
        var fib = new byte[900];
        BinaryPrimitives.WriteUInt16LittleEndian(fib, 0xA5EC);
        BinaryPrimitives.WriteUInt16LittleEndian(fib.AsSpan(2), NFib);
        BinaryPrimitives.WriteUInt16LittleEndian(fib.AsSpan(6), 0x0409);
        BinaryPrimitives.WriteUInt16LittleEndian(fib.AsSpan(10), (ushort)(0x0200 | (complex ? 0x0004 : 0) | ExtraFlags));
        BinaryPrimitives.WriteUInt16LittleEndian(fib.AsSpan(12), 0x00BF);
        BinaryPrimitives.WriteUInt16LittleEndian(fib.AsSpan(32), 14);
        BinaryPrimitives.WriteUInt16LittleEndian(fib.AsSpan(62), 22);
        var lw = 64;
        BinaryPrimitives.WriteInt32LittleEndian(fib.AsSpan(lw), textEnd);
        BinaryPrimitives.WriteInt32LittleEndian(fib.AsSpan(lw + 12), _main.Length);
        BinaryPrimitives.WriteInt32LittleEndian(fib.AsSpan(lw + 16), _footnotes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(fib.AsSpan(lw + 20), _headers.Length);
        BinaryPrimitives.WriteInt32LittleEndian(fib.AsSpan(lw + 32), _endnotes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(fib.AsSpan(lw + 36), _textBoxes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(fib.AsSpan(152), 0x5D);
        for (var i = 0; i < blob.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(fib.AsSpan(154 + i * 8), blob[i].Fc);
            BinaryPrimitives.WriteInt32LittleEndian(fib.AsSpan(158 + i * 8), blob[i].Lcb);
        }
        return fib;
    }

    /// <summary>A PLC: the CPs, then the data elements.</summary>
    private static byte[] Plex(IReadOnlyList<int> cps, IReadOnlyList<byte[]> data, int count)
    {
        if (count == 0)
            return [];
        var output = new List<byte>();
        foreach (var cp in cps)
            output.AddRange(BitConverter.GetBytes(cp));
        foreach (var element in data)
            output.AddRange(element);
        return [.. output];
    }

    private static byte[] PieceTable(List<(int Cp, int End, int Fc, bool Compressed)> pieces)
    {
        var plc = new List<byte>();
        foreach (var piece in pieces)
            plc.AddRange(BitConverter.GetBytes(piece.Cp));
        plc.AddRange(BitConverter.GetBytes(pieces.Count > 0 ? pieces[^1].End : 0));
        foreach (var piece in pieces)
        {
            plc.AddRange(BitConverter.GetBytes((ushort)0));
            plc.AddRange(BitConverter.GetBytes(piece.Compressed ? (uint)(piece.Fc * 2) | 0x40000000 : (uint)piece.Fc));
            plc.AddRange(BitConverter.GetBytes((ushort)0));
        }
        return [0x02, .. BitConverter.GetBytes(plc.Count), .. plc];
    }

    /// <summary>
    /// FKPs for <paramref name="runs"/>, written to the WordDocument stream at page boundaries; returns the PlcBte (the
    /// first FC of each page, the end, then each page's number).
    /// </summary>
    private static byte[] Fkps(MemoryStream word, List<(int Fc, int End, byte[] Grpprl)> runs, bool paragraphs)
    {
        var pages = new List<(int Fc, int Pn)>();
        var index = 0;
        while (index < runs.Count)
        {
            var page = new byte[Page];
            var count = 0;
            var top = Page - 1;
            var offsets = new List<int>();
            while (index + count < runs.Count && count < 100)
            {
                var grpprl = runs[index + count].Grpprl;
                var size = paragraphs ? (grpprl.Length % 2 == 1 ? 1 + grpprl.Length : 2 + grpprl.Length) : (grpprl.Length == 0 ? 0 : 1 + grpprl.Length);
                var newTop = size == 0 ? top : (top - size) & ~1;
                var header = (count + 2) * 4 + (count + 1) * (paragraphs ? 13 : 1);
                if (newTop < header)
                {
                    if (count == 0)
                        throw new InvalidOperationException("A grpprl doesn't fit an FKP page.");
                    break;
                }
                offsets.Add(size == 0 ? 0 : newTop);
                if (size > 0)
                {
                    if (paragraphs && grpprl.Length % 2 == 1)
                    {
                        page[newTop] = (byte)((grpprl.Length + 1) / 2);
                        grpprl.CopyTo(page, newTop + 1);
                    }
                    else if (paragraphs)
                    {
                        page[newTop] = 0;
                        page[newTop + 1] = (byte)(grpprl.Length / 2);
                        grpprl.CopyTo(page, newTop + 2);
                    }
                    else
                    {
                        page[newTop] = (byte)grpprl.Length;
                        grpprl.CopyTo(page, newTop + 1);
                    }
                }
                top = newTop;
                count++;
            }
            for (var i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(i * 4), runs[index + i].Fc);
                page[(count + 1) * 4 + i * (paragraphs ? 13 : 1)] = (byte)(offsets[i] / 2);
            }
            BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(count * 4), runs[index + count - 1].End);
            page[Page - 1] = (byte)count;
            pages.Add((runs[index].Fc, (int)(word.Position / Page)));
            word.Write(page);
            index += count;
        }
        var plc = new List<byte>();
        foreach (var (fc, _) in pages)
            plc.AddRange(BitConverter.GetBytes(fc));
        plc.AddRange(BitConverter.GetBytes(runs.Count > 0 ? runs[^1].End : 0));
        foreach (var (_, pn) in pages)
            plc.AddRange(BitConverter.GetBytes(pn));
        return [.. plc];
    }

    private byte[] Stylesheet()
    {
        var output = new List<byte>();
        output.AddRange(BitConverter.GetBytes((ushort)18));
        output.AddRange(HwpBuilder.Bytes((ushort)Styles.Count, (ushort)10, (ushort)1, (ushort)Styles.Count, (ushort)15, (ushort)0,
            (ushort)StandardFonts.Ascii, (ushort)StandardFonts.EastAsia, (ushort)StandardFonts.Other));
        foreach (var style in Styles)
        {
            if (style is null)
            {
                output.AddRange(BitConverter.GetBytes((ushort)0));
                continue;
            }
            var std = new List<byte>();
            var upxs = style.Kind switch
            {
                1 => new[] { (byte[])[0, 0, .. style.Papx], style.Chpx },
                2 => [style.Chpx],
                3 => [style.Tapx ?? [], [0, 0, .. style.Papx], style.Chpx],
                _ => [(byte[])[0, 0, .. style.Papx]],
            };
            std.AddRange(HwpBuilder.Bytes((ushort)style.Sti, (ushort)(style.Kind | style.Base << 4), (ushort)(upxs.Length | 0 << 4), (ushort)0, (ushort)0));
            std.AddRange(BitConverter.GetBytes((ushort)style.Name.Length));
            std.AddRange(Encoding.Unicode.GetBytes(style.Name));
            std.AddRange([0, 0]);
            foreach (var upx in upxs)
            {
                std.AddRange(BitConverter.GetBytes((ushort)upx.Length));
                std.AddRange(upx);
                if (upx.Length % 2 == 1)
                    std.Add(0);
            }
            output.AddRange(BitConverter.GetBytes((ushort)std.Count));
            output.AddRange(std);
        }
        return [.. output];
    }

    private byte[] FontTable()
    {
        var output = new List<byte>();
        output.AddRange(HwpBuilder.Bytes((ushort)Fonts.Count, (ushort)0));
        foreach (var font in Fonts)
        {
            var ffn = new List<byte> { 0x26 }; // prq, TrueType, family
            ffn.AddRange(BitConverter.GetBytes((short)400));
            ffn.AddRange([0, 0]); // charset, alternate name index
            ffn.AddRange(new byte[10 + 24]);
            ffn.AddRange(Encoding.Unicode.GetBytes(font + "\0"));
            output.Add((byte)ffn.Count);
            output.AddRange(ffn);
        }
        return [.. output];
    }

    private byte[] Bookmarks()
    {
        if (_bookmarks.Count == 0)
            return [];
        var output = new List<byte>();
        output.AddRange(HwpBuilder.Bytes((ushort)0xFFFF, (ushort)_bookmarks.Count, (ushort)0));
        foreach (var (name, _, _) in _bookmarks)
        {
            output.AddRange(BitConverter.GetBytes((ushort)name.Length));
            output.AddRange(Encoding.Unicode.GetBytes(name));
        }
        return [.. output];
    }

    private byte[] NoteReferences(List<(byte[] Chpx, int Start)> notes)
    {
        if (notes.Count == 0)
            return [];
        // Where the reference runs went in the main text (their sprms are the run's own array).
        var refs = notes.Select(n => _main.Runs.First(r => ReferenceEquals(r.Chpx, n.Chpx)).Start).ToList();
        return Plex([.. refs, _main.Length], refs.Select(_ => BitConverter.GetBytes((short)1)).ToList(), notes.Count);
    }

    private static byte[] NoteTexts(List<(byte[] Chpx, int Start)> notes, int length) =>
        notes.Count == 0 ? [] : Plex([.. notes.Select(n => n.Start), length, length], [], notes.Count);

    /// <summary>
    /// A PlcFld: a CP (in the story) per field character with an FLD: the character (19 begin, 20 separator, 21 end),
    /// then the field type (flt) at the begin and the flags at the end (fHasSep).
    /// </summary>
    private static byte[] Fields(Story story)
    {
        var text = story.Text.ToString();
        var cps = new List<int>();
        var flds = new List<byte[]>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\u0013' or '\u0014' or '\u0015'))
                continue;
            byte second = 0;
            if (text[i] == '\u0013')
            {
                var instruction = text[(i + 1)..].TrimStart();
                second = instruction.StartsWith("HYPERLINK", StringComparison.Ordinal) ? (byte)88
                    : instruction.StartsWith("NUMPAGES", StringComparison.Ordinal) ? (byte)26
                    : instruction.StartsWith("PAGE", StringComparison.Ordinal) ? (byte)33 : (byte)0;
            }
            else if (text[i] == '\u0015')
            {
                second = 0x80;
            }
            cps.Add(i);
            flds.Add([(byte)text[i], second]);
        }
        return Plex([.. cps, text.Length], flds, cps.Count);
    }

    // ───────────────────────── Headers ─────────────────────────

    private byte[] HeaderPlex { get; set; } = [];

    /// <summary>PlcfHdd: six separator stories (empty), then six stories per section.</summary>
    private void BuildHeaders()
    {
        if (_headerStories.Count == 0)
            return;
        var sections = _sections.Count + 1;
        var cps = new List<int>();
        for (var i = 0; i < 6; i++)
            cps.Add(0);
        for (var section = 0; section < sections; section++)
        {
            for (var kind = 0; kind < 6; kind++)
            {
                cps.Add(_headers.Length);
                if (_headerStories.TryGetValue((section, kind), out var runs))
                {
                    Add(_headers, 0, [], '\r', runs);
                    Add(_headers, 0, [], '\r', []); // the story's own end, as Word writes it
                }
            }
        }
        cps.Add(_headers.Length);
        Add(_headers, 0, [], '\r', []); // the guard paragraph
        HeaderPlex = [.. cps.SelectMany(BitConverter.GetBytes)];
    }

    // ───────────────────────── Lists ─────────────────────────

    private (byte[] Lists, byte[] Levels) ListTables()
    {
        var lists = new List<byte>(BitConverter.GetBytes((short)_lists.Count));
        var levels = new List<byte>();
        foreach (var (lsid, definition, simple) in _lists)
        {
            lists.AddRange(BitConverter.GetBytes(lsid));
            lists.AddRange(BitConverter.GetBytes(0));
            for (var i = 0; i < 9; i++)
                lists.AddRange(BitConverter.GetBytes((ushort)0x0FFF));
            lists.Add((byte)(simple ? 1 : 0));
            lists.Add(0);
            for (var i = 0; i < (simple ? 1 : 9); i++)
            {
                var level = i < definition.Length ? definition[i] : new Level(0, ((char)i) + ".");
                var lvlf = new byte[28];
                BinaryPrimitives.WriteInt32LittleEndian(lvlf, level.Start);
                lvlf[4] = (byte)level.Nfc;
                // rgbxchNums: where the level numbers are in the number text (1-based), as Word writes them.
                var placeholders = level.Text.Select((c, index) => (c, index)).Where(x => x.c < 9).Take(9).ToList();
                for (var k = 0; k < placeholders.Count; k++)
                    lvlf[6 + k] = (byte)(placeholders[k].index + 1);
                lvlf[15] = 0; // a tab after the number
                lvlf[24] = (byte)(level.Chpx?.Length ?? 0);
                lvlf[25] = (byte)(level.Papx?.Length ?? 0);
                levels.AddRange(lvlf);
                levels.AddRange(level.Papx ?? []);
                levels.AddRange(level.Chpx ?? []);
                levels.AddRange(BitConverter.GetBytes((ushort)level.Text.Length));
                levels.AddRange(Encoding.Unicode.GetBytes(level.Text));
            }
        }
        return ([.. lists], [.. levels]);
    }

    private byte[] Lfos()
    {
        var output = new List<byte>(BitConverter.GetBytes(_lfos.Count));
        foreach (var (lsid, overrides) in _lfos)
            output.AddRange([.. BitConverter.GetBytes(lsid), .. new byte[8], (byte)overrides.Length, 0, 0, 0]);
        foreach (var (_, overrides) in _lfos)
        {
            if (overrides.Length == 0)
                continue;
            output.AddRange(BitConverter.GetBytes(-1));
            foreach (var (level, start) in overrides)
            {
                output.AddRange(BitConverter.GetBytes(start));
                output.AddRange(BitConverter.GetBytes((uint)(level | 0x10)));
            }
        }
        return [.. output];
    }

    // ───────────────────────── OfficeArt ─────────────────────────

    private static byte[] Record(int version, int instance, int type, byte[] data) =>
        [.. BitConverter.GetBytes((ushort)(version | instance << 4)), .. BitConverter.GetBytes((ushort)type), .. BitConverter.GetBytes(data.Length), .. data];

    /// <summary>A PNG BLIP: one UID, the tag byte, the picture.</summary>
    private static byte[] Blip(byte[] png) => Record(0, 0x6E0, 0xF01E, [.. new byte[16], 0xFF, .. png]);

    private static byte[] Fbse(byte[] blip) =>
        Record(2, 6, 0xF007, [6, 6, .. new byte[16], 0xFF, 0, .. BitConverter.GetBytes(blip.Length), .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(0), 0, 0, 0, 0, .. blip]);

    /// <summary>OfficeArtContent: the drawing group with the BStore of the floating pictures, then the main text's drawing.</summary>
    private byte[] Drawings()
    {
        if (_shapes.Count == 0)
            return [];
        var bstore = new List<byte>();
        var pibs = new Dictionary<int, int>();
        foreach (var shape in _shapes.Where(s => s.Png is not null))
        {
            bstore.AddRange(Fbse(Blip(shape.Png!)));
            pibs[shape.Spid] = pibs.Count + 1;
        }
        var group = Record(0xF, 0, 0xF000, [.. Record(0, 0, 0xF006, new byte[16]), .. (bstore.Count > 0 ? Record(0xF, pibs.Count, 0xF001, [.. bstore]) : [])]);
        var shapes = new List<byte>(Record(0xF, 0, 0xF004, Record(2, 0, 0xF00A, HwpBuilder.Bytes(1024, 0x5))));
        foreach (var (spid, type, properties, story, _) in _shapes)
        {
            var options = new List<byte>(properties);
            var count = properties.Length / 6;
            if (pibs.TryGetValue(spid, out var pib))
            {
                options.AddRange(HwpBuilder.Bytes((ushort)0x4104, pib));
                count++;
            }
            var children = new List<byte>(Record(2, type, 0xF00A, HwpBuilder.Bytes(spid, 0xA00)));
            if (count > 0)
                children.AddRange(Record(3, count, 0xF00B, [.. options]));
            children.AddRange(Record(0, 0, 0xF010, new byte[4]));
            children.AddRange(Record(0, 0, 0xF011, new byte[4]));
            if (story is { } index)
                children.AddRange(Record(0, 0, 0xF00D, BitConverter.GetBytes(index << 16)));
            shapes.AddRange(Record(0xF, 0, 0xF004, [.. children]));
        }
        var drawing = Record(0xF, 0, 0xF002, [.. Record(0, 0, 0xF008, new byte[8]), .. Record(0xF, 0, 0xF003, [.. shapes])]);
        return [.. group, 0, .. drawing];
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static byte[] Summary(string title)
    {
        var text = Encoding.Unicode.GetBytes(title + "\0");
        var property = (byte[])[.. BitConverter.GetBytes(0x1F), .. BitConverter.GetBytes(text.Length / 2), .. text];
        var section = (byte[])[.. BitConverter.GetBytes(16 + property.Length), .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(2), .. BitConverter.GetBytes(16), .. property];
        var header = new byte[48];
        BinaryPrimitives.WriteUInt16LittleEndian(header, 0xFFFE);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), 48);
        return [.. header, .. section];
    }

    private static void Pad(MemoryStream stream, int alignment)
    {
        while (stream.Position % alignment != 0)
            stream.WriteByte(0);
    }
}
