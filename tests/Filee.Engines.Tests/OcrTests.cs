// Text recognition with Tesseract (issue #288): scans and pictures become searchable PDFs, and their text goes on into
// DOCX and TXT. The real tests need Tesseract (pwsh build/fetch-engines.ps1 -Only 7zip,tesseract) and download the
// language data they read the first time (English, German).

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Ocr;
using ImageMagick;
using ImageMagick.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

public class OcrLanguageListTests
{
    [Fact]
    public void Every_language_is_pinned_and_named()
    {
        Assert.Equal("eng", OcrLanguages.All[0].Code);
        Assert.Equal(69, OcrLanguages.All.Count);
        Assert.All(OcrLanguages.All, language =>
        {
            Assert.NotEqual(language.Code, language.NativeName); // its culture is known
            var component = OcrLanguages.Components[language.Code];
            Assert.Matches("^[0-9A-F]{64}$", component.Sha256);
            Assert.True(component.Size > 0);
            var url = language.Code == "ara"
                ? $"https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/4.1.0/{language.Code}.traineddata"
                : $"https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/{language.Code}.traineddata";
            Assert.Equal(url, component.Url);
        });
        Assert.Equal("Deutsch", OcrLanguages.Find("deu")!.NativeName);
        Assert.Null(OcrLanguages.Find("klingon"));
    }

    private static IEnumerable<string> Codes(string? setting) => OcrLanguages.Parse(setting).Select(l => l.Code);

    [Fact]
    public void A_preset_names_one_language_or_several_joined_with_a_plus()
    {
        Assert.Equal(["deu"], Codes("deu")); // as settings have it from before several languages (#1123)
        Assert.Equal(["chi_sim", "eng"], Codes("chi_sim+eng"));
        Assert.Equal(["eng", "chi_sim"], Codes("eng+chi_sim")); // the main one stays first
        Assert.Equal("chi_sim+eng", OcrLanguages.Join(["chi_sim", "eng", "chi_sim"]));
        Assert.Equal("deu", OcrLanguages.Join(["deu"]));
    }

    [Theory]
    [InlineData("deu+klingon+deu", "deu")]
    [InlineData("klingon", "eng")]
    [InlineData("../../evil+eng", "eng")]
    [InlineData("+", "eng")]
    [InlineData("", "eng")]
    [InlineData(null, "eng")]
    public void Only_listed_languages_count_each_once_and_with_none_english_is_read(string? setting, string read) =>
        Assert.Equal([read], Codes(setting));

