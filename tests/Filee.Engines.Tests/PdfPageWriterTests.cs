// The Filee renderer's PDF output (Rendering/PdfPageWriter.cs): Skia's PDF backend with fonts subset by Filee. The
// acceptance page of the "PDF backend and font subsetting" spike is built here in code: one A4 page with Korean, Latin,
// Arabic and Devanagari paragraphs shaped by the layout engine's shaper (Typesetting/Shaper.cs, through TestShaper), a
// link, outline entries and some non-text drawing. It needs installed fonts (Malgun Gothic, Segoe UI, Nirmala UI on
// Windows) and is skipped where they are missing, like the other font-dependent tests. The page also goes through the
// render quality harness's read-back checks (RenderQuality/). Set FILEE_RENDER_OUT to a folder to keep the PDFs for a
// look in other viewers.

using System.Text;
using Filee.Engines.Rendering;
using Filee.Engines.Tests.RenderQuality;
using PdfSharp.Pdf.IO;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

#pragma warning disable CA1416

namespace Filee.Engines.Tests;

public sealed class PdfPageWriterTests : IDisposable
{
    private const float A4Width = 595.28f, A4Height = 841.89f, Margin = 50;

    private const string Korean =
        "Filee는 문서를 PDF로 바꿀 때 글꼴 파일 전체를 넣지 않고 실제로 쓴 글자만 골라 담습니다. 맑은 고딕 한 줄 때문에 " +
        "십삼 메가바이트짜리 파일이 생기는 일을 막고, 글자는 그대로 검색하고 복사할 수 있게 남깁니다. 한글 문서의 문단과 표, " +
        "머리말과 꼬리말이 모두 같은 방식으로 그려지며 화면에서 보는 모양과 인쇄한 모양이 같아야 합니다. " +
        "다람쥐 헌 쳇바퀴에 타고파. 키스의 고유조건은 입술끼리 만나야 하고 특별한 기술은 필요치 않다. " +
        "닭 잡아서 치킨파티 함. 콩고물과 우유가 들어간 빙수는 차갑고 달콤하다.";

    private const string Latin =
        "Filee writes searchable PDF files: every font is embedded as a subset that holds only the glyphs the page uses, " +
        "so a page of text stays small and its words can be found, selected and copied. The quick brown fox jumps over " +
        "the lazy dog. Sphinx of black quartz, judge my vow — 0123456789 (€, ©, “quotes”, office, " +
        "finally, waffle).";

    private const string Arabic =
        "يكتب فيلي ملفات قابلة للبحث، وكل خط فيها مضمّن كمجموعة جزئية تحتوي فقط على الحروف المستخدمة في الصفحة. " +
        "هذا نص عربي للتجربة مع التشكيل: بِسْمِ اللَّهِ الرَّحْمَٰنِ الرَّحِيمِ.";

    private const string Devanagari =
        "फ़ाइली खोजने योग्य पीडीएफ़ फ़ाइलें लिखता है: हर फ़ॉन्ट का केवल वह हिस्सा जोड़ा जाता है जिसके अक्षर पृष्ठ पर " +
        "इस्तेमाल हुए हैं। हिन्दी में संयुक्ताक्षर जैसे क्ष, त्र, ज्ञ और श्र भी सही दिखने चाहिए।";

    private readonly TestShaper _shaper = new();

    public void Dispose() => _shaper.Dispose();

    // ───────────────────────── The acceptance page ─────────────────────────

    public static TheoryData<string> KoreanFonts() => new()
    {
        "malgun.ttf", // TrueType, 13 MB
        "NotoSansCJKkr-Regular.otf", // CID-keyed CFF (Noto Sans CJK)
        "NotoSansCJK-Regular.ttc",
        "NotoSansKR-VF.ttf", // variable TrueType (Windows 11 ships it)
    };

