// Equations on more routes: Word's and TeX's inline equations breaking across lines of text at their operators, the
// StarMath source of OpenDocument formula objects (written from the tree, kept from LibreOffice's files, read when the
// MathML shows nothing), PowerPoint's Office Math in titles and tables and drawn by the slide renderer from a14:m,
// MathML read back from HTML and EPUB, and equation pictures drawn straight into their part. Every input is built in
// code.

using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Filee.Engines.Ebooks;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Odt;
using Filee.Engines.Hwp.Hwpx.Pptx;
using Filee.Engines.Rendering;
using Filee.Engines.Slides;
using Filee.Engines.Typesetting;
using SkiaSharp;

namespace Filee.Engines.Tests;

public sealed class MathRoutesTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    private static readonly XNamespace Mml = "http://www.w3.org/1998/Math/MathML";
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace A14 = "http://schemas.microsoft.com/office/drawing/2010/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";

    private readonly Shaper _shaper = new();

    public void Dispose() => _shaper.Dispose();

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private static List<HEquation> Equations(HDocument document) => [.. HDocumentWalker.Inlines(document.Sections.SelectMany(s => s.Blocks).ToList()).OfType<HEquation>()];

    private static XDocument Entry(ZipArchive zip, string name)
    {
        using var stream = (zip.GetEntry(name) ?? throw new Xunit.Sdk.XunitException($"no {name}")).Open();
        return XDocument.Load(stream);
    }

    private static HCharFormat Format() => new(Size: 1000, Font: "Noto Sans");

    // ───────────────────────── Inline equations across lines ─────────────────────────

    private const string Long = "a+b+c-d+e+f-g+h+i-j+k+l-m+n+o-p+q+r-s+t+u";

    private static HParagraph Inline(string tex)
    {
        var paragraph = new HParagraph { Format = new HParaFormat(Align: HAlign.Left, Before: 0, After: 0) };
        paragraph.Inlines.Add(new HText("Then ", Format()));
        paragraph.Inlines.Add(new HEquation(LatexMath.Parse(tex)!, Format()) { Tex = tex });
        paragraph.Inlines.Add(new HText(" ends.", Format()));
        return paragraph;
    }

    private static List<GlyphRun> Drawn(ParagraphBox box, float width)
    {
        var page = new PageContent(width + 200, 2000);
        var y = 0f;
        foreach (var line in box.Lines)
        {
            line.Draw(page, 0, y);
            y += line.Height;
        }
        // (Direction controls, drawn as nothing, left out.)
        return [.. page.Items.OfType<GlyphItem>().Select(i => i.Run)
            .Where(r => !r.Text.All(c => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))];
    }

    [Fact]
    public void An_inline_equation_breaks_across_lines_before_its_operators()
    {
        var fonts = RepositoryFonts.Load();
        Assert.SkipUnless(fonts.Has(RepositoryFonts.Latin), "Noto Sans is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        var layouter = new ParagraphLayouter(new FontResolver(fonts), _shaper) { WordEquations = true };
        const float Width = 120;

        var box = layouter.Layout(Inline(Long), Width);
        Assert.True(box.Lines.Count >= 3, $"{box.Lines.Count} lines");
        var runs = Drawn(box, Width);
        // Nothing past the column (a little room for an italic letter's overhang).
        Assert.All(runs, r => Assert.True(r.Positions.Max(p => p.X) <= Width, $"{r.Text} at {r.Positions.Max(p => p.X)}"));
        // The lines after the first break before an operator: each starts with one (or with the text after the equation).
        var starts = runs.GroupBy(r => MathF.Round(r.Positions[0].Y)).OrderBy(g => g.Key).Skip(1)
            .Select(g => WordEquationLayoutTests.Plain(g.OrderBy(r => r.Positions[0].X).First().Text)).ToList();
        Assert.All(starts, s => Assert.Matches("^([+−-]|ends)", s));

        // In a wide column it is one line, its glyphs where the whole equation laid out at once has them.
        Assert.Single(layouter.Layout(Inline(Long), 1000).Lines);
        var only = new HParagraph { Format = new HParaFormat(Align: HAlign.Left, Before: 0, After: 0) };
        only.Inlines.Add(new HEquation(LatexMath.Parse(Long)!, Format()));
        var wide = layouter.Layout(only, 1000);
        Assert.Single(wide.Lines);
        var math = new MathLayout(new FontResolver(fonts), _shaper);
        var whole = math.Layout(LatexMath.Parse(Long)!, 10, SKColors.Black, MathMode.Text);
        var page = new PageContent(1000, 100);
        whole.Paint(page, 0, 50, 1);
        var alone = page.Items.OfType<GlyphItem>().SelectMany(i => i.Run.Positions.Select(p => p.X)).Order().ToList();
        var inLine = Drawn(wide, 1000).SelectMany(r => r.Positions.Select(p => p.X)).Order().ToList();
        Assert.Equal(alone.Count, inLine.Count);
        for (var i = 0; i < alone.Count; i++)
            Assert.Equal(alone[i] - alone[0], inLine[i] - inLine[0], 0.01);

        // In a right-to-left paragraph too: the pieces stay in their order, left to right.
        var rtl = new HParagraph { Format = new HParaFormat(Align: HAlign.Right, Before: 0, After: 0, RightToLeft: true) };
        rtl.Inlines.Add(new HText("مرحبا ", Format()));
        rtl.Inlines.Add(new HEquation(LatexMath.Parse(Long)!, Format()));
        var mirrored = Drawn(layouter.Layout(rtl, 1000), 1000).Where(r => !r.Text.Any(c => c is >= '\u0600' and <= '\u06FF' or ' '))
            .SelectMany(r => r.Positions.Select(p => p.X)).Order().ToList();
        Assert.Equal(alone.Count, mirrored.Count);
        for (var i = 0; i < alone.Count; i++)
            Assert.Equal(alone[i] - alone[0], mirrored[i] - mirrored[0], 0.01);
    }

    [Fact]
    public void A_hangul_equation_or_one_without_operators_stays_whole()
    {
        var fonts = RepositoryFonts.Load();
        Assert.SkipUnless(fonts.Has(RepositoryFonts.Latin), "Noto Sans is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        // 한글's equations (not Word's) are one object, as 한글 sets them.
        var hangul = new ParagraphLayouter(new FontResolver(fonts), _shaper).Layout(Inline(Long), 120);
        Assert.Contains(Drawn(hangul, 120), r => r.Positions.Max(p => p.X) > 120); // on a line of its own, past the column
        // No operator to break at: whole.
        var math = new MathLayout(new FontResolver(fonts), _shaper);
        Assert.Null(math.Pieces(LatexMath.Parse(@"\frac{a+b}{c+d}")!, 10, SKColors.Black, MathMode.Text));
        Assert.Null(math.Pieces(LatexMath.Parse("x")!, 10, SKColors.Black, MathMode.Text));
        // A sign at the start is no place to break.
        Assert.Equal(2, math.Pieces(LatexMath.Parse("-a+b")!, 10, SKColors.Black, MathMode.Text)!.Count);
    }

    // ───────────────────────── StarMath ─────────────────────────

    [Theory]
    [InlineData(@"\frac{a}{b}", "{ a } over { b }")]
    [InlineData(@"x^2 + y_i", "{ x } ^ { 2 } + { y } _ { i }")]
    [InlineData(@"\sqrt{x} \le \sqrt[3]{y}", "sqrt { x } <= nroot { 3 } { y }")]
    [InlineData(@"\alpha \in \mathbb{R}", "%alpha in setR")]
    [InlineData(@"\sum_{k=1}^{n} k", "sum from { k = 1 } to { n } k")]
    [InlineData(@"\lim_{x \to 0} f(x)", "lim from { x toward 0 } f \\( x \\)")]
    [InlineData(@"\left( a \right]", "left ( { a } right ]")]
    [InlineData(@"\begin{pmatrix} a & b \\ c & d \end{pmatrix}", "left ( matrix { { a } # { b } ## { c } # { d } } right )")]
    [InlineData(@"\sin x \cdot \text{if}", "sin x cdot \"if\"")]
    public void Starmath_is_written_from_the_tree(string tex, string starMath) => Assert.Equal(starMath, StarMath.Write(LatexMath.Parse(tex)!));

    [Theory]
    [InlineData(@"\frac{a+b}{2c} = \sqrt{x}")]
    [InlineData(@"\sum_{i=1}^{n} i^2 = \frac{n(n+1)(2n+1)}{6}")]
    [InlineData(@"\int_0^1 \sqrt{x}\,dx = \frac{2}{3}")]
    [InlineData(@"\lim_{x \to \infty} \frac{1}{x} = 0")]
    [InlineData(@"\begin{pmatrix} a & b \\ c & d \end{pmatrix} \begin{bmatrix} 1 \\ 2 \end{bmatrix}")]
    [InlineData(@"f(x) = \begin{cases} x & x > 0 \\ -x & x \le 0 \end{cases}")]
    [InlineData(@"\hat{x} + \vec{v} + \overline{AB} + \tilde{n}")]
    [InlineData(@"\left| x \right| + \left\{ a \right\} + \lfloor y \rfloor")]
    [InlineData(@"\alpha \beta \Gamma \pm \infty \partial \nabla \times \div")]
    [InlineData(@"\mathbf{F} = m\mathrm{a}")]
    [InlineData(@"A \cup B \cap C \subseteq D \Rightarrow \forall x \exists y")]
    [InlineData(@"\binom{n}{k}")]
    public void Starmath_reads_back_what_is_written(string tex)
    {
        var tree = LatexMath.Parse(tex)!;
        var starMath = StarMath.Write(tree)!;
        var read = StarMath.Read(starMath);
        Assert.NotNull(read);
        Assert.Equal(Normal(EquationScript.Linear(tree)), Normal(EquationScript.Linear(read)));
    }

    /// <summary>Linear text without spaces and with one kind of minus.</summary>
    private static string Normal(string linear) => linear.Replace(" ", "", StringComparison.Ordinal).Replace('−', '-');

    [Fact]
    public void Libreoffice_starmath_reads_into_the_tree()
    {
        // As LibreOffice users type it: words, %greek, braces left out where StarMath doesn't need them.
        var read = StarMath.Read("sum from{i=1} to n i^2 = {n(n+1)(2n+1)} over 6 newline %alpha cdot left lbrace stack{a # b} right none")!;
        var pile = Assert.IsType<MathPile>(read);
        Assert.Equal(2, pile.Rows.Count);
        Assert.Equal("SUM _{i = 1} ^{n} i ^{2} = {{n ( n + 1 ) ( 2 n + 1 )} over {6}}", EquationScript.Script(pile.Rows[0]));
        Assert.Equal("alpha CDOT cases {a # b}", EquationScript.Script(pile.Rows[1]));
        // Attributes are passed over, an unknown word is a variable (as StarMath reads it), a stray brace doesn't stop the reading.
        Assert.Equal("{bold {x}} + yz", EquationScript.Script(StarMath.Read("color red size 14 bold x + } yz")!));
        Assert.Null(StarMath.Read("  <?> "));
    }

    [Fact]
    public void Deep_starmath_is_read_flat_and_quickly()
    {
        var text = string.Concat(Enumerable.Repeat("sqrt { x ", 20_000)) + string.Concat(Enumerable.Repeat("} ", 20_000));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var read = StarMath.Read(text)!;
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"{started.Elapsed}");
        Assert.Equal(20_000, EquationScript.Linear(read).Count(c => c == 'x'));
        // Too deep to write: no source rather than a stack overflow.
        MathNode deep = new MathText("x");
        for (var i = 0; i < 5_000; i++)
            deep = new MathRoot(null, deep);
        Assert.Null(StarMath.Write(deep));
    }

    [Fact]
    public void Odt_formulas_keep_their_starmath_source_for_libreoffice()
    {
        var source = MarkdownReader.Read("Inline $\\frac{a}{b}$ and\n\n$$x^2 = \\sqrt{y}$$\n", fx.NewFolder());
        var odt = Path.Combine(fx.NewFolder(), "math.odt");
        OdtWriter.Write(source, odt, Token);

        string Annotation(string path, string folder)
        {
            using var zip = ZipFile.OpenRead(path);
            var math = Entry(zip, folder + "/content.xml").Root!;
            var annotations = math.Descendants(Mml + "annotation").ToList();
            // The presentation first, then the StarMath source, then the TeX.
            Assert.NotEqual("annotation", math.Element(Mml + "semantics")!.Elements().First().Name.LocalName);
            Assert.Equal(["StarMath 5.0", "application/x-tex"], annotations.Select(a => (string?)a.Attribute("encoding")));
            return annotations[0].Value;
        }
        Assert.Equal("{ a } over { b }", Annotation(odt, "Object 1"));
        Assert.Equal("{ x } ^ { 2 } = sqrt { y }", Annotation(odt, "Object 2"));

        // A LibreOffice file's own source (as its user typed it) is kept through Filee: read, written again.
        var libre = Path.Combine(fx.NewFolder(), "libre.odt");
        File.Copy(odt, libre);
        using (var zip = ZipFile.Open(libre, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("Object 1/content.xml")!;
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("Object 1/content.xml").Open());
            writer.Write("<math xmlns=\"http://www.w3.org/1998/Math/MathML\"><semantics><mfrac><mi>a</mi><mi>b</mi></mfrac>" +
                         "<annotation encoding=\"StarMath 5.0\">a over b</annotation></semantics></math>");
        }
        var read = OdtReader.Read(libre, fx.NewFolder());
        var first = Equations(read)[0];
        Assert.Equal("a over b", first.StarMath);
        Assert.Equal("{{a} over {b}}", EquationScript.Script(first.Math));
        var again = Path.Combine(fx.NewFolder(), "again.odt");
        OdtWriter.Write(read, again, Token);
        Assert.Equal("a over b", Annotation2(again));

        static string Annotation2(string path)
        {
            using var zip = ZipFile.OpenRead(path);
            return Entry(zip, "Object 1/content.xml").Root!.Descendants(Mml + "annotation").First(a => (string?)a.Attribute("encoding") == "StarMath 5.0").Value;
        }
    }

    [Fact]
    public void A_formula_whose_mathml_shows_nothing_is_read_from_its_starmath()
    {
        var math = XElement.Parse("<math xmlns=\"http://www.w3.org/1998/Math/MathML\"><semantics><mrow/>" +
                                  "<annotation encoding=\"StarMath 5.0\">E = m c^2</annotation></semantics></math>");
        var formula = MathMLReader.Read(math)!;
        Assert.Equal("E = m c ^{2}", EquationScript.Script(formula.Math));
        Assert.Equal("E = m c^2", formula.StarMath);
    }

    // ───────────────────────── PPTX ─────────────────────────

    private const string Deck = """
        # Energy $E = mc^2$

        | Name | Formula |
        | --- | --- |
        | Area | $\pi r^2$ |
        | Sum | $\sum_{k=1}^{n} k$ |

        Text with $a + b$ inline.
        """;

    [Fact]
    public void Pptx_titles_and_tables_have_office_math_with_their_linear_text_as_fallback()
    {
        var source = MarkdownReader.Read(Deck, fx.NewFolder());
        var pptx = Path.Combine(fx.NewFolder(), "deck.pptx");
        PptxWriter.Write(source, pptx, Token);
        PptxWriterTests.Valid(pptx);

        using (var zip = ZipFile.OpenRead(pptx))
        {
            var slides = zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$")).Select(e => Entry(zip, e.FullName)).ToList();
            var alternates = slides.SelectMany(s => s.Descendants(Mc + "AlternateContent")).ToList();
            // The title: a placeholder with a14:m in its choice, its linear text in the fallback.
            var title = Assert.Single(alternates, a => a.Element(Mc + "Choice")!.Descendants(P + "ph").Any(ph => (string?)ph.Attribute("type") == "title"));
            Assert.Single(title.Element(Mc + "Choice")!.Descendants(A14 + "m"));
            var fallbackTitle = title.Element(Mc + "Fallback")!;
            Assert.Empty(fallbackTitle.Descendants(A14 + "m"));
            Assert.Contains(EquationScript.Linear(Equations(source)[0].Math), string.Concat(fallbackTitle.Descendants(A + "t").Select(t => t.Value)), StringComparison.Ordinal);
            // The table: its graphic frame with a14:m in the cells, the same table with linear text as the fallback.
            var table = Assert.Single(alternates, a => a.Element(Mc + "Choice")!.Descendants(A + "tbl").Any());
            var choiceCells = table.Element(Mc + "Choice")!.Descendants(A + "tc").ToList();
            Assert.Equal(2, choiceCells.Count(c => c.Descendants(A14 + "m").Any()));
            Assert.All(table.Element(Mc + "Choice")!.Descendants(A14 + "m"), m => Assert.Equal(A + "p", m.Parent!.Name));
            var fallbackTable = table.Element(Mc + "Fallback")!;
            Assert.Empty(fallbackTable.Descendants(A14 + "m"));
            Assert.Equal(choiceCells.Count, fallbackTable.Descendants(A + "tc").Count());
            // The same shape id in both.
            Assert.Equal((string?)table.Element(Mc + "Choice")!.Descendants(P + "cNvPr").First().Attribute("id"),
                (string?)fallbackTable.Descendants(P + "cNvPr").First().Attribute("id"));
        }

        // Filee's document reader takes them from the choices: four equations, as the Markdown has.
        var read = PptxReader.Read(pptx, fx.NewFolder());
        Assert.Equal(Equations(source).Select(e => EquationScript.Script(e.Math)).Order(), Equations(read).Select(e => EquationScript.Script(e.Math)).Order());
    }

    [Fact]
    public void The_slide_renderer_draws_powerpoint_equations_from_their_office_math()
    {
        var source = MarkdownReader.Read(Deck, fx.NewFolder());
        var pptx = Path.Combine(fx.NewFolder(), "deck.pptx");
        PptxWriter.Write(source, pptx, Token);

        var deck = PptxSlideReader.Read(pptx, cancellationToken: Token);
        var elements = deck.Slides.SelectMany(s => Flat(s.Elements)).ToList();
        var frames = elements.OfType<SlideShape>().Select(s => s.Text).Concat(elements.OfType<SlideTable>().SelectMany(t => t.Rows.SelectMany(r => r.Cells.Select(c => c.Text))))
            .OfType<SlideTextFrame>().ToList();
        var equations = frames.SelectMany(f => HDocumentWalker.Inlines([.. f.Paragraphs]).OfType<HEquation>()).ToList();
        Assert.Equal(Equations(source).Select(e => EquationScript.Script(e.Math)).Order(), equations.Select(e => EquationScript.Script(e.Math)).Order());
        // Not the fallback's picture of the text box: no shape is filled with one.
        Assert.DoesNotContain(elements.OfType<SlideShape>(), s => s.Fill is PictureFill);

        // Drawn as glyphs (the math font's), not as text of the linear form.
        var fonts = RepositoryFonts.Load();
        using var renderer = new SlideRenderer(new FontResolver(fonts), _shaper, Token);
        var pages = renderer.Render(deck);
        var text = string.Concat(pages.SelectMany(p => p.Items.OfType<GlyphItem>()).Select(g => g.Run.Text));
        Assert.DoesNotContain("^2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("π r", text, StringComparison.Ordinal);

        static IEnumerable<SlideElement> Flat(IEnumerable<SlideElement> list) =>
            list.SelectMany(e => e is SlideGroup group ? Flat(group.Children).Prepend(e) : [e]);
    }

    // ───────────────────────── HTML and EPUB ─────────────────────────

    [Fact]
    public void Html_mathml_reads_as_equations()
    {
        var dir = fx.NewFolder();
        var html = Path.Combine(dir, "math.html");
        File.WriteAllText(html, """
            <html><body>
            <p>Inline <math><mi>x</mi><mo>=</mo><mfrac><mn>1</mn><mn>2</mn></mfrac></math> and text.</p>
            <math display="block"><semantics><mrow><msup><mi>e</mi><mrow><mi>i</mi><mi>π</mi></mrow></msup><mo>+</mo><mn>1</mn><mo>=</mo><mn>0</mn></mrow>
            <annotation encoding="application/x-tex">e^{i\pi} + 1 = 0</annotation></semantics></math>
            <p>After.</p>
            </body></html>
            """);
        var document = HtmlReader.Read(html, Path.Combine(dir, "media"));
        var blocks = document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().ToList();
        Assert.Equal(3, blocks.Count);
        var inline = Assert.Single(blocks[0].Inlines.OfType<HEquation>());
        Assert.False(inline.Display);
        Assert.Equal("x = {{1} over {2}}", EquationScript.Script(inline.Math));
        Assert.Contains(blocks[0].Inlines, i => i is HText { Text: "Inline " } and not HEquation);
        Assert.Contains(blocks[0].Inlines, i => i is HText { Text: " and text." } and not HEquation);
        var display = Assert.IsType<HEquation>(Assert.Single(blocks[1].Inlines));
        Assert.True(display.Display);
        Assert.Equal(@"e^{i\pi} + 1 = 0", display.Tex);
        Assert.Equal(HAlign.Center, blocks[1].Format.Align);
        // The annotation's text isn't read as text.
        Assert.DoesNotContain(blocks, b => b.Inlines.OfType<HText>().Any(t => t is not HEquation && t.Text.Contains("pi", StringComparison.Ordinal)));
    }

    [Fact]
    public void Epub_equations_come_back_as_equations()
    {
        var source = MarkdownReader.Read("# Formulas\n\nInline $x^2 + y^2 = r^2$ here.\n\n$$\\int_0^1 \\sqrt{x}\\,dx = \\frac{2}{3}$$\n", fx.NewFolder());
        var epub = Path.Combine(fx.NewFolder(), "math.epub");
        EpubWriter.Write(new Book(source, new BookMetadata { Title = "Formulas", Language = "en" }), epub, epub, fx.NewFolder());

        var read = EpubReader.Read(epub, fx.NewFolder());
        var equations = Equations(read);
        Assert.Equal(Equations(source).Select(e => e.Tex), equations.Select(e => e.Tex));
        Assert.Equal([false, true], equations.Select(e => e.Display));
    }

    [Fact]
    public void Deep_html_mathml_is_read_without_recursion()
    {
        var dir = fx.NewFolder();
        var html = Path.Combine(dir, "deep.html");
        File.WriteAllText(html, "<p><math>" + string.Concat(Enumerable.Repeat("<mrow>", 20_000)) + "<mi>x</mi>" +
                                string.Concat(Enumerable.Repeat("</mrow>", 20_000)) + "</math></p>");
        var started = System.Diagnostics.Stopwatch.StartNew();
        var document = HtmlReader.Read(html, Path.Combine(dir, "media"));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"{started.Elapsed}");
        Assert.NotNull(document);
    }

    // ───────────────────────── Pictures ─────────────────────────

    [Fact]
    public void An_equation_picture_is_drawn_into_its_part_as_it_is_drawn_alone()
    {
        using var pictures = new EquationPicture();
        var equation = new HEquation(LatexMath.Parse(@"\frac{a}{b}")!, Format());
        var image = pictures.Draw(equation, display: true, dpi: 200)!;
        using var target = new MemoryStream();
        Assert.True(pictures.Write(equation, display: true, target, dpi: 200));
        Assert.Equal(image.Png, target.ToArray());
        Assert.Equal((image.Width, image.Ascent, image.Descent), pictures.Frame(equation, display: true));
        // An empty equation: no picture, nothing written.
        var empty = new HEquation(MathRow.Empty, Format());
        Assert.Null(pictures.Frame(empty, display: false));
        using var none = new MemoryStream();
        Assert.False(pictures.Write(empty, display: false, none));
        Assert.Equal(0, none.Length);
    }
}
