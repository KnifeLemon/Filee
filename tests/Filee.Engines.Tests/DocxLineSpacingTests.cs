// Word's automatic line spacing is font-metric based; the HWPX percentage must account for the font family.
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public class DocxLineSpacingTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string LatinStyles = """
        <w:docDefaults>
          <w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri"/><w:sz w:val="22"/></w:rPr></w:rPrDefault>
          <w:pPrDefault><w:pPr><w:spacing w:line="240" w:lineRule="auto"/></w:pPr></w:pPrDefault>
        </w:docDefaults>
        <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
        """;

    private const string EastAsianStyles = """
        <w:docDefaults>
          <w:rPrDefault><w:rPr><w:rFonts w:ascii="Times New Roman" w:hAnsi="Times New Roman" w:eastAsia="宋体"/><w:sz w:val="24"/></w:rPr></w:rPrDefault>
          <w:pPrDefault><w:pPr><w:spacing w:line="240" w:lineRule="auto"/></w:pPr></w:pPrDefault>
        </w:docDefaults>
        <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
        """;

    private HParagraph ReadParagraph(DocxBuilder docx)
    {
        var dir = fx.NewFolder();
        var path = docx.Save(Path.Combine(dir, "source.docx"));
        var document = DocxReader.Read(path, Path.Combine(dir, "media"));
        return Assert.Single(document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>());
    }

    [Fact]
    public void Latin_single_spacing_uses_a_compact_font_metric_factor()
    {
        var paragraph = ReadParagraph(new DocxBuilder { Styles = LatinStyles }
            .Paragraph(P(R("A compact Calibri paragraph."))));

        Assert.Equal(new HLineSpacing(HLineSpacingKind.Percent, 120), paragraph.Format.LineSpacing);
    }

    [Fact]
    public void Latin_multiple_spacing_preserves_the_requested_multiplier()
    {
        var paragraph = ReadParagraph(new DocxBuilder { Styles = LatinStyles }
            .Paragraph(P(R("A 1.5-spaced Calibri paragraph."),
                "<w:spacing w:line=\"360\" w:lineRule=\"auto\"/>")));

        Assert.Equal(new HLineSpacing(HLineSpacingKind.Percent, 180), paragraph.Format.LineSpacing);
    }

    [Fact]
    public void East_Asian_single_spacing_keeps_the_existing_130_percent_mapping()
    {
        var paragraph = ReadParagraph(new DocxBuilder { Styles = EastAsianStyles }
            .Paragraph(P(R("한글 문단의 줄 간격."))));

        Assert.Equal(new HLineSpacing(HLineSpacingKind.Percent, 130), paragraph.Format.LineSpacing);
    }
}
