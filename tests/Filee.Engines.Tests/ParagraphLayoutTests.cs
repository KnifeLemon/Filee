// The layout engine's paragraph layout (Typesetting/ParagraphLayout.cs, ParagraphText.cs): line breaking against the
// column width, 한글 어절 / 글자, alignment, indents, tabs, line height rules, character spacing and the right-to-left
// start. Paragraphs are written here and laid out with the Noto fonts build/fetch-fonts.ps1 puts into
// src/Filee.App/Assets/Fonts (CI fetches them; without them the tests skip), so the results don't depend on the fonts
// a computer has. Expected widths come from the shaper itself, not from numbers kept here.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
using SkiaSharp;

namespace Filee.Engines.Tests;

public sealed class ParagraphLayoutTests : IDisposable
{
    private const string Latin = "Noto Sans";
    private const string Korean = "Noto Sans KR";
    private const string Arabic = "Noto Sans Arabic";

    private const string English = "Filee lays out every paragraph the way the document asks: line spacing, alignment and the place where each line ends.";
    private const string Hangul = "한글 문서는 줄 간격과 정렬에 따라 줄이 나뉘는 자리가 달라집니다. 같은 문단을 몇 번 그려도 줄은 같은 자리에서 끝나야 합니다.";

    private readonly Shaper _shaper = new();
    private readonly ParagraphLayouter _layouter;
    private readonly RepositoryFonts _fonts;

    public ParagraphLayoutTests()
    {
        _fonts = RepositoryFonts.Load();
        _layouter = new ParagraphLayouter(new FontResolver(_fonts), _shaper);
    }

    public void Dispose() => _shaper.Dispose();

    // ───────────────────────── Breaking ─────────────────────────

    [Fact]
    public void Lines_cover_the_text_fit_the_column_and_break_after_spaces()
    {
        var (box, text) = Layout(Paragraph(English), 200);

        Assert.True(box.Lines.Count > 2);
        Assert.Equal(text, string.Concat(box.Lines.Select(l => text[l.Start..l.End])));
        foreach (var line in box.Lines)
            Assert.True(line.Width <= 200.01f, $"{line.Width} > 200");
        foreach (var line in box.Lines.SkipLast(1))
            Assert.Equal(' ', text[line.End - 1]);
        // Greedy: the next word wouldn't have fitted.
        var first = box.Lines[0];
        var nextWord = text[first.End..].Split(' ')[0];
        Assert.True(Width(text[first.Start..first.End] + nextWord, Latin) > 200);
    }

    [Fact]
    public void Korean_breaks_between_syllables_by_default_and_between_words_with_eojeol()
    {
        var syllables = Layout(Paragraph(Hangul, breaking: new HLineBreaking(NonLatin: HNonLatinBreak.BreakWord)), 180);
        var words = Layout(Paragraph(Hangul, breaking: new HLineBreaking(NonLatin: HNonLatinBreak.KeepWord)), 180);

        Assert.All(words.Box.Lines.SkipLast(1), line => Assert.Equal(' ', words.Text[line.End - 1]));
        Assert.Contains(syllables.Box.Lines.SkipLast(1), line => syllables.Text[line.End - 1] != ' ');
        Assert.True(syllables.Box.Lines.Count <= words.Box.Lines.Count);
    }

    [Fact]
    public void Latin_words_break_anywhere_when_the_paragraph_says_so()
    {
        var (box, text) = Layout(Paragraph(English, breaking: new HLineBreaking(Latin: HLatinBreak.BreakWord)), 150);

        Assert.Contains(box.Lines.SkipLast(1), line => text[line.End - 1] != ' ');
        Assert.All(box.Lines, line => Assert.True(line.Width <= 150.01f));
    }

    [Fact]
    public void A_word_wider_than_the_column_breaks_between_clusters()
    {
        var (box, text) = Layout(Paragraph("Pneumonoultramicroscopicsilicovolcanoconiosis"), 60);

        Assert.True(box.Lines.Count > 1);
        Assert.Equal(text, string.Concat(box.Lines.Select(l => text[l.Start..l.End])));
        Assert.All(box.Lines, line => Assert.True(line.Width <= 60.01f));
    }

    [Fact]
    public void A_line_break_ends_the_line_and_a_last_one_leaves_an_empty_line()
    {
        var paragraph = Paragraph("one");
        paragraph.Inlines.Add(new HLineBreak(Format()));
        paragraph.Inlines.Add(new HText("two", Format()));
        paragraph.Inlines.Add(new HLineBreak(Format()));

        var (box, text) = Layout(paragraph, 400);

        Assert.Equal(3, box.Lines.Count);
        Assert.Equal("two", text[box.Lines[1].Start..box.Lines[1].End].TrimEnd(ParagraphText.LineSeparator));
        Assert.Equal(box.Lines[2].Start, box.Lines[2].End);
        Assert.Empty(box.Lines[2].Runs);
    }

