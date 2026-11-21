// Equations in the built-in writers and readers: ODT and FODT formula objects (MathML in an object of its own with a
// replacement picture, read back as equations by MathMLReader), PPTX Office Math (a14:m in mc:AlternateContent with
// a picture fallback, read back by PptxReader), EPUB MathML checked by epubcheck's rules that are cheap to check here,
// KF8 equations as opaque pictures, and Word's wrap settings (m:mathPr) for the layout's line breaks with the
// equation kept whole in the model and broken into paragraphs by the HWPX writer only. Every input is built in code.

using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Ebooks;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Hwp.Hwpx.Odt;
using Filee.Engines.Hwp.Hwpx.Pptx;
using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
using SkiaSharp;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public sealed class MathWriterTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    private static readonly XNamespace Mml = "http://www.w3.org/1998/Math/MathML";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace A14 = "http://schemas.microsoft.com/office/drawing/2010/main";
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private readonly Shaper _shaper = new();

    public void Dispose() => _shaper.Dispose();

    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Two inline formulas and three display ones.</summary>
    private const string Formulas = """
        # Formulas

        Inline $x^2 + y^2 = r^2$ and $\alpha \le \beta$.

        $$
        \sum_{k=1}^{n} k = \frac{n(n+1)}{2}
        $$

        $$\int_0^1 \sqrt{x}\,dx = \frac{2}{3}$$

        $$A = \begin{pmatrix} a & b \\ c & d \end{pmatrix}$$
        """;

    private HDocument Markdown(string markdown = Formulas) => MarkdownReader.Read(markdown, fx.NewFolder());

    private static List<HEquation> Equations(HDocument document) => [.. HDocumentWalker.Inlines(document.Sections.SelectMany(s => s.Blocks).ToList()).OfType<HEquation>()];

    private static XDocument Entry(ZipArchive zip, string name)
    {
        using var stream = (zip.GetEntry(name) ?? throw new Xunit.Sdk.XunitException($"no {name}")).Open();
        return XDocument.Load(stream);
    }

    private static byte[] Bytes(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static bool IsPng(byte[] bytes) => bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G';

    // ───────────────────────── ODT and FODT ─────────────────────────

    [Fact]
    public void Odt_equations_are_formula_objects_with_mathml_and_a_replacement_picture()
    {
        var source = Markdown();
        var odt = Path.Combine(fx.NewFolder(), "math.odt");
        OdtWriter.Write(source, odt, Token);

        using var zip = ZipFile.OpenRead(odt);
        var content = Entry(zip, "content.xml");
        var frames = content.Descendants(Draw + "frame").Where(f => f.Element(Draw + "object") is not null).ToList();
        Assert.Equal(5, frames.Count);
        var manifest = Entry(zip, "META-INF/manifest.xml").Root!.Elements(Manifest + "file-entry")
            .ToDictionary(e => (string)e.Attribute(Manifest + "full-path")!, e => (string)e.Attribute(Manifest + "media-type")!);
        var displays = 0;
        foreach (var frame in frames)
        {
            // As LibreOffice Writer keeps a formula: in the text, on the baseline, the object's folder and its picture.
            Assert.Equal("as-char", (string?)frame.Attribute(XName.Get("anchor-type", "urn:oasis:names:tc:opendocument:xmlns:text:1.0")));
            Assert.StartsWith("-", (string?)frame.Attribute(XName.Get("y", "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0")));
            var folder = ((string)frame.Element(Draw + "object")!.Attribute(XLink + "href")!)[2..];
            Assert.Equal("application/vnd.oasis.opendocument.formula", manifest[folder + "/"]);
            Assert.Equal("text/xml", manifest[folder + "/content.xml"]);
            var math = Entry(zip, folder + "/content.xml").Root!;
            Assert.Equal(Mml + "math", math.Name);
            Assert.NotEmpty((string?)math.Attribute("alttext") ?? "");
            Assert.EndsWith("pt", (string?)math.Descendants(Mml + "mstyle").First().Attribute("mathsize"), StringComparison.Ordinal);
            if ((string?)math.Attribute("display") == "block")
                displays++;
            var picture = ((string)frame.Element(Draw + "image")!.Attribute(XLink + "href")!)[2..];
            Assert.Equal($"ObjectReplacements/{folder}", picture);
            Assert.Equal("image/png", manifest[picture]);
            Assert.True(IsPng(Bytes(zip, picture)));
        }
        Assert.Equal(3, displays);
        // No equation written as its text any more.
        Assert.DoesNotContain("\\frac", content.Root!.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("x^2", string.Concat(content.Descendants().Where(e => e.Name.LocalName == "p").Select(p => string.Concat(p.Nodes().OfType<XText>()))), StringComparison.Ordinal);

        // Read back: the same equations (their TeX kept as the annotation), not pictures.
        var read = OdtReader.Read(odt, fx.NewFolder());
        Assert.Equal(Equations(source).Select(e => EquationScript.Script(e.Math)), Equations(read).Select(e => EquationScript.Script(e.Math)));
        Assert.Equal(3, Equations(read).Count(e => e.Display));
        Assert.Empty(HDocumentWalker.Inlines(read.Sections.SelectMany(s => s.Blocks).ToList()).OfType<HImage>());
        Assert.DoesNotContain(read.LeftOut.Kinds, k => k.Kind is HLeftOutKind.FormulaAsText or HLeftOutKind.ObjectPictured);
    }

    [Fact]
    public void Word_equations_go_through_odt_mathml_and_come_back_the_same()
    {
        // No TeX: the equations are read from the MathML itself.
        var dir = fx.NewFolder();
        var docx = new DocxBuilder().Paragraph(P(R("Inline ") + "<m:oMath>" + MR("E=m") + "<m:sSup><m:e>" + MR("c") + "</m:e><m:sup>" + MR("2") + "</m:sup></m:sSup></m:oMath>"))
            .Paragraph(P("<m:oMathPara><m:oMath><m:f><m:num>" + MR("a+b") + "</m:num><m:den>" + MR("2c") + "</m:den></m:f>" + MR("=") +
                         "<m:rad><m:radPr><m:degHide m:val=\"1\"/></m:radPr><m:deg/><m:e>" + MR("x") + "</m:e></m:rad>" +
                         "<m:nary><m:naryPr><m:chr m:val=\"∑\"/></m:naryPr><m:sub>" + MR("i=1") + "</m:sub><m:sup>" + MR("n") + "</m:sup><m:e>" + MR("i") + "</m:e></m:nary>" +
                         MR("+") + "<m:d><m:e><m:m><m:mr><m:e>" + MR("1") + "</m:e><m:e>" + MR("2") + "</m:e></m:mr><m:mr><m:e>" + MR("3") + "</m:e><m:e>" + MR("4") + "</m:e></m:mr></m:m></m:e></m:d>" +
                         MR("+") + "<m:acc><m:e>" + MR("v") + "</m:e></m:acc>" + MR("+") + "<m:d><m:e>" + MR("t−1") + "</m:e></m:d>" +
                         "</m:oMath></m:oMathPara>", "<w:jc w:val=\"center\"/>"))
            .Save(Path.Combine(dir, "source.docx"));
        var source = DocxReader.Read(docx, Path.Combine(dir, "media"));
        var odt = Path.Combine(dir, "math.odt");
        OdtWriter.Write(source, odt, Token);

        var read = OdtReader.Read(odt, fx.NewFolder());
        Assert.Equal(Equations(source).Select(e => EquationScript.Script(e.Math)), Equations(read).Select(e => EquationScript.Script(e.Math)));
        Assert.All(Equations(read), e => Assert.Null(e.Tex));
    }

    private static string MR(string text) => $"<m:r><m:t>{System.Security.SecurityElement.Escape(text)}</m:t></m:r>";

    [Fact]
    public async Task Fodt_holds_the_mathml_in_its_draw_object_and_its_package_gets_the_folder_back()
    {
        var input = Path.Combine(fx.NewFolder(), "math.md");
        await File.WriteAllTextAsync(input, Formulas, Token);
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = "fodt" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));

        var flat = XDocument.Load(job.Outputs.Single());
        var objects = flat.Descendants(Draw + "object").ToList();
        Assert.Equal(5, objects.Count);
        // As LibreOffice writes a flat file: the math:math element in draw:object, the picture as binary data beside it.
        Assert.All(objects, o => Assert.Equal(Mml + "math", Assert.Single(o.Elements()).Name));
        Assert.All(objects, o => Assert.NotNull(o.Parent!.Element(Draw + "image")?.Element(Office + "binary-data")));
        Assert.All(objects, o => Assert.Null(o.Attribute(XLink + "href")));

        // FODT → ODT: each formula a folder again, read back as equations.
        var back = await fx.ConvertAsync([job.Outputs.Single()], new Preset { TargetFormat = "odt" });
        Assert.True(back.State == JobState.Completed, string.Join("; ", back.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        using (var zip = ZipFile.OpenRead(back.Outputs.Single()))
        {
            var manifest = Entry(zip, "META-INF/manifest.xml").Root!.Elements(Manifest + "file-entry").ToList();
            Assert.Equal(5, manifest.Count(e => (string?)e.Attribute(Manifest + "media-type") == "application/vnd.oasis.opendocument.formula"));
        }
        var read = OdtReader.Read(back.Outputs.Single(), fx.NewFolder());
        Assert.Equal(5, Equations(read).Count);
    }

    [Fact]
    public void Libreoffice_mathml_reads_as_equations()
    {
        // What LibreOffice Math writes: fences as stretchy prefix / postfix operators, a matrix in them, a sum with
        // munderover, sqrt, an accent, function application.
        var math = XElement.Parse(
            "<math xmlns=\"http://www.w3.org/1998/Math/MathML\" display=\"block\"><semantics><mrow>" +
            "<mrow><mo fence=\"true\" form=\"prefix\" stretchy=\"true\">(</mo><mtable><mtr><mtd><mn>1</mn></mtd><mtd><mn>2</mn></mtd></mtr>" +
            "<mtr><mtd><mn>3</mn></mtd><mtd><mn>4</mn></mtd></mtr></mtable><mo fence=\"true\" form=\"postfix\" stretchy=\"true\">)</mo></mrow>" +
            "<mo>+</mo><munderover><mo stretchy=\"false\">∑</mo><mrow><mi>i</mi><mo>=</mo><mn>1</mn></mrow><mi>n</mi></munderover><msqrt><mi>x</mi></msqrt>" +
            "<mo>+</mo><mover accent=\"true\"><mi>v</mi><mo>→</mo></mover><mo>+</mo><mi>sin</mi><mo>⁡</mo><mi>x</mi>" +
            "<mo>+</mo><mrow><mo fence=\"true\" form=\"prefix\" stretchy=\"true\">{</mo><mtable><mtr><mtd><mi>a</mi></mtd></mtr><mtr><mtd><mi>b</mi></mtd></mtr></mtable><mo fence=\"true\" form=\"postfix\" stretchy=\"true\"/></mrow>" +
            "</mrow><annotation encoding=\"StarMath 5.0\">left( matrix{1 # 2 ## 3 # 4} right) + sum from{i=1} to n sqrt x</annotation></semantics></math>");

        var formula = MathMLReader.Read(math)!;
        Assert.True(formula.Display);
        Assert.Null(formula.Tex);
        Assert.Equal("pmatrix {1 & 2 # 3 & 4} + SUM _{i = 1} ^{n} sqrt {x} + vec {v} + sin x + cases {a # b}", EquationScript.Script(formula.Math));
    }

    [Fact]
    public void Deep_mathml_is_read_flat_and_quickly()
    {
        var deep = new XElement(Mml + "math");
        var current = deep;
        for (var i = 0; i < 5_000; i++)
        {
            var next = new XElement(Mml + "mrow", new XElement(Mml + "mi", "x"));
            current.Add(next);
            current = next;
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        var formula = MathMLReader.Read(deep)!;
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(5_000, EquationScript.Linear(formula.Math).Count(c => c == 'x'));
    }

    // ───────────────────────── PPTX ─────────────────────────

    [Fact]
    public void Pptx_equations_are_office_math_with_a_picture_fallback()
    {
        var source = Markdown();
        var pptx = Path.Combine(fx.NewFolder(), "math.pptx");
        PptxWriter.Write(source, pptx, Token);
        PptxWriterTests.Valid(pptx);

        using (var zip = ZipFile.OpenRead(pptx))
        {
            var slides = zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$")).Select(e => (e.FullName, Xml: Entry(zip, e.FullName))).ToList();
            var alternates = slides.SelectMany(s => s.Xml.Descendants(Mc + "AlternateContent").Select(a => (s.FullName, Element: a))).ToList();
            Assert.NotEmpty(alternates);
            var maths = alternates.SelectMany(a => a.Element.Descendants(A14 + "m")).ToList();
            Assert.Equal(5, maths.Count);
            Assert.Equal(3, maths.Count(m => m.Element(M + "oMathPara") is not null));
            Assert.All(maths, m => Assert.Equal(A + "p", m.Parent!.Name));
            // PowerPoint's run properties in the math: a:rPr with the math font.
            Assert.All(maths, m => Assert.All(m.Descendants(M + "r"), r => Assert.Equal("Cambria Math", (string?)r.Element(A + "rPr")?.Element(A + "latin")?.Attribute("typeface"))));
            foreach (var (slide, alternate) in alternates)
            {
                Assert.Equal("a14", (string?)alternate.Element(Mc + "Choice")!.Attribute("Requires"));
                var fallback = alternate.Element(Mc + "Fallback")!.Element(P + "sp")!;
                Assert.Empty(fallback.Descendants(A14 + "m"));
                var blip = (string)fallback.Element(P + "spPr")!.Element(A + "blipFill")!.Element(A + "blip")!.Attribute(R + "embed")!;
                var rels = Entry(zip, slide.Replace("slides/", "slides/_rels/", StringComparison.Ordinal) + ".rels").Root!;
                var target = (string)rels.Elements().Single(e => (string?)e.Attribute("Id") == blip).Attribute("Target")!;
                Assert.True(IsPng(Bytes(zip, "ppt/" + target.Replace("../", "", StringComparison.Ordinal))));
            }
        }

        // Filee's own reader takes the equations from the choice: the same as Word math of them reads back.
        var read = PptxReader.Read(pptx, fx.NewFolder());
        var docx = Path.Combine(fx.NewFolder(), "math.docx");
        DocxWriter.Write(source, docx);
        var word = DocxReader.Read(docx, fx.NewFolder());
        Assert.Equal(Equations(word).Select(e => EquationScript.Script(e.Math)), Equations(read).Select(e => EquationScript.Script(e.Math)));
        Assert.Equal(3, Equations(read).Count(e => e.Display));
    }

    // ───────────────────────── EPUB and KF8 ─────────────────────────

    /// <summary>MathML 3 presentation elements (what epubcheck's MathML schema allows, without the deprecated ones Filee doesn't write).</summary>
    private static readonly HashSet<string> Presentation =
    [
        "math", "semantics", "annotation", "annotation-xml", "mrow", "mi", "mn", "mo", "mtext", "mspace", "ms", "mfrac", "msqrt", "mroot",
        "mstyle", "merror", "mpadded", "mphantom", "mfenced", "menclose", "msub", "msup", "msubsup", "munder", "mover", "munderover",
        "mmultiscripts", "mtable", "mtr", "mlabeledtr", "mtd", "maligngroup", "malignmark", "mprescripts", "none",
    ];

    [Fact]
    public async Task Epub_mathml_follows_epubcheck_rules()
    {
        var input = Path.Combine(fx.NewFolder(), "math.md");
        await File.WriteAllTextAsync(input, Formulas + "\n\n# Plain\n\nNo math here.\n", Token);
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = "epub" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));

        using var zip = ZipFile.OpenRead(job.Outputs.Single());
        var opfEntry = zip.Entries.Single(e => e.FullName.EndsWith(".opf", StringComparison.Ordinal));
        var opf = Entry(zip, opfEntry.FullName).Root!;
        var root = Path.GetDirectoryName(opfEntry.FullName)!.Replace('\\', '/');
        var pages = opf.Descendants().Where(e => e.Name.LocalName == "item" && (string?)e.Attribute("media-type") == "application/xhtml+xml").ToList();
        var withMath = 0;
        foreach (var item in pages)
        {
            var href = (root.Length > 0 ? root + "/" : "") + (string)item.Attribute("href")!;
            var page = Entry(zip, href); // well-formed
            var maths = page.Descendants().Where(e => e.Name.LocalName == "math").ToList();
            // OPF-014 / OPF-015: the mathml property if and only if the page has MathML.
            Assert.Equal(maths.Count > 0, ((string?)item.Attribute("properties"))?.Split(' ').Contains("mathml") == true);
            withMath += maths.Count > 0 ? 1 : 0;
            foreach (var math in maths)
            {
                Assert.Equal(Mml + "math", math.Name);
                Assert.NotEmpty((string?)math.Attribute("alttext") ?? ""); // for reading systems that speak it
                Assert.All(math.DescendantsAndSelf(), e => Assert.Equal(Mml.NamespaceName, e.Name.NamespaceName));
                Assert.All(math.DescendantsAndSelf(), e => Assert.Contains(e.Name.LocalName, Presentation));
                Assert.All(math.Descendants(Mml + "mfrac").Concat(math.Descendants(Mml + "mroot")).Concat(math.Descendants(Mml + "msub"))
                    .Concat(math.Descendants(Mml + "msup")), e => Assert.Equal(2, e.Elements().Count()));
                Assert.All(math.Descendants(Mml + "msubsup").Concat(math.Descendants(Mml + "munderover")), e => Assert.Equal(3, e.Elements().Count()));
                foreach (var semantics in math.Descendants(Mml + "semantics"))
                {
                    // The presentation first, annotations after it.
                    var children = semantics.Elements().ToList();
                    Assert.DoesNotContain(children[0].Name.LocalName, new[] { "annotation", "annotation-xml" });
                    Assert.All(children.Skip(1), c => Assert.Equal("annotation", c.Name.LocalName));
                }
                // Tokens hold text only.
                Assert.All(math.Descendants().Where(e => e.Name.LocalName is "mi" or "mn" or "mo" or "mtext"), e => Assert.False(e.HasElements));
            }
        }
        Assert.True(withMath >= 1);
        Assert.Contains(pages, p => ((string?)p.Attribute("properties"))?.Contains("mathml", StringComparison.Ordinal) != true);
    }

    [Fact]
    public void Kindle_books_show_equations_as_opaque_pictures()
    {
        var book = new Book(Markdown(), new BookMetadata { Title = "Formulas", Language = "en" });
        var path = Path.Combine(fx.NewFolder(), "math.azw3");
        MobiWriter.Write(book, path, path, fx.NewFolder(), mobi6: false);

        var read = MobiReader.ReadBook(path, fx.NewFolder(), Token).Document;
        var pictures = HDocumentWalker.Inlines(read.Sections.SelectMany(s => s.Blocks).ToList()).OfType<HImage>().ToList();
        Assert.Equal(5, pictures.Count);
        Assert.Empty(Equations(read));
        foreach (var picture in pictures)
        {
            // No transparency (Kindle readers show none), drawn at 300 ppi: several pixels per point.
            using var bitmap = SKBitmap.Decode(picture.Path);
            Assert.NotNull(bitmap);
            Assert.True(bitmap.Height >= 20, $"{bitmap.Height} px");
            Assert.All(new[] { bitmap.GetPixel(0, 0), bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1) }, c => Assert.Equal(SKColors.White, c));
        }
        // The text around them stays.
        Assert.Contains("Inline", HDocumentWalker.Text(HDocumentWalker.Inlines(read.Sections.SelectMany(s => s.Blocks).ToList())), StringComparison.Ordinal);
    }

    [Fact]
    public void Kindle_pictures_are_sized_in_em_with_the_linear_text_as_their_alt()
    {
        using var pictures = new EquationPicture();
        var folder = fx.NewFolder();
        var chapters = XhtmlWriter.Write(Markdown(), new XhtmlOptions
        {
            ImageSource = path => Path.GetFileName(path),
            MathPictures = (equation, display) =>
            {
                var image = pictures.Draw(equation, display, opaque: true)!;
                var file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png");
                File.WriteAllBytes(file, image.Png);
                return (file, image);
            },
        });
        var body = chapters.Single().Body;
        var images = Regex.Matches(body, "<img [^>]*class=\"math\"[^>]*/>").Select(m => m.Value).ToList();
        Assert.Equal(5, images.Count);
        Assert.All(images, i => Assert.Matches("style=\"width:[0-9.]+em;vertical-align:-[0-9.]+em\"", i));
        Assert.Contains(images, i => i.Contains("alt=\"x^2+y^2=r^2\"", StringComparison.Ordinal));
        Assert.DoesNotContain("<math", body, StringComparison.Ordinal);
    }

    // ───────────────────────── Word's wrap settings ─────────────────────────

    /// <summary>A display equation wider than the page: y = e^-1 x + e^-2 x … − x.</summary>
    private static string WideOmml(int terms = 30) =>
        "<m:oMathPara><m:oMath>" + MR("y=") + string.Concat(Enumerable.Range(1, terms).Select(i =>
            MR(i % 3 == 0 ? "−" : "+") + $"<m:sSup><m:e>{MR("e")}</m:e><m:sup>{MR("-" + i)}</m:sup></m:sSup>" + MR("x"))) + "</m:oMath></m:oMathPara>";

    [Fact]
    public void A_wide_word_display_equation_stays_whole_with_the_documents_wrap_settings()
    {
        var dir = fx.NewFolder();
        var docx = new DocxBuilder
        {
            Settings = "<m:mathPr><m:brkBin m:val=\"repeat\"/><m:brkBinSub m:val=\"-+\"/><m:wrapIndent m:val=\"720\"/><m:wrapRight/></m:mathPr>",
        }.Paragraph(P(WideOmml(), "<w:jc w:val=\"center\"/>"));
        var document = DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media"));

        var paragraph = Assert.Single(document.Sections.Single().Blocks.OfType<HParagraph>());
        var equation = Assert.IsType<HEquation>(Assert.Single(paragraph.Inlines));
        Assert.True(equation.Display);
        Assert.Equal(new HMathWrap(HMathBreak.Repeat, "-+", 3600, Right: true), equation.Wrap);

        // Without settings: Word's defaults (none stored).
        var plain = DocxReader.Read(new DocxBuilder().Paragraph(P(WideOmml())).Save(Path.Combine(dir, "plain.docx")), Path.Combine(dir, "media2"));
        Assert.Null(Assert.IsType<HEquation>(Assert.Single(Assert.IsType<HParagraph>(plain.Sections.Single().Blocks.Single()).Inlines)).Wrap);
    }

    [Fact]
    public void A_wide_word_display_equation_breaks_into_paragraphs_in_hwpx_only()
    {
        var dir = fx.NewFolder();
        var docx = new DocxBuilder().Paragraph(P(WideOmml(), "<w:spacing w:before=\"100\" w:after=\"200\"/><w:jc w:val=\"center\"/>"));
        var document = DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media"));
        var hwpx = Path.Combine(dir, "out.hwpx");
        HwpxWriter.Write(document, hwpx, cancellationToken: Token);

        using var zip = ZipFile.OpenRead(hwpx);
        XNamespace hp = "http://www.hancom.co.kr/hwpml/2011/paragraph";
        var section = Entry(zip, "Contents/section0.xml");
        var equations = section.Descendants(hp + "equation").ToList();
        Assert.True(equations.Count > 1, $"{equations.Count} equations");
        Assert.All(equations, e => Assert.Single(e.Ancestors(hp + "p").First().Descendants(hp + "equation")));
        var width = HwpxUnits.FromTwips(11906 - 2880);
        Assert.All(equations, e => Assert.True((int)e.Element(hp + "sz")!.Attribute("width")! <= width, $"{e.Element(hp + "sz")}"));
        Assert.StartsWith("y =", equations[0].Element(hp + "script")!.Value, StringComparison.Ordinal);
        Assert.All(equations.Skip(1), e => Assert.Matches("^[+-]", e.Element(hp + "script")!.Value));
    }

    private (MathLayout Math, List<List<GlyphRun>> Lines) Wrapped(HMathWrap? wrap, float width)
    {
        var fonts = RepositoryFonts.Load();
        Assert.SkipUnless(fonts.Has(RepositoryFonts.Latin), "Noto Sans is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        var math = new MathLayout(new FontResolver(fonts), _shaper);
        var tree = LatexMath.Parse(@"a+b+c-d+e+f-g+h+i-j+k+l-m+n+o-p+q+r-s+t+u")!;
        var box = math.Layout(tree, 12, SKColors.Black, MathMode.Display, width, wrap);
        Assert.True(box.Width <= width + 0.5f, $"{box.Width}");
        var page = new PageContent(600, 600);
        box.Paint(page, 0, 100, 1);
        var lines = page.Items.OfType<GlyphItem>().Select(i => i.Run).GroupBy(r => MathF.Round(r.Positions[0].Y)).OrderBy(g => g.Key)
            .Select(g => g.OrderBy(r => r.Positions[0].X).ToList()).ToList();
        Assert.True(lines.Count >= 3, $"{lines.Count} lines");
        return (math, lines);
    }

    private static string Plain(GlyphRun run) => WordEquationLayoutTests.Plain(run.Text);

    [Fact]
    public void Wrap_settings_move_and_repeat_the_operator_at_a_break()
    {
        // Before (Word's default): the operator starts the next line.
        var (_, before) = Wrapped(null, 90);
        Assert.All(before.Skip(1), line => Assert.Matches("^[+−]", Plain(line[0])));

        // After: it ends the line.
        var (_, after) = Wrapped(new HMathWrap(HMathBreak.After), 90);
        Assert.All(after.SkipLast(1), line => Assert.Matches("[+−]$", Plain(line[^1])));
        Assert.All(after.Skip(1), line => Assert.DoesNotMatch("^[+−]", Plain(line[0])));

        // Repeat: on both lines; a minus as "-+" says, minus first and plus after.
        var (_, repeat) = Wrapped(new HMathWrap(HMathBreak.Repeat, "-+"), 90);
        for (var i = 0; i + 1 < repeat.Count; i++)
        {
            var (end, start) = (Plain(repeat[i][^1]), Plain(repeat[i + 1][0]));
            Assert.Matches("[+−]$", end);
            Assert.Equal("+", start[..1]);
        }
    }

    [Fact]
    public void A_line_breaks_inside_brackets_too_wide_for_one()
    {
        var fonts = RepositoryFonts.Load();
        Assert.SkipUnless(fonts.Has(RepositoryFonts.Latin), "Noto Sans is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        var math = new MathLayout(new FontResolver(fonts), _shaper);
        var tree = LatexMath.Parse(@"y=\left(a+b+c+d+e+f+g+h+i+j+k+l+m+n+o+p\right)")!;
        const float Width = 100;
        var box = math.Layout(tree, 12, SKColors.Black, MathMode.Display, Width);
        Assert.True(box.Width <= Width + 0.5f, $"{box.Width}");
        var page = new PageContent(600, 600);
        box.Paint(page, 0, 100, 1);
        var runs = page.Items.OfType<GlyphItem>().Select(i => i.Run).ToList();
        var lines = runs.Select(r => MathF.Round(r.Positions[0].Y)).Distinct().Order().ToList();
        Assert.True(lines.Count >= 3, $"{lines.Count} lines");
        // The opening bracket on the first line, the closing one on the last, the letters between them in order.
        var letters = runs.Where(r => Plain(r).Length == 1 && char.IsAsciiLetter(Plain(r)[0])).ToList();
        Assert.Equal(MathF.Round(letters.Single(r => Plain(r) == "y").Positions[0].Y), lines[0]);
        Assert.Equal(MathF.Round(letters.Single(r => Plain(r) == "p").Positions[0].Y), lines[^1]);
    }

    [Fact]
    public void Wrap_settings_set_the_indent_or_put_the_lines_to_the_right()
    {
        var (_, indented) = Wrapped(new HMathWrap(Indent: 1000), 90); // 10 pt
        Assert.All(indented.Skip(1), line => Assert.Equal(10, line[0].Positions[0].X, 0.5));

        var (_, right) = Wrapped(new HMathWrap(Right: true), 90);
        var ends = right.Skip(1).Select(line => line[^1].Positions[^1].X).ToList();
        Assert.True(ends.Max() - ends.Min() < 8, string.Join(", ", ends));
        Assert.Contains(right.Skip(1), line => line[0].Positions[0].X > 1);
    }
}
