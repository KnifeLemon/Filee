// reStructuredText and LaTeX through Pandoc (optional engine): to and from Markdown, HTML, DOCX, EPUB, and to HWPX
// through Pandoc's AST and the built-in HWPX writer. Skipped when Pandoc is not installed.

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Tests;

public class PandocMarkupTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Rst = """
        Weekly notes
        ============

        Decided: **ship it** and *write docs*.

        - first item
        - second item

        Next steps
        ==========

        Two sections of the same level, so the first one stays a heading (a lone one would become the title).
        """;

    private const string Tex = """
        \documentclass{article}
        \begin{document}
        \section{Results}
        The result is \textbf{good} and \emph{fast}.
        \end{document}
        """;

    private void SkipUnlessInstalled() =>
        Assert.SkipUnless(fx.Catalog.StatusOf(fx.Converters.Single(c => c.Id == "pandoc")).IsAvailable, "Pandoc not installed");

    private async Task<string> WriteAsync(string name, string text)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        return path;
    }

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    [Fact]
    public void Markup_routes()
    {
        var planner = fx.Catalog.CreatePlanner(["pandoc"]);
        foreach (var (from, to) in new[] { ("rst", "docx"), ("rst", "md"), ("tex", "html"), ("tex", "epub"), ("md", "rst"), ("docx", "tex") })
            Assert.Equal("pandoc", Assert.Single(planner.Plan(from, to)!.Steps).Converter.Id);
        Assert.Equal("hwpx-writer", Assert.Single(planner.Plan("rst", "hwpx")!.Steps).Converter.Id);
        Assert.Equal("hwpx-writer", Assert.Single(planner.Plan("tex", "hwpx")!.Steps).Converter.Id);
        Assert.DoesNotContain(fx.Converters.Single(c => c.Id == "pandoc").Edges, e => e.To == "pdf");
    }

    [Fact]
    public async Task Rst_to_docx_markdown_and_hwpx()
    {
        SkipUnlessInstalled();
        var rst = await WriteAsync("notes.rst", Rst);

        var docx = DocxAssert.ReadBack(await ConvertAsync(rst, "docx"));
        var paragraphs = DocxAssert.Paragraphs(docx.Sections.SelectMany(s => s.Blocks)).ToList();
        Assert.Equal(1, paragraphs.Single(p => DocxAssert.TextOf(p) == "Weekly notes").HeadingLevel);

        var md = await File.ReadAllTextAsync(await ConvertAsync(rst, "md"), TestContext.Current.CancellationToken);
        Assert.Contains("# Weekly notes", md);
        Assert.Contains("**ship it**", md);

        var hwpx = await ConvertAsync(rst, "hwpx");
        HwpxAssert.ValidPackage(hwpx);
        Assert.Contains("first item", HwpxAssert.Xml(hwpx).Value);
    }

    [Fact]
    public async Task Latex_to_markdown_epub_and_back()
    {
        SkipUnlessInstalled();
        var tex = await WriteAsync("paper.tex", Tex);

        var md = await File.ReadAllTextAsync(await ConvertAsync(tex, "md"), TestContext.Current.CancellationToken);
        Assert.Contains("# Results", md);
        Assert.Contains("**good**", md);

        using (var epub = ZipFile.OpenRead(await ConvertAsync(tex, "epub")))
            Assert.Equal("application/epub+zip", new StreamReader(epub.GetEntry("mimetype")!.Open()).ReadToEnd());

        var markdown = await WriteAsync("draft.md", "# Title\n\nSome *text*.\n");
        var latex = await File.ReadAllTextAsync(await ConvertAsync(markdown, "tex"), TestContext.Current.CancellationToken);
        Assert.Contains("\\begin{document}", latex); // standalone, ready to compile
        Assert.Contains("\\section{Title}", latex);

        var hwpx = await ConvertAsync(tex, "hwpx");
        HwpxAssert.ValidPackage(hwpx);
        Assert.Contains("The result is", HwpxAssert.Xml(hwpx).Value);
    }
}