    [Theory]
    [MemberData(nameof(KoreanFonts))]
    public void Mixed_script_a4_page_is_small_searchable_and_looks_right(string koreanFont)
    {
        var korean = InstalledFont(koreanFont);
        var latin = InstalledFont("segoeui.ttf");
        var devanagari = InstalledFont("Nirmala.ttc", "Nirmala.ttf");
        Assert.SkipUnless(korean is not null && latin is not null && devanagari is not null,
            $"needs {koreanFont}, Segoe UI and Nirmala UI installed");
        using var koreanFace = PageFont.FromFile(korean);
        using var latinFace = PageFont.FromFile(latin);
        using var devanagariFace = PageFont.FromFile(devanagari);
        var (page, text, outline) = MixedPage(koreanFace, latinFace, latinFace, devanagariFace);

        var pdf = Write([page], new PdfDocumentInfo { Title = "Mixed scripts", Outline = outline });
        Keep(pdf, $"mixed-{Path.GetFileNameWithoutExtension(koreanFont)}.pdf");

        // Under 200 KB with every font embedded and subset.
        Assert.True(pdf.Length < 200_000, $"{pdf.Length:N0} bytes");
        var fonts = PdfChecks.ListFonts(pdf);
        Assert.Equal(3, fonts.Count);
        foreach (var font in fonts)
        {
            Assert.Equal("/Type0", font.Subtype);
            Assert.Equal("/FontFile2", font.File);
            Assert.Matches("^/[A-Z]{6}\\+", font.BaseFont);
        }
        Assert.True(fonts.Sum(f => f.Length) < new FileInfo(korean!).Length / 10);

        // The exact source text, Arabic in logical order, clusters whole.
        Assert.Equal(text, PdfLines.Text(pdf));

        // The render quality harness's read-back checks: the text, no glyphs drawn over each other, everything inside
        // the page and its 50 pt margins, every font embedded and subset. Segoe UI kerns reh and zain 0.08 em under a
        // letter that doesn't join them, so overlaps up to 0.1 em are the font's own.
        Assert.All(new[]
        {
            PdfChecks.TextRoundTrip(pdf, text),
            PdfChecks.OverlappingGlyphs(pdf, tolerance: 0.1),
            PdfChecks.GlyphsOutsidePage(pdf),
            PdfChecks.GlyphsOutsideMargins(pdf, new PageMargins(Margin, Margin, Margin, Margin)),
            PdfChecks.PageCount(pdf, 1),
            PdfChecks.FontsEmbeddedAndSubset(pdf),
        }, check => Assert.True(check.Ok, check.ToString()));

        // PDFium draws what Skia draws (rendered at 3× and averaged down, so rasterizer anti-aliasing differences fade).
        const int Dpi = 100;
        using var pdfium = Conversion.ToImage(pdf, 0, null, new RenderOptions(Dpi: 3 * Dpi, WithAnnotations: false, BackgroundColor: SKColors.White));
        using var skia = page.Render(3 * Dpi);
        var (ssim, off) = Ssim.Compare(GreyImage.From(pdfium, 3), GreyImage.From(skia, 3));
        Assert.True(ssim > 0.9 && off < 0.01, $"SSIM {ssim:F4}, {off:P2} of the pixels differ");
    }

