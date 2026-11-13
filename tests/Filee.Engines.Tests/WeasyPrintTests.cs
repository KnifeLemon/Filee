// WeasyPrint (#1403): its routes (HTML, Markdown, e-mails → PDF only, and only when it is installed), the page Filee
// hands it (pictures beside the document as data: URIs, nothing else it could load), and, with WeasyPrint installed,
// the PDFs: a style sheet by file: URL, a picture by absolute path, one by relative path outside the folder and one
// by https: address are never loaded, neither from Filee's page nor when WeasyPrint gets the page as it is.
//
// Every page sits in "inbox" with a picture in "inbox/img" (4×4, kept); "private" next to it holds a picture (6×6)
// and two style sheets that must never be read. The https: address is a listener on this computer that counts
// connections, so nothing goes to the internet.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Text;
using ImageMagick;
using UglyToad.PdfPig;

namespace Filee.Engines.Tests;

public sealed partial class WeasyPrintTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>The engines the route tests count as installed besides WeasyPrint, so routes don't depend on this PC.</summary>
    private static readonly string[] Others = ["rhwp", "pandoc", "libreoffice", "calibre"];

    private bool HasWeasyPrint => fx.Catalog.StatusOf(fx.Converters.Single(c => c.Id == "weasyprint")).IsAvailable;

    private const string NotInstalled = "WeasyPrint not found (pwsh build/fetch-engines.ps1 -Only weasyprint)";

    private static IEnumerable<string> Ids(Route? route) => route?.Steps.Select(s => s.Converter.Id) ?? [];

    private RoutePlanner WithoutWeasyPrint() =>
        new ConverterCatalog(fx.Converters.Where(c => c.Id != "weasyprint")) { Priority = fx.Catalog.Priority }.CreatePlanner(Others);

    // ───────────────────────── Routes ─────────────────────────

    [Fact]
    public void WeasyPrint_makes_the_pdfs_of_pages_markdown_and_e_mails()
    {
        var planner = fx.Catalog.CreatePlanner([.. Others, "weasyprint"]);

        Assert.Equal(["weasyprint"], Ids(planner.Plan("html", "pdf")));
        Assert.Equal(["markdown", "weasyprint"], Ids(planner.Plan("md", "pdf")));
        foreach (var from in new[] { "eml", "msg", "mhtml" })
            Assert.Equal(["email", "weasyprint"], Ids(planner.Plan(from, "pdf")));
        Assert.Equal(["weasyprint", "pdfium"], Ids(planner.Plan("html", "png")));
        // It shapes text, so a page in Arabic or Hindi takes it too.
        Assert.Equal(["weasyprint"], Ids(planner.Plan("html", "pdf", shaping: true)));
        Assert.Equal(["markdown", "weasyprint"], Ids(planner.Plan("md", "pdf", shaping: true)));
    }

    [Fact]
    public void WeasyPrint_wins_wherever_it_is_in_the_engine_order()
    {
        foreach (var index in new[] { 0, fx.Catalog.Priority.Count })
        {
            var order = fx.Catalog.Priority.Where(id => id != "weasyprint").ToList();
            order.Insert(Math.Min(index, order.Count), "weasyprint");
            var planner = new ConverterCatalog(fx.Converters) { Priority = order }.CreatePlanner([.. Others, "weasyprint"]);
            foreach (var from in WeasyPrintConverter.Sources)
                Assert.Equal("weasyprint", Ids(planner.Plan(from, "pdf")).Last());
        }
    }

    [Fact]
    public void Every_other_route_is_the_same_with_or_without_WeasyPrint()
    {
        var with = fx.Catalog.CreatePlanner([.. Others, "weasyprint"]);
        var without = WithoutWeasyPrint();

        // A Word document, an e-book, a PDF, ... never goes through a page to reach WeasyPrint.
        foreach (var from in FormatRegistry.Known.Select(f => f.Id).Where(id => !WeasyPrintConverter.Sources.Contains(id)))
            foreach (var to in new[] { "pdf", "png", "jpg", "docx", "hwpx", "txt", "html" })
                Assert.True(Ids(without.Plan(from, to)).SequenceEqual(Ids(with.Plan(from, to))), $"{from} → {to}");
        // Pages, Markdown and e-mails keep their routes to everything but PDF and the pictures made from it.
        foreach (var from in WeasyPrintConverter.Sources)
            foreach (var to in new[] { "docx", "hwpx", "txt", "epub", "odt", "html", "md" })
                Assert.True(Ids(without.Plan(from, to)).SequenceEqual(Ids(with.Plan(from, to))), $"{from} → {to}");
    }

    [Fact]
    public void Without_WeasyPrint_no_route_takes_it()
    {
        var planner = WithoutWeasyPrint();
        foreach (var from in WeasyPrintConverter.Sources)
            Assert.DoesNotContain("weasyprint", Ids(planner.Plan(from, "pdf")));
        // The edge itself is held to its sources.
        var edge = Assert.Single(new WeasyPrintConverter().Edges);
        Assert.Equal(("html", "pdf"), (edge.From, edge.To));
        Assert.Equal(WeasyPrintConverter.Sources, edge.Sources);
    }

    [Fact]
    public void A_source_list_keeps_an_edge_out_of_routes_from_other_formats()
    {
        var planner = new RoutePlanner([new FakeConverter("a", [new("x", "y")]), new FakeConverter("b", [new("y", "z", Sources: ["y"])])]);
        Assert.Null(planner.Plan("x", "z"));
        Assert.Equal(["b"], Ids(planner.Plan("y", "z")));
    }

    private sealed class FakeConverter(string id, IReadOnlyList<ConversionEdge> edges) : IConverter
    {
        public string Id => id;
        public string DisplayName => id;
        public IReadOnlyList<ConversionEdge> Edges => edges;
        public int MaxParallelism => 1;
        public EngineStatus GetStatus() => EngineStatus.Available();
        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void WeasyPrint_is_a_windows_download_found_only_in_fileees_engine_folders()
    {
        var component = EngineDownloads.ComponentsForRuntime("win-x64")["weasyprint"];
        Assert.Equal("70.0", component.Version);
        Assert.EndsWith("/v70.0/weasyprint-windows-onedir.zip", component.Url, StringComparison.Ordinal);
        Assert.Equal("zip", component.Kind);
        foreach (var runtime in new[] { "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" })
            Assert.False(EngineDownloads.ComponentsForRuntime(runtime).ContainsKey("weasyprint"), runtime);
        Assert.Equal(OperatingSystem.IsWindows(), EngineDownloads.Packages.Any(p => p.Id == "weasyprint"));
        // Offered, not ticked by default: it changes how pages become PDFs.
        Assert.False(EngineDownloads.IsSuggested(new EnginePackage("weasyprint", ["weasyprint"], 65_000_000, ["weasyprint"])));
        Assert.Equal(["--allowed-protocols", "data"], WeasyPrintConverter.Arguments("page.html", "out.pdf")[..2]);
    }

    [Fact]
    public void The_onedir_zip_keeps_the_program_folder_and_its_licence()
    {
        var staging = fx.NewFolder();
        var folder = Directory.CreateDirectory(Path.Combine(staging, "onedir", "weasyprint")).FullName;
        File.WriteAllText(Path.Combine(folder, EngineEnvironment.ProgramName("weasyprint.exe")), "");
        Directory.CreateDirectory(Path.Combine(folder, "_internal"));
        File.WriteAllText(Path.Combine(staging, "LICENSE"), "BSD 3-Clause License");
        File.WriteAllText(Path.Combine(staging, "README.rst"), "WeasyPrint");

        Assert.Equal(folder, EngineInstaller.WeasyPrintFolder(staging));
        Assert.True(File.Exists(Path.Combine(folder, "LICENSE")));
        Assert.Equal(folder, EngineEnvironment.FolderWithPrograms("weasyprint", folder));
    }

    // ───────────────────────── The page WeasyPrint gets (no WeasyPrint needed) ─────────────────────────

    /// <summary>"inbox/page.html" (or another document there) with "inbox/img/inside.png" and the files in "private".</summary>
    private sealed record Folders(string Inbox, string Inside, string Secret, string Css, string ImportedCss);

    private Folders Make()
    {
        var root = fx.NewFolder();
        var inbox = Path.Combine(root, "inbox");
        var inside = Path.Combine(inbox, "img", "inside.png");
        var secret = Path.Combine(root, "private", "secret.png");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secret)!);
        File.WriteAllBytes(inside, EbookBuilders.Png(4, 4, MagickColors.Teal));
        File.WriteAllBytes(secret, EbookBuilders.Png(6, 6, MagickColors.Red));
        var css = Path.Combine(root, "private", "linked.css");
        File.WriteAllText(css, "h1::after { content: \"LINKEDSTYLE\"; }");
        var imported = Path.Combine(root, "private", "imported.css");
        File.WriteAllText(imported, "h1::before { content: \"IMPORTEDSTYLE\"; }");
        return new Folders(inbox, inside, secret, css, imported);
    }

    /// <summary>
    /// A page with the four references WeasyPrint must not load (a file: style sheet, a picture by absolute path, one by
    /// relative path outside the folder, one by https: address), an @import and a CSS url(), the picture beside it,
    /// and <paramref name="extra"/>.
    /// </summary>
    private static string Page(Folders folders, int port, string extra = "")
    {
        var page = Path.Combine(folders.Inbox, "page.html");
        File.WriteAllText(page, $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"><title>Report</title>
            <link rel="stylesheet" href="{{new Uri(folders.Css).AbsoluteUri}}">
            <style>@import url("{{new Uri(folders.ImportedCss).AbsoluteUri}}"); body { background-image: url("../private/secret.png"); }</style>
            </head><body>
            <h1>Quarterly report</h1>
            <p><img src="img/inside.png" alt="kept"></p>
            <p><img src="{{folders.Secret}}" alt="absolute"></p>
            <p><img src="../private/secret.png" alt="relative"></p>
            <p><img src="https://127.0.0.1:{{port}}/pixel.png" alt="remote"></p>
            {{extra}}
            </body></html>
            """, new UTF8Encoding(false));
        return page;
    }

    /// <summary>The pictures of the page's data: URIs, decoded: their sizes.</summary>
    private static List<(uint Width, uint Height)> DataPictures(string html) =>
        DataUri().Matches(html).Select(m =>
        {
            using var image = new MagickImage(Convert.FromBase64String(m.Groups[1].Value));
            return (image.Width, image.Height);
        }).ToList();

    private static void AssertNothingLoadable(string html, Folders folders)
    {
        foreach (var gone in new[] { "file:", "https:", "127.0.0.1", "../", "secret.png", "linked.css", "imported.css", "@import", folders.Secret })
            Assert.DoesNotContain(gone, html, StringComparison.OrdinalIgnoreCase);
        // Every address left is a data: URI or names an element of the page (a <link> stays, without its href).
        Assert.All(Address().Matches(html),
            m => Assert.True(m.Groups[1].Value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || m.Groups[1].Value.StartsWith('#'), m.Value));
    }

    [Fact]
    public void The_page_WeasyPrint_gets_holds_only_the_picture_beside_it_as_a_data_uri()
    {
        var folders = Make();
        var page = Page(folders, port: 9, extra: """<iframe src="https://example.com/"></iframe><object data="../private/secret.png"></object><svg><image href="../private/secret.png"/></svg><p style="background: url('../private/secret.png')">styled</p>""");

        var html = File.ReadAllText(WeasyPrintConverter.PreparePage(page, fx.NewFolder(), page));

        Assert.Equal([(4u, 4u)], DataPictures(html));
        AssertNothingLoadable(html, folders);
        Assert.Contains("Quarterly report", html, StringComparison.Ordinal);
        Assert.True(File.Exists(folders.Secret)); // the user's files are untouched
    }

    [Fact]
    public void The_page_of_a_markdown_file_holds_only_the_picture_beside_the_markdown()
    {
        var folders = Make();
        var markdown = Path.Combine(folders.Inbox, "notes.md");
        File.WriteAllText(markdown, $"# Notes\n\n![kept](img/inside.png)\n\n![absolute](<{folders.Secret}>)\n\n![relative](../private/secret.png)\n\n![remote](https://127.0.0.1:9/pixel.png)\n");
        // The Markdown step writes its page into the job's folder, linking the pictures by file: URL (MarkdownConverter).
        var step = Path.Combine(fx.NewFolder(), "notes.html");
        File.WriteAllText(step, MarkdownConverter.ToHtml(File.ReadAllText(markdown), markdown, step));
        Assert.Contains("file:", File.ReadAllText(step), StringComparison.Ordinal);

        var html = File.ReadAllText(WeasyPrintConverter.PreparePage(step, fx.NewFolder(), markdown));

        Assert.Equal([(4u, 4u)], DataPictures(html));
        AssertNothingLoadable(html, folders);
    }

    [Fact]
    public void A_picture_svg_beside_the_page_is_embedded_too()
    {
        var folders = Make();
        File.WriteAllText(Path.Combine(folders.Inbox, "img", "chart.svg"), """<svg xmlns="http://www.w3.org/2000/svg" width="5" height="5"><rect width="5" height="5"/></svg>""");
        var page = Path.Combine(folders.Inbox, "chart.html");
        File.WriteAllText(page, """<html><body><img src="img/chart.svg"></body></html>""");

        var html = File.ReadAllText(WeasyPrintConverter.PreparePage(page, fx.NewFolder(), page));

        Assert.Contains("src=\"data:image/svg+xml;base64,", html, StringComparison.Ordinal);
    }

    /// <summary>An inline SVG whose parts refer to each other: a symbol used twice, a gradient built on another, url(#…) fills.</summary>
    private const string InlineSvg = """
        <style>.bar { fill: url(#g); }</style>
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="200" height="60">
        <defs><linearGradient id="base"><stop offset="0" stop-color="teal"/></linearGradient>
        <linearGradient id="g" xlink:href="#base"/><text id="s" x="5" y="20">USEDSYMBOL</text></defs>
        <use href="#s"/><use xlink:href="#s" y="20"/><rect class="bar" style="fill: url(#g)" y="45" width="100" height="10"/>
        </svg>
        """;

    [Fact]
    public void References_to_the_pages_own_elements_stay()
    {
        var folders = Make();
        var page = Page(folders, port: 9, extra: InlineSvg);

        var html = File.ReadAllText(WeasyPrintConverter.PreparePage(page, fx.NewFolder(), page));

        Assert.Contains("<use href=\"#s\"/>", html, StringComparison.Ordinal);
        Assert.Contains("<use xlink:href=\"#s\"", html, StringComparison.Ordinal);
        Assert.Contains("xlink:href=\"#base\"", html, StringComparison.Ordinal);
        Assert.Contains(".bar { fill: url(#g); }", html, StringComparison.Ordinal);
        Assert.Contains("style=\"fill: url(#g)\"", html, StringComparison.Ordinal);
        AssertNothingLoadable(html, folders);
    }

    [Fact]
    public void Off_windows_a_missing_weasyprint_is_not_offered_as_an_install()
    {
        Assert.SkipWhen(WeasyPrintConverter.Locate() is not null, "a WeasyPrint is installed here");
        Assert.Equal(OperatingSystem.IsWindows() ? "engine.reason.not_installed" : "engine.reason.unsupported_platform",
            new WeasyPrintConverter().GetStatus().ReasonKey);
    }

    [GeneratedRegex("data:image/[a-z]+;base64,([A-Za-z0-9+/=]+)")]
    private static partial Regex DataUri();

    [GeneratedRegex("(?:src|href)\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex Address();

    // ───────────────────────── With WeasyPrint ─────────────────────────

    /// <summary>A listener on this computer: the https: pictures point at it, and it counts who connects.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public Listener() => _listener.Start();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>True once anything connected.</summary>
        public bool Contacted => _listener.Pending();

        public void Dispose() => _listener.Stop();
    }

    private static List<(int Width, int Height)> PdfPictures(string pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.GetPages().SelectMany(p => p.GetImages()).Select(i => (i.WidthInSamples, i.HeightInSamples)).ToList();
    }

    private static string PdfText(string pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Concat(document.GetPages().Select(p => p.Text));
    }

    private async Task<string> ToPdfAsync(string input)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = "pdf" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    [Fact]
    public async Task A_page_becomes_a_pdf_with_only_the_picture_beside_it()
    {
        Assert.SkipUnless(HasWeasyPrint, NotInstalled);
        var folders = Make();
        using var listener = new Listener();
        var page = Page(folders, listener.Port);
        Assert.Equal(["weasyprint"], Ids(fx.Catalog.CreatePlanner().Plan("html", "pdf")));

        var pdf = await ToPdfAsync(page);

        Assert.Equal([(4, 4)], PdfPictures(pdf));
        var text = PdfText(pdf);
        Assert.Contains("Quarterly report", text, StringComparison.Ordinal);
        Assert.DoesNotContain("LINKEDSTYLE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IMPORTEDSTYLE", text, StringComparison.Ordinal);
        Assert.False(listener.Contacted);
    }

    [Fact]
    public async Task WeasyPrint_itself_refuses_everything_but_data_uris()
    {
        Assert.SkipUnless(HasWeasyPrint, NotInstalled);
        var folders = Make();
        using var listener = new Listener();
        // The page as it is, without Filee's copy: the lock holds by itself. Only the 3×3 data: picture loads, not even
        // the picture beside the page, which Filee would have embedded.
        var dataPicture = Convert.ToBase64String(EbookBuilders.Png(3, 3, MagickColors.Navy));
        var page = Page(folders, listener.Port, $"""<p><img src="data:image/png;base64,{dataPicture}"></p>""");
        var pdf = Path.Combine(fx.NewFolder(), "raw.pdf");

        var result = await ProcessRunner.RunAsync(WeasyPrintConverter.Locate()!, WeasyPrintConverter.Arguments(page, pdf),
            TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken, workingDirectory: folders.Inbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([(3, 3)], PdfPictures(pdf));
        var text = PdfText(pdf);
        Assert.DoesNotContain("LINKEDSTYLE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IMPORTEDSTYLE", text, StringComparison.Ordinal);
        Assert.False(listener.Contacted);
        Assert.Contains("disallowed protocol", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_inline_svg_draws_the_symbol_it_uses()
    {
        Assert.SkipUnless(HasWeasyPrint, NotInstalled);
        var folders = Make();
        using var listener = new Listener();

        var pdf = await ToPdfAsync(Page(folders, listener.Port, InlineSvg));

        Assert.Contains("USEDSYMBOL", PdfText(pdf), StringComparison.Ordinal);
        Assert.Equal([(4, 4)], PdfPictures(pdf));
        Assert.False(listener.Contacted);
    }

    [Fact]
    public async Task Markdown_becomes_a_pdf_with_its_table_and_only_the_picture_beside_it()
    {
        Assert.SkipUnless(HasWeasyPrint, NotInstalled);
        var folders = Make();
        using var listener = new Listener();
        var markdown = Path.Combine(folders.Inbox, "notes.md");
        File.WriteAllText(markdown, $"""
            # Release notes

            Some **bold** text.

            | Name | Value |
            |------|-------|
            | Alpha | 42 |

            ![kept](img/inside.png)

            ![absolute](<{folders.Secret}>)

            ![relative](../private/secret.png)

            ![remote](https://127.0.0.1:{listener.Port}/pixel.png)

            """);
        Assert.Equal(["markdown", "weasyprint"], Ids(fx.Catalog.CreatePlanner().Plan("md", "pdf")));

        var pdf = await ToPdfAsync(markdown);

        Assert.Equal([(4, 4)], PdfPictures(pdf));
        var text = PdfText(pdf);
        Assert.Contains("Release notes", text, StringComparison.Ordinal);
        Assert.Contains("Alpha", text, StringComparison.Ordinal);
        Assert.Contains("42", text, StringComparison.Ordinal);
        Assert.False(listener.Contacted);
    }

    [Fact]
    public async Task An_e_mail_becomes_a_pdf_with_its_inline_picture_and_nothing_it_links_to()
    {
        Assert.SkipUnless(HasWeasyPrint, NotInstalled);
        var folders = Make();
        using var listener = new Listener();
        var inline = Convert.ToBase64String(EbookBuilders.Png(16, 16, MagickColors.Teal));
        var html = $"""<html><body><p>Hello from the mail</p><img src="cid:logo@filee"><img src="https://127.0.0.1:{listener.Port}/track.png"><img src="{new Uri(folders.Secret).AbsoluteUri}"></body></html>""";
        var eml = Path.Combine(folders.Inbox, "mail.eml");
        File.WriteAllText(eml, string.Join("\r\n",
            "From: sender@example.com", "To: receiver@example.com", "Subject: Report", "Date: Thu, 01 Oct 2026 09:30:00 +0900",
            "MIME-Version: 1.0", "Content-Type: multipart/related; boundary=\"related\"", "",
            "--related", "Content-Type: text/html; charset=utf-8", "", html,
            "--related", "Content-Type: image/png", "Content-Transfer-Encoding: base64", "Content-ID: <logo@filee>", "", inline,
            "--related--", ""));
        Assert.Equal(["email", "weasyprint"], Ids(fx.Catalog.CreatePlanner().Plan("eml", "pdf")));

        var pdf = await ToPdfAsync(eml);

        Assert.Equal([(16, 16)], PdfPictures(pdf));
        Assert.Contains("Hello from the mail", PdfText(pdf), StringComparison.Ordinal);
        Assert.False(listener.Contacted);
    }
}