    [Fact]
    public async Task Each_language_is_fetched_and_a_failed_download_says_which_one()
    {
        var languages = OcrLanguages.Parse("chi_sim+deu+eng");
        var asked = new List<string>();

        var data = await TesseractConverter.LanguageDataAsync(languages, (code, _) =>
        {
            asked.Add(code);
            return Task.FromResult(code + ".traineddata");
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["chi_sim", "deu", "eng"], asked);
        Assert.Equal(["chi_sim.traineddata", "deu.traineddata", "eng.traineddata"], data);

        var refused = await Assert.ThrowsAsync<ConversionRefusedException>(() => TesseractConverter.LanguageDataAsync(languages, (code, _) =>
            code == "deu" ? throw new HttpRequestException("no network") : Task.FromResult(code + ".traineddata"), TestContext.Current.CancellationToken));

        Assert.Equal("error.ocr_language_download", refused.ErrorKey);
        Assert.Equal("Deutsch: no network", refused.Detail);
    }

    [Fact]
    public void Tesseract_is_offered_but_not_suggested()
    {
        var package = EngineDownloads.Packages.Single(p => p.Id == "tesseract");

        Assert.False(EngineDownloads.IsSuggested(package));
        Assert.Equal(OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract", Assert.Single(EngineEnvironment.OwnCopyPrograms["tesseract"]));
    }
}

/// <summary>
/// Tesseract 4.x reads pdf.ttf from the tessdata folder of the job, so it is copied there from the system (no Tesseract
/// needed). The folders and the prefix are passed in: TESSDATA_PREFIX of the process is not touched.
/// </summary>
public class OcrPdfFontTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private string FolderWith(string? font)
    {
        var folder = fx.NewFolder();
        if (font is not null)
            File.WriteAllText(Path.Combine(folder, "pdf.ttf"), font);
        return folder;
    }

    [Fact]
    public void The_font_is_found_in_the_second_folder_and_the_first_one_wins_when_both_have_it()
    {
        var empty = FolderWith(null);
        var second = FolderWith("second");
        var third = FolderWith("third");

        Assert.Equal(Path.Combine(second, "pdf.ttf"), TesseractConverter.FindPdfFont([empty, second, third]));
        Assert.Equal(Path.Combine(third, "pdf.ttf"), TesseractConverter.FindPdfFont([third, second]));
        Assert.Equal(Path.Combine(second, "pdf.ttf"), TesseractConverter.FindPdfFont([Path.Combine(empty, "missing"), "", second]));
    }

    [Fact]
    public void No_font_is_no_error_and_nothing_is_copied()
    {
        var folders = new[] { FolderWith(null), Path.Combine(fx.NewFolder(), "missing") };
        var tessdata = fx.NewFolder();

        Assert.Null(TesseractConverter.FindPdfFont(folders));
        Assert.Null(TesseractConverter.FindPdfFont([]));
        TesseractConverter.CopyPdfFont(tessdata, folders);

        Assert.Empty(Directory.GetFileSystemEntries(tessdata));
    }

    [Fact]
    public void A_found_font_is_copied_into_the_job_tessdata()
    {
        var tessdata = fx.NewFolder();
        File.WriteAllText(Path.Combine(tessdata, "pdf.ttf"), "old");

        TesseractConverter.CopyPdfFont(tessdata, [FolderWith(null), FolderWith("system font")]);

        Assert.Equal("system font", File.ReadAllText(Path.Combine(tessdata, "pdf.ttf")));
    }

    [Fact]
    public void The_prefix_comes_first_then_the_executable_then_the_system_folders_each_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "filee-font-" + Guid.NewGuid().ToString("N")[..6]);
        var prefix = Path.Combine(root, "prefix");
        var executable = Path.Combine(root, "bin", "tesseract");
        var debian = Path.GetFullPath("/usr/share/tesseract-ocr/4.00/tessdata");

        var folders = TesseractConverter.PdfFontFolders(executable, prefix).ToList();

        Assert.Equal([prefix, Path.Combine(prefix, "tessdata"), Path.Combine(root, "bin")], folders.Take(3));
        var system = folders.IndexOf(debian);
        Assert.True(system > folders.IndexOf(Path.Combine(root, "bin")), "the system folders come after the executable's");
        Assert.Equal(folders.Count, folders.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count());

        // The prefix is optional, and a prefix that is also the executable's folder is listed once.
        Assert.Equal(Path.Combine(root, "bin"), TesseractConverter.PdfFontFolders(executable, null).First());
        Assert.Equal(Path.Combine(root, "bin"), TesseractConverter.PdfFontFolders(executable, "").First());
        Assert.Equal(1, TesseractConverter.PdfFontFolders(executable, Path.Combine(root, "bin")).Count(f => f == Path.Combine(root, "bin")));
    }
}

/// <summary>Which pages count as having text, so OCR leaves them alone (no Tesseract needed).</summary>
public class OcrPagesWithTextTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>
    /// A one-page A4 PDF written by hand: optionally a picture over the whole page (a scan), then lines of Helvetica
    /// text, visible or invisible (render mode 3, as an OCR layer is written).
    /// </summary>
    private string Pdf(bool scan, int lines, string text = "ABC-000123", bool invisible = false)
    {
        var content = new StringBuilder();
        if (scan)
            content.Append("q 595 0 0 842 0 0 cm /Im1 Do Q\n");
        for (var i = 0; i < lines; i++)
            content.Append($"BT /F1 10 Tf {(invisible ? "3" : "0")} Tr 40 {800 - i * 12} Td ({text}) Tj ET\n");
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> /XObject << /Im1 6 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\n\u00c8\nendstream",
        ];
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(fx.NewFolder(), "page.pdf");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(pdf.ToString()));
        return path;
    }

    [Fact]
    public void A_scan_with_only_a_stamp_is_recognized()
    {
        Assert.Equal([false], TesseractConverter.PagesWithText(Pdf(scan: true, lines: 1)));
        Assert.Equal([false], TesseractConverter.PagesWithText(Pdf(scan: true, lines: 0)));
    }

    [Fact]
    public void An_ocr_layer_a_page_of_text_and_a_drawn_note_are_left_alone()
    {
        Assert.Equal([true], TesseractConverter.PagesWithText(Pdf(scan: true, lines: 1, invisible: true)));
        Assert.Equal([true], TesseractConverter.PagesWithText(Pdf(scan: true, lines: 40, text: "The quick brown fox jumps over the lazy dog again and again")));
        Assert.Equal([true], TesseractConverter.PagesWithText(Pdf(scan: false, lines: 1)));
    }
}

