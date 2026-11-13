// 한글 equations for the layout engine: the equation script read into the equation tree (Hwp/Hwpx/EquationParser) the
// way 한글 reads it, and the tree laid out and drawn (Typesetting/MathLayout) with real fonts, on its line in the box
// 한글 laid it out in. Read back through EquationScript.Script, the tree is checked as the script Filee writes for it.

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Xml.Linq;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
using SkiaSharp;

namespace Filee.Engines.Tests;

public sealed class EquationLayoutTests : IDisposable
{
    private readonly RepositoryFonts _fonts = RepositoryFonts.Load();
    private readonly Shaper _shaper = new();

    public void Dispose() => _shaper.Dispose();

    // ───────────────────────── Script ─────────────────────────

    [Theory]
    // Command words split off the start of a run of letters, longest first; the letters left stay one variable.
    [InlineData("n(rmA cupB)", "n ( {rm {A cup B )}}")]
    [InlineData("itn(rmB)", "{it {n ( {rm {B )}}}}")]
    [InlineData("cdotscdots", "cdots cdots")]
    [InlineData("13 LEQx", "13 LEQ x")]
    [InlineData("rmbarAD", "{rm {bar {AD}}}")]
    // Symbol words and the structure words.
    [InlineData("a<=b != c", "a LEQ b NEQ c")]
    [InlineData("{x _{1} +x _{2}} over {2} =1", "{{x _{1} + x _{2}} over {2}} = 1")]
    [InlineData("a atop b", "{{a} atop {b}}")]
    [InlineData("root 3 of x + sqrt 2", "root {3} of {x} + sqrt {2}")]
    [InlineData("x = root 3", "x = sqrt {3}")]
    [InlineData("A= LEFT { 6,``8 RIGHT }", "A = LEFT LBRACE 6 , ~ ~ 8 RIGHT RBRACE")]
    [InlineData("Q = left{ -2, 6 right", "Q = LEFT LBRACE - 2 , 6 RIGHT .")]
    [InlineData("SUM from {i=1} to n i", "SUM _{i = 1} ^{n} i")]
    [InlineData("int _0 ^1 x dx", "INT _{0} ^{1} x dx")]
    [InlineData("lim from {x -> 0} f(x)", "lim _{x rarrow 0} f ( x )")]
    [InlineData("f(x)= {cases{x-2&(x>2)#x+3&(x LEQ2)}}", "f ( x ) = {cases {x - 2 & ( x > 2 ) # x + 3 & ( x LEQ 2 )}}")]
    [InlineData("pmatrix{1&0#0&1}", "pmatrix {1 & 0 # 0 & 1}")]
    [InlineData("vec AB + hat x", "vec {AB} + hat {x}")]
    [InlineData("\"if\" ~ x", "\"if\" ~ x")]
    public void A_script_reads_into_the_tree_hangul_reads(string script, string written) =>
        Assert.Equal(written, EquationScript.Script(EquationParser.Parse(script)));

    [Theory]
    [InlineData("")]
    [InlineData("}}} {{ ^ _ # &")]
    [InlineData("LEFT ( a")]
    [InlineData("over over")]
    [InlineData("matrix")]
    [InlineData("\"unclosed")]
    public void Broken_scripts_keep_what_they_can_without_failing(string script)
    {
        var tree = EquationParser.Parse(script);
        Assert.NotNull(EquationScript.Linear(tree));
    }

    // ───────────────────────── Layout ─────────────────────────

    [Fact]
    public void A_fraction_stands_on_the_axis_with_its_parts_above_and_below_the_rule()
    {
        var math = Math();
        var letter = math.Layout(EquationParser.Parse("x"), 10, SKColors.Black);
        var fraction = math.Layout(EquationParser.Parse("{a+b} over c"), 10, SKColors.Black);

        Assert.True(fraction.Ascent > letter.Ascent + 3 && fraction.Descent > letter.Descent + 3);
        var page = Paint(fraction);
        var (a, c) = (Glyphs(page, "a"), Glyphs(page, "c"));
        Assert.True(a.Positions[0].Y < 0 - 3 && c.Positions[0].Y > 3, $"{a.Positions[0].Y} / {c.Positions[0].Y}");
        Assert.Contains(page.Items, i => i is DrawingItem);
    }

    [Fact]
    public void Scripts_are_smaller_and_raised_or_lowered_and_brackets_grow_with_what_they_hold()
    {
        var math = Math();
        var page = Paint(math.Layout(EquationParser.Parse("x ^2 _i"), 10, SKColors.Black));
        var (two, i) = (Glyphs(page, "2"), Glyphs(page, "i"));
        Assert.Equal(7f, two.Size, 0.01f);
        Assert.True(two.Positions[0].Y < -3 && i.Positions[0].Y > 0);

        var small = math.Layout(EquationParser.Parse("LEFT ( x RIGHT )"), 10, SKColors.Black);
        var tall = math.Layout(EquationParser.Parse("LEFT ( {a over b} RIGHT )"), 10, SKColors.Black);
        Assert.True(tall.Height > small.Height + 5);
    }