    [Fact]
    public void Condense_keeps_a_word_that_fits_once_the_spaces_shrink()
    {
        var text = "aaaa aaaa aaaa aaaa aaaa aaaa";
        var natural = Width(text, Latin);
        // The last word is 2 pt too wide; five spaces can give that up at 50%.
        var width = natural - 2;

        var plain = Layout(Paragraph(text), width);
        var condensed = Layout(Paragraph(text, breaking: new HLineBreaking(Condense: 50)), width);

        Assert.Equal(2, plain.Box.Lines.Count);
        var line = Assert.Single(condensed.Box.Lines);
        Assert.True(line.Width <= width + 0.01f, $"{line.Width} > {width}");
    }

    [Theory]
    [InlineData(nameof(HLineWrap.Keep))]
    [InlineData(nameof(HLineWrap.Squeeze))]
    public void Keep_and_squeeze_hold_the_paragraph_on_one_line(string mode)
    {
        var wrap = Enum.Parse<HLineWrap>(mode);
        var (box, _) = Layout(Paragraph(English, breaking: new HLineBreaking(LineWrap: wrap)), 200);

        var line = Assert.Single(box.Lines);
        if (wrap == HLineWrap.Squeeze)
            Assert.InRange(line.Width, 199.9f, 200.01f);
        else
            Assert.True(line.Width > 200);
    }

    // ───────────────────────── Alignment and indents ─────────────────────────

    [Theory]
    [InlineData(nameof(HAlign.Left))]
    [InlineData(nameof(HAlign.Right))]
    [InlineData(nameof(HAlign.Center))]
    public void Alignment_places_each_line_in_the_column(string alignment)
    {
        var align = Enum.Parse<HAlign>(alignment);
        var (box, _) = Layout(Paragraph(English, new HParaFormat(Align: align, Left: 1000, Right: 2000)), 300);

        foreach (var line in box.Lines)
        {
            var expected = align switch
            {
                HAlign.Left => 10f,
                HAlign.Right => 300 - 20 - line.Width,
                _ => 10 + (300 - 30 - line.Width) / 2,
            };
            Assert.Equal(expected, line.Left, 0.01f);
        }
    }

    [Fact]
    public void Justify_fills_every_line_but_the_last()
    {
        var (box, _) = Layout(Paragraph(English, new HParaFormat(Align: HAlign.Justify)), 220);

        foreach (var line in box.Lines.SkipLast(1))
            Assert.Equal(220, line.Width, 0.05f);
        Assert.True(box.Lines[^1].Width < 219);
        Assert.Equal(0, box.Lines[^1].Left, 0.01f);
    }

    [Fact]
    public void Distribute_fills_the_last_line_too()
    {
        var (box, _) = Layout(Paragraph(Hangul, new HParaFormat(Align: HAlign.Distribute)), 220);

        Assert.All(box.Lines, line => Assert.Equal(220, line.Width, 0.05f));
    }

    [Fact]
    public void A_first_line_indent_moves_the_first_line_and_a_hanging_one_the_others()
    {
        var indented = Layout(Paragraph(English, new HParaFormat(Left: 1000, FirstLine: 2000)), 250).Box;
        var hanging = Layout(Paragraph(English, new HParaFormat(Left: 3000, FirstLine: -2000)), 250).Box;

        Assert.Equal(30, indented.Lines[0].Left, 0.01f);
        Assert.Equal(10, indented.Lines[1].Left, 0.01f);
        Assert.Equal(10, hanging.Lines[0].Left, 0.01f);
        Assert.Equal(30, hanging.Lines[1].Left, 0.01f);
        Assert.True(indented.Lines[0].Width <= 250 - 30 + 0.01f);
    }