/// <summary>Which pictures are read from an enlarged copy, and by how much (#138; no Tesseract needed).</summary>
public class OcrEnlargeTests
{
    [Fact]
    public void A_screenshot_is_read_at_twice_its_size()
    {
        Assert.Equal(2, TesseractConverter.EnlargeFactor(658, 789, null)); // the screenshot of #138
        Assert.Equal(2, TesseractConverter.EnlargeFactor(999, 400, 96));
        Assert.Equal(2, TesseractConverter.EnlargeFactor(400, 999, 72)); // a phone's picture of a receipt
        Assert.Equal(2, TesseractConverter.EnlargeFactor(1, 1, null));
    }

    [Fact]
    public void Scans_and_large_pictures_are_read_as_they_are()
    {
        Assert.Equal(1, TesseractConverter.EnlargeFactor(1000, 3000, null)); // the short side decides
        Assert.Equal(1, TesseractConverter.EnlargeFactor(2480, 3508, 300)); // A4 at 300 dpi
        Assert.Equal(1, TesseractConverter.EnlargeFactor(900, 600, 300)); // a business card scanned at 300 dpi
        Assert.Equal(1, TesseractConverter.EnlargeFactor(900, 600, 600));
        Assert.Equal(1, TesseractConverter.EnlargeFactor(0, 600, null));
    }

    [Fact]
    public void A_long_screenshot_is_enlarged_only_as_far_as_the_pixel_cap()
    {
        Assert.Equal(1.25, TesseractConverter.EnlargeFactor(800, 20000, null), 6); // 16 MP → 25 MP
        Assert.Equal(1, TesseractConverter.EnlargeFactor(900, 20000, null)); // under 1.25: not worth it
        var factor = TesseractConverter.EnlargeFactor(990, 15000, null);
        Assert.InRange(factor, TesseractConverter.LeastEnlargement, TesseractConverter.EnlargeBy);
        Assert.True(990 * factor * 15000 * factor <= TesseractConverter.MaxEnlargedPixels + 1);
    }
}

