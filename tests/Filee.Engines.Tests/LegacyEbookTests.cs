// The older e-book formats read in-process (CHM, PDB, PML, TCR, RB, SNB, OEB), with calibre left out of the engines:
// every book built in code by LegacyEbookBuilders converts to EPUB, HWPX, TXT and Markdown (PDF in
// LegacyEbookPdfTests), and keeps its text, headings, pictures, links and metadata. The LZX decoder is checked against
// the test encoder (LzxEncoder) on every block kind, reset interval and window size; damaged and hostile files are
// refused with InvalidDataException or error.document_too_complex.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Ebooks;
using Filee.Engines.Hwp.Hwpx;
using static Filee.Engines.Tests.LegacyEbookBuilders;

namespace Filee.Engines.Tests;

public class LegacyEbookTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly string[] OptionalEngines = ["pandoc", "libreoffice", "calibre"];

    /// <summary>Formats and the extension a test file gets.</summary>
    public static readonly TheoryData<string> Formats = ["chm", "pdb", "ztxt", "pml", "tcr", "rb", "snb", "oeb", "opf"];

    private ConverterCatalog BuiltInCatalog() =>
        new(fx.Converters.Where(c => !OptionalEngines.Contains(c.Id))) { Priority = fx.Catalog.Priority };

    private async Task<string> ConvertAsync(string input, string target)
    {
        await using var queue = new JobQueue(BuiltInCatalog());
        var job = new ConversionJob([input], new Preset { TargetFormat = target }, target);
        await queue.RunAsync(job);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    private async Task<string> FailureAsync(string input, string target)
    {
        await using var queue = new JobQueue(BuiltInCatalog());
        var job = new ConversionJob([input], new Preset { TargetFormat = target }, target);
        await queue.RunAsync(job);
        Assert.Equal(JobState.Failed, job.State);
        var file = job.Files.Single();
        return $"{file.ErrorKey} {file.ErrorDetail}";
    }

    /// <summary>A test book of <paramref name="kind"/> in a new folder.</summary>
    internal static string Book(string folder, string kind) => kind switch
    {
        "chm" => Write(folder, "keeper.chm", Chm()),
        "pdb" => Write(folder, "keeper.pdb", PalmDoc(StoryText)),
        "ztxt" => Write(folder, "keeper-ztxt.pdb", Ztxt(StoryText)),
        "pml" => Pml(folder),
        "tcr" => Write(folder, "keeper.tcr", Tcr(StoryText)),
        "rb" => Write(folder, "keeper.rb", RocketBook()),
        "snb" => Write(folder, "keeper.snb", Snb()),
        "oeb" => OebZip(folder),
        "opf" => OebFolder(folder),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Write(string folder, string name, byte[] bytes)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string Text(HDocument document) => PlainTextWriter.Write(document);

    // ───────────────────────── Routes and conversions ─────────────────────────

    [Fact]
    public void Legacy_formats_take_built_in_routes_without_calibre()
    {
        var planner = BuiltInCatalog().CreatePlanner(["rhwp"]);
        foreach (var from in new[] { "chm", "pdb", "pml", "tcr", "rb", "snb", "oeb" })
        {
            foreach (var to in new[] { "epub", "txt", "md", "fb2", "htmlz" })
                Assert.Equal("ebook", Assert.Single(planner.Plan(from, to)!.Steps).Converter.Id);
            Assert.Equal("hwpx-writer", Assert.Single(planner.Plan(from, "hwpx")!.Steps).Converter.Id);
            Assert.Equal(["hwpx-writer", "rhwp"], planner.Plan(from, "pdf")!.Steps.Select(s => s.Converter.Id));
            Assert.Equal("docx-writer", Assert.Single(planner.Plan(from, "docx")!.Steps).Converter.Id);
        }
        // Only LIT and LRF still need calibre.
        Assert.Null(planner.Plan("lit", "epub"));
        Assert.Null(planner.Plan("lrf", "epub"));
    }

    [Fact]
    public void Built_in_readers_win_over_calibre_in_the_default_order()
    {
        var planner = fx.Catalog.CreatePlanner(["calibre", "rhwp"]);
        foreach (var from in EbookConverter.BookReaders.Intersect(CalibreConverter.Inputs))
        {
            Assert.Equal("ebook", Assert.Single(planner.Plan(from, "epub")!.Steps).Converter.Id);
            Assert.Equal(["hwpx-writer", "rhwp"], planner.Plan(from, "pdf")!.Steps.Select(s => s.Converter.Id));
        }
        foreach (var from in CalibreConverter.OnlyCalibreReads)
            Assert.Equal("calibre", Assert.Single(planner.Plan(from, "pdf")!.Steps).Converter.Id);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Converts_to_epub_hwpx_txt_and_markdown(string kind)
    {
        var book = Book(fx.NewFolder(), kind);

        var epub = await ConvertAsync(book, "epub");
        var package = EpubAssert.Valid(epub);
        var text = Text(EpubReader.ReadBook(epub, fx.NewFolder(), TestContext.Current.CancellationToken).Document);
        foreach (var expected in new[] { Heading1, Line1, Line2, "Café, naïve" })
            Assert.Contains(expected, text);
        if (kind is "chm" or "rb" or "snb" or "oeb" or "opf")
            Assert.Equal(Title, package.Title);
        if (kind is "rb" or "snb" or "oeb" or "opf")
            Assert.Contains(Author, package.Authors);
        if (kind is "chm" or "pml" or "rb" or "snb" or "oeb" or "opf")
            Assert.True(package.Images >= 1, "the picture is kept");

        var hwpx = await ConvertAsync(book, "hwpx");
        Assert.Contains(Line2, Text(HwpxReader.Read(hwpx, fx.NewFolder())));

        var txt = await File.ReadAllTextAsync(await ConvertAsync(book, "txt"), TestContext.Current.CancellationToken);
        Assert.Contains(Line1, txt);
        Assert.Contains(Line2, txt);

        var markdown = await File.ReadAllTextAsync(await ConvertAsync(book, "md"), TestContext.Current.CancellationToken);
        Assert.Contains(Line1, markdown);
        if (kind is not ("pdb" or "ztxt" or "tcr"))
            Assert.Contains("# " + Heading1, markdown); // headings survive (plain-text formats have none)
    }

    // ───────────────────────── CHM ─────────────────────────

    [Fact]
    public void Chm_pages_follow_the_contents_file_with_pictures_links_and_the_title()
    {
        var path = Write(fx.NewFolder(), "keeper.chm", Chm());

        var book = ChmReader.ReadBook(path, fx.NewFolder(), TestContext.Current.CancellationToken);

        Assert.Equal(Title, book.Metadata.Title);
        Assert.Equal("en-US", book.Metadata.Language);
        var text = Text(book.Document);
        Assert.True(text.IndexOf("Café", StringComparison.Ordinal) < text.IndexOf(Heading1, StringComparison.Ordinal));
        Assert.True(text.IndexOf(Heading1, StringComparison.Ordinal) < text.IndexOf(Heading2, StringComparison.Ordinal));
        Assert.Contains("Line 600 of chapter 2", text); // the whole LZX section, across frames and resets
        Assert.DoesNotContain("Not in the contents", text);
        Assert.Single(HDocumentWalker.Images(book.Document));
        var inlines = book.Document.Sections.SelectMany(s => HDocumentWalker.Inlines(s.Blocks)).ToList();
        var link = inlines.OfType<HLink>().Single(l => l.Target.StartsWith('#'));
        Assert.Contains(inlines.OfType<HBookmark>(), b => "#" + b.Name == link.Target);
    }

    [Theory]
    [InlineData(0x412, 949, "한글 도움말 페이지")] // Korean: CP949 pages without a charset
    [InlineData(0x419, 1251, "Русская страница")]
    [InlineData(0x411, 932, "日本語のページ")]
    public void Chm_pages_without_a_charset_use_the_code_page_of_the_books_language(int lcid, int codePage, string words)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Write(fx.NewFolder(), "local.chm", Chm(new ChmOptions(Lcid: lcid), Encoding.GetEncoding(codePage), words));

        var book = ChmReader.ReadBook(path, fx.NewFolder(), TestContext.Current.CancellationToken);

        Assert.Contains(words, Text(book.Document));
        Assert.Equal(Title, book.Metadata.Title);
    }

    [Theory]
    [InlineData(0, 16, null, false)] // one stream without resets
    [InlineData(4, 15, null, true)] // the smallest window, no contents file
    [InlineData(2, 21, 12_000_000, true)] // the largest window, E8 translation
    public void Chm_reads_with_other_compression_settings(int resetFrames, int windowBits, int? e8, bool contents)
    {
        var path = Write(fx.NewFolder(), "settings.chm", Chm(new ChmOptions(ResetFrames: resetFrames, E8FileSize: e8, Contents: contents, WindowBits: windowBits)));

        var book = ChmReader.ReadBook(path, fx.NewFolder(), TestContext.Current.CancellationToken);

        var text = Text(book.Document);
        Assert.Contains(Line2, text);
        Assert.Contains("Line 600 of chapter 2", text);
        if (!contents)
            Assert.StartsWith("Café", text.TrimStart()); // the default topic comes first
    }

    // ───────────────────────── LZX ─────────────────────────

    public static readonly TheoryData<int, int, string> LzxCases = new()
    {
        { 16, 2, "123" }, // verbatim, aligned and uncompressed in turn
        { 16, 0, "1" },
        { 15, 1, "2" },
        { 17, 4, "3" },
        { 21, 2, "21" },
        { 18, 0, "32" },
    };

    [Theory]
    [MemberData(nameof(LzxCases))]
    public void Lzx_decoder_reads_what_the_encoder_wrote(int windowBits, int resetFrames, string kinds)
    {
        var data = LzxSample(300_000);
        var options = new LzxEncoder.Options(windowBits, resetFrames, BlockSize: 23_456, Kinds: [.. kinds.Select(k => (LzxEncoder.BlockKind)(k - '0'))]);
        var (compressed, _) = LzxEncoder.Compress(data, options);

        Assert.Equal(data, Decode(compressed, windowBits, resetFrames, data.Length));
    }

    [Fact]
    public void Lzx_decoder_undoes_e8_translation()
    {
        var data = LzxSample(100_000);
        for (var i = 100; i < data.Length - 20; i += 997)
        {
            data[i] = 0xE8; // a call with a relative target
            BitConverter.GetBytes(i % 3 == 0 ? -i / 2 : 5_000 + i).CopyTo(data, i + 1);
        }
        var (compressed, _) = LzxEncoder.Compress(data, new LzxEncoder.Options(16, 2, E8FileSize: 6_000_000));

        Assert.Equal(data, Decode(compressed, 16, 2, data.Length));
    }

    /// <summary>Text, runs and noise: literals, long and short matches, repeat offsets.</summary>
    internal static byte[] LzxSample(int length)
    {
        var random = new Random(4321);
        var data = new byte[length];
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => $"Paragraph {i}: the keeper trimmed the wick. ")));
        for (var i = 0; i < length;)
        {
            switch (random.Next(4))
            {
                case 0:
                    var start = random.Next(text.Length - 600);
                    var count = Math.Min(length - i, random.Next(20, 600));
                    Array.Copy(text, start, data, i, count);
                    i += count;
                    break;
                case 1:
                    var run = Math.Min(length - i, random.Next(3, 400));
                    data.AsSpan(i, run).Fill((byte)random.Next(256));
                    i += run;
                    break;
                default:
                    var noise = Math.Min(length - i, random.Next(1, 300));
                    random.NextBytes(data.AsSpan(i, noise));
                    i += noise;
                    break;
            }
        }
        return data;
    }

    internal static byte[] Decode(byte[] compressed, int windowBits, int resetFrames, long length)
    {
        using var input = new MemoryStream(compressed);
        using var output = new MemoryStream();
        new LzxDecoder(new LzxInput(input, 0, compressed.Length), windowBits, resetFrames).Decompress(output, length, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    [Fact]
    public void Damaged_lzx_data_fails_with_invalid_data()
    {
        var data = LzxSample(80_000);
        var (compressed, _) = LzxEncoder.Compress(data, new LzxEncoder.Options(16, 2, Kinds: [LzxEncoder.BlockKind.Verbatim]));

        // An unknown block type (the first 3 bits after the E8 bit).
        var unknown = (byte[])compressed.Clone();
        unknown[1] = (byte)(unknown[1] & 0x8F | 0x70); // block type 7
        Assert.Throws<InvalidDataException>(() => Decode(unknown, 16, 2, data.Length));
        // Cut short.
        Assert.Throws<InvalidDataException>(() => Decode(compressed[..(compressed.Length / 3)], 16, 2, data.Length));
        // Any byte changed: decoded, or refused with InvalidDataException, never anything else.
        var random = new Random(7);
        for (var i = 0; i < 300; i++)
        {
            var damaged = (byte[])compressed.Clone();
            damaged[random.Next(damaged.Length)] ^= (byte)(1 << random.Next(8));
            try
            {
                Decode(damaged, 16, 2, data.Length);
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    // ───────────────────────── PDB ─────────────────────────

    [Fact]
    public void Pdb_reads_palmdoc_ztxt_and_mobipocket_and_refuses_ereader_and_plucker()
    {
        var folder = fx.NewFolder();
        var palmdoc = PdbReader.ReadBook(Write(folder, "a.pdb", PalmDoc(StoryText)), fx.NewFolder(), TestContext.Current.CancellationToken);
        Assert.Contains(Line1, Text(palmdoc.Document));
        Assert.Equal("The_Lighthouse_Keeper", palmdoc.Metadata.Title);
        Assert.Contains("Ünïcödé", Text(PdbReader.ReadBook(Write(folder, "b.pdb", Ztxt(StoryText)), fx.NewFolder(), TestContext.Current.CancellationToken).Document));

        var mobi = EbookBuilders.Mobi($"<html><body><h1>{Heading1}</h1><p>{Line1}</p></body></html>", [], new EbookBuilders.MobiOptions(Title: Title));
        Assert.Equal(Title, PdbReader.ReadBook(Write(folder, "c.pdb", mobi), fx.NewFolder(), TestContext.Current.CancellationToken).Metadata.Title);

        var ereader = EbookBuilders.PalmDb(Title, "PNRdPPrs", [new byte[132], [1, 2, 3]]);
        Assert.Equal(PdbReader.EreaderMessage, Assert.Throws<InvalidOperationException>(() => PdbReader.ReadBook(Write(folder, "d.pdb", ereader), fx.NewFolder(), TestContext.Current.CancellationToken)).Message);
        var plucker = EbookBuilders.PalmDb(Title, "DataPlkr", [new byte[8]]);
        Assert.Contains("Plucker", Assert.Throws<InvalidDataException>(() => PdbReader.ReadBook(Write(folder, "e.pdb", plucker), fx.NewFolder(), TestContext.Current.CancellationToken)).Message);
    }

    [Fact]
    public async Task Ereader_and_drm_books_fail_with_a_clear_message()
    {
        var folder = fx.NewFolder();
        var ereader = Write(folder, "locked.pdb", EbookBuilders.PalmDb(Title, "PNRdPPrs", [new byte[132], [1, 2, 3]]));
        Assert.Contains("eReader", await FailureAsync(ereader, "epub"));
        var rocket = Write(folder, "locked.rb", RocketBook(encrypted: true));
        Assert.Contains("DRM", await FailureAsync(rocket, "epub"));
    }

    // ───────────────────────── PML, TCR, RB, SNB, OEB ─────────────────────────

    [Fact]
    public void Pml_keeps_styles_alignment_links_notes_and_pictures()
    {
        var book = PmlReader.ReadBook(Pml(fx.NewFolder()), fx.NewFolder(), TestContext.Current.CancellationToken);

        var document = book.Document;
        var paragraphs = document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().ToList();
        Assert.Contains(paragraphs, p => p.HeadingLevel == 1 && HDocumentWalker.Text(p.Inlines) == Heading1);
        Assert.Contains(paragraphs, p => p.HeadingLevel == 2 && HDocumentWalker.Text(p.Inlines) == Heading2);
        Assert.Contains(paragraphs, p => p.HeadingLevel == 2 && p.PageBreakBefore);
        var inlines = document.Sections.SelectMany(s => HDocumentWalker.Inlines(s.Blocks)).ToList();
        Assert.Contains(inlines, i => i is HText { Format.Bold: true } t && t.Text.Contains("lamp", StringComparison.Ordinal));
        Assert.Contains(inlines, i => i is HText { Format.Italic: true, Text: "italic" });
        var text = Text(document);
        Assert.Contains("© centred line", text);
        Assert.Contains("☺ smile\\", text);
        Assert.Contains("A note at the end.", text);
        Assert.Contains("Café, naïve, Ünïcödé", text);
        Assert.Single(HDocumentWalker.Images(document));
        foreach (var link in inlines.OfType<HLink>())
            Assert.Contains(inlines.OfType<HBookmark>(), b => "#" + b.Name == link.Target);
        Assert.Equal(2, inlines.OfType<HLink>().Count()); // the chapter link and the note
    }

    [Fact]
    public void Tcr_text_comes_back_exactly()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Equal(StoryText, Encoding.GetEncoding(1252).GetString(TcrReader.Decompress(Tcr(StoryText), TestContext.Current.CancellationToken)));
        Assert.Throws<InvalidDataException>(() => TcrReader.Decompress("!!8-Bit!!\u0005ab"u8.ToArray(), TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => TcrReader.Decompress("plain text"u8.ToArray(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Snb_keeps_metadata_cover_and_chapter_order()
    {
        var book = SnbReader.ReadBook(Write(fx.NewFolder(), "keeper.snb", Snb()), fx.NewFolder(), TestContext.Current.CancellationToken);

        Assert.Equal(Title, book.Metadata.Title);
        Assert.Equal([Author], book.Metadata.Authors);
        Assert.Equal("en", book.Metadata.Language);
        Assert.Equal("Harbour Press", book.Metadata.Publisher);
        Assert.NotNull(book.Metadata.CoverImage);
        var text = Text(book.Document);
        Assert.True(text.IndexOf(Heading1, StringComparison.Ordinal) < text.IndexOf(Heading2, StringComparison.Ordinal));
        Assert.Contains("Line 700: the keeper", text); // the bzip2 block after the stored one
    }

    [Fact]
    public void Oeb_reads_only_files_inside_the_package_and_writes_nothing_next_to_it()
    {
        var folder = fx.NewFolder();
        var opf = OebFolder(folder);
        var before = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length;

        var book = OebReader.ReadBook(opf, fx.NewFolder(), TestContext.Current.CancellationToken);

        Assert.Equal(Title, book.Metadata.Title);
        Assert.Equal([Author], book.Metadata.Authors);
        Assert.DoesNotContain("Outside the book", Text(book.Document));
        Assert.Single(HDocumentWalker.Images(book.Document));
        Assert.Equal(before, Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length);
        var inlines = book.Document.Sections.SelectMany(s => HDocumentWalker.Inlines(s.Blocks)).ToList();
        var link = inlines.OfType<HLink>().Single();
        Assert.Contains(inlines.OfType<HBookmark>(), b => "#" + b.Name == link.Target);
    }

    // ───────────────────────── Hostile input ─────────────────────────

    private static void TooComplex(Action read) =>
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(read).ErrorKey);

    [Fact]
    public void Chm_whose_compressed_section_claims_too_much_is_refused()
    {
        var folder = fx.NewFolder();
        // One section-1 file that claims 900 MB, from a few kilobytes of LZX.
        var (lzx, _) = LzxEncoder.Compress(new byte[200_000], new LzxEncoder.Options());
        var stored = Control(16, 2);
        var entries = new List<(string, int, long, long)>
        {
            ("::DataSpace/Storage/MSCompressed/Content", 0, 0, lzx.Length),
            ("::DataSpace/Storage/MSCompressed/ControlData", 0, lzx.Length, stored.Length),
            ("/big.htm", 1, 0, 900L * 1024 * 1024),
        };
        var bomb = Write(folder, "bomb.chm", ChmContainer(entries, [.. lzx, .. stored], 0x409));
        TooComplex(() => ChmReader.ReadBook(bomb, fx.NewFolder()));

        // A file larger than a part may be, and a ratio far beyond LZX on real pages.
        entries[2] = ("/big.htm", 1, 0, 60L * 1024 * 1024);
        var ratio = Write(folder, "ratio.chm", ChmContainer(entries, [.. lzx, .. stored], 0x409));
        TooComplex(() => ChmReader.ReadBook(ratio, fx.NewFolder()));
    }

    private static byte[] Control(int windowBits, int resetFrames)
    {
        var control = new byte[28];
        BitConverter.GetBytes(6).CopyTo(control, 0);
        "LZXC"u8.CopyTo(control.AsSpan(4));
        BitConverter.GetBytes(2).CopyTo(control, 8);
        BitConverter.GetBytes(resetFrames).CopyTo(control, 12);
        BitConverter.GetBytes((1 << windowBits) / LzxEncoder.FrameSize).CopyTo(control, 16);
        return control;
    }

    [Fact]
    public void Damaged_chm_containers_fail_with_invalid_data()
    {
        var folder = fx.NewFolder();
        var good = Chm();
        void Damaged(string name, Action<byte[]> change)
        {
            var bytes = (byte[])good.Clone();
            change(bytes);
            var path = Write(folder, name, bytes);
            Assert.Throws<InvalidDataException>(() => ChmReader.ReadBook(path, fx.NewFolder()));
        }
        Damaged("signature.chm", b => b[0] = (byte)'X');
        Damaged("directory.chm", b => BitConverter.GetBytes(long.MaxValue / 2).CopyTo(b, 0x48));
        Damaged("chunks.chm", b => BitConverter.GetBytes(1_000_000).CopyTo(b, 0x78 + 44)); // ITSP chunk count
        Damaged("chunksize.chm", b => BitConverter.GetBytes(16).CopyTo(b, 0x78 + 16));
        Damaged("cut.chm", b => Array.Clear(b, b.Length - 20_000, 20_000)); // the LZX data
        var cut = Write(folder, "short.chm", good[..(good.Length / 2)]);
        Assert.Throws<InvalidDataException>(() => ChmReader.ReadBook(cut, fx.NewFolder(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Text_formats_that_unpack_beyond_the_limits_are_refused()
    {
        var folder = fx.NewFolder();
        // TCR: 256 entries of 255 bytes, then 300,000 codes = 76 MB of text.
        var tcr = new MemoryStream();
        tcr.Write("!!8-Bit!!"u8);
        for (var i = 0; i < 256; i++)
        {
            tcr.WriteByte(255);
            tcr.Write(new byte[255].Select(_ => (byte)'a').ToArray());
        }
        tcr.Write(new byte[300_000]);
        TooComplex(() => TcrReader.ReadBook(Write(folder, "bomb.tcr", tcr.ToArray()), fx.NewFolder()));

        // zTXT: 20 MB of spaces in a zlib stream of some 20 KB.
        var spaces = new string(' ', 20 * 1024 * 1024);
        TooComplex(() => PdbReader.ReadBook(Write(folder, "bomb.pdb", Ztxt(spaces)), fx.NewFolder()));

        // RB: a file that claims more than a part may hold.
        var rocket = RocketBook();
        var html = rocket.AsSpan().IndexOf("keeper.html"u8);
        var data = BitConverter.ToInt32(rocket, html + 36); // the file's offset
        BitConverter.GetBytes(200 * 1024 * 1024).CopyTo(rocket, data + 4);
        TooComplex(() => RocketBookReader.ReadBook(Write(folder, "bomb.rb", rocket), fx.NewFolder()));

        // SNB: a header that claims 2 GB of text.
        var snb = Snb();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(snb.AsSpan(36), int.MaxValue);
        TooComplex(() => SnbReader.ReadBook(Write(folder, "bomb.snb", snb), fx.NewFolder()));
    }

    [Fact]
    public void Damaged_rb_snb_and_pdb_files_fail_with_invalid_data()
    {
        var folder = fx.NewFolder();
        var rocket = RocketBook();
        BitConverter.GetBytes(0x7FFFFFF0).CopyTo(rocket, 0x18);
        Assert.Throws<InvalidDataException>(() => RocketBookReader.ReadBook(Write(folder, "toc.rb", rocket), fx.NewFolder(), TestContext.Current.CancellationToken));

        var snb = Snb();
        snb[^1] = (byte)'X'; // the tail's signature
        Assert.Throws<InvalidDataException>(() => SnbReader.ReadBook(Write(folder, "tail.snb", snb), fx.NewFolder(), TestContext.Current.CancellationToken));

        var pdb = PalmDoc(StoryText);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(pdb.AsSpan(78 + 8), int.MaxValue); // record 1 starts after the file
        Assert.Throws<InvalidDataException>(() => PdbReader.ReadBook(Write(folder, "records.pdb", pdb), fx.NewFolder(), TestContext.Current.CancellationToken));
    }
}
