// AZW3 (KF8) and MOBI (MOBI 6 + KF8) written by the built-in e-book engine: books read back with MobiReader into the
// same document model as the EPUB written from the same book (compared as HDocumentAssert dumps), both parts of a
// MOBI, the indexes (SKEL, FRAG, NCX) and records as the format describes them, and the routes (calibre only when
// ranked first). Kindle Previewer isn't available to the tests; a book from each should be checked there by hand.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Ebooks;
using Filee.Engines.Hwp.Hwpx;
using ImageMagick;

namespace Filee.Engines.Tests;

public class KindleWriterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly string[] OptionalEngines = ["pandoc", "libreoffice", "calibre"];

    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A model read back, with its title trimmed as both readers' metadata may differ in spacing.</summary>
    private static HDocument Normalize(HDocument document)
    {
        document.Title = document.Title?.Trim();
        return document;
    }

    /// <summary>The EPUB of <paramref name="book"/> read back: the reference a Kindle book must match.</summary>
    private HDocument EpubModel(Book book)
    {
        var epub = Path.Combine(fx.NewFolder(), "reference.epub");
        // The EPUB writer adds a cover page for covers the content doesn't show; Kindle books keep the cover in EXTH.
        var withoutCover = new Book(book.Document, new BookMetadata { Title = book.Metadata.Title, Language = book.Metadata.Language });
        EpubWriter.Write(withoutCover, epub, epub, fx.NewFolder());
        return Normalize(EpubReader.ReadBook(epub, fx.NewFolder(), Token).Document);
    }

    private string Kindle(Book book, bool mobi6)
    {
        var path = Path.Combine(fx.NewFolder(), mobi6 ? "book.mobi" : "book.azw3");
        MobiWriter.Write(book, path, path, fx.NewFolder(), mobi6);
        return path;
    }

    /// <summary>
    /// A long book: three chapters of Korean and accented text (records and fragments end inside multibyte
    /// characters), lists, a table, bold and italic, pictures, a link into a later fragment of a later chapter and one
    /// back, a footnote.
    /// </summary>
    private Book LongBook()
    {
        var folder = fx.NewFolder();
        File.WriteAllBytes(Path.Combine(folder, "pic.png"), EbookBuilders.Png(120, 60, MagickColors.Teal));
        File.WriteAllBytes(Path.Combine(folder, "cover.png"), EbookBuilders.Png(300, 450, MagickColors.Navy));
        var html = new StringBuilder("<html><head><title>긴 책</title></head><body>\n");
        for (var chapter = 1; chapter <= 3; chapter++)
        {
            html.Append($"<h1 id=\"ch{chapter}\">{chapter}장: 등대지기 이야기</h1>\n");
            html.Append($"<h2>{chapter}.1 바다</h2>\n");
            for (var p = 1; p <= 120; p++)
            {
                html.Append($"<p>{chapter}장 {p}번째 문단: 밤하늘에 <b>별</b>이 <i>반짝</i>였다. Café, naïve, Ünïcödé — 等待 {p}.");
                if (chapter == 1 && p == 3)
                    html.Append(" <a href=\"#far\">먼 곳으로</a>");
                if (chapter == 3 && p == 100)
                    html.Append(" <a href=\"#ch1\">처음으로</a>");
                html.Append("</p>\n");
                if (p == 60)
                    html.Append("<p><img src=\"pic.png\" alt=\"그림\"/></p>\n<ul><li>목록 하나</li><li>목록 둘</li></ul>\n<table><tr><th>이름</th><th>값</th></tr><tr><td>별</td><td>42</td></tr></table>\n");
            }
            if (chapter == 3)
                html.Append("<p id=\"far\">여기가 먼 곳입니다.</p>\n");
        }
        html.Append("</body></html>\n");
        var source = Path.Combine(folder, "book.html");
        File.WriteAllText(source, html.ToString(), new UTF8Encoding(false));
        var document = HtmlReader.Read(source, Path.Combine(folder, "media"));
        var metadata = new BookMetadata { Title = "긴 책", Language = "ko", Publisher = "등대 출판", Description = "시험용 책", CoverImage = Path.Combine(folder, "cover.png") };
        metadata.Authors.AddRange(["김작가", "Ada Lovelace"]);
        return new Book(document, metadata);
    }

    // ───────────────────────── Routes ─────────────────────────

    [Fact]
    public void Kindle_books_are_written_without_calibre_and_calibre_ranked_first_takes_epub()
    {
        var builtIn = new ConverterCatalog(fx.Converters.Where(c => !OptionalEngines.Contains(c.Id))) { Priority = fx.Catalog.Priority }.CreatePlanner();
        foreach (var from in new[] { "epub", "fb2", "mobi", "azw3", "htmlz", "md", "docx", "html", "txt" })
            foreach (var to in new[] { "azw3", "mobi" })
                if (from != to)
                    Assert.Equal("ebook", Assert.Single(builtIn.Plan(from, to)!.Steps).Converter.Id);

        var withCalibre = fx.Catalog.CreatePlanner(["calibre"]);
        Assert.Equal("ebook", Assert.Single(withCalibre.Plan("epub", "azw3")!.Steps).Converter.Id);
        Assert.Equal("ebook", Assert.Single(withCalibre.Plan("epub", "mobi")!.Steps).Converter.Id);

        var order = fx.Catalog.Priority.Where(id => id != "calibre").Prepend("calibre").ToList();
        var calibreFirst = new ConverterCatalog(fx.Converters) { Priority = order }.CreatePlanner(["calibre"]);
        Assert.Equal("calibre", Assert.Single(calibreFirst.Plan("epub", "azw3")!.Steps).Converter.Id);
        Assert.Equal("calibre", Assert.Single(calibreFirst.Plan("epub", "mobi")!.Steps).Converter.Id);
    }

    [Theory]
    [InlineData("azw3")]
    [InlineData("mobi")]
    public async Task Epub_converts_to_kindle_books_that_read_back(string format)
    {
        var epub = EbookBuilders.Epub(fx.NewFolder());
        await using var queue = new JobQueue(new ConverterCatalog(fx.Converters.Where(c => !OptionalEngines.Contains(c.Id))) { Priority = fx.Catalog.Priority });
        var job = new ConversionJob([epub], new Preset { TargetFormat = format }, format);

        await queue.RunAsync(job);

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var output = job.Outputs.Single();
        Assert.EndsWith("." + format, output);
        var book = MobiReader.ReadBook(output, fx.NewFolder(), Token);
        Assert.Equal("별빛 이야기", book.Metadata.Title);
        Assert.Equal(["김작가"], book.Metadata.Authors);
        Assert.Equal("ko", book.Metadata.Language);
        Assert.NotNull(book.Metadata.CoverImage);
        var source = EpubReader.ReadBook(epub, fx.NewFolder(), Token);
        Assert.Equal(HDocumentAssert.Dump(EpubModel(source), layout: false), HDocumentAssert.Dump(Normalize(book.Document), layout: false));
    }

    // ───────────────────────── Round trips ─────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Kf8_part_reads_back_as_the_same_model_as_the_epub(bool mobi6)
    {
        var book = LongBook();

        var path = Kindle(book, mobi6);
        var read = MobiReader.ReadBook(path, fx.NewFolder(), Token);

        Assert.Equal(HDocumentAssert.Dump(EpubModel(book), layout: false), HDocumentAssert.Dump(Normalize(read.Document), layout: false));
        Assert.Equal("긴 책", read.Metadata.Title);
        Assert.Equal(["김작가", "Ada Lovelace"], read.Metadata.Authors);
        Assert.Equal("등대 출판", read.Metadata.Publisher);
        Assert.Equal("시험용 책", read.Metadata.Description);
        Assert.Equal("ko", read.Metadata.Language);
        using var cover = new MagickImage(read.Metadata.CoverImage!);
        Assert.Equal(300u, cover.Width);
        var inlines = read.Document.Sections.SelectMany(s => HDocumentWalker.Inlines(s.Blocks)).ToList();
        var links = inlines.OfType<HLink>().Where(l => l.Target.StartsWith('#')).ToList();
        Assert.Equal(2, links.Count);
        foreach (var link in links)
            Assert.Contains(inlines.OfType<HBookmark>(), b => "#" + b.Name == link.Target);
    }

    [Fact]
    public void Mobi6_part_reads_back_with_the_same_text_headings_pictures_and_links()
    {
        var book = LongBook();
        var path = Kindle(book, mobi6: true);

        var old = MobiReader.ReadBook(path, fx.NewFolder(), Token, kf8: false);

        var expected = EpubModel(book);
        var text = PlainTextWriter.Write(old.Document);
        Assert.Contains(PlainTextWriter.Write(expected).Trim(), text); // the contents page comes first
        var headings = old.Document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().Where(p => p.HeadingLevel > 0).Select(p => HDocumentWalker.Text(p.Inlines)).ToList();
        foreach (var heading in expected.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().Where(p => p.HeadingLevel > 0))
            Assert.Contains(HDocumentWalker.Text(heading.Inlines), headings);
        Assert.Equal(HDocumentWalker.Images(expected).Count(), HDocumentWalker.Images(old.Document).Count());
        var inlines = old.Document.Sections.SelectMany(s => HDocumentWalker.Inlines(s.Blocks)).ToList();
        var links = inlines.OfType<HLink>().Where(l => l.Target.StartsWith('#')).ToList();
        Assert.True(links.Count >= 2 + 6, "the two links and the contents page entries");
        foreach (var link in links)
            Assert.Contains(inlines.OfType<HBookmark>(), b => "#" + b.Name == link.Target);
        Assert.Equal("긴 책", old.Metadata.Title);
        Assert.NotNull(old.Metadata.CoverImage);
    }

    // ───────────────────────── Structure ─────────────────────────

    [Fact]
    public void Azw3_has_kf8_records_indexes_and_a_table_of_contents()
    {
        var path = Kindle(LongBook(), mobi6: false);
        var database = PalmDatabase.Open(File.ReadAllBytes(path));
        Assert.Equal("BOOKMOBI", database.TypeCreator);
        var header = MobiHeader.Parse(database, 0);
        Assert.Equal(8L, header.Version);
        Assert.Equal(2, header.Compression);
        Assert.Null(header.Kf8Boundary);

        // Every text record: at most 4,096 bytes of text, PalmDOC-compressed, then the overlap entry.
        var strings = new Dictionary<long, string>();
        long total = 0;
        for (var i = 1; i <= header.TextRecords; i++)
        {
            var record = database.Record(i);
            var size = record.Length - MobiReader.TrailingSize(record, header.ExtraFlags);
            var text = PalmDocCompression.Decompress(record[..size]);
            Assert.True(text.Length == 4096 || i == header.TextRecords);
            total += text.Length;
        }
        Assert.Equal(header.TextLength, total);

        var skeletons = MobiIndex.Read(database, (int)header.Skeleton, strings);
        var fragments = MobiIndex.Read(database, (int)header.Fragment, strings);
        Assert.Equal(skeletons.Sum(s => s.Tag(1)), fragments.Count);
        Assert.True(fragments.Count > skeletons.Count, "long chapters are cut into several fragments");
        Assert.All(fragments, f => Assert.Equal("P-//*[@aid='0']", strings[f.Tag(2)]));

        var ncxRecord = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(database.Record(0)[0xF4..]);
        var toc = MobiIndex.Read(database, (int)ncxRecord, strings);
        var labels = toc.Select(e => strings[e.Tag(3)]).ToList();
        Assert.Contains("1장: 등대지기 이야기", labels);
        Assert.Contains("3.1 바다", labels);
        Assert.Equal(3, toc.Count(e => e.Tag(4) == 0));
        Assert.All(toc.Where(e => e.Tag(4) == 1), e => Assert.True(e.Tag(21) >= 0, "a child names its parent"));
    }

    [Fact]
    public void Mobi_joins_a_mobi6_and_a_kf8_part_at_a_boundary()
    {
        var path = Kindle(LongBook(), mobi6: true);
        var database = PalmDatabase.Open(File.ReadAllBytes(path));
        var header = MobiHeader.Parse(database, 0);

        Assert.Equal(6L, header.Version);
        var boundary = Assert.NotNull(header.Kf8Boundary);
        Assert.Equal("BOUNDARY", Encoding.ASCII.GetString(database.Record(boundary - 1)));
        Assert.Equal(8L, MobiHeader.Parse(database, boundary).Version);
        // Pictures once, between the MOBI 6 text and the boundary: the content picture, the cover and its thumbnail.
        Assert.Equal(3L, boundary - 1 - header.FirstImage);
    }

    // ───────────────────────── Building blocks ─────────────────────────

    [Fact]
    public void Palmdoc_compression_round_trips_every_kind_of_byte()
    {
        var random = new Random(11);
        List<byte[]> samples =
        [
            Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("The keeper trimmed the wick. 등대지기는 심지를 다듬었다. ", 80)))[..4096],
            [.. Enumerable.Range(0, 4096).Select(i => (byte)(i % 256))],
            new byte[4096],
            [.. Enumerable.Range(0, 3000).Select(_ => (byte)random.Next(256))],
            " a b c d @ ~ \u007f"u8.ToArray(),
            [],
        ];
        foreach (var sample in samples)
        {
            var compressed = PalmDocCompression.Compress(sample);
            Assert.Equal(sample, PalmDocCompression.Decompress(compressed));
        }
        Assert.True(PalmDocCompression.Compress(samples[0]).Length < 2048);
    }

    [Fact]
    public void Index_writer_and_reader_agree_across_records_and_string_records()
    {
        var cncx = new CncxWriter();
        var entries = Enumerable.Range(0, 6000).Select(i => new IndexEntry($"{i:X5}", new Dictionary<int, long[]>
        {
            [1] = [i * 1000L],
            [3] = [cncx.Add($"Entry {i}: {new string('x', i % 40)}")],
            [6] = [i, i * 7L],
        })).ToList();
        var records = MobiIndexWriter.Write([new IndexTag(1, 1, 1), new IndexTag(3, 1, 2), new IndexTag(6, 2, 4)], entries, cncx);
        Assert.True(records.Count > 3, "several data records");
        var database = PalmDatabase.Open(EbookBuilders.PalmDb("index", "BOOKMOBI", [new byte[16], .. records]));

        var strings = new Dictionary<long, string>();
        var read = MobiIndex.Read(database, 1, strings);

        Assert.Equal(6000, read.Count);
        for (var i = 0; i < read.Count; i += 997)
        {
            Assert.Equal($"{i:X5}", read[i].Key);
            Assert.Equal(i * 1000L, read[i].Tag(1));
            Assert.Equal($"Entry {i}: {new string('x', i % 40)}", strings[read[i].Tag(3)]);
            Assert.Equal(i * 7L, read[i].Tag(6, 1));
        }
    }

    [Fact]
    public void Base32_numbers_match_the_reader()
    {
        foreach (var value in new long[] { 0, 1, 31, 32, 1023, 1_048_575, 123_456_789 })
            Assert.Equal(value, MobiReader.Base32(MobiWriter.Base32(value, 10)));
        Assert.Equal("000V", MobiWriter.Base32(31, 4));
    }
}