    [Theory]
    [InlineData(nameof(HTabKind.Left))]
    [InlineData(nameof(HTabKind.Right))]
    [InlineData(nameof(HTabKind.Center))]
    [InlineData(nameof(HTabKind.Decimal))]
    public void A_tab_aligns_the_text_after_it_at_its_stop(string tab)
    {
        var kind = Enum.Parse<HTabKind>(tab);
        var paragraph = new HParagraph { Format = new HParaFormat(Tabs: [new HTabStop(15000, kind, "DOT")]) };
        paragraph.Inlines.Add(new HText("Item", Format()));
        paragraph.Inlines.Add(new HTab(Format()));
        paragraph.Inlines.Add(new HText("123.45", Format()));

        var line = Assert.Single(Layout(paragraph, 400).Box.Lines);

        var (start, end, dot) = Extent(line, "123.45");
        var at = kind switch
        {
            HTabKind.Left => start,
            HTabKind.Right => end,
            HTabKind.Center => (start + end) / 2,
            _ => dot,
        };
        Assert.Equal(150, at, 0.05f);
        // The leader runs between "Item" and the number.
        var leader = Assert.Single(line.Decorations, d => d.Kind == DecorationKind.Dotted);
        Assert.True(leader.Left >= Extent(line, "Item").End && leader.Right <= start);
    }

    [Fact]
    public void Default_tab_stops_are_every_forty_points_from_the_indent()
    {
        var paragraph = new HParagraph { Format = new HParaFormat(Left: 1000) };
        paragraph.Inlines.Add(new HText("a", Format()));
        paragraph.Inlines.Add(new HTab(Format()));
        paragraph.Inlines.Add(new HText("b", Format()));

        var line = Assert.Single(Layout(paragraph, 400).Box.Lines);

        Assert.Equal(10 + 40, Extent(line, "b").Start, 0.05f);
    }

    [Fact]
    public void A_list_number_hangs_and_the_text_starts_at_the_indent()
    {
        var paragraph = Paragraph(English, new HParaFormat(Left: 2000, FirstLine: -2000));

        var (box, text) = Layout(paragraph, 250, marker: "1.");

        Assert.StartsWith("1.\t", text);
        Assert.Equal(0, box.Lines[0].Left, 0.01f);
        Assert.Equal(20, Extent(box.Lines[0], "Filee").Start, 0.05f);
        Assert.Equal(20, box.Lines[1].Left, 0.01f);
    }

    // ───────────────────────── Line height ─────────────────────────

    [Theory]
    [InlineData(nameof(HLineHeightRule.Percent), 160, 16)]
    [InlineData(nameof(HLineHeightRule.Percent), 100, 10)]
    [InlineData(nameof(HLineHeightRule.Exact), 2000, 20)]
    [InlineData(nameof(HLineHeightRule.AtLeast), 2400, 24)]
    [InlineData(nameof(HLineHeightRule.AtLeast), 500, 10)]
    [InlineData(nameof(HLineHeightRule.BetweenLines), 500, 15)]
    public void Hangul_line_rules_measure_from_the_font_size(string lineRule, int value, float pitch)
    {
        var rule = Enum.Parse<HLineHeightRule>(lineRule);
        var (box, _) = Layout(Paragraph(English, new HParaFormat(Layout: new HParaLayout(new HLineHeight(rule, value)))), 200);

        Assert.All(box.Lines, line => Assert.Equal(pitch, line.Height, 0.01f));
        // 한글 puts the baseline at 85% of the text height.
        Assert.Equal(8.5f, box.Lines[0].Baseline, 0.01f);
    }

    [Theory]
    [InlineData(240)]
    [InlineData(360)]
    public void Word_multiple_line_spacing_measures_from_the_font(int value)
    {
        var (box, _) = Layout(Paragraph(English, new HParaFormat(Layout: new HParaLayout(new HLineHeight(HLineHeightRule.Multiple, value)))), 200);

        var metrics = _shaper.Metrics(_fonts.Font(Latin));
        var single = (metrics.WinAscent + metrics.WinDescent) * 10f / metrics.UnitsPerEm;
        Assert.All(box.Lines, line => Assert.Equal(single * value / 240, line.Height, 0.01f));
        Assert.Equal(metrics.WinAscent * 10f / metrics.UnitsPerEm, box.Lines[0].Baseline, 0.01f);
    }

    [Fact]
    public void The_largest_text_on_a_line_sets_its_height()
    {
        var paragraph = Paragraph("small ");
        paragraph.Inlines.Add(new HText("BIG", Format(size: 2000)));

        var line = Assert.Single(Layout(paragraph, 400).Box.Lines);

        Assert.Equal(32, line.Height, 0.01f); // 160% of 20 pt
    }

    [Fact]
    public void An_empty_paragraph_is_one_line_as_high_as_its_mark()
    {
        var paragraph = new HParagraph { MarkFormat = Format(size: 1500) };

        var line = Assert.Single(Layout(paragraph, 400).Box.Lines);

        Assert.Equal(24, line.Height, 0.01f);
        Assert.Empty(line.Runs);
    }

