// Lines around a paragraph (w:pBdr) in DOCX → HWPX: the rule under a heading, a boxed paragraph (issue #510).

using System.Xml.Linq;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using static Filee.Engines.Tests.DocxBuilder;
using static Filee.Engines.Tests.HwpxAssert;

namespace Filee.Engines.Tests;

public class DocxParagraphBorderTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Rule = "<w:pBdr><w:bottom w:val=\"single\" w:sz=\"12\" w:space=\"1\" w:color=\"FF0000\"/></w:pBdr>";

    private (List<HParagraph> Paragraphs, string Hwpx) Convert(DocxBuilder docx)
    {
        var dir = fx.NewFolder();
        var document = DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media"));
        var hwpx = Path.Combine(dir, "out.hwpx");
        HwpxWriter.Write(document, hwpx);
        ValidPackage(hwpx);
        return (document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().ToList(), hwpx);
    }

    [Fact]
    public void A_rule_under_a_paragraph_is_read_from_its_style_and_turned_off_with_nil()
    {
        var styled = new DocxBuilder
        {
            Styles = new DocxBuilder().Styles + $"<w:style w:type=\"paragraph\" w:styleId=\"Ruled\"><w:name w:val=\"Ruled\"/><w:pPr>{Rule}</w:pPr></w:style>",
        };
        var (paragraphs, _) = Convert(styled
            .Paragraph(P(R("own"), Rule))
            .Paragraph(P(R("between")))
            .Paragraph(P(R("style"), "<w:pStyle w:val=\"Ruled\"/>"))
            .Paragraph(P(R("off"), "<w:pStyle w:val=\"Ruled\"/><w:pBdr><w:bottom w:val=\"nil\"/></w:pBdr>"))
            .Paragraph(P(R("none"))));

        var line = new HBorder(HBorderStyle.Solid, 12 / 8.0 * 25.4 / 72, "#FF0000");
        Assert.Equal(new HBorders(HBorder.None, HBorder.None, HBorder.None, line), paragraphs[0].Format.Borders);
        Assert.Equal(paragraphs[0].Format.Borders, paragraphs[2].Format.Borders);
        Assert.Null(paragraphs[3].Format.Borders);
        Assert.Null(paragraphs[4].Format.Borders);
    }

    [Fact]
    public void Paragraphs_with_the_same_borders_share_one_box()
    {
        var box = "<w:pBdr><w:top w:val=\"single\" w:sz=\"4\"/><w:left w:val=\"single\" w:sz=\"4\"/><w:bottom w:val=\"single\" w:sz=\"4\"/><w:right w:val=\"single\" w:sz=\"4\"/></w:pBdr>";
        var (paragraphs, _) = Convert(new DocxBuilder()
            .Paragraph(P(R("first"), box))
            .Paragraph(P(R("middle"), box))
            .Paragraph(P(R("last"), box))
            .Paragraph(P(R("indented"), box + "<w:ind w:left=\"720\"/>"))
            .Paragraph(P(R("other"), Rule)));

        var line = new HBorder(HBorderStyle.Solid, 4 / 8.0 * 25.4 / 72, "#000000");
        Assert.Equal(new HBorders(line, line, line, HBorder.None), paragraphs[0].Format.Borders);
        Assert.Equal(new HBorders(line, line, HBorder.None, HBorder.None), paragraphs[1].Format.Borders);
        Assert.Equal(new HBorders(line, line, HBorder.None, line), paragraphs[2].Format.Borders);
        Assert.Equal(new HBorders(line, line, line, line), paragraphs[3].Format.Borders); // another indent: a box of its own
        Assert.Equal(HBorder.None, paragraphs[4].Format.Borders!.Value.Top);
    }

    [Fact]
    public void The_border_reaches_the_paragraph_shape()
    {
        var box = "<w:pBdr><w:top w:val=\"single\" w:sz=\"4\"/><w:left w:val=\"single\" w:sz=\"4\"/><w:bottom w:val=\"single\" w:sz=\"4\"/><w:right w:val=\"single\" w:sz=\"4\"/></w:pBdr>";
        var (_, hwpx) = Convert(new DocxBuilder().Paragraph(P(R("ruled"), Rule)).Paragraph(P(R("boxed"), box)).Paragraph(P(R("plain"))));
        var section = Xml(hwpx);
        XElement Border(string text) => HeadItem(hwpx, "paraPr", (string)ParagraphWith(section, text).Attribute("paraPrIDRef")!).Element(Hh + "border")!;
        XElement Fill(XElement border) => HeadItem(hwpx, "borderFill", (string)border.Attribute("borderFillIDRef")!);

        var ruled = Border("ruled");
        Assert.Equal(("SOLID", "#FF0000"), ((string)Fill(ruled).Element(Hh + "bottomBorder")!.Attribute("type")!, (string)Fill(ruled).Element(Hh + "bottomBorder")!.Attribute("color")!));
        Assert.Equal("NONE", (string)Fill(ruled).Element(Hh + "topBorder")!.Attribute("type")!);
        Assert.Equal(("0", "0", "0"), ((string)ruled.Attribute("offsetLeft")!, (string)ruled.Attribute("offsetRight")!, (string)ruled.Attribute("connect")!));

        var boxed = Border("boxed");
        Assert.Equal("SOLID", (string)Fill(boxed).Element(Hh + "leftBorder")!.Attribute("type")!);
        Assert.Equal("400", (string)boxed.Attribute("offsetLeft")!);

        // No border: the template's shape stays as it was, without a line.
        var plain = Border("plain");
        Assert.Equal("NONE", (string)Fill(plain).Element(Hh + "bottomBorder")!.Attribute("type")!);
    }
}
