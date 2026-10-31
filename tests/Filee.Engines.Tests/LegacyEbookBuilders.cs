// Generates the older e-book formats in code, from their format descriptions: CHM (ITSF directory, an LZX section
// written by LzxEncoder, #SYSTEM and a contents file), PDB (PalmDOC and zTXT), PML, TCR, RB, SNB and OEB (loose
// and zipped). Every book holds the same short story so the tests can look for the same text in each.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ImageMagick;

namespace Filee.Engines.Tests;

internal static class LegacyEbookBuilders
{
    public const string Title = "The Lighthouse Keeper";
    public const string Author = "Ada Lovelace";
    public const string Heading1 = "Chapter One";
    public const string Heading2 = "Chapter Two";
    public const string Line1 = "The lamp was lit at dusk every evening.";
    public const string Line2 = "Ships passed safely through the narrow strait.";
    public const string Accented = "Café, naïve, Ünïcödé";

    private static byte[] Png(MagickColor color) => EbookBuilders.Png(48, 32, color);

    /// <summary>Text long enough for several LZX frames (some 40 KB per chapter).</summary>
    private static string Filler(int chapter) =>
        string.Concat(Enumerable.Range(1, 600).Select(i => $"<p>Line {i} of chapter {chapter}: the keeper climbed {i % 17} steps and trimmed the wick.</p>\n"));

    // ───────────────────────── CHM ─────────────────────────

    public sealed record ChmOptions(int Lcid = 0x409, int ResetFrames = 2, int? E8FileSize = null, bool Contents = true, int WindowBits = 16);

    /// <summary>
    /// A CHM: an intro page (the default topic), two chapters with a picture and a link between them, a page the
    /// contents file leaves out, the contents file (intro → chapter 1 → chapter 2), #SYSTEM and the LZX section.
    /// </summary>
    public static byte[] Chm(ChmOptions? options = null, Encoding? pageEncoding = null, string? accented = null)
    {
        options ??= new ChmOptions();
        pageEncoding ??= Encoding.UTF8;
        accented ??= Accented;
        var compressed = new List<(string Name, byte[] Data)>
        {
            ("/toc.hhc", Encoding.ASCII.GetBytes("""
                <HTML><BODY><UL>
                <LI><OBJECT type="text/sitemap"><param name="Name" value="Intro"><param name="Local" value="html/intro.htm"></OBJECT>
                <UL><LI><OBJECT type="text/sitemap"><param name="Name" value="One"><param name="Local" value="html/ch1.htm#top"></OBJECT>
                <LI><OBJECT type="text/sitemap"><param value="html\ch2.htm" name="Local"><param name="Name" value="Two"></OBJECT></UL>
                </UL></BODY></HTML>
                """)),
            ("/html/intro.htm", pageEncoding.GetBytes($"<html><head><title>Intro</title></head><body><p>{accented}</p></body></html>")),
            ("/html/ch1.htm", pageEncoding.GetBytes($"<html><body><h1 id=\"top\">{Heading1}</h1><p>{Line1}</p><p><img src=\"../images/pic.png\"></p><p>See <a href=\"ch2.htm#end\">the end</a>.</p>\n{Filler(1)}</body></html>")),
            ("/html/ch2.htm", pageEncoding.GetBytes($"<html><body><h1>{Heading2}</h1><p>{Line2}</p>\n{Filler(2)}<p id=\"end\">The end.</p></body></html>")),
            ("/html/aux.htm", "<html><body><p>Not in the contents.</p></body></html>"u8.ToArray()),
            ("/images/pic.png", Png(MagickColors.SeaGreen)),
        };
        var section1 = compressed.SelectMany(f => f.Data).ToArray();
        var (lzx, frames) = LzxEncoder.Compress(section1, new LzxEncoder.Options(options.WindowBits, options.ResetFrames, E8FileSize: options.E8FileSize));

        var system = new MemoryStream();
        system.Write(Le32(3));
        var ansi = AnsiEncoding(options.Lcid);
        if (options.Contents)
            SystemRecord(system, 0, Encoding.ASCII.GetBytes("toc.hhc\0"));
        SystemRecord(system, 2, Encoding.ASCII.GetBytes("html/intro.htm\0"));
        SystemRecord(system, 3, [.. ansi.GetBytes(Title), 0]);
        SystemRecord(system, 4, [.. Le32(options.Lcid), .. new byte[24]]);

        var control = new MemoryStream();
        control.Write(Le32(6));
        control.Write("LZXC"u8);
        control.Write(Le32(2));
        control.Write(Le32(Math.Max(1, options.ResetFrames == 0 ? 1 << 15 : options.ResetFrames)));
        control.Write(Le32((1 << options.WindowBits) / LzxEncoder.FrameSize));
        control.Write(Le32((1 << options.WindowBits) / LzxEncoder.FrameSize));
        control.Write(Le32(0));

        var reset = new MemoryStream();
        reset.Write(Le32(2));
        reset.Write(Le32(frames.Count));
        reset.Write(Le32(8));
        reset.Write(Le32(0x28));
        reset.Write(Le64(section1.Length));
        reset.Write(Le64(lzx.Length));
        reset.Write(Le64(LzxEncoder.FrameSize));
        foreach (var frame in frames)
            reset.Write(Le64(frame));

        var stored = new List<(string Name, byte[] Data)>
        {
            ("/#SYSTEM", system.ToArray()),
            ("::DataSpace/Storage/MSCompressed/Content", lzx),
            ("::DataSpace/Storage/MSCompressed/ControlData", control.ToArray()),
            ("::DataSpace/Storage/MSCompressed/Transform/{7FC28940-9D31-11D0-9B27-00A0C91E9C7C}/InstanceData/ResetTable", reset.ToArray()),
        };
        var entries = new List<(string Name, int Section, long Offset, long Length)>();
        long offset = 0;
        foreach (var (name, data) in stored)
        {
            entries.Add((name, 0, offset, data.Length));
            offset += data.Length;
        }
        offset = 0;
        foreach (var (name, data) in compressed)
        {
            entries.Add((name, 1, offset, data.Length));
            offset += data.Length;
        }
        entries.Add(("/html/", 0, 0, 0)); // a folder entry
        return ChmContainer(entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(), stored.SelectMany(f => f.Data).ToArray(), options.Lcid);
    }

