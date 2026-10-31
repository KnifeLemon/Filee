// HWP 5.0 → document model (Hwp/Binary): documents written as HWPX in code are made binary HWP by `rhwp convert`
// and must read into the same model as the HWPX they came from; small HWP files built in code (HwpBuilder) check the
// records one by one, the documents Filee refuses (password, distribution, DRM, HWP 3.x) and the HWP → HWPX package.
// Hostile compound files and records are in DocumentHostileInputTests, mutated ones in DocumentFuzzTests.

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Binary;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Tests.RenderQuality;
using static Filee.Engines.Tests.HwpBuilder;

namespace Filee.Engines.Tests;

public class HwpReaderTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private string Write(HwpBuilder builder, string name = "built.hwp")
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private static HDocument Read(string path) => HwpReader.Read(path, Path.Combine(Path.GetDirectoryName(path)!, "media"));

    private static string Text(HDocument document) =>
        string.Join("\n", document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().Select(p => string.Concat(p.Inlines.OfType<HText>().Select(t => t.Text))));

    // ───────────────────────── HWP from rhwp ─────────────────────────

    /// <summary>
    /// The documents of the acceptance check: Filee's rich DOCX and the render features, written by HwpxWriter. (The
    /// hand-made 한글-style package of HwpxReaderTests is not one: rhwp drops its note shapes.)
    /// </summary>
    private List<string> HwpxSources(string folder)
    {
        var rich = Path.Combine(folder, "rich.hwpx");
        HwpxWriter.Write(DocxReader.Read(DocumentFuzzTests.RichDocx(folder), Path.Combine(folder, "docx-media")), rich);
        List<string> sources = [rich];
        sources.AddRange(RenderFeatures.All.Select(feature => feature.Write(Path.Combine(folder, feature.Name))));
        return sources;
    }

    /// <summary>
    /// What rhwp's HWP doesn't carry over from an HWPX, removed from both models: the title (rhwp writes no summary
    /// information), numbering levels 8 to 10 (HWP keeps seven in the record rhwp writes) and the empty levels HWP
    /// always has where the source defines fewer.
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
        document.Title = null;
        foreach (var section in document.Sections)
        {
            var blocks = section.Blocks.Concat(section.Headers.SelectMany(h => h.Blocks)).Concat(section.Footers.SelectMany(f => f.Blocks));
            foreach (var paragraph in Paragraphs(blocks))
            {
                if (paragraph.List?.Numbering.Levels is not { } levels)
                    continue;
                if (levels.Count > 7)
                    levels.RemoveRange(7, levels.Count - 7);
                // HWP has every level; the source may define fewer.
                while (levels.Count > 1 && levels[^1].Text.Length == 0)
                    levels.RemoveAt(levels.Count - 1);
            }
        }
        return HDocumentAssert.Dump(document);
    }

    [Fact]
    public async Task Hwp_files_made_by_rhwp_read_into_the_model_of_their_hwpx_source()
    {
        Assert.SkipUnless(DocumentRenderers.RhwpExecutable() is not null, "rhwp not available");
        var folder = fx.NewFolder();
        foreach (var hwpx in HwpxSources(folder))
        {
            var hwp = await DocumentRenderers.ConvertToHwpAsync(hwpx, TestContext.Current.CancellationToken);
            var media = Path.Combine(folder, Path.GetFileNameWithoutExtension(hwpx));
            var skippedInHwpx = new Dictionary<string, int>();
            var expected = HwpxReader.Read(hwpx, media + "-hwpx", skippedInHwpx);
            var skipped = new Dictionary<string, int>();
            var actual = HwpReader.Read(hwp, media + "-hwp", skipped);
            // What the model can't hold is the same kind of thing (rhwp may write a picture the source lacks twice).
            Assert.Equal(skippedInHwpx.Keys.Order(), skipped.Keys.Order());
            Assert.Equal(Comparable(expected), Comparable(actual));

            // Read for layout: the lines rhwp stores are its own estimate, not 한글's, and are marked so.
            var layout = HwpReader.ReadForLayout(hwp, media + "-hwp-layout");
            Assert.All(layout.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>(), p => Assert.True(p.Lines is null or { Estimated: true }));
        }
    }

    // ───────────────────────── Records ─────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Text_paragraph_shapes_and_the_page_are_read(bool compressed)
    {
        var builder = new HwpBuilder { Flags = compressed ? 1u : 0 }.PlainDocInfo().SectionStart(landscape: true)
            .Paragraph("한글 문서 파일 형식").Paragraph("두 번째 문단");
        var document = Read(Write(builder));

        var section = Assert.Single(document.Sections);
        Assert.Equal(new HPage(84188, 59528, 8504, 8504, 5668, 4252, 4252, 4252, 0), section.Page);
        Assert.Equal("\n한글 문서 파일 형식\n두 번째 문단", Text(document));
        var paragraph = section.Blocks.OfType<HParagraph>().Last();
        Assert.Equal(HAlign.Left, paragraph.Format.Align);
        Assert.Equal(new HLineSpacing(HLineSpacingKind.Percent, 160), paragraph.Format.LineSpacing);
        Assert.Equal(1000, Assert.IsType<HText>(Assert.Single(paragraph.Inlines)).Format.Size);
        Assert.Equal("함초롬바탕", Assert.IsType<HText>(Assert.Single(paragraph.Inlines)).Format.EastAsianFont);
    }

    [Fact]
    public void Stored_lines_reach_the_layout_reader()
    {
        var builder = new HwpBuilder().PlainDocInfo().SectionStart().Paragraph("줄");
        var path = Write(builder);
        var paragraph = HwpReader.ReadForLayout(path, Path.Combine(Path.GetDirectoryName(path)!, "media")).Sections[0].Blocks.OfType<HParagraph>().Last();
        var segment = Assert.Single(paragraph.Lines!.Segments);
        Assert.Equal((1000, 850, 42520), (segment.VerticalSize, segment.Baseline, segment.HorizontalSize));
        Assert.False(paragraph.Lines.Estimated);
    }

    [Fact]
    public void Margins_are_stored_doubled_and_read_at_their_size()
    {
        // 30 pt left margin, 10 pt before: HWP stores 6000 and 2000.
        var builder = new HwpBuilder().PlainDocInfo();
        builder.DocInfo[^2] = Record(25, 1, Bytes(1 << 2, 6000, 0, -2000, 2000, 0, 160, (ushort)0, (ushort)0, (ushort)1, (short)0, (short)0, (short)0, (short)0, 0, 0, 160));
        var format = Read(Write(builder.SectionStart().Paragraph("들여쓰기"))).Sections[0].Blocks.OfType<HParagraph>().Last().Format;
        Assert.Equal((4000, -1000, 1000), (format.Left, format.FirstLine, format.Before)); // hanging indent: left grows by it
    }

    [Fact]
    public void Hwpx_named_hwp_is_read_as_hwpx()
    {
        var folder = fx.NewFolder();
        var hwp = Path.Combine(folder, "really-hwpx.hwp");
        File.Copy(HwpxReaderTests.HangulStylePackage(folder), hwp);
        Assert.Equal("한글 문서 제목", Read(hwp).Title);
    }

    // ───────────────────────── Refused documents ─────────────────────────

    [Theory]
    [InlineData(0x2u, "password")]
    [InlineData(0x4u, "distribution")]
    [InlineData(0x10u, "DRM")]
    public void Protected_documents_are_refused_with_a_reason(uint flags, string reason)
    {
        var path = Write(new HwpBuilder { Flags = flags }.PlainDocInfo().SectionStart().Paragraph("비밀"));
        Assert.Contains(reason, Assert.Throws<InvalidDataException>(() => Read(path)).Message);
    }

    [Fact]
    public void Distribution_documents_are_refused_by_their_encrypted_streams_too()
    {
        var builder = new HwpBuilder().PlainDocInfo().SectionStart();
        builder.Streams["ViewText/Section0"] = new byte[300];
        Assert.Contains("distribution", Assert.Throws<InvalidDataException>(() => Read(Write(builder))).Message);
    }

    [Fact]
    public void Old_and_foreign_files_are_refused_with_a_reason()
    {
        var folder = fx.NewFolder();
        var hwp3 = Path.Combine(folder, "old.hwp");
        File.WriteAllBytes(hwp3, [.. "HWP Document File V3.00 \x1A\x01\x02\x03\x04\x05"u8, .. new byte[200]]);
        Assert.Contains("HWP 3", Assert.Throws<InvalidDataException>(() => Read(hwp3)).Message);

        Assert.Contains("version 4", Assert.Throws<InvalidDataException>(() => Read(Write(new HwpBuilder { Version = 0x04000000 }.PlainDocInfo()))).Message);

        var builder = new HwpBuilder().PlainDocInfo();
        builder.Streams["WordDocument"] = new byte[600];
        var foreign = Write(builder);
        // A compound file without an HWP file header: overwrite the signature.
        var bytes = File.ReadAllBytes(foreign);
        var at = bytes.AsSpan().IndexOf("HWP Document File"u8);
        bytes[at] = (byte)'X';
        File.WriteAllBytes(foreign, bytes);
        Assert.Contains("not an HWP document", Assert.Throws<InvalidDataException>(() => Read(foreign)).Message);
    }

    // ───────────────────────── HWP → HWPX ─────────────────────────

    [Fact]
    public void Hwp_routes_take_the_built_in_reader_in_the_default_engine_order()
    {
        var catalog = new ConverterCatalog(fx.Converters) { Priority = new Filee.Core.Settings.AppSettings().EnginePriority };
        var planner = catalog.CreatePlanner(["rhwp", "libreoffice", "pandoc"]);
        Assert.Equal(["hwpx-writer"], planner.Plan("hwp", "hwpx")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal(["docx-writer"], planner.Plan("hwp", "docx")!.Steps.Select(s => s.Converter.Id));
        // rhwp stays the HWP → PDF engine.
        Assert.Equal(["rhwp"], planner.Plan("hwp", "pdf")!.Steps.Select(s => s.Converter.Id));
    }

    [Fact]
    public async Task Hwp_becomes_an_hwpx_package_without_rhwp()
    {
        var folder = fx.NewFolder();
        var hwp = Write(new HwpBuilder { Flags = 1 }.PlainDocInfo().SectionStart().Paragraph("변환"));
        var writer = fx.Converters.Single(c => c.Id == "hwpx-writer");
        Assert.Contains(writer.Edges, e => e is { From: "hwp", To: "hwpx" });

        var output = Path.Combine(folder, "out.hwpx");
        var written = await writer.ConvertAsync(new ConversionStep(hwp, "hwp", "hwpx", new Preset(), new HwpxAssert.FixedPath(output), folder), null, TestContext.Current.CancellationToken);
        Assert.Equal(output, Assert.Single(written));
        using (var zip = ZipFile.OpenRead(output))
        {
            Assert.Equal("mimetype", zip.Entries[0].FullName);
            Assert.NotNull(zip.GetEntry("Contents/header.xml"));
            Assert.NotNull(zip.GetEntry("Contents/section0.xml"));
        }
        Assert.Equal(Text(Read(hwp)), Text(HwpxReader.Read(output, Path.Combine(folder, "media"))));
    }
}
