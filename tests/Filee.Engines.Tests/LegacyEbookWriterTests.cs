// The older e-book formats written by the built-in engine (PDB as PalmDOC, PML, TCR, RB, SNB): each book is built in
// code, written, and read back with Filee's own reader (text, headings, styles, links, pictures and metadata where
// the format holds them), the files' structure is checked against the format descriptions (headers, record and file
// counts, dictionaries, blocks), and the routes take the built-in writers ahead of calibre unless calibre is ranked
// first. Limits refuse with error.document_too_complex, and a cancelled write leaves no file behind.

using System.Buffers.Binary;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Ebooks;
using Filee.Engines.Hwp.Hwpx;
using ImageMagick;

namespace Filee.Engines.Tests;

public class LegacyEbookWriterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly string[] OptionalEngines = ["pandoc", "libreoffice", "calibre"];
    private const string Title = "The Lighthouse Keeper";
    private const string Author = "Ada Lovelace";
    private const string Line1 = "Ships passed safely through the narrow strait.";
    private const string Accented = "Café, naïve — “quoted” €5";
    private const string Korean = "등대지기의 밤";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A book of two chapters with a sub-heading, bold and italic text, a picture, a link to the second chapter, a
    /// list, a table, a footnote and characters from beyond ASCII (Korean too unless <paramref name="latinOnly"/>).
    /// </summary>
    private Book SampleBook(bool latinOnly = false)
    {
        var folder = fx.NewFolder();
        using (var jpeg = new MagickImage(MagickColors.Teal, 40, 30))
            jpeg.Write(Path.Combine(folder, "pic.jpg"), MagickFormat.Jpeg);
        var html = $"""
            <html><head><title>{Title}</title></head><body>
            <h1>Chapter One</h1>
            <p>The lamp was <b>lit</b> at <i>dusk</i> every evening. {Accented}{(latinOnly ? "" : " " + Korean)}</p>
            <p><img src="pic.jpg" alt=""/></p>
            <p>Go to <a href="#two">the second chapter</a>. Back\slash and &lt;footnote&gt; text.</p>
            <ul><li>First item</li><li>Second item</li></ul>
            <table><tr><th>Name</th><th>Value</th></tr><tr><td>Star</td><td>42</td></tr></table>
            <h2>A Section</h2>
            <p>Water is H<sub>2</sub>O.</p>
            <h1 id="two">Chapter Two</h1>
            <p>{Line1}</p>
            </body></html>
            """;
        var source = Path.Combine(folder, "book.html");
        File.WriteAllText(source, html, new UTF8Encoding(false));
        var document = HtmlReader.Read(source, Path.Combine(folder, "media"));
        var note = new HNote(endnote: false);
        var noteParagraph = new HParagraph();
        noteParagraph.Inlines.Add(new HText("A note at the end.", default));
        note.Blocks.Add(noteParagraph);
        document.Sections[^1].Blocks.OfType<HParagraph>().Last().Inlines.Add(note);
        var metadata = new BookMetadata { Title = Title, Language = "en", Publisher = "Harbour Press", Description = "A short story." };
        metadata.Authors.Add(Author);
        return new Book(document, metadata);
    }

    private static string Text(HDocument document) => PlainTextWriter.Write(document);

    private static IEnumerable<HInline> Inlines(HDocument document) => document.Sections.SelectMany(s => HDocumentWalker.Inlines(s.Blocks));

    private static List<string> Headings(HDocument document, int level) =>
        [.. document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().Where(p => p.HeadingLevel == level).Select(p => HDocumentWalker.Text(p.Inlines))];

    private string Output(string name) => Path.Combine(fx.NewFolder(), name);

    /// <summary>Hands out paths in one folder, as the job queue does for the final output.</summary>
    private sealed class FolderAllocator(string folder, string name) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) =>
            Path.Combine(folder, name + suffix + (extension.Length > 0 ? "." + extension : ""));
    }

    // ───────────────────────── Routes ─────────────────────────

    [Fact]
    public void Legacy_formats_are_written_without_calibre_and_calibre_ranked_first_takes_epub()
    {
        var builtIn = new ConverterCatalog(fx.Converters.Where(c => !OptionalEngines.Contains(c.Id))) { Priority = fx.Catalog.Priority }.CreatePlanner(["rhwp"]);
        foreach (var to in EbookConverter.LegacyWriters)
            foreach (var from in new[] { "epub", "mobi", "azw3", "fb2", "htmlz", "chm", "pdb", "pml", "tcr", "rb", "snb", "oeb", "md", "html", "txt", "docx" })
                if (from != to)
                    Assert.Equal("ebook", Assert.Single(builtIn.Plan(from, to)!.Steps).Converter.Id);

        var withCalibre = fx.Catalog.CreatePlanner(["calibre", "rhwp"]);
        foreach (var to in EbookConverter.LegacyWriters)
            Assert.Equal("ebook", Assert.Single(withCalibre.Plan("epub", to)!.Steps).Converter.Id);

        var order = fx.Catalog.Priority.Where(id => id != "calibre").Prepend("calibre").ToList();
        var calibreFirst = new ConverterCatalog(fx.Converters) { Priority = order }.CreatePlanner(["calibre"]);
        foreach (var to in EbookConverter.LegacyWriters)
            Assert.Equal("calibre", Assert.Single(calibreFirst.Plan("epub", to)!.Steps).Converter.Id);
    }

    [Theory]
    [InlineData("pdb")]
    [InlineData("pml")]
    [InlineData("tcr")]
    [InlineData("rb")]
    [InlineData("snb")]
    public async Task Epub_converts_to_each_format_and_reads_back(string format)
    {
        var epub = EbookBuilders.Epub(fx.NewFolder());
        await using var queue = new JobQueue(new ConverterCatalog(fx.Converters.Where(c => !OptionalEngines.Contains(c.Id))) { Priority = fx.Catalog.Priority });
        var job = new ConversionJob([epub], new Preset { TargetFormat = format }, format);

        await queue.RunAsync(job);

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var output = Assert.Single(job.Outputs);
        Assert.EndsWith("." + format, output);
        var book = format switch
        {
            "pdb" => PdbReader.ReadBook(output, fx.NewFolder(), Token),
            "pml" => PmlReader.ReadBook(output, fx.NewFolder(), Token),
            "tcr" => TcrReader.ReadBook(output, fx.NewFolder(), Token),
            "rb" => RocketBookReader.ReadBook(output, fx.NewFolder(), Token),
            _ => SnbReader.ReadBook(output, fx.NewFolder(), Token),
        };
        var source = EpubReader.ReadBook(epub, fx.NewFolder(), Token).Document;
        var expected = Headings(source, 1).Concat(Headings(source, 2)).Take(3);
        var text = Text(book.Document);
        foreach (var heading in expected)
            Assert.Contains(heading, text);
        if (format is "pml" or "rb" or "snb")
            Assert.NotEmpty(Inlines(book.Document).OfType<HImage>());
        if (format is "rb" or "snb")
            Assert.Equal("별빛 이야기", book.Metadata.Title);
        if (format == "pml")
            Assert.True(Directory.Exists(Path.ChangeExtension(output, null) + PmlPictures.FolderSuffix), "the pictures sit beside the book");
    }

    // ───────────────────────── PDB ─────────────────────────

    [Fact]
    public void Pdb_reads_back_with_its_text_title_and_bookmarks()
    {
        var book = SampleBook();
        var path = Output("book.pdb");
        PdbWriter.Write(book, path, path, Token);

        var read = PdbReader.ReadBook(path, fx.NewFolder(), Token);
        Assert.Equal(Title, read.Metadata.Title);
        var expected = LegacyWriting.PlainText(book.Document, "\n");
        var database = PalmDatabase.Open(File.ReadAllBytes(path));
        Assert.Equal(expected, Encoding.UTF8.GetString(MobiReader.PalmDocText(database)));
        var text = Text(read.Document);
        foreach (var part in new[] { "Chapter One", Accented, Korean, "First item", Line1, "A note at the end." })
            Assert.Contains(part, text);

        Assert.Equal("TEXtREAd", database.TypeCreator);
        var header = database.Record(0);
        Assert.Equal(16, header.Length);
        Assert.Equal(2, PalmDatabase.U16(header, 0)); // PalmDOC compression
        var length = Encoding.UTF8.GetByteCount(expected);
        Assert.Equal(length, PalmDatabase.U32(header, 4));
        var textRecords = (length + 4095) / 4096;
        Assert.Equal(textRecords, PalmDatabase.U16(header, 8));
        Assert.Equal(4096, PalmDatabase.U16(header, 10));
        // Bookmarks after the text: Chapter One, A Section, Chapter Two, each at its heading's byte offset.
        Assert.Equal(1 + textRecords + 3, database.Count);
        var bytes = Encoding.UTF8.GetBytes(expected);
        var names = new List<string>();
        for (var i = 1 + textRecords; i < database.Count; i++)
        {
            var record = database.Record(i);
            Assert.Equal(20, record.Length);
            var name = Encoding.Latin1.GetString(record[..16]).TrimEnd('\0');
            names.Add(name);
            var position = (int)PalmDatabase.U32(record, 16);
            Assert.StartsWith(name, Encoding.UTF8.GetString(bytes, position, bytes.Length - position));
        }
        Assert.Equal(["Chapter One", "A Section", "Chapter Two"], names);
    }

    [Fact]
    public void Pdb_and_tcr_text_is_windows_1252_when_it_fits_and_utf8_otherwise()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var latin = new Book(PlainText.ToDocument($"{Accented}\n\n{Line1}\n"), new BookMetadata { Title = Title });
        var pdb = Output("latin.pdb");
        PdbWriter.Write(latin, pdb, pdb, Token);
        Assert.Equal(Encoding.GetEncoding(1252).GetBytes($"{Accented}\n\n{Line1}\n"), MobiReader.PalmDocText(PalmDatabase.Open(File.ReadAllBytes(pdb))));
        Assert.Contains(Accented, Text(PdbReader.ReadBook(pdb, fx.NewFolder(), Token).Document));

        var tcr = Output("latin.tcr");
        TcrWriter.Write(latin, tcr, Token);
        Assert.Equal(Encoding.GetEncoding(1252).GetBytes($"{Accented}\r\n\r\n{Line1}\r\n"), TcrReader.Decompress(File.ReadAllBytes(tcr), Token));

        var korean = SampleBook();
        TcrWriter.Write(korean, tcr, Token);
        Assert.Equal(Encoding.UTF8.GetBytes(LegacyWriting.PlainText(korean.Document, "\r\n")), TcrReader.Decompress(File.ReadAllBytes(tcr), Token));
        Assert.Contains(Korean, Text(TcrReader.ReadBook(tcr, fx.NewFolder(), Token).Document));
    }

    // ───────────────────────── TCR ─────────────────────────

    [Fact]
    public void Tcr_reads_back_exactly_with_a_dictionary_that_compresses()
    {
        var book = SampleBook();
        var path = Output("book.tcr");
        TcrWriter.Write(book, path, Token);

        var read = TcrReader.ReadBook(path, fx.NewFolder(), Token);
        foreach (var part in new[] { "Chapter Two", Accented, Korean, Line1 })
            Assert.Contains(part, Text(read.Document));

        // A long text: every byte it uses has an entry, all 256 codes are used, and it shrinks well.
        var story = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 3000).Select(i => $"Line {i}: the keeper climbed the stairs and trimmed the wick. 등대 {i % 7}\r\n")));
        var tcr = TcrWriter.Compress(story, Token);
        Assert.Equal(story, TcrReader.Decompress(tcr, Token));
        Assert.True(tcr.AsSpan().StartsWith("!!8-Bit!!"u8));
        var entries = TcrWriter.Dictionary(story, Token);
        Assert.Equal(256, entries.Count);
        Assert.All(story.Distinct(), b => Assert.Contains(entries, e => e is [var only] && only == b));
        Assert.All(entries, e => Assert.InRange(e.Length, 1, 255));
        Assert.True(tcr.Length < story.Length / 2, $"{tcr.Length} of {story.Length} bytes");

        // Every byte value: no code is left for pairs, and the text still comes back.
        var all = Enumerable.Range(0, 512).Select(i => (byte)i).ToArray();
        Assert.Equal(all, TcrReader.Decompress(TcrWriter.Compress(all, Token), Token));
        Assert.Equal("!!8-Bit!!"u8.ToArray().Concat(Enumerable.Repeat((byte)0, 256)), TcrWriter.Compress([], Token));
    }

    // ───────────────────────── PML ─────────────────────────

    [Fact]
    public void Pml_keeps_chapters_styles_links_notes_pictures_and_characters()
    {
        var book = SampleBook();
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "book.pml");
        PmlWriter.Write(book, path, new PmlPictures(path, new FolderAllocator(folder, "book"), fx.NewFolder()), Token);

        var bytes = File.ReadAllBytes(path);
        Assert.All(bytes, b => Assert.InRange(b, (byte)0x0A, (byte)0x7E)); // ASCII only
        var pml = Encoding.ASCII.GetString(bytes);
        Assert.Contains("\\xChapter One\\x\r\n", pml);
        Assert.Contains("\\X1A Section\\X1\r\n", pml);
        Assert.Contains("\\Blit\\B", pml);
        Assert.Contains("\\idusk\\i", pml);
        Assert.Contains("Caf\\a233, na\\a239ve \\a151 \\a147quoted\\a148 \\a1285", pml);
        Assert.Contains("\\UB4F1", pml); // 등
        Assert.Contains("Back\\\\slash and \\a060footnote>", pml);
        Assert.Contains("\\q=\"#two\"the second chapter\\q", pml);
        Assert.Contains("\\Q=\"two\"", pml);
        Assert.Contains("\\m=\"img0001.png\"", pml);
        Assert.Contains("H\\Sb2\\SbO", pml);
        Assert.Contains("\\Fn=\"fn1\"1\\Fn", pml);
        Assert.Contains("<footnote id=\"fn1\">\r\nA note at the end.", pml);
        var picture = Path.Combine(folder, "book" + PmlPictures.FolderSuffix, "img0001.png");
        using (var image = new MagickImage(picture))
            Assert.Equal(MagickFormat.Png, image.Format);

        var read = PmlReader.ReadBook(path, fx.NewFolder(), Token).Document;
        Assert.Equal(["Chapter One", "Chapter Two"], Headings(read, 1));
        Assert.Equal(["A Section"], Headings(read, 2));
        var text = Text(read);
        foreach (var part in new[] { Accented, Korean, "Back\\slash and <footnote> text.", "● First item", "Name | Value", Line1, "A note at the end." })
            Assert.Contains(part, text);
        var inlines = Inlines(read).ToList();
        Assert.Contains(inlines, i => i is HText { Text: "lit", Format.Bold: true });
        Assert.Contains(inlines, i => i is HText { Text: "dusk", Format.Italic: true });
        Assert.Contains(inlines, i => i is HText { Text: "2", Format.Subscript: true });
        Assert.Single(inlines.OfType<HImage>());
        var link = Assert.Single(inlines.OfType<HLink>(), l => HDocumentWalker.Text(l.Content) == "the second chapter");
        Assert.Contains(inlines, i => i is HBookmark b && "#" + b.Name == link.Target);
    }

    [Fact]
    public void Pml_without_pictures_writes_no_folder()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "book.pml");
        PmlWriter.Write(SampleBook(), path, pictures: null, Token);

        Assert.Equal([path], Directory.GetFileSystemEntries(folder));
        Assert.DoesNotContain("\\m=", File.ReadAllText(path));
    }

    // ───────────────────────── RB ─────────────────────────

    [Fact]
    public void Rb_keeps_metadata_headings_styles_links_and_pictures()
    {
        var book = SampleBook();
        var path = Output("book.rb");
        RocketBookWriter.Write(book, path, path, fx.NewFolder(), Token);

        var data = File.ReadAllBytes(path);
        Assert.True(data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xB0, 0x0C, 0xB0, 0x0C, 0x02, 0x00, (byte)'N', (byte)'U', (byte)'V', (byte)'O']));
        Assert.Equal(0x128u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x18)));
        Assert.Equal((uint)data.Length, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x1C)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x128)));
        var entries = Enumerable.Range(0, 3).Select(i =>
        {
            var entry = data.AsSpan(0x128 + 4 + i * 44, 44);
            return (Name: Encoding.ASCII.GetString(entry[..32]).TrimEnd('\0'), Length: BinaryPrimitives.ReadInt32LittleEndian(entry[32..]),
                Offset: BinaryPrimitives.ReadInt32LittleEndian(entry[36..]), Flags: BinaryPrimitives.ReadInt32LittleEndian(entry[40..]));
        }).ToList();
        Assert.Equal([("book.info", 8), ("book.html", 1), ("img0001.png", 0)], entries.Select(e => (e.Name, e.Flags)));
        Assert.Equal(0x128 + 4 + 3 * 44, entries[0].Offset);
        Assert.Equal(data.Length, entries[^1].Offset + entries[^1].Length);
        var info = Encoding.Latin1.GetString(data, entries[0].Offset, entries[0].Length);
        Assert.Equal($"TITLE={Title}\r\nAUTHOR={Author}\r\nBODY=book.html\r\n", info);
        // The page: its chunk count fits its size (4 KB each), and every chunk is a zlib stream.
        var page = data.AsSpan(entries[1].Offset, entries[1].Length);
        var chunks = BinaryPrimitives.ReadInt32LittleEndian(page);
        var size = BinaryPrimitives.ReadInt32LittleEndian(page[4..]);
        Assert.Equal((size + 4095) / 4096, chunks);
        Assert.Equal(0x78, page[8 + chunks * 4]);

        var read = RocketBookReader.ReadBook(path, fx.NewFolder(), Token);
        Assert.Equal(Title, read.Metadata.Title);
        Assert.Equal([Author], read.Metadata.Authors);
        Assert.Equal(["Chapter One", "Chapter Two"], Headings(read.Document, 1));
        Assert.Equal(["A Section"], Headings(read.Document, 2));
        var text = Text(read.Document);
        foreach (var part in new[] { Accented, Korean, "Back\\slash and <footnote> text.", "First item", Line1, "A note at the end." })
            Assert.Contains(part, text);
        var inlines = Inlines(read.Document).ToList();
        Assert.Contains(inlines, i => i is HText { Text: "lit", Format.Bold: true });
        Assert.Contains(inlines, i => i is HText { Text: "dusk", Format.Italic: true });
        Assert.Single(inlines.OfType<HImage>());
        var link = Assert.Single(inlines.OfType<HLink>(), l => HDocumentWalker.Text(l.Content) == "the second chapter");
        Assert.Contains(inlines, i => i is HBookmark b && "#" + b.Name == link.Target);
        Assert.Contains(read.Document.Sections.SelectMany(s => s.Blocks), b => b is HTable);
    }

    // ───────────────────────── SNB ─────────────────────────

    [Fact]
    public void Snb_keeps_metadata_cover_chapters_and_pictures()
    {
        var book = SampleBook();
        var cover = Path.Combine(fx.NewFolder(), "cover.png");
        File.WriteAllBytes(cover, EbookBuilders.Png(30, 45, MagickColors.Navy));
        book.Metadata.CoverImage = cover;
        var path = Output("book.snb");
        SnbWriter.Write(book, path, path, fx.NewFolder(), Token);

        var data = File.ReadAllBytes(path);
        Assert.True(data.AsSpan().StartsWith("SNBP000B"u8));
        Assert.True(data.AsSpan(data.Length - 8).SequenceEqual("SNBP000B"u8));
        var files = SnbReader.Unpack(data, DocumentLimits.Default, Token);
        Assert.Equal(BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20)), files.Count);
        Assert.Equal(["snbc/ch0001.snbc", "snbc/ch0002.snbc", "snbc/images/img0001.png", "snbc/images/img0002.jpg", "snbf/book.snbf", "snbf/toc.snbf"],
            files.Select(f => f.Name));
        var toc = Encoding.UTF8.GetString(files.Single(f => f.Name == "snbf/toc.snbf").Bytes);
        Assert.Contains("<chapters>2</chapters>", toc);
        Assert.Contains("<chapter src=\"ch0002.snbc\">Chapter Two</chapter>", toc);
        var chapter = Encoding.UTF8.GetString(files.Single(f => f.Name == "snbc/ch0001.snbc").Bytes);
        Assert.Contains("<img>img0002.jpg</img>", chapter);
        Assert.Contains("<text><![CDATA[A Section]]></text>", chapter);
        // The text stream's one block is bzip2.
        var textSize = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(36));
        Assert.Equal(files.Where(f => f.Name.Contains(".snb")).Sum(f => f.Bytes.Length), textSize);
        var binarySize = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(32));
        var textBlock = 44 + BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(28)) + binarySize;
        Assert.True(data.AsSpan(textBlock).StartsWith("BZh9"u8));

        var read = SnbReader.ReadBook(path, fx.NewFolder(), Token);
        Assert.Equal(Title, read.Metadata.Title);
        Assert.Equal([Author], read.Metadata.Authors);
        Assert.Equal("en", read.Metadata.Language);
        Assert.Equal("Harbour Press", read.Metadata.Publisher);
        Assert.Equal("A short story.", read.Metadata.Description);
        Assert.NotNull(read.Metadata.CoverImage);
        Assert.Equal(["Chapter One", "Chapter Two"], Headings(read.Document, 1));
        var text = Text(read.Document);
        foreach (var part in new[] { Accented, Korean, "● First item", "Name | Value", Line1, "[1] A note at the end." })
            Assert.Contains(part, text);
        Assert.Single(Inlines(read.Document).OfType<HImage>());
    }

    [Fact]
    public void Snb_text_of_several_blocks_reads_back_and_incompressible_blocks_are_stored()
    {
        // Text of several blocks: each one bzip2.
        var story = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 4000).Select(i => $"<p>Line {i} of the story.</p>")));
        var data = SnbWriter.Container([new("snbc/a.snbc", story, Text: true)], Token);
        Assert.Equal(story, Assert.Single(SnbReader.Unpack(data, DocumentLimits.Default, Token)).Bytes);
        Assert.True(story.Length > 3 * 0x8000);

        // Random bytes: the full block is stored, the short last one that bzip2 can't shrink below a block is padded
        // to a stored block of its own.
        var noise = new byte[2 * 0x8000 - 10];
        new Random(7).NextBytes(noise);
        data = SnbWriter.Container([new("snbc/b.snbc", noise, Text: true)], Token);
        Assert.Equal(noise, Assert.Single(SnbReader.Unpack(data, DocumentLimits.Default, Token)).Bytes);
        Assert.Equal(2 * 0x8000, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(36)));
        var firstBlock = 44 + BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(28));
        Assert.True(data.AsSpan(firstBlock, 0x8000).SequenceEqual(noise.AsSpan(0, 0x8000)));
    }

    // ───────────────────────── Limits and failures ─────────────────────────

    [Fact]
    public void Books_beyond_the_readers_limits_are_refused()
    {
        var text = Assert.Throws<ConversionRefusedException>(() => LegacyWriting.CheckText(DocumentLimits.Default.MaxPartBytes + 1, "PDB"));
        Assert.Equal(DocumentLimits.ErrorKey, text.ErrorKey);
        var records = Assert.Throws<ConversionRefusedException>(() => PalmDatabase.Build("x", "TEXtREAd", Enumerable.Repeat(Array.Empty<byte>(), 70_000).ToList()));
        Assert.Equal(DocumentLimits.ErrorKey, records.ErrorKey);
        var many = Enumerable.Range(0, DocumentLimits.Default.MaxPackageEntries + 1).Select(i => new SnbWriter.SnbFile($"f{i}", [], Text: true)).ToList();
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => SnbWriter.Container(many, Token)).ErrorKey);
    }

    [Theory]
    [InlineData("pdb")]
    [InlineData("pml")]
    [InlineData("tcr")]
    [InlineData("rb")]
    [InlineData("snb")]
    public void A_cancelled_write_leaves_no_file(string format)
    {
        var book = SampleBook();
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "book." + format);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            switch (format)
            {
                case "pdb":
                    PdbWriter.Write(book, path, path, cancelled.Token);
                    break;
                case "pml":
                    PmlWriter.Write(book, path, new PmlPictures(path, new FolderAllocator(folder, "book"), fx.NewFolder()), cancelled.Token);
                    break;
                case "tcr":
                    TcrWriter.Write(book, path, cancelled.Token);
                    break;
                case "rb":
                    RocketBookWriter.Write(book, path, path, fx.NewFolder(), cancelled.Token);
                    break;
                default:
                    SnbWriter.Write(book, path, path, fx.NewFolder(), cancelled.Token);
                    break;
            }
        });
        Assert.Empty(Directory.GetFiles(folder, "*", SearchOption.AllDirectories));
    }
}
