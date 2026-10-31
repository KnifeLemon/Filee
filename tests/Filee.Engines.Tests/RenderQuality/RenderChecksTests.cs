// The render quality harness's building blocks on PDFs written in code (no renderer, no installed fonts): each
// read-back check, the line reader, the sequence matching, SSIM, the line compare and the thresholds.

using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Tests.RenderQuality;

public sealed class RenderChecksTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "filee-render-checks", Guid.NewGuid().ToString("N")[..8]);
    private readonly TestShaper _shaper = new();

    public RenderChecksTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        _shaper.Dispose();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private byte[] Pdf(GlyphPdfBuilder builder) => File.ReadAllBytes(builder.Save(Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".pdf")));

    private byte[] Pdf(PdfBuilder builder) => File.ReadAllBytes(builder.Save(Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".pdf")));

    // ───────────────────────── Sequences ─────────────────────────

    [Theory]
    [InlineData("ABCABBA", "CBABAC", 4)]
    [InlineData("abcdef", "abcdef", 6)]
    [InlineData("", "abc", 0)]
    [InlineData("abc", "", 0)]
    [InlineData("xaxbxc", "abc", 3)]
    [InlineData("한글 문서", "한글문서", 4)]
    public void Longest_common_subsequence(string a, string b, int length)
    {
        var (pairs, exact) = SequenceMatch.Match(a.ToCharArray(), b.ToCharArray());

        Assert.True(exact);
        Assert.Equal(length, pairs.Count);
        Assert.All(pairs, p => Assert.Equal(a[p.A], b[p.B]));
        Assert.Equal(pairs.OrderBy(p => p.A).ToList(), pairs);
        Assert.Equal(pairs.OrderBy(p => p.B).ToList(), pairs);
    }

    [Fact]
    public void Matching_past_the_limit_pairs_equal_items_in_order_of_occurrence()
    {
        var (pairs, exact) = SequenceMatch.Match("abcdefgh".ToCharArray(), "hgfedcba".ToCharArray(), maxDifferences: 3);

        Assert.False(exact);
        Assert.Equal(8, pairs.Count); // every letter has its twin, though not in order
    }

    // ───────────────────────── Text and lines ─────────────────────────

    [Fact]
    public void Text_round_trip_counts_the_expected_text_in_order()
    {
        var pdf = Pdf(new GlyphPdfBuilder()
            .Run("1.", 50, 100, 6) // a list number the document adds
            .Run("Hello", 70, 100, 6)
            .ActualText("ffi", b => b.Glyph("ﬃ", 106, 100, 9))
            .Run("world", 50, 120, 6));

        Assert.Equal(1, PdfChecks.TextRoundTrip(pdf, "Hello ffi world").Value);
        var missing = PdfChecks.TextRoundTrip(pdf, "Hello there world");
        Assert.False(missing.Ok);
        Assert.Equal(10.0 / 15, missing.Value, 4); // "Hello" and "world" of "Hellothereworld"
        Assert.Contains("missing", missing.Detail);
    }

    [Fact]
    public void Lines_group_letters_on_one_baseline_in_content_order()
    {
        var pdf = Pdf(new GlyphPdfBuilder()
            .Run("one", 50, 100, 6)
            .Glyph("2", 70, 96, 4, size: 7) // a superscript stays on its line
            .Run("two", 50, 116, 6)
            .Run("ab", 300, 116, 6)); // a second column on the same baseline

        var lines = PdfLines.Read(pdf);

        Assert.Equal(["one2", "twoab"], lines.Select(l => l.Key));
        Assert.Equal(100, lines[0].Baseline, 2);
        Assert.Equal(116, lines[1].Baseline, 2);
        Assert.Equal(12, lines[1].Size, 2);
    }

    // ───────────────────────── Overlaps ─────────────────────────

    [Fact]
    public void Glyphs_drawn_over_each_other_are_found()
    {
        var pdf = Pdf(new GlyphPdfBuilder()
            .Run("abc", 50, 100, 6)                                      // side by side
            .Glyph("x", 50, 130, 6).Glyph("y", 55, 130, 6)               // 1 pt = 0.083 em over: found
            .Glyph("p", 50, 160, 6).Glyph("q", 55.5, 160, 6)             // 0.5 pt = 0.042 em: within the tolerance
            .Glyph("क", 50, 190, 6).Glyph("ि", 52, 190, 0)     // a mark without an advance on its letter
            .Glyph("m", 50, 220, 6).Glyph("n", 53, 240, 6));             // different lines

        var result = PdfChecks.OverlappingGlyphs(pdf);

        Assert.Equal(0.25, result.Value); // one of the four neighbouring pairs (ab, bc, xy, pq)
        Assert.False(result.Ok);
        Assert.Contains("1 of 4 pairs", result.Detail);
        Assert.Contains("\"xy\"", result.Detail);
        Assert.Equal(0, PdfChecks.OverlappingGlyphs(pdf, tolerance: 0.1).Value);
    }

    [Fact]
    public void Right_to_left_glyphs_drawn_in_logical_order_do_not_overlap()
    {
        // Glyphs written from right to left, as Filee's page writer and rhwp leave Arabic and Hebrew.
        var pdf = Pdf(new GlyphPdfBuilder().Glyph("א", 80, 100, 6).Glyph("ב", 74, 100, 6).Glyph("ג", 68, 100, 6));

        Assert.True(PdfChecks.OverlappingGlyphs(pdf).Ok);
    }

    // ───────────────────────── Page and margins ─────────────────────────

    [Fact]
    public void Glyphs_outside_the_page_are_found()
    {
        var inside = new PdfBuilder().NewPage();
        inside.Line("inside", 12, inside.Font(), 100, 100);
        var outside = new PdfBuilder().NewPage();
        outside.Line("W", 12, outside.Font(), -20, 100).Line("bottom", 12, outside.Font(), 100, 845);

        Assert.True(PdfChecks.GlyphsOutsidePage(Pdf(inside)).Ok);
        var result = PdfChecks.GlyphsOutsidePage(Pdf(outside));
        Assert.Equal(1 + "bottom".Length, result.Value); // "W" left of the page, "bottom" below it
        Assert.Contains("\"W\"", result.Detail);
    }

    [Fact]
    public void Glyphs_outside_the_margins_are_found_but_not_those_in_the_header_area()
    {
        var margins = new PageMargins(Left: 85, Top: 57, Right: 85, Bottom: 42.5);
        var pdf = Pdf(new GlyphPdfBuilder()
            .Run("header", 85, 70, 6)    // in the header area: inside the top margin as 한글 measures it
            .Run("body", 100, 200, 6)
            .Run("edge", 488, 300, 6)    // ends at 512 = the right margin (595 - 85 = 510) + 2 pt
            .Run("low", 100, 805, 6));   // below 842 - 42.5

        var result = PdfChecks.GlyphsOutsideMargins(pdf, margins);

        Assert.Equal(1 + 3, result.Value); // the last letter of "edge" and all of "low"
        Assert.Equal(0, PdfChecks.GlyphsOutsideMargins(pdf, new PageMargins(10, 10, 10, 10)).Value);
    }

    [Fact]
    public void Page_count_is_measured_against_the_document()
    {
        var builder = new PdfBuilder().NewPage();
        var font = builder.Font();
        var pdf = Pdf(builder.Line("one", 12, font, 100, 100).NewPage().Line("two", 12, font, 100, 100));

        Assert.True(PdfChecks.PageCount(pdf, 2).Ok);
        var wrong = PdfChecks.PageCount(pdf, 1);
        Assert.Equal(2, wrong.Value);
        Assert.False(wrong.Ok);
    }

    // ───────────────────────── Baselines ─────────────────────────

    [Fact]
    public void Baselines_are_met_within_half_a_point()
    {
        Assert.True(PdfChecks.BaselinesMatch([100, 116.5, 132], [100, 116, 132]).Ok);
        var off = PdfChecks.BaselinesMatch([100, 116.6, 132], [100, 116, 132]);
        Assert.Equal(2.0 / 3, off.Value, 4);
        Assert.Contains("line 2", off.Detail);
        Assert.Equal(0.5, PdfChecks.BaselinesMatch([100, 116], [100, 117, 132, 148], 1.5).Value); // missing lines count
    }

    [Fact]
    public void Baseline_pitch_follows_the_documents_line_height()
    {
        var even = Pdf(new GlyphPdfBuilder().Run("one", 50, 100, 6).Run("two", 50, 116, 6).Run("three", 50, 132, 6));
        var uneven = Pdf(new GlyphPdfBuilder().Run("one", 50, 100, 6).Run("two", 50, 116, 6).Run("three", 50, 133, 6));

        Assert.True(PdfChecks.BaselinePitch(even, 16).Ok);
        Assert.Equal(2.0 / 3, PdfChecks.BaselinePitch(uneven, 16).Value, 4);
    }

    // ───────────────────────── Fonts ─────────────────────────

    [Fact]
    public void Fonts_must_be_embedded_and_subset()
    {
        using var face = PageFont.FromFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "FileeTest-TrueType.ttf"));
        var page = new PageContent(300, 100);
        page.DrawGlyphs(_shaper.Shape(face, 20, "Filee", 10, 50).Run);
        using var output = new MemoryStream();
        PdfPageWriter.Write(output, [page], cancellationToken: TestContext.Current.CancellationToken);
        var builder = new PdfBuilder().NewPage();
        var standard = Pdf(builder.Line("Helvetica", 12, builder.Font(), 100, 100));

        Assert.True(PdfChecks.FontsEmbeddedAndSubset(output.ToArray()).Ok);               // Filee's subset TrueType
        Assert.True(PdfChecks.FontsEmbeddedAndSubset(Pdf(new GlyphPdfBuilder().Run("a", 50, 100, 6))).Ok); // Type 3
        var result = PdfChecks.FontsEmbeddedAndSubset(standard);
        Assert.Equal(1, result.Value);
        Assert.Contains("Helvetica not embedded", result.Detail);
    }

    // ───────────────────────── Compare ─────────────────────────

    [Fact]
    public void Ssim_is_one_for_the_same_picture_and_drops_with_changes()
    {
        var page = Picture(0);
        Assert.Equal((1.0, 0.0), Ssim.Compare(page, page));
        Assert.Equal(1, Ssim.Compare(Blank(), Blank()).Ssim); // paper only: nothing to compare

        var moved = Ssim.Compare(page, Picture(3));
        Assert.InRange(moved.Ssim, 0, 0.9);
        Assert.InRange(Ssim.Compare(page, Blank()).Ssim, 0, 0.5);

        static GreyImage Picture(int shift)
        {
            using var bitmap = new SKBitmap(200, 100);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);
                using var paint = new SKPaint { Color = SKColors.Black };
                for (var x = 10; x < 190; x += 12)
                    canvas.DrawRect(x + shift, 20, 6, 50, paint);
            }
            return GreyImage.From(bitmap);
        }

        static GreyImage Blank() => new(200, 100, Enumerable.Repeat(255.0, 200 * 100).ToArray());
    }

    [Fact]
    public void Line_breaks_and_baselines_are_compared_line_by_line()
    {
        string[] lines = ["The first line", "the second line", "the third line", "and the last one"];
        var reference = new PdfBuilder().NewPage();
        for (var i = 0; i < lines.Length; i++)
            reference.Line(lines[i], 12, reference.Font(), 72, 100 + 16 * i);
        // The last line on the next page, the third broken one word earlier.
        var candidate = new PdfBuilder().NewPage();
        var font = candidate.Font();
        candidate.Line(lines[0], 12, font, 72, 100).Line(lines[1], 12, font, 72, 116).Line("the third", 12, font, 72, 132.3)
            .Line("line", 12, font, 72, 148).NewPage().Line(lines[3], 12, font, 72, 100);

        var (agree, placed, referenceLines, candidateLines, exact) =
            RenderDiff.CompareLines(PdfLines.Read(Pdf(reference)), PdfLines.Read(Pdf(candidate)));

        Assert.True(exact);
        Assert.Equal((4, 5), (referenceLines, candidateLines));
        Assert.Equal(3.0 / 5, agree, 4);  // first, second and last line
        Assert.Equal(2.0 / 5, placed, 4); // the last one moved to another page
    }

    [Fact]
    public void A_pdf_compared_with_itself_is_identical_and_a_shifted_copy_is_not()
    {
        var builder = new PdfBuilder().NewPage();
        var font = builder.Font();
        for (var i = 0; i < 10; i++)
            builder.Line($"Line {i + 1} of a page drawn twice", 12, font, 72, 100 + 16 * i);
        var pdf = Pdf(builder);

        var same = RenderDiff.Compare(pdf, pdf);
        Assert.True(same.SamePageCount && same.LineAgreement == 1 && same.BaselineAgreement == 1 && same.MinSsim == 1, same.ToString());

        var shifted = RenderDiff.Compare(pdf, RenderQualityTests.Shift(pdf, 2));
        Assert.Equal(1, shifted.LineAgreement);
        Assert.Equal(0, shifted.BaselineAgreement);
        Assert.True(shifted.MinSsim < 0.9, shifted.ToString());
    }

    // ───────────────────────── Thresholds ─────────────────────────

    [Fact]
    public void Measurements_are_judged_against_the_ideal_their_threshold_or_the_allow_list()
    {
        var thresholds = new RenderThresholds
        {
            Thresholds = new() { ["rhwp"] = new() { ["script-ar"] = new() { ["overlaps"] = 0.5, ["text"] = 0.9 } } },
            Allow = [new("filee", "script-ar", "vs-rhwp-*", "rhwp draws Arabic unshaped")],
        };
        Verdict Judge(string renderer, string feature, string check, double value, bool ok) =>
            thresholds.Judge(new Measurement(renderer, feature, new CheckResult(check, value, ok, "")));

        Assert.Equal(Verdict.Ideal, Judge("rhwp", "tables", "overlaps", 0, true));
        Assert.Equal(Verdict.Regression, Judge("rhwp", "tables", "overlaps", 0.01, false));
        Assert.Equal(Verdict.WithinThreshold, Judge("rhwp", "script-ar", "overlaps", 0.5, false));
        Assert.Equal(Verdict.Improved, Judge("rhwp", "script-ar", "overlaps", 0.4, false));
        Assert.Equal(Verdict.Ideal, Judge("rhwp", "script-ar", "overlaps", 0, true));
        Assert.Equal(Verdict.Regression, Judge("rhwp", "script-ar", "overlaps", 0.51, false));
        Assert.Equal(Verdict.Regression, Judge("rhwp", "script-ar", "text", 0.89, false));
        Assert.Equal(Verdict.Improved, Judge("rhwp", "script-ar", "text", 0.95, false));
        Assert.Equal(Verdict.Allowed, Judge("filee", "script-ar", "vs-rhwp-ssim", 0.2, false));
        Assert.Equal(Verdict.Regression, Judge("filee", "script-ar", "overlaps", 0.1, false)); // only the compares are allowed

        Assert.Equal("0.9734", RenderThresholds.Suggest(new CheckResult("text", 0.97349, false, "")));
        Assert.Equal("0.5311", RenderThresholds.Suggest(new CheckResult("overlaps", 0.53101, false, "")));
    }

    [Fact]
    public void The_thresholds_file_names_known_checks_and_gives_reasons()
    {
        var thresholds = RenderThresholds.Load();
        string[] checks = [PdfChecks.Text, PdfChecks.Overlaps, PdfChecks.OutsidePage, PdfChecks.OutsideMargins, PdfChecks.Pages,
            PdfChecks.Baselines, PdfChecks.Fonts, RenderThresholds.Lines, RenderThresholds.Placed, RenderThresholds.MinSsim];
        var features = RenderFeatures.Full.SelectMany(f => new[] { f.Name, f.Name + ".hwp" }).ToHashSet();
        // The slide renderer's features: one slide each of the slide deck, and the deck as a whole.
        var slides = SlideFeatures.Names.Append("deck").ToHashSet();

        Assert.Contains("rhwp", thresholds.Thresholds.Keys);
        foreach (var (renderer, byFeature) in thresholds.Thresholds)
        {
            foreach (var (feature, values) in byFeature)
            {
                Assert.Contains(feature, renderer == SlideQualityTests.Renderer ? slides : features);
                Assert.All(values.Keys, check => Assert.Contains(check.Replace(RenderThresholds.Versus, "", StringComparison.Ordinal)
                    .Replace(RenderThresholds.VersusLibreOffice, "", StringComparison.Ordinal), checks));
            }
        }
        Assert.All(thresholds.Allow, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
            Assert.True(entry.Feature == "*" || features.Contains(entry.Feature), entry.Feature);
        });
    }
}
