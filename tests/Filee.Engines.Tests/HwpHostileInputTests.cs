// Hostile HWP 5.0 files and HWPX packages for the HWP ↔ HWPX writers (Hwp/Binary) and the HWPX reader's text: BinData
// records that all name one stream or too many streams, paragraphs with a highlighter range or a character shape for
// nearly every character, a control with a million unknown members, runs of a million small texts, and the temporary
// file the writers build the output in. Each must be read or refused within the limits, quickly.

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Binary;
using Filee.Engines.Hwp.Hwpx;
using static Filee.Engines.Tests.HwpBuilder;

namespace Filee.Engines.Tests;

public sealed partial class HwpHostileInputTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string HwpNamespaces =
        "xmlns:hp=\"http://www.hancom.co.kr/hwpml/2011/paragraph\" xmlns:hs=\"http://www.hancom.co.kr/hwpml/2011/section\" xmlns:hc=\"http://www.hancom.co.kr/hwpml/2011/core\"";

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>Runs <paramref name="action"/> and returns what it threw; fails when it takes longer than <paramref name="seconds"/>.</summary>
    private static Exception? Within(Action action, int seconds = 30)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, 16 * 1024 * 1024);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(seconds)), $"Still running after {seconds} s");
        return error;
    }

    private string Write(HwpBuilder builder)
    {
        var path = Path.Combine(fx.NewFolder(), "input.hwp");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private static string Part(string package, string name)
    {
        using var zip = ZipFile.OpenRead(package);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    /// <summary>An HWPX package written by Filee whose section0.xml holds <paramref name="paragraphs"/>.</summary>
    private string Hwpx(string paragraphs)
    {
        var folder = fx.NewFolder();
        var document = new HDocument();
        var paragraph = new HParagraph();
        paragraph.Inlines.Add(new HText("seed", default));
        document.Sections.Add(new HSection { Blocks = { paragraph } });
        var seed = Path.Combine(folder, "seed.hwpx");
        HwpxWriter.Write(document, seed);
        var target = Path.Combine(folder, "input.hwpx");
        using (var input = ZipFile.OpenRead(seed))
        using (var output = ZipFile.Open(target, ZipArchiveMode.Create))
        {
            foreach (var entry in input.Entries)
            {
                // Stored: the repeated markup would pass the inflate ratio once over its floor.
                using var to = output.CreateEntry(entry.FullName, CompressionLevel.NoCompression).Open();
                if (entry.FullName == "Contents/section0.xml")
                    to.Write(Encoding.UTF8.GetBytes($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><hs:sec {HwpNamespaces}>{paragraphs}</hs:sec>"));
                else
                {
                    using var from = entry.Open();
                    from.CopyTo(to);
                }
            }
        }
        return target;
    }

    /// <summary>A paragraph of <paramref name="text"/> with these character shapes and highlighter ranges.</summary>
    private static IEnumerable<byte[]> Paragraph(string text, IEnumerable<(int Position, int Shape)> shapes, IEnumerable<(int Start, int End, uint Tag)> ranges)
    {
        var shapeBytes = shapes.SelectMany(s => Bytes(s.Position, s.Shape)).ToArray();
        var rangeBytes = ranges.SelectMany(r => Bytes(r.Start, r.End, r.Tag)).ToArray();
        yield return Record(66, 0, Bytes(text.Length + 1, 0, (ushort)0, (byte)0, (byte)0, (ushort)(shapeBytes.Length / 8), (ushort)(rangeBytes.Length / 12), (ushort)0, 0, (ushort)0));
        yield return Record(67, 1, Encoding.Unicode.GetBytes(text + "\r"));
        yield return Record(68, 1, shapeBytes);
        if (rangeBytes.Length > 0)
            yield return Record(70, 1, rangeBytes);
    }

    // ───────────────────────── BinData ─────────────────────────

    [Fact]
    public void BinData_records_naming_one_stream_write_it_once()
    {
        var builder = HwpFuzzer.Rich();
        // Twenty thousand more records, all naming BIN0001.png (the stream name compares without case).
        for (var i = 0; i < 20_000; i++)
            builder.DocInfo.Add(Record(18, 1, Bytes((ushort)1, (ushort)1, i % 2 == 0 ? "png" : "PNG")));
        var input = Write(builder);
        var output = Path.Combine(fx.NewFolder(), "output.hwpx");

        Assert.Null(Within(() => HwpReader.ToHwpx(input, output)));

        using var zip = ZipFile.OpenRead(output);
        Assert.Single(zip.Entries, e => e.FullName.StartsWith("BinData/", StringComparison.Ordinal));
        Assert.Single(Regex.Matches(Part(output, "Contents/content.hpf"), "href=\"BinData/"));
        // The picture names the one item.
        Assert.Contains("binaryItemIDRef=\"image1\"", Part(output, "Contents/section0.xml"));
    }

    [Fact]
    public void A_compressed_stream_named_by_many_records_is_inflated_once()
    {
        // A 2 MB stream of zeros (a few KB packed) named by 2,000 records: 4 GB if each record inflated it again.
        var builder = new HwpBuilder { Flags = 1 }.PlainDocInfo();
        for (var i = 0; i < 2_000; i++)
            builder.DocInfo.Add(Record(18, 1, Bytes((ushort)1, (ushort)1, "png")));
        builder.Streams["BinData/BIN0001.png"] = Pack(new byte[2 * 1024 * 1024], compress: true);
        builder.SectionStart().Paragraph("text");
        var input = Write(builder);
        var output = Path.Combine(fx.NewFolder(), "output.hwpx");

        Assert.Null(Within(() => HwpReader.ToHwpx(input, output, DocumentLimits.Default with { MaxPackageBytes = 64L * 1024 * 1024 })));
        using var zip = ZipFile.OpenRead(output);
        Assert.Equal(2 * 1024 * 1024, Assert.Single(zip.Entries, e => e.FullName.StartsWith("BinData/", StringComparison.Ordinal)).Length);
    }

    [Fact]
    public void More_embedded_streams_than_a_package_may_hold_are_refused_and_leave_nothing_behind()
    {
        var builder = new HwpBuilder().PlainDocInfo();
        for (var i = 1; i <= 51; i++)
            builder.DocInfo.Add(Record(18, 1, Bytes((ushort)1, (ushort)i, "png")));
        builder.SectionStart().Paragraph("text");
        var input = Write(builder);
        var folder = fx.NewFolder();
        var output = Path.Combine(folder, "output.hwpx");

        var refusal = Assert.IsType<ConversionRefusedException>(Within(() => HwpReader.ToHwpx(input, output, DocumentLimits.Default with { MaxPackageEntries = 50 })));
        Assert.Equal(DocumentLimits.ErrorKey, refusal.ErrorKey);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    // ───────────────────────── Paragraph text ─────────────────────────

    [Fact]
    public void Highlighter_ranges_open_and_close_where_they_start_and_end()
    {
        // Out of record order, a range of nothing, a range that never ends and a range of another kind (bookmark).
        var builder = new HwpBuilder().PlainDocInfo().SectionStart();
        builder.Section.AddRange(Paragraph("abcdef", [(0, 0)],
            [(4, 6, 0x020000FF), (0, 2, 0x02FF0000), (2, 4, 0x0200FF00), (1, 1, 0x02000000), (5, 9, 0x02FFFFFF), (0, 6, 0x01000000)]));
        var output = Path.Combine(fx.NewFolder(), "output.hwpx");
        HwpReader.ToHwpx(Write(builder), output);

        var section = Part(output, "Contents/section0.xml");
        var paragraph = section[section.LastIndexOf("<hp:p ", StringComparison.Ordinal)..];
        var marks = MarkpenOrText().Replace(paragraph, m => m.Groups["color"].Success ? $"[{m.Groups["color"].Value}" : m.Value.StartsWith("<hp:markpenEnd", StringComparison.Ordinal) ? "]" : "");
        Assert.Equal("[#0000FFab][#00FF00cd][#FF0000e[#FFFFFFf]", marks);
    }

    [GeneratedRegex("<hp:markpenBegin color=\"(?<color>[^\"]+)\" ?/>|<[^>]+>")]
    private static partial Regex MarkpenOrText();

    [Fact]
    public void A_paragraph_with_a_shape_and_a_range_for_nearly_every_character_is_written_quickly()
    {
        const int Length = 300_000;
        var builder = new HwpBuilder().PlainDocInfo().SectionStart();
        builder.Section.AddRange(Paragraph(new string('a', Length),
            Enumerable.Range(0, Length / 3).Select(i => (i * 3, 0)),
            Enumerable.Range(0, Length / 3).Select(i => (i * 3, i * 3 + 1, 0x02FFFF00u))));
        var input = Write(builder);
        var output = Path.Combine(fx.NewFolder(), "output.hwpx");

        Assert.Null(Within(() => HwpReader.ToHwpx(input, output)));
        var section = Part(output, "Contents/section0.xml");
        Assert.Equal(Length / 3, Regex.Count(section, "<hp:markpenBegin "));
        Assert.Equal(Length / 3, Regex.Count(section, "<hp:markpenEnd ?/>"));
    }

    [Fact]
    public void A_control_with_a_million_unknown_members_is_written_quickly_and_lines_still_map()
    {
        const int Members = 1_000_000;
        // Each unknown member takes eight positions of the package's text and none of the HWP's.
        var members = string.Concat(Enumerable.Repeat("<hp:x/>", Members));
        var hwpx = Hwpx("<hp:p paraPrIDRef=\"0\" styleIDRef=\"0\"><hp:run charPrIDRef=\"0\"><hp:ctrl>" + members + "</hp:ctrl><hp:t>abc</hp:t></hp:run>" +
            $"<hp:linesegarray><hp:lineseg textpos=\"0\" vertpos=\"0\" vertsize=\"1000\" textheight=\"1000\" baseline=\"850\" spacing=\"600\" horzpos=\"0\" horzsize=\"42520\" flags=\"393216\"/>" +
            $"<hp:lineseg textpos=\"{Members * 8 + 1}\" vertpos=\"1600\" vertsize=\"1000\" textheight=\"1000\" baseline=\"850\" spacing=\"600\" horzpos=\"0\" horzsize=\"42520\" flags=\"393216\"/></hp:linesegarray></hp:p>");
        var hwp = Path.Combine(fx.NewFolder(), "output.hwp");

        Assert.Null(Within(() => HwpxToHwp.Write(hwpx, hwp)));
        var back = Path.Combine(fx.NewFolder(), "back.hwpx");
        HwpReader.ToHwpx(hwp, back);
        var section = Part(back, "Contents/section0.xml");
        Assert.Contains("<hp:lineseg textpos=\"0\" ", section);
        Assert.Contains("<hp:lineseg textpos=\"1\" ", section);
    }

    [Fact]
    public void A_run_of_a_million_small_texts_is_read_quickly_as_one_text()
    {
        const int Spaces = 1_000_000;
        var hwpx = Hwpx("<hp:p paraPrIDRef=\"0\" styleIDRef=\"0\"><hp:run charPrIDRef=\"0\"><hp:t>a" + string.Concat(Enumerable.Repeat("<hp:nbSpace/>", Spaces)) + "b</hp:t></hp:run></hp:p>");

        foreach (var read in new Func<string, string, HDocument>[] { (path, media) => HwpxReader.Read(path, media), (path, media) => HwpxReader.ReadForLayout(path, media) })
        {
            HDocument? document = null;
            Assert.Null(Within(() => document = read(hwpx, Path.Combine(fx.NewFolder(), "media"))));
            var text = Assert.Single(document!.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HText>());
            Assert.Equal("a" + new string('\u00A0', Spaces) + "b", text.Text);
        }
    }

    // ───────────────────────── Temporary files ─────────────────────────

    [Fact]
    public void The_writers_leave_a_file_named_like_their_temporary_one_alone()
    {
        var folder = fx.NewFolder();
        var hwpx = Path.Combine(folder, "output.hwpx");
        var hwp = Path.Combine(folder, "output.hwp");
        File.WriteAllText(hwpx + ".tmp", "keep");
        File.WriteAllText(hwp + ".tmp", "keep");

        HwpReader.ToHwpx(Write(new HwpBuilder().PlainDocInfo().SectionStart().Paragraph("text")), hwpx);
        HwpxToHwp.Write(hwpx, hwp);

        Assert.Equal("keep", File.ReadAllText(hwpx + ".tmp"));
        Assert.Equal("keep", File.ReadAllText(hwp + ".tmp"));
        Assert.Equal(["output.hwp", "output.hwp.tmp", "output.hwpx", "output.hwpx.tmp"], Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }
}