    [Fact]
    public void Page_size_links_and_outline_are_exact()
    {
        var latin = InstalledFont("segoeui.ttf", "arial.ttf", "DejaVuSans.ttf", "Arial.ttf");
        Assert.SkipUnless(latin is not null, "no Latin font installed");
        using var face = PageFont.FromFile(latin);
        var page = new PageContent(A4Width, A4Height);
        page.DrawGlyphs(_shaper.Shape(face, 20, "Chapter one", Margin, 100).Run);
        page.LinkToUrl(new SKRect(Margin, 80, 200, 105), "https://github.com/KnifeLemon/Filee");
        page.Destination(new SKPoint(0, 90), "chapter-1");
        var second = new PageContent(400, 300);
        second.DrawGlyphs(_shaper.Shape(face, 12, "Back to chapter one", 20, 40).Run);
        second.LinkToDestination(new SKRect(20, 28, 160, 44), "chapter-1");
        OutlineEntry[] outline = [new("Chapter one", 0, 80, 0), new("Section", 0, 300, 1), new("Second page", 1, 20, 0)];

        var pdf = Write([page, second], new PdfDocumentInfo { Title = "Links", Outline = outline });

        using (var document = PdfPigDocument.Open(pdf))
        {
            var first = document.GetPage(1);
            Assert.Equal(A4Width, first.Width, 0.01);
            Assert.Equal(A4Height, first.Height, 0.01);
            Assert.Equal(400, document.GetPage(2).Width, 0.01);
            // The text stays where it was drawn, measured from the top edge.
            var letter = first.Letters[0];
            Assert.Equal(100, first.Height - letter.StartBaseLine.Y, 0.01); // PdfPig measures from the media box's corner
            Assert.Equal("Chapter one", string.Concat(first.Letters.Select(l => l.Value)));
        }

        using var pdfSharp = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        var annotations = pdfSharp.Pages[0].Elements.GetArray("/Annots")!;
        Assert.Contains(Enumerable.Range(0, annotations.Elements.Count), i =>
            annotations.Elements.GetDictionary(i)?.Elements.GetDictionary("/A")?.Elements.GetString("/URI") == "https://github.com/KnifeLemon/Filee");
        Assert.NotNull(pdfSharp.Pages[1].Elements.GetArray("/Annots"));
        var titles = pdfSharp.Outlines.Select(o => o.Title).ToList();
        Assert.Equal(["Chapter one", "Second page"], titles);
        Assert.Equal("Section", Assert.Single(pdfSharp.Outlines[0].Outlines).Title);
    }

