// Pictures a document refers to are read only from the document's own folder and its subfolders (DocumentFiles):
// HTML, Markdown and Pandoc documents must not copy other pictures on the PC into the converted file, and a path or
// file: URL naming another computer is refused before anything looks at it (no SMB connection). Every document here
// sits in "inbox" with a picture in "inbox/img" (4×4, kept) and one in the sibling folder "private" (6×6, left out).

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;
using Filee.Engines.Text;
using ImageMagick;

namespace Filee.Engines.Tests;

public class DocumentPicturePathTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>A folder with "inbox/img/inside.png" and "private/secret.png" (and "private/note.txt").</summary>
    private sealed record Folders(string Inbox, string Inside, string Secret, string Note);

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
        var note = Path.Combine(root, "private", "note.txt");
        File.WriteAllText(note, "SECRETTEXT");
        return new Folders(inbox, inside, secret, note);
    }

    /// <summary>The ways a document can point outside its folder: up, absolute, and a file: URL.</summary>
    private static string[] Outside(Folders folders) => ["../private/secret.png", folders.Secret, new Uri(folders.Secret).AbsoluteUri];

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    /// <summary>The single picture stored under <paramref name="prefix"/>: the 4×4 one from the subfolder.</summary>
    private static void AssertOnlyTheInsidePicture(string package, string prefix)
    {
        using var zip = ZipFile.OpenRead(package);
        var pictures = zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Length > 0).ToList();
        using var stream = Assert.Single(pictures).Open();
        using var image = new MagickImage(stream);
        Assert.Equal((4u, 4u), (image.Width, image.Height));
    }

    private static string Markdown(Folders folders) =>
        "Hello\n\n![inside](img/inside.png)\n\n" + string.Join("\n\n", Outside(folders).Select(o => $"![outside](<{o}>)")) + "\n";

    private static string Html(Folders folders) =>
        "<html><body><p>Hello</p><p><img src=\"img/inside.png\"></p>"
        + string.Concat(Outside(folders).Select(o => $"<p><img src=\"{o}\"></p>")) + "</body></html>";

    // ───────────────────────── The rule ─────────────────────────

    [Theory]
    [InlineData(@"\\host\share\x.png")]
    [InlineData("//host/share/x.png")]
    [InlineData(@"\\?\UNC\host\share\x.png")]
    [InlineData(@"\\.\UNC\host\share\x.png")]
    [InlineData("%5C%5Chost%5Cshare%5Cx.png")]
    [InlineData("file://host/share/x.png")]
    [InlineData("file:////host/share/x.png")]
    [InlineData("file://localhost/share/x.png")]
    public void A_reference_to_another_computer_is_refused_by_its_text(string reference)
    {
        // Only the text decides: the path on the other computer is never looked at (no File.Exists, no SMB).
        if (reference.StartsWith("file:", StringComparison.Ordinal))
            Assert.NotEmpty(new Uri(reference).Host);
        else
            Assert.True(DocumentFiles.OnOtherComputer(Uri.UnescapeDataString(reference)));
        var inbox = Make().Inbox;
        Assert.Null(DocumentFiles.Find(reference, inbox));
        Assert.Null(MarkdownReader.ResolveImage(reference, inbox));
    }

    [Fact]
    public void Only_files_in_the_documents_folder_and_below_are_found()
    {
        var folders = Make();

        Assert.Equal(folders.Inside, DocumentFiles.Find("img/inside.png", folders.Inbox));
        Assert.Equal(folders.Inside, DocumentFiles.Find("img%2Finside.png", folders.Inbox));
        Assert.Equal(folders.Inside, DocumentFiles.Find(new Uri(folders.Inside).AbsoluteUri, folders.Inbox));
        Assert.Equal(folders.Inside, DocumentFiles.Find(folders.Inside, folders.Inbox));
        foreach (var outside in Outside(folders).Append("img/../../private/secret.png").Append("https://example.com/x.png"))
            Assert.Null(DocumentFiles.Find(outside, folders.Inbox));
        // A sibling folder that only shares the name's start is outside too.
        Assert.Null(DocumentFiles.Find("../inbox-evil/x.png", folders.Inbox));
        // Several roots: the document's folder and a scratch folder (Pandoc's extracted pictures).
        Assert.Equal(folders.Secret, DocumentFiles.Find(folders.Secret, folders.Inbox, folders.Inbox, Path.GetDirectoryName(folders.Secret)!));
    }

    // ───────────────────────── Markdown ─────────────────────────

    [Fact]
    public async Task Markdown_to_docx_embeds_only_pictures_from_its_folder()
    {
        var folders = Make();
        var md = Path.Combine(folders.Inbox, "report.md");
        await File.WriteAllTextAsync(md, Markdown(folders), TestContext.Current.CancellationToken);

        AssertOnlyTheInsidePicture(await ConvertAsync(md, "docx"), "word/media/");
    }

    [Fact]
    public async Task Markdown_to_hwpx_embeds_only_pictures_from_its_folder()
    {
        var folders = Make();
        var md = Path.Combine(folders.Inbox, "report.md");
        await File.WriteAllTextAsync(md, Markdown(folders), TestContext.Current.CancellationToken);

        AssertOnlyTheInsidePicture(await ConvertAsync(md, "hwpx"), "BinData/");
    }

    [Fact]
    public async Task Markdown_to_epub_embeds_only_pictures_from_its_folder()
    {
        var folders = Make();
        var md = Path.Combine(folders.Inbox, "book.md");
        await File.WriteAllTextAsync(md, Markdown(folders), TestContext.Current.CancellationToken);

        AssertOnlyTheInsidePicture(await ConvertAsync(md, "epub"), "OEBPS/images/");
    }

    [Fact]
    public void Markdown_to_html_elsewhere_links_only_pictures_from_its_folder()
    {
        var folders = Make();
        var md = Path.Combine(folders.Inbox, "report.md");

        // Written away from the Markdown file (a step towards LibreOffice): pictures from outside lose their link.
        var html = MarkdownConverter.ToHtml(Markdown(folders) + "\n![web](https://example.com/x.png)\n", md, Path.Combine(fx.NewFolder(), "step.html"));

        Assert.Contains($"src=\"{new Uri(folders.Inside).AbsoluteUri}\"", html);
        Assert.Contains("src=\"https://example.com/x.png\"", html);
        Assert.DoesNotContain("secret", html);
    }

    // ───────────────────────── HTML ─────────────────────────

    [Fact]
    public async Task Html_to_docx_embeds_only_pictures_from_its_folder()
    {
        var folders = Make();
        var html = Path.Combine(folders.Inbox, "page.html");
        await File.WriteAllTextAsync(html, Html(folders), TestContext.Current.CancellationToken);

        AssertOnlyTheInsidePicture(await ConvertAsync(html, "docx"), "word/media/");
    }

    [Fact]
    public async Task Html_to_hwpx_embeds_only_pictures_from_its_folder()
    {
        var folders = Make();
        var html = Path.Combine(folders.Inbox, "page.html");
        await File.WriteAllTextAsync(html, Html(folders), TestContext.Current.CancellationToken);

        AssertOnlyTheInsidePicture(await ConvertAsync(html, "hwpx"), "BinData/");
    }

    [Fact]
    public void Html_reads_style_sheets_only_from_its_folder()
    {
        var folders = Make();
        var css = Path.Combine(Path.GetDirectoryName(folders.Secret)!, "style.css");
        File.WriteAllText(css, ".x { font-weight: bold }");
        var html = $"<html><head><link rel=\"stylesheet\" href=\"../private/style.css\"></head><body><p class=\"x\">Text</p></body></html>";

        var content = HtmlReader.Parse(html, new HtmlReadOptions { BaseFolder = folders.Inbox, MediaFolder = Path.Combine(folders.Inbox, "media") });

        var run = content.Blocks.SelectMany(b => HDocumentWalker.Inlines([b])).OfType<HText>().Single();
        Assert.NotEqual(true, run.Format.Bold);
    }

    // ───────────────────────── Pandoc ─────────────────────────

    [Fact]
    public void Pandoc_pictures_are_checked_before_pandoc_writes_them()
    {
        var folders = Make();
        var media = Path.Combine(fx.NewFolder(), "media");
        Directory.CreateDirectory(media);
        var extracted = Path.Combine(media, "image1.png"); // what Pandoc extracted from a DOCX or ODT
        File.WriteAllBytes(extracted, EbookBuilders.Png(4, 4, MagickColors.Teal));
        string[] targets = ["img/inside.png", extracted, .. Outside(folders), @"\\host\share\x.png", "file://host/share/x.png",
            "data:image/png;base64," + Convert.ToBase64String(EbookBuilders.Png(4, 4, MagickColors.Teal))];
        var images = string.Join(",", targets.Select(t =>
            $$"""{"t":"Image","c":[["",[],[]],[{"t":"Str","c":"alt"}],[{{JsonSerializer.Serialize(t)}},""]]}"""));
        var ast = JsonNode.Parse($$"""{"meta":{},"blocks":[{"t":"Para","c":[{{images}}]}]}""")!;

        PandocConverter.CheckPictures(ast, target => PandocConverter.Picture(target, folders.Inbox, media));

        var inlines = ast["blocks"]![0]!["c"]!.AsArray();
        var kept = inlines.Where(i => (string?)i!["t"] == "Image").Select(i => (string)i!["c"]![2]![0]!).ToList();
        Assert.Equal(3, kept.Count);
        Assert.Equal(folders.Inside, kept[0]);
        Assert.Equal(extracted, kept[1]);
        Assert.StartsWith(media, kept[2]); // the data: URI, written as a file
        Assert.Equal(5, inlines.Count(i => (string?)i!["t"] == "Span")); // the others keep their description
    }

    private bool HasPandoc => PandocConverter.Locate() is not null && fx.Catalog.StatusOf(fx.Converters.Single(c => c.Id == "pandoc")).IsAvailable;

    [Fact]
    public async Task Latex_through_pandoc_embeds_only_pictures_from_its_folder_and_includes_nothing()
    {
        Assert.SkipUnless(HasPandoc, "Pandoc not found (pwsh build/fetch-engines.ps1 -Only pandoc)");
        var folders = Make();
        var tex = Path.Combine(folders.Inbox, "paper.tex");
        await File.WriteAllTextAsync(tex, $$"""
            \documentclass{article}
            \usepackage{graphicx}
            \begin{document}
            Hello
            \includegraphics{img/inside.png}
            \includegraphics{../private/secret.png}
            \includegraphics{{{folders.Secret.Replace('\\', '/')}}}
            \input{../private/note.txt}
            \end{document}
            """, TestContext.Current.CancellationToken);

        var docx = await ConvertAsync(tex, "docx"); // Pandoc's own DOCX writer
        AssertOnlyTheInsidePicture(docx, "word/media/");
        Assert.DoesNotContain("SECRETTEXT", DocxAssert.Body(docx).Value);
    }

    [Fact]
    public async Task Rst_through_pandoc_into_hwpx_embeds_only_pictures_from_its_folder_and_includes_nothing()
    {
        Assert.SkipUnless(HasPandoc, "Pandoc not found (pwsh build/fetch-engines.ps1 -Only pandoc)");
        var folders = Make();
        var rst = Path.Combine(folders.Inbox, "notes.rst");
        await File.WriteAllTextAsync(rst, $"""
            Notes
            =====

            Hello

            .. image:: img/inside.png

            .. image:: ../private/secret.png

            .. image:: {new Uri(folders.Secret).AbsoluteUri}

            .. include:: ../private/note.txt

            """, TestContext.Current.CancellationToken);

        var hwpx = await ConvertAsync(rst, "hwpx"); // Pandoc's AST, the built-in HWPX writer
        AssertOnlyTheInsidePicture(hwpx, "BinData/");
        Assert.DoesNotContain("SECRETTEXT", HwpxAssert.ReadBackText(hwpx));

        var md = await ConvertAsync(rst, "md"); // Pandoc all the way: pictures go into "notes_files"
        var pictures = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(md)!, Path.GetFileNameWithoutExtension(md) + "_files"), "*", SearchOption.AllDirectories);
        using var picture = new MagickImage(Assert.Single(pictures));
        Assert.Equal((4u, 4u), (picture.Width, picture.Height));
        Assert.DoesNotContain("SECRETTEXT", await File.ReadAllTextAsync(md, TestContext.Current.CancellationToken));
    }
}
