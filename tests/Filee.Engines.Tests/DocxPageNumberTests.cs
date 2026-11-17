// A section that restarts its page numbers (w:pgNumType w:start): rhwp went on counting from the section before (issue #280).

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using static Filee.Engines.Tests.DocxBuilder;
using static Filee.Engines.Tests.HwpxAssert;

namespace Filee.Engines.Tests;

public class DocxPageNumberTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    [Fact]
    public void A_restart_is_written_as_a_new_number_control_too()
    {
        var dir = fx.NewFolder();
        var docx = new DocxBuilder { SectionProperties = DefaultPage + "<w:pgNumType w:start=\"1\"/>" }
            .Paragraph(P(R("front matter"), $"<w:sectPr>{DefaultPage}</w:sectPr>"))
            .Paragraph(P(R("body")));
        var hwpx = Path.Combine(dir, "out.hwpx");
        HwpxWriter.Write(DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media")), hwpx);
        ValidPackage(hwpx);

        Assert.Empty(Xml(hwpx).Descendants(Hp + "newNum"));
        var restart = Assert.Single(Xml(hwpx, "Contents/section1.xml").Descendants(Hp + "newNum"));
        Assert.Equal(("1", "PAGE"), ((string)restart.Attribute("num")!, (string)restart.Attribute("numType")!));
    }
}