    [Fact]
    public void Variables_are_italic_and_numbers_functions_and_rm_text_upright()
    {
        var page = Paint(Math().Layout(EquationParser.Parse("sin x + 2 + rm A"), 10, SKColors.Black));

        Assert.True(Glyphs(page, "x").SkewX != 0 || Glyphs(page, "x").Font.Typeface.IsItalic);
        Assert.Equal(0, Glyphs(page, "sin").SkewX);
        Assert.False(Glyphs(page, "sin").Font.Typeface.IsItalic);
        Assert.False(Glyphs(page, "A").Font.Typeface.IsItalic);
    }

    [Fact]
    public void An_equation_in_a_paragraph_takes_hangul_s_box_on_the_line_and_is_drawn_as_math()
    {
        SkipWithoutFonts();
        var paragraph = new HParagraph { Format = new HParaFormat(Before: 0, After: 0), MarkFormat = Format() };
        paragraph.Inlines.Add(new HText("a", Format()));
        // A box 30 pt wide and 20 high, the baseline 15 pt down (75%), for a fraction wider than that: drawn narrowed.
        paragraph.Inlines.Add(new HEquation(EquationParser.Parse("{1+2+3+4+5+6} over 7"), Format()) { Width = 3000, Height = 2000, BaseLine = 75, Size = 1000 });
        paragraph.Inlines.Add(new HText("b", Format()));

        var box = new ParagraphLayouter(new FontResolver(_fonts), _shaper).Layout(paragraph, 200);
        var line = Assert.Single(box.Lines);
        var page = new PageContent(200, 100);
        line.Draw(page, 0, 0);

        var runs = page.Items.OfType<GlyphItem>().Select(i => i.Run).ToList();
        var (a, b) = (runs.Single(r => r.Text == "a"), runs.Single(r => r.Text == "b"));
        using var font = new SKFont(a.Font.Typeface, a.Size);
        Assert.InRange(b.Positions[0].X - a.Positions[0].X - font.MeasureText("a"), 29.5f, 30.5f);
        // The line holds the box above (15) and below (5) the baseline; the fraction is drawn, not its linear text.
        Assert.True(line.Baseline >= 15 - 0.01f);
        Assert.DoesNotContain(runs, r => r.Text.Contains('/'));
        var seven = runs.Single(r => r.Text == "7");
        Assert.True(seven.Positions[0].Y > a.Positions[0].Y);
        Assert.True(runs.Where(r => r.Text.Contains('1')).All(r => r.ScaleX < 1));
    }

    // ───────────────────────── Hostile input ─────────────────────────

    /// <summary>Stack of the thread the deep equations are read and laid out on: a third of a thread-pool thread's.</summary>
    private const int SmallStack = 512 * 1024;

    /// <summary>Scripts nested thousands of levels deep in each way the reader recurses (and "over" without braces).</summary>
    private static string DeepScript(string kind) => kind switch
    {
        "braces" => new string('{', 30_000),
        "fractions" => Repeat("{a over ", 5_000) + "a",
        "over chain" => "a" + Repeat(" over a", 20_000),
        "roots" => Repeat("sqrt ", 10_000) + "x",
        "indexed roots" => Repeat("root 2 of ", 10_000) + "x",
        "styles" => Repeat("rm ", 10_000) + "x",
        "styles in one word" => Repeat("rm", 10_000) + "x",
        "scripts" => Repeat("x ^{", 10_000) + "2",
        "brackets" => Repeat("LEFT ( ", 10_000) + "x",
        "matrices" => Repeat("matrix {", 5_000) + "1",
        "accents" => Repeat("hat ", 10_000) + "x",
        "colours" => Repeat("color ", 30_000) + "x",
        "limits" => Repeat("SUM from ", 10_000) + "i",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [InlineData("braces")]
    [InlineData("fractions")]
    [InlineData("over chain")]
    [InlineData("roots")]
    [InlineData("indexed roots")]
    [InlineData("styles")]
    [InlineData("styles in one word")]
    [InlineData("scripts")]
    [InlineData("brackets")]
    [InlineData("matrices")]
    [InlineData("accents")]
    [InlineData("colours")]
    [InlineData("limits")]
    public void A_script_nested_past_the_limit_is_kept_as_its_text(string kind)
    {
        var script = DeepScript(kind);
        var watch = Stopwatch.StartNew();
        OnSmallStack(() =>
        {
            var tree = EquationParser.Parse(script);
            Assert.Equal(script, Assert.IsType<MathQuoted>(tree).Text);
            Assert.Equal(script, EquationScript.Linear(tree));
            Assert.True(EquationScript.Measure(tree, 1000).Width > 0);
        });
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"{watch.Elapsed}");
    }

