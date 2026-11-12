// Line breaks inside HWPX paragraphs (<hp:lineBreak/>), which Unhwp skips, through HWPX → TXT, Markdown and HTML
// (issue #1416). The HWPX files are written by Filee's own HWPX writer.

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Tests;

public class UnhwpLineBreakTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Mark = "";

    private static readonly HCharFormat Plain = default;

    private static HParagraph Paragraph(params HInline[] inlines)
    {
        var paragraph = new HParagraph();
        paragraph.Inlines.AddRange(inlines);
        return paragraph;
    }

    /// <summary>A paragraph of the lines, a line break between each two.</summary>
    private static HParagraph Lines(params string[] lines)
    {
        var paragraph = Paragraph(new HText(lines[0], Plain));
        foreach (var line in lines.Skip(1))
            paragraph.Inlines.AddRange([new HLineBreak(Plain), new HText(line, Plain)]);
        return paragraph;
    }

    private static string Write(string folder, params HBlock[] blocks)
    {
        var section = new HSection();
        section.Blocks.AddRange(blocks);
        var document = new HDocument();
        document.Sections.Add(section);
        var path = Path.Combine(folder, "code.hwpx");
        HwpxWriter.Write(document, path);
        return path;
    }

    /// <summary>The code of the issue: an empty line, indentation, a line that would read as a heading in Markdown.</summary>
    private string Code(string folder) =>
        Write(folder, Lines("import a;", "", "class X {", "    int y; // # 1", "# not a heading", "}"), Paragraph(new HText("Second para.", Plain)));

    private async Task<string> ConvertAsync(string hwpx, string target)
    {
        var job = await fx.ConvertAsync([hwpx], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return (await File.ReadAllTextAsync(job.Outputs.Single(), TestContext.Current.CancellationToken)).ReplaceLineEndings("\n");
    }

    private void SkipUnlessUnhwp()
    {
        var status = fx.Catalog.StatusOf(fx.Converters.Single(c => c.Id == "unhwp"));
        Assert.SkipUnless(status.IsAvailable, $"unhwp not available ({status.ReasonKey})");
    }

    [Fact]
    public async Task Text_keeps_the_lines_their_indentation_and_empty_lines()
    {
        SkipUnlessUnhwp();
        var text = await ConvertAsync(Code(fx.NewFolder()), "txt");

        Assert.Equal("import a;\n\nclass X {\n    int y; // # 1\n# not a heading\n}\nSecond para.", text.Trim());
    }

    [Fact]
    public async Task Markdown_has_hard_breaks()
    {
        SkipUnlessUnhwp();
        var markdown = await ConvertAsync(Code(fx.NewFolder()), "md");

        Assert.Equal("import a;\\\n\\\nclass X {\\\n    int y; // # 1\\\n\\# not a heading\\\n}\n\nSecond para.", markdown.Trim());
    }

    [Fact]
    public async Task Html_has_a_break_for_each_line_and_keeps_the_indentation()
    {
        SkipUnlessUnhwp();
        var html = await ConvertAsync(Code(fx.NewFolder()), "html");

        Assert.Contains("import a;<br />\n<br />\nclass X {<br />\n    int y; // # 1<br />\n# not a heading<br />\n}</p>", html);
        Assert.DoesNotContain("<h1", html);
        Assert.DoesNotContain(Mark, html);
    }

    [Fact]
    public async Task Bold_lines_and_table_cells_keep_their_breaks()
    {
        SkipUnlessUnhwp();
        var bold = new HCharFormat(Bold: true);
        var table = new HTable { ColumnCount = 2 };
        foreach (var texts in new string[][] { ["cell a", "cell b"], ["r2"] })
        {
            var row = new HRow();
            foreach (var cellText in new[] { Lines(texts), Paragraph(new HText("other", Plain)) })
            {
                var cell = new HCell();
                cell.Blocks.Add(cellText);
                row.Cells.Add(cell);
            }
            table.Rows.Add(row);
        }
        var hwpx = Write(fx.NewFolder(), Paragraph(new HText("Label", bold), new HLineBreak(bold), new HText("value", Plain), new HLineBreak(Plain)), table);

        var markdown = await ConvertAsync(hwpx, "md");
        // Unhwp closes the bold after the break ("**Label⏎**value"); it moves before the break. The break that ends the paragraph goes.
        Assert.Contains("**Label**\\\nvalue\n", markdown);
        Assert.Contains("| cell a<br>cell b |", markdown);

        var html = await ConvertAsync(hwpx, "html");
        Assert.Contains("<strong>Label</strong><br />\nvalue</p>", html);
        Assert.Contains("cell a<br>cell b", html);
    }

    [Fact]
    public async Task A_document_without_line_breaks_is_read_as_it_is()
    {
        SkipUnlessUnhwp();
        var dir = fx.NewFolder();
        var hwpx = Write(dir, Paragraph(new HText("One line.", Plain)), Paragraph(new HText("    Indented line.", Plain)));
        var work = Directory.CreateDirectory(Path.Combine(dir, "work")).FullName;

        Assert.Null(UnhwpLineBreaks.Mark(hwpx, work));
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));

        using var unhwp = Unhwp.UnhwpDocument.ParseFile(hwpx);
        Assert.Equal(unhwp.ToText().ReplaceLineEndings("\n").Trim(), (await ConvertAsync(hwpx, "txt")).Trim());
        Assert.Equal("One line.\n\n    Indented line.", (await ConvertAsync(hwpx, "md")).Trim());
    }

    [Fact]
    public void Line_breaks_of_any_prefix_become_the_mark_and_other_namespaces_stay()
    {
        var dir = fx.NewFolder();
        var hwpx = Write(dir, Lines("a", "b", "c"), Paragraph(new HText("Private  use.", Plain)));
        // Another prefix for the paragraph namespace, and a lineBreak of a namespace that isn't HWPML.
        var xml = Section(hwpx);
        var first = xml.IndexOf("<hp:lineBreak/>", StringComparison.Ordinal);
        xml = xml[..first] + "<p:lineBreak xmlns:p=\"http://www.hancom.co.kr/hwpml/2011/paragraph\"/><o:lineBreak xmlns:o=\"urn:other\"/>"
            + xml[(first + "<hp:lineBreak/>".Length)..];
        using (var zip = ZipFile.Open(hwpx, ZipArchiveMode.Update))
        {
            zip.GetEntry("Contents/section0.xml")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("Contents/section0.xml").Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }
        var work = Directory.CreateDirectory(Path.Combine(dir, "work")).FullName;

        var (copy, mark) = UnhwpLineBreaks.Mark(hwpx, work)!.Value;

        Assert.Equal('', mark); // U+F8FF is in the text
        Assert.StartsWith(work, copy, StringComparison.Ordinal);
        Assert.Equal(xml, Section(hwpx)); // the input stays as it was
        var marked = Section(copy);
        Assert.Contains($"<hp:t>a{mark}<o:lineBreak xmlns:o=\"urn:other\" />b{mark}c</hp:t>", marked);
        Assert.DoesNotContain("hp:lineBreak", marked);
        Assert.DoesNotContain("p:lineBreak xmlns:p", marked);
    }

    [Theory]
    [InlineData("a" + Mark + "b", false, "a\\\nb")]
    [InlineData("a" + Mark + Mark + "    b", false, "a\\\n\\\n    b")]
    [InlineData("a" + Mark + "    b", true, "a\\\n&nbsp;&nbsp;&nbsp;&nbsp;b")]
    [InlineData("a" + Mark + "# h" + Mark + "- l" + Mark + "1. n" + Mark + "> q" + Mark + "---" + Mark + "<div>" + Mark + ": d", false,
        "a\\\n\\# h\\\n\\- l\\\n1\\. n\\\n\\> q\\\n\\---\\\n\\<div>\\\n\\: d")]
    [InlineData("a" + Mark + "#hashtag" + Mark + "-1" + Mark + "**b**", false, "a\\\n#hashtag\\\n-1\\\n**b**")]
    [InlineData("**bold" + Mark + "**plain", false, "**bold**\\\nplain")]
    [InlineData("plain**" + Mark + "bold**", false, "plain\\\n**bold**")]
    [InlineData("*it" + Mark + "alic*", false, "*it\\\nalic*")]
    [InlineData("end" + Mark + Mark, false, "end")]
    [InlineData("**end" + Mark + "**", false, "**end**")]
    [InlineData("| a" + Mark + "b | c |", false, "| a<br>b | c |")]
    [InlineData("# Title" + Mark + "sub", false, "# Title<br>sub")]
    [InlineData("no breaks", false, "no breaks")]
    public void Markdown_lines(string markdown, bool html, string expected) =>
        Assert.Equal(expected, UnhwpLineBreaks.Markdown(markdown, Mark[0], html));

    private static string Section(string hwpx)
    {
        using var zip = ZipFile.OpenRead(hwpx);
        using var reader = new StreamReader(zip.GetEntry("Contents/section0.xml")!.Open());
        return reader.ReadToEnd();
    }
}
