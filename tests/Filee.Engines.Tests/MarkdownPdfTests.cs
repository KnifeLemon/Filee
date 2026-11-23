// Markdown math as actual PDF glyphs, without turning code examples into equations or emboldening normal glyphs.
// Page preparation runs everywhere; browser integration tests skip when Windows Edge/Chrome is unavailable.

using AngleSharp.Html.Parser;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Text;
using ImageMagick;
using UglyToad.PdfPig;

namespace Filee.Engines.Tests;

public class MarkdownPdfTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Equations = """
        $$
        a=\frac{x+\sqrt{x^2+4y}}{2}\\
        b=\frac{-x+\sqrt{x^2+4y}}{2}
        $$
        """;

    private static void RequireBrowser() =>
        Assert.SkipUnless(MarkdownPdfRenderer.LocateBrowser() is not null, "Windows Edge or Chrome is not installed");

    private ConverterCatalog BrowserCatalog() => new(fx.Converters.Select(c =>
        c is MarkdownConverter ? new MarkdownConverter() : c))
    { Priority = fx.Catalog.Priority };

    [Fact]
    public void Without_a_browser_the_existing_markdown_edges_remain_available()
    {
        var converter = new MarkdownConverter { BrowserPdfEnabled = false };
        Assert.Equal([("md", "html"), ("md", "txt"), ("txt", "txt")], converter.Edges.Select(e => (e.From, e.To)));
        Assert.True(converter.GetStatus().IsAvailable);
    }

    [Fact]
    public void The_browser_edge_is_only_for_direct_markdown_pdf_conversions()
    {
        RequireBrowser();
        var edge = Assert.Single(new MarkdownConverter().Edges, e => e.To == "pdf");
        Assert.Equal("md", edge.From);
        Assert.True(edge.DirectOnly);
        var planner = BrowserCatalog().CreatePlanner();
        Assert.Equal("markdown", Assert.Single(planner.Plan("md", "pdf")!.Steps).Converter.Id);
        Assert.Equal("markdown", Assert.Single(planner.Plan("md", "html")!.Steps).Converter.Id);
    }

    [Fact]
    public void Prepared_pages_embed_mathjax_and_keep_code_out_of_math_processing()
    {
        var folder = fx.NewFolder();
        var html = MarkdownPdfRenderer.PreparePage(Equations + "\n\n```tex\n\\frac{1}{2}\n```", Path.Combine(folder, "sample.md"), Path.Combine(folder, "sample.html"));
        var page = new HtmlParser().ParseDocument(html);
        Assert.Contains(@"\begin{gathered}", Assert.Single(page.QuerySelectorAll("div.math")).TextContent);
        Assert.Contains(@"\frac{1}{2}", Assert.Single(page.QuerySelectorAll("pre code")).TextContent);
        Assert.All(page.QuerySelectorAll("script"), script => Assert.False(script.HasAttribute("src")));
        Assert.Contains("connect-src 'none'", page.QuerySelector("meta[http-equiv='Content-Security-Policy']")!.GetAttribute("content"));
        Assert.Contains("stroke-width:0!important", html); // normal glyphs have no additional outline
    }

    [Fact]
    public void Prepared_pages_preserve_local_pictures_and_remove_active_or_remote_content()
    {
        var folder = fx.NewFolder();
        var picture = EngineFixture.MakeImage(folder, "inside.png", MagickFormat.Png, 64, 64);
        var markdown = $"![inside]({Path.GetFileName(picture)})\n\n![remote](https://example.invalid/picture.png)\n\n<script>document.title='injected';</script>";
        var page = new HtmlParser().ParseDocument(MarkdownPdfRenderer.PreparePage(markdown,
            Path.Combine(folder, "sample.md"), Path.Combine(fx.NewFolder(), "page.html")));
        Assert.StartsWith("data:image/png;base64,", Assert.Single(page.QuerySelectorAll("img")).GetAttribute("src"));
        Assert.DoesNotContain("document.title='injected'", page.DocumentElement.OuterHtml);
    }

    [Fact]
    public async Task Fractions_and_roots_are_visible_pdf_outlines_instead_of_latex_text()
    {
        RequireBrowser();
        var folder = fx.NewFolder();
        var input = Path.Combine(folder, "equations.md");
        var output = Path.Combine(folder, "equations.pdf");
        await MarkdownPdfRenderer.RenderAsync(Equations, input, output, folder, TestContext.Current.CancellationToken);
        using (var pdf = PdfDocument.Open(output))
        {
            Assert.Equal(1, pdf.NumberOfPages);
            Assert.DoesNotContain(@"\frac", pdf.GetPage(1).Text);
            Assert.DoesNotContain(@"\sqrt", pdf.GetPage(1).Text);
        }
        // A PDF with an empty text layer could also have dropped the equations: require visible ink too.
        var job = await fx.ConvertAsync([output], new Preset { TargetFormat = "png" });
        Assert.Equal(JobState.Completed, job.State);
        using var image = new MagickImage(Assert.Single(job.Outputs));
        using var pixels = image.GetPixels();
        var rgb = pixels.ToByteArray(PixelMapping.RGB)!;
        Assert.True(rgb.Count(channel => channel < 100) > 100, "The equations must be drawn, not omitted");
    }

    [Fact]
    public async Task Invalid_math_reports_an_error_instead_of_silently_printing_a_broken_equation()
    {
        RequireBrowser();
        var folder = fx.NewFolder();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => MarkdownPdfRenderer.RenderAsync(
            "$$\n\\notARealMathCommand{1}\n$$", Path.Combine(folder, "bad.md"), Path.Combine(folder, "bad.pdf"), folder,
            TestContext.Current.CancellationToken));
        Assert.Contains("MathJax", error.Message);
    }

    [Fact]
    public async Task Cancelled_rendering_does_not_write_a_pdf()
    {
        RequireBrowser();
        var folder = fx.NewFolder();
        var output = Path.Combine(folder, "cancelled.pdf");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MarkdownPdfRenderer.RenderAsync(Equations,
            Path.Combine(folder, "sample.md"), output, folder, cancellation.Token));
        Assert.False(File.Exists(output));
    }
}
