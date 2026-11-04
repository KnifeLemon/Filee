// Word → PDF through LibreOffice: a note whose number is in brackets ("(" ref ")") came out as "()1" in Arabic (issue #620),
// because LibreOffice draws its own label at the start of the note and leaves the brackets behind.

using System.IO.Compression;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Office;
using Filee.Engines.Pdf;

namespace Filee.Engines.Tests;

public class DocxNoteMarksTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Rtl = "<w:rtl/>";
    private const string Bidi = "<w:bidi/>";
    private const string Note = "هذه حاشية سفلية باللغة العربية.";

    private static string Reference(int id, string attributes = "") =>
        $"<w:r><w:rPr><w:vertAlign w:val=\"superscript\"/></w:rPr><w:footnoteReference w:id=\"{id}\"{attributes}/></w:r>";

    private static string NoteMark() => $"<w:r><w:rPr>{Rtl}</w:rPr><w:footnoteRef/></w:r>";

    /// <summary>A note in Word's usual layout for Arabic papers: its number in brackets.</summary>
    private static string Bracketed(int id, string text = Note) =>
        $"<w:footnote w:id=\"{id}\">{DocxBuilder.P(DocxBuilder.R("(", Rtl) + NoteMark() + DocxBuilder.R(") " + text, Rtl), Bidi)}</w:footnote>";

    private string Document(string body, string notes, string name = "paper.docx")
    {
        var builder = new DocxBuilder();
        builder.Paragraph(DocxBuilder.P(body, Bidi));
        builder.Footnotes(notes);
        return builder.Save(Path.Combine(fx.NewFolder(), name));
    }

    private static XDocument Part(string docx, string name)
    {
        using var zip = ZipFile.OpenRead(docx);
        using var stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static string Texts(XElement element) => string.Concat(element.Descendants(W + "t").Select(t => t.Value));

    [Fact]
    public void A_bracketed_note_number_becomes_text_where_word_has_it()
    {
        var path = Document(
            DocxBuilder.R("نص البحث ", Rtl) + DocxBuilder.R("(", Rtl) + Reference(1) + DocxBuilder.R(")", Rtl) + DocxBuilder.R(" ثم ", Rtl) + Reference(2),
            Bracketed(1) + $"<w:footnote w:id=\"2\">{DocxBuilder.P(NoteMark() + DocxBuilder.R(" حاشية أخرى.", Rtl), Bidi)}</w:footnote>");

        var copy = DocxNoteMarks.Prepare(path, fx.NewFolder());

        Assert.NotNull(copy);
        Assert.Equal(Path.GetFileName(path), Path.GetFileName(copy));
        var notes = Part(copy, "word/footnotes.xml").Root!.Elements(W + "footnote").Where(n => (string?)n.Attribute(W + "type") is null).ToList();
        Assert.Equal("(1) " + Note, Texts(notes[0]));
        Assert.Equal("2 حاشية أخرى.", Texts(notes[1])); // all notes change: a custom mark doesn't count in the numbering
        Assert.Empty(notes.Descendants(W + "footnoteRef"));

        var body = Part(copy, "word/document.xml").Root!;
        Assert.Equal("نص البحث (​1) ثم ​2", Texts(body));
        var references = body.Descendants(W + "footnoteReference").ToList();
        Assert.Equal(2, references.Count);
        Assert.All(references, r => Assert.Equal("1", (string?)r.Attribute(W + "customMarkFollows")));
        // The number keeps the look of the reference.
        var number = body.Descendants(W + "t").First(t => t.Value == "1").Parent!;
        Assert.NotNull(number.Element(W + "rPr")?.Element(W + "vertAlign"));
    }

    [Fact]
    public void A_note_with_its_number_first_needs_no_copy()
    {
        var path = Document(Reference(1),
            $"<w:footnote w:id=\"1\">{DocxBuilder.P(NoteMark() + DocxBuilder.R(") " + Note, Rtl), Bidi)}</w:footnote>");

        Assert.Null(DocxNoteMarks.Prepare(path, fx.NewFolder()));
    }

    [Fact]
    public void Numbers_that_depend_on_the_pages_stay_with_libreoffice()
    {
        var builder = new DocxBuilder();
        builder.Paragraph(DocxBuilder.P(Reference(1), "<w:sectPr><w:footnotePr><w:numRestart w:val=\"eachPage\"/></w:footnotePr></w:sectPr>"));
        builder.Footnotes(Bracketed(1));
        var path = builder.Save(Path.Combine(fx.NewFolder(), "restart.docx"));

        Assert.Null(DocxNoteMarks.Prepare(path, fx.NewFolder()));
    }

    [Fact]
    public void Deleted_references_and_custom_marks_are_not_counted()
    {
        var path = Document(
            $"<w:del w:id=\"9\" w:author=\"a\">{Reference(5)}</w:del>" + Reference(6, " w:customMarkFollows=\"1\"") + DocxBuilder.R("*") + Reference(1),
            Bracketed(1) + "<w:footnote w:id=\"5\"><w:p/></w:footnote><w:footnote w:id=\"6\"><w:p/></w:footnote>");

        var copy = DocxNoteMarks.Prepare(path, fx.NewFolder());

        Assert.NotNull(copy);
        Assert.StartsWith("(1) ", Texts(Part(copy, "word/footnotes.xml").Root!.Elements(W + "footnote").First(n => (string?)n.Attribute(W + "id") == "1")));
    }

    [Fact]
    public async Task The_number_stays_inside_its_brackets_in_the_pdf()
    {
        var available = fx.Converters.Where(c => fx.Catalog.StatusOf(c).IsAvailable).Select(c => c.Id).ToHashSet();
        Assert.SkipUnless(available.Contains("libreoffice"), "LibreOffice is not available");
        var path = Document(
            DocxBuilder.R("نص البحث ", Rtl) + DocxBuilder.R("(", Rtl) + Reference(1) + DocxBuilder.R(")", Rtl) + DocxBuilder.R(" ثم نهاية الفقرة.", Rtl),
            Bracketed(1));

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "pdf" });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var text = PdfDocumentReader.PlainText(PdfDocumentReader.ReadText(Assert.Single(job.Outputs)));
        // The number between the brackets, whichever way this reader reads them.
        Assert.Matches(@"[()]1[()] " + Note, text);
    }
}