    public static Encoding AnsiEncoding(int lcid)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(System.Globalization.CultureInfo.GetCultureInfo(lcid).TextInfo.ANSICodePage);
    }

    private static void SystemRecord(Stream stream, int code, byte[] data)
    {
        stream.Write(Le16(code));
        stream.Write(Le16(data.Length));
        stream.Write(data);
    }

    /// <summary>The ITSF container: header, header section 0, the ITSP directory with PMGL chunks, then the content.</summary>
    public static byte[] ChmContainer(List<(string Name, int Section, long Offset, long Length)> entries, byte[] content, int lcid, int chunkSize = 4096)
    {
        var chunks = new List<byte[]>();
        var current = new MemoryStream();
        void Close()
        {
            var chunk = new byte[chunkSize];
            "PMGL"u8.CopyTo(chunk);
            var used = 20 + (int)current.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)(chunkSize - used));
            BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(12), chunks.Count - 1);
            BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(16), -1);
            current.ToArray().CopyTo(chunk, 20);
            if (chunks.Count > 0)
                BinaryPrimitives.WriteInt32LittleEndian(chunks[^1].AsSpan(16), chunks.Count);
            chunks.Add(chunk);
            current = new MemoryStream();
        }
        foreach (var (name, section, offset, length) in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(name);
            var entry = new MemoryStream();
            entry.Write(EncInt(bytes.Length));
            entry.Write(bytes);
            entry.Write(EncInt(section));
            entry.Write(EncInt(offset));
            entry.Write(EncInt(length));
            if (20 + current.Length + entry.Length > chunkSize - 16)
                Close();
            entry.WriteTo(current);
        }
        Close();

        var itsp = new byte[0x54];
        "ITSP"u8.CopyTo(itsp);
        BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(8), 0x54);
        BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(12), 0x0A);
        BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(16), chunkSize);
        BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(20), 2);
        BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(24), 1);
        BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(28), -1);
        BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(32), 0);
        BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(36), chunks.Count - 1);
        BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(40), -1);
        BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(44), chunks.Count);
        BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(48), lcid);
        BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(68), 0x54);
        for (var o = 72; o < 84; o += 4)
            BinaryPrimitives.WriteInt32LittleEndian(itsp.AsSpan(o), -1);
        var directory = itsp.Concat(chunks.SelectMany(c => c)).ToArray();

        const int HeaderLength = 0x60;
        const int Section0Length = 0x18;
        var directoryOffset = HeaderLength + Section0Length;
        var contentOffset = directoryOffset + directory.Length;
        var header = new byte[HeaderLength];
        "ITSF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 3);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), HeaderLength);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), lcid);
        new Guid("7C01FD10-7BAA-11D0-9E0C-00A0C922E6EC").ToByteArray().CopyTo(header, 24);
        new Guid("7C01FD11-7BAA-11D0-9E0C-00A0C922E6EC").ToByteArray().CopyTo(header, 40);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x38), HeaderLength);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x40), Section0Length);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x48), directoryOffset);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x50), directory.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x58), contentOffset);
        var section0 = new byte[Section0Length];
        BinaryPrimitives.WriteInt32LittleEndian(section0, 0x1FE);
        BinaryPrimitives.WriteInt64LittleEndian(section0.AsSpan(8), contentOffset + content.Length);
        return [.. header, .. section0, .. directory, .. content];
    }

    private static byte[] EncInt(long value)
    {
        var groups = new List<byte> { (byte)(value & 0x7F) };
        for (value >>= 7; value > 0; value >>= 7)
            groups.Insert(0, (byte)(0x80 | (value & 0x7F)));
        return [.. groups];
    }

    // ───────────────────────── PDB ─────────────────────────

    /// <summary>A PalmDOC (TEXtREAd) book, PalmDOC-compressed in 4 KB records.</summary>
    public static byte[] PalmDoc(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var records = new List<byte[]> { new byte[16] };
        for (var offset = 0; offset < bytes.Length; offset += 4096)
            records.Add(EbookBuilders.PalmDocCompress(bytes[offset..Math.Min(bytes.Length, offset + 4096)]));
        BinaryPrimitives.WriteUInt16BigEndian(records[0], 2);
        BinaryPrimitives.WriteUInt32BigEndian(records[0].AsSpan(4), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(records[0].AsSpan(8), (ushort)(records.Count - 1));
        BinaryPrimitives.WriteUInt16BigEndian(records[0].AsSpan(10), 4096);
        return EbookBuilders.PalmDb(Title, "TEXtREAd", records);
    }

    /// <summary>A zTXT book: one zlib stream cut into 8 KB records.</summary>
    public static byte[] Ztxt(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(bytes);
        var stream = compressed.ToArray();
        var records = new List<byte[]> { new byte[24] };
        for (var offset = 0; offset < stream.Length; offset += 8192)
            records.Add(stream[offset..Math.Min(stream.Length, offset + 8192)]);
        BinaryPrimitives.WriteUInt16BigEndian(records[0], 0x012C);
        BinaryPrimitives.WriteUInt16BigEndian(records[0].AsSpan(2), (ushort)(records.Count - 1));
        BinaryPrimitives.WriteUInt32BigEndian(records[0].AsSpan(4), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(records[0].AsSpan(8), 8192);
        return EbookBuilders.PalmDb(Title, "zTXTGPlm", records);
    }

    public static string StoryText => $"{Heading1}\n\n{Line1}\n\n{Accented}\n\n{Heading2}\n\n{Line2}\n";

    // ───────────────────────── PML ─────────────────────────

    /// <summary>A PML book in Windows-1252 with chapters, styles, a picture, a link, an anchor and a footnote.</summary>
    public static string Pml(string folder)
    {
        var pml = $"""
            \x{Heading1}\x
            \B{Line1}\B Then \iitalic\i and \uunderlined\u words.
            \c\a169 centred line\c
            \m="pic.png"
            Go to \q="#two"the second chapter\q.\Fn="note1"1\Fn
            \T="10%"{Accented}
            \p
            \X1\Q="two"{Heading2}\X1
            {Line2}\Sp2\Sp and \U263A smile\\
            <footnote id="note1">A note at the end.</footnote>

            """;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Path.Combine(folder, "keeper.pml");
        File.WriteAllBytes(path, Encoding.GetEncoding(1252).GetBytes(pml.Replace("\n", "\r\n")));
        var images = Directory.CreateDirectory(Path.Combine(folder, "keeper_img")).FullName;
        File.WriteAllBytes(Path.Combine(images, "pic.png"), Png(MagickColors.Goldenrod));
        return path;
    }

    // ───────────────────────── TCR ─────────────────────────

    /// <summary>TCR: single characters for codes 0-127, frequent words and pairs for 128-255.</summary>
    public static byte[] Tcr(string text)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1252).GetBytes(text);
        var dictionary = new List<byte[]>();
        for (var i = 0; i < 128; i++)
            dictionary.Add([(byte)i]);
        foreach (var word in new[] { "the ", "The ", "e ", "ed ", "s ", " a", "th", "in", "er", "an", "re", "on", "at", "en", "nd", "st" })
            dictionary.Add(Encoding.ASCII.GetBytes(word));
        for (var i = 0x80; dictionary.Count < 256; i++)
            dictionary.Add([(byte)(dictionary.Count)]); // the upper half of Windows-1252 as itself
        var output = new MemoryStream();
        output.Write("!!8-Bit!!"u8);
        foreach (var entry in dictionary)
        {
            output.WriteByte((byte)entry.Length);
            output.Write(entry);
        }
        for (var i = 0; i < bytes.Length;)
        {
            var best = -1;
            for (var code = 0; code < 256; code++)
            {
                var entry = dictionary[code];
                if (entry.Length > 0 && i + entry.Length <= bytes.Length && bytes.AsSpan(i, entry.Length).SequenceEqual(entry)
                    && (best < 0 || entry.Length > dictionary[best].Length))
                    best = code;
            }
            if (best < 0)
                best = bytes[i]; // only bytes 128+ the dictionary lacks: not in this test text
            output.WriteByte((byte)best);
            i += dictionary[best].Length;
        }
        return output.ToArray();
    }

    // ───────────────────────── RB ─────────────────────────

    /// <summary>A Rocket eBook: an info page, a compressed HTML page and a stored picture.</summary>
    public static byte[] RocketBook(bool encrypted = false)
    {
        var html = Encoding.UTF8.GetBytes($"<html><body><h1>{Heading1}</h1><p>{Line1}</p><p><img src=\"pic.png\"></p><p>{Accented}</p><h1>{Heading2}</h1><p>{Line2}</p>{Filler(1)}</body></html>");
        var files = new List<(string Name, byte[] Data, int Flags)>
        {
            ("keeper.info", Encoding.ASCII.GetBytes($"TITLE={Title}\r\nAUTHOR={Author}\r\nBODY=keeper.html\r\n"), 8),
            ("keeper.html", RocketCompress(html), encrypted ? 3 : 1),
            ("pic.png", Png(MagickColors.SlateBlue), 0),
        };
        const int TocOffset = 0x128;
        var data = new MemoryStream();
        var header = new byte[TocOffset];
        new byte[] { 0xB0, 0x0C, 0xB0, 0x0C, 0x02, 0x00, (byte)'N', (byte)'U', (byte)'V', (byte)'O' }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x18), TocOffset);
        var dataOffset = TocOffset + 4 + files.Count * 44;
        var toc = new MemoryStream();
        toc.Write(Le32(files.Count));
        var offset = dataOffset;
        foreach (var (name, bytes, flags) in files)
        {
            var nameBytes = new byte[32];
            Encoding.ASCII.GetBytes(name).CopyTo(nameBytes, 0);
            toc.Write(nameBytes);
            toc.Write(Le32(bytes.Length));
            toc.Write(Le32(offset));
            toc.Write(Le32(flags));
            offset += bytes.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x1C), (uint)offset);
        data.Write(header);
        toc.WriteTo(data);
        foreach (var file in files)
            data.Write(file.Data);
        return data.ToArray();
    }

    private static byte[] RocketCompress(byte[] data)
    {
        var chunks = new List<byte[]>();
        for (var offset = 0; offset < data.Length; offset += 4096)
        {
            using var chunk = new MemoryStream();
            using (var zlib = new ZLibStream(chunk, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(data, offset, Math.Min(4096, data.Length - offset));
            chunks.Add(chunk.ToArray());
        }
        var output = new MemoryStream();
        output.Write(Le32(chunks.Count));
        output.Write(Le32(data.Length));
        foreach (var chunk in chunks)
            output.Write(Le32(chunk.Length));
        foreach (var chunk in chunks)
            output.Write(chunk);
        return output.ToArray();
    }

    // ───────────────────────── SNB ─────────────────────────

    /// <summary>An SNB book: book.snbf, toc.snbf, two chapters and a picture; the text stream is long enough for two blocks.</summary>
    public static byte[] Snb()
    {
        var filler = string.Concat(Enumerable.Range(1, 700).Select(i => $"Line {i}: the keeper trimmed the wick again and again.\n"));
        var text = new List<(string Name, byte[] Data)>
        {
            ("snbf/book.snbf", Encoding.UTF8.GetBytes($"<book-snbf version=\"1.0\"><head><name>{Title}</name><author>{Author}</author><language>EN</language><rights/><publisher>Harbour Press</publisher><generator>test</generator><created/><abstract>A short story.</abstract><cover>cover.png</cover></head></book-snbf>")),
            ("snbf/toc.snbf", Encoding.UTF8.GetBytes("<toc-snbf><head><chapters>2</chapters></head><body><chapter src=\"ch1.snbc\">One</chapter><chapter src=\"ch2.snbc\">Two</chapter></body></toc-snbf>")),
            ("snbc/ch1.snbc", Encoding.UTF8.GetBytes($"<snbc><head><title>{Heading1}</title></head><body><text><![CDATA[{Line1}\n{Accented}]]></text><img>pic.png</img><text><![CDATA[{filler}]]></text></body></snbc>")),
            ("snbc/ch2.snbc", Encoding.UTF8.GetBytes($"<snbc><head><title>{Heading2}</title></head><body><text><![CDATA[{Line2}]]></text></body></snbc>")),
        };
        var binary = new List<(string Name, byte[] Data)>
        {
            ("snbc/images/pic.png", Png(MagickColors.Tomato)),
            ("snbc/images/cover.png", Png(MagickColors.Navy)),
        };
        var textStream = text.SelectMany(t => t.Data).ToArray();
        var binaryStream = binary.SelectMany(b => b.Data).ToArray();

        var table = new MemoryStream();
        var names = new MemoryStream();
        foreach (var (name, data) in text)
            TableEntry(table, names, 0x41000000, name, data.Length);
        foreach (var (name, data) in binary)
            TableEntry(table, names, 0x01000000, name, data.Length);
        names.WriteTo(table);
        var tableBytes = table.ToArray();
        var packedTable = Zlib(tableBytes);

        var file = new MemoryStream();
        var header = new byte[44];
        "SNBP000B"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), 0x8000);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12), 0x00A3A3A3);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20), text.Count + binary.Count);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(24), tableBytes.Length);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(28), packedTable.Length);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(32), binaryStream.Length);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(36), textStream.Length);
        file.Write(header);
        file.Write(packedTable);
        var blocks = new List<int>();
        for (var offset = 0; offset < binaryStream.Length; offset += 0x8000)
            blocks.Add((int)file.Length + offset);
        file.Write(binaryStream);
        for (var offset = 0; offset < textStream.Length; offset += 0x8000)
        {
            blocks.Add((int)file.Length);
            var block = textStream[offset..Math.Min(textStream.Length, offset + 0x8000)];
            // The first block stays stored (as when bzip2 doesn't help); the others are compressed.
            if (offset == 0 && block.Length == 0x8000)
            {
                file.Write(block);
                continue;
            }
            using var packed = new MemoryStream();
            using (var bzip2 = new SharpCompress.Compressors.BZip2.BZip2Stream(packed, SharpCompress.Compressors.CompressionMode.Compress, false))
                bzip2.Write(block);
            file.Write(packed.ToArray());
        }
        var tail = new MemoryStream();
        foreach (var block in blocks)
            tail.Write(Be32(block));
        for (var i = 0; i < text.Count + binary.Count; i++)
        {
            tail.Write(Be32(0));
            tail.Write(Be32(0));
        }
        var packedTail = Zlib(tail.ToArray());
        var tailOffset = (int)file.Length;
        file.Write(packedTail);
        file.Write(Be32(packedTail.Length));
        file.Write(Be32(tailOffset));
        file.Write("SNBP000B"u8);
        return file.ToArray();
    }

    private static void TableEntry(Stream table, MemoryStream names, int attributes, string name, int size)
    {
        table.Write(Be32(attributes));
        table.Write(Be32((int)names.Length));
        table.Write(Be32(size));
        names.Write(Encoding.UTF8.GetBytes(name));
        names.WriteByte(0);
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }

    // ───────────────────────── OEB ─────────────────────────

    private static string OebPackage => $"""
        <?xml version="1.0"?>
        <!DOCTYPE package PUBLIC "+//ISBN 0-9673008-1-9//DTD OEB 1.0.1 Package//EN" "http://openebook.org/dtds/oeb-1.0.1/oebpkg101.dtd">
        <package unique-identifier="id">
          <metadata><dc-metadata xmlns:dc="http://purl.org/dc/elements/1.0/" xmlns:oebpackage="http://openebook.org/namespaces/oeb-package/1.0/">
            <dc:Title>{Title}</dc:Title><dc:Creator role="aut">{Author}</dc:Creator><dc:Language>en</dc:Language><dc:Identifier id="id">keeper-1</dc:Identifier>
          </dc-metadata></metadata>
          <manifest>
            <item id="c1" href="text/one.html" media-type="text/x-oeb1-document"/>
            <item id="c2" href="text/two.html" media-type="text/x-oeb1-document"/>
            <item id="pic" href="pic.png" media-type="image/png"/>
            <item id="outside" href="../secret.html" media-type="text/x-oeb1-document"/>
          </manifest>
          <spine><itemref idref="c1"/><itemref idref="c2"/><itemref idref="outside"/></spine>
        </package>
        """;

    private static string OebChapter1 => $"<html><body><h1>{Heading1}</h1><p>{Line1}</p><p><img src=\"../pic.png\"/></p><p>{Accented}</p><p><a href=\"two.html#end\">to the end</a></p></body></html>";

    private static string OebChapter2 => $"<html><body><h1>{Heading2}</h1><p>{Line2}</p><p id=\"end\">The end.</p></body></html>";

    /// <summary>An OEB 1 book as loose files: book/keeper.opf, two chapters and a picture (and a file outside it).</summary>
    public static string OebFolder(string folder)
    {
        var book = Directory.CreateDirectory(Path.Combine(folder, "book")).FullName;
        Directory.CreateDirectory(Path.Combine(book, "text"));
        File.WriteAllText(Path.Combine(book, "keeper.opf"), OebPackage);
        File.WriteAllText(Path.Combine(book, "text", "one.html"), OebChapter1);
        File.WriteAllText(Path.Combine(book, "text", "two.html"), OebChapter2);
        File.WriteAllBytes(Path.Combine(book, "pic.png"), Png(MagickColors.Orchid));
        File.WriteAllText(Path.Combine(folder, "secret.html"), "<html><body><p>Outside the book.</p></body></html>");
        return Path.Combine(book, "keeper.opf");
    }

    /// <summary>The same book zipped as an .oeb.</summary>
    public static string OebZip(string folder)
    {
        var path = Path.Combine(folder, "keeper.oeb");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        EbookBuilders.Entry(zip, "keeper.opf", OebPackage);
        EbookBuilders.Entry(zip, "text/one.html", OebChapter1);
        EbookBuilders.Entry(zip, "text/two.html", OebChapter2);
        EbookBuilders.Entry(zip, "pic.png", Png(MagickColors.Orchid));
        return path;
    }

    // ───────────────────────── Numbers ─────────────────────────

    private static byte[] Le16(int value) => [(byte)value, (byte)(value >> 8)];

    private static byte[] Le32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Le64(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Be32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }
}