    [Fact]
    public void Cff_font_is_embedded_as_a_truetype_subset()
    {
        // FileeTest-CID.otf: a CID-keyed CFF subset of Noto Sans KR (see FontTests), so this runs everywhere.
        using var face = PageFont.FromFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "FileeTest-CID.otf"));
        var page = new PageContent(300, 100);
        var (run, _) = _shaper.Shape(face, 24, "Filee 한글 변환", 10, 50);
        page.DrawGlyphs(run);

        var pdf = Write([page]);

        var font = Assert.Single(PdfChecks.ListFonts(pdf));
        Assert.Equal("/Type0", font.Subtype);
        Assert.Equal("/FontFile2", font.File);
        Assert.Equal("Filee 한글 변환", PdfLines.Text(pdf));
    }

    [Fact]
    public void Right_to_left_runs_are_written_in_logical_order()
    {
        using var face = PageFont.FromFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "FileeTest-TrueType.ttf"));
        // A run as a right-to-left shaper reports it: visual order, clusters descending.
        var run = new GlyphRun(face, 10, [3, 2, 1], [new(30, 10), new(20, 10), new(10, 10)], "abc", [2, 1, 0]);

        var (glyphs, positions, clusters) = PageContent.LogicalOrder(run);

        Assert.Equal([1, 2, 3], glyphs);
        Assert.Equal([new SKPoint(10, 10), new SKPoint(20, 10), new SKPoint(30, 10)], positions);
        Assert.Equal([0, 1, 2], clusters);
        // Clusters out of order either way become one cluster for the whole run.
        Assert.Equal([0, 0, 0], PageContent.LogicalOrder(run with { Clusters = [1, 2, 0] }).Clusters);
    }

    [Fact]
    public void Clusters_become_utf8_offsets()
    {
        var (utf8, offsets) = PageContent.Utf8Clusters("aé한😀b", [0, 1, 2, 3, 5]);

        Assert.Equal(Encoding.UTF8.GetBytes("aé한😀b"), utf8);
        Assert.Equal([0u, 1u, 3u, 6u, 10u], offsets);
    }

    // ───────────────────────── Page building ─────────────────────────

    /// <summary>Headings, paragraphs broken at spaces by shaped width, a link and a filled box.</summary>
    private (PageContent Page, string Text, List<OutlineEntry> Outline) MixedPage(PageFont korean, PageFont latin, PageFont arabic, PageFont devanagari)
    {
        var page = new PageContent(A4Width, A4Height);
        var text = new StringBuilder();
        var outline = new List<OutlineEntry>();
        var y = Margin + 20f;
        var accent = new SKColor(0x1F, 0x3A, 0x68);

        void Heading(string title, PageFont font, bool rightToLeft = false)
        {
            var x = rightToLeft ? A4Width - Margin - _shaper.Width(font, 16, title) : Margin;
            page.DrawGlyphs(_shaper.Shape(font, 16, title, x, y, accent).Run);
            text.Append(title);
            outline.Add(new OutlineEntry(title, 0, y - 16, 0));
            var ruleY = y + 5;
            page.Draw(canvas =>
            {
                using var paint = new SKPaint { Color = accent, StrokeWidth = 0.75f, IsAntialias = true };
                canvas.DrawLine(Margin, ruleY, A4Width - Margin, ruleY, paint);
            });
            y += 28;
        }

        void Paragraph(string body, PageFont font, float size, bool rightToLeft = false)
        {
            foreach (var line in Lines(font, size, body, A4Width - 2 * Margin))
            {
                var x = rightToLeft ? A4Width - Margin - _shaper.Width(font, size, line) : Margin;
                page.DrawGlyphs(_shaper.Shape(font, size, line, x, y).Run);
                text.Append(line);
                y += size * 1.6f;
            }
            y += 14;
        }

        Heading("1. 한국어", korean);
        Paragraph(Korean, korean, 11);
        Heading("2. English", latin);
        Paragraph(Latin, latin, 11);
        page.LinkToUrl(new SKRect(Margin, y - 30, Margin + 120, y - 16), "https://github.com/KnifeLemon/Filee");
        Heading("العربية", arabic, rightToLeft: true);
        Paragraph(Arabic, arabic, 13, rightToLeft: true);
        Heading("4. हिन्दी", devanagari);
        Paragraph(Devanagari, devanagari, 12);
        var boxTop = y;
        page.Draw(canvas =>
        {
            using var fill = new SKPaint { Color = new SKColor(0xE8, 0xEE, 0xF7) };
            canvas.DrawRoundRect(new SKRect(Margin, boxTop, A4Width - Margin, boxTop + 40), 6, 6, fill);
        });
        return (page, text.ToString(), outline);
    }

    private List<string> Lines(PageFont font, float size, string body, float maxWidth)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in body.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && _shaper.Width(font, size, candidate) > maxWidth)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }
        if (current.Length > 0)
            lines.Add(current);
        return lines;
    }

    // ───────────────────────── Checks ─────────────────────────

    private static byte[] Write(IReadOnlyList<PageContent> pages, PdfDocumentInfo? info = null)
    {
        using var output = new MemoryStream();
        PdfPageWriter.Write(output, pages, info, cancellationToken: TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private static void Keep(byte[] pdf, string name)
    {
        if (Environment.GetEnvironmentVariable("FILEE_RENDER_OUT") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, name), pdf);
        }
    }

    /// <summary>A font file from the system's or the user's font folders, by file name; null when none is there.</summary>
    private static string? InstalledFont(params string[] names)
    {
        var folders = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
            "/usr/share/fonts", "/usr/local/share/fonts", "/Library/Fonts", "/System/Library/Fonts",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Fonts"),
        };
        foreach (var folder in folders.Where(f => f.Length > 0 && Directory.Exists(f)))
        {
            foreach (var name in names)
            {
                var match = Directory.EnumerateFiles(folder, name, new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true })
                    .FirstOrDefault();
                if (match is not null)
                    return match;
            }
        }
        return null;
    }
}
