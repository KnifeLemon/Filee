// Word equations on Filee's own route (DOCX → model → layout engine → page), checked where the glyphs land: fractions,
// radicals, sums with limits, brackets of several heights, matrices, equation arrays lined up at their & marks, accents,
// the equation number at the right margin and long equations broken into lines (#355, #356, #143).
//
// Brackets and radical signs come from the computer's math font (Cambria Math on Windows, STIX Two Math on macOS);
// those checks skip where none is installed. The drawn brackets of computers without one are checked with the
// repository's fonts, which have no math font.

using System.Diagnostics;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
using SkiaSharp;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public sealed class WordEquationLayoutTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-word-math").FullName;
    private readonly Shaper _shaper = new();
    private readonly FontResolver _system = new();

    public void Dispose()
    {
        _shaper.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ───────── OMML ─────────

    private static string MR(string text, string sty = "") =>
        $"<m:r>{(sty.Length > 0 ? $"<m:rPr><m:sty m:val=\"{sty}\"/></m:rPr>" : "")}<m:t>{System.Security.SecurityElement.Escape(text)}</m:t></m:r>";

    private static string Frac(string numerator, string denominator) => $"<m:f><m:num>{numerator}</m:num><m:den>{denominator}</m:den></m:f>";

    private static string Rad(string body, string degree = "") =>
        $"<m:rad><m:radPr>{(degree.Length == 0 ? "<m:degHide m:val=\"1\"/>" : "")}</m:radPr><m:deg>{degree}</m:deg><m:e>{body}</m:e></m:rad>";

    private static string Nary(string chr, string sub, string sup, string body) =>
        $"<m:nary><m:naryPr><m:chr m:val=\"{chr}\"/><m:limLoc m:val=\"undOvr\"/></m:naryPr><m:sub>{sub}</m:sub><m:sup>{sup}</m:sup><m:e>{body}</m:e></m:nary>";

    private static string Delimited(string content, string open, string close) =>
        $"<m:d><m:dPr><m:begChr m:val=\"{open}\"/><m:endChr m:val=\"{close}\"/></m:dPr><m:e>{content}</m:e></m:d>";

    private static string EqArr(params string[] lines) => "<m:eqArr>" + string.Concat(lines.Select(l => $"<m:e>{l}</m:e>")) + "</m:eqArr>";

    private static string Matrix(params string[][] rows) =>
        "<m:m>" + string.Concat(rows.Select(r => "<m:mr>" + string.Concat(r.Select(c => $"<m:e>{c}</m:e>")) + "</m:mr>")) + "</m:m>";

    private static string Accent(string chr, string body) => $"<m:acc><m:accPr><m:chr m:val=\"{chr}\"/></m:accPr><m:e>{body}</m:e></m:acc>";

    private static string Display(string content) => $"<m:oMathPara><m:oMath>{content}</m:oMath></m:oMathPara>";

    /// <summary>One display equation per paragraph, 20 pt so the shapes are large enough to measure.</summary>
    private static DocxBuilder Equations(params string[] equations)
    {
        var docx = new DocxBuilder();
        foreach (var equation in equations)
            docx.Paragraph(P(Display(equation.Replace("<m:r>", "<m:r><w:rPr><w:sz w:val=\"40\"/></w:rPr>", StringComparison.Ordinal)), "<w:jc w:val=\"center\"/>"));
        return docx;
    }

    // ───────── Math font: brackets and radicals ─────────

    [Fact]
    public void A_brace_before_an_equation_array_is_the_math_font_s_and_spans_its_lines()
    {
        SkipWithoutMathFont();
        var page = Render(Equations(Delimited(EqArr(MR("x+y=1"), MR("x−y=2"), MR("2x=3")), "{", "")))[0];

        var brace = Single(page, "{");
        Assert.NotNull(MathTable.Of(brace.Font));
        var (ink, size) = (Ink(brace), 20f);
        var lines = Runs(page, "=").Select(Ink).OrderBy(r => r.Top).ToList();
        Assert.Equal(3, lines.Count);
        // From above the first line's letters to below the last one's, much taller than the brace of one line.
        var letters = Runs(page, "x").Concat(Runs(page, "2")).Select(Ink).ToList();
        Assert.True(ink.Top <= letters.Min(r => r.Top) && ink.Bottom >= letters.Max(r => r.Bottom), $"{ink} / {letters.Min(r => r.Top)}…{letters.Max(r => r.Bottom)}");
        Assert.True(ink.Height > 3 * size * 0.8f, $"{ink.Height}");
        // Heavier than the old 0.06 em line: the glyph is at least a fifth of an em wide.
        Assert.True(ink.Width > size * 0.2f, $"{ink.Width}");
    }

    [Fact]
    public void Brackets_grow_in_steps_with_what_they_hold_and_cover_it()
    {
        SkipWithoutMathFont();
        var page = Render(Equations(
            Delimited(MR("x"), "(", ")"),
            Delimited(Frac(MR("a"), MR("b")), "(", ")"),
            Delimited(Frac(Frac(MR("a"), MR("b")), Frac(MR("c"), MR("d"))), "(", ")"),
            Delimited(EqArr([.. Enumerable.Range(1, 6).Select(i => MR($"{i}"))]), "[", "]")))[0];

        var brackets = Runs(page, "(").Concat(Runs(page, "[")).OrderBy(r => r.Positions.Min(p => p.Y)).ToList();
        Assert.Equal(4, brackets.Count);
        Assert.All(brackets, b => Assert.NotNull(MathTable.Of(b.Font)));
        var heights = brackets.Select(b => Ink(b).Height).ToList();
        for (var i = 1; i < heights.Count; i++)
            Assert.True(heights[i] > heights[i - 1] + 2, string.Join(", ", heights));
        // The plain one is the font's own bracket, about a line high; the six lines take an assembly of parts.
        Assert.InRange(heights[0], 20 * 0.9f, 20 * 1.5f);
        Assert.True(brackets[^1].Glyphs.Length > 1 || heights[^1] > 6 * 20 * 0.9f, $"{brackets[^1].Glyphs.Length} glyphs, {heights[^1]} pt");

        // Each covers its content: the six digits lie between the ends of the square brackets.
        var digits = Enumerable.Range(1, 6).Select(i => Ink(Single(page, $"{i}"))).ToList();
        var square = Ink(brackets[^1]);
        Assert.True(square.Top <= digits.Min(d => d.Top) && square.Bottom >= digits.Max(d => d.Bottom), $"{square} / {digits.Min(d => d.Top)}…{digits.Max(d => d.Bottom)}");
        // Closing brackets mirror the opening ones at the same height.
        var closing = Runs(page, ")").Concat(Runs(page, "]")).OrderBy(r => r.Positions.Min(p => p.Y)).Select(r => Ink(r).Height).ToList();
        Assert.Equal(heights, closing, (a, b) => Math.Abs(a - b) < 0.5f);
    }

    [Fact]
    public void A_radical_sign_from_the_math_font_reaches_over_its_body()
    {
        SkipWithoutMathFont();
        var page = Render(Equations(Rad(Frac(MR("a"), MR("b"))), Rad(MR("x"), MR("3"))))[0];

        var signs = Runs(page, "√").OrderBy(r => r.Positions[0].Y).ToList();
        Assert.Equal(2, signs.Count);
        Assert.All(signs, s => Assert.NotNull(MathTable.Of(s.Font)));
        var (sign, a, b) = (Ink(signs[0]), Ink(Single(page, "a")), Ink(Single(page, "b")));
        Assert.True(sign.Top < a.Top && sign.Bottom > b.Top, $"{sign} / {a} / {b}");
        Assert.True(sign.Right <= a.Left + 0.5f, $"{sign.Right} / {a.Left}");
        Assert.True(sign.Height > Ink(signs[1]).Height + 5);

        // The index sits small on the left arm, above the sign's foot.
        var (three, x, root) = (Single(page, "3"), Ink(Single(page, "x")), Ink(signs[1]));
        Assert.Equal(20 * 0.6f, three.Size, 0.01f);
        Assert.True(Ink(three).Left < root.Left + root.Width / 2 && Ink(three).Bottom < root.Bottom - 2, $"{Ink(three)} / {root}");
        // The body starts after the sign (an italic x leans a little back over it).
        Assert.True(Single(page, "x").Positions[0].X >= root.Right - 1, $"{x} / {root}");
        Assert.True(x.Top > root.Top, $"{x} / {root}");
    }

    [Fact]
    public void A_matrix_has_columns_rows_and_brackets_over_both_rows()
    {
        SkipWithoutMathFont();
        var page = Render(Equations(MR("A=") + Delimited(Matrix([MR("1"), MR("22")], [MR("333"), MR("4")]), "(", ")")))[0];

        var (one, two, three, four) = (Single(page, "1"), Single(page, "22"), Single(page, "333"), Single(page, "4"));
        // Rows on one baseline each; cells centred in their columns.
        Assert.Equal(one.Positions[0].Y, two.Positions[0].Y, 0.01f);
        Assert.Equal(three.Positions[0].Y, four.Positions[0].Y, 0.01f);
        Assert.True(three.Positions[0].Y > one.Positions[0].Y + 20);
        Assert.Equal(Centre(Ink(one)), Centre(Ink(three)), 0.5f);
        Assert.Equal(Centre(Ink(two)), Centre(Ink(four)), 0.5f);
        var bracket = Ink(Single(page, "("));
        Assert.True(bracket.Top <= Ink(one).Top && bracket.Bottom >= Ink(three).Bottom, $"{bracket}");
        Assert.True(bracket.Right <= Ink(three).Left && Ink(Single(page, ")")).Left >= Ink(four).Right);
    }

    // ───────── Any fonts: structure ─────────

    [Fact]
    public void Equation_array_lines_line_up_at_their_alignment_marks()
    {
        // Issue #355's system of equations: without the marks each line was centred by itself.
        var page = Render(Equations(EqArr(MR("x+y&=10"), MR("2x&=4"), MR("x−3y+z&=0")), EqArr(MR("a&=b&c&=d"), MR("aaa&=b&c&=dd"))))[0];

        var equals = Runs(page, "=").OrderBy(r => r.Positions[0].Y).ThenBy(r => r.Positions[0].X).ToList();
        Assert.Equal(7, equals.Count);
        var first = equals.Take(3).Select(e => e.Positions[0].X).ToList();
        Assert.True(first.Max() - first.Min() < 0.5f, string.Join(", ", first));
        // The lines start at different places (the left parts end at the mark) and are on their own baselines.
        var lefts = new[] { "y", "2", "z" }.Select(t => Runs(page, t).Min(r => r.Positions[0].Y)).ToList();
        Assert.True(lefts[0] < lefts[1] && lefts[1] < lefts[2]);
        // Two pairs: each pair's = lines up, and the second pair starts a space after the first's widest part.
        var (top, bottom) = (equals.Skip(3).Take(2).ToList(), equals.Skip(5).Take(2).ToList());
        Assert.Equal(top[0].Positions[0].X, bottom[0].Positions[0].X, 0.5f);
        Assert.Equal(top[1].Positions[0].X, bottom[1].Positions[0].X, 0.5f);
        Assert.True(top[1].Positions[0].X - top[0].Positions[0].X > 20 * 1.5f);
    }

    [Fact]
    public void A_fraction_stands_its_parts_centred_above_and_below_the_axis()
    {
        var page = Render(Equations(Frac(MR("a+b"), MR("2c"))))[0];
        var (a, b, two, c) = (Ink(Single(page, "a")), Ink(Single(page, "b")), Ink(Single(page, "2")), Ink(Single(page, "c")));
        Assert.True(two.Top > a.Bottom + 2, $"{a} / {two}");
        // Numerator and denominator centred on each other.
        Assert.Equal((a.Left + b.Right) / 2, (two.Left + c.Right) / 2, 1f);
        Assert.Equal(20f, Single(page, "a").Size, 0.01f);
    }

    [Fact]
    public void A_sum_has_its_limits_centred_above_and_below_and_an_integral_beside_it()
    {
        var page = Render(Equations(Nary("∑", MR("i=1"), MR("n"), MR("i")), Nary("∫", MR("0"), MR("1"), MR("x"))))[0];

        var sum = Single(page, "∑");
        var (sign, n, lower) = (Ink(sum), Single(page, "n"), Runs(page, "i").OrderBy(r => r.Positions[0].X).First());
        Assert.Equal(20 * 1.4f, sum.Size, 0.01f);
        Assert.Equal(20 * 0.7f, n.Size, 0.01f);
        Assert.True(Ink(n).Bottom < sign.Top && Ink(lower).Top > sign.Bottom, $"{Ink(n)} / {sign} / {Ink(lower)}");
        var ones = Runs(page, "1").Where(r => r.Size < 15).OrderBy(r => r.Positions[0].Y).ToList();
        var limits = (Ink(lower).Left + Ink(ones[0]).Right) / 2;
        Assert.Equal(Centre(sign), limits, 1f);
        Assert.Equal(Centre(sign), Centre(Ink(n)), 1f);

        var integral = Ink(Single(page, "∫"));
        var (zero, upper) = (Ink(Single(page, "0")), Ink(ones[^1]));
        Assert.True(zero.Left > Centre(integral) && upper.Left > Centre(integral));
        Assert.True(upper.Bottom < zero.Top);
    }

    [Fact]
    public void An_accent_sits_over_its_letter_and_raises_the_box()
    {
        var math = new MathLayout(_system, _shaper);
        static MathNode Read(string omml) => OmmlReader.Read(System.Xml.Linq.XElement.Parse($"<m:oMath {Namespaces}>{omml}</m:oMath>"));
        var plain = math.Layout(Read(MR("a")), 20, SKColors.Black);
        var hat = math.Layout(Read(Accent("̂", MR("a"))), 20, SKColors.Black);
        var bar = math.Layout(Read("<m:bar><m:barPr><m:pos m:val=\"top\"/></m:barPr><m:e>" + MR("AB") + "</m:e></m:bar>"), 20, SKColors.Black);

        Assert.True(hat.Ascent > plain.Ascent + 2 && hat.Width == plain.Width);
        var page = new PageContent(200, 200);
        hat.Paint(page, 50, 100, 1);
        var letter = Ink(Single(page, "a"));
        Assert.Equal(100, Single(page, "a").Positions[0].Y, 0.01f);
        var mark = DrawnInk(page);
        Assert.True(mark.Bottom <= letter.Top + 0.5f, $"{mark} / {letter}");
        Assert.InRange(Centre(mark), letter.Left - 1, letter.Right + 1);

        // Word's bar (m:bar) is a line over the whole of its content.
        Assert.Equal(bar.Width, math.Layout(Read(MR("AB")), 20, SKColors.Black).Width, 0.01f);
        var barred = new PageContent(200, 200);
        bar.Paint(barred, 50, 100, 1);
        var (line, letters) = (DrawnInk(barred), Ink(Single(barred, "AB")));
        Assert.True(line.Bottom <= letters.Top && line.Height < 3, $"{line} / {letters}");
        Assert.True(line.Left <= letters.Left + 2 && line.Right >= letters.Right - 2, $"{line} / {letters}");
    }

    // ───────── Equation numbers and long equations (#1143) ─────────

    /// <summary>A4 with 1 inch margins: the column ends 72 pt from the right edge.</summary>
    private const float RightMargin = 595.3f - 72;

    [Fact]
    public void An_equation_number_is_at_the_right_margin_centred_on_the_fraction_bar()
    {
        // 11 pt text: the number DocxReader places for rhwp (NumberBox) was 7 pt below the bar on this route.
        var docx = new DocxBuilder().Paragraph(P(Display(EqArr(Frac(MR("a"), MR("b")) + MR("=c#(1)"))), "<w:jc w:val=\"center\"/>"));
        var page = Render(docx)[0];

        Assert.DoesNotContain(page.Items.OfType<GlyphItem>(), i => i.Run.Text.Contains('#'));
        var number = Ink(Single(page, "(1)"));
        Assert.InRange(number.Right, RightMargin - 2, RightMargin + 0.5f);
        // Word centres the number on the equation's math axis: the fraction's bar.
        var bar = DrawnInk(page);
        TestContext.Current.TestOutputHelper?.WriteLine($"number middle {number.MidY:0.00} pt, fraction bar {bar.MidY:0.00} pt");
        Assert.Equal(bar.MidY, number.MidY, 0.5f);
        // The equation still in the middle of the line.
        Assert.InRange(Centre(Ink(Single(page, "a"))), 297.6f - 20, 297.6f + 20);
    }

    [Fact]
    public void An_equation_number_beside_a_radical_is_centred_on_the_math_axis()
    {
        var page = Render(new DocxBuilder().Paragraph(P(Display(EqArr(Rad(MR("x+1")) + MR("#(2)"))), "<w:jc w:val=\"center\"/>")))[0];

        var (x, number) = (Single(page, "x"), Ink(Single(page, "(2)")));
        // The axis is above the baseline the radical's body stands on.
        var axis = x.Positions[0].Y - x.Size * MathLayout.Axis;
        Assert.Equal(axis, number.MidY, 0.5f);
        Assert.InRange(number.Right, RightMargin - 2, RightMargin + 0.5f);
        Assert.True(Ink(x).Right < number.Left - 50);
    }

    [Fact]
    public void Each_line_of_an_equation_array_has_its_number_at_the_margin_level_with_it()
    {
        // Word's "#(n)" in each line of an equation array numbers that line, at the right margin.
        var page = Render(new DocxBuilder().Paragraph(P(Display(EqArr(MR("x+y&=10#(1)"), MR("2x&=4#(2)"), MR("x−3y+z&=0#(3)"))),
            "<w:jc w:val=\"center\"/>")))[0];

        Assert.DoesNotContain(page.Items.OfType<GlyphItem>(), i => i.Run.Text.Contains('#'));
        var equals = Runs(page, "=").OrderBy(r => r.Positions[0].Y).ToList();
        Assert.Equal(3, equals.Count);
        // The lines still line up at their marks.
        Assert.True(equals.Max(e => e.Positions[0].X) - equals.Min(e => e.Positions[0].X) < 0.5f);
        for (var i = 0; i < 3; i++)
        {
            var number = Ink(Single(page, $"({i + 1})"));
            var axis = equals[i].Positions[0].Y - equals[i].Size * MathLayout.Axis;
            TestContext.Current.TestOutputHelper?.WriteLine($"line {i + 1}: number middle {number.MidY:0.00} pt, axis {axis:0.00} pt");
            Assert.Equal(axis, number.MidY, 0.5f);
            Assert.InRange(number.Right, RightMargin - 2, RightMargin + 0.5f);
        }
    }

    [Fact]
    public void An_equation_wider_than_the_column_breaks_into_lines_within_the_margins()
    {
        var terms = string.Concat(Enumerable.Range(1, 40).Select(i => MR("+") + $"<m:sSup><m:e>{MR("e")}</m:e><m:sup>{MR("-" + i)}</m:sup></m:sSup>" + MR("x")));
        var page = Render(new DocxBuilder().Paragraph(P(Display(MR("y=") + terms), "<w:jc w:val=\"center\"/>")))[0];

        var glyphs = page.Items.OfType<GlyphItem>().Select(i => i.Run).ToList();
        var baselines = glyphs.Where(r => r.Text == "x").Select(r => MathF.Round(r.Positions[0].Y)).Distinct().ToList();
        Assert.True(baselines.Count >= 2, $"{baselines.Count} lines");
        Assert.All(glyphs.Select(Ink), r => Assert.InRange(r.Right, 72 - 1, 595.3f - 72 + 1));
        Assert.All(glyphs.Select(Ink), r => Assert.True(r.Left >= 72 - 1, $"{r}"));
    }

    // ───────── Without a math font ─────────

    [Fact]
    public void Without_a_math_font_tall_brackets_are_drawn_heavy_in_the_middle_and_small_ones_are_text()
    {
        var fonts = RepositoryFonts.Load();
        Assert.SkipUnless(fonts.Has(RepositoryFonts.Latin), "Noto Sans is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        var math = new MathLayout(new FontResolver(fonts), _shaper);
        Assert.Null(math.MathFont());
        static MathNode Read(string omml) => OmmlReader.Read(System.Xml.Linq.XElement.Parse($"<m:oMath {Namespaces}>{omml}</m:oMath>"));

        // A bracket around a letter is the text font's.
        var small = new PageContent(200, 200);
        math.Layout(Read(Delimited(MR("x"), "(", ")")), 20, SKColors.Black).Paint(small, 10, 100, 1);
        Assert.Contains(small.Items.OfType<GlyphItem>(), i => i.Run.Text == "(");

        // A brace before three lines is drawn: as tall as the lines, thick in the middle of its arms, thin at the tips.
        var page = new PageContent(200, 300);
        math.Layout(Read(Delimited(EqArr(MR("x+y=1"), MR("x−y=2"), MR("2x=3")), "{", "")), 20, SKColors.Black).Paint(page, 10, 150, 1);
        Assert.DoesNotContain(page.Items.OfType<GlyphItem>(), i => i.Run.Text == "{");
        const int Scale = 20;
        using var bitmap = Raster(page, Scale);
        var ink = DrawnInk(page);
        var letters = page.Items.OfType<GlyphItem>().Select(i => Ink(i.Run)).ToList();
        Assert.True(ink.Top <= letters.Min(r => r.Top) + 1 && ink.Bottom >= letters.Max(r => r.Bottom) - 1, $"{ink}");
        // The arm runs up and down: its thickness is across a row. The tip turns right: its thickness is down a column.
        var arm = (float)Enumerable.Range((int)(ink.Left * Scale) - 2, (int)(ink.Width * Scale) + 4)
            .Count(x => bitmap.GetPixel(x, (int)((ink.Top + ink.Height / 4) * Scale)).Alpha > 128) / Scale;
        var tip = (float)Enumerable.Range((int)(ink.Top * Scale) - 2, 3 * Scale)
            .Count(y => bitmap.GetPixel((int)((ink.Right - 0.3f) * Scale), y).Alpha > 128) / Scale;
        Assert.True(arm >= 20 * 0.07f, $"arm {arm}"); // the old line was 0.06 em everywhere
        Assert.True(arm > tip * 1.8f, $"arm {arm}, tip {tip}");
    }

    // ───────── The MATH table ─────────

    [Fact]
    public void A_math_table_with_a_part_that_hardly_grows_stops_at_a_bound()
    {
        // MATH 1.0: constants (214 bytes, all 0), one vertical construction for glyph 5: no variants, an assembly of one
        // extender 1 unit long with no overlap. Stretching it to a million units must not take a million glyphs.
        var table = new List<byte>();
        void U16(int value) => table.AddRange([(byte)(value >> 8), (byte)value]);
        const int Constants = 10, Variants = Constants + 214;
        U16(1);
        U16(0);
        U16(Constants);
        U16(0);
        U16(Variants);
        table.AddRange(new byte[214]);
        // MathVariants: overlap, vertical coverage, horizontal coverage, counts, the construction's offset.
        U16(0);
        U16(12);
        U16(0);
        U16(1);
        U16(0);
        U16(18);
        // Coverage (format 1, glyph 5) at 12.
        U16(1);
        U16(1);
        U16(5);
        // Construction at 18: the assembly 4 bytes on, no variants.
        U16(4);
        U16(0);
        // Assembly at 22: italic correction, one part (glyph 7, connectors 0, advance 1, extender).
        U16(0);
        U16(0);
        U16(1);
        U16(7);
        U16(0);
        U16(0);
        U16(1);
        U16(1);

        var math = MathTable.Parse([.. table], 1000);
        Assert.NotNull(math);
        Assert.True(math.Stretches(5));
        var watch = Stopwatch.StartNew();
        var glyphs = math.Stretch(5, 1_000_000, _ => 1);
        Assert.InRange(glyphs.Count, 1, 300);
        Assert.All(glyphs, g => Assert.Equal(7, g.Glyph));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));

        // Offsets past the end are refused, not read.
        Assert.Throws<InvalidDataException>(() => MathTable.Parse([.. table.Take(Variants + 16)], 1000));
    }

    // ───────── Helpers ─────────

    private void SkipWithoutMathFont() =>
        Assert.SkipUnless(new MathLayout(_system, _shaper).MathFont() is not null,
            "No math font installed (Cambria Math, STIX Two Math, Latin Modern Math …)");

    private List<PageContent> Render(DocxBuilder docx)
    {
        var path = docx.Save(Path.Combine(_folder, $"{Guid.NewGuid():N}.docx"));
        var document = DocxReader.Read(path, Path.Combine(_folder, "media"));
        return new PageComposer(_system, _shaper, styles: StyleResolver.FromTemplate()).Compose(document, TestContext.Current.CancellationToken).Pages;
    }

    private static List<GlyphRun> Runs(PageContent page, string text) =>
        [.. page.Items.OfType<GlyphItem>().Select(i => i.Run).Where(r => r.Text == text)];

    private static GlyphRun Single(PageContent page, string text) => Assert.Single(Runs(page, text));

    private static float Centre(SKRect rect) => (rect.Left + rect.Right) / 2;

    /// <summary>The outlines of a run's glyphs on the page.</summary>
    private static SKRect Ink(GlyphRun run)
    {
        // Measured at 1000 pt and scaled down: Skia rounds glyph bounds out to whole units of the size it measures at.
        const float Large = 1000;
        using var font = new SKFont(run.Font.Typeface, Large) { LinearMetrics = true, Hinting = SKFontHinting.None };
        var widths = new float[run.Glyphs.Length];
        var bounds = new SKRect[run.Glyphs.Length];
        font.GetGlyphWidths(run.Glyphs, widths, bounds);
        var ink = SKRect.Empty;
        for (var i = 0; i < bounds.Length; i++)
        {
            var scale = run.Size / Large;
            var b = new SKRect(bounds[i].Left * scale, bounds[i].Top * scale, bounds[i].Right * scale, bounds[i].Bottom * scale);
            var placed = new SKRect(run.Positions[i].X + b.Left * run.ScaleX, run.Positions[i].Y + b.Top, run.Positions[i].X + b.Right * run.ScaleX, run.Positions[i].Y + b.Bottom);
            ink = ink.IsEmpty ? placed : SKRect.Union(ink, placed);
        }
        return ink;
    }

    /// <summary>The page's drawings (not its glyphs) as pixels, <paramref name="scale"/> per point.</summary>
    private static SKBitmap Raster(PageContent page, int scale)
    {
        var bitmap = new SKBitmap((int)page.Width * scale, (int)page.Height * scale);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        foreach (var drawing in page.Items.OfType<DrawingItem>())
        {
            canvas.Save();
            drawing.Drawing(canvas);
            canvas.Restore();
        }
        return bitmap;
    }

    /// <summary>Where the page's drawings put ink.</summary>
    private static SKRect DrawnInk(PageContent page)
    {
        const int Scale = 10;
        using var bitmap = Raster(page, Scale);
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha < 64)
                    continue;
                (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
            }
        }
        Assert.True(right >= 0, "nothing drawn");
        return new SKRect(left / (float)Scale, top / (float)Scale, (right + 1) / (float)Scale, (bottom + 1) / (float)Scale);
    }
}
