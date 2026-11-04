// Word's "multiple" line spacing is of the line height the fonts have, 한글's of the font size (issue #510).

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public class DocxLineHeightTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Calibri = "<w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:eastAsia=\"SimSun\"/>";
    private const string Times = "<w:rFonts w:ascii=\"Times New Roman\" w:hAnsi=\"Times New Roman\" w:eastAsia=\"SimSun\"/>";

    private List<HParagraph> Read(DocxBuilder docx)
    {
        var dir = fx.NewFolder();
        return DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media"))
            .Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().ToList();
    }

    private static HLineSpacing Percent(int value) => new(HLineSpacingKind.Percent, value);

    [Fact]
    public void A_multiple_is_of_the_height_of_the_paragraphs_font()
    {
        var paragraphs = Read(new DocxBuilder()
            .Paragraph(P(R("single", Calibri)))
            .Paragraph(P(R("a little more", Calibri), "<w:spacing w:line=\"264\" w:lineRule=\"auto\"/>"))
            .Paragraph(P(R("serif", Times), "<w:spacing w:line=\"480\" w:lineRule=\"auto\"/>")));

        Assert.Equal(Percent(122), paragraphs[0].Format.LineSpacing);
        Assert.Equal(Percent(134), paragraphs[1].Format.LineSpacing);
        Assert.Equal(Percent(230), paragraphs[2].Format.LineSpacing);
    }

    [Fact]
    public void Cjk_text_and_unknown_fonts_keep_about_one_point_three()
    {
        var paragraphs = Read(new DocxBuilder()
            .Paragraph(P(R("中文", Times)))
            .Paragraph(P(R("中文 and Latin", Times)))
            .Paragraph(P(R("Latin", "<w:rFonts w:ascii=\"Some Font\" w:hAnsi=\"Some Font\"/>"))));

        Assert.All(paragraphs, p => Assert.Equal(Percent(130), p.Format.LineSpacing));
    }

    [Fact]
    public void The_tallest_text_decides_a_mixed_paragraph()
    {
        var paragraphs = Read(new DocxBuilder()
            // Both at 10 pt: Calibri's taller line counts.
            .Paragraph(P(R("serif", Times) + R("sans", Calibri)))
            // Calibri at 10 pt is lower than Times New Roman at 20 pt: the percentage is of the 20 pt.
            .Paragraph(P(R("small", Calibri) + R("large", Times + "<w:sz w:val=\"40\"/>"))));

        Assert.Equal(Percent(122), paragraphs[0].Format.LineSpacing);
        Assert.Equal(Percent(115), paragraphs[1].Format.LineSpacing);
    }

    [Fact]
    public void An_empty_paragraph_has_the_height_of_its_mark()
    {
        var paragraphs = Read(new DocxBuilder().Paragraph(P("", $"<w:rPr>{Calibri}</w:rPr>")));

        Assert.Equal(Percent(122), Assert.Single(paragraphs).Format.LineSpacing);
    }

    [Fact]
    public void Exact_and_at_least_spacing_stay_as_they_are()
    {
        var paragraphs = Read(new DocxBuilder()
            .Paragraph(P(R("exact", Calibri), "<w:spacing w:line=\"300\" w:lineRule=\"exact\"/>"))
            .Paragraph(P(R("at least", Calibri), "<w:spacing w:line=\"300\" w:lineRule=\"atLeast\"/>")));

        Assert.Equal(new HLineSpacing(HLineSpacingKind.Fixed, HwpxUnits.FromTwips(300)), paragraphs[0].Format.LineSpacing);
        Assert.Equal(new HLineSpacing(HLineSpacingKind.AtLeast, HwpxUnits.FromTwips(300)), paragraphs[1].Format.LineSpacing);
    }

    [Fact]
    public void The_document_grid_counts_the_height_of_the_font()
    {
        // 23 pt of Calibri (28 pt high) needs two cells of a 15 pt grid where 130% (29.9 pt) would too, 12 pt of Calibri
        // at 14.65 pt one cell where 130% (15.6 pt) would need two.
        var docx = new DocxBuilder { SectionProperties = DefaultPage + "<w:docGrid w:type=\"lines\" w:linePitch=\"300\"/>" };
        var paragraphs = Read(docx.Paragraph(P(R("fits one cell", Calibri + "<w:sz w:val=\"24\"/>"))));

        Assert.Equal(new HLineSpacing(HLineSpacingKind.AtLeast, HwpxUnits.FromTwips(300)), Assert.Single(paragraphs).Format.LineSpacing);
    }
}