    [Fact]
    public void Space_before_and_after_are_kept_apart_from_the_lines()
    {
        var (box, _) = Layout(Paragraph(English, new HParaFormat(Before: 600, After: 1200)), 200);

        Assert.Equal(6, box.SpaceBefore);
        Assert.Equal(12, box.SpaceAfter);
        Assert.Equal(6 + 12 + box.Lines.Sum(l => l.Height), box.Height, 0.01f);
    }

    // ───────────────────────── Characters ─────────────────────────

    [Fact]
    public void Letter_spacing_widens_each_character_by_a_share_of_its_own_width()
    {
        var plain = Single(Paragraph("abcd")).Width;
        var spaced = Single(Paragraph("abcd", format: Format(spacing: 10))).Width;
        var narrow = Single(Paragraph("abcd", format: Format(spacing: 10) with { Layout = new HCharLayout(HLanguageSlots.All(new HSlotFormat(Ratio: 50))) })).Width;

        // 한글's 자간: 10% of each character's width, the width ratio applied.
        Assert.Equal(plain * 1.1f, spaced, 0.01f);
        Assert.Equal(plain / 2 * 1.1f, narrow, 0.01f);
    }

    [Fact]
    public void Word_letter_spacing_is_an_absolute_distance()
    {
        var plain = Single(Paragraph("abcd")).Width;
        var spaced = Single(Paragraph("abcd", format: Format() with { Layout = new HCharLayout(WordFonts: new HWordFonts(Latin, Latin, Korean, Latin), LetterSpacing: 100) })).Width;

        Assert.Equal(plain + 4, spaced, 0.01f); // 1 pt after each of four characters
    }

    [Fact]
    public void The_width_ratio_scales_the_glyphs()
    {
        var plain = Single(Paragraph("abcd"));
        var narrow = Single(Paragraph("abcd", format: Format() with { Layout = new HCharLayout(HLanguageSlots.All(new HSlotFormat(Ratio: 50))) }));

        Assert.Equal(plain.Width / 2, narrow.Width, 0.01f);
        Assert.Equal(0.5f, narrow.Runs[0].ScaleX, 0.001f);
    }

    [Fact]
    public void Without_the_fonts_own_space_a_space_is_half_the_size()
    {
        var format = Format() with { Layout = new HCharLayout(FontSpace: false) };

        var spaced = Single(Paragraph("a a", format: format)).Width;
        var joined = Single(Paragraph("aa", format: format)).Width;

        Assert.Equal(joined + 5, spaced, 0.01f);
    }

    [Fact]
    public void Superscript_is_smaller_and_raised()
    {
        var paragraph = Paragraph("x");
        paragraph.Inlines.Add(new HText("2", Format() with { Superscript = true }));

        var line = Single(paragraph);

        var two = line.Runs.Single(r => r.Text == "2");
        Assert.True(two.Size < 10);
        Assert.True(two.Positions[0].Y < 0, "raised above the baseline");
    }