public class OcrTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>On a Mac Apple Vision takes the OCR step (VisionConverter, AppleVisionTests), so these are Tesseract's elsewhere.</summary>
    private static readonly bool Installed = TesseractConverter.Locate() is not null && !new VisionConverter().GetStatus().IsAvailable;

    private const string Skip = "Tesseract is not installed (pwsh build/fetch-engines.ps1 -Only 7zip,tesseract), or Apple Vision reads instead";

    private static readonly string? Arial = EngineEnvironment.FirstExisting(@"C:\Windows\Fonts\arial.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf");

    private static readonly string? ChineseFont = EngineEnvironment.FirstExisting(@"C:\Windows\Fonts\msyh.ttc",
        "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc", "/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc");

    /// <summary>A page as a scanner writes it: black text on white at 300 dpi, no text layer.</summary>
    private static string Scan(string folder, string name, params string[] lines) => ScanIn(Arial, folder, name, lines);

    private static string ScanIn(string? font, string folder, string name, params string[] lines)
    {
        using var image = new MagickImage(MagickColors.White, 2480, 800) { Density = new Density(300) };
        var drawables = new Drawables().FontPointSize(28).FillColor(MagickColors.Black);
        if (font is not null)
            drawables.Font(font);
        for (var i = 0; i < lines.Length; i++)
            drawables.Text(150, 220 + i * 180, lines[i]);
        drawables.Draw(image);
        var path = Path.Combine(folder, name);
        image.Write(path);
        return path;
    }

    private static string PageText(string pdf, int page)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return string.Join(" ", document.GetPage(page).GetWords().Select(w => w.Text));
    }

    private static Preset Ocr(string target, string language = "eng") => new() { TargetFormat = target, Pdf = { Ocr = true, OcrLanguage = language } };

    private static void AssertDone(ConversionJob job) =>
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));

    [Fact]
    public async Task A_picture_becomes_a_searchable_pdf()
    {
        Assert.SkipUnless(Installed, Skip);
        var scan = Scan(fx.NewFolder(), "letter.png", "Filee reads scanned pages", "into searchable documents");

        var job = await fx.ConvertAsync([scan], Ocr("pdf"));

        AssertDone(job);
        var text = PageText(job.Outputs.Single(), 1);
        Assert.Contains("scanned", text);
        Assert.Contains("searchable", text);
    }

    [Fact]
    public async Task Pictures_and_scans_with_korean_and_cyrillic_names_are_recognized()
    {
        Assert.SkipUnless(Installed, Skip);
        var folder = Path.Combine(fx.NewFolder(), "김민수 Документы");
        Directory.CreateDirectory(folder);
        var picture = Scan(folder, "스캔.png", "Filee reads scanned pages");
        var scanPdf = (await fx.ConvertAsync([Scan(folder, "Скан.png", "The quarterly report is ready")], new Preset { TargetFormat = "pdf" })).Outputs.Single();

        var job = await fx.ConvertAsync([picture, scanPdf], Ocr("pdf"));

        AssertDone(job);
        Assert.Contains("scanned", PageText(job.Outputs.Single(o => Path.GetFileName(o).StartsWith("스캔", StringComparison.Ordinal)), 1));
        Assert.Contains("quarterly", PageText(job.Outputs.Single(o => Path.GetFileName(o).StartsWith("Скан", StringComparison.Ordinal)), 1));
    }

    [Fact]
    public async Task Removing_metadata_leaves_the_document_information_behind()
    {
        Assert.SkipUnless(Installed, Skip);
        var folder = fx.NewFolder();
        var scanPdf = (await fx.ConvertAsync([Scan(folder, "scan.png", "Private letter")], new Preset { TargetFormat = "pdf" })).Outputs.Single();
        using (var document = PdfReader.Open(scanPdf, PdfDocumentOpenMode.Modify))
        {
            document.Info.Title = "Secret title";
            document.Info.Author = "Someone";
            document.Save(scanPdf);
        }
        var keep = Ocr("pdf");
        var remove = Ocr("pdf");
        remove.Pdf.RemoveMetadata = true;

        var kept = (await fx.ConvertAsync([scanPdf], keep)).Outputs.Single();
        var removed = (await fx.ConvertAsync([scanPdf], remove)).Outputs.Single();

        using (var document = PdfReader.Open(kept, PdfDocumentOpenMode.Import))
            Assert.Equal("Secret title", document.Info.Title);
        using (var document = PdfReader.Open(removed, PdfDocumentOpenMode.Import))
        {
            Assert.Equal("", document.Info.Title);
            Assert.Equal("", document.Info.Author);
        }
        Assert.Contains("Private", PageText(removed, 1));
    }

    [Fact]
    public async Task A_scanned_pdf_becomes_a_docx_with_its_text_and_without_the_scan()
    {
        Assert.SkipUnless(Installed, Skip);
        var folder = fx.NewFolder();
        var scanPdf = (await fx.ConvertAsync([Scan(folder, "scan.png", "The quarterly report is ready")], new Preset { TargetFormat = "pdf" })).Outputs.Single();
        Assert.Equal("", PageText(scanPdf, 1)); // only a picture

        var job = await fx.ConvertAsync([scanPdf], Ocr("docx"));

        AssertDone(job);
        using var docx = ZipFile.OpenRead(job.Outputs.Single());
        using var reader = new StreamReader(docx.GetEntry("word/document.xml")!.Open());
        var xml = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("quarterly", xml);
        Assert.DoesNotContain("<w:drawing", xml);
    }

    [Fact]
    public async Task Pages_with_text_stay_as_they_are_and_only_scans_are_recognized()
    {
        Assert.SkipUnless(Installed, Skip);
        var folder = fx.NewFolder();
        var withText = (await fx.ConvertAsync([Scan(folder, "first.png", "This page was searchable before")], Ocr("pdf"))).Outputs.Single();
        var scan = (await fx.ConvertAsync([Scan(folder, "second.png", "This page is only a picture")], new Preset { TargetFormat = "pdf" })).Outputs.Single();
        var mixed = Path.Combine(folder, "mixed.pdf");
        using (var document = new PdfDocument())
        {
            foreach (var part in new[] { withText, scan })
            {
                using var source = PdfReader.Open(part, PdfDocumentOpenMode.Import);
                document.AddPage(source.Pages[0]);
            }
            document.Save(mixed);
        }

        var job = await fx.ConvertAsync([mixed], Ocr("pdf"));

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.Equal(PageText(withText, 1), PageText(output, 1)); // copied, not recognized again
        Assert.Contains("picture", PageText(output, 2));
    }

    [Fact]
    public async Task A_long_scan_is_recognized_page_by_page_in_order()
    {
        Assert.SkipUnless(Installed, Skip);
        Assert.InRange(TesseractConverter.Runs, 1, 4);
        var folder = fx.NewFolder();
        var words = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf" };
        var scans = words.Select((w, i) => Scan(folder, $"p{i}.png", $"Page {w}")).ToList();
        var pdf = (await fx.ConvertAsync(scans, new Preset { TargetFormat = "pdf", Pdf = { MergeIntoSingle = true } })).Outputs.Single();

        var job = await fx.ConvertAsync([pdf], Ocr("pdf"));

        AssertDone(job);
        var output = job.Outputs.Single();
        for (var i = 0; i < words.Length; i++)
            Assert.Contains(words[i], PageText(output, i + 1));
    }

    [Fact]
    public async Task A_pdf_that_has_text_everywhere_is_copied_with_a_note()
    {
        Assert.SkipUnless(Installed, Skip);
        var searchable = (await fx.ConvertAsync([Scan(fx.NewFolder(), "done.png", "Already recognized")], Ocr("pdf"))).Outputs.Single();

        var job = await fx.ConvertAsync([searchable], Ocr("pdf"));

        AssertDone(job);
        Assert.Equal("notice.ocr_has_text", job.Files[0].NoticeKey);
        Assert.Equal(await File.ReadAllBytesAsync(searchable, TestContext.Current.CancellationToken),
            await File.ReadAllBytesAsync(job.Outputs.Single(), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A screenshot of a small table (#138): letters about 8 pixels tall, as on a screen, and no resolution in the
    /// file. The rows are drawn from <see cref="RowLeft"/>, <see cref="RowTop"/> and <see cref="RowHeight"/> pixels.
    /// </summary>
    private static string Screenshot(string folder, string name, params string[] rows)
    {
        using var image = new MagickImage(MagickColors.White, 560, (uint)(RowTop + RowHeight * rows.Length));
        var drawables = new Drawables().FontPointSize(11).FillColor(MagickColors.Black);
        if (Arial is not null)
            drawables.Font(Arial);
        for (var i = 0; i < rows.Length; i++)
            drawables.Text(RowLeft, RowTop + RowHeight * i, rows[i]);
        drawables.Draw(image);
        var path = Path.Combine(folder, name);
        image.Write(path);
        return path;
    }

    private const int RowLeft = 12, RowTop = 22, RowHeight = 17;

    private static readonly string[] Rows =
    [
        "Region      Quarter     Revenue     Margin",
        "Highland    Spring      482,137     12.4%",
        "Coastal     Autumn      903,551     17.9%",
        "Valley      Winter      266,808     9.6%",
    ];

    [Fact]
    public async Task A_small_screenshot_is_read_from_an_enlarged_copy_and_its_words_lie_over_the_picture()
    {
        Assert.SkipUnless(Installed, Skip);
        var screenshot = Screenshot(fx.NewFolder(), "table.png", Rows);

        var job = await fx.ConvertAsync([screenshot], Ocr("pdf"));

        AssertDone(job);
        using var document = UglyToad.PdfPig.PdfDocument.Open(job.Outputs.Single());
        var page = document.GetPage(1);
        // The page is the picture at 300 dpi, as without enlarging, and shows the picture itself, not the copy.
        Assert.Equal(560 * 72.0 / TesseractConverter.Dpi, page.Width, 1);
        var picture = Assert.Single(page.GetImages());
        Assert.Equal(560, picture.WidthInSamples);
        var words = page.GetWords().ToList();
        var text = string.Join(" ", words.Select(w => w.Text));
        foreach (var word in new[] { "Highland", "Coastal", "Valley", "Revenue", "482,137", "903,551", "266,808", "Autumn" })
            Assert.Contains(word, text);
        // "Coastal" starts the third row: where it is drawn on the picture, in points from the top left.
        var coastal = words.First(w => w.Text == "Coastal").BoundingBox;
        var points = 72.0 / TesseractConverter.Dpi;
        Assert.InRange(coastal.Left, (RowLeft - 3) * points, (RowLeft + 3) * points);
        Assert.InRange(page.Height - coastal.Bottom, (RowTop + 2 * RowHeight - 4) * points, (RowTop + 2 * RowHeight + 4) * points);
    }

    [Fact]
    public async Task The_text_of_an_enlarged_screenshot_goes_on_into_a_docx()
    {
        Assert.SkipUnless(Installed, Skip);
        var screenshot = Screenshot(fx.NewFolder(), "table.jpg", Rows);

        var job = await fx.ConvertAsync([screenshot], Ocr("docx"));

        AssertDone(job);
        using var docx = ZipFile.OpenRead(job.Outputs.Single());
        using var reader = new StreamReader(docx.GetEntry("word/document.xml")!.Open());
        var xml = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Coastal", xml);
        Assert.Contains("903,551", xml);
    }

    [Fact]
    public async Task Another_language_is_downloaded_when_a_preset_needs_it()
    {
        Assert.SkipUnless(Installed, Skip);
        var scan = Scan(fx.NewFolder(), "brief.jpg", "Die Größe der Straße ändert sich");

        var job = await fx.ConvertAsync([scan], Ocr("txt", "deu"));

        AssertDone(job);
        Assert.True(OcrLanguages.IsInstalled("deu"));
        var text = await File.ReadAllTextAsync(job.Outputs.Single(), TestContext.Current.CancellationToken);
        Assert.Contains("Größe", text);
        Assert.Contains("Straße", text);
    }

    [Fact]
    public async Task Text_in_two_scripts_is_read_only_with_both_languages()
    {
        Assert.SkipUnless(Installed, Skip);
        var scan = Scan(fx.NewFolder(), "report.png", "Отчёт за квартал готов", "The quarterly report is ready");

        var english = await fx.ConvertAsync([scan], Ocr("txt"));
        var both = await fx.ConvertAsync([scan], Ocr("txt", "rus+eng"));

        AssertDone(english);
        AssertDone(both);
        Assert.DoesNotContain("квартал", await File.ReadAllTextAsync(english.Outputs.Single(), TestContext.Current.CancellationToken));
        Assert.True(OcrLanguages.IsInstalled("rus"));
        var text = await File.ReadAllTextAsync(both.Outputs.Single(), TestContext.Current.CancellationToken);
        Assert.Contains("квартал", text);
        Assert.Contains("quarterly", text);
    }

    [Fact]
    public async Task Simplified_chinese_and_english_on_one_page_are_both_read()
    {
        // The scans of #1123. Linux runs without a Chinese font skip this; rus+eng above runs everywhere.
        Assert.SkipUnless(Installed && ChineseFont is not null, Skip);
        var scan = ScanIn(ChineseFont, fx.NewFolder(), "report.png", "季度报告已经准备好", "The quarterly report is ready");

        var job = await fx.ConvertAsync([scan], Ocr("txt", "chi_sim+eng"));

        AssertDone(job);
        var text = await File.ReadAllTextAsync(job.Outputs.Single(), TestContext.Current.CancellationToken);
        Assert.Contains("报告", text);
        Assert.Contains("quarterly", text);
    }
}
