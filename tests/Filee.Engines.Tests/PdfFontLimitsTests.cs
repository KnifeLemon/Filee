// Hostile inputs on the Compress PDF font path, built in code: content streams nested a million deep, chains of
// thousands of forms, one big form drawn under many resource dictionaries (Pdf/PdfFontUses.cs), CFF subroutines that
// call each other exponentially often (Fonts/Cff/Type2Interpreter.cs, CffSubsetter.cs) and character maps whose
// groups cover the same codes over and over (Fonts/CmapTable.cs). Each must finish quickly; past a limit a font stays
// whole (a content stream or form the walk gives up on makes its fonts unsure, a font program that runs too long is
// refused with InvalidDataException, which leaves the PDF's font as it was).

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Fonts;
using Filee.Engines.Fonts.Cff;
using Filee.Engines.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

public class PdfFontLimitsTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Compress_pdf_still_writes_a_file_from_hostile_content_and_forms()
    {
        // The million "[" also overflowed PDFsharp's own content parser, which the picture pass used unchecked.
        var source = Path.Combine(fx.NewFolder(), "hostile.pdf");
        using (var document = new PdfDocument())
        {
            var font = Font(document);
            AddPage(document, Resources(document, font), "/F1 12 Tf (A) Tj " + new string('[', 1_000_000));
            AddPage(document, Resources(document, font, ("/X", FormChain(document, font, 20_000))), "/X Do");
            document.Save(source);
        }

        var job = await fx.ConvertAsync([source], new Preset
        {
            TargetFormat = "pdf",
            Pdf = { Compression = PdfCompression.Ebook },
            Output = { FileNamePattern = "{name}_compressed" },
        });

        var errors = string.Join("; ", job.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, $"{job.State}: {errors}");
        using var output = PdfReader.Open(job.Outputs.Single(), PdfDocumentOpenMode.Import);
        Assert.Equal(2, output.PageCount);
    }

    [Fact]
    public void Content_nested_too_deeply_for_pdfsharp_is_refused_before_it_parses()
    {
        Assert.Throws<InvalidDataException>(() => PdfContentNesting.Checked(Encoding.Latin1.GetBytes(new string('[', 1_000_000))));
        Assert.Throws<InvalidDataException>(() => PdfContentNesting.Checked(Encoding.Latin1.GetBytes(string.Concat(Enumerable.Repeat("<</A ", 100)))));
        // Brackets inside strings, comments, hex strings and inline image data don't count.
        var image = new string('[', 200);
        PdfContentNesting.Checked(Encoding.Latin1.GetBytes($"({image}) Tj % {image}\n<5B5B> Tj BI /W 200 /H 1 /BPC 8 /CS /G ID {image} EI [[1]] TJ"));

        using var document = new PdfDocument();
        AddPage(document, Resources(document, Font(document)), new string('[', 1_000_000));
        Assert.False(PdfBlankPages.IsBlank(document.Pages[0]));
    }

    [Fact]
    public void Text_after_a_marked_content_dictionary_is_still_read()
    {
        using var document = new PdfDocument();
        var font = Font(document);
        AddPage(document, Resources(document, font), "/P <</MCID 0>> BDC /F1 12 Tf (A) Tj EMC /Span <</ActualText (x)>> BDC (B) Tj EMC");

        var uses = PdfFontUses.Find(document, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 'A', 'B' }.Select(c => (int)c), uses.Codes[font].Order());
    }

    [Fact]
    public void A_content_stream_nested_a_million_deep_leaves_its_fonts_whole()
    {
        using var document = new PdfDocument();
        var font = Font(document);
        AddPage(document, Resources(document, font), "/F1 12 Tf (A) Tj " + new string('[', 1_000_000));

        var uses = Timed(() => PdfFontUses.Find(document, TestContext.Current.CancellationToken));

        Assert.Contains(font, uses.Unsure);
    }

    [Fact]
    public void Arrays_nested_a_few_deep_still_read()
    {
        using var document = new PdfDocument();
        var font = Font(document);
        AddPage(document, Resources(document, font), "/F1 12 Tf [[[[[[[[[[1]]]]]]]]]] pop <</A <</B [1 2]>>>> pop (AB) Tj");

        var uses = PdfFontUses.Find(document, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(font, uses.Unsure);
        Assert.Equal(new[] { 'A', 'B' }.Select(c => (int)c), uses.Codes[font].Order());
    }

    [Fact]
    public void A_chain_of_thousands_of_forms_is_followed_only_so_deep_and_its_fonts_stay_whole()
    {
        using var document = new PdfDocument();
        var font = Font(document);
        var first = FormChain(document, font, 20_000);
        AddPage(document, Resources(document, font, ("/X", first)), "/X Do");

        var uses = Timed(() => PdfFontUses.Find(document, TestContext.Current.CancellationToken));

        Assert.Contains(font, uses.Unsure);
    }

    [Fact]
    public void A_short_chain_of_forms_is_followed_to_its_end()
    {
        using var document = new PdfDocument();
        var font = Font(document);
        var first = FormChain(document, font, 10);
        AddPage(document, Resources(document, font, ("/X", first)), "/X Do");

        var uses = PdfFontUses.Find(document, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(font, uses.Unsure);
        Assert.Contains((int)'A', uses.Codes[font]);
    }

    [Fact]
    public void A_big_form_drawn_under_many_resources_is_read_once_and_each_font_counted()
    {
        // A 2 MB form without resources of its own, drawn by 3000 forms that each name a font /F1 of their own: read
        // again for each, that is 6 GB of content and past the document's budget, so every font would stay whole.
        using var document = new PdfDocument();
        var text = new StringBuilder("/F1 12 Tf (A) Tj\n");
        while (text.Length < 2_000_000)
            text.Append("0 0 m 100 100 l S\n");
        var shared = Stream(document, text.ToString());
        shared.Elements.SetName("/Subtype", "/Form");
        var fonts = new List<PdfDictionary>();
        var parents = new List<(string, PdfDictionary)>();
        for (var i = 0; i < 3000; i++)
        {
            var font = Font(document);
            fonts.Add(font);
            var parent = Stream(document, "/S Do");
            parent.Elements.SetName("/Subtype", "/Form");
            parent.Elements["/Resources"] = Resources(document, font, ("/S", shared));
            parents.Add(($"/P{i}", parent));
        }
        AddPage(document, Resources(document, fonts[0], [.. parents]), string.Concat(parents.Select(p => p.Item1 + " Do ")));

        var uses = Timed(() => PdfFontUses.Find(document, TestContext.Current.CancellationToken));

        Assert.All(fonts, font =>
        {
            Assert.DoesNotContain(font, uses.Unsure);
            Assert.Equal(new[] { (int)'A' }, uses.Codes[font]);
        });
    }

    [Fact]
    public void Cff_subroutines_calling_each_other_exponentially_are_refused_quickly()
    {
        var cff = ExponentialCff();

        Assert.Throws<InvalidDataException>(() => Timed(() => CffSubsetter.Subset(cff, [1], TestContext.Current.CancellationToken)));
        Assert.Throws<InvalidDataException>(() => Timed(() => new Type2Interpreter(CffFont.Parse(cff)).Run(1)));
        // Glyph 0 (endchar alone) still runs.
        Assert.Empty(new Type2Interpreter(CffFont.Parse(cff)).Run(0));
    }

    [Fact]
    public void Real_cff_fonts_run_every_glyph_within_the_budgets()
    {
        foreach (var name in new[] { "FileeTest-CID.otf", "FileeTest-NameKeyed.otf" })
        {
            var cff = SfntFont.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", name))).Require("CFF ");
            var font = CffFont.Parse(cff);
            var interpreter = new Type2Interpreter(font);
            for (var glyph = 0; glyph < font.GlyphCount; glyph++)
                interpreter.Run(glyph);
            CffSubsetter.Subset(cff, Enumerable.Range(0, font.GlyphCount), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void Cmap_groups_covering_unicode_many_times_over_are_refused_quickly()
    {
        // 2000 groups of every code point: two billion codes to visit.
        var cmap = Cmap((3, 10, Format12(Enumerable.Repeat((0u, 0x10FFFFu), 2000))));

        Assert.Throws<InvalidDataException>(() => Timed(() => CmapTable.Read(cmap, out _)));
        Assert.Throws<InvalidDataException>(() => Timed(() => CmapTable.ReadAll(cmap)));
    }

    [Fact]
    public void Cmap_format_4_segments_covering_the_bmp_many_times_over_are_refused_quickly()
    {
        var cmap = Cmap((3, 1, Format4(Enumerable.Repeat((0, 0xFFFE), 2000))));

        Assert.Throws<InvalidDataException>(() => Timed(() => CmapTable.Read(cmap, out _)));
    }

    [Fact]
    public void Many_cmap_records_pointing_at_one_big_subtable_are_refused_quickly()
    {
        var cmap = Cmap([.. Enumerable.Repeat((3, 10, Format12([(0u, 0x10FFFFu)])), 1000)], shared: true);

        Assert.Throws<InvalidDataException>(() => Timed(() => CmapTable.ReadAll(cmap)));
    }

    [Fact]
    public void A_cmap_of_every_code_point_still_reads()
    {
        var cmap = Cmap((0, 3, Format4([(0x20, 0x7E)])), (3, 10, Format12([(0u, 0x10FFFFu)], format: 13)));

        Assert.Equal(0x10FFFF - 0xFFFF, Timed(() => CmapTable.Read(cmap, out _)).Count(p => p.Key > 0xFFFF));
        Assert.Equal(2, CmapTable.ReadAll(cmap).Count);
    }

    private static T Timed<T>(Func<T> run)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return run();
        }
        finally
        {
            Assert.True(stopwatch.Elapsed < Quick, $"Took {stopwatch.Elapsed.TotalSeconds:0.0} s");
        }
    }

    private static PdfDictionary Font(PdfDocument document)
    {
        var font = new PdfDictionary(document);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/TrueType");
        font.Elements.SetName("/BaseFont", "/FileeTest");
        document.Internals.AddObject(font);
        return font;
    }

    private static PdfDictionary Resources(PdfDocument document, PdfDictionary font, params (string Name, PdfDictionary Form)[] forms)
    {
        var resources = new PdfDictionary(document);
        var fonts = new PdfDictionary(document);
        fonts.Elements["/F1"] = font.Reference;
        resources.Elements["/Font"] = fonts;
        if (forms.Length > 0)
        {
            var xobjects = new PdfDictionary(document);
            foreach (var (name, form) in forms)
                xobjects.Elements[name] = form.Reference;
            resources.Elements["/XObject"] = xobjects;
        }
        return resources;
    }

    private static PdfDictionary Stream(PdfDocument document, string content)
    {
        var stream = new PdfDictionary(document);
        stream.CreateStream(Encoding.Latin1.GetBytes(content));
        document.Internals.AddObject(stream);
        return stream;
    }

    private static void AddPage(PdfDocument document, PdfDictionary resources, string content)
    {
        var page = document.AddPage();
        page.Elements["/Resources"] = resources;
        page.Elements["/Contents"] = Stream(document, content).Reference;
    }

    /// <summary><paramref name="length"/> forms, each with its own resources, showing "A" and drawing the next.</summary>
    private static PdfDictionary FormChain(PdfDocument document, PdfDictionary font, int length)
    {
        PdfDictionary? next = null;
        for (var i = 0; i < length; i++)
        {
            var form = Stream(document, next is null ? "/F1 12 Tf (A) Tj" : "/F1 12 Tf (A) Tj /X Do");
            form.Elements.SetName("/Subtype", "/Form");
            form.Elements["/Resources"] = next is null ? Resources(document, font) : Resources(document, font, ("/X", next));
            next = form;
        }
        return next!;
    }

    /// <summary>
    /// A CFF font whose glyph 1 calls global subroutine 0, and subroutine n (up to 9) calls subroutine n + 1 a thousand
    /// times: 1000^9 lines if run whole.
    /// </summary>
    internal static byte[] ExponentialCff()
    {
        var subrs = new byte[10][];
        for (var n = 0; n < 9; n++)
        {
            var call = new byte[] { (byte)(n + 1 - 107 + 139), 29 }; // n + 1 (less the bias of 107) callgsubr
            subrs[n] = [.. Enumerable.Repeat(call, 1000).SelectMany(c => c), 11];
        }
        subrs[9] = [139 + 1, 139 + 1, 5, 11]; // 1 1 rlineto return
        byte[][] charStrings = [[14], [139 - 107, 29, 14]]; // endchar; 0 callgsubr endchar

        byte[] header = [1, 0, 4, 1];
        var names = Index([[(byte)'T']]);
        var strings = Index([]);
        var globals = Index(subrs);
        const int topLength = 6; // one five-byte integer and the CharStrings operator
        var topIndex = Index([new byte[topLength]]).Length;
        var charStringsAt = header.Length + names.Length + topIndex + strings.Length + globals.Length;
        var top = new byte[topLength];
        top[0] = 29;
        BinaryPrimitives.WriteInt32BigEndian(top.AsSpan(1), charStringsAt);
        top[5] = 17;
        return [.. header, .. names, .. Index([top]), .. strings, .. globals, .. Index(charStrings)];
    }

    private static byte[] Index(byte[][] items)
    {
        if (items.Length == 0)
            return [0, 0];
        var output = new List<byte> { (byte)(items.Length >> 8), (byte)items.Length, 4 };
        var offset = 1;
        foreach (var item in items.Prepend([]))
        {
            offset += item.Length;
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, offset);
            output.AddRange(bytes);
        }
        foreach (var item in items)
            output.AddRange(item);
        return [.. output];
    }

    /// <summary>A cmap table with the given subtables.</summary>
    internal static byte[] Cmap(params (int Platform, int Encoding, byte[] Subtable)[] subtables) => Cmap(subtables, shared: false);

    /// <summary>A cmap table; with <paramref name="shared"/>, every record points at the first subtable.</summary>
    private static byte[] Cmap((int Platform, int Encoding, byte[] Subtable)[] subtables, bool shared)
    {
        var w = new BigEndianWriter(4 + 8 * subtables.Length);
        w.U16(0);
        w.U16(subtables.Length);
        var offset = 4 + 8 * subtables.Length;
        foreach (var (platform, encoding, subtable) in subtables)
        {
            w.U16(platform);
            w.U16(encoding);
            w.U32((uint)offset);
            if (!shared)
                offset += subtable.Length;
        }
        var body = shared ? subtables[0].Subtable : subtables.SelectMany(s => s.Subtable).ToArray();
        return [.. w.ToArray(), .. body];
    }

    /// <summary>Format 12 (glyphs from 1 on) or 13 (glyph 1 throughout) with the given groups.</summary>
    internal static byte[] Format12(IEnumerable<(uint Start, uint End)> groups, int format = 12)
    {
        var list = groups.ToList();
        var w = new BigEndianWriter(16 + 12 * list.Count);
        w.U16(format);
        w.U16(0);
        w.U32((uint)(16 + 12 * list.Count));
        w.U32(0);
        w.U32((uint)list.Count);
        foreach (var (start, end) in list)
        {
            w.U32(start);
            w.U32(end);
            w.U32(1);
        }
        return w.ToArray();
    }

    /// <summary>Format 4 with the given segments (glyph = code + 1) and the closing 0xFFFF one.</summary>
    internal static byte[] Format4(IEnumerable<(int Start, int End)> segments)
    {
        var list = segments.Append((0xFFFF, 0xFFFF)).ToList();
        var w = new BigEndianWriter(16 + 8 * list.Count);
        w.U16(4);
        w.U16(Math.Min(0xFFFF, 16 + 8 * list.Count));
        w.U16(0);
        w.U16(list.Count * 2);
        w.U16(0);
        w.U16(0);
        w.U16(0);
        foreach (var (_, end) in list)
            w.U16(end);
        w.U16(0);
        foreach (var (start, _) in list)
            w.U16(start);
        foreach (var _ in list)
            w.U16(1);
        foreach (var _ in list)
            w.U16(0);
        return w.ToArray();
    }
}