    [Fact]
    public void Underline_strike_and_shade_are_drawn_under_and_through_the_text()
    {
        var format = Format() with { Underline = true, Strike = true, Shade = "#FFFF00" };

        var line = Single(Paragraph("marked", format: format));

        var fill = Assert.Single(line.Decorations, d => d.Kind == DecorationKind.Fill);
        Assert.Equal(new SKColor(255, 255, 0), fill.Color);
        var lines = line.Decorations.Where(d => d.Kind == DecorationKind.Line).OrderBy(d => d.Top).ToList();
        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].Top < 0, "strike-through above the baseline");
        Assert.True(lines[1].Top > 0, "underline below it");
        Assert.All(line.Decorations, d => Assert.Equal(line.Width, d.Right - d.Left, 0.01f));
    }

    [Fact]
    public void Links_cover_their_text()
    {
        var paragraph = Paragraph("see ");
        var link = new HLink("https://filee.sh");
        link.Content.Add(new HText("filee.sh", Format()));
        paragraph.Inlines.Add(link);

        var line = Single(paragraph);

        var area = Assert.Single(line.Links);
        Assert.Equal("https://filee.sh", area.Target);
        var (start, end, _) = Extent(line, "filee.sh");
        Assert.Equal(start, area.Left, 0.01f);
        Assert.Equal(end, area.Right, 0.01f);
    }

    [Fact]
    public void A_right_to_left_paragraph_starts_at_the_right_and_keeps_its_text_in_order()
    {
        Assert.SkipUnless(_fonts.Has(Arabic), "Noto Sans Arabic is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        const string text = "يحافظ برنامج التحويل على تنسيق الفقرات ومواضع فواصل الأسطر.";

        var (box, _) = Layout(Paragraph(text, format: Format(font: Arabic)), 150);

        Assert.True(box.Lines.Count > 1);
        Assert.All(box.Lines, line => Assert.Equal(150, line.Left + line.Width, 0.05f));
        Assert.Equal(text.Replace(" ", ""), string.Concat(box.Lines.SelectMany(l => l.Runs).Select(r => r.Text)).Replace(" ", ""));
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static HCharFormat Format(int size = 1000, int spacing = 0, string font = Latin) =>
        new(Size: size, Font: font, EastAsianFont: Korean, Spacing: spacing, Color: "#000000");

    private static HParagraph Paragraph(string text, HParaFormat paragraph = default, HLineBreaking? breaking = null, HCharFormat? format = null)
    {
        if (breaking is not null)
            paragraph = paragraph with { Layout = (paragraph.Layout ?? new HParaLayout()) with { Breaking = breaking } };
        var result = new HParagraph { Format = paragraph, MarkFormat = format ?? Format() };
        result.Inlines.Add(new HText(text, format ?? Format()));
        return result;
    }

    private (ParagraphBox Box, string Text) Layout(HParagraph paragraph, float width, string? marker = null)
    {
        Assert.SkipUnless(_fonts.Has(Latin) && _fonts.Has(Korean), "Noto Sans and Noto Sans KR are not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        return (_layouter.Layout(paragraph, width, marker), ParagraphText.From(paragraph, marker).Text);
    }

    private LineBox Single(HParagraph paragraph) => Assert.Single(Layout(paragraph, 400).Box.Lines);

    private float Width(string text, string family) => _shaper.Shape(_fonts.Font(family), text).Width(10);

    /// <summary>Where the glyphs of <paramref name="text"/> start and end on the line, and the x of its '.' (or NaN).</summary>
    private static (float Start, float End, float Dot) Extent(LineBox line, string text)
    {
        var run = line.Runs.First(r => r.Text.Contains(text, StringComparison.Ordinal));
        var from = run.Text.IndexOf(text, StringComparison.Ordinal);
        using var font = new SKFont(run.Font.Typeface, run.Size) { ScaleX = run.ScaleX, LinearMetrics = true, Subpixel = true };
        float start = float.MaxValue, end = float.MinValue, dot = float.NaN;
        for (var g = 0; g < run.Glyphs.Length; g++)
        {
            var cluster = run.Clusters[g];
            if (cluster < from || cluster >= from + text.Length)
                continue;
            var x = run.Positions[g].X;
            start = Math.Min(start, x);
            end = Math.Max(end, x + font.GetGlyphWidths([run.Glyphs[g]])[0]);
            if (run.Text[cluster] == '.')
                dot = x;
        }
        return (start, end, dot);
    }

    /// <summary>The Noto fonts of src/Filee.App/Assets/Fonts as the only fonts there are.</summary>
    private sealed class RepositoryFonts : IFontCollection
    {
        private readonly Dictionary<string, List<FontFace>> _families = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PageFont> _fonts = new(StringComparer.OrdinalIgnoreCase);

        public static RepositoryFonts Load()
        {
            var fonts = new RepositoryFonts();
            var folder = Folder();
            if (folder is null)
                return fonts;
            (string File, string Family, int Weight)[] files =
            [
                ("NotoSans-Regular.ttf", Latin, 400), ("NotoSans-Bold.ttf", Latin, 700),
                ("NotoSansKR-Regular.otf", Korean, 400), ("NotoSansKR-Bold.otf", Korean, 700),
                ("NotoSansArabic-Regular.ttf", Arabic, 400),
            ];
            foreach (var (file, family, weight) in files)
            {
                var path = Path.Combine(folder, file);
                if (!File.Exists(path))
                    continue;
                var font = PageFont.FromFile(path);
                var typeface = font.Typeface;
                if (weight == 400)
                    fonts._fonts[family] = font;
                if (!fonts._families.TryGetValue(family, out var faces))
                    fonts._families[family] = faces = [];
                faces.Add(new FontFace(family, weight, false, c => typeface.ContainsGlyph(c), () => font));
            }
            return fonts;
        }

        public bool Has(string family) => _fonts.ContainsKey(family);

        public PageFont Font(string family) => _fonts[family];

        public IReadOnlyList<FontFace> Family(string name) => _families.GetValueOrDefault(name) ?? [];

        public FontFace? ForCharacter(int codePoint, FontStyle style, string? language) =>
            _families.Values.SelectMany(f => f).FirstOrDefault(f => f.Weight == 400 && f.Has(codePoint));

        private static string? Folder()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Filee.slnx")))
                    return Path.Combine(dir.FullName, "src", "Filee.App", "Assets", "Fonts");
            }
            return null;
        }
    }
}
