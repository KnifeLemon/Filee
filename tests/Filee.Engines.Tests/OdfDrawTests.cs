// Filee's OpenDocument presentation and drawing reader (Odf/Draw): ODP / ODG packages and flat FODP / FODG files read
// into the slide model and drawn by the slide renderer, without LibreOffice. Every file is written in code (OdpBuilder);
// pictures are drawn at 72 dpi, so a pixel is a point.

using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Drawings;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Odf.Draw;
using Filee.Engines.Office;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Tests;

public sealed class OdfDrawTests : IDisposable
{
    /// <summary>Points per centimetre.</summary>
    private const float Cm = 72 / 2.54f;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"filee-odp-{Guid.NewGuid():N}");

    public OdfDrawTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Saves a package, or a flat file for a .fodp / .fodg name.</summary>
    private string Save(OdpBuilder builder, string name = "deck.odp") =>
        Path.GetExtension(name).StartsWith(".fo", StringComparison.Ordinal) ? builder.SaveFlat(Path.Combine(_folder, name)) : builder.Save(Path.Combine(_folder, name));

    private static SlideDeck Read(string path) => OdfDrawReader.Read(path, cancellationToken: Token);

    /// <summary>The slide as a picture at 72 dpi (a pixel per point).</summary>
    private static SKBitmap Picture(SlideDeck deck, int slide = 0) => SlideOutput.ToBitmaps(deck, 72, [slide], Token).Single();

    private static string Text(SlideTextFrame frame) =>
        string.Concat(frame.Paragraphs.SelectMany(p => p.Inlines).Select(i => i switch
        {
            HText t => t.Text,
            HTab => "\t",
            HLink link => string.Concat(link.Content.OfType<HText>().Select(t => t.Text)),
            _ => "",
        }));

    private static bool Near(SKColor actual, SKColor expected, int tolerance = 40) =>
        Math.Abs(actual.Red - expected.Red) <= tolerance && Math.Abs(actual.Green - expected.Green) <= tolerance && Math.Abs(actual.Blue - expected.Blue) <= tolerance;

    private static void AssertColor(SKBitmap picture, float x, float y, SKColor expected)
    {
        var actual = picture.GetPixel((int)x, (int)y);
        Assert.True(Near(actual, expected), $"pixel ({(int)x}, {(int)y}) is {actual}, expected {expected}");
    }

    private const string GraphicDefault =
        "<style:default-style style:family=\"graphic\"><style:graphic-properties svg:stroke-color=\"#3465a4\" draw:fill-color=\"#729fcf\"/>" +
        "<style:text-properties fo:font-size=\"18pt\" fo:color=\"#000000\"/></style:default-style>";

    /// <summary>A solid fill without an outline.</summary>
    private static string Solid(string name, string color) => OdpBuilder.GraphicStyle(name, $"draw:fill=\"solid\" draw:fill-color=\"{color}\" draw:stroke=\"none\"");

    // ───────────────────────── Text ─────────────────────────

    /// <summary>
    /// Title and outline styles and placeholders as LibreOffice Impress writes them: a title, an outline without a position
    /// of its own (the master's outline placeholder's) and an empty subtitle placeholder, which is not drawn.
    /// </summary>
    internal static OdpBuilder TextDeck() => new()
    {
        Styles = GraphicDefault +
                 "<style:style style:name=\"Default-title\" style:family=\"presentation\"><style:graphic-properties draw:stroke=\"none\" draw:fill=\"none\" draw:textarea-vertical-align=\"middle\"/>" +
                 "<style:paragraph-properties fo:text-align=\"center\"/><style:text-properties fo:font-size=\"44pt\"/></style:style>" +
                 "<style:style style:name=\"Default-outline1\" style:family=\"presentation\"><style:graphic-properties draw:stroke=\"none\" draw:fill=\"none\">" +
                 "<text:list-style style:name=\"Default-outline1\"><text:list-level-style-bullet text:level=\"1\" text:bullet-char=\"●\"><style:list-level-properties/>" +
                 "<style:text-properties fo:font-size=\"45%\" fo:color=\"#C00000\"/></text:list-level-style-bullet>" +
                 "<text:list-level-style-bullet text:level=\"2\" text:bullet-char=\"–\"><style:list-level-properties/></text:list-level-style-bullet></text:list-style>" +
                 "</style:graphic-properties><style:paragraph-properties fo:margin-left=\"1.2cm\" fo:text-indent=\"-0.9cm\"/><style:text-properties fo:font-size=\"32pt\"/></style:style>" +
                 "<style:style style:name=\"Default-outline2\" style:family=\"presentation\" style:parent-style-name=\"Default-outline1\">" +
                 "<style:paragraph-properties fo:margin-left=\"2.4cm\" fo:text-indent=\"-0.8cm\"/><style:text-properties fo:font-size=\"28pt\"/></style:style>",
        ContentStyles = "<style:style style:name=\"pr1\" style:family=\"presentation\" style:parent-style-name=\"Default-title\"><style:graphic-properties draw:fill=\"none\"/></style:style>",
        MasterStyles =
            "<style:master-page style:name=\"Default\" style:page-layout-name=\"PM1\">" +
            "<draw:frame presentation:style-name=\"Default-title\" presentation:class=\"title\" svg:x=\"2cm\" svg:y=\"1cm\" svg:width=\"24cm\" svg:height=\"2.5cm\" presentation:placeholder=\"true\"><draw:text-box/></draw:frame>" +
            "<draw:frame presentation:style-name=\"Default-outline1\" presentation:class=\"outline\" svg:x=\"2cm\" svg:y=\"4cm\" svg:width=\"24cm\" svg:height=\"10cm\" presentation:placeholder=\"true\"><draw:text-box/></draw:frame>" +
            "</style:master-page>",
        Pages = OdpBuilder.Page(
            "<draw:frame presentation:style-name=\"pr1\" presentation:class=\"title\" svg:x=\"2cm\" svg:y=\"1cm\" svg:width=\"24cm\" svg:height=\"2.5cm\"><draw:text-box><text:p>Agenda</text:p></draw:text-box></draw:frame>" +
            "<draw:frame presentation:style-name=\"Default-outline1\" presentation:class=\"outline\"><draw:text-box>" +
            "<text:list><text:list-item><text:p>First point</text:p></text:list-item><text:list-item><text:p>Second</text:p>" +
            "<text:list><text:list-item><text:p>Deeper</text:p></text:list-item></text:list></text:list-item>" +
            "<text:list-item><text:p/></text:list-item></text:list></draw:text-box></draw:frame>" +
            "<draw:frame presentation:class=\"subtitle\" presentation:placeholder=\"true\" svg:x=\"1cm\" svg:y=\"14cm\" svg:width=\"5cm\" svg:height=\"1cm\"><draw:text-box/></draw:frame>"),
    };

    [Fact]
    public void A_text_slide_has_its_title_and_list_with_bullets_and_levels()
    {
        var deck = Read(Save(TextDeck()));
        Assert.Equal(28 * Cm, deck.Width, 0.05);
        Assert.Equal(15.75f * Cm, deck.Height, 0.05);
        var slide = Assert.Single(deck.Slides);
        // The master's placeholders are templates; the empty subtitle isn't drawn.
        var shapes = slide.Elements.Cast<SlideShape>().ToList();
        Assert.Equal(2, shapes.Count);

        var title = shapes[0];
        Assert.Equal("Agenda", Text(title.Text!));
        Assert.Equal(4400, title.Text!.Paragraphs[0].Inlines.OfType<HText>().Single().Format.Size);
        Assert.Equal(HAlign.Center, title.Text.Paragraphs[0].Format.Align);
        Assert.Equal(TextAnchor.Middle, title.Text.Anchor);

        var outline = shapes[1];
        // Lengths are read in 1/100 pt.
        Assert.Equal(2 * Cm, outline.Bounds.Left, 0.01);
        Assert.Equal(4 * Cm, outline.Bounds.Top, 0.01);
        Assert.Equal(24 * Cm, outline.Bounds.Width, 0.01);
        Assert.Equal(10 * Cm, outline.Bounds.Height, 0.01);
        var paragraphs = outline.Text!.Paragraphs;
        Assert.Equal(4, paragraphs.Count);
        // Level 1: a red bullet at 45 % of 32 pt, a tab to the hanging indent, the text in 32 pt.
        var bullet = Assert.IsType<HText>(paragraphs[0].Inlines[0]);
        Assert.Equal("●", bullet.Text);
        Assert.Equal("#C00000", bullet.Format.Color);
        Assert.Equal(1440, bullet.Format.Size);
        Assert.IsType<HTab>(paragraphs[0].Inlines[1]);
        Assert.Equal("First point", ((HText)paragraphs[0].Inlines[2]).Text);
        Assert.Equal(3200, ((HText)paragraphs[0].Inlines[2]).Format.Size);
        Assert.Equal((int)Math.Round(1.2 * 7200 / 2.54), paragraphs[0].Format.Left);
        // Level 2: the master's outline 2 style.
        Assert.Equal("–", ((HText)paragraphs[2].Inlines[0]).Text);
        Assert.Equal("Deeper", ((HText)paragraphs[2].Inlines[2]).Text);
        Assert.Equal(2800, ((HText)paragraphs[2].Inlines[2]).Format.Size);
        Assert.Equal((int)Math.Round(2.4 * 7200 / 2.54), paragraphs[2].Format.Left);
        // An empty item shows no bullet.
        Assert.Empty(paragraphs[3].Inlines);

        using var picture = Picture(deck);
        var dark = 0;
        for (var y = (int)(4 * Cm); y < (int)(6 * Cm); y++)
            for (var x = (int)(2 * Cm); x < (int)(12 * Cm); x++)
                dark += picture.GetPixel(x, y).Red < 128 ? 1 : 0;
        Assert.True(dark > 100, $"{dark} dark pixels where the first point is");
        AssertColor(picture, 27 * Cm, 15 * Cm, SKColors.White);
    }

    [Fact]
    public void A_numbered_list_without_paragraph_indents_takes_its_levels_indent()
    {
        var builder = new OdpBuilder
        {
            Styles = GraphicDefault,
            ContentStyles =
                "<text:list-style style:name=\"L1\"><text:list-level-style-number text:level=\"1\" style:num-format=\"a\" style:num-prefix=\"(\" style:num-suffix=\")\" text:start-value=\"3\">" +
                "<style:list-level-properties text:space-before=\"0.5cm\" text:min-label-width=\"0.6cm\"/></text:list-level-style-number>" +
                "<text:list-level-style-number text:level=\"2\" style:num-format=\"I\" style:num-suffix=\".\"/></text:list-style>" +
                "<style:style style:name=\"P1\" style:family=\"paragraph\"><style:paragraph-properties fo:margin-left=\"0cm\" fo:text-indent=\"0cm\" fo:text-align=\"end\"/></style:style>",
            Pages = OdpBuilder.Page("<draw:frame draw:text-style-name=\"P1\" svg:x=\"1cm\" svg:y=\"1cm\" svg:width=\"20cm\" svg:height=\"8cm\"><draw:text-box><text:list text:style-name=\"L1\">" +
                                    "<text:list-item><text:p>one</text:p></text:list-item><text:list-item><text:p>two</text:p>" +
                                    "<text:list><text:list-item><text:p>inner</text:p></text:list-item><text:list-item><text:p>inner</text:p></text:list-item></text:list></text:list-item>" +
                                    "<text:list-item><text:p>three</text:p></text:list-item></text:list></draw:text-box></draw:frame>"),
        };
        var frame = ((SlideShape)Assert.Single(Assert.Single(Read(Save(builder)).Slides).Elements)).Text!;
        Assert.Equal(["(c)\tone", "(d)\ttwo", "I. inner", "II. inner", "(e)\tthree"],
            frame.Paragraphs.Select(p => string.Concat(p.Inlines.Select(i => i is HText t ? t.Text : "\t"))));
        // The level's indent: 0.5 + 0.6 cm, the label hanging 0.6 cm (level 2 has none: a space after its number); the
        // shape's text style aligns to the end.
        Assert.Equal((int)Math.Round(1.1 * 7200 / 2.54), frame.Paragraphs[0].Format.Left);
        Assert.Equal(-(int)Math.Round(0.6 * 7200 / 2.54), frame.Paragraphs[0].Format.FirstLine);
        Assert.Equal(HAlign.Right, frame.Paragraphs[0].Format.Align);
    }

    // ───────────────────────── Shapes ─────────────────────────

    /// <summary>
    /// A triangle whose apex is an equation of the view box's width and a modifier, a circle drawn by angle-ellipse, and a
    /// preset (diamond) named without a path.
    /// </summary>
    internal static OdpBuilder ShapeDeck() => new()
    {
        Styles = GraphicDefault,
        ContentStyles = Solid("red", "#FF0000") + Solid("blue", "#0000FF") + Solid("green", "#00A000"),
        Pages = OdpBuilder.Page(
            "<draw:custom-shape draw:style-name=\"red\" svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"10cm\" svg:height=\"5cm\">" +
            "<draw:enhanced-geometry svg:viewBox=\"0 0 21600 21600\" draw:type=\"mso-spt100\" draw:modifiers=\"10800\" draw:enhanced-path=\"M ?f0 0 L 21600 21600 0 21600 Z N\">" +
            "<draw:equation draw:name=\"f0\" draw:formula=\"if($0 - 1, width / 2, 0)\"/></draw:enhanced-geometry></draw:custom-shape>" +
            "<draw:custom-shape draw:style-name=\"green\" svg:x=\"14cm\" svg:y=\"2cm\" svg:width=\"4cm\" svg:height=\"4cm\">" +
            "<draw:enhanced-geometry svg:viewBox=\"0 0 21600 21600\" draw:type=\"ellipse\" draw:enhanced-path=\"U 10800 10800 10800 10800 0 360 Z N\"/></draw:custom-shape>" +
            "<draw:custom-shape draw:style-name=\"blue\" svg:x=\"20cm\" svg:y=\"2cm\" svg:width=\"4cm\" svg:height=\"4cm\"><draw:enhanced-geometry draw:type=\"diamond\"/></draw:custom-shape>"),
    };

    [Fact]
    public void A_custom_shape_is_drawn_from_its_enhanced_path_equations_and_modifiers()
    {
        var deck = Read(Save(ShapeDeck()));
        var shapes = Assert.Single(deck.Slides).Elements.Cast<SlideShape>().ToList();
        Assert.Equal(3, shapes.Count);
        Assert.Equal(new SolidFill(SKColors.Red), shapes[0].Fill);
        Assert.Null(shapes[0].Line);

        using var picture = Picture(deck);
        // Triangle: the apex at the top centre, the base along the bottom.
        AssertColor(picture, 7 * Cm, 2.3f * Cm, SKColors.Red);
        AssertColor(picture, 2.3f * Cm, 6.8f * Cm, SKColors.Red);
        AssertColor(picture, 2.3f * Cm, 2.3f * Cm, SKColors.White);
        AssertColor(picture, 11.7f * Cm, 2.3f * Cm, SKColors.White);
        // Circle: the centre filled, the box's corner not.
        AssertColor(picture, 16 * Cm, 4 * Cm, new SKColor(0, 0xA0, 0));
        AssertColor(picture, 14.2f * Cm, 2.2f * Cm, SKColors.White);
        // Diamond (DrawingML's preset): the centre filled, the corners not.
        AssertColor(picture, 22 * Cm, 4 * Cm, SKColors.Blue);
        AssertColor(picture, 20.2f * Cm, 2.2f * Cm, SKColors.White);
    }

    [Fact]
    public void A_rotated_rectangle_turns_about_its_centre()
    {
        var builder = new OdpBuilder
        {
            Styles = GraphicDefault,
            ContentStyles = Solid("blue", "#0000FF"),
            // 45° counter-clockwise about the top-left corner, which then goes to (10 cm, 5 cm).
            Pages = OdpBuilder.Page("<draw:rect draw:style-name=\"blue\" svg:width=\"4cm\" svg:height=\"2cm\" draw:transform=\"rotate (0.785398163397448) translate (10cm 5cm)\"/>"),
        };
        var deck = Read(Save(builder));
        var rectangle = Assert.IsType<SlideShape>(Assert.Single(Assert.Single(deck.Slides).Elements));
        Assert.Equal(315, rectangle.Rotation, 0.01);
        Assert.Equal(4 * Cm, rectangle.Bounds.Width, 0.01);
        Assert.Equal(2 * Cm, rectangle.Bounds.Height, 0.01);
        var center = new SKPoint((10 + 2.1213f) * Cm, (5 - 0.7071f) * Cm);
        Assert.Equal(center.X, rectangle.Bounds.MidX, 0.05);
        Assert.Equal(center.Y, rectangle.Bounds.MidY, 0.05);

        using var picture = Picture(deck);
        AssertColor(picture, center.X, center.Y, SKColors.Blue);
        // Along the long side, up to the right.
        AssertColor(picture, center.X + 1.2f * Cm, center.Y - 1.2f * Cm, SKColors.Blue);
        // Inside the box as it was before turning, outside it now.
        AssertColor(picture, center.X + 1.8f * Cm, center.Y + 0.8f * Cm, SKColors.White);
    }

    [Fact]
    public void Lines_polygons_paths_and_ellipse_parts_have_their_outlines()
    {
        var builder = new OdpBuilder
        {
            Styles = GraphicDefault +
                     "<draw:marker draw:name=\"Arrow\" svg:viewBox=\"0 0 20 30\" svg:d=\"M10 0l-10 30h20z\"/>",
            ContentStyles = Solid("red", "#FF0000") + Solid("blue", "#0000FF") +
                            OdpBuilder.GraphicStyle("line", "draw:stroke=\"solid\" svg:stroke-color=\"#000000\" svg:stroke-width=\"0.2cm\" draw:marker-end=\"Arrow\" draw:marker-end-width=\"0.6cm\""),
            Pages = OdpBuilder.Page(
                "<draw:line draw:style-name=\"line\" svg:x1=\"1cm\" svg:y1=\"1cm\" svg:x2=\"9cm\" svg:y2=\"1cm\"/>" +
                "<draw:polygon draw:style-name=\"red\" svg:x=\"1cm\" svg:y=\"3cm\" svg:width=\"4cm\" svg:height=\"4cm\" svg:viewBox=\"0 0 4000 4000\" draw:points=\"0,0 4000,0 0,4000\"/>" +
                "<draw:path draw:style-name=\"blue\" svg:x=\"6cm\" svg:y=\"3cm\" svg:width=\"4cm\" svg:height=\"4cm\" svg:viewBox=\"0 0 4000 4000\" svg:d=\"M4000 4000 h-4000 l4000 -4000 z\"/>" +
                "<draw:ellipse draw:style-name=\"red\" svg:x=\"11cm\" svg:y=\"3cm\" svg:width=\"4cm\" svg:height=\"4cm\" draw:kind=\"section\" draw:start-angle=\"0\" draw:end-angle=\"90\"/>"),
        };
        var deck = Read(Save(builder));
        var elements = Assert.Single(deck.Slides).Elements.Cast<SlideShape>().ToList();
        Assert.Equal(4, elements.Count);
        var line = elements[0];
        Assert.Equal(0.2f * Cm, line.Line!.Width, 0.01);
        Assert.Equal(LineEndKind.Triangle, line.Line.Tail.Kind);
        Assert.Null(line.Fill);

        using var picture = Picture(deck);
        AssertColor(picture, 5 * Cm, 1 * Cm, SKColors.Black);
        AssertColor(picture, 1.5f * Cm, 3.5f * Cm, SKColors.Red);
        AssertColor(picture, 4.5f * Cm, 6.5f * Cm, SKColors.White);
        AssertColor(picture, 9.5f * Cm, 6.5f * Cm, SKColors.Blue);
        AssertColor(picture, 6.5f * Cm, 3.5f * Cm, SKColors.White);
        // A quarter section from 0° to 90° counter-clockwise: the top right quarter.
        AssertColor(picture, 13.8f * Cm, 4.2f * Cm, SKColors.Red);
        AssertColor(picture, 12.2f * Cm, 5.8f * Cm, SKColors.White);
    }

    [Fact]
    public void A_picture_frame_shows_its_picture_from_the_package_or_the_flat_file()
    {
        var png = OdpBuilder.Png(8, 8, new SKColor(0, 0x80, 0));
        var builder = new OdpBuilder
        {
            Styles = GraphicDefault,
            Pages = OdpBuilder.Page("<draw:frame svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"6cm\" svg:height=\"4cm\"><draw:image xlink:href=\"Pictures/a.png\"/></draw:frame>"),
        };
        builder.Parts["Pictures/a.png"] = png;
        var flat = new OdpBuilder
        {
            Styles = GraphicDefault,
            Pages = OdpBuilder.Page("<draw:frame svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"6cm\" svg:height=\"4cm\"><draw:image><office:binary-data>" +
                                    Convert.ToBase64String(png) + "</office:binary-data></draw:image></draw:frame>"),
        };
        foreach (var path in new[] { Save(builder), Save(flat, "flat.fodp") })
        {
            var deck = Read(path);
            var frame = Assert.IsType<SlideShape>(Assert.Single(Assert.Single(deck.Slides).Elements));
            Assert.True(frame.IsPicture);
            Assert.Null(frame.Line);
            using var picture = Picture(deck);
            AssertColor(picture, 5 * Cm, 4 * Cm, new SKColor(0, 0x80, 0));
            AssertColor(picture, 9 * Cm, 4 * Cm, SKColors.White);
        }
    }

    // ───────────────────────── Masters and groups ─────────────────────────

    [Fact]
    public void Master_page_shapes_and_background_are_drawn_behind_the_page()
    {
        var builder = new OdpBuilder
        {
            Styles = GraphicDefault,
            // Master and page styles share a name: each part's own automatic styles are meant.
            StylesAutomatic = "<style:style style:name=\"Mdp1\" style:family=\"drawing-page\"><style:drawing-page-properties draw:fill=\"solid\" draw:fill-color=\"#FFFF00\"/></style:style>" +
                              Solid("gr1", "#0000FF"),
            ContentStyles = Solid("gr1", "#FF0000") +
                            "<style:style style:name=\"dp2\" style:family=\"drawing-page\"><style:drawing-page-properties presentation:background-objects-visible=\"false\" presentation:background-visible=\"false\"/></style:style>",
            MasterStyles = "<style:master-page style:name=\"Default\" style:page-layout-name=\"PM1\" draw:style-name=\"Mdp1\">" +
                           "<draw:rect draw:style-name=\"gr1\" svg:x=\"0cm\" svg:y=\"0cm\" svg:width=\"28cm\" svg:height=\"2cm\"/>" +
                           "<draw:frame presentation:class=\"title\" svg:x=\"2cm\" svg:y=\"5cm\" svg:width=\"24cm\" svg:height=\"3cm\"><draw:text-box><text:p>Title template</text:p></draw:text-box></draw:frame>" +
                           "<presentation:notes><draw:page-thumbnail svg:x=\"0cm\" svg:y=\"0cm\" svg:width=\"5cm\" svg:height=\"5cm\"/></presentation:notes>" +
                           "</style:master-page>",
            Pages = OdpBuilder.Page("<draw:rect draw:style-name=\"gr1\" svg:x=\"10cm\" svg:y=\"8cm\" svg:width=\"4cm\" svg:height=\"4cm\"/>") +
                    OdpBuilder.Page("<draw:rect draw:style-name=\"gr1\" svg:x=\"10cm\" svg:y=\"8cm\" svg:width=\"4cm\" svg:height=\"4cm\"/>", "dp2"),
        };
        var deck = Read(Save(builder));
        Assert.Equal(2, deck.Slides.Count);
        var first = deck.Slides[0];
        Assert.Equal(new SolidFill(new SKColor(0xFF, 0xFF, 0)), first.Background);
        Assert.Equal(2, first.Elements.Count);
        Assert.Equal(new SolidFill(SKColors.Blue), ((SlideShape)first.Elements[0]).Fill);
        Assert.Equal(new SolidFill(SKColors.Red), ((SlideShape)first.Elements[1]).Fill);

        using (var picture = Picture(deck))
        {
            AssertColor(picture, 14 * Cm, 1 * Cm, SKColors.Blue);
            AssertColor(picture, 12 * Cm, 10 * Cm, SKColors.Red);
            AssertColor(picture, 3 * Cm, 10 * Cm, new SKColor(0xFF, 0xFF, 0));
            // The master's title placeholder is a template.
            AssertColor(picture, 14 * Cm, 6.5f * Cm, new SKColor(0xFF, 0xFF, 0));
        }

        // A page without the master's background and shapes.
        var second = deck.Slides[1];
        Assert.Null(second.Background);
        Assert.Single(second.Elements);
        using var plain = Picture(deck, 1);
        AssertColor(plain, 14 * Cm, 1 * Cm, SKColors.White);
    }

    [Fact]
    public void Footer_and_slide_number_of_the_master_show_when_the_page_says_so()
    {
        var builder = new OdpBuilder
        {
            Styles = GraphicDefault,
            ContentStyles = "<style:style style:name=\"dp1\" style:family=\"drawing-page\"><style:drawing-page-properties presentation:display-footer=\"true\" presentation:display-page-number=\"true\" presentation:display-date-time=\"false\"/></style:style>",
            MasterStyles = "<style:master-page style:name=\"Default\" style:page-layout-name=\"PM1\">" +
                           "<draw:frame presentation:class=\"footer\" svg:x=\"2cm\" svg:y=\"14cm\" svg:width=\"10cm\" svg:height=\"1cm\"><draw:text-box><text:p><presentation:footer/></text:p></draw:text-box></draw:frame>" +
                           "<draw:frame presentation:class=\"page-number\" svg:x=\"20cm\" svg:y=\"14cm\" svg:width=\"6cm\" svg:height=\"1cm\"><draw:text-box><text:p>Slide <text:page-number>&lt;number&gt;</text:page-number></text:p></draw:text-box></draw:frame>" +
                           "<draw:frame presentation:class=\"date-time\" svg:x=\"12cm\" svg:y=\"14cm\" svg:width=\"6cm\" svg:height=\"1cm\"><draw:text-box><text:p>date</text:p></draw:text-box></draw:frame>" +
                           "</style:master-page>",
            Pages = "<presentation:footer-decl presentation:name=\"ftr1\">Filee Inc.</presentation:footer-decl>" +
                    OdpBuilder.Page("") +
                    "<draw:page draw:name=\"two\" draw:master-page-name=\"Default\" draw:style-name=\"dp1\" presentation:use-footer-name=\"ftr1\"/>",
        };
        var deck = Read(Save(builder));
        Assert.Empty(deck.Slides[0].Elements);
        var texts = deck.Slides[1].Elements.Cast<SlideShape>().Select(s => Text(s.Text!)).ToList();
        Assert.Equal(["Filee Inc.", "Slide 2"], texts);
    }

    [Fact]
    public void A_group_keeps_its_shapes_where_the_page_puts_them()
    {
        var builder = new OdpBuilder
        {
            Styles = GraphicDefault,
            ContentStyles = Solid("red", "#FF0000") + Solid("blue", "#0000FF"),
            Pages = OdpBuilder.Page(
                "<draw:g><draw:rect draw:style-name=\"red\" svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"3cm\" svg:height=\"3cm\"/>" +
                "<draw:g><draw:ellipse draw:style-name=\"blue\" svg:x=\"10cm\" svg:y=\"6cm\" svg:width=\"4cm\" svg:height=\"4cm\"/></draw:g></draw:g>"),
        };
        var deck = Read(Save(builder));
        var group = Assert.IsType<SlideGroup>(Assert.Single(Assert.Single(deck.Slides).Elements));
        Assert.Equal(2, group.Children.Count);
        Assert.IsType<SlideGroup>(group.Children[1]);
        Assert.Equal(group.Bounds, group.ChildBounds);

        using var picture = Picture(deck);
        AssertColor(picture, 3.5f * Cm, 3.5f * Cm, SKColors.Red);
        AssertColor(picture, 12 * Cm, 8 * Cm, SKColors.Blue);
        AssertColor(picture, 7 * Cm, 4 * Cm, SKColors.White);
    }

    [Fact]
    public void Gradients_and_hatches_fill_shapes()
    {
        var builder = new OdpBuilder
        {
            Styles = GraphicDefault +
                     "<draw:gradient draw:name=\"g1\" draw:style=\"linear\" draw:start-color=\"#FF0000\" draw:end-color=\"#0000FF\" draw:angle=\"0\" draw:border=\"0%\"/>" +
                     "<draw:hatch draw:name=\"h1\" draw:style=\"double\" draw:color=\"#000000\" draw:distance=\"0.1cm\" draw:rotation=\"0\"/>",
            ContentStyles = OdpBuilder.GraphicStyle("g", "draw:fill=\"gradient\" draw:fill-gradient-name=\"g1\" draw:stroke=\"none\"") +
                            OdpBuilder.GraphicStyle("h", "draw:fill=\"hatch\" draw:fill-hatch-name=\"h1\" draw:fill-hatch-solid=\"true\" draw:fill-color=\"#FFFF00\" draw:stroke=\"none\""),
            Pages = OdpBuilder.Page("<draw:rect draw:style-name=\"g\" svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"6cm\" svg:height=\"10cm\"/>" +
                                    "<draw:rect draw:style-name=\"h\" svg:x=\"10cm\" svg:y=\"2cm\" svg:width=\"6cm\" svg:height=\"6cm\"/>"),
        };
        var deck = Read(Save(builder));
        var shapes = Assert.Single(deck.Slides).Elements.Cast<SlideShape>().ToList();
        var gradient = Assert.IsType<GradientFill>(shapes[0].Fill);
        Assert.Equal(90, gradient.Angle);
        var hatch = Assert.IsType<PatternFill>(shapes[1].Fill);
        Assert.Equal("cross", hatch.Preset);

        using var picture = Picture(deck);
        // Start colour at the top, end colour at the bottom.
        Assert.True(picture.GetPixel((int)(5 * Cm), (int)(2.3f * Cm)).Red > 200);
        Assert.True(picture.GetPixel((int)(5 * Cm), (int)(11.7f * Cm)).Blue > 200);
    }

    // ───────────────────────── Formats and routes ─────────────────────────

    [Fact]
    public void A_flat_presentation_reads_like_its_package()
    {
        var builder = ShapeDeck();
        builder.Title = "Shapes";
        var package = Read(Save(builder));
        var flat = Read(Save(builder, "flat.fodp"));
        Assert.Equal("Shapes", package.Title);
        Assert.Equal("Shapes", flat.Title);
        Assert.Equal(package.Slides[0].Elements.Count, flat.Slides[0].Elements.Count);
        Assert.Equal(package.Slides[0].Elements.Select(e => e.Bounds), flat.Slides[0].Elements.Select(e => e.Bounds));
        Assert.Equal(package.Slides[0].Elements.Cast<SlideShape>().Select(s => s.Fill), flat.Slides[0].Elements.Cast<SlideShape>().Select(s => s.Fill));
    }

    [Theory]
    [InlineData("odg", "pdf")]
    [InlineData("odg", "svg")]
    [InlineData("fodg", "png")]
    public async Task A_drawing_goes_through_the_drawing_converter_a_file_per_page(string format, string target)
    {
        var builder = new OdpBuilder
        {
            Drawing = true,
            Styles = GraphicDefault,
            ContentStyles = Solid("red", "#FF0000"),
            Pages = "<draw:page draw:name=\"one\" draw:master-page-name=\"Default\"><draw:rect draw:style-name=\"red\" svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"6cm\" svg:height=\"4cm\"/></draw:page>" +
                    "<draw:page draw:name=\"two\" draw:master-page-name=\"Default\"><draw:custom-shape svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"6cm\" svg:height=\"4cm\"><text:p>Two</text:p>" +
                    "<draw:enhanced-geometry draw:type=\"rectangle\" draw:enhanced-path=\"M 0 0 L 21600 0 21600 21600 0 21600 Z N\"/></draw:custom-shape></draw:page>",
        };
        var source = Save(builder, $"drawing.{format}");
        var converter = new DrawingConverter();
        Assert.Contains(converter.Edges, e => e.From == format && e.To == target);
        var written = await converter.ConvertAsync(new ConversionStep(source, format, target, new Preset(), new FolderOutput(_folder), _folder), null, Token);
        if (target == "pdf")
        {
            using var document = UglyToad.PdfPig.PdfDocument.Open(Assert.Single(written));
            Assert.Equal(2, document.NumberOfPages);
            Assert.Equal(21 * Cm, document.GetPage(1).Width, 0.5);
            return;
        }
        Assert.Equal(2, written.Count);
        if (target == "svg")
        {
            Assert.All(written, file => Assert.Equal("svg", XDocument.Load(file).Root!.Name.LocalName));
            return;
        }
        using var picture = SKBitmap.Decode(written[0]);
        Assert.True(picture.Height > picture.Width);
    }

    [Fact]
    public void Presentations_go_to_the_slide_renderer_and_drawings_to_the_drawing_converter()
    {
        Assert.All(new[] { "odp", "otp", "fodp" }, format => Assert.Contains(format, SlideFormats.Decks));
        Assert.Equal(["odg", "fodg"], DrawingFormats.BuiltIn.Select(f => f.Id).Where(OdfDrawReader.Drawings.Contains));
        // ODP → PPTX stays with LibreOffice: the PPTX writer doesn't take OpenDocument presentations.
        Assert.DoesNotContain(new PptxWriterConverter().Edges, e => OdfDrawReader.Presentations.Contains(e.From));
    }

    [Fact]
    public async Task The_text_of_a_presentation_goes_to_documents_slide_by_slide()
    {
        var builder = TextDeck();
        builder.Pages += OdpBuilder.Page("<draw:frame svg:x=\"2cm\" svg:y=\"8cm\" svg:width=\"10cm\" svg:height=\"2cm\"><draw:text-box><text:p>Lower</text:p></draw:text-box></draw:frame>" +
                                         "<draw:frame svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"10cm\" svg:height=\"2cm\"><draw:text-box><text:p>Upper</text:p></draw:text-box></draw:frame>");
        var document = await DocumentReaders.ReadAsync(Save(builder), "odp", _folder, Token);
        Assert.Equal(2, document.Sections.Count);
        string Texts(HSection section) => string.Join("|", section.Blocks.OfType<HParagraph>().Select(p => string.Concat(p.Inlines.OfType<HText>().Select(t => t.Text))));
        Assert.Equal("Agenda|●First point|●Second|–Deeper|", Texts(document.Sections[0]));
        Assert.Equal("Upper|Lower", Texts(document.Sections[1]));
        Assert.Equal(28 * 7200 / 2.54, document.Sections[0].Page!.Width, 1.0);
    }

    // ───────────────────────── Capability check ─────────────────────────

    [Fact]
    public void Charts_3d_scenes_and_other_objects_are_named_by_the_check()
    {
        string Deck(string content, string? name = null, bool hidden = false)
        {
            var builder = new OdpBuilder
            {
                Styles = GraphicDefault,
                ContentStyles = "<style:style style:name=\"hidden\" style:family=\"drawing-page\"><style:drawing-page-properties presentation:visibility=\"hidden\"/></style:style>",
                Pages = OdpBuilder.Page(content, hidden ? "hidden" : null),
            };
            builder.Parts["Object 1/content.xml"] = System.Text.Encoding.UTF8.GetBytes(
                $"<office:document-content {OdpBuilder.Namespaces}><office:body><office:chart><chart:chart/></office:chart></office:body></office:document-content>");
            return Save(builder, name ?? $"check-{Guid.NewGuid():N}.odp");
        }
        var chart = "<draw:frame svg:x=\"1cm\" svg:y=\"1cm\" svg:width=\"5cm\" svg:height=\"5cm\"><draw:object xlink:href=\"./Object 1\"/><draw:image xlink:href=\"./ObjectReplacements/Object 1\"/></draw:frame>";
        Assert.Equal("charts", OdfDrawReader.FirstUnsupported(Deck(chart), Token));
        Assert.Equal("3D scenes", OdfDrawReader.FirstUnsupported(Deck("<dr3d:scene svg:x=\"1cm\" svg:y=\"1cm\" svg:width=\"5cm\" svg:height=\"5cm\"/>"), Token));
        Assert.Equal("OLE objects", OdfDrawReader.FirstUnsupported(Deck("<draw:frame svg:width=\"5cm\" svg:height=\"5cm\"><draw:object-ole xlink:href=\"./Object 2\"/></draw:frame>"), Token));
        Assert.Equal("shadows", OdfDrawReader.FirstUnsupported(ShadowDeck(), Token));
        Assert.Null(OdfDrawReader.FirstUnsupported(Deck("<draw:rect svg:width=\"5cm\" svg:height=\"5cm\"/>"), Token));
        // On a hidden slide nothing is drawn, so nothing is missing.
        Assert.Null(OdfDrawReader.FirstUnsupported(Deck(chart, hidden: true), Token));
        Assert.Null(OdfDrawReader.FirstUnsupported(Path.Combine(_folder, "missing.odp"), Token));

        // Through the renderer's check.
        var renderer = new Typesetting.FileeRendererConverter();
        Assert.Equal("charts", renderer.FirstUnsupported(Deck(chart, "renderer.odp"), "odp"));
        // The chart's replacement picture can't be drawn here (no part): the frame is left out.
        Assert.Empty(Read(Deck(chart)).Slides[0].Elements);
    }

    private string ShadowDeck()
    {
        var builder = new OdpBuilder
        {
            Styles = GraphicDefault,
            ContentStyles = OdpBuilder.GraphicStyle("shadow", "draw:shadow=\"visible\" draw:fill=\"solid\""),
            Pages = OdpBuilder.Page("<draw:rect draw:style-name=\"shadow\" svg:width=\"5cm\" svg:height=\"5cm\"/>"),
        };
        return Save(builder, "shadow.odp");
    }

    // ───────────────────────── Hostile input ─────────────────────────

    [Fact]
    public void Groups_nested_too_deep_are_refused()
    {
        var builder = new OdpBuilder
        {
            Styles = GraphicDefault,
            Pages = OdpBuilder.Page(string.Concat(Enumerable.Repeat("<draw:g>", 40)) + "<draw:rect svg:width=\"1cm\" svg:height=\"1cm\"/>" +
                                    string.Concat(Enumerable.Repeat("</draw:g>", 40))),
        };
        var refused = Assert.Throws<ConversionRefusedException>(() => Read(Save(builder)));
        Assert.Contains("nested", refused.Message);
    }

    [Fact(Timeout = 30_000)]
    public void Hostile_geometry_is_contained_or_refused_quickly()
    {
        // Equations in a loop, a chain of 4,000 that refer back, a formula nested 10,000 deep, a huge text:s.
        var chain = string.Concat(Enumerable.Range(1, 4000).Select(i => $"<draw:equation draw:name=\"c{i}\" draw:formula=\"?c{i - 1} + 1\"/>"));
        var deep = new string('(', 10_000) + "1" + new string(')', 10_000);
        var builder = new OdpBuilder
        {
            Styles = GraphicDefault,
            Pages = OdpBuilder.Page(
                "<draw:custom-shape svg:width=\"5cm\" svg:height=\"5cm\"><text:p>a<text:s text:c=\"2000000000\"/>b</text:p>" +
                "<draw:enhanced-geometry svg:viewBox=\"0 0 21600 21600\" draw:enhanced-path=\"M ?f0 ?f1 L ?c4000 ?deep 21600 21600 Z N\">" +
                "<draw:equation draw:name=\"f0\" draw:formula=\"?f1 + 1\"/><draw:equation draw:name=\"f1\" draw:formula=\"?f0 * 2\"/>" +
                $"<draw:equation draw:name=\"c0\" draw:formula=\"0\"/>{chain}<draw:equation draw:name=\"deep\" draw:formula=\"{deep}\"/>" +
                "</draw:enhanced-geometry></draw:custom-shape>"),
        };
        var shape = Assert.IsType<SlideShape>(Assert.Single(Assert.Single(Read(Save(builder)).Slides).Elements));
        Assert.Single(shape.Paths);
        Assert.Equal(10_002, Text(shape.Text!).Length);

        // A master shape of 100,000 path tokens on 30 pages passes the deck's path budget.
        var path = "M 0 0 " + string.Concat(Enumerable.Repeat("L 1 1 ", 50_000));
        var heavy = new OdpBuilder
        {
            Styles = GraphicDefault,
            MasterStyles = "<style:master-page style:name=\"Default\" style:page-layout-name=\"PM1\"><draw:custom-shape svg:width=\"5cm\" svg:height=\"5cm\">" +
                           $"<draw:enhanced-geometry draw:enhanced-path=\"{path}\"/></draw:custom-shape></style:master-page>",
            Pages = string.Concat(Enumerable.Repeat(OdpBuilder.Page(""), 30)),
        };
        var refused = Assert.Throws<ConversionRefusedException>(() => Read(Save(heavy, "heavy.odp")));
        Assert.Contains("path commands", refused.Message);
    }

    [Fact]
    public void Other_opendocument_types_and_damaged_files_are_refused_with_their_reason()
    {
        var text = Path.Combine(_folder, "text.odp");
        new OdtBuilder().Save(text);
        Assert.Contains("application/vnd.oasis.opendocument.text", Assert.Throws<InvalidDataException>(() => Read(text)).Message);
        var junk = Path.Combine(_folder, "junk.fodp");
        File.WriteAllText(junk, "not xml at all");
        Assert.Throws<InvalidDataException>(() => Read(junk));
    }

    /// <summary>Hands out <c>out{suffix}.{ext}</c> paths in a folder.</summary>
    private sealed class FolderOutput(string folder) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => Path.Combine(folder, $"out{suffix}.{extension}");
    }
}

