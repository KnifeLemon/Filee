// RTF → document model without Pandoc or LibreOffice. The RTF is written here the way Word, WordPad and LibreOffice
// write it: code-page text and \u escapes, a style sheet with headings, lists through \listtable, tables with merged
// and shaded cells, notes, hyperlink and page fields, headers and footers, pictures and page setup; and hostile files
// built in code that must be refused at the document limits or read in linear time.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using ImageMagick;

namespace Filee.Engines.Tests;

public class RtfReaderTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    static RtfReaderTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private HDocument Read(string rtf) => RtfReader.Read(Encoding.Latin1.GetBytes(rtf), fx.NewFolder(), DocumentLimits.Default);

    /// <summary>Text in a code page as RTF hex escapes (\'b1\'db ...).</summary>
    private static string Hex(string text, int codePage) =>
        string.Concat(Encoding.GetEncoding(codePage).GetBytes(text).Select(b => b < 0x80 ? ((char)b).ToString() : $"\\'{b:x2}"));

    private static List<HParagraph> Paragraphs(HDocument document) => [.. document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>()];

    private static string Text(IEnumerable<HInline> inlines) => string.Concat(inlines.Select(i => i switch
    {
        HText t => t.Text,
        HLink l => Text(l.Content),
        HTab => "\t",
        HLineBreak => "\n",
        _ => "",
    }));

    [Fact]
    public void Wordpad_text_in_code_pages_and_unicode_escapes()
    {
        var rtf = "{\\rtf1\\ansi\\ansicpg1252\\deff0\\nouicompat{\\fonttbl{\\f0\\fnil\\fcharset0 Calibri;}{\\f1\\fnil\\fcharset129 "
                  + Hex("맑은 고딕", 949) + ";}}\r\n{\\colortbl ;\\red255\\green0\\blue0;}\r\n"
                  + "{\\*\\generator Riched20 10.0.22621}\\viewkind4\\uc1 \r\n"
                  + "\\pard\\sa200\\sl276\\slmult1\\f0\\fs22\\lang9 Plain \\b bold\\b0  \\i italic\\i0  \\cf1 red\\cf0\\par\r\n"
                  + "\\qc\\f1 " + Hex("한글 문서", 949) + "\\par\r\n"
                  + "\\ql\\f0 caf\\u233?\\u-10179?\\u-8704? \\'93quoted\\'94\\par\r\n}";

        var document = Read(rtf);

        var paragraphs = Paragraphs(document);
        Assert.Equal(["Plain bold italic red", "한글 문서", "café\U0001F600 \u201Cquoted\u201D"], paragraphs.Select(p => Text(p.Inlines)));
        var runs = paragraphs[0].Inlines.OfType<HText>().ToList();
        Assert.True(runs.Single(r => r.Text == "bold").Format.Bold);
        Assert.True(runs.Single(r => r.Text == "italic").Format.Italic);
        Assert.Equal("#FF0000", runs.Single(r => r.Text == "red").Format.Color);
        Assert.Equal(1100, runs[0].Format.Size);
        Assert.Equal("Calibri", runs[0].Format.Font);
        Assert.Equal(HAlign.Center, paragraphs[1].Format.Align);
        Assert.Equal("맑은 고딕", paragraphs[1].Inlines.OfType<HText>().Single().Format.EastAsianFont);
        Assert.Equal(new HLineSpacing(HLineSpacingKind.Percent, 150), paragraphs[0].Format.LineSpacing); // 1.15 lines
        Assert.Equal(1000, paragraphs[0].Format.After);
    }

    [Fact]
    public void Word_styles_lists_tables_notes_fields_and_page_setup()
    {
        var png = Convert.ToHexString(MakePng()).ToLowerInvariant();
        var rtf = "{\\rtf1\\ansi\\ansicpg1252\\uc1\\deff0{\\fonttbl{\\f0\\froman\\fcharset0 Times New Roman;}}"
                  + "{\\colortbl;\\red0\\green0\\blue0;\\red255\\green255\\blue0;}"
                  + "{\\stylesheet{\\ql \\fs24 \\snext0 Normal;}{\\s1\\ql\\keepn\\outlinelevel0 \\b\\fs32 \\sbasedon0 \\snext0 heading 1;}}"
                  + "{\\*\\listtable{\\list\\listtemplateid1{\\listlevel\\levelnfc0\\levelstartat1{\\leveltext\\'02\\'00.;}{\\levelnumbers\\'01;}}"
                  + "{\\listlevel\\levelnfc4\\levelstartat1{\\leveltext\\'02\\'01);}{\\levelnumbers\\'01;}}\\listid100}"
                  + "{\\list\\listtemplateid2{\\listlevel\\levelnfc23{\\leveltext\\'01\\u-3913 ?;}{\\levelnumbers;}}\\listid200}}"
                  + "{\\*\\listoverridetable{\\listoverride\\listid100\\ls1}{\\listoverride\\listid200\\ls2}}"
                  + "{\\info{\\title Report}{\\author Someone}}"
                  + "\\paperw11906\\paperh16838\\margl1134\\margr1134\\margt1417\\margb1417"
                  + "\\sectd\\headery709\\footery709"
                  + "{\\footer\\pard\\qc Page {\\field{\\*\\fldinst PAGE}{\\fldrslt 1}} of {\\field{\\*\\fldinst NUMPAGES}{\\fldrslt 3}}\\par}"
                  + "\\pard\\plain\\s1\\keepn\\outlinelevel0\\b\\fs32 Introduction\\par"
                  + "\\pard\\plain\\fs24 {\\listtext 1.\\tab}\\ls1\\ilvl0 First\\par"
                  + "{\\listtext a)\\tab}\\ls1\\ilvl1 Inner\\par"
                  + "{\\listtext \\'b7\\tab}\\ls2 Dot\\par"
                  + "\\pard See {\\field{\\*\\fldinst {HYPERLINK \"https://filee.sh\"}}{\\fldrslt {\\ul the site}}}"
                  + "{\\*\\bkmkstart here}{\\*\\bkmkend here} and a note{\\super\\chftn}{\\footnote\\pard\\plain{\\super\\chftn} Note text.\\par}.\\par"
                  + "\\trowd\\trgaph108\\trleft0\\clbrdrt\\brdrs\\clbrdrl\\brdrs\\clbrdrb\\brdrs\\clbrdrr\\brdrs\\clcbpat2\\clmgf\\cellx3000"
                  + "\\clbrdrt\\brdrs\\clbrdrl\\brdrs\\clbrdrb\\brdrs\\clbrdrr\\brdrs\\clmrg\\cellx6000\\clvmgf\\cellx9000"
                  + "\\pard\\intbl Merged\\cell\\cell Tall\\cell\\row"
                  + "\\trowd\\trgaph108\\trleft0\\cellx3000\\cellx6000\\clvmrg\\cellx9000"
                  + "\\pard\\intbl A\\cell B\\cell\\cell\\row"
                  + "\\pard After the table.\\page Next page {\\pict\\pngblip\\picw2\\pich2\\picwgoal1440\\pichgoal720 " + png + "}\\par"
                  + "\\sect\\sectd\\lndscpsxn\\pgwsxn16838\\pghsxn11906 Landscape section\\par}";

        var document = Read(rtf);

        Assert.Equal("Report", document.Title);
        Assert.Equal(2, document.Sections.Count);
        var first = document.Sections[0];
        Assert.Equal(new HPage(59530, 84190, 5670, 5670, 3545, 3545, 3540, 3540, 0), first.Page);
        Assert.True(document.Sections[1].Page!.Landscape);

        var footer = Assert.Single(first.Footers);
        Assert.Equal([HFieldKind.PageNumber, HFieldKind.TotalPages],
            footer.Blocks.OfType<HParagraph>().Single().Inlines.OfType<HField>().Select(f => f.Kind));

        var paragraphs = first.Blocks.OfType<HParagraph>().ToList();
        Assert.Equal(1, paragraphs[0].HeadingLevel);
        Assert.Equal("Introduction", Text(paragraphs[0].Inlines));

        // Lists: numbers with their levels, a bullet; the rendered list text is not text of the paragraph.
        Assert.Equal(["First", "Inner", "Dot"], paragraphs.Skip(1).Take(3).Select(p => Text(p.Inlines)));
        var numbered = paragraphs[1].List!;
        Assert.Equal(("DIGIT", "^1.", false), (numbered.Numbering.Levels[0].Format, numbered.Numbering.Levels[0].Text, numbered.Numbering.Levels[0].Bullet));
        Assert.Equal(("LATIN_SMALL", "^2)"), (numbered.Numbering.Levels[1].Format, numbered.Numbering.Levels[1].Text));
        Assert.Equal(1, paragraphs[2].List!.Level);
        Assert.Same(numbered.Numbering, paragraphs[2].List!.Numbering);
        Assert.True(paragraphs[3].List!.Numbering.Levels[0].Bullet);
        Assert.Equal("\u2022", paragraphs[3].List!.Numbering.Levels[0].Text);

        // A hyperlink, a bookmark and a footnote in one paragraph.
        var linked = paragraphs[4];
        Assert.Equal("See the site and a note.", Text(linked.Inlines));
        Assert.Equal("https://filee.sh", linked.Inlines.OfType<HLink>().Single().Target);
        Assert.Equal("here", linked.Inlines.OfType<HBookmark>().Single().Name);
        var note = linked.Inlines.OfType<HNote>().Single();
        Assert.False(note.Endnote);
        Assert.Equal("Note text.", Text(note.Blocks.OfType<HParagraph>().Single().Inlines).Trim());

        // A table: a merged, shaded first cell, a cell spanning two rows.
        var table = first.Blocks.OfType<HTable>().Single();
        Assert.Equal(3, table.ColumnCount);
        Assert.Equal([15000, 15000, 15000], table.ColumnWidths!);
        var top = table.Rows[0].Cells;
        Assert.Equal(2, top.Count);
        Assert.Equal((0, 2, "#FFFF00"), (top[0].Column, top[0].ColSpan, top[0].Fill));
        Assert.Equal(HBorders.Grid, top[0].Borders);
        Assert.Equal((2, 2), (top[1].Column, top[1].RowSpan));
        Assert.Equal(["A", "B"], table.Rows[1].Cells.Select(c => Text(c.Blocks.OfType<HParagraph>().Single().Inlines)));

        // A page break and a picture sized by \picwgoal.
        var after = first.Blocks.SkipWhile(b => b is not HTable).Skip(1).OfType<HParagraph>().ToList();
        Assert.Equal("After the table.", Text(after[0].Inlines));
        Assert.True(after[1].PageBreakBefore);
        var picture = after[1].Inlines.OfType<HImage>().Single();
        Assert.Equal((7200, 3600), (picture.Width, picture.Height));
        Assert.True(File.Exists(picture.Path));
        Assert.Equal("Landscape section", Text(document.Sections[1].Blocks.OfType<HParagraph>().Single().Inlines));
    }

    [Fact]
    public void Endnotes_and_libreoffice_unicode_without_fallback()
    {
        var rtf = "{\\rtf1\\ansi\\deff0\\adeflang1025{\\fonttbl{\\f0\\froman\\fprq2\\fcharset0 Liberation Serif;}}"
                  + "\\uc0\\pard\\plain \\u54620\\u44544  text{\\super\\chftn}{\\footnote\\ftnalt\\pard\\plain End.\\par}\\par}";

        var paragraph = Paragraphs(Read(rtf)).Single();

        // The space after the last escape ends it; the second one is text.
        Assert.Equal("한글 text", Text(paragraph.Inlines));
        Assert.True(paragraph.Inlines.OfType<HNote>().Single().Endnote);
    }

    [Fact]
    public void Unknown_destinations_and_hidden_text_are_left_out()
    {
        var rtf = "{\\rtf1\\ansi{\\*\\unknowndest secret}{\\*\\themedata 0102}{\\xe index entry}"
                  + "Shown{\\v hidden} text{\\*\\annotation comment}.\\par}";

        Assert.Equal("Shown text.", Text(Paragraphs(Read(rtf)).Single().Inlines));
    }

    [Fact]
    public void Damaged_and_hostile_files_fail_cleanly()
    {
        Assert.Throws<InvalidDataException>(() => Read("not rtf at all"));
        var deep = "{\\rtf1 " + new string('{', 2000) + "x" + new string('}', 2000) + "}";
        Assert.Throws<ConversionRefusedException>(() => Read(deep));
        // Unclosed groups and a truncated hex byte still give the text read so far.
        Assert.Equal("Cut", Text(Paragraphs(Read("{\\rtf1 {\\b Cut\\'4")).Single().Inlines));
    }

    [Fact]
    public void Rows_without_their_own_definition_share_the_one_before()
    {
        var rtf = "{\\rtf1 \\trowd\\cellx1000\\cellx2000\\pard\\intbl a\\cell b\\cell\\row"
                  + "\\pard\\intbl c\\cell d\\cell\\row"
                  + "\\cellx3000\\pard\\intbl e\\cell f\\cell g\\cell\\row\\pard After.\\par}";

        var table = Read(rtf).Sections[0].Blocks.OfType<HTable>().Single();

        // The third row adds a cell to the definition it kept: the rows before don't get it.
        Assert.Equal(3, table.ColumnCount);
        Assert.Equal([2, 2, 3], table.Rows.Select(r => r.Cells.Count));
        Assert.Equal(["a", "b", "c", "d", "e", "f", "g"],
            table.Rows.SelectMany(r => r.Cells).Select(c => Text(c.Blocks.OfType<HParagraph>().Single().Inlines)));
    }

    [Fact]
    public void Hostile_tables_are_refused_at_the_cell_budget()
    {
        // 16,384 cell edges (about 115 KB) kept by 100,000 rows of one empty cell each.
        var wide = new StringBuilder("{\\rtf1 \\trowd");
        for (var i = 1; i <= 16_384; i++)
            wide.Append("\\cellx").Append(i);
        wide.Insert(wide.Length, "\\cell\\row ", 100_000).Append('}');
        var refused = Assert.Throws<ConversionRefusedException>(() => Read(wide.ToString()));
        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);

        // Empty cells count too, though they hold no paragraph of the file's.
        var limits = DocumentLimits.Default with { MaxParagraphs = 1_000 };
        var cells = "{\\rtf1 \\trowd\\cellx100" + string.Concat(Enumerable.Repeat("\\cell", 2_000)) + "\\row}";
        Assert.Throws<ConversionRefusedException>(() => RtfReader.Read(Encoding.Latin1.GetBytes(cells), fx.NewFolder(), limits));

        // Rows of other divisions whose edges together pass the column limit.
        var columns = new StringBuilder("{\\rtf1 ");
        for (var row = 0; row < 40; row++)
        {
            columns.Append("\\trowd");
            for (var i = 1; i <= 500; i++)
                columns.Append("\\cellx").Append(row + i * 41);
            columns.Append("\\pard\\intbl x\\cell\\row");
        }
        Assert.Throws<ConversionRefusedException>(() => RtfReader.Read(Encoding.Latin1.GetBytes(columns.Append('}').ToString()), fx.NewFolder(),
            DocumentLimits.Default with { MaxTableColumns = 10_000 }));
    }

    [Fact]
    public void Long_runs_and_style_names_read_in_linear_time()
    {
        // A million non-breaking spaces, one control symbol each: one run.
        var spaces = "{\\rtf1 " + string.Concat(Enumerable.Repeat("\\~", 1_000_000)) + "\\par}";
        var run = Assert.Single(Paragraphs(Read(spaces)).Single().Inlines.OfType<HText>());
        Assert.Equal(1_000_000, run.Text.Length);

        // A style name in 400,000 pieces.
        var name = "{\\rtf1 {\\stylesheet{\\s1 " + string.Concat(Enumerable.Repeat("a\\~", 200_000)) + ";}}\\s1 Styled\\par}";
        Assert.Equal("Styled", Text(Paragraphs(Read(name)).Single().Inlines));
    }

    [Fact]
    public void Many_lists_are_looked_up_by_id()
    {
        var rtf = new StringBuilder("{\\rtf1 {\\*\\listtable");
        for (var i = 1; i <= 50_000; i++)
            rtf.Append("{\\list{\\listlevel\\levelnfc").Append(i == 50_000 ? 4 : 0).Append("{\\leveltext\\'02\\'00.;}}\\listid").Append(i).Append('}');
        rtf.Append("}{\\*\\listoverridetable{\\listoverride\\listid50000\\ls1}}");
        rtf.Insert(rtf.Length, "\\pard\\ls1 Item\\par", 50_000).Append('}');

        var paragraphs = Paragraphs(Read(rtf.ToString()));

        Assert.Equal(50_000, paragraphs.Count);
        Assert.All(paragraphs, p => Assert.Equal("LATIN_SMALL", p.List!.Numbering.Levels[0].Format));
        Assert.Same(paragraphs[0].List!.Numbering, paragraphs[^1].List!.Numbering);

        // Tables of fonts, colours, styles and lists stay under the object limit.
        var colours = "{\\rtf1 {\\colortbl" + new string(';', 2_000) + "}x\\par}";
        Assert.Throws<ConversionRefusedException>(() =>
            RtfReader.Read(Encoding.Latin1.GetBytes(colours), fx.NewFolder(), DocumentLimits.Default with { MaxObjects = 1_000 }));
    }

    [Fact]
    public void Pictures_claiming_too_many_pixels_are_left_out()
    {
        // A PNG header claiming 100,000 × 100,000 pixels.
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0x00, 0x01, 0x86, 0xA0, 0x00, 0x01, 0x86, 0xA0, 8, 6, 0, 0, 0, 0, 0, 0, 0 };
        var media = fx.NewFolder();
        var rtf = "{\\rtf1 Before {\\pict\\pngblip\\picwgoal1440\\pichgoal1440 " + Convert.ToHexString(png) + "} after.\\par}";

        var paragraph = Paragraphs(RtfReader.Read(Encoding.Latin1.GetBytes(rtf), media, DocumentLimits.Default)).Single();

        Assert.Empty(paragraph.Inlines.OfType<HImage>());
        Assert.Equal("Before  after.", Text(paragraph.Inlines));
        Assert.Empty(Directory.EnumerateFileSystemEntries(media));
    }

    [Fact]
    public void Nested_hyperlinks_are_refused_past_the_nesting_limit()
    {
        const string Open = "{\\field{\\*\\fldinst HYPERLINK \"https://filee.sh\"}{\\fldrslt ";
        var nested = "{\\rtf1 " + string.Concat(Enumerable.Repeat(Open + "x", 40)) + new string('}', 80) + "\\par}";
        Assert.Throws<ConversionRefusedException>(() => Read(nested));

        // Links one after the other leave their level when they close.
        var many = "{\\rtf1 " + string.Concat(Enumerable.Repeat(Open + "x}}", 100)) + "\\par}";
        Assert.Equal(100, Paragraphs(Read(many)).Single().Inlines.OfType<HLink>().Count());
    }

    [Fact]
    public async Task Rtf_converts_to_docx_and_hwpx_without_optional_engines()
    {
        var dir = fx.NewFolder();
        var source = Path.Combine(dir, "letter.rtf");
        await File.WriteAllTextAsync(source, "{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}}\\f0\\fs24 Dear reader,\\par Thanks.\\par}", TestContext.Current.CancellationToken);

        foreach (var target in new[] { "docx", "hwpx" })
        {
            var job = await fx.ConvertAsync([source], new Preset { TargetFormat = target });

            Assert.Equal(JobState.Completed, job.State);
            var read = target == "docx"
                ? Filee.Engines.Hwp.Hwpx.Docx.DocxReader.Read(job.Outputs.Single(), fx.NewFolder())
                : HwpxReader.Read(job.Outputs.Single(), fx.NewFolder());
            Assert.Equal(["Dear reader,", "Thanks."], Paragraphs(read).Select(p => Text(p.Inlines)).Where(t => t.Length > 0));
        }
    }

    private static byte[] MakePng()
    {
        using var image = new MagickImage(MagickColors.Teal, 2, 2);
        return image.ToByteArray(MagickFormat.Png);
    }
}
