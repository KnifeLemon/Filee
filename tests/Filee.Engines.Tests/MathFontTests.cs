// Word's and TeX's math in the math font with math italic letters, wide display formulas broken into lines before
// their operators (Word's rule), \xrightarrow stretching in Typst, and stacked content as 한글's REL / BUILDREL.
//
// The math font checks use the computer's math font (Cambria Math on Windows, STIX Two Math on macOS) and skip where
// none is installed; the line breaks use the repository's fonts, which have none.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
using SkiaSharp;

namespace Filee.Engines.Tests;

public sealed class MathFontTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    private readonly Shaper _shaper = new();
    private readonly RepositoryFonts _fonts = RepositoryFonts.Load();

    public void Dispose() => _shaper.Dispose();

    // ───────────────────────── Math alphabets ─────────────────────────

    [Theory]
    [InlineData('x', true, false, 0x1D465)] // 𝑥
    [InlineData('A', true, false, 0x1D434)] // 𝐴
    [InlineData('h', true, false, 0x210E)] // ℎ, the gap in the italic alphabet
    [InlineData('h', true, true, 0x1D489)] // 𝒉
    [InlineData('v', false, true, 0x1D42F)] // 𝐯
    [InlineData('2', false, true, 0x1D7D0)] // 𝟐
    [InlineData('2', true, false, '2')] // digits stay upright
    [InlineData('x', false, false, 'x')] // upright (rm) letters stay as they are
    [InlineData('α', true, false, 0x1D6FC)] // 𝛼
    [InlineData('ω', true, false, 0x1D714)] // 𝜔
    [InlineData('ϕ', true, false, 0x1D719)] // 𝜙
    [InlineData('Γ', true, false, 'Γ')] // capital Greek stays upright
    [InlineData('+', true, true, '+')]
    public void Letters_map_to_the_math_alphabets(char letter, bool italic, bool bold, int expected) =>
        Assert.Equal(expected, MathLayout.Alphabet(letter, italic, bold));

    [Fact]
    public void Word_and_tex_math_is_set_in_the_math_font_with_math_italic_letters()
    {
        var math = SystemMath();
        var font = math.MathFont()!.Value.Font;
        var runs = Runs(math, @"x+\sin y=2\alpha+\Gamma+\mathbf{v}+\text{if}", MathMode.Display);

        string Italic(char c) => char.ConvertFromUtf32(MathLayout.Alphabet(c, italic: true, bold: false));
        GlyphRun With(string text) => Assert.Single(runs, r => r.Text == text);
        // Variables and lowercase Greek letters in math italic, function names, digits and capital Greek upright, all
        // in the math font; bold letters in math bold; \text in the text font.
        Assert.Same(font, With(Italic('x')).Font);
        Assert.Same(font, With(Italic('y')).Font);
        Assert.Same(font, With(Italic('α')).Font);
        Assert.Same(font, With("sin").Font);
        Assert.Same(font, With("2").Font);
        Assert.Same(font, With("Γ").Font);
        Assert.Same(font, With(char.ConvertFromUtf32(0x1D42F)).Font);
        Assert.NotSame(font, With("if").Font);
        Assert.All(runs, r => Assert.Equal(0, r.SkewX)); // the alphabet leans, not a slant

        // 한글's equations keep their own fonts.
        Assert.DoesNotContain(Runs(math, "x+y", MathMode.Fixed), r => r.Text.Contains(Italic('x'), StringComparison.Ordinal));
    }

    [Fact]
    public void A_display_sum_is_the_math_font_s_larger_variant()
    {
        var math = SystemMath();
        var tree = LatexMath.Parse(@"\sum_{i=1}^{n} i")!;
        var display = Assert.Single(Runs(math, tree, MathMode.Display), r => r.Text == "∑");
        var inline = Assert.Single(Runs(math, tree, MathMode.Text), r => r.Text == "∑");
        Assert.NotEqual(inline.Glyphs[0], display.Glyphs[0]);
        Assert.Equal(inline.Size, display.Size); // a variant glyph, not a scaled one
    }

    [Fact]
    public void Relations_and_binary_operators_are_spaced_as_in_word()
    {
        var math = SystemMath();
        float Width(MathNode node) => math.Layout(node, 18, SKColors.Black, MathMode.Display).Width;
        var (a, b) = (Width(new MathText("a")), Width(new MathText("b")));
        // A thick space (5/18 em) on each side of a relation, a medium one (4/18 em) around a binary operator; the
        // glyphs measured as plain text, which isn't spaced.
        Assert.Equal(10, Width(LatexMath.Parse("a=b")!) - a - b - Width(new MathText("=")), 0.05);
        Assert.Equal(8, Width(LatexMath.Parse("a+b")!) - a - b - Width(new MathText("+")), 0.05);
        // Not in a script.
        var script = math.Layout(LatexMath.Parse("x_{a+b}")!, 18, SKColors.Black, MathMode.Display).Width;
        var plain = math.Layout(new MathScripts(new MathText("x"), new MathRow([new MathText("a"), new MathText("+"), new MathText("b")]), null), 18, SKColors.Black,
            MathMode.Display).Width;
        Assert.Equal(plain, script, 0.05);
    }

    // ───────────────────────── Line breaks ─────────────────────────

    private const string Wide = "(a+b)^7=a^7+7a^6b+21a^5b^2+35a^4b^3+35a^3b^4+21a^2b^5+7ab^6+b^7";

    [Fact]
    public void A_wide_display_formula_breaks_before_its_operators_and_indents_the_rest()
    {
        var math = RepositoryMath();
        var tree = LatexMath.Parse(Wide)!;
        var whole = math.Layout(tree, 12, SKColors.Black, MathMode.Display);
        const float Width = 200;
        Assert.True(whole.Width > Width * 1.5f, $"{whole.Width}");

        var broken = math.Layout(tree, 12, SKColors.Black, MathMode.Display, Width);
        Assert.True(broken.Width <= Width, $"{broken.Width}");
        Assert.True(broken.Height > whole.Height * 1.8f, $"{broken.Height} vs {whole.Height}");
        var page = new PageContent(400, 400);
        broken.Paint(page, 0, 100, 1);
        var runs = page.Items.OfType<GlyphItem>().Select(i => i.Run).ToList();
        var lines = runs.GroupBy(r => MathF.Round(r.Positions[0].Y)).Where(g => g.Any(r => r.Size == 12)).OrderBy(g => g.Key).ToList();
        Assert.True(lines.Count >= 3, $"{lines.Count} lines");
        foreach (var line in lines.Skip(1))
        {
            // Each line after the first starts with the operator it broke before, an inch (a third of the width) in.
            var first = line.Where(r => r.Size == 12).MinBy(r => r.Positions[0].X)!;
            Assert.Equal("+", first.Text[..1]);
            Assert.Equal(Width / 3, first.Positions[0].X, 0.5);
        }

        // Inline formulas and 한글's equations stay on one line; so does one that fits.
        Assert.Equal(whole.Height, math.Layout(tree, 12, SKColors.Black, MathMode.Fixed, Width).Height, 0.01);
        var inline = math.Layout(tree, 12, SKColors.Black, MathMode.Text);
        Assert.Equal(inline.Height, math.Layout(tree, 12, SKColors.Black, MathMode.Text, Width).Height, 0.01);
        Assert.Equal(whole.Height, math.Layout(tree, 12, SKColors.Black, MathMode.Display, whole.Width + 1).Height, 0.01);
    }

    [Fact]
    public void A_sign_is_not_a_place_to_break()
    {
        var items = ((MathRow)LatexMath.Parse("a=-b+(-c)")!).Items;
        var breaks = Enumerable.Range(1, items.Count - 1).Where(i => MathLayout.BreaksBefore(items[i], items[i - 1])).Select(i => items[i]).ToList();
        Assert.Equal(["=", "+"], breaks.Select(b => ((MathSymbol)b).Text));
        // Relations and binary operators written as commands break too; a slash doesn't.
        Assert.True(MathLayout.BreaksBefore(Atom(@"\le"), new MathText("a")));
        Assert.True(MathLayout.BreaksBefore(Atom(@"\times"), new MathText("a")));
        Assert.False(MathLayout.BreaksBefore(new MathSymbol("/"), new MathText("a")));
    }

    [Fact]
    public void Word_s_reader_breaks_before_relations_and_not_after_them()
    {
        // DocxReader's lines (for 한글 and rhwp): before ≤ too, and "= −x" keeps its sign with the =.
        var tree = LatexMath.Parse(@"x_1+x_2+x_3+x_4=-y_1-y_2\le z")!;
        var width = EquationScript.Measure(tree, 1000).Width;
        var lines = EquationScript.BreakLines(tree, width / 3, 1000);
        Assert.True(lines.Count >= 3, $"{lines.Count} lines");
        Assert.All(lines.Skip(1), line =>
        {
            var first = ((MathRow)line).Items[0];
            Assert.True(first is MathSymbol { Text: "+" or "-" or "=" } or MathCommand { Glyph: "≤" }, $"{first}");
        });
        Assert.DoesNotContain(lines, line => ((MathRow)line).Items[^1] is MathSymbol { Text: "=" });
    }

    [Fact]
    public void A_wide_tex_display_formula_breaks_on_the_page()
    {
        Assert.SkipUnless(_fonts.Has(RepositoryFonts.Latin), "Noto Sans is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        var markdown = $"Before.\n\n$${Wide}+{Wide.Replace("a", "c", StringComparison.Ordinal)}$$\n\nAfter.\n";
        var document = MarkdownReader.Read(markdown, fx.NewFolder());
        var page = new PageComposer(new FontResolver(_fonts), _shaper, styles: StyleResolver.FromTemplate())
            .Compose(document, TestContext.Current.CancellationToken).Pages[0];

        var runs = page.Items.OfType<GlyphItem>().Select(i => i.Run).ToList();
        var left = runs.Single(r => r.Text.StartsWith("Before", StringComparison.Ordinal)).Positions[0].X;
        // Nothing runs past the margins (the text's left margin mirrored on the right), and the formula takes lines.
        Assert.All(runs, r => Assert.True(r.Positions[^1].X < page.Width - left, $"{r.Text} at {r.Positions[^1].X}"));
        var math = runs.Where(r => r.Text is "+" or "=").Select(r => MathF.Round(r.Positions[0].Y)).Distinct().Count();
        Assert.True(math >= 2, $"{math} lines of math");
    }

    // ───────────────────────── Writers ─────────────────────────

    [Theory]
    [InlineData(@"A\xrightarrow[g]{f}B", "A stretch(→)_(g)^(f) B")]
    [InlineData(@"\xLeftarrow{\text{long}}", "stretch(⇐)^(\"long\")")]
    [InlineData(@"a\overset{n}{=}b", "a limits(=)^(n) b")]
    public void Typst_stretches_an_arrow_to_its_text(string tex, string typst) =>
        Assert.Equal(typst, TypstMathWriter.Write(LatexMath.Parse(tex)!));

    [Theory]
    [InlineData(@"A\xrightarrow[g]{f}B", "A REL rarrow {f} {g} B")]
    [InlineData(@"A\xrightarrow{f}B", "A BUILDREL rarrow {f} B")]
    [InlineData(@"a\overset{d e f}{=}b", "a BUILDREL = {d e f} b")]
    [InlineData(@"\underset{n}{\Rightarrow}", "REL RARROW {} {n}")]
    [InlineData(@"\overset{a}{X}", "X ^{a}")]
    public void Hangul_stacks_over_a_relation_with_rel_and_buildrel(string tex, string script)
    {
        var tree = LatexMath.Parse(tex)!;
        Assert.Equal(script, EquationScript.Script(tree));
        // Read back, it is the same stack.
        Assert.Equal(EquationScript.Script(tree), EquationScript.Script(EquationParser.Parse(script)));
    }

    [Fact]
    public void Rel_is_read_as_a_stack_and_measured_as_rhwp_lays_it_out()
    {
        var stack = Assert.IsType<MathStack>(Atom(EquationParser.Parse("REL -> {abc} {d}")));
        Assert.Equal("→", Assert.IsType<MathCommand>(stack.Base).Glyph);
        Assert.NotNull(stack.Over);
        Assert.NotNull(stack.Under);
        // As wide as the text over it (at 0.7), taller than the arrow by both rows of text.
        var box = EquationScript.Measure(stack, 1000);
        var over = EquationScript.Measure(stack.Over, 700);
        var arrow = EquationScript.Measure(stack.Base, 1000);
        Assert.Equal(Math.Max(over.Width, arrow.Width), box.Width, 0.01);
        Assert.True(box.Height > arrow.Height + over.Height, $"{box.Height}");
        Assert.True(box.Baseline > over.Height, $"{box.Baseline}");
    }

    // ───────────────────────── Helpers ─────────────────────────

    private MathLayout SystemMath()
    {
        var math = new MathLayout(new FontResolver(), _shaper);
        Assert.SkipUnless(math.MathFont() is not null, "No math font installed (Cambria Math, STIX Two Math, Latin Modern Math …)");
        return math;
    }

    private MathLayout RepositoryMath()
    {
        Assert.SkipUnless(_fonts.Has(RepositoryFonts.Latin), "Noto Sans is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        return new MathLayout(new FontResolver(_fonts), _shaper);
    }

    private static MathNode Atom(string tex) => Atom(LatexMath.Parse(tex)!);

    private static MathNode Atom(MathNode node) => node is MathRow { Items: [var only] } ? Atom(only) : node;

    private static List<GlyphRun> Runs(MathLayout math, string tex, MathMode mode) => Runs(math, LatexMath.Parse(tex)!, mode);

    private static List<GlyphRun> Runs(MathLayout math, MathNode tree, MathMode mode)
    {
        var page = new PageContent(600, 200);
        math.Layout(tree, 20, SKColors.Black, mode).Paint(page, 10, 100, 1);
        return [.. page.Items.OfType<GlyphItem>().Select(i => i.Run)];
    }
}
