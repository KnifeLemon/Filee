// Mutation fuzzing of the document readers (HWPX for conversion and for layout, DOCX, HWP): seeds are written in
// code, DocumentFuzzer (packages) or HwpFuzzer (HWP records and compound files) changes them, and every input must be read or refused with InvalidDataException /
// ConversionRefusedException, in time. In a normal test run each reader gets a short, fixed run (the same inputs every
// time); the nightly fuzz workflow sets FILEE_FUZZ_SECONDS for a longer run with a new random seed, and
// FILEE_FUZZ_OUT for a folder that keeps the inputs that failed. FILEE_FUZZ_SEED repeats a run.

using System.Globalization;
using Filee.Engines.Hwp.Binary;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Hwp.Hwpx.Markup;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class DocumentFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Inputs per reader in a normal test run.</summary>
    private const int QuickIterations = 50;

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void HwpxReader_reads_or_refuses_every_mutated_package() =>
        Fuzz("hwpx", "hwpx", HwpxSeeds(), (path, media) => HwpxReader.Read(path, media));

    [Fact]
    public void HwpxReader_for_layout_reads_or_refuses_every_mutated_package() =>
        Fuzz("hwpx-layout", "hwpx", HwpxSeeds(), (path, media) => HwpxReader.ReadForLayout(path, media));

    [Fact]
    public void DocxReader_reads_or_refuses_every_mutated_package() =>
        Fuzz("docx", "docx", DocxSeeds(), (path, media) => DocxReader.Read(path, media));

    [Fact]
    public void HwpReader_reads_or_refuses_every_mutated_file() =>
        Fuzz("hwp", "hwp", HwpSeeds(), (path, media) => HwpReader.ReadForLayout(path, media), (seed, random) => new HwpFuzzer(seed, random).Next);

    [Fact]
    public void NotebookReader_reads_or_refuses_every_mutated_notebook() =>
        Fuzz("ipynb", "ipynb", [System.Text.Encoding.UTF8.GetBytes(NotebookTests.Analysis())], (path, media) => NotebookReader.Read(path, media),
            JsonMutator(["{", "}", "[", "]", ",", ":", "\"cells\"", "\"source\"", "\"outputs\"", "\"cell_type\": \"code\"", "\"cell_type\": \"markdown\"",
                  "\"output_type\": \"stream\"", "\"data\"", "\"image/png\"", "\"text/html\"", "\"attachments\"", "\"worksheets\"", "\\u001b[31m",
                  "\\n", "# ", "**", "![x](attachment:chart.png)", "<table><tr><td>", "1e999"]));

    [Fact]
    public void OpmlReader_reads_or_refuses_every_mutated_outline() =>
        Fuzz("opml", "opml", [System.Text.Encoding.UTF8.GetBytes(OpmlTests.Plan)], (path, media) => OpmlReader.Read(path, media),
            XmlMutator(["<outline text=\"x\">", "</outline>", "<outline/>", " type=\"link\" url=\"", " type=\"rss\" xmlUrl=\"", " _note=\"",
                         "&#10;", "&lt;b&gt;", "&amp;", "<!DOCTYPE opml [<!ENTITY a \"aaaa\">]>", "&a;", "<![CDATA[", "]]>", "<head>", "<body>"]));

    [Fact]
    public void TextileReader_reads_or_refuses_every_mutated_page() =>
        Fuzz("textile", "textile", [System.Text.Encoding.UTF8.GetBytes(TextileTests.Page)], (path, media) => TextileReader.Read(path, media),
            TextMutator(["h1. ", "p. ", "bq. ", "bc. ", "bc.. ", "fn1. ", "###. ", "notextile. ", "* ", "** ", "# ", "|", "|_. ", "|\\2. ",
                         "|/3. ", "table. ", "\"text\":http://x", "[\"t\":u]", "!img.png!", "[1]", "*", "_", "-", "+", "^", "~", "@", "%", "==",
                         "??", "(class)", "{color:red}", "[en]", "&#42;", "<br>", "- term := def"]));

    [Fact]
    public void MediaWikiReader_reads_or_refuses_every_mutated_page() =>
        Fuzz("mediawiki", "wiki", [System.Text.Encoding.UTF8.GetBytes(MediaWikiTests.Page)], (path, media) => MediaWikiReader.Read(path, media),
            TextMutator(["{|", "|}", "|-", "|+", "!", "!!", "||", "| colspan=\"99999\" |", "rowspan=\"2147483647\"", "{{", "}}", "[[", "]]",
                         "[[File:x.png|thumb]]", "[http://x y]", "''", "'''", "'''''", "== ", " ==", "* ", "# ", ": ", "; ", "----", "<ref>",
                         "</ref>", "<ref name=\"a\" />", "<nowiki>", "</nowiki>", "<pre>", "</pre>", "<blockquote>", "</blockquote>",
                         "<!--", "-->", "<b>", "</b>", "<sup>", "&lt;", "__TOC__", "[[Category:x]]"]));

    /// <summary>The mutator of a text format, with the format's own tokens.</summary>
    private static Func<byte[], Random, Func<byte[]>> TextMutator(string[] tokens) => (seed, random) => new TextFuzzer(seed, random, tokens).Next;

    /// <summary>The mutator of an XML format in one file: mostly the tree, sometimes the text.</summary>
    private static Func<byte[], Random, Func<byte[]>> XmlMutator(string[] tokens) => (seed, random) => new XmlFuzzer(seed, random, tokens).Next;

    /// <summary>The mutator of a JSON format: mostly the tree, sometimes the text.</summary>
    private static Func<byte[], Random, Func<byte[]>> JsonMutator(string[] tokens) => (seed, random) => new JsonFuzzer(seed, random, tokens).Next;

    // ───────────────────────── Seeds ─────────────────────────

    /// <summary>
    /// HWP files built in code (an HWP seed is the builder, so the fuzzer changes records and rebuilds the compound
    /// file): a plain document, one with a table, a note and a picture, and a compressed one.
    /// </summary>
    private static List<byte[]> HwpSeeds()
    {
        var plain = new HwpBuilder().PlainDocInfo().SectionStart().Paragraph("첫 문단").Paragraph("둘째 문단");
        var compressed = new HwpBuilder { Flags = 1 }.PlainDocInfo().SectionStart().Paragraph("압축된 문단");
        return [plain.Build(), HwpFuzzer.Rich().Build(), compressed.Build()];
    }

    private List<byte[]> HwpxSeeds()
    {
        var folder = fx.NewFolder();
        var hangul = HwpxReaderTests.HangulStylePackage(folder);
        // DOCX read and written as HWPX: what Filee's own writer makes.
        var written = Path.Combine(folder, "written.hwpx");
        HwpxWriter.Write(DocxReader.Read(RichDocx(folder), Path.Combine(folder, "media-docx")), written);
        return [File.ReadAllBytes(hangul), File.ReadAllBytes(written)];
    }

    private List<byte[]> DocxSeeds()
    {
        var folder = fx.NewFolder();
        var rich = RichDocx(folder);
        // The 한글-style package written as DOCX: what Filee's own writer makes.
        var written = Path.Combine(folder, "written.docx");
        DocxWriter.Write(HwpxReader.Read(HwpxReaderTests.HangulStylePackage(folder), Path.Combine(folder, "media-hwpx")), written);
        return [File.ReadAllBytes(rich), File.ReadAllBytes(written)];
    }

    /// <summary>
    /// A DOCX with most of what the reader handles: styles based on styles, a numbered list, a nested table with
    /// merged cells, inline and floating pictures, a text box with a VML fallback, a group, fields, a hyperlink,
    /// notes, a header and footer, math, a content control, a VML picture and a symbol.
    /// </summary>
    internal static string RichDocx(string folder)
    {
        var builder = new DocxBuilder
        {
            Numbering = """
                <w:abstractNum w:abstractNumId="0"><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/><w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr></w:lvl>
                <w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val=""/><w:rPr><w:rFonts w:ascii="Symbol"/></w:rPr></w:lvl></w:abstractNum>
                <w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
                """,
            Settings = "<w:evenAndOddHeaders/><w:noLineBreaksBefore w:lang=\"ko-KR\" w:val=\"!%),.:;?]}\"/>",
        };
        builder.Styles += """
            <w:style w:type="paragraph" w:styleId="Quote"><w:name w:val="Quote"/><w:basedOn w:val="Heading1"/><w:pPr><w:ind w:left="400"/><w:jc w:val="center"/></w:pPr><w:rPr><w:i/></w:rPr></w:style>
            <w:style w:type="paragraph" w:styleId="Loop"><w:name w:val="Loop"/><w:basedOn w:val="Loop2"/></w:style>
            <w:style w:type="paragraph" w:styleId="Loop2"><w:name w:val="Loop2"/><w:basedOn w:val="Loop"/></w:style>
            """;
        var png = builder.Image(TinyPng(4, 3));
        var link = builder.Hyperlink("https://example.invalid/page");
        var header = builder.Header(P(R("머리말 ") + Field("PAGE", "1")));
        var footer = builder.Footer(P(R("꼬리말 ") + Field("NUMPAGES", "3")));
        builder.Footnotes("<w:footnote w:id=\"1\"><w:p><w:r><w:footnoteRef/></w:r>" + R(" 각주 본문") + "</w:p></w:footnote>");
        builder.Endnotes("<w:endnote w:id=\"1\"><w:p><w:r><w:endnoteRef/></w:r>" + R(" 미주 본문") + "</w:p></w:endnote>");
        builder.SectionProperties = $"<w:headerReference w:type=\"default\" r:id=\"{header}\"/><w:footerReference w:type=\"default\" r:id=\"{footer}\"/>" +
                                    DefaultPage + "<w:cols w:num=\"2\" w:space=\"720\"/><w:docGrid w:type=\"lines\" w:linePitch=\"360\"/><w:titlePg/>";

        const string Cell = "<w:tc><w:tcPr><w:tcW w:w=\"2000\" w:type=\"dxa\"/></w:tcPr>{0}</w:tc>";
        var inner = "<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"1000\"/></w:tblGrid><w:tr>" +
                    string.Format(CultureInfo.InvariantCulture, Cell, P(R("안쪽 표"))) + "</w:tr></w:tbl>";
        var table = "<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblLook w:firstRow=\"1\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"2000\"/><w:gridCol w:w=\"2000\"/><w:gridCol w:w=\"2000\"/></w:tblGrid>" +
                    "<w:tr><w:trPr><w:tblHeader/><w:trHeight w:val=\"400\"/></w:trPr>" +
                    "<w:tc><w:tcPr><w:gridSpan w:val=\"2\"/><w:shd w:val=\"clear\" w:fill=\"FFFF00\"/></w:tcPr>" + P(R("병합")) + "</w:tc>" +
                    "<w:tc><w:tcPr><w:vMerge w:val=\"restart\"/><w:vAlign w:val=\"center\"/></w:tcPr>" + P(R("세로")) + "</w:tc></w:tr>" +
                    "<w:tr><w:trPr><w:cantSplit/></w:trPr>" + string.Format(CultureInfo.InvariantCulture, Cell, inner + P("")) +
                    string.Format(CultureInfo.InvariantCulture, Cell, P(R("칸"))) + "<w:tc><w:tcPr><w:vMerge/></w:tcPr>" + P("") + "</w:tc></w:tr></w:tbl>";
        var textBox = "<w:r><mc:AlternateContent><mc:Choice Requires=\"wps\"><w:drawing><wp:anchor distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\" simplePos=\"0\" relativeHeight=\"3\" behindDoc=\"0\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\">" +
                      "<wp:simplePos x=\"0\" y=\"0\"/><wp:positionH relativeFrom=\"column\"><wp:posOffset>100</wp:posOffset></wp:positionH><wp:positionV relativeFrom=\"paragraph\"><wp:align>top</wp:align></wp:positionV>" +
                      "<wp:extent cx=\"914400\" cy=\"457200\"/><wp:wrapSquare wrapText=\"bothSides\"/><wp:docPr id=\"3\" name=\"tb\"/><a:graphic><a:graphicData uri=\"" + ShapeUri + "\">" +
                      "<wps:wsp><wps:spPr><a:xfrm rot=\"5400000\"><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"457200\"/></a:xfrm><a:prstGeom prst=\"roundRect\"/><a:solidFill><a:srgbClr val=\"DDEEFF\"/></a:solidFill><a:ln w=\"12700\"><a:solidFill><a:schemeClr val=\"accent1\"/></a:solidFill></a:ln></wps:spPr>" +
                      "<wps:txbx><w:txbxContent>" + P(R("글상자")) + "</w:txbxContent></wps:txbx><wps:bodyPr lIns=\"0\" tIns=\"45720\" anchor=\"ctr\"/></wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing></mc:Choice>" +
                      "<mc:Fallback><w:pict><v:rect style=\"position:absolute;width:72pt;height:36pt;z-index:2\" fillcolor=\"#ddeeff\" strokecolor=\"red\" strokeweight=\"1pt\"><v:textbox><w:txbxContent>" + P(R("VML 글상자")) + "</w:txbxContent></v:textbox></v:rect></w:pict></mc:Fallback></mc:AlternateContent></w:r>";
        var group = Anchor("http://schemas.microsoft.com/office/word/2010/wordprocessingGroup",
            "<wpg:wgp><wpg:cNvGrpSpPr/><wpg:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"1828800\" cy=\"914400\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"1828800\" cy=\"914400\"/></a:xfrm></wpg:grpSpPr>" +
            Picture(png, 914400, 914400).Replace("<pic:pic>", "<pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">", StringComparison.Ordinal) +
            "<wps:wsp><wps:spPr><a:xfrm><a:off x=\"914400\" y=\"0\"/><a:ext cx=\"914400\" cy=\"914400\"/></a:xfrm><a:prstGeom prst=\"ellipse\"/></wps:spPr><wps:bodyPr/></wps:wsp>" +
            "<wpg:grpSp><wpg:grpSpPr><a:xfrm><a:off x=\"0\" y=\"457200\"/><a:ext cx=\"914400\" cy=\"457200\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"914400\" cy=\"457200\"/></a:xfrm></wpg:grpSpPr>" +
            "<wps:wsp><wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"457200\" cy=\"457200\"/></a:xfrm><a:prstGeom prst=\"rect\"/></wps:spPr><wps:txbx><w:txbxContent>" + P(R("묶음 안")) + "</w:txbxContent></wps:txbx><wps:bodyPr/></wps:wsp></wpg:grpSp></wpg:wgp>",
            1828800, 914400, "<wp:positionH relativeFrom=\"page\"><wp:align>center</wp:align></wp:positionH>", "<wp:positionV relativeFrom=\"page\"><wp:posOffset>914400</wp:posOffset></wp:positionV>");

        builder.Paragraph(P(R("제목") + "<w:bookmarkStart w:id=\"0\" w:name=\"top\"/><w:bookmarkEnd w:id=\"0\"/>", "<w:pStyle w:val=\"Heading1\"/>"))
            .Paragraph(P(R("인용", "<w:b/><w:color w:val=\"FF0000\"/><w:highlight w:val=\"yellow\"/>"), "<w:pStyle w:val=\"Quote\"/><w:spacing w:before=\"120\" w:after=\"240\" w:line=\"360\" w:lineRule=\"auto\"/>"))
            .Paragraph(P(R("고리"), "<w:pStyle w:val=\"Loop\"/>"))
            .Paragraph(P(R("첫째"), "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>"))
            .Paragraph(P(R("둘째"), "<w:numPr><w:ilvl w:val=\"1\"/><w:numId w:val=\"1\"/></w:numPr>"))
            .Paragraph(table)
            .Paragraph(P(InlinePicture(png, 381000, 285750) + textBox + group))
            .Paragraph(P($"<w:hyperlink r:id=\"{link}\">{R("링크", "<w:rStyle w:val=\"Hyperlink\"/>")}</w:hyperlink>" +
                         "<w:hyperlink w:anchor=\"top\">" + R("책갈피") + "</w:hyperlink>" +
                         Field("HYPERLINK \"https://example.invalid/field\" \\o \"tip\"", "필드 링크") +
                         "<w:fldSimple w:instr=\" PAGE \">" + R("1") + "</w:fldSimple>" +
                         "<w:r><w:footnoteReference w:id=\"1\"/></w:r><w:r><w:endnoteReference w:id=\"1\"/></w:r>" +
                         "<w:r><w:sym w:font=\"Wingdings\" w:char=\"F0FC\"/><w:tab/><w:br/><w:t>끝</w:t><w:br w:type=\"column\"/></w:r>"))
            .Paragraph(P("<m:oMath><m:f><m:num><m:r><m:t>x</m:t></m:r></m:num><m:den><m:sSup><m:e><m:r><m:t>y</m:t></m:r></m:e><m:sup><m:r><m:t>2</m:t></m:r></m:sup></m:sSup></m:den></m:f><m:rad><m:radPr><m:degHide m:val=\"1\"/></m:radPr><m:deg/><m:e><m:r><m:t>z</m:t></m:r></m:e></m:rad></m:oMath>"))
            .Paragraph("<w:sdt><w:sdtPr/><w:sdtContent>" + P(R("내용 컨트롤")) + "</w:sdtContent></w:sdt>")
            .Paragraph(P($"<w:r><w:pict><v:shape style=\"width:30pt;height:20pt\"><v:imagedata r:id=\"{png}\"/></v:shape><v:line from=\"0,0\" to=\"10pt,20pt\" strokecolor=\"#0000ff\"/></w:pict></w:r>" +
                         "<w:r><w:br w:type=\"page\"/></w:r>" + R("다음 쪽", "<w:rFonts w:ascii=\"Arial\" w:eastAsia=\"바탕\"/><w:sz w:val=\"28\"/><w:spacing w:val=\"20\"/><w:w w:val=\"90\"/><w:vertAlign w:val=\"superscript\"/>")))
            .Paragraph("<w:p><w:pPr><w:sectPr>" + DefaultPage + "<w:type w:val=\"continuous\"/></w:sectPr></w:pPr></w:p>");
        return builder.Save(Path.Combine(folder, "rich.docx"));
    }

    /// <summary>A valid PNG of <paramref name="width"/> × <paramref name="height"/> grey pixels.</summary>
    internal static byte[] TinyPng(int width, int height)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            output.Write(length);
            var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            output.Write(body);
            var crc = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Filee.Engines.Archives.Crc32.Compute(body));
            output.Write(crc);
        }
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 0; // greyscale
        Chunk("IHDR", header);
        using (var pixels = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(pixels, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                for (var y = 0; y < height; y++)
                {
                    zlib.WriteByte(0); // no filter
                    for (var x = 0; x < width; x++)
                        zlib.WriteByte(0x80);
                }
            }
            Chunk("IDAT", pixels.ToArray());
        }
        Chunk("IEND", []);
        return output.ToArray();
    }

    // ───────────────────────── Running ─────────────────────────

    /// <param name="name">Names the kept inputs.</param>
    /// <param name="mutator">Makes the mutated inputs of one seed; ZIP packages (DocumentFuzzer) when null.</param>
    private void Fuzz(string name, string extension, List<byte[]> seeds, Action<string, string> read, Func<byte[], Random, Func<byte[]>>? mutator = null)
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("FILEE_FUZZ_SECONDS"), out var s) && s > 0 ? s : 0;
        var seed = int.TryParse(Environment.GetEnvironmentVariable("FILEE_FUZZ_SEED"), out var fixedSeed) ? fixedSeed
            : seconds > 0 ? Environment.TickCount : 20261028;
        var outFolder = Environment.GetEnvironmentVariable("FILEE_FUZZ_OUT") is { Length: > 0 } o ? o : null;
        var keep = outFolder ?? fx.NewFolder();
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"Seed {seed}, {(seconds > 0 ? seconds + " s" : QuickIterations + " inputs per seed package")}");

        var random = new Random(seed);
        var fuzzers = seeds.Select(bytes => mutator?.Invoke(bytes, random) ?? new DocumentFuzzer(bytes, random).Next).ToList();
        var folder = fx.NewFolder();
        // With an output folder the input being read lives there: a crash that ends the process (a stack overflow)
        // leaves it behind for the report. A run that ends normally removes it.
        var input = Path.Combine(outFolder ?? folder, $"running-{name}.{extension}");
        var media = Path.Combine(folder, "media");

        // Every seed itself reads without trouble.
        foreach (var bytes in seeds)
        {
            File.WriteAllBytes(input, bytes);
            Assert.Equal(DocumentFuzzer.Outcome.Read, DocumentFuzzer.Run(() => read(input, media), ReadTimeout));
        }

        var failures = new Dictionary<string, string>();
        var (inputs, refused) = (0, 0);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (seconds > 0 ? stopwatch.Elapsed < TimeSpan.FromSeconds(seconds) : inputs < QuickIterations * seeds.Count)
        {
            var fuzzer = fuzzers[inputs % fuzzers.Count];
            var bytes = fuzzer();
            inputs++;
            File.WriteAllBytes(input, bytes);
            if (Directory.Exists(media))
                Directory.Delete(media, recursive: true);
            try
            {
                if (DocumentFuzzer.Run(() => read(input, media), ReadTimeout) == DocumentFuzzer.Outcome.Refused)
                    refused++;
            }
            catch (DocumentFuzzer.FuzzFailure failure)
            {
                // One input per kind of failure (exception type and the frame it came from).
                var frame = failure.InnerException?.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
                var kind = $"{failure.InnerException?.GetType().Name ?? "Timeout"} {frame}";
                if (!failures.ContainsKey(kind))
                {
                    var kept = Path.Combine(keep, $"{name}-{seed}-{inputs}.{extension}");
                    File.WriteAllBytes(kept, bytes);
                    failures[kind] = $"{failure.Message} (input {kept})\n{failure.InnerException}";
                }
                if (failure.InnerException is null)
                    break; // a read that hangs keeps running; don't pile them up
            }
        }
        output?.WriteLine($"{inputs} inputs, {refused} refused, {inputs - refused - failures.Count} read, {failures.Count} kinds of failure");
        if (failures.Count == 0 || failures.Keys.All(k => !k.StartsWith("Timeout", StringComparison.Ordinal)))
            File.Delete(input); // a read that still runs holds it open
        Assert.True(failures.Count == 0, $"Seed {seed}: " + string.Join("\n\n", failures.Values));
    }
}
