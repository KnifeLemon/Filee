// Word → PDF through LibreOffice: a note whose number is in brackets ("(" ref ")") came out as "()1" in Arabic (issue #620),
// because LibreOffice draws its own label at the start of the note and leaves the brackets behind.

using System.IO.Compression;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
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

    /// <summary>The document with more parts in its package.</summary>
    private static string WithParts(string docx, params (string Part, byte[] Content)[] parts)
    {
        using (var zip = ZipFile.Open(docx, ZipArchiveMode.Update))
        {
            foreach (var (name, content) in parts)
            {
                zip.GetEntry(name)?.Delete();
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(content);
            }
        }
        return docx;
    }

    private static string RawPart(string docx, string name)
    {
        using var zip = ZipFile.OpenRead(docx);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void The_copy_keeps_what_the_link_removal_left_out()
    {
        const string Links = """
            <Relationship Id="rId90" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="file:///C:/Users/someone/id-card.jpg" TargetMode="External"/>
            <Relationship Id="rId91" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="\\127.0.0.1\share\x.png" TargetMode="External"/>
            </Relationships>
            """;
        var folder = fx.NewFolder();
        var path = Document(Reference(1), Bracketed(1));
        WithParts(path, ("word/_rels/document.xml.rels", System.Text.Encoding.UTF8.GetBytes(
            RawPart(path, "word/_rels/document.xml.rels").Replace("</Relationships>", Links))));

        var unlinked = LinkedContent.Strip(path, "docx", folder);
        Assert.NotNull(unlinked);
        var copy = DocxNoteMarks.Prepare(unlinked, folder);

        Assert.NotNull(copy);
        var written = RawPart(copy, "word/_rels/document.xml.rels");
        Assert.DoesNotContain("id-card", written);
        Assert.DoesNotContain("127.0.0.1", written);
        Assert.StartsWith("(1) ", Texts(Part(copy, "word/footnotes.xml").Root!.Elements(W + "footnote").First(n => (string?)n.Attribute(W + "id") == "1")));
    }

    [Fact]
    public void An_xml_part_over_the_limits_is_refused_not_copied()
    {
        var levels = BoundedXml.MaxElementDepth + 1;
        var deep = string.Concat(Enumerable.Repeat("<a>", levels)) + string.Concat(Enumerable.Repeat("</a>", levels));
        var path = WithParts(Document(Reference(1), Bracketed(1)), ("word/document.xml", System.Text.Encoding.UTF8.GetBytes(deep)));

        var refused = Assert.Throws<ConversionRefusedException>(() => DocxNoteMarks.Prepare(path, fx.NewFolder()));

        Assert.Equal("error.document_too_large", refused.ErrorKey);
    }

    [Fact]
    public void A_part_that_unpacks_to_too_much_stops_the_copy_with_the_limits_error()
    {
        var folder = fx.NewFolder();
        var path = WithParts(Document(Reference(1), Bracketed(1)), ("word/media/bomb.bin", new byte[64 * 1024]));
        var output = Path.Combine(folder, "copy.docx");

        var refused = Assert.Throws<ConversionRefusedException>(() => PackageRepacker.Repack(path, output, replaced: null, maxBytes: 32 * 1024));

        Assert.Equal("error.document_too_large", refused.ErrorKey);
        Assert.Contains("word/media/bomb.bin", refused.Detail);
        PackageRepacker.Repack(path, output); // the real limit has room for it
        using var zip = ZipFile.OpenRead(output);
        Assert.NotNull(zip.GetEntry("word/media/bomb.bin"));
    }

    [Fact]
    public async Task A_note_or_run_of_a_million_elements_takes_no_longer_than_reading_it()
    {
        // In the note: a walk per run took six minutes at 200,000 of them, for a file of some kilobytes.
        var runs = string.Concat(Enumerable.Repeat("<w:r><w:t> </w:t></w:r>", 1_000_000));
        var elements = string.Concat(Enumerable.Repeat("<w:tab/>", 500_000));
        var path = Document(
            $"<w:r>{elements}<w:footnoteReference w:id=\"1\"/>{elements}</w:r>",
            $"<w:footnote w:id=\"1\">{DocxBuilder.P(runs + DocxBuilder.R("(", Rtl) + NoteMark() + DocxBuilder.R(") " + Note, Rtl), Bidi)}</w:footnote>");

        var work = Task.Run(() => DocxNoteMarks.Prepare(path, fx.NewFolder()), TestContext.Current.CancellationToken);

        // Far above what it takes (seconds), far below what a walk per element takes (hours).
        Assert.Same(work, await Task.WhenAny(work, Task.Delay(TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken)));
        var copy = await work;
        Assert.NotNull(copy);
        Assert.Contains(" " + Note, Texts(Part(copy, "word/footnotes.xml").Root!));
        var run = Part(copy, "word/document.xml").Descendants(W + "footnoteReference").Single().Parent!;
        Assert.Equal(500_000, run.Elements(W + "tab").Count());
        Assert.Equal(500_000, ((XElement)run.NextNode!).Elements(W + "tab").Count());
    }

    [Fact]
    public void The_formatting_of_a_reference_nested_to_the_limit_is_copied_without_overflowing_the_stack()
    {
        var levels = BoundedXml.MaxElementDepth - 10;
        var format = "<w:rPr>" + string.Concat(Enumerable.Repeat("<w:x>", levels)) + string.Concat(Enumerable.Repeat("</w:x>", levels)) + "</w:rPr>";
        var path = Document($"<w:r>{format}<w:footnoteReference w:id=\"1\"/></w:r>", Bracketed(1));
        string? copy = null;

        // A thread pool thread's stack, where LibreOfficeConverter runs this.
        var thread = new Thread(() => copy = DocxNoteMarks.Prepare(path, fx.NewFolder()), 1536 * 1024);
        thread.Start();
        thread.Join();

        Assert.NotNull(copy);
    }
}
