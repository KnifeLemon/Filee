// East Asian spacing in Filee's own renderer (issue #280, Typesetting/ParagraphLayout.EastAsian.cs): Word's justification
// of Chinese and Japanese lines between their characters, Korean justified at its spaces, and the auto-spacing between
// East Asian text and Latin letters (w:autoSpaceDE) or digits (w:autoSpaceDN). Word documents are built here and laid out
// as the renderer lays them out (PageComposer with the template's styles); 한글's paragraphs go through the paragraph
// layout directly. The Noto fonts of build/fetch-fonts.ps1 draw them; without them the tests skip.

using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
using SkiaSharp;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public sealed class EastAsianSpacingTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    /// <summary>The body of DocxBuilder's A4 page: 1 inch margins.</summary>
    private const float BodyLeft = 72, BodyRight = 595.3f - 72;

    private const string Justify = "<w:jc w:val=\"both\"/>";
    private const string AutoSpaceOff = "<w:autoSpaceDE w:val=\"0\"/><w:autoSpaceDN w:val=\"0\"/>";

    /// <summary>"The system has 3 GPU nodes, each with 8 graphics cards, used for training and inference."</summary>
    private const string Chinese = "本系统共 3 个 GPU 节点，每个节点配备 8 张显卡，用于模型的训练和推理，";

    private const string Korean = "필리는 문서를 변환할 때 Word 문단의 정렬과 줄 간격을 그대로 따릅니다. 양쪽 정렬된 줄은 빈칸이 조금씩 넓어지고 ";

    private const float Tolerance = 0.05f;

    // ───────────────────────── Justification ─────────────────────────

    [Fact]
    public void A_justified_chinese_paragraph_spreads_between_its_characters_and_leaves_the_last_line()
    {
        var builder = Word(RepositoryFonts.Chinese, 21);
        builder.Paragraph(P(R(string.Concat(Enumerable.Repeat(Chinese, 4))), Justify + AutoSpaceOff));

        var lines = Lines(Compose(builder));

        Assert.True(lines.Count >= 3, $"{lines.Count} lines");
        foreach (var line in lines.SkipLast(1))
        {
            Assert.Equal(BodyRight, Right(line), 0.5f);
            var han = Gaps(line, (a, b) => IsHan(a.Char) && IsHan(b.Char));
            var spaces = Gaps(line, (a, _) => a.Char == ' ');
            var latin = Gaps(line, (a, b) => char.IsAsciiLetter(a.Char) && char.IsAsciiLetter(b.Char));
            Assert.NotEmpty(han);
            // Every gap beside a Chinese character takes the same share, no more than half a character, and so do the
            // spaces: the room isn't all put into the few spaces.
            Assert.All(han, gap => Assert.InRange(gap, Tolerance, 10.5f / 2));
            Assert.Equal(han.Min(), han.Max(), Tolerance);
            Assert.All(spaces, gap => Assert.Equal(han[0], gap, Tolerance));
            // Latin words keep their letters together.
            Assert.All(latin, gap => Assert.Equal(0, gap, 0.2f));
        }

        var last = lines[^1];
        Assert.Equal(BodyLeft, Left(last), 0.5f);
        Assert.True(Right(last) < BodyRight - 5, $"the last line ends at {Right(last)}");
        Assert.All(Gaps(last, (a, b) => IsHan(a.Char) && IsHan(b.Char)), gap => Assert.Equal(0, gap, Tolerance));
    }

    [Fact]
    public void A_justified_korean_paragraph_widens_its_spaces_and_keeps_its_words_together()
    {
        var builder = Word(RepositoryFonts.Korean, 20);
        builder.Paragraph(P(R(string.Concat(Enumerable.Repeat(Korean, 3))), Justify + AutoSpaceOff));

        var lines = Lines(Compose(builder));

        Assert.True(lines.Count >= 3, $"{lines.Count} lines");
        foreach (var line in lines.SkipLast(1))
        {
            Assert.Equal(BodyRight, Right(line), 0.5f);
            Assert.All(Gaps(line, (a, b) => IsHangul(a.Char) && IsHangul(b.Char)), gap => Assert.Equal(0, gap, Tolerance));
            var spaces = Gaps(line, (a, _) => a.Char == ' ');
            Assert.NotEmpty(spaces);
            Assert.Equal(spaces.Min(), spaces.Max(), Tolerance);
        }
        Assert.Contains(lines.SkipLast(1), line => Gaps(line, (a, _) => a.Char == ' ').Max() > Tolerance);
    }

    [Fact]
    public void Hangul_justifies_chinese_at_its_spaces_only()
    {
        // 한글's paragraphs (no SpreadEastAsian) widen the spaces only, as before.
        var fonts = Fonts();
        var paragraph = Hwp(string.Concat(Enumerable.Repeat(Chinese, 3)), new HParaFormat(Align: HAlign.Justify));
        var box = Layouter(fonts).Layout(paragraph, 300);

        Assert.True(box.Lines.Count >= 2);
        var line = Glyphs(box.Lines[0]);
        Assert.All(Gaps(line, (a, b) => IsHan(a.Char) && IsHan(b.Char)), gap => Assert.Equal(0, gap, Tolerance));
        Assert.Contains(Gaps(line, (a, _) => a.Char == ' '), gap => gap > Tolerance);
    }

    // ───────────────────────── Auto-spacing ─────────────────────────

    [Fact]
    public void Chinese_and_latin_letters_get_a_quarter_of_a_character_between_them_unless_turned_off()
    {
        const string text = "使用GPU节点";
        var builder = Word(RepositoryFonts.Chinese, 21);
        builder.Paragraph(P(R(text)))
            .Paragraph(P(R(text), "<w:autoSpaceDE w:val=\"0\"/>"));

        var lines = Lines(Compose(builder));
        Assert.Equal(2, lines.Count);
        var (on, off) = (lines[0], lines[1]);

        // Word's default (no w:autoSpaceDE) is on: 10.5 pt / 4 on either side of "GPU".
        Assert.Equal(10.5f / 4, Gap(on, '用', 'G') - Gap(off, '用', 'G'), Tolerance);
        Assert.Equal(10.5f / 4, Gap(on, 'U', '节') - Gap(off, 'U', '节'), Tolerance);
        Assert.Equal(0, Gap(off, '用', 'G'), 0.2f);
        Assert.Equal(Gap(off, 'G', 'P'), Gap(on, 'G', 'P'), Tolerance);
        Assert.Equal(Gap(off, '节', '点'), Gap(on, '节', '点'), Tolerance);
        Assert.Equal(2 * 10.5f / 4, Right(on) - Right(off), Tolerance);
    }

    [Fact]
    public void Digits_get_the_space_with_autoSpaceDN_and_none_inside_the_number()
    {
        const string text = "共2024个GPU";
        var builder = Word(RepositoryFonts.Chinese, 21);
        builder.Paragraph(P(R(text)))
            .Paragraph(P(R(text), "<w:autoSpaceDN w:val=\"0\"/>"))
            .Paragraph(P(R(text), AutoSpaceOff));

        var lines = Lines(Compose(builder));
        Assert.Equal(3, lines.Count);
        var (both, letters, none) = (lines[0], lines[1], lines[2]);

        Assert.Equal(10.5f / 4, Gap(both, '共', '2') - Gap(none, '共', '2'), Tolerance);
        Assert.Equal(10.5f / 4, Gap(both, '4', '个') - Gap(none, '4', '个'), Tolerance);
        Assert.Equal(Gap(none, '0', '2'), Gap(both, '0', '2'), Tolerance);
        // Without autoSpaceDN the digits get none, the letters still do.
        Assert.Equal(Gap(none, '共', '2'), Gap(letters, '共', '2'), Tolerance);
        Assert.Equal(Gap(none, '4', '个'), Gap(letters, '4', '个'), Tolerance);
        Assert.Equal(10.5f / 4, Gap(letters, '个', 'G') - Gap(none, '个', 'G'), Tolerance);
    }

    [Fact]
    public void Korean_gets_the_space_before_latin_words_and_numbers_but_not_at_spaces_or_punctuation()
    {
        const string text = "Word는 2024년에 (Filee) 나왔다";
        var builder = Word(RepositoryFonts.Korean, 20);
        builder.Paragraph(P(R(text)))
            .Paragraph(P(R(text), AutoSpaceOff));

        var lines = Lines(Compose(builder));
        var (on, off) = (lines[0], lines[1]);

        Assert.Equal(2.5f, Gap(on, 'd', '는') - Gap(off, 'd', '는'), Tolerance);
        Assert.Equal(2.5f, Gap(on, '4', '년') - Gap(off, '4', '년'), Tolerance);
        // Before "2024" a space, after "(Filee)" a bracket: nothing added there.
        Assert.Equal(Gap(off, '는', ' '), Gap(on, '는', ' '), Tolerance);
        Assert.Equal(Gap(off, ')', ' '), Gap(on, ')', ' '), Tolerance);
        Assert.Equal(5f, Right(on) - Right(off), Tolerance);
    }

    [Fact]
    public void Justified_lines_hold_the_auto_spacing_and_spread_the_rest()
    {
        var builder = Word(RepositoryFonts.Chinese, 21);
        builder.Paragraph(P(R(string.Concat(Enumerable.Repeat("训练用GPU节点共8个，", 6))), Justify));

        var lines = Lines(Compose(builder));

        Assert.True(lines.Count >= 2);
        var line = lines[0];
        Assert.Equal(BodyRight, Right(line), 0.5f);
        var han = Gaps(line, (a, b) => IsHan(a.Char) && IsHan(b.Char));
        // A boundary takes its quarter character on top of the share every gap beside a Chinese character takes.
        Assert.All(Gaps(line, (a, b) => IsHan(a.Char) && b.Char == 'G'), gap => Assert.Equal(han[0] + 10.5f / 4, gap, Tolerance));
        Assert.All(Gaps(line, (a, b) => a.Char == 'U' && IsHan(b.Char)), gap => Assert.Equal(han[0] + 10.5f / 4, gap, Tolerance));
        Assert.All(han, gap => Assert.InRange(gap, 0, 10.5f / 2));
    }

    [Fact]
    public void Hangul_auto_spacing_applies_to_lines_laid_out_anew_but_not_to_stored_lines()
    {
        var fonts = Fonts();
        var breaking = new HLineBreaking(AutoSpaceLatin: true, AutoSpaceDigits: true);
        var format = new HParaFormat { Layout = new HParaLayout(Breaking: breaking) };

        var anew = Glyphs(Assert.Single(Layouter(fonts).Layout(Hwp("한글Word", format, RepositoryFonts.Korean), 300).Lines));
        var stored = Hwp("한글Word", format, RepositoryFonts.Korean);
        stored.Lines = new HLineLayout([new HLineSegment(0, 0, 1600, 1600, 1500, 400, 0, 30000, 0)], false, [0]);
        var kept = Glyphs(Assert.Single(Layouter(fonts).Layout(stored, 300).Lines));

        Assert.Equal(2.5f, Gap(anew, '글', 'W') - Gap(kept, '글', 'W'), Tolerance);
        Assert.Equal(0, Gap(kept, '글', 'W'), 0.2f);
    }

    [Fact]
    public void A_space_in_a_word_document_is_as_wide_as_the_fonts_own()
    {
        // The template's character shapes say 한글's half-a-character spaces; the writer turns that off, and so does the
        // renderer's style resolver (a Chinese line's spaces were twice as wide as Word's).
        var builder = Word(RepositoryFonts.Chinese, 21);
        builder.Paragraph(P(R("共 3 个 GPU")));

        var line = Lines(Compose(builder)).Single();

        var spaces = line.Zip(line.Skip(1)).Where(p => p.First.Char == ' ').ToList();
        Assert.Equal(3, spaces.Count);
        Assert.All(spaces, p => Assert.Equal(p.First.Advance, p.Second.X - p.First.X, Tolerance));
        Assert.All(spaces, p => Assert.InRange(p.First.Advance, 2, 3.5f));
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>A Word document whose text is in Noto Sans and <paramref name="eastAsia"/> at <paramref name="halfPoints"/>.</summary>
    private static DocxBuilder Word(string eastAsia, int halfPoints)
    {
        var fonts = Fonts();
        Assert.SkipUnless(fonts.Has(eastAsia), $"{eastAsia} is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        return new DocxBuilder
        {
            Styles = $"""
                <w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="{RepositoryFonts.Latin}" w:hAnsi="{RepositoryFonts.Latin}" w:eastAsia="{eastAsia}"/><w:sz w:val="{halfPoints}"/></w:rPr></w:rPrDefault>
                <w:pPrDefault><w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>
                <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
                """,
        };
    }

    private static RepositoryFonts Fonts()
    {
        var fonts = RepositoryFonts.Load();
        Assert.SkipUnless(fonts.Has(RepositoryFonts.Latin) && fonts.Has(RepositoryFonts.Korean) && fonts.Has(RepositoryFonts.Chinese),
            "Noto Sans, Noto Sans KR and Noto Sans SC are not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        return fonts;
    }

    private readonly Shaper _shaper = new();

    public void Dispose() => _shaper.Dispose();

    private ParagraphLayouter Layouter(RepositoryFonts fonts) => new(new FontResolver(fonts), _shaper);

    /// <summary>A 한글 paragraph at 10 pt, Latin text in Noto Sans and East Asian text in <paramref name="eastAsia"/>.</summary>
    private static HParagraph Hwp(string text, HParaFormat format, string eastAsia = RepositoryFonts.Chinese)
    {
        var character = new HCharFormat(Size: 1000, Font: RepositoryFonts.Latin, EastAsianFont: eastAsia, Color: "#000000");
        var paragraph = new HParagraph { Format = format, MarkFormat = character };
        paragraph.Inlines.Add(new HText(text, character));
        return paragraph;
    }

    /// <summary>Lays a Word document out as the renderer does: its formats resolved against the template's styles.</summary>
    private ComposedDocument Compose(DocxBuilder builder)
    {
        var document = DocxReader.Read(builder.Save(Path.Combine(fx.NewFolder(), "document.docx")), fx.NewFolder());
        return new PageComposer(new FontResolver(Fonts()), _shaper, document.Kinsoku, StyleResolver.FromTemplate())
            .Compose(document, TestContext.Current.CancellationToken);
    }

    /// <summary>A drawn glyph: the character its cluster starts with, its origin and advance.</summary>
    private readonly record struct Glyph(char Char, float X, float Y, float Advance);

    private static List<Glyph> Glyphs(IEnumerable<(PageFont Font, float Size, float ScaleX, ushort[] Glyphs, SKPoint[] Positions, string Text, int[] Clusters)> runs)
    {
        var glyphs = new List<Glyph>();
        foreach (var run in runs)
        {
            using var font = new SKFont(run.Font.Typeface, run.Size) { ScaleX = run.ScaleX, LinearMetrics = true, Subpixel = true };
            var widths = font.GetGlyphWidths(run.Glyphs);
            for (var g = 0; g < run.Glyphs.Length; g++)
                glyphs.Add(new Glyph(run.Text[run.Clusters[g]], run.Positions[g].X, run.Positions[g].Y, widths[g]));
        }
        return [.. glyphs.OrderBy(g => g.X)];
    }

    private static List<Glyph> Glyphs(LineBox line) =>
        Glyphs(line.Runs.Select(r => (r.Font, r.Size, r.ScaleX, r.Glyphs, r.Positions, r.Text, r.Clusters)));

    /// <summary>The glyphs of each line of the first page (by baseline), top to bottom, left to right.</summary>
    private static List<List<Glyph>> Lines(ComposedDocument document) =>
        [.. Glyphs(document.Pages[0].Items.OfType<GlyphItem>().Select(i => i.Run).Select(r => (r.Font, r.Size, r.ScaleX, r.Glyphs, r.Positions, r.Text, r.Clusters)))
            .GroupBy(g => MathF.Round(g.Y)).OrderBy(g => g.Key).Select(g => g.OrderBy(x => x.X).ToList())];

    /// <summary>The room between each pair of neighbouring glyphs <paramref name="pair"/> picks, beyond the first one's advance.</summary>
    private static List<float> Gaps(List<Glyph> line, Func<Glyph, Glyph, bool> pair) =>
        [.. line.Zip(line.Skip(1)).Where(p => pair(p.First, p.Second)).Select(p => p.Second.X - p.First.X - p.First.Advance)];

    /// <summary>The room between the first <paramref name="first"/> followed by <paramref name="second"/>.</summary>
    private static float Gap(List<Glyph> line, char first, char second)
    {
        var gaps = Gaps(line, (a, b) => a.Char == first && b.Char == second);
        Assert.True(gaps.Count > 0, $"no '{first}{second}' on the line");
        return gaps[0];
    }

    private static float Left(List<Glyph> line) => line.Min(g => g.X);

    private static float Right(List<Glyph> line) => line.Max(g => g.X + g.Advance);

    private static bool IsHan(char c) => c is >= '一' and <= '鿿';

    private static bool IsHangul(char c) => c is >= '가' and <= '힣';
}