    [Fact]
    public void A_long_flat_script_and_one_nested_to_the_limit_keep_their_tree()
    {
        var flat = EquationParser.Parse(Repeat("a + ", 10_000) + "a");
        Assert.Equal(20_001, Assert.IsType<MathRow>(flat).Items.Count);

        // 99 groups with the letter in the innermost: 100 levels, kept; 101 groups are not.
        var braces = EquationParser.Parse(new string('{', EquationParser.MaxDepth - 1) + "x" + new string('}', EquationParser.MaxDepth - 1));
        Assert.IsNotType<MathQuoted>(braces);
        Assert.False(EquationScript.Deeper(braces, EquationParser.MaxDepth));
        var past = EquationParser.Parse(new string('{', EquationParser.MaxDepth + 1) + "x");
        Assert.IsType<MathQuoted>(past);
    }

    /// <summary>The deepest trees each reader keeps go through every walk, and the layout and its drawing, on a small stack.</summary>
    [Fact]
    public void The_deepest_equations_the_readers_keep_are_walked_and_laid_out_on_a_small_stack()
    {
        var math = Math();
        // Fractions in fractions to just within EquationParser.MaxDepth (a fraction and its group are two levels).
        const int fractions = EquationParser.MaxDepth / 2 - 2;
        var script = Repeat("{a over ", fractions) + "b" + new string('}', fractions);
        var parsed = EquationParser.Parse(script);
        Assert.IsNotType<MathQuoted>(parsed);

        // Word math to just within OmmlReader's 200 element levels (a fraction is two: m:f, m:num).
        const int levels = 99;
        var omml = OmmlReader.Read(XElement.Parse($"<m:oMath {DocxBuilder.Namespaces}>" + Repeat("<m:f><m:num>", levels) + "<m:r><m:t>x</m:t></m:r>" +
                                                  Repeat("</m:num><m:den><m:r><m:t>y</m:t></m:r></m:den></m:f>", levels) + "</m:oMath>"));
        Assert.True(EquationScript.Deeper(omml, 2 * levels));

        foreach (var tree in new[] { parsed, omml })
        {
            OnSmallStack(() =>
            {
                Assert.NotEmpty(EquationScript.Script(tree));
                Assert.NotEmpty(EquationScript.Linear(tree));
                Assert.True(EquationScript.Measure(tree, 1000).Height > 0);
                Assert.NotEmpty(EquationScript.BreakLines(tree, 100, 1000));
                var box = math.Layout(tree, 10, SKColors.Black);
                Assert.True(box.Height > 0);
                Paint(box);
            });
        }
    }

    [Fact]
    public void Nested_integrals_lay_out_each_limit_once()
    {
        var math = Math();
        // An integral laid out its limits twice: 2^40 layouts of the innermost here.
        var script = Repeat("int _{", 40) + "x" + new string('}', 40);
        var tree = EquationParser.Parse(script);
        Assert.IsNotType<MathQuoted>(tree);
        var watch = Stopwatch.StartNew();
        Assert.True(math.Layout(tree, 10, SKColors.Black).Height > 0);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"{watch.Elapsed}");
    }

    private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

    /// <summary>Runs <paramref name="work"/> on a thread with <see cref="SmallStack"/> and passes its exception on.</summary>
    private static void OnSmallStack(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, SmallStack);
        thread.Start();
        thread.Join();
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    // ───────────────────────── Helpers ─────────────────────────

    private void SkipWithoutFonts() =>
        Assert.SkipUnless(_fonts.Has(RepositoryFonts.Latin) && _fonts.Has(RepositoryFonts.Korean),
            "Noto Sans and Noto Sans KR are not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");

    private MathLayout Math()
    {
        SkipWithoutFonts();
        return new MathLayout(new FontResolver(_fonts), _shaper);
    }

    private static HCharFormat Format() => new(Size: 1000, Font: RepositoryFonts.Latin, EastAsianFont: RepositoryFonts.Korean, Color: "#000000");

    private static PageContent Paint(EquationBox box)
    {
        var page = new PageContent(200, 200);
        box.Paint(page, 0, 0, 1);
        return page;
    }

    private static GlyphRun Glyphs(PageContent page, string text) => page.Items.OfType<GlyphItem>().Select(i => i.Run).First(r => r.Text == text);
}
