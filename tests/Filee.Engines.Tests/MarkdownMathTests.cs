// TeX math in Markdown (#1652): where $ … $, $$ … $$, \( … \) and \[ … \] start and end (MarkdownMath), the TeX read into
// the equation tree (LatexMath, checked as the 한글 script EquationScript writes for it), and the equations in each
// writer: Word math in DOCX (read back by DocxReader), MathML in HTML and EPUB, 한글 equations in HWPX, typeset on the
// page, the source kept where a writer has no equations. Hostile formulas (deep nesting, huge ones, a flood of them)
// stay text or are refused, quickly.

using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;

namespace Filee.Engines.Tests;

public sealed partial class MarkdownMathTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    // ───────────────────────── TeX ─────────────────────────

    [Theory]
    [InlineData(@"\frac{a+b}{2}", "{{a + b} over {2}}")]
    [InlineData(@"x^2", "x ^{2}")]
    [InlineData(@"x_i^2", "x _{i} ^{2}")]
    [InlineData(@"x^23", "x ^{2} 3")] // a script without braces is one token
    [InlineData(@"\sqrt[3]{x}", "root {3} of {x}")]
    [InlineData(@"\sqrt{x^2+1}", "sqrt {x ^{2} + 1}")]
    [InlineData(@"\alpha + \beta \times \Gamma", "alpha + beta TIMES Gamma")]
    [InlineData(@"\sum_{i=1}^{n} i", "SUM _{i = 1} ^{n} i")]
    [InlineData(@"\int_0^1 f(x)\,dx", "INT _{0} ^{1} f ( x ) ~ d x")]
    [InlineData(@"\lim_{x \to 0} \frac{\sin x}{x}", "lim _{x rarrow 0} {{sin x} over {x}}")]
    [InlineData(@"\left( \frac{a}{b} \right)", "LEFT ( {{a} over {b}} RIGHT )")]
    [InlineData(@"\left\{ x \right.", "LEFT LBRACE x RIGHT .")]
    [InlineData(@"\begin{pmatrix} 1 & 0 \\ 0 & 1 \end{pmatrix}", "pmatrix {1 & 0 # 0 & 1}")]
    [InlineData(@"\begin{vmatrix} a & b \\ c & d \\ \end{vmatrix}", "dmatrix {a & b # c & d}")]
    [InlineData(@"|x| = \begin{cases} x & x \ge 0 \\ -x & \text{otherwise} \end{cases}", "| x | = cases {x & x GEQ 0 # - x & \"otherwise\"}")]
    [InlineData(@"\begin{aligned} a &= b \\ c &= d \end{aligned}", "pile {a = b # c = d}")]
    [InlineData(@"\hat{x} + \vec{v} + \overline{AB}", "hat {x} + vec {v} + bar {A B}")]
    [InlineData(@"\mathbf{v} \cdot \mathrm{d}x", "{bold {{rm {v}}}} CDOT {rm {d}} x")]
    [InlineData(@"a \neq b \leq c \to \infty", "a NEQ b LEQ c rarrow INF")]
    [InlineData(@"\binom{n}{k}", "LEFT ( {{n} atop {k}} RIGHT )")]
    [InlineData(@"x \in \mathbb{R}", "x IN ℝ")]
    [InlineData(@"f'(x)", "f ^{prime} ( x )")]
    [InlineData(@"f''(x) + g'''", "f ^{prime prime} ( x ) + g ^{prime prime prime}")]
    [InlineData(@"f^\prime + f'_i + f'^2", "f ^{prime} + f _{i} ^{prime} + f ^{prime 2}")]
    [InlineData(@"\{x\}", "LBRACE x RBRACE")]
    [InlineData(@"\displaystyle a \over b", "{{a} over {b}}")]
    [InlineData(@"e^{i\pi} + 1 = 0", "e ^{i pi} + 1 = 0")]
    [InlineData(@"\not\in \not= \not\subset", "NOTIN NEQ ⊄")]
    [InlineData(@"3.14 \quad x", "3.14 ~ x")]
    public void Tex_reads_into_the_equation_tree(string tex, string script) =>
        Assert.Equal(script, EquationScript.Script(LatexMath.Parse(tex)!));

    [Theory]
    [InlineData(@"\foo{x} + 1", "\"\\foo{x}\" + 1")]
    [InlineData(@"\foo[a]{x}{y}", "\"\\foo[a]{x}{y}\"")]
    [InlineData(@"\foo [0, 1]", "\"\\foo\" [ 0 , 1 ]")] // brackets alone are the formula's, not an argument
    [InlineData(@"\begin{weird} a & b \end{weird}", "\"\\begin{weird}\" {a b} \"\\end{weird}\"")]
    public void An_unknown_command_is_kept_as_its_text(string tex, string script) =>
        Assert.Equal(script, EquationScript.Script(LatexMath.Parse(tex)!));

    [Theory]
    [InlineData(@"\frac{")]
    [InlineData(@"}}} {{ ^ _ & \\")]
    [InlineData(@"\left( a")]
    [InlineData(@"a \right) b \end{x}")]
    [InlineData(@"\begin{matrix} 1 & 2")]
    [InlineData(@"\sqrt[")]
    [InlineData(@"\text{unclosed")]
    [InlineData(@"x^")]
    [InlineData(@"\")]
    [InlineData(@"\mathbb{}")]
    public void Broken_tex_keeps_what_it_can_without_failing(string tex)
    {
        var tree = LatexMath.Parse(tex);
        Assert.NotNull(tree);
        Assert.NotNull(EquationScript.Linear(tree));
        Assert.NotNull(MathMLWriter.Write(tree, display: true, tex));
        Assert.NotNull(OmmlWriter.Write(tree, display: true));
    }

    // ───────────────────────── Markdown ─────────────────────────

    [Theory]
    [InlineData("a $x$ b", "x")]
    [InlineData("$5$", "5")]
    [InlineData("costs $5 and $10", "")] // a closing $ after a space doesn't close
    [InlineData("$5-$10", "")] // nor one before a digit
    [InlineData("$ x$", "")] // an opening $ before a space doesn't open
    [InlineData("a \\$x$", "")] // an escaped $ is text
    [InlineData("`$x$` code", "")] // a code span wins
    [InlineData("$a `b$` c", "")] // a code span that starts inside wins too
    [InlineData("$a`b$", "a`b")] // a backtick without its pair is math
    [InlineData("\\(x\\) and \\(y\\)", "x|y")]
    [InlineData("\\\\(x\\\\)", "")] // \\ is an escaped backslash: the bracket after it is text
    [InlineData("text $$a+b$$ more", "a+b")]
    [InlineData("$\\$$", "\\$")]
    public void Math_delimiters_follow_the_usual_rules(string markdown, string expected)
    {
        var equations = Equations(MarkdownReader.Read(markdown, fx.NewFolder()));
        Assert.Equal(expected, string.Join("|", equations.Select(e => e.Tex)));
    }

    [Theory]
    [InlineData("$$\n\\frac{a}{b}\n$$")]
    [InlineData("$$\\frac{a}{b}$$")]
    [InlineData("\\[\n\\frac{a}{b}\n\\]")]
    [InlineData("\\[\\frac{a}{b}\\]")]
    public void Display_math_is_a_centred_equation_of_its_own(string markdown)
    {
        var document = MarkdownReader.Read($"Before\n\n{markdown}\n\nAfter", fx.NewFolder());

        var paragraph = document.Sections.Single().Blocks.OfType<HParagraph>().Single(p => p.Inlines.Any(i => i is HEquation));
        var equation = Assert.IsType<HEquation>(Assert.Single(paragraph.Inlines));
        Assert.True(equation.Display);
        Assert.Equal(HAlign.Center, paragraph.Format.Align);
        Assert.Equal("{{a} over {b}}", EquationScript.Script(equation.Math));
    }

    [Fact]
    public void Display_math_among_text_stays_in_its_line()
    {
        var paragraph = MarkdownReader.Read("where $$E = mc^2$$ holds", fx.NewFolder()).Sections.Single().Blocks.OfType<HParagraph>().Single();

        var equation = Assert.Single(paragraph.Inlines.OfType<HEquation>());
        Assert.False(equation.Display);
        Assert.Null(paragraph.Format.Align);
        Assert.Equal("$$E = mc^2$$", equation.Text);
    }

    [Fact]
    public void Writers_without_equations_keep_the_tex_source()
    {
        var document = MarkdownReader.Read("Area $\\pi r^2$ and\n\n$$\\sum_{i=1}^n i$$", fx.NewFolder());

        var text = PlainTextWriter.Write(document);
        Assert.Contains("$\\pi r^2$", text);
        Assert.Contains("$$\\sum_{i=1}^n i$$", text);

        var markdown = MarkdownWriter.Write(document, _ => null);
        Assert.Contains("$\\pi r^2$", markdown); // not escaped: it reads back as math
        Assert.Equal(["\\pi r^2", "\\sum_{i=1}^n i"], Equations(MarkdownReader.Read(markdown, fx.NewFolder())).Select(e => e.Tex));
    }

    [Fact]
    public void Math_in_a_table_header_cell_is_math_as_in_the_body_cells()
    {
        var document = MarkdownReader.Read("| Formula $x^2$ | Value |\n| --- | --- |\n| $y_1$ | 2 |", fx.NewFolder());

        var table = document.Sections.Single().Blocks.OfType<HTable>().Single();
        var header = table.Rows[0].Cells[0].Blocks.OfType<HParagraph>().Single();
        Assert.Equal("x^2", Assert.Single(header.Inlines.OfType<HEquation>()).Tex);
        Assert.True(header.Inlines.OfType<HText>().Where(t => t is not HEquation && t.Text.Trim().Length > 0).All(t => t.Format.Bold == true));
        Assert.Equal(["x^2", "y_1"], Equations(document).Select(e => e.Tex));
        // Written back, the header keeps its math.
        Assert.Equal(["x^2", "y_1"], Equations(MarkdownReader.Read(MarkdownWriter.Write(document, _ => null), fx.NewFolder())).Select(e => e.Tex));
    }

    // ───────────────────────── Writers ─────────────────────────

    private const string Formulas = """
        # Formulas

        Inline $x^2 + y^2 = r^2$ and $\alpha \le \beta$.

        $$
        \sum_{k=1}^{n} k = \frac{n(n+1)}{2}
        $$

        $$\int_0^1 \sqrt{x}\,dx = \frac{2}{3}$$

        $$\lim_{x \to 0} \frac{\sin x}{x} = 1$$

        $$A = \begin{pmatrix} a & b \\ c & d \end{pmatrix}, \quad |x| = \begin{cases} x & x \ge 0 \\ -x & \text{else} \end{cases}$$

        $$\hat{\mathbf{v}} = \left( \frac{1}{\sqrt{2}} \right)$$
        """;

    [Fact]
    public void Docx_gets_word_math_that_reads_back_as_the_same_equations()
    {
        var document = MarkdownReader.Read(Formulas, fx.NewFolder());
        var output = Path.Combine(fx.NewFolder(), "math.docx");

        DocxWriter.Write(document, output);

        using (var zip = ZipFile.OpenRead(output))
        {
            XNamespace m = "http://schemas.openxmlformats.org/officeDocument/2006/math";
            var body = XDocument.Load(zip.GetEntry("word/document.xml")!.Open());
            Assert.Equal(5, body.Descendants(m + "oMathPara").Count());
            Assert.Equal(7, body.Descendants(m + "oMath").Count());
            Assert.NotEmpty(body.Descendants(m + "nary"));
            Assert.NotEmpty(body.Descendants(m + "f"));
            Assert.NotEmpty(body.Descendants(m + "m"));
            Assert.NotEmpty(body.Descendants(m + "eqArr"));
        }

        // As text: Word keeps upright text (\text) as plain letters, the TeX as quoted text.
        var written = Equations(document).Select(e => EquationScript.Linear(Flat(e.Math))).ToList();
        var read = Equations(DocxReader.Read(output, fx.NewFolder())).Select(e => EquationScript.Linear(Flat(e.Math))).ToList();
        Assert.Equal(written, read);
    }

    [Fact]
    public void Html_gets_mathml_with_the_tex_as_its_annotation()
    {
        var folder = fx.NewFolder();
        var input = Path.Combine(folder, "math.md");
        var html = Filee.Engines.Text.MarkdownConverter.ToHtml(Formulas, input, Path.Combine(folder, "math.html"));

        var maths = MathElement().Matches(html).Select(m => XElement.Parse(m.Value)).ToList();
        Assert.Equal(7, maths.Count);
        XNamespace mml = "http://www.w3.org/1998/Math/MathML";
        Assert.Equal(5, maths.Count(e => (string?)e.Attribute("display") == "block"));
        Assert.Contains(maths, e => e.Descendants(mml + "mfrac").Any());
        Assert.Contains(maths, e => e.Descendants(mml + "munderover").Any());
        Assert.Contains(maths, e => e.Descendants(mml + "mtable").Any());
        Assert.Contains(maths, e => (string?)e.Descendants(mml + "annotation").SingleOrDefault() == "x^2 + y^2 = r^2");
        Assert.DoesNotContain("\\frac", html.Replace("alttext", "", StringComparison.Ordinal)[..html.IndexOf("<math", StringComparison.Ordinal)]);
    }

    [Fact]
    public async Task Epub_pages_with_mathml_say_so_in_the_package()
    {
        var input = Path.Combine(fx.NewFolder(), "math.md");
        await File.WriteAllTextAsync(input, Formulas, TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = "epub" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));

        using var zip = ZipFile.OpenRead(job.Outputs.Single());
        var opf = new StreamReader(zip.Entries.Single(e => e.FullName.EndsWith(".opf", StringComparison.Ordinal)).Open()).ReadToEnd();
        Assert.Contains("properties=\"mathml\"", opf);
        var chapter = zip.Entries.First(e => e.FullName.EndsWith("ch001.xhtml", StringComparison.Ordinal));
        var page = XDocument.Load(chapter.Open()); // still well-formed XHTML
        Assert.NotEmpty(page.Descendants(XName.Get("math", "http://www.w3.org/1998/Math/MathML")));
    }

    [Fact]
    public void Hwpx_gets_hangul_equations()
    {
        var output = Path.Combine(fx.NewFolder(), "math.hwpx");

        HwpxWriter.Write(MarkdownReader.Read(Formulas, fx.NewFolder()), output, cancellationToken: TestContext.Current.CancellationToken);

        using var zip = ZipFile.OpenRead(output);
        var section = new StreamReader(zip.GetEntry("Contents/section0.xml")!.Open()).ReadToEnd();
        Assert.Equal(7, Regex.Matches(section, "<hp:equation ").Count);
        // The scripts are 한글's; the TeX stays only as the equations' description (hp:shapeComment).
        var scripts = Regex.Matches(section, "<hp:script>(.*?)</hp:script>").Select(m => m.Groups[1].Value).ToList();
        Assert.Contains(scripts, s => s.Contains("SUM _{k = 1} ^{n} k", StringComparison.Ordinal));
        Assert.DoesNotContain(scripts, s => s.Contains('\\'));
    }

    [Fact]
    public void The_page_typesets_the_math_instead_of_printing_its_source()
    {
        var fonts = RepositoryFonts.Load();
        Assert.SkipUnless(fonts.Has(RepositoryFonts.Latin), "Noto Sans is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        using var shaper = new Shaper();
        var document = MarkdownReader.Read(Formulas, fx.NewFolder());

        var pages = new PageComposer(new FontResolver(fonts), shaper).Compose(document, TestContext.Current.CancellationToken).Pages;

        var texts = pages.SelectMany(p => p.Items.OfType<GlyphItem>()).Select(g => g.Run.Text).ToList();
        Assert.DoesNotContain(texts, t => t.Contains('\\') || t.Contains('$'));
        Assert.Contains(texts, t => t.Contains('∑'));
        // The fraction bars and the root signs are drawn.
        Assert.True(pages.SelectMany(p => p.Items).Count(i => i is not GlyphItem) >= 5);
    }

    [Theory]
    [InlineData(@"f'(x)", "′")]
    [InlineData(@"f''", "′′")]
    [InlineData(@"f^\prime", "′")]
    public void A_prime_is_drawn_after_its_base_at_full_size(string tex, string primes)
    {
        var fonts = RepositoryFonts.Load();
        Assert.SkipUnless(fonts.Has(RepositoryFonts.Latin), "Noto Sans is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        using var shaper = new Shaper();
        const float size = 20;
        var page = new PageContent(400, 200);

        new MathLayout(new FontResolver(fonts), shaper).Layout(LatexMath.Parse(tex)!, size, SkiaSharp.SKColors.Black).Paint(page, 10, 100, 1);

        var runs = page.Items.OfType<GlyphItem>().Select(i => i.Run).ToList();
        var f = runs.First(r => r.Text == "f");
        var prime = runs.Where(r => r.Text.All(c => c == '′')).ToList();
        Assert.Equal(primes, string.Concat(prime.Select(r => r.Text)));
        Assert.All(prime, r =>
        {
            Assert.Equal(size, r.Size); // not shrunk to a script's size
            Assert.True(r.Positions[0].X >= f.Positions[0].X + size * 0.3f, $"prime at {r.Positions[0].X}, f at {f.Positions[0].X}");
            Assert.True(Math.Abs(r.Positions[0].Y - f.Positions[0].Y) < size * 0.05f, $"prime baseline {r.Positions[0].Y}, f {f.Positions[0].Y}");
        });
    }

    [Fact]
    public void A_prime_is_a_superscript_after_its_base_in_word_math_and_mathml()
    {
        var tree = LatexMath.Parse("f'(x)")!;

        XNamespace m = "http://schemas.openxmlformats.org/officeDocument/2006/math";
        var omml = XElement.Parse($"<r xmlns:m=\"{m}\" xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">{OmmlWriter.Write(tree, display: false)}</r>");
        var sup = Assert.Single(omml.Descendants(m + "sSup"));
        Assert.Equal("f", sup.Element(m + "e")!.Value);
        Assert.Equal("′", sup.Element(m + "sup")!.Value);
        Assert.Equal("(", ((XElement)sup.NextNode!).Value); // the bracket follows the scripted f

        XNamespace mml = "http://www.w3.org/1998/Math/MathML";
        var msup = Assert.Single(XElement.Parse(MathMLWriter.Write(tree, display: false)).Descendants(mml + "msup"));
        Assert.Equal(["f", "′"], msup.Elements().Select(e => e.Value));
    }

    // ───────────────────────── Bounds ─────────────────────────

    [Fact]
    public void Hostile_formulas_stay_text_quickly()
    {
        var watch = Stopwatch.StartNew();
        string[] hostile =
        [
            new string('{', 50_000) + "x" + new string('}', 50_000), // longer than a formula may be
            new string('{', 5_000) + "x" + new string('}', 5_000),
            string.Concat(Enumerable.Repeat(@"\sqrt", 3_000)) + "x",
            string.Concat(Enumerable.Repeat(@"\left(", 3_000)) + "x",
            string.Concat(Enumerable.Repeat(@"\frac1", 3_000)) + "2",
            "x" + string.Concat(Enumerable.Repeat("^", 10_000)) + "2",
            string.Concat(Enumerable.Repeat(@"a \over ", 5_000)) + "b",
            string.Concat(Enumerable.Repeat(@"\begin{matrix}", 3_000)),
            string.Concat(Enumerable.Repeat(@"\text{$", 3_000)),
            string.Concat(Enumerable.Repeat(@"\rm ", 5_000)) + "x",
        ];
        foreach (var tex in hostile)
        {
            var tree = LatexMath.Parse(tex);
            if (tree is not null)
            {
                Assert.False(EquationScript.Deeper(tree, LatexMath.MaxDepth));
                EquationScript.Script(tree);
                MathMLWriter.Write(tree, display: true, tex);
                OmmlWriter.Write(tree, display: true);
            }
            var paragraph = MarkdownReader.Read($"$${tex}$$", fx.NewFolder()).Sections.Single().Blocks.OfType<HParagraph>().First();
            Assert.All(paragraph.Inlines.OfType<HEquation>(), e => Assert.False(EquationScript.Deeper(e.Math, LatexMath.MaxDepth)));
        }
        // A deep one is its source, as text.
        Assert.Null(LatexMath.Parse(hostile[1]));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"{watch.Elapsed}");
    }

    [Fact]
    public void A_flood_of_equations_is_refused_with_the_usual_error()
    {
        var limits = DocumentLimits.Default with { MaxObjects = 10 };
        MarkdownReader.Read(string.Concat(Enumerable.Repeat("$x$ ", 10)), fx.NewFolder(), null, null, limits);

        var refused = Assert.Throws<ConversionRefusedException>(() =>
            MarkdownReader.Read(string.Concat(Enumerable.Repeat("$x$ ", 11)), fx.NewFolder(), null, null, limits));
        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static List<HEquation> Equations(HDocument document) =>
        [.. HDocumentWalker.Inlines(document.Sections.SelectMany(s => s.Blocks)).OfType<HEquation>()];

    /// <summary>
    /// The tree with rows inside rows flattened (Word groups an operator's operand, the TeX doesn't) and spaces left out
    /// (OmmlReader reads none in math).
    /// </summary>
    private static MathNode Flat(MathNode node) => node switch
    {
        MathRow row => new MathRow([.. row.Items.Where(i => i is not MathSpace).SelectMany(i => Flat(i) is MathRow inner ? inner.Items : [Flat(i)])]),
        MathFraction f => f with { Numerator = Flat(f.Numerator), Denominator = Flat(f.Denominator) },
        MathRoot r => r with { Index = r.Index is null ? null : Flat(r.Index), Body = Flat(r.Body) },
        MathScripts s => s with { Base = Flat(s.Base), Sub = s.Sub is null ? null : Flat(s.Sub), Sup = s.Sup is null ? null : Flat(s.Sup) },
        MathBigOperator b => b with { Lower = b.Lower is null ? null : Flat(b.Lower), Upper = b.Upper is null ? null : Flat(b.Upper) },
        MathLimit l => l with { Lower = l.Lower is null ? null : Flat(l.Lower) },
        MathMatrix m => m with { Rows = [.. m.Rows.Select(r => (IReadOnlyList<MathNode>)[.. r.Select(Flat)])] },
        MathCases c => c with { Rows = [.. c.Rows.Select(r => (IReadOnlyList<MathNode>)[.. r.Select(Flat)])] },
        MathPile p => p with { Rows = [.. p.Rows.Select(Flat)] },
        MathBrackets b => b with { Body = Flat(b.Body) },
        MathAccent a => a with { Body = Flat(a.Body) },
        MathStyled s => s with { Body = Flat(s.Body) },
        _ => node,
    };

    [GeneratedRegex("<math .*?</math>", RegexOptions.Singleline)]
    private static partial Regex MathElement();
}
