// A continuous section break that brings a new header (issue #1152): one 한글 section, with the header's control in the
// paragraph where the Word section starts.

using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using UglyToad.PdfPig;
using static Filee.Engines.Tests.DocxBuilder;
using static Filee.Engines.Tests.HwpxAssert;

namespace Filee.Engines.Tests;

public class DocxSectionHeaderTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static string Reference(string id, string kind = "header") => $"<w:{kind}Reference w:type=\"default\" r:id=\"{id}\"/>";

    private static string SectionEnd(string properties = "") => P("", $"<w:sectPr>{properties}{DefaultPage}</w:sectPr>");

    private const string Continuous = "<w:type w:val=\"continuous\"/>";

    private static string NextPage(string text) => $"<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>{P(R(text))}";

    private HDocument Read(DocxBuilder docx)
    {
        var dir = fx.NewFolder();
        return DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media"));
    }

    private static string Text(HHeaderFooter item) =>
        string.Concat(item.Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HText>().Select(t => t.Text));

    private async Task<List<string>> PagesAsync(DocxBuilder docx)
    {
        var rhwp = fx.Converters.Single(c => c.Id == "rhwp");
        Assert.SkipUnless(fx.Catalog.StatusOf(rhwp).IsAvailable, "rhwp not found (pwsh build/fetch-engines.ps1 -Only rhwp)");
        var job = await fx.ConvertAsync([docx.Save(Path.Combine(fx.NewFolder(), "source.docx"))], new Preset { TargetFormat = "pdf" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        using var document = PdfDocument.Open(job.Outputs.Single());
        return document.GetPages().Select(p => p.Text).ToList();
    }

    [Fact]
    public async Task The_header_of_a_continuous_section_is_drawn_when_the_section_before_has_none()
    {
        var docx = new DocxBuilder();
        var header = docx.Header(P(R("LogoHeader")));
        docx.Paragraph(P(R("Title")) + SectionEnd() + P(R("Contents")) + NextPage("Second"));
        docx.SectionProperties = Reference(header) + Continuous + DefaultPage;

        var pages = await PagesAsync(docx);

        Assert.Equal(2, pages.Count); // no new page for the section
        Assert.Contains("LogoHeader", pages[1]);
    }

    [Fact]
    public async Task The_header_of_a_continuous_section_replaces_the_one_before()
    {
        var docx = new DocxBuilder();
        var (one, two) = (docx.Header(P(R("HeaderOne"))), docx.Header(P(R("HeaderTwo"))));
        docx.Paragraph(P(R("Contents")) + SectionEnd(Reference(one)) + P(R("After")) + NextPage("Second"));
        docx.SectionProperties = Reference(two) + Continuous + DefaultPage;

        var pages = await PagesAsync(docx);

        Assert.Contains("HeaderTwo", pages[1]);
        Assert.DoesNotContain("HeaderOne", pages[1]);
    }

    [Fact]
    public void A_next_page_section_inherits_the_header_of_a_continuous_one()
    {
        var docx = new DocxBuilder();
        var (one, two) = (docx.Header(P(R("HeaderOne"))), docx.Header(P(R("HeaderTwo"))));
        docx.Paragraph(P(R("First")) + SectionEnd(Reference(one)) + P(R("Second")) + SectionEnd(Reference(two) + Continuous) + P(R("Third")));

        var document = Read(docx);

        Assert.Equal(2, document.Sections.Count);
        Assert.Equal(["HeaderOne", "HeaderTwo"], document.Sections[0].Headers.Select(Text));
        Assert.Equal("HeaderTwo", Text(Assert.Single(document.Sections[1].Headers)));
    }

    [Fact]
    public void A_continuous_section_without_a_reference_adds_no_header()
    {
        var docx = new DocxBuilder();
        var header = docx.Header(P(R("HeaderOne")));
        docx.Paragraph(P(R("First")) + SectionEnd(Reference(header)) + P(R("Second")));
        docx.SectionProperties = Continuous + DefaultPage;

        var section = Assert.Single(Read(docx).Sections);

        Assert.Null(Assert.Single(section.Headers).StartsAt);
    }

    [Fact]
    public void The_controls_go_into_the_paragraph_where_the_section_starts()
    {
        var dir = fx.NewFolder();
        var docx = new DocxBuilder();
        var (header, footer) = (docx.Header(P(R("HeaderOne"))), docx.Footer(P(R("FooterOne"))));
        docx.Paragraph(P(R("First")) + SectionEnd() + P(R("Second")));
        docx.SectionProperties = Reference(header) + Reference(footer, "footer") + Continuous + DefaultPage;
        var hwpx = Path.Combine(dir, "out.hwpx");
        HwpxWriter.Write(DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media")), hwpx);
        ValidPackage(hwpx);

        var paragraph = Xml(hwpx).Elements(Hp + "p").Single(p => p.Elements(Hp + "run").Any(r => r.Descendants(Hp + "header").Any()));
        Assert.Single(paragraph.Descendants(Hp + "footer"));
        Assert.Contains("Second", paragraph.Elements(Hp + "run").Elements(Hp + "t").Select(t => t.Value));
    }
}
