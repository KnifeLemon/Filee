// The equation tree written as TeX (TexMathWriter: LaTeX, Markdown, AsciiDoc) and as Typst math (TypstMathWriter):
// TeX read into the tree, written back and read again gives the same tree; Word math (OMML) and 한글 equation scripts,
// which come as a tree, become TeX that Markdown reads back as the same equations; and a tree nested past the limit is
// left to the equation's text, quickly.

using System.Diagnostics;
using System.Xml.Linq;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;

namespace Filee.Engines.Tests;

public class TexMathWriterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";

    [Theory]
    [InlineData(@"\frac{a+b}{2c}")]
    [InlineData(@"x_i^2 + y^{n+1}")]
    [InlineData(@"\sqrt[3]{x} + \sqrt{x^2+1}")]
    [InlineData(@"\sum_{i=1}^{n} i^2")]
    [InlineData(@"\int_0^\infty e^{-x}\,dx")]
    [InlineData(@"\lim_{x \to 0} \frac{\sin x}{x}")]
    [InlineData(@"\begin{pmatrix} a & b \\ c & d \end{pmatrix}")]
    [InlineData(@"\begin{vmatrix} a & b \\ c & d \end{vmatrix} + \begin{bmatrix} 1 \\ 2 \end{bmatrix}")]
    [InlineData(@"|x| = \begin{cases} x & x \ge 0 \\ -x & \text{otherwise} \end{cases}")]
    [InlineData(@"\left( \frac{a}{b} \right) + \left\{ x \right. + \left\langle y \right\rangle")]
    [InlineData(@"\hat{x} + \overline{AB} + \vec{v} + \tilde{y} + \underline{z}")]
    [InlineData(@"\mathbf{v} \cdot \mathrm{d}x + \boldsymbol{\alpha}")]
    [InlineData(@"\alpha \leq \beta \neq \gamma \to \infty")]
    [InlineData(@"\binom{n}{k}")]
    [InlineData(@"\begin{aligned} a &= b \\ c &= d \end{aligned}")]
    [InlineData(@"f'(x) + f''(y)")]
    [InlineData(@"\{x\} \cup \emptyset")]
    [InlineData(@"x \in \mathbb{R} \subseteq \mathcal{L}")]
    [InlineData(@"\operatorname{lcm}(a, b) + \max_{i} x_i")]
    [InlineData(@"a \quad b \qquad c \, d \; e")]
    [InlineData(@"\not\in \not= \pm \times \div")]
    [InlineData(@"\Gamma \Delta \Omega \omega")]
    public void Tex_written_from_the_tree_reads_back_as_the_same_tree(string tex)
    {
        var tree = LatexMath.Parse(tex)!;
        var written = TexMathWriter.Write(tree);
        Assert.NotNull(written);
        var again = LatexMath.Parse(written);
        Assert.NotNull(again);
        Assert.Equal(EquationScript.Script(tree), EquationScript.Script(again));
    }

    [Fact]
    public void Characters_tex_treats_specially_are_escaped()
    {
        var tree = new MathRow([
            new MathQuoted("50% & #1 {a} $x_y ~^\\"),
            new MathText("한"),
            new MathCommand("Alpha", "Α"),
            new MathSymbol("{"),
            new MathSymbol("#"),
        ]);
        var tex = TexMathWriter.Write(tree)!;
        Assert.Contains(@"\text{50\% \& \#1 \{a\} \$x\_y \textasciitilde{}\textasciicircum{}\textbackslash{}}", tex, StringComparison.Ordinal);
        Assert.Contains(@"\text{한}", tex, StringComparison.Ordinal);
        Assert.Contains("A", tex, StringComparison.Ordinal);
        Assert.DoesNotContain("Α", tex, StringComparison.Ordinal); // Greek Alpha has no command: the Latin letter
        Assert.Contains(@"\{\#", tex, StringComparison.Ordinal);
    }

    [Fact]
    public void A_control_word_is_kept_apart_from_a_letter_after_it()
    {
        var tex = TexMathWriter.Write(new MathRow([new MathCommand("alpha", "α"), new MathText("x"), new MathFunction("sin"), new MathText("y")]))!;
        Assert.Equal(@"\alpha x\sin y", tex);
    }

    [Fact]
    public void Word_math_becomes_tex()
    {
        // (a + b) / 2 under a square root, with x² beside it.
        var oMath = new XElement(M + "oMath",
            new XElement(M + "rad", new XElement(M + "radPr", new XElement(M + "degHide", new XAttribute(M + "val", "1"))), new XElement(M + "deg"),
                new XElement(M + "e",
                    new XElement(M + "f",
                        new XElement(M + "num", Run("a+b")),
                        new XElement(M + "den", Run("2"))))),
            new XElement(M + "sSup", new XElement(M + "e", Run("x")), new XElement(M + "sup", Run("2"))));
        var tex = TexMathWriter.Write(OmmlReader.Read(oMath))!;
        Assert.Equal(@"\sqrt{\frac{a+b}{2}}x^{2}", tex);

        static XElement Run(string text) => new(M + "r", new XElement(M + "t", text));
    }

    [Fact]
    public void A_hangul_equation_becomes_tex()
    {
        var tex = TexMathWriter.Write(EquationParser.Parse("{a} over {b} + sqrt {x} + SUM _{i=1} ^{n} i + alpha LEQ beta"))!;
        Assert.Equal(@"\frac{a}{b}+\sqrt{x}+\sum_{i=1}^{n}i+\alpha\leq\beta", tex);
    }

    [Theory]
    [InlineData(@"\frac{a}{b}", "frac(a, b)")]
    [InlineData(@"x_i^2", "x_(i)^(2)")]
    [InlineData(@"\sqrt[3]{x} + \sqrt{y}", "root(3, x) + sqrt(y)")]
    [InlineData(@"\sum_{i=1}^{n} i", "∑_(i = 1)^(n) i")]
    [InlineData(@"\lim_{x \to 0} \sin x", "lim_(x → 0) op(\"sin\") x")]
    [InlineData(@"\begin{pmatrix} a & b \\ c & d \end{pmatrix}", "mat(delim: \"(\", a, b; c, d)")]
    [InlineData(@"\begin{cases} 1 & x > 0 \\ 0 & \text{else} \end{cases}", "cases(1 & x > 0, 0 & \"else\")")]
    [InlineData(@"\left| x \right| + \left( a \right)", "abs(x) + (a)")]
    [InlineData(@"\hat{x} + \vec{v} + \overline{AB}", "hat(x) + arrow(v) + overline(A B)")]
    [InlineData(@"\mathbf{v} + \mathrm{d}", "bold(upright(v)) + upright(d)")]
    [InlineData(@"\binom{n}{k}", "binom(n, k)")]
    [InlineData(@"f'(x)", "f' \\( x \\)")]
    [InlineData(@"ab", "a b")]
    [InlineData(@"\frac{f(a,b)}{2}", "frac(f \\( a \\, b \\), 2)")]
    [InlineData(@"a \quad b \, c", "a quad b thin c")]
    public void Tex_becomes_typst_math(string tex, string typst) =>
        Assert.Equal(typst, TypstMathWriter.Write(LatexMath.Parse(tex)!));

    [Fact]
    public void Aligned_lines_are_typst_lines_with_their_marks()
    {
        var typst = TypstMathWriter.Write(LatexMath.Parse(@"\begin{aligned} a &= b \\ c &= d \end{aligned}")!);
        Assert.Equal("a & = b \\\nc & = d", typst);
        // Inside something else, lines are a matrix.
        Assert.StartsWith("(mat(delim: #none", TypstMathWriter.Write(LatexMath.Parse(@"\left( \begin{gathered} a \\ b \end{gathered} \right)")!), StringComparison.Ordinal);
    }

    [Fact]
    public void Typst_escapes_its_syntax_and_quotes_text()
    {
        var typst = TypstMathWriter.Write(new MathRow([new MathQuoted("say \"hi\" \\ now"), new MathText("한글"), new MathSymbol("#"), new MathSymbol("$"), new MathSymbol("/")]))!;
        Assert.Equal("\"say \\\"hi\\\" \\\\ now\" \"한글\" \\# \\$ \\/", typst);
    }

    // ───────────────────────── Markdown ─────────────────────────

    private static HDocument Document(params HParagraph[] paragraphs)
    {
        var document = new HDocument();
        var section = new HSection();
        section.Blocks.AddRange(paragraphs);
        document.Sections.Add(section);
        return document;
    }

    [Fact]
    public void Markdown_writes_word_and_hangul_equations_as_tex_that_it_reads_back()
    {
        var inline = new HParagraph();
        var fraction = new MathFraction(new MathRow([new MathText("a"), new MathSymbol("+"), new MathText("b")]), new MathNumber("2"));
        inline.Inlines.AddRange([new HText("Half: ", default), new HEquation(fraction, default), new HText(" done.", default)]);
        // Word's display equation: alone in a centred paragraph.
        var display = new HParagraph { Format = new HParaFormat(Align: HAlign.Center) };
        var sum = EquationParser.Parse("SUM _{i=1} ^{n} i ^{2}");
        display.Inlines.Add(new HEquation(sum, default));

        var markdown = MarkdownWriter.Write(Document(inline, display), _ => null);
        Assert.Contains(@"Half: $\frac{a+b}{2}$ done.", markdown, StringComparison.Ordinal);
        Assert.Contains(@"$$\sum_{i=1}^{n}i^{2}$$", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("(a+b)/2", markdown, StringComparison.Ordinal);

        var read = MarkdownReader.Read(markdown, fx.NewFolder());
        var equations = HDocumentWalker.Inlines(read.Sections.SelectMany(s => s.Blocks)).OfType<HEquation>().ToList();
        Assert.Equal(2, equations.Count);
        Assert.Equal(EquationScript.Script(fraction), EquationScript.Script(equations[0].Math));
        Assert.Equal(EquationScript.Script(sum), EquationScript.Script(equations[1].Math));
        var paragraphs = read.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().ToList();
        Assert.Equal(HAlign.Center, paragraphs.Single(p => p.Inlines.Contains(equations[1])).Format.Align);
    }

    [Fact]
    public void Markdown_keeps_tex_it_read_as_written()
    {
        var read = MarkdownReader.Read("Area $\\pi r^2$ here.\n", fx.NewFolder());
        Assert.Contains("$\\pi r^2$", MarkdownWriter.Write(read, _ => null), StringComparison.Ordinal);
    }

    // ───────────────────────── Depth ─────────────────────────

    [Fact]
    public void A_tree_nested_thousands_deep_is_left_to_its_text_in_time()
    {
        MathNode node = new MathText("x");
        for (var i = 0; i < 20_000; i++)
            node = new MathFraction(node, new MathNumber("2"));
        var watch = Stopwatch.StartNew();
        Assert.Null(TexMathWriter.Write(node));
        Assert.Null(TypstMathWriter.Write(node));
        // The writers then write the equation's text (here its given source).
        var paragraph = new HParagraph();
        paragraph.Inlines.Add(new HEquation(node, default, "deep equation"));
        Assert.Contains("deep equation", MarkdownWriter.Write(Document(paragraph), _ => null), StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"{watch.Elapsed}");
    }
}
