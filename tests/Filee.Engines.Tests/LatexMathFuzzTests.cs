// Mutation fuzzing of TeX math in Markdown (#1652): Markdown with $ … $, $$ … $$, \( … \) and \[ … \] read by
// MarkdownReader (MarkdownMath finds the math, LatexMath reads it), the equations written as Word math, MathML and a 한글
// script, laid out and drawn. Every input must be read or refused (ConversionRefusedException) in time and on the 1 MB
// stack FuzzHarness gives it. FILEE_FUZZ_SECONDS runs longer with a new random seed, FILEE_FUZZ_SEED repeats a run.

using System.Text;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class LatexMathFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const int QuickIterations = 50;

    /// <summary>Each kind of structure LatexMath reads, inline and display.</summary>
    private const string Seed = """
        Inline $x^2_i + \frac{a}{b} - \sqrt[3]{y'}$ and \(\alpha \le \beta\), not $5 or `$x$`.

        $$
        \sum_{k=1}^{n} k = \frac{n(n+1)}{2}, \quad \int_0^1 f(x)\,dx, \quad \lim_{x \to 0} \frac{\sin x}{x}
        $$

        \[ \left( \begin{pmatrix} a & b \\ c & d \end{pmatrix} \right\} \begin{cases} x & \text{if } x > 0 \\ -x & \text{else} \end{cases} \]

        $$\begin{aligned} f(x) &= \hat{\mathbf{v}} \cdot \vec{w} \\ &\neq \overline{AB} \not\in \mathbb{R} \end{aligned} \tag{1}$$

        $$\binom{n}{k} = {n \choose k}, \operatorname{arg\,max}_x \boldsymbol{\mu}, \foo{x} \mathcal{L} \big( \Big]$$
        """;

    [Fact]
    public void Markdown_math_reads_writes_and_lays_out_every_mutated_input() =>
        new FuzzHarness(fx, QuickIterations, 20261119).Files("latex", "md", [Encoding.UTF8.GetBytes(Seed)],
            FuzzHarness.Text(["$", "$$", "\\(", "\\)", "\\[", "\\]", "{", "}", "^", "_", "&", "\\\\", "'", "`", "\\frac", "\\sqrt",
                "\\left(", "\\right)", "\\middle|", "\\begin{matrix}", "\\end{matrix}", "\\begin{cases}", "\\begin{aligned}", "\\end{aligned}",
                "\\text{", "\\mathbf", "\\rm ", "\\over ", "\\not", "\\sum", "\\lim", "\\hat", "\\mathbb{", "\\tag{", "[", "]", "%", "\\"]),
            (path, _) =>
            {
                var document = MarkdownReader.Read(File.ReadAllText(path), Path.GetDirectoryName(path)!);
                using var shaper = new Typesetting.Shaper();
                var layout = new Typesetting.MathLayout(new Typesetting.FontResolver(), shaper);
                foreach (var equation in HDocumentWalker.Inlines(document.Sections.SelectMany(s => s.Blocks)).OfType<HEquation>())
                {
                    EquationScript.Script(equation.Math);
                    MathMLWriter.Write(equation.Math, equation.Display, equation.Tex);
                    OmmlWriter.Write(equation.Math, equation.Display);
                    layout.Layout(equation.Math, 10, SkiaSharp.SKColors.Black).Paint(new Rendering.PageContent(200, 200), 0, 0, 1);
                }
            });
}