/// <summary>Routes and whole conversions of OpenDocument presentations and drawings through the built-in engines.</summary>
[Collection("EngineEnvironment")] // FileeRenderer is process-wide
public sealed class OdfDrawRouteTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    public void Dispose()
    {
        Infrastructure.EngineEnvironment.FileeRenderer = false;
        fx.Catalog.Refresh();
    }

    [Fact]
    public async Task With_the_renderer_on_a_presentation_is_drawn_by_the_slide_renderer()
    {
        Infrastructure.EngineEnvironment.FileeRenderer = true;
        fx.Catalog.Refresh();
        var planner = fx.Catalog.CreatePlanner();
        foreach (var format in OdfDrawReader.Presentations)
        {
            var step = Assert.Single(planner.Plan(format, "pdf")!.Steps);
            Assert.Equal("layout", step.Converter.Id);
            Assert.True(step.Checked);
        }
        Assert.Equal("drawing", Assert.Single(planner.Plan("odg", "svg")!.Steps).Converter.Id);

        var source = OdfDrawTests.TextDeck().Save(Path.Combine(fx.NewFolder(), "deck.odp"));
        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "pdf" });
        Assert.Equal(JobState.Completed, job.State);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(job.Outputs.Single());
        Assert.Equal(1, pdf.NumberOfPages);
        Assert.Equal(28 * 72 / 2.54, pdf.GetPage(1).Width, 0.5);
        Assert.Contains("Agenda", pdf.GetPage(1).Text);
    }
}
