// Math as Word and TeX set it (#356, #1652): Word's bold and spacing read from OMML, stacked content (\overset,
// \xrightarrow, Word's m:limUpp) through the readers and writers, inline formulas set in TeX's text style with Word's
// matrix row spacing, and an equation centred with tabs written as display math by the markup writers.

using System.Xml.Linq;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Hwp.Hwpx.Markup;
using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
using SkiaSharp;

namespace Filee.Engines.Tests;

public sealed class MathPolishTests : IDisposable
{
    private readonly RepositoryFonts _fonts = RepositoryFonts.Load();
    private readonly Shaper _shaper = new();

    public void Dispose() => _shaper.Dispose();

    private static MathNode Omml(string content) => OmmlReader.Read(XElement.Parse($"<m:oMath {DocxBuilder.Namespaces}>{content}</m:oMath>"));

    private static string Run(string text, string sty = "") =>
        $"<m:r>{(sty.Length > 0 ? $"<m:rPr><m:sty m:val=\"{sty}\"/></m:rPr>" : "")}<m:t xml:space=\"preserve\">{text}</m:t></m:r>";

    // ───────────────────────── Reading Word math ─────────────────────────

    [Fact]
    public void Bold_is_upright_and_bold_italic_leans()
    {
        Assert.Equal("\\mathbf{x}", TexMathWriter.Write(Omml(Run("x", "b"))));
        Assert.Equal("\\boldsymbol{x}", TexMathWriter.Write(Omml(Run("x", "bi"))));
        // Bold digits are bold too; plain digits stay as they are.
        Assert.Equal("\\mathbf{0}", TexMathWriter.Write(Omml(Run("0", "b"))));
        Assert.Equal("0", TexMathWriter.Write(Omml(Run("0", "p"))));
    }

    [Fact]
    public void Spaces_of_a_set_width_are_kept_and_typed_spaces_are_not()
    {
        // An em space (Word's \quad), four quarter-em spaces (what OmmlWriter writes for \quad) and a thin space.
        Assert.Equal([2.0], Spaces(Omml(Run("a\u2003b"))));
        Assert.Equal([2.0], Spaces(Omml(Run("a\u2005\u2005\u2005\u2005b"))));
        Assert.Equal([0.333], Spaces(Omml(Run("a\u2009b"))));
        Assert.Empty(Spaces(Omml(Run("a b"))));
    }

    [Theory]
    [InlineData("\\mathbf{v}\\quad w")]
    [InlineData("a\\,b")]
    [InlineData("\\boldsymbol{x}=1")]
    public void Tex_through_word_math_comes_back_the_same(string tex)
    {
        var tree = LatexMath.Parse(tex)!;
        var omml = XElement.Parse($"<w:p {DocxBuilder.Namespaces}>{OmmlWriter.Write(tree, display: false)}</w:p>").Elements().Single();
        Assert.Equal(TexMathWriter.Write(tree), TexMathWriter.Write(OmmlReader.Read(omml)));
    }

    private static List<double> Spaces(MathNode node) => node switch
    {
        MathSpace space => [System.Math.Round(space.Width, 3)],
        MathRow row => [.. row.Items.SelectMany(Spaces)],
        MathStyled styled => Spaces(styled.Body),
        _ => [],
    };

    // ───────────────────────── Stacked content ─────────────────────────

    [Theory]
    [InlineData("\\overset{a}{\\underset{b}{X}}", "\\overset{a}{\\underset{b}{X}}", "limits(X)_(b)^(a)")]
    [InlineData("a\\overset{n}{=}b", "a\\overset{n}{=}b", "a limits(=)^(n) b")]
    [InlineData("\\underset{x\\in A}{\\max}f", "\\underset{x\\in A}{\\max}f", null)]
    [InlineData("A\\xrightarrow[g]{f}B", "A\\xrightarrow[{g}]{f}B", "A limits(→)_(g)^(f) B")]
    [InlineData("A\\xleftarrow{f}B", "A\\xleftarrow{f}B", null)]
    public void Stacked_content_is_written_stacked(string tex, string written, string? typst)
    {
        var tree = LatexMath.Parse(tex)!;
        Assert.Contains(Nodes(tree), n => n is MathStack);
        Assert.Equal(written, TexMathWriter.Write(tree));
        if (typst is not null)
            Assert.Equal(typst, TypstMathWriter.Write(tree));
        // Word keeps it as limits over and under; read back, it is the same stack.
        var omml = XElement.Parse($"<w:p {DocxBuilder.Namespaces}>{OmmlWriter.Write(tree, display: false)}</w:p>").Elements().Single();
        Assert.Equal(written, TexMathWriter.Write(OmmlReader.Read(omml)));
        // 한글 and the linear text, which can't stack, get scripts.
        Assert.DoesNotContain("overset", EquationScript.Script(tree), StringComparison.Ordinal);
    }

    [Fact]
    public void Word_upper_and_lower_limits_stack_and_a_sum_takes_them_as_its_limits()
    {
        var stack = Assert.IsType<MathStack>(Single(Omml($"<m:limUpp><m:e><m:limLow><m:e>{Run("→")}</m:e><m:lim>{Run("b")}</m:lim></m:limLow></m:e><m:lim>{Run("a")}</m:lim></m:limUpp>")));
        Assert.NotNull(stack.Over);
        Assert.NotNull(stack.Under);
        var sum = Assert.IsType<MathBigOperator>(Single(Omml($"<m:limLow><m:e>{Run("∑")}</m:e><m:lim>{Run("i")}</m:lim></m:limLow>")));
        Assert.NotNull(sum.Lower);
    }

    private static MathNode Single(MathNode node) => node is MathRow { Items: [var only] } ? Single(only) : node;

    private static IEnumerable<MathNode> Nodes(MathNode node) => node switch
    {
        MathRow row => [row, .. row.Items.SelectMany(Nodes)],
        MathStack stack => [stack, .. Nodes(stack.Base)],
        _ => [node],
    };

    // ───────────────────────── Layout ─────────────────────────

    private MathLayout Math()
    {
        Assert.SkipUnless(_fonts.Has(RepositoryFonts.Latin), "Noto Sans is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        return new MathLayout(new FontResolver(_fonts), _shaper);
    }

    [Fact]
    public void Inline_formulas_set_fractions_and_sums_smaller()
    {
        var math = Math();
        var fraction = LatexMath.Parse("\\frac{a}{b}")!;
        var sum = LatexMath.Parse("\\sum_{i=1}^{n} i")!;
        var display = math.Layout(fraction, 12, SKColors.Black, MathMode.Display);
        var inline = math.Layout(fraction, 12, SKColors.Black, MathMode.Text);
        Assert.True(inline.Height < display.Height * 0.85, $"{inline.Height} vs {display.Height}");
        // 한글's equations keep one size.
        Assert.Equal(display.Height, math.Layout(fraction, 12, SKColors.Black).Height, 0.01);
        // Inline, the limits go to the side: lower and wider than set above and below.
        var inlineSum = math.Layout(sum, 12, SKColors.Black, MathMode.Text);
        var displaySum = math.Layout(sum, 12, SKColors.Black, MathMode.Display);
        Assert.True(inlineSum.Height < displaySum.Height * 0.8, $"{inlineSum.Height} vs {displaySum.Height}");
    }

    [Fact]
    public void Word_matrix_rows_are_closer_than_hangul_ones_and_even()
    {
        var math = Math();
        var plain = new MathMatrix("matrix", [[new MathText("E")], [new MathNumber("0")]]);
        // #356: two rows of letters 1.16 em apart in Word (1 em of text and the gap); 한글's keep a quarter em between.
        Assert.Equal(12 * 2.16, math.Layout(plain, 12, SKColors.Black, MathMode.Display).Height, 0.1);
        Assert.Equal(12 * 2.25, math.Layout(plain, 12, SKColors.Black).Height, 0.1);
        // Rows of a Word matrix are as tall as the tallest: a row with a fraction makes the plain row as tall.
        var mixed = new MathMatrix("matrix", [[new MathText("E")], [new MathFraction(new MathText("a"), new MathText("b"))], [new MathText("F")]]);
        var lines = math.Layout(mixed, 12, SKColors.Black, MathMode.Display);
        var tall = math.Layout(new MathFraction(new MathText("a"), new MathText("b")), 12, SKColors.Black, MathMode.Display);
        Assert.Equal(3 * tall.Height + 2 * 12 * 0.16, lines.Height, 0.1);
    }

    [Fact]
    public void An_arrow_grows_under_its_text()
    {
        var math = Math();
        var shortArrow = math.Layout(LatexMath.Parse("\\xrightarrow{f}")!, 12, SKColors.Black, MathMode.Display);
        var longArrow = math.Layout(LatexMath.Parse("\\xrightarrow{\\text{a long name}}")!, 12, SKColors.Black, MathMode.Display);
        Assert.True(longArrow.Width > shortArrow.Width * 2, $"{longArrow.Width} vs {shortArrow.Width}");
        // The text is above the arrow, the arrow on the axis.
        Assert.True(longArrow.Ascent > 12 * 0.6f);
    }

    // ───────────────────────── Markup writers ─────────────────────────

    [Fact]
    public void An_equation_centred_with_tabs_is_display_math()
    {
        var tree = LatexMath.Parse("x^2")!;
        var paragraph = new HParagraph();
        paragraph.Inlines.AddRange([new HTab(default), new HEquation(tree, default), new HTab(default), new HText(" ", default)]);
        var document = new HDocument();
        var section = new HSection();
        section.Blocks.Add(paragraph);
        document.Sections.Add(section);

        var tex = TexMathWriter.Write(tree);
        Assert.Contains($"\\[\n{tex}\n\\]", new LatexWriter(_ => null).Write(document), StringComparison.Ordinal);
        Assert.Contains($"[latexmath]\n++++\n{tex}\n++++", new AsciiDocWriter(_ => null).Write(document), StringComparison.Ordinal);
        Assert.Contains($"$ {TypstMathWriter.Write(tree)} $", new TypstWriter(_ => null).Write(document), StringComparison.Ordinal);

        // With text beside it, it stays inline.
        paragraph.Inlines.Add(new HText("is the square", default));
        Assert.Contains($"${tex}$", new LatexWriter(_ => null).Write(document), StringComparison.Ordinal);
    }
}
