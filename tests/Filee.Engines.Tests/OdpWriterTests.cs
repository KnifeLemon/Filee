// Filee's OpenDocument presentation writer (Odf/Draw/OdpWriter): PowerPoint decks built in code (SlideDeckBuilder) read
// into the slide model, written as ODP and read back with Filee's own ODP reader, so both decks are compared shape by
// shape and as pictures drawn by the slide renderer. Also the package rules (mimetype first and stored, the manifest),
// master pages, lists, notes, the converter's routes and conversions, and hostile models. Nothing is read from disk
// but what the tests write.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Odf.Draw;
using Filee.Engines.Office;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Tests;

public sealed class OdpWriterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace Presentation = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";
    private static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// A deck with what the writer writes: a title slide, bullets on two levels and numbers, a right-to-left paragraph,
    /// shapes (filled, turned, mirrored, a gradient, a dashed arrow), a cropped picture, a table and a group; every
    /// slide has the master's bar.
    /// </summary>
    internal static SlideDeckBuilder Sample()
    {
        var b = new SlideDeckBuilder();
        var picture = b.Media(SlideDeckBuilder.TestPicture());
        b.Slide(b.Placeholder("ctrTitle", SlideDeckBuilder.Paragraph("Built-in engines")) +
                b.Placeholder("subTitle", SlideDeckBuilder.Paragraph("ODP without LibreOffice"), 1), SlideDeckBuilder.Layout.Title);
        b.Slide(b.Placeholder("title", SlideDeckBuilder.Paragraph("Agenda")) +
                b.Placeholder("body",
                    SlideDeckBuilder.Paragraph("Readers") +
                    SlideDeckBuilder.Paragraph("ODP and ODG", "<a:pPr lvl=\"1\"/>") +
                    SlideDeckBuilder.Paragraph("Writers") +
                    SlideDeckBuilder.Paragraph("First step", "<a:pPr marL=\"342900\" indent=\"-342900\"><a:buFont typeface=\"+mj-lt\"/><a:buAutoNum type=\"arabicPeriod\"/></a:pPr>") +
                    SlideDeckBuilder.Paragraph("Second step", "<a:pPr marL=\"342900\" indent=\"-342900\"><a:buFont typeface=\"+mj-lt\"/><a:buAutoNum type=\"arabicPeriod\"/></a:pPr>") +
                    SlideDeckBuilder.Paragraph("مرحبا بالعالم", "<a:pPr marL=\"0\" indent=\"0\" algn=\"r\" rtl=\"1\"><a:buNone/></a:pPr>"), 1),
            SlideDeckBuilder.Layout.Content);
        const string Gradient = "<a:gradFill><a:gsLst><a:gs pos=\"0\"><a:srgbClr val=\"C62828\"/></a:gs><a:gs pos=\"100000\"><a:srgbClr val=\"1565C0\"/></a:gs></a:gsLst><a:lin ang=\"0\" scaled=\"0\"/></a:gradFill>";
        const string Dashed = "<a:ln w=\"38100\"><a:solidFill><a:srgbClr val=\"263238\"/></a:solidFill><a:prstDash val=\"dash\"/><a:tailEnd type=\"triangle\"/></a:ln>";
        b.Slide(
            b.Shape("roundRect", 40, 40, 220, 110, SlideDeckBuilder.Solid("#2E7D32"), paragraphs: SlideDeckBuilder.Paragraph("Shapes")) +
            b.Shape("rightArrow", 300, 50, 200, 90, SlideDeckBuilder.Solid("#F9A825"), rotation: 30) +
            b.Shape("ellipse", 560, 40, 180, 120, Gradient, "<a:ln><a:noFill/></a:ln>", styled: false) +
            b.Shape("rtTriangle", 780, 40, 120, 120, SlideDeckBuilder.Solid("#6A1B9A"), flipH: true) +
            b.Connector("straightConnector1", 40, 200, 300, 60, Dashed) +
            b.Picture("rIdP", 380, 190, 240, 150, "<a:srcRect l=\"10000\" r=\"20000\"/>") +
            b.Table(660, 200, [120, 120], 40, [SlideDeckBuilder.Cell("A1") + SlideDeckBuilder.Cell("B1"), SlideDeckBuilder.Cell("A2") + SlideDeckBuilder.Cell("B2")]) +
            b.Group(40, 360, 300, 140, 1,
                b.Shape("rect", 0, 0, 140, 140, SlideDeckBuilder.Solid("#00838F")) + b.Shape("ellipse", 160, 0, 140, 140, SlideDeckBuilder.Solid("#AD1457"))),
            pictures: new Dictionary<string, string> { ["rIdP"] = picture });
        return b;
    }

    private string Save(SlideDeckBuilder builder, string name = "deck.pptx") => builder.Save(Path.Combine(fx.NewFolder(), name));

    private static SlideDeck ReadPptx(string path) => PptxSlideReader.Read(path, cancellationToken: Token);

    private static string Write(SlideDeck deck, string folder, IReadOnlyList<IReadOnlyList<string>>? notes = null)
    {
        var path = Path.Combine(folder, $"deck-{Guid.NewGuid():N}.odp");
        OdpWriter.Write(deck, path, notes, cancellationToken: Token);
        return path;
    }

    private static XElement Part(string odp, string name)
    {
        using var zip = ZipFile.OpenRead(odp);
        using var stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream).Root!;
    }

    /// <summary>The leaves of a slide, groups opened.</summary>
    private static List<SlideElement> Leaves(IEnumerable<SlideElement> elements) =>
        elements.SelectMany(e => e is SlideGroup group ? Leaves(group.Children) : [e]).ToList();

    /// <summary>The leaves with their boxes on the page (groups' child spaces mapped, their turns left aside).</summary>
    private static List<(SlideElement Element, SKRect Box)> Placed(IEnumerable<SlideElement> elements, SKMatrix parent)
    {
        var result = new List<(SlideElement, SKRect)>();
        foreach (var element in elements)
        {
            if (element is SlideGroup group)
            {
                var (box, child) = (group.Bounds, group.ChildBounds);
                var inner = parent.PreConcat(SKMatrix.CreateTranslation(box.Left, box.Top))
                    .PreConcat(SKMatrix.CreateScale(child.Width > 0 ? box.Width / child.Width : 1, child.Height > 0 ? box.Height / child.Height : 1))
                    .PreConcat(SKMatrix.CreateTranslation(-child.Left, -child.Top));
                result.AddRange(Placed(group.Children, inner));
            }
            else
            {
                result.Add((element, parent.MapRect(element.Bounds)));
            }
        }
        return result;
    }

    private static string Words(SlideTextFrame? frame) =>
        frame is null ? "" : string.Join("\n", frame.Paragraphs.Select(p => string.Concat(p.Inlines.Select(i => i switch
        {
            HText t => t.Text,
            HTab => "\t",
            HLink link => string.Concat(link.Content.OfType<HText>().Select(t => t.Text)),
            _ => "",
        }))));

    private static SKBitmap Picture(SlideDeck deck, int slide, int dpi = 48) => SlideOutput.ToBitmaps(deck, dpi, [slide], Token).Single();

    /// <summary>The share of pixels whose colour differs by more than <paramref name="tolerance"/> in a channel.</summary>
    private static double Differs(SKBitmap a, SKBitmap b, int tolerance = 48)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        var differ = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var (p, q) = (a.GetPixel(x, y), b.GetPixel(x, y));
                if (Math.Abs(p.Red - q.Red) > tolerance || Math.Abs(p.Green - q.Green) > tolerance || Math.Abs(p.Blue - q.Blue) > tolerance)
                    differ++;
            }
        }
        return differ / (double)(a.Width * a.Height);
    }

    // ───────────────────────── Package ─────────────────────────

    [Fact]
    public void The_package_has_its_mimetype_first_and_stored_and_every_part_in_the_manifest()
    {
        var odp = Write(ReadPptx(Save(Sample())), fx.NewFolder());

        // The type at a fixed offset: the first local header, stored (method 0), no extra field, then the text.
        var bytes = File.ReadAllBytes(odp);
        Assert.Equal("PK\u0003\u0004", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(8, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28)));
        Assert.Equal("mimetype", Encoding.ASCII.GetString(bytes, 30, 8));
        Assert.Equal("application/vnd.oasis.opendocument.presentation", Encoding.ASCII.GetString(bytes, 38, 47));

        using var zip = ZipFile.OpenRead(odp);
        Assert.Equal("mimetype", zip.Entries[0].FullName);
        var manifest = Part(odp, "META-INF/manifest.xml");
        var entries = manifest.Elements(Manifest + "file-entry").ToDictionary(e => (string)e.Attribute(Manifest + "full-path")!, e => (string)e.Attribute(Manifest + "media-type")!);
        Assert.Equal("application/vnd.oasis.opendocument.presentation", entries["/"]);
        Assert.Equal("1.3", (string?)manifest.Attribute(Manifest + "version"));
        foreach (var entry in zip.Entries.Where(e => e.FullName is not ("mimetype" or "META-INF/manifest.xml")))
            Assert.True(entries.ContainsKey(entry.FullName), $"{entry.FullName} is not in the manifest");
        foreach (var path in entries.Keys.Where(p => p != "/"))
            Assert.NotNull(zip.GetEntry(path));
        Assert.Contains(entries, e => e.Key.StartsWith("Pictures/", StringComparison.Ordinal) && e.Value == "image/png");

        // Every XML part parses and says ODF 1.3; styles a shape names exist.
        foreach (var name in new[] { "content.xml", "styles.xml", "meta.xml" })
            Assert.Equal("1.3", (string?)Part(odp, name).Attribute(Office + "version"));
        var content = Part(odp, "content.xml");
        var styles = Part(odp, "styles.xml");
        var defined = content.Descendants(Style + "style").Concat(styles.Descendants(Style + "style")).Select(s => (string)s.Attribute(Style + "name")!).ToHashSet();
        foreach (var named in content.Descendants().Attributes(Draw + "style-name"))
            Assert.Contains(named.Value, defined);
        Assert.Equal("presentation", content.Element(Office + "body")!.Elements().Single().Name.LocalName);
    }

    // ───────────────────────── Round trip ─────────────────────────

    [Fact]
    public void A_deck_read_back_from_its_odp_has_the_same_shapes_text_and_picture()
    {
        var deck = ReadPptx(Save(Sample()));
        var odp = Write(deck, fx.NewFolder());
        var back = OdfDrawReader.Read(odp, cancellationToken: Token);

        Assert.Equal(deck.Width, back.Width, 0.05f);
        Assert.Equal(deck.Height, back.Height, 0.05f);
        Assert.Equal(deck.Slides.Count, back.Slides.Count);
        for (var s = 0; s < deck.Slides.Count; s++)
        {
            var before = Placed(deck.Slides[s].Elements, SKMatrix.Identity);
            var after = Placed(back.Slides[s].Elements, SKMatrix.Identity);
            Assert.Equal(before.Count, after.Count);
            for (var i = 0; i < before.Count; i++)
            {
                var ((a, boxA), (b, boxB)) = (before[i], after[i]);
                Assert.Equal(a.GetType(), b.GetType());
                // The centre stays; a box turned by draw:transform keeps its size.
                Assert.Equal(boxA.MidX, boxB.MidX, 0.5f);
                Assert.Equal(boxA.MidY, boxB.MidY, 0.5f);
                Assert.Equal(boxA.Width, boxB.Width, 0.5f);
                Assert.Equal(boxA.Height, boxB.Height, 0.5f);
                if (a is SlideShape shape)
                    Assert.Equal(Words(shape.Text), Words(((SlideShape)b).Text));
            }

            // As pictures: within a few edge pixels of each other.
            using var original = Picture(deck, s);
            using var written = Picture(back, s);
            var share = Differs(original, written);
            Assert.True(share < 0.01, $"slide {s + 1}: {share:P2} of the pixels differ");
        }

        // The arrow keeps its turn, the triangle its mirror, the line its dash and arrowhead.
        var shapes = Leaves(back.Slides[2].Elements).OfType<SlideShape>().ToList();
        Assert.Contains(shapes, e => Math.Abs(e.Rotation - 30) < 0.1f);
        var line = Assert.Single(shapes, e => e.Line?.Dash is not null);
        Assert.Equal(LineEndKind.Triangle, line.Line!.Tail.Kind);
        Assert.Equal(3, line.Line.Width, 0.05f);
        Assert.Contains(shapes, e => e.Fill is GradientFill);
        Assert.Contains(shapes, e => e.IsPicture);
        Assert.Single(Leaves(back.Slides[2].Elements).OfType<SlideTable>());
    }

    [Fact]
    public void Bullets_and_numbers_become_lists_and_read_back_as_the_same_text()
    {
        var deck = ReadPptx(Save(Sample()));
        var odp = Write(deck, fx.NewFolder());
        var content = Part(odp, "content.xml");
        var page = content.Descendants(Draw + "page").ElementAt(1);

        var items = page.Descendants(Text + "list-item").Where(i => i.Element(Text + "p") is not null).ToList();
        Assert.Equal(5, items.Count);
        Assert.Equal(["1", "2"], items.Select(i => (string?)i.Attribute(Text + "start-value")).OfType<string>());
        // "ODP and ODG" is indented further: a level deeper, inside list items of its list.
        var nested = items.Single(i => i.Value == "ODP and ODG");
        Assert.True(nested.Ancestors(Text + "list").Count() > items.Single(i => i.Value == "Readers").Ancestors(Text + "list").Count());
        var styles = content.Descendants(Text + "list-style").ToList();
        Assert.Contains(styles, s => s.Descendants(Text + "list-level-style-bullet").Any(l => (string?)l.Attribute(Text + "bullet-char") == "•"));
        Assert.Contains(styles, s => s.Descendants(Text + "list-level-style-number").Any(l => (string?)l.Attribute(Style + "num-format") == "1" && (string?)l.Attribute(Style + "num-suffix") == "."));
        // Impress sizes the label by fo:font-size in the level's text properties and adds text:space-before to the
        // paragraph's margin, which already holds the indent.
        var levels = styles.SelectMany(s => s.Elements()).ToList();
        Assert.All(levels, l => Assert.EndsWith("%", (string?)l.Element(Style + "text-properties")?.Attribute(Fo + "font-size")));
        Assert.All(levels, l => Assert.Equal("0pt", (string?)l.Element(Style + "list-level-properties")?.Attribute(Text + "space-before")));

        // The right-to-left paragraph is written so, and comes back so.
        var rtl = content.Descendants(Style + "paragraph-properties").Where(p => (string?)p.Attribute(Style + "writing-mode") == "rl-tb");
        Assert.NotEmpty(rtl);

        var back = OdfDrawReader.Read(odp, cancellationToken: Token);
        var body = back.Slides[1].Elements.OfType<SlideShape>().Single(s => Words(s.Text).Contains("Readers", StringComparison.Ordinal));
        var expected = deck.Slides[1].Elements.OfType<SlideShape>().Single(s => Words(s.Text).Contains("Readers", StringComparison.Ordinal));
        Assert.Equal(Words(expected.Text), Words(body.Text));
        Assert.Contains("1.\tFirst step", Words(body.Text), StringComparison.Ordinal);
        Assert.True(body.Text!.Paragraphs.Last().Format.RightToLeft);
    }

    [Fact]
    public void Shapes_every_slide_starts_with_go_on_a_master_page()
    {
        var deck = ReadPptx(Save(Sample()));
        var odp = Write(deck, fx.NewFolder());
        var styles = Part(odp, "styles.xml");
        var content = Part(odp, "content.xml");

        var master = Assert.Single(styles.Descendants(Style + "master-page"));
        // The master's bar, behind every slide; the title and outline placeholders Impress gives a master.
        Assert.Contains(master.Elements(Draw + "custom-shape"), s => (string?)s.Attribute(Draw + "layer") == "backgroundobjects");
        Assert.Equal(["title", "outline"], master.Elements(Draw + "frame").Select(f => (string?)f.Attribute(Presentation + "class")));
        var layout = styles.Descendants(Style + "page-layout-properties").First();
        Assert.Equal("960pt", (string?)layout.Attribute(XName.Get("page-width", "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0")));
        Assert.All(content.Descendants(Draw + "page"), p => Assert.Equal((string?)master.Attribute(Style + "name"), (string?)p.Attribute(Draw + "master-page-name")));
        // The bar is on no page of its own.
        var pages = content.Descendants(Draw + "page").ToList();
        Assert.Equal(deck.Slides.Sum(s => s.Elements.Count) - deck.Slides.Count * master.Elements(Draw + "custom-shape").Count(),
            pages.Sum(p => p.Elements().Count(e => e.Name.Namespace == Draw)));
    }

    [Fact]
    public void Speaker_notes_go_on_the_notes_pages()
    {
        var b = new SlideDeckBuilder();
        const string Notes = "<p:notes xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">" +
                             "<p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>" +
                             "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Notes\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
                             "<p:txBody><a:bodyPr/><a:p><a:r><a:t>Say hello</a:t></a:r><a:br/><a:r><a:t>then  wave</a:t></a:r></a:p><a:p><a:r><a:t>Thank them</a:t></a:r></a:p><a:p/></p:txBody></p:sp>" +
                             "</p:spTree></p:cSld></p:notes>";
        b.Part("ppt/notesSlides/notesSlide1.xml", Notes, "presentationml.notesSlide");
        b.Slide(b.TextBox(40, 40, 400, 100, SlideDeckBuilder.Paragraph("With notes")),
            relationships: [("rId9", "notesSlide", "../notesSlides/notesSlide1.xml")]);
        b.Slide(b.TextBox(40, 40, 400, 100, SlideDeckBuilder.Paragraph("Hidden")), hidden: true);
        b.Slide(b.TextBox(40, 40, 400, 100, SlideDeckBuilder.Paragraph("Without")));
        var pptx = Save(b);

        var notes = PptxNotes.Read(pptx, cancellationToken: Token);
        Assert.Equal(2, notes.Count);
        Assert.Equal(["Say hello", "then  wave", "Thank them"], notes[0]);
        Assert.Empty(notes[1]);

        var odp = Write(ReadPptx(pptx), fx.NewFolder(), notes);
        var pages = Part(odp, "content.xml").Descendants(Draw + "page").ToList();
        var first = pages[0].Element(Presentation + "notes")!;
        Assert.Equal(1, (int?)first.Element(Draw + "page-thumbnail")!.Attribute(Draw + "page-number"));
        var frame = first.Element(Draw + "frame")!;
        Assert.Equal("notes", (string?)frame.Attribute(Presentation + "class"));
        // Two spaces: one, then text:s for the one ODF would collapse.
        Assert.Equal(["Say hello", "then wave", "Thank them"], frame.Descendants(Text + "p").Select(p => p.Value));
        Assert.Single(frame.Descendants(Text + "s"));
        Assert.Equal("true", (string?)pages[1].Element(Presentation + "notes")!.Element(Draw + "frame")!.Attribute(Presentation + "placeholder"));
    }

    // ───────────────────────── Fills and lines ─────────────────────────

    [Fact]
    public void Fills_and_lines_come_back_as_the_model_has_them()
    {
        var deck = new SlideDeck { Width = 400, Height = 300 };
        var slide = new Slide { Background = new SolidFill(new SKColor(0xEC, 0xEF, 0xF1)) };
        deck.Slides.Add(slide);
        SlideShape Box(float x, SlideFill fill, SlideLine? line = null)
        {
            var path = new SKPath();
            path.AddRect(new SKRect(0, 0, 80, 80));
            var shape = new SlideShape { Bounds = SKRect.Create(x, 20, 80, 80), Paths = [new SlidePath(path)], Fill = fill, Line = line };
            slide.Elements.Add(shape);
            return shape;
        }
        Box(10, new SolidFill(new SKColor(0x10, 0x20, 0x30, 0x80)));
        Box(100, new GradientFill([new(0, SKColors.Red), new(1, SKColors.Blue)], GradientShape.Linear, 90, false, SKRect.Empty));
        Box(190, new GradientFill([new(0, SKColors.White), new(1, SKColors.Black)], GradientShape.Circle, 0, false, new SKRect(0.5f, 0.5f, 0.5f, 0.5f)));
        Box(280, new PatternFill("dnDiag", SKColors.Black, SKColors.Yellow),
            new SlideLine(new SolidFill(SKColors.Green), 4, [3, 1], SKStrokeCap.Round, SKStrokeJoin.Miter, new LineEnd(LineEndKind.Oval, 3, 3), new LineEnd(LineEndKind.Stealth, 5, 5)));

        var odp = Write(deck, fx.NewFolder());
        // Angles carry their unit: LibreOffice reads a bare number as degrees in files it didn't write.
        var named = Part(odp, "styles.xml").Descendants().Concat(Part(odp, "content.xml").Descendants()).ToList();
        Assert.Equal("0deg", named.Where(e => e.Name == Draw + "gradient" && (string?)e.Attribute(Draw + "style") == "linear").Select(e => (string?)e.Attribute(Draw + "angle")).Single());
        Assert.Equal("135deg", named.Where(e => e.Name == Draw + "hatch").Select(e => (string?)e.Attribute(Draw + "rotation")).Single());

        var back = OdfDrawReader.Read(odp, cancellationToken: Token);
        var shapes = back.Slides[0].Elements.OfType<SlideShape>().ToList();
        Assert.Equal(new SKColor(0xEC, 0xEF, 0xF1), Assert.IsType<SolidFill>(back.Slides[0].Background).Color);
        Assert.Equal(new SKColor(0x10, 0x20, 0x30, 0x80), Assert.IsType<SolidFill>(shapes[0].Fill).Color);
        var linear = Assert.IsType<GradientFill>(shapes[1].Fill);
        Assert.Equal(90, linear.Angle, 0.5f);
        Assert.Equal(SKColors.Red, linear.Stops[0].Color);
        Assert.Equal(SKColors.Blue, linear.Stops[^1].Color);
        var radial = Assert.IsType<GradientFill>(shapes[2].Fill);
        Assert.Equal(GradientShape.Circle, radial.Shape);
        Assert.Equal(SKColors.White, radial.Stops[0].Color);
        Assert.Equal("dnDiag", Assert.IsType<PatternFill>(shapes[3].Fill).Preset);
        var line = shapes[3].Line!;
        Assert.Equal(4, line.Width, 0.05f);
        Assert.Equal(SKColors.Green, Assert.IsType<SolidFill>(line.Fill).Color);
        Assert.Equal(SKStrokeJoin.Miter, line.Join);
        Assert.Equal(SKStrokeCap.Round, line.Cap);
        Assert.Equal([3, 1], line.Dash!.Take(2));
        Assert.Equal(LineEndKind.Oval, line.Head.Kind);
        Assert.Equal(LineEndKind.Stealth, line.Tail.Kind);
        Assert.Equal(5, line.Tail.Width, 0.1f);
    }

    // ───────────────────────── Routes and conversions ─────────────────────────

    [Fact]
    public void Decks_and_documents_reach_odp_through_the_writer_and_odp_keeps_libreoffices_routes()
    {
        var planner = fx.Catalog.CreatePlanner(fx.Converters.Select(c => c.Id).ToArray());
        foreach (var from in new[] { "pptx", "pptm", "potx", "ppsx", "ppt", "md", "docx", "odt", "html", "txt", "hwpx" })
            Assert.Equal("odp-writer", Assert.Single(planner.Plan(from, "odp")!.Steps).Converter.Id);
        var writer = fx.Converters.OfType<OdpWriterConverter>().Single();
        Assert.All(writer.Edges, e => Assert.True(e.DirectOnly));
        Assert.All(writer.Edges.Where(e => OdpWriterConverter.Decks.Contains(e.From)), e => Assert.True(e.Checked));
        Assert.DoesNotContain(writer.Edges, e => e.From is "odp" or "otp" or "fodp" or "pdf");
        // ODP → PPTX stays LibreOffice's; a document → PDF never goes through slides.
        Assert.DoesNotContain(planner.Plan("odp", "pptx")?.Steps ?? [], s => s.Converter.Id == "odp-writer");
        Assert.DoesNotContain(planner.Plan("md", "pdf")!.Steps, s => s.To == "odp");
    }

    [Fact]
    public async Task A_pptx_and_a_markdown_file_convert_to_odp()
    {
        var folder = fx.NewFolder();
        var job = await fx.ConvertAsync([Save(Sample())], new Preset { TargetFormat = "odp" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var deck = OdfDrawReader.Read(job.Outputs.Single(), cancellationToken: Token);
        Assert.Equal(3, deck.Slides.Count);

        var markdown = Path.Combine(folder, "talk.md");
        await File.WriteAllTextAsync(markdown, "# Plan\n\n- read\n- write\n\n::: notes\nSpeak slowly\n:::\n\n# Done\n\nThanks.\n", Token);
        job = await fx.ConvertAsync([markdown], new Preset { TargetFormat = "odp" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var odp = job.Outputs.Single();
        var slides = OdfDrawReader.Read(odp, cancellationToken: Token).Slides;
        Assert.Equal(2, slides.Count);
        Assert.Contains(slides[0].Elements.OfType<SlideShape>(), s => Words(s.Text) == "Plan");
        Assert.Contains(slides[0].Elements.OfType<SlideShape>(), s => Words(s.Text).Contains("•\tread", StringComparison.Ordinal));
        Assert.Contains("Speak slowly", Part(odp, "content.xml").Descendants(Presentation + "notes").First().Value, StringComparison.Ordinal);
    }

    // ───────────────────────── Hostile models ─────────────────────────

    [Fact]
    public void Text_xml_cannot_hold_is_left_out_and_the_file_still_reads()
    {
        var deck = new SlideDeck { Width = 400, Height = 300, Title = "Bad \u0001 title \uD800" };
        var slide = new Slide();
        deck.Slides.Add(slide);
        var frame = new SlideTextFrame();
        var paragraph = new HParagraph();
        paragraph.Inlines.Add(new HText("a\u0000b\u0008<&>\"'\uDC00c￾😀  d", default));
        var link = new HLink("https://example.com/?a=1&b=\"2\"");
        link.Content.Add(new HText("link", default));
        paragraph.Inlines.Add(link);
        frame.Paragraphs.Add(paragraph);
        var path = new SKPath();
        path.AddRect(new SKRect(0, 0, 100, 50));
        slide.Elements.Add(new SlideShape { Bounds = SKRect.Create(10, 10, 100, 50), Paths = [new SlidePath(path)], Text = frame });

        var odp = Write(deck, fx.NewFolder());
        var back = OdfDrawReader.Read(odp, cancellationToken: Token);
        Assert.Equal("ab<&>\"'c😀  dlink", Words(back.Slides[0].Elements.OfType<SlideShape>().Single().Text));
        Assert.Equal("Bad  title", back.Title);
    }

    [Fact]
    public void Groups_nested_too_deep_are_refused_and_cancelling_stops_the_writer()
    {
        var deck = new SlideDeck { Width = 400, Height = 300 };
        var slide = new Slide();
        deck.Slides.Add(slide);
        var path = new SKPath();
        path.AddRect(new SKRect(0, 0, 10, 10));
        SlideElement element = new SlideShape { Bounds = SKRect.Create(0, 0, 10, 10), Paths = [new SlidePath(path)], Fill = new SolidFill(SKColors.Red) };
        for (var i = 0; i < 200; i++)
        {
            var group = new SlideGroup { Bounds = SKRect.Create(0, 0, 10, 10), ChildBounds = SKRect.Create(0, 0, 10, 10) };
            group.Children.Add(element);
            element = group;
        }
        slide.Elements.Add(element);
        var folder = fx.NewFolder();
        var refused = Assert.Throws<ConversionRefusedException>(() => OdpWriter.Write(deck, Path.Combine(folder, "deep.odp"), cancellationToken: Token));
        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);
        Assert.False(File.Exists(Path.Combine(folder, "deep.odp")));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => OdpWriter.Write(ReadPptx(Save(Sample())), Path.Combine(folder, "cancelled.odp"), cancellationToken: cancelled.Token));
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void Shapes_with_broken_numbers_are_left_out_rather_than_written_wrong()
    {
        var deck = new SlideDeck { Width = 400, Height = 300 };
        var slide = new Slide();
        deck.Slides.Add(slide);
        var path = new SKPath();
        path.MoveTo(0, 0);
        path.LineTo(1e30f, 5);
        path.LineTo(5, 5);
        path.Close();
        slide.Elements.Add(new SlideShape { Bounds = new SKRect(float.NaN, 0, 10, 10), Paths = [new SlidePath(path)], Fill = new SolidFill(SKColors.Red) });
        slide.Elements.Add(new SlideShape { Bounds = SKRect.Create(10, 10, 10, 10), Paths = [new SlidePath(path)], Fill = new SolidFill(SKColors.Blue), Rotation = float.PositiveInfinity });
        slide.Elements.Add(new SlideShape { Bounds = SKRect.Create(10, 10, 10, 10), Paths = [new SlidePath(path)], Fill = new SolidFill(SKColors.Green) });

        var back = OdfDrawReader.Read(Write(deck, fx.NewFolder()), cancellationToken: Token);
        var shape = Assert.Single(back.Slides[0].Elements.OfType<SlideShape>());
        Assert.Equal(SKColors.Green, Assert.IsType<SolidFill>(shape.Fill).Color);
    }
}
