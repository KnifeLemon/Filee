// HWPX → HWP 5.0 (Hwp/Binary/HwpxToHwp) and the compound file writer under it. The documents of the HWP reader's
// acceptance check (the rich DOCX and the render features, written by HwpxWriter) and a 한글-style package with stored
// line layouts become HWP files that Filee's HWP reader must read into the model of their HWPX; HWP → HWPX → HWP gives
// the records back; rhwp, while bundled, must open the files too. The compound file writer is checked against the
// reader with streams on both sides of the mini stream cutoff and with more FAT sectors than the header lists (DIFAT).

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Binary;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Tests.RenderQuality;

namespace Filee.Engines.Tests;

public class HwpWriterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>The rich DOCX and the render features as HWPX, written by HwpxWriter, and a 한글-style package.</summary>
    private static List<string> Sources(string folder)
    {
        var rich = Path.Combine(folder, "rich.hwpx");
        HwpxWriter.Write(DocxReader.Read(DocumentFuzzTests.RichDocx(folder), Path.Combine(folder, "docx-media")), rich);
        List<string> sources = [rich, HwpxReaderTests.HangulStylePackage(folder)];
        sources.AddRange(RenderFeatures.All.Select(feature => feature.Write(Path.Combine(folder, feature.Name))));
        return sources;
    }

    /// <summary>
    /// The model dump without what HWP always has and HWPX may leave out: numbering levels the source doesn't define
    /// (HWP stores ten) and a table's inner margins (a table without hp:inMargin has none in HWP).
    /// </summary>
    private static string Comparable(HDocument document)
    {
        static IEnumerable<HParagraph> Paragraphs(IEnumerable<HBlock> blocks) => blocks.SelectMany(block => block switch
        {
            HParagraph p => p.Inlines.SelectMany(i => i switch
            {
                HNote note => Paragraphs(note.Blocks),
                HTextBox box => Paragraphs(box.Blocks),
                _ => [],
            }).Prepend(p),
            HTable t => Paragraphs(t.Caption).Concat(t.Rows.SelectMany(r => r.Cells).SelectMany(c => Paragraphs(c.Blocks))),
            _ => [],
        });
        foreach (var section in document.Sections)
        {
            var blocks = section.Blocks.Concat(section.Headers.SelectMany(h => h.Blocks)).Concat(section.Footers.SelectMany(f => f.Blocks));
            foreach (var paragraph in Paragraphs(blocks))
            {
                if (paragraph.List?.Numbering.Levels is not { } levels)
                    continue;
                while (levels.Count > 1 && levels[^1].Text.Length == 0)
                    levels.RemoveAt(levels.Count - 1);
            }
        }
        return HDocumentAssert.Dump(document).Replace("margin=HInsets { Left = 0, Right = 0, Top = 0, Bottom = 0 }", "margin=", StringComparison.Ordinal);
    }

    private static string Write(string hwpx)
    {
        var hwp = Path.ChangeExtension(hwpx, ".hwp");
        HwpxToHwp.Write(hwpx, hwp);
        return hwp;
    }

    // ───────────────────────── Round trips ─────────────────────────

    [Fact]
    public void Hwp_written_from_hwpx_reads_into_the_model_of_the_hwpx()
    {
        var folder = fx.NewFolder();
        foreach (var hwpx in Sources(folder))
        {
            var hwp = Write(hwpx);
            var media = Path.Combine(folder, Path.GetFileNameWithoutExtension(hwpx));
            var skippedInHwpx = new Dictionary<string, int>();
            var skipped = new Dictionary<string, int>();
            var expected = HwpxReader.Read(hwpx, media + "-hwpx", skippedInHwpx);
            var actual = HwpReader.Read(hwp, media + "-hwp", skipped);
            Assert.Equal(skippedInHwpx.Keys.Order(), skipped.Keys.Order());
            Assert.Equal(Comparable(expected), Comparable(actual));

            // Read for layout: master pages, shapes and 한글's stored line layout come through too.
            Assert.Equal(Comparable(HwpxReader.ReadForLayout(hwpx, media + "-hwpx-layout")), Comparable(HwpReader.ReadForLayout(hwp, media + "-hwp-layout")));
        }
    }

    [Fact]
    public void Stored_line_layouts_survive_and_estimated_ones_are_left_out()
    {
        var folder = fx.NewFolder();
        var hwpx = HwpxReaderTests.HangulStylePackage(folder);
        var lines = HwpxReader.ReadForLayout(hwpx, Path.Combine(folder, "a")).Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().Count(p => p.Lines is not null);
        Assert.True(lines > 0);
        var hwp = Write(hwpx);
        Assert.Equal(lines, HwpReader.ReadForLayout(hwp, Path.Combine(folder, "b")).Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().Count(p => p.Lines is { Estimated: false }));

        // A package that says its lines are an estimate (FileeLineSegments) gets none: 한글 lays the text out itself.
        var estimated = Path.Combine(folder, "estimated.hwpx");
        File.Copy(hwpx, estimated);
        using (var zip = ZipFile.Open(estimated, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("Contents/content.hpf")!;
            string text;
            using (var reader = new StreamReader(entry.Open()))
                text = reader.ReadToEnd();
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("Contents/content.hpf").Open());
            writer.Write(text.Replace("</opf:metadata>", $"<opf:meta name=\"{HwpxReader.EstimatedLinesMeta}\" content=\"estimated\"/></opf:metadata>", StringComparison.Ordinal));
        }
        Assert.All(HwpReader.ReadForLayout(Write(estimated), Path.Combine(folder, "c")).Sections.SelectMany(s => s.Blocks).OfType<HParagraph>(), p => Assert.Null(p.Lines));
    }

    [Fact]
    public void Hwp_to_hwpx_to_hwp_keeps_the_document()
    {
        var folder = fx.NewFolder();
        var hwp = Path.Combine(folder, "built.hwp");
        File.WriteAllBytes(hwp, new HwpBuilder { Flags = 1 }.PlainDocInfo().SectionStart(landscape: true).Paragraph("한글 문서").Paragraph("둘째 문단").Build());
        var hwpx = Path.Combine(folder, "built.hwpx");
        HwpReader.ToHwpx(hwp, hwpx);
        var again = Write(hwpx);
        Assert.Equal(HDocumentAssert.Dump(HwpReader.ReadForLayout(hwp, Path.Combine(folder, "a"))), HDocumentAssert.Dump(HwpReader.ReadForLayout(again, Path.Combine(folder, "b"))));
    }

    [Fact]
    public void The_file_has_the_streams_hangul_writes()
    {
        var folder = fx.NewFolder();
        var document = DocxReader.Read(DocumentFuzzTests.RichDocx(folder), Path.Combine(folder, "docx-media"));
        document.Title = "보고서 제목";
        var hwpx = Path.Combine(folder, "titled.hwpx");
        HwpxWriter.Write(document, hwpx);
        var hwp = Write(hwpx);
        using var cfb = Filee.Engines.Documents.CompoundFile.Open(hwp);
        Assert.Equal(
            ["\u0005HwpSummaryInformation", "BinData/BIN0001.png", "BodyText/Section0", "BodyText/Section1", "DocInfo", "DocOptions/_LinkDoc", "FileHeader",
             "PrvImage", "PrvText", "Scripts/DefaultJScript", "Scripts/JScriptVersion"],
            cfb.Streams.Order(StringComparer.Ordinal));
        var header = cfb.Read("FileHeader")!;
        Assert.Equal("HWP Document File", System.Text.Encoding.ASCII.GetString(header, 0, 17));
        Assert.Equal(HwpxToHwp.FormatVersion, BitConverter.ToUInt32(header, 32));
        Assert.Equal(1u, BitConverter.ToUInt32(header, 36)); // compressed, nothing else

        // Compressed streams end like 한글's: the CRC-32 and the length of what they hold.
        var version = cfb.Read("Scripts/JScriptVersion")!;
        Assert.Equal(8u, BitConverter.ToUInt32(version, version.Length - 4));
        using var file = HwpFile.Open(hwp, Documents.DocumentLimits.Default);
        Assert.Equal("보고서 제목", file.Title());
    }

    // ───────────────────────── Routes ─────────────────────────

    [Fact]
    public void Hwp_output_takes_the_built_in_writer_in_the_default_engine_order()
    {
        var catalog = new ConverterCatalog(fx.Converters) { Priority = new Filee.Core.Settings.AppSettings().EnginePriority };
        var planner = catalog.CreatePlanner(["rhwp", "libreoffice", "pandoc"]);
        Assert.Equal(["hwpx-writer"], planner.Plan("hwpx", "hwp")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal(["hwpx-writer", "hwpx-writer"], planner.Plan("docx", "hwp")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal(["hwpx-writer", "hwpx-writer"], planner.Plan("md", "hwp")!.Steps.Select(s => s.Converter.Id));
    }

    [Fact]
    public async Task The_converter_writes_hwp_from_hwpx()
    {
        var folder = fx.NewFolder();
        var hwpx = Sources(folder)[0];
        var writer = fx.Converters.Single(c => c.Id == "hwpx-writer");
        var output = Path.Combine(folder, "out.hwp");
        var written = await writer.ConvertAsync(new ConversionStep(hwpx, "hwpx", "hwp", new Preset(), new HwpxAssert.FixedPath(output), folder), null, TestContext.Current.CancellationToken);
        Assert.Equal(output, Assert.Single(written));
        Assert.Equal(Comparable(HwpxReader.Read(hwpx, Path.Combine(folder, "a"))), Comparable(HwpReader.Read(output, Path.Combine(folder, "b"))));
    }

    [Fact]
    public void Damaged_and_foreign_packages_are_refused()
    {
        var folder = fx.NewFolder();
        var notZip = Path.Combine(folder, "plain.hwpx");
        File.WriteAllText(notZip, "not a package");
        Assert.Throws<InvalidDataException>(() => HwpxToHwp.Write(notZip, Path.Combine(folder, "a.hwp")));

        var empty = Path.Combine(folder, "empty.hwpx");
        using (var zip = ZipFile.Open(empty, ZipArchiveMode.Create))
            zip.CreateEntry("mimetype");
        Assert.Throws<InvalidDataException>(() => HwpxToHwp.Write(empty, Path.Combine(folder, "b.hwp")));
        Assert.False(File.Exists(Path.Combine(folder, "b.hwp")));
    }

    // ───────────────────────── rhwp ─────────────────────────

    [Fact]
    public async Task Rhwp_reads_the_hwp_files()
    {
        Assert.SkipUnless(DocumentRenderers.RhwpExecutable() is not null, "rhwp not available");
        var exe = DocumentRenderers.RhwpExecutable()!;
        var folder = fx.NewFolder();
        foreach (var hwpx in Sources(folder))
        {
            var hwp = Write(hwpx);
            var exported = Path.ChangeExtension(hwpx, ".rhwp.hwpx");
            ProcessResult result;
            try
            {
                result = await ProcessRunner.RunAsync(exe, ["export-hwpx", hwp, exported], TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"rhwp hangs on {Path.GetFileName(hwp)}", ex);
            }
            Assert.True(File.Exists(exported), $"rhwp could not read {Path.GetFileName(hwp)}: {result.StandardError}");
            Assert.Equal(Text(HwpxReader.Read(hwpx, Path.Combine(folder, "a"))), Text(HwpxReader.Read(exported, Path.Combine(folder, "b"))));
        }

        // rhwp shows a number in a circle (글자 겹침) as the circled digit HWP stores (⑫), Filee as the number.
        static string Text(HDocument document) => string.Concat(string.Concat(document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>()
                .SelectMany(p => p.Inlines.OfType<HText>()).Select(t => t.Text)).Replace(" ", "", StringComparison.Ordinal)
            .Select(c => c is >= '①' and <= '⑳' ? (c - '①' + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : c.ToString()));
    }

    // ───────────────────────── Compound file ─────────────────────────

    [Fact]
    public void Compound_files_keep_streams_on_both_sides_of_the_mini_stream_cutoff()
    {
        var streams = new Dictionary<string, byte[]>();
        var random = new Random(5);
        foreach (var size in (int[])[0, 1, 63, 64, 65, 511, 512, 4095, 4096, 4097, 100_000])
        {
            var bytes = new byte[size];
            random.NextBytes(bytes);
            streams[$"Data/S{size}"] = bytes;
        }
        // Enough members for a directory of several sectors and a deep tree of names.
        for (var i = 0; i < 300; i++)
            streams[$"Many/BIN{i:X4}.png"] = [(byte)i, (byte)(i >> 8)];
        streams["Root"] = [1, 2, 3];

        var writer = new CompoundFileWriter();
        foreach (var (path, bytes) in streams)
            writer.Add(path, bytes);
        using var output = new MemoryStream();
        writer.Write(output);
        Assert.Equal(0, output.Length % CompoundFileWriter.SectorSize);

        output.Position = 0;
        using var cfb = Filee.Engines.Documents.CompoundFile.Open(output);
        Assert.Equal(streams.Keys.Order(StringComparer.Ordinal), cfb.Streams.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in streams)
            Assert.Equal(bytes, cfb.Read(path));
        Assert.Equal(streams["Many/BIN012A.png"], cfb.Read("many/bin012a.PNG")); // names compare without case
    }

    [Fact]
    public void Compound_files_with_more_fat_sectors_than_the_header_lists_use_difat_sectors()
    {
        // 109 FAT sectors cover 109 × 128 sectors (6.8 MB); 20 MB needs two DIFAT sectors.
        var big = new byte[20 * 1024 * 1024];
        new Random(7).NextBytes(big);
        var writer = new CompoundFileWriter();
        writer.Add("Big", big);
        writer.Add("Small", [42]);
        using var output = new MemoryStream();
        writer.Write(output);

        var header = output.GetBuffer();
        var fatSectors = BitConverter.ToUInt32(header, 44);
        var difatSectors = BitConverter.ToUInt32(header, 72);
        Assert.True(fatSectors > 109);
        Assert.Equal(2u, difatSectors);
        Assert.True((long)fatSectors * 128 >= output.Length / CompoundFileWriter.SectorSize - 1); // the FAT covers every sector

        output.Position = 0;
        using var cfb = Filee.Engines.Documents.CompoundFile.Open(output, new Documents.DocumentLimits { MaxPartBytes = 32 * 1024 * 1024 });
        Assert.Equal(big, cfb.Read("Big"));
        Assert.Equal([42], cfb.Read("Small"));


    }

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(127, 1, 0)]
    [InlineData(128, 2, 0)]
    [InlineData(109 * 128 - 109, 109, 0)]
    [InlineData(109 * 128 - 108, 110, 1)]
    [InlineData(200 * 128, 202, 1)]
    public void Fat_and_difat_sizes_cover_themselves(int dataSectors, int fat, int difat)
    {
        Assert.Equal((fat, difat), CompoundFileWriter.TableSizes(dataSectors));
    }
}
