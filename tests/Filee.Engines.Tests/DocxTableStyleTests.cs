// The table style in DOCX → HWPX: its paragraph and run formatting reaches the text in the cells, and the lines of its
// banded rows and the bottom line of vertically merged cells come through (#1152).

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public class DocxTableStyleTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Word's defaults since 2013 (10 pt after, 1.15 lines) and a table style that turns them off, like Table Grid.</summary>
    private static DocxBuilder Builder(string tableStyle) => new()
    {
        Styles = """
            <w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val="22"/></w:rPr></w:rPrDefault>
            <w:pPrDefault><w:pPr><w:spacing w:after="200" w:line="276" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>
            <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
            <w:style w:type="paragraph" w:styleId="Spaced"><w:name w:val="Spaced"/><w:basedOn w:val="Normal"/>
              <w:pPr><w:spacing w:after="120"/></w:pPr><w:rPr><w:sz w:val="32"/></w:rPr></w:style>
            <w:style w:type="table" w:default="1" w:styleId="TableNormal"><w:name w:val="Normal Table"/></w:style>
            """ + tableStyle,
    };

    private const string Grid = """
        <w:style w:type="table" w:styleId="Grid"><w:name w:val="Grid"/><w:basedOn w:val="TableNormal"/>
          <w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr><w:rPr><w:sz w:val="18"/></w:rPr></w:style>
        """;

    private static string Table(string style, string look, params string[] rows) =>
        $"<w:tbl><w:tblPr><w:tblStyle w:val=\"{style}\"/>{look}</w:tblPr><w:tblGrid><w:gridCol w:w=\"2000\"/><w:gridCol w:w=\"2000\"/></w:tblGrid>" +
        string.Concat(rows.Select(r => $"<w:tr>{r}</w:tr>")) + "</w:tbl>";

    private static string Cell(string paragraphs, string properties = "") => $"<w:tc><w:tcPr>{properties}</w:tcPr>{paragraphs}</w:tc>";

    private HDocument Read(DocxBuilder docx)
    {
        var dir = fx.NewFolder();
        return DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media"));
    }

    private static HTable OnlyTable(HDocument document) => document.Sections.SelectMany(s => s.Blocks).OfType<HTable>().Single();

    private static HParagraph Paragraph(HCell cell, int index = 0) => cell.Blocks.OfType<HParagraph>().ElementAt(index);

    [Fact]
    public void The_table_styles_spacing_and_size_replace_the_document_defaults_in_cells()
    {
        var document = Read(Builder(Grid)
            .Paragraph(Table("Grid", "", Cell(P(R("cell"))) + Cell(P(R("other")))))
            .Paragraph(P(R("body"))));

        var cell = Paragraph(OnlyTable(document).Rows[0].Cells[0]);
        Assert.Equal(0, cell.Format.After);
        Assert.Equal(900, ((HText)cell.Inlines[0]).Format.Size);

        var body = document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().Single(p => p.Inlines.OfType<HText>().Any(t => t.Text == "body"));
        Assert.Equal(HwpxUnits.FromTwips(200), body.Format.After);
        Assert.Equal(1100, ((HText)body.Inlines[0]).Format.Size);
    }

    [Fact]
    public void A_paragraph_style_and_direct_formatting_win_over_the_table_style()
    {
        var document = Read(Builder(Grid).Paragraph(Table("Grid", "",
            Cell(P(R("styled"), "<w:pStyle w:val=\"Spaced\"/>")) +
            Cell(P(R("direct"), "<w:spacing w:after=\"60\"/>"))) + P("")));

        var row = OnlyTable(document).Rows[0];
        var styled = Paragraph(row.Cells[0]);
        Assert.Equal(HwpxUnits.FromTwips(120), styled.Format.After);
        Assert.Equal(1600, ((HText)styled.Inlines[0]).Format.Size);
        Assert.Equal(HwpxUnits.FromTwips(60), Paragraph(row.Cells[1]).Format.After);
    }

    [Fact]
    public void The_table_style_does_not_reach_paragraphs_after_the_table()
    {
        var document = Read(Builder(Grid).Paragraph(Table("Grid", "", Cell(P(R("cell"))) + Cell(P(R("other"))))).Paragraph(P(R("after"))));

        var after = document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().Last();
        Assert.Equal(HwpxUnits.FromTwips(200), after.Format.After);
    }

    private const string Banded = """
        <w:style w:type="table" w:styleId="Banded"><w:name w:val="Banded"/><w:basedOn w:val="TableNormal"/>
          <w:tblPr><w:tblStyleRowBandSize w:val="1"/><w:tblBorders><w:top w:val="single" w:sz="4" w:color="7F7F7F"/>
            <w:bottom w:val="single" w:sz="4" w:color="7F7F7F"/></w:tblBorders></w:tblPr>
          <w:tblStylePr w:type="firstRow"><w:rPr><w:b/></w:rPr></w:tblStylePr>
          <w:tblStylePr w:type="band1Horz"><w:tcPr><w:tcBorders><w:top w:val="single" w:sz="4" w:color="7F7F7F"/>
            <w:bottom w:val="single" w:sz="4" w:color="7F7F7F"/></w:tcBorders><w:shd w:val="clear" w:fill="EEEEEE"/></w:tcPr></w:tblStylePr>
        </w:style>
        """;

    private static readonly HBorder Line = new(HBorderStyle.Solid, 4 / 8.0 * 25.4 / 72, "#7F7F7F");

    [Fact]
    public void Banded_rows_take_their_lines_and_shading_when_the_table_turns_banding_on()
    {
        var rows = Enumerable.Range(1, 4).Select(i => Cell(P(R($"a{i}"))) + Cell(P(R($"b{i}")))).ToArray();
        var document = Read(Builder(Banded)
            .Paragraph(Table("Banded", "<w:tblLook w:firstRow=\"0\" w:noHBand=\"0\" w:noVBand=\"1\"/>", rows) + P(""))
            .Paragraph(Table("Banded", "<w:tblLook w:firstRow=\"0\" w:noHBand=\"1\" w:noVBand=\"1\"/>", rows) + P("")));

        var tables = document.Sections.SelectMany(s => s.Blocks).OfType<HTable>().ToList();
        var banded = tables[0].Rows;
        Assert.All(new[] { banded[0], banded[2] }, row => Assert.All(row.Cells, cell =>
        {
            Assert.Equal(Line, cell.Borders!.Value.Top);
            Assert.Equal(Line, cell.Borders!.Value.Bottom);
            Assert.Equal("#EEEEEE", cell.Fill);
        }));
        Assert.All(banded[1].Cells, cell => Assert.Null(cell.Fill));

        var plain = tables[1].Rows;
        Assert.Equal(HBorder.None, plain[0].Cells[0].Borders!.Value.Bottom);
        Assert.Null(plain[0].Cells[0].Fill);
    }

    [Fact]
    public void Banding_starts_below_the_header_row_and_the_header_rows_run_formatting_reaches_its_text()
    {
        var rows = Enumerable.Range(1, 3).Select(i => Cell(P(R($"a{i}"))) + Cell(P(R($"b{i}")))).ToArray();
        var document = Read(Builder(Banded).Paragraph(Table("Banded", "<w:tblLook w:val=\"04A0\"/>", rows) + P("")));

        var table = OnlyTable(document).Rows;
        Assert.Null(table[0].Cells[0].Fill);
        Assert.Equal(true, ((HText)Paragraph(table[0].Cells[0]).Inlines[0]).Format.Bold);
        Assert.Equal("#EEEEEE", table[1].Cells[0].Fill);
        Assert.Null(table[2].Cells[0].Fill);
        Assert.NotEqual(true, ((HText)Paragraph(table[1].Cells[0]).Inlines[0]).Format.Bold);
    }

    [Fact]
    public void A_vertically_merged_cell_takes_the_bottom_line_of_its_last_row()
    {
        var document = Read(Builder(Banded).Paragraph(Table("Banded", "<w:tblLook w:firstRow=\"0\" w:noHBand=\"1\" w:noVBand=\"1\"/>",
            Cell(P(R("merged")), "<w:vMerge w:val=\"restart\"/>") + Cell(P(R("b1"))),
            Cell(P(""), "<w:vMerge/>") + Cell(P(R("b2"))),
            Cell(P(""), "<w:vMerge/>") + Cell(P(R("b3")))) + P("")));

        var merged = OnlyTable(document).Rows[0].Cells[0];
        Assert.Equal(3, merged.RowSpan);
        Assert.Equal(Line, merged.Borders!.Value.Top);
        Assert.Equal(Line, merged.Borders!.Value.Bottom);
    }
}
