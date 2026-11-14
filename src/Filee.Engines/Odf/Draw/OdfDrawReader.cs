// OpenDocument presentations (.odp, .otp, .fodp) and drawings (.odg, .fodg) → the slide model (Slides/SlideModel.cs),
// read straight from the package or flat file per OpenDocument 1.3 Part 3, for pictures of each page drawn by the slide
// renderer (presentations through FileeRendererConverter, drawings through DrawingConverter) without LibreOffice.
//
// What is read:
// - Pages (draw:page) in order, hidden slides left out (as LibreOffice's PDF export does); the page size of the first
//   page's master (style:master-page → style:page-layout).
// - Backgrounds: the page's drawing-page style, else its master's (unless presentation:background-visible is off).
// - Master pages: their shapes behind each page's (unless presentation:background-objects-visible is off). Their
//   presentation placeholders are templates, not drawn, apart from the footer, date and slide number when the page
//   shows them (presentation:display-footer / -date-time / -page-number), with the page's footer and fixed date
//   declarations and its number filled in.
// - Placeholders (presentation:class): empty ones (presentation:placeholder) are left out; one without a position takes
//   its master's; text takes the presentation style chain, outline levels the master's outline styles ("<master>-
//   outline2" …).
// - Shapes: draw:frame (text box, picture, table, an embedded object's replacement picture), draw:custom-shape
//   (OdfEnhancedGeometry), draw:rect (corner radius), draw:ellipse / draw:circle (full, section, cut, arc),
//   draw:line, draw:polyline, draw:polygon, draw:path (SVG path data), draw:connector and draw:measure (straight),
//   draw:g groups, draw:a links (their content); placement by svg:x / svg:y / svg:width / svg:height and
//   draw:transform (rotate, translate, scale, skew, matrix), turned and mirrored as the model has it.
// - Fills, lines and text frames through the graphic and presentation style chains (OdfGraphicStyles, OdfDrawText).
// Left out: charts, OLE objects, 3D scenes, Fontwork, shadows, form controls, notes, animations; the capability check
// (FirstUnsupported, a pass of this reader that draws nothing) names the first of them on a shown page, so such a file
// can keep LibreOffice's route.
//
// Hostile input: the package through DocumentPackage, a flat file through SafeXml (OdfDrawPackage); pages up to
// MaxPages; DocumentBudget for paragraphs, objects and nesting (groups, lists); a character count for text; limits for
// the whole deck on path commands (every use of a shape counts its tokens, equations or points again) and on elements
// visited (a master's shapes are visited again for every page on it); repeated table rows and cells are capped.
// Enhanced geometry is parsed once per element, styles' chains once per style.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Slides;
using SkiaSharp;
using OdfNames = Filee.Engines.Hwp.Hwpx.Odt.Odf;

namespace Filee.Engines.Odf.Draw;

internal sealed partial class OdfDrawReader
{
    /// <summary>Presentation formats (decks through the slide renderer).</summary>
    public static readonly string[] Presentations = ["odp", "otp", "fodp"];

    /// <summary>Drawing formats (pages through the drawing converter).</summary>
    public static readonly string[] Drawings = ["odg", "fodg"];

    /// <summary>Path commands and points of all shapes of a deck at most, every use counting again.</summary>
    private const int MaxDeckCommands = 2_000_000;

    /// <summary>Elements visited per XML element the document may hold: a master's shapes are visited for every page on it.</summary>
    private const int VisitsPerElement = 4;

    /// <summary>Operations of one draw:transform read at most.</summary>
    private const int MaxTransformOperations = 16;

    /// <summary>The largest page side, in points (200 inches).</summary>
    private const float MaxPageSide = 14400;

    /// <summary>Lengths of shapes are cut at ±10 km in points, so sums stay finite.</summary>
    private const float MaxLength = 28_000_000;

    private static readonly XNamespace Office = OdfNames.Office;
    private static readonly XNamespace Style = OdfNames.Style;
    private static readonly XNamespace Text = OdfNames.Text;
    private static readonly XNamespace Table = OdfNames.Table;
    private static readonly XNamespace Draw = OdfNames.Draw;
    private static readonly XNamespace Svg = OdfNames.Svg;
    private static readonly XNamespace Fo = OdfNames.Fo;
    private static readonly XNamespace XLink = OdfNames.XLink;
    private static readonly XNamespace Presentation = OdfNames.Presentation;
    private static readonly XNamespace Dr3d = OdfNames.Dr3d;
    private static readonly XNamespace Chart = OdfNames.Chart;

    /// <summary>Placeholder classes of a master page that are templates for the pages, not drawn on them.</summary>
    private static readonly HashSet<string> TemplateClasses =
        ["title", "outline", "subtitle", "text", "graphic", "object", "chart", "table", "orgchart", "page", "notes", "handout", "vertical_title", "vertical_outline"];

    private readonly OdfDrawPackage _package;
    private readonly OdfGraphicStyles _styles;
    private readonly DocumentBudget _budget;
    private readonly CancellationToken _cancellationToken;
    private readonly bool _scan;
    private readonly long _maxVisits;
    private readonly Dictionary<XElement, OdfEnhancedGeometry> _geometries = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, string> _footers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _dates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _headers = new(StringComparer.Ordinal);
    private long _commands;
    private long _visits;

    /// <summary>The first thing on a shown page this reader doesn't draw, found while reading.</summary>
    private string? _unsupported;

    private OdfDrawReader(OdfDrawPackage package, CancellationToken cancellationToken, bool scan)
    {
        _package = package;
        _styles = new OdfGraphicStyles(package);
        _budget = new DocumentBudget(package.Limits);
        _cancellationToken = cancellationToken;
        _scan = scan;
        _maxVisits = (long)VisitsPerElement * package.Limits.MaxXmlElements;
    }

    /// <summary>Reads the presentation or drawing at <paramref name="path"/>.</summary>
    public static SlideDeck Read(string path, DocumentLimits? limits = null, CancellationToken cancellationToken = default)
    {
        using var package = OdfDrawPackage.Open(path, limits);
        return new OdfDrawReader(package, cancellationToken, scan: false).ReadDeck();
    }

    /// <summary>
    /// The first thing on a shown page the reader doesn't draw (charts, OLE objects, 3D scenes, ...); null when it draws
    /// everything, or when the file can't be read (the conversion then stops with the reader's own reason).
    /// </summary>
    public static string? FirstUnsupported(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            using var package = OdfDrawPackage.Open(path);
            var reader = new OdfDrawReader(package, cancellationToken, scan: true);
            reader.ReadDeck();
            return reader._unsupported;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or Core.Conversion.ConversionRefusedException)
        {
            return null;
        }
    }

    private void Unsupported(string feature) => _unsupported ??= feature;

    private static float? Points(string? value) => OdfGraphicStyles.Points(value) is { } points ? Math.Clamp(points, -MaxLength, MaxLength) : null;

    private static string? Attr(XElement? element, XName name) => (string?)element?.Attribute(name);

    private void Visit()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (++_visits > _maxVisits)
            throw DocumentLimits.Exceeded($"The drawing's pages show more than {DocumentLimits.Count(_maxVisits)} shapes in all.");
    }

    private void Commands(long count)
    {
        _commands += count;
        if (_commands > MaxDeckCommands)
            throw DocumentLimits.Exceeded($"The drawing's shapes have more than {DocumentLimits.Count(MaxDeckCommands)} path commands and points.");
    }

    // ───────────────────────── Deck and pages ─────────────────────────

    /// <summary>What a page's elements share: its number, master and the footer texts it shows.</summary>
    private sealed class PageContext
    {
        public int Number { get; init; }
        public int Count { get; init; }
        public string? MasterName { get; init; }
        public XElement? Master { get; init; }
        public string? Footer { get; init; }
        public string? DateTime { get; init; }
        public string? Header { get; init; }
        public bool ShowFooter { get; init; }
        public bool ShowDateTime { get; init; }
        public bool ShowPageNumber { get; init; }
        public bool ShowHeader { get; init; }
    }

    private SlideDeck ReadDeck()
    {
        var body = _package.Content.Element(Office + "body");
        var document = body?.Element(Office + "presentation") ?? body?.Element(Office + "drawing")
                       ?? throw new InvalidDataException("The file is not an OpenDocument presentation or drawing (no office:presentation or office:drawing).");
        foreach (var declaration in document.Elements())
        {
            if (Attr(declaration, Presentation + "name") is not { } name)
                continue;
            if (declaration.Name == Presentation + "footer-decl")
                _footers.TryAdd(name, declaration.Value);
            else if (declaration.Name == Presentation + "header-decl")
                _headers.TryAdd(name, declaration.Value);
            // A date that shows the current date is left out: the picture would change with the day it is made.
            else if (declaration.Name == Presentation + "date-time-decl" && Attr(declaration, Presentation + "source") == "fixed")
                _dates.TryAdd(name, declaration.Value);
        }

        var pages = document.Elements(Draw + "page").ToList();
        SlideDeck? deck = null;
        for (var index = 0; index < pages.Count && !(_scan && _unsupported is not null); index++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var page = pages[index];
            var masterName = Attr(page, Draw + "master-page-name");
            var master = masterName is not null ? _styles.MasterPages.GetValueOrDefault(masterName) : null;
            master ??= _styles.MasterPages.Values.FirstOrDefault();
            masterName = Attr(master, Style + "name") ?? masterName;
            var pageProps = OdfGraphicStyles.Props(_styles.Chain("drawing-page", Attr(page, Draw + "style-name"), OdfScope.Content, false), "drawing-page-properties");
            if (pageProps[Presentation + "visibility"] == "hidden")
                continue;
            deck ??= NewDeck(master);
            if (deck.Slides.Count >= _package.Limits.MaxPages)
                throw DocumentLimits.Exceeded($"The file has more than {DocumentLimits.Count(_package.Limits.MaxPages)} pages.");
            var context = new PageContext
            {
                Number = index + 1,
                Count = pages.Count,
                MasterName = masterName,
                Master = master,
                Footer = _footers.GetValueOrDefault(Attr(page, Presentation + "use-footer-name") ?? ""),
                DateTime = _dates.GetValueOrDefault(Attr(page, Presentation + "use-date-time-name") ?? ""),
                Header = _headers.GetValueOrDefault(Attr(page, Presentation + "use-header-name") ?? ""),
                ShowFooter = pageProps[Presentation + "display-footer"] == "true",
                ShowDateTime = pageProps[Presentation + "display-date-time"] == "true",
                ShowPageNumber = pageProps[Presentation + "display-page-number"] == "true",
                ShowHeader = pageProps[Presentation + "display-header"] == "true",
            };
            deck.Slides.Add(ReadPage(page, pageProps, context));
        }
        return deck ?? NewDeck(_styles.MasterPages.Values.FirstOrDefault());
    }

    private SlideDeck NewDeck(XElement? master)
    {
        var layout = _styles.PageLayout(Attr(master, Style + "page-layout-name"));
        var drawing = _package.Content.Element(Office + "body")?.Element(Office + "drawing") is not null;
        // LibreOffice's defaults: a 28 × 15.75 cm slide, an A4 drawing.
        var width = Points(Attr(layout, Fo + "page-width")) is > 0 and var w ? w : drawing ? 595.28f : 793.7f;
        var height = Points(Attr(layout, Fo + "page-height")) is > 0 and var h ? h : drawing ? 841.89f : 446.46f;
        return new SlideDeck
        {
            Width = Math.Clamp(width, 1, MaxPageSide),
            Height = Math.Clamp(height, 1, MaxPageSide),
            Title = _package.Title,
        };
    }

    private Slide ReadPage(XElement page, OdfProps pageProps, PageContext context)
    {
        var masterProps = OdfGraphicStyles.Props(_styles.Chain("drawing-page", Attr(context.Master, Draw + "style-name"), OdfScope.Styles, false),
            "drawing-page-properties");
        var slide = new Slide();
        // A page fill of its own; without one (or with none) the master's shows through.
        if (pageProps[Draw + "fill"] is { } fill && fill != "none")
            slide.Background = _styles.Fill(pageProps, solid: false);
        else if (pageProps[Presentation + "background-visible"] != "false")
            slide.Background = _styles.Fill(masterProps, solid: false);
        if (context.Master is { } master && pageProps[Presentation + "background-objects-visible"] != "false")
            Elements(master.Elements(), slide.Elements, context, OdfScope.Styles, onMaster: true);
        Elements(page.Elements(), slide.Elements, context, OdfScope.Content, onMaster: false);
        return slide;
    }

    // ───────────────────────── Elements ─────────────────────────

    private void Elements(IEnumerable<XElement> elements, List<SlideElement> output, PageContext context, OdfScope scope, bool onMaster)
    {
        foreach (var element in elements)
        {
            // The check stops at the first thing it finds.
            if (_scan && _unsupported is not null)
                return;
            if (element.Name == Dr3d + "scene")
            {
                Unsupported("3D scenes");
                continue;
            }
            if (element.Name.Namespace != Draw)
                continue; // presentation:notes, animations, forms
            Visit();
            if (Attr(element, Presentation + "class") is { } placeholder)
            {
                if (onMaster && !ShownOnPage(placeholder, context))
                    continue;
                if (!onMaster && Attr(element, Presentation + "placeholder") == "true")
                    continue; // an empty placeholder ("click to add text") isn't drawn
            }
            if (Element(element, context, scope, onMaster) is { } item)
                output.Add(item);
        }
    }

    /// <summary>Whether a placeholder of a master page shows on the page: footer, date and number when the page says so.</summary>
    private static bool ShownOnPage(string placeholder, PageContext context) => placeholder switch
    {
        "footer" => context.ShowFooter,
        "date-time" => context.ShowDateTime,
        "page-number" => context.ShowPageNumber,
        "header" => context.ShowHeader,
        _ => !TemplateClasses.Contains(placeholder),
    };

    private SlideElement? Element(XElement element, PageContext context, OdfScope scope, bool onMaster)
    {
        switch (element.Name.LocalName)
        {
            case "g":
                return Group(element, context, scope, onMaster);
            case "a":
                {
                    // A link around shapes: the shapes.
                    var group = new SlideGroup();
                    using (_budget.Enter())
                        Elements(element.Elements(), group.Children, context, scope, onMaster);
                    return Whole(group);
                }
            case "frame":
                return Frame(element, context, scope);
            case "custom-shape":
                return CustomShape(element, context, scope);
            case "rect" or "ellipse" or "circle":
                return BasicShape(element, context, scope);
            case "line" or "connector" or "measure":
                return Line(element, context, scope);
            case "polyline" or "polygon" or "path":
                return PathShape(element, context, scope);
            case "control":
                Unsupported("form controls");
                return null;
            default:
                return null;
        }
    }

    /// <summary>A group: its children keep the page's coordinates.</summary>
    private SlideGroup? Group(XElement element, PageContext context, OdfScope scope, bool onMaster)
    {
        _budget.Object();
        var group = new SlideGroup();
        using (_budget.Enter())
            Elements(element.Elements(), group.Children, context, scope, onMaster);
        return group.Children.Count == 0 ? null : Whole(group);
    }

    /// <summary>A group whose child space is its parent's: the identity map over the box its children take (turns left aside).</summary>
    private static SlideGroup Whole(SlideGroup group)
    {
        var box = SKRect.Empty;
        var first = true;
        foreach (var child in group.Children)
        {
            box = first ? child.Bounds : new SKRect(Math.Min(box.Left, child.Bounds.Left), Math.Min(box.Top, child.Bounds.Top),
                Math.Max(box.Right, child.Bounds.Right), Math.Max(box.Bottom, child.Bounds.Bottom));
            first = false;
        }
        if (box.Width <= 0 || box.Height <= 0)
            box = new SKRect(box.Left, box.Top, box.Left + Math.Max(box.Width, 1), box.Top + Math.Max(box.Height, 1));
        group.Bounds = box;
        group.ChildBounds = box;
        return group;
    }

    /// <summary>The style chain of a shape: the graphic default, its graphic style, then its presentation style.</summary>
    private List<XElement> StyleChain(XElement element, PageContext context, OdfScope scope)
    {
        var chain = _styles.Chain("graphic", Attr(element, Draw + "style-name"), scope, withDefault: true);
        var presentation = Attr(element, Presentation + "style-name");
        // A placeholder without a style of its own takes its master's.
        if (presentation is null && Attr(element, Presentation + "class") is { } placeholder && MasterPlaceholder(context, placeholder) is { } fromMaster)
            presentation = Attr(fromMaster, Presentation + "style-name");
        if (presentation is null)
            return chain;
        return [.. chain, .. _styles.Chain("presentation", presentation, presentation == Attr(element, Presentation + "style-name") ? scope : OdfScope.Styles, false)];
    }

    /// <summary>The master page's placeholder of a class.</summary>
    private static XElement? MasterPlaceholder(PageContext context, string placeholder) =>
        context.Master?.Elements().FirstOrDefault(e => Attr(e, Presentation + "class") == placeholder);

    // ───────────────────────── Placement ─────────────────────────

    /// <summary>Where an element is: its box (before turning), the turn (degrees clockwise) and mirroring.</summary>
    private readonly record struct Placement(SKRect Bounds, float Rotation, bool FlipHorizontal, bool FlipVertical);

    /// <summary>
    /// The placement of a shape from svg:x, svg:y, svg:width, svg:height and draw:transform (applied after the
    /// position, operation by operation); a placeholder without a size takes its master's. Null when it can't be placed.
    /// </summary>
    private static Placement? Place(XElement element, PageContext context)
    {
        var source = element;
        if (element.Attribute(Svg + "width") is null && Attr(element, Presentation + "class") is { } placeholder
            && MasterPlaceholder(context, placeholder) is { } master && !ReferenceEquals(master, element))
            source = master;
        var width = Math.Clamp(Points(Attr(source, Svg + "width")) ?? 0, 0, MaxLength);
        var height = Math.Clamp(Points(Attr(source, Svg + "height")) ?? 0, 0, MaxLength);
        var matrix = SKMatrix.CreateTranslation(Points(Attr(source, Svg + "x")) ?? 0, Points(Attr(source, Svg + "y")) ?? 0);
        if (Attr(source, Draw + "transform") is { } transform)
            matrix = Transform(transform, matrix);
        return Decompose(matrix, width, height);
    }

    /// <summary>A box of <paramref name="width"/> × <paramref name="height"/> mapped by <paramref name="matrix"/>, as the model places it.</summary>
    private static Placement? Decompose(SKMatrix matrix, float width, float height)
    {
        var ex = matrix.MapVector(1, 0);
        var ey = matrix.MapVector(0, 1);
        var center = matrix.MapPoint(width / 2, height / 2);
        var sx = ex.Length;
        var sy = ey.Length;
        if (!float.IsFinite(sx) || !float.IsFinite(sy) || !float.IsFinite(center.X) || !float.IsFinite(center.Y))
            return null;
        var w = Math.Clamp(width * sx, 0, MaxLength);
        var h = Math.Clamp(height * sy, 0, MaxLength);
        var rotation = sx > 0 ? MathF.Atan2(ex.Y, ex.X) * 180 / MathF.PI : 0;
        if (rotation < 0)
            rotation += 360;
        var mirrored = ex.X * ey.Y - ex.Y * ey.X < 0;
        return new Placement(SKRect.Create(center.X - w / 2, center.Y - h / 2, w, h), Math.Abs(rotation) < 1e-4 || Math.Abs(rotation - 360) < 1e-4 ? 0 : rotation,
            false, mirrored);
    }

    /// <summary>
    /// <paramref name="matrix"/> followed by the operations of a draw:transform: rotate (radians, counter-clockwise as
    /// seen), translate and matrix (lengths with units), scale, skewX, skewY.
    /// </summary>
    private static SKMatrix Transform(string transform, SKMatrix matrix)
    {
        var at = 0;
        for (var count = 0; count < MaxTransformOperations && at < transform.Length; count++)
        {
            var open = transform.IndexOf('(', at);
            var close = open < 0 ? -1 : transform.IndexOf(')', open);
            if (close < 0)
                break;
            var name = transform[at..open].Trim();
            var arguments = transform[(open + 1)..close].Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
            at = close + 1;
            double Number(int i) => i < arguments.Length && double.TryParse(arguments[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
                ? Math.Clamp(v, -1e6, 1e6)
                : 0;
            float Length(int i) => i < arguments.Length ? Points(arguments[i]) ?? 0 : 0;
            SKMatrix? operation = name switch
            {
                "rotate" => SKMatrix.CreateRotation((float)-Number(0)),
                "translate" => SKMatrix.CreateTranslation(Length(0), Length(1)),
                "scale" => SKMatrix.CreateScale((float)Number(0), (float)(arguments.Length > 1 ? Number(1) : Number(0))),
                "skewX" => SKMatrix.CreateSkew((float)Math.Tan(-Number(0)), 0),
                "skewY" => SKMatrix.CreateSkew(0, (float)Math.Tan(-Number(0))),
                "matrix" => new SKMatrix((float)Number(0), (float)Number(2), Length(4), (float)Number(1), (float)Number(3), Length(5), 0, 0, 1),
                _ => null,
            };
            if (operation is { } op)
                matrix = matrix.PostConcat(op);
        }
        return matrix;
    }

    private static SlideShape Placed(Placement place) => new()
    {
        Bounds = place.Bounds,
        Rotation = place.Rotation,
        FlipHorizontal = place.FlipHorizontal,
        FlipVertical = place.FlipVertical,
    };

    private static SlidePath Rectangle(SKRect bounds, float radius = 0)
    {
        var path = new SKPath();
        var box = new SKRect(0, 0, bounds.Width, bounds.Height);
        if (radius > 0)
            path.AddRoundRect(box, Math.Min(radius, box.Width / 2), Math.Min(radius, box.Height / 2));
        else
            path.AddRect(box);
        return new SlidePath(path);
    }

    // ───────────────────────── Shapes ─────────────────────────

    private SlideShape? CustomShape(XElement element, PageContext context, OdfScope scope)
    {
        _budget.Object();
        if (Place(element, context) is not { } place)
            return null;
        var chain = StyleChain(element, context, scope);
        var props = OdfGraphicStyles.Props(chain, "graphic-properties");
        Shadow(props);
        var shape = Placed(place);
        shape.Fill = _styles.Fill(props, solid: true);
        shape.Line = _styles.Line(props, stroke: true);

        SKRect? textArea = null;
        float textRotation = 0;
        if (element.Element(Draw + "enhanced-geometry") is { } geometryElement)
        {
            if (!_geometries.TryGetValue(geometryElement, out var geometry))
                _geometries[geometryElement] = geometry = OdfEnhancedGeometry.Parse(geometryElement);
            if (geometry.Extruded)
                Unsupported("3D effects");
            if (geometry.Fontwork)
                Unsupported("Fontwork");
            if (!geometry.HasPath && OdfEnhancedGeometry.Preset(geometry.Type) is null)
                Unsupported($"the shape {geometry.Type}");
            Commands(geometry.Cost);
            var result = geometry.Evaluate(shape.Bounds.Width, shape.Bounds.Height, shape.Fill is not null, shape.Line is not null);
            shape.Paths = result.Paths;
            textArea = result.TextRect;
            textRotation = (float)-geometry.TextRotation;
            shape.FlipHorizontal ^= geometry.MirrorHorizontal;
            shape.FlipVertical ^= geometry.MirrorVertical;
        }
        else
        {
            shape.Paths = [Rectangle(shape.Bounds)];
        }
        shape.Text = TextFrame(element, chain, context, scope, textArea, textRotation);
        return shape.Fill is null && shape.Line is null && shape.Text is null ? null : shape;
    }

    private SlideShape? BasicShape(XElement element, PageContext context, OdfScope scope)
    {
        _budget.Object();
        if (Place(element, context) is not { } place)
            return null;
        var chain = StyleChain(element, context, scope);
        var props = OdfGraphicStyles.Props(chain, "graphic-properties");
        Shadow(props);
        var shape = Placed(place);
        shape.Fill = _styles.Fill(props, solid: true);
        shape.Line = _styles.Line(props, stroke: true);
        if (element.Name.LocalName == "rect")
        {
            shape.Paths = [Rectangle(shape.Bounds, Math.Max(0, Points(Attr(element, Draw + "corner-radius")) ?? 0))];
        }
        else
        {
            var box = new SKRect(0, 0, shape.Bounds.Width, shape.Bounds.Height);
            var path = new SKPath();
            var kind = Attr(element, Draw + "kind") ?? "full";
            var start = OdfGraphicStyles.Angle(Attr(element, Draw + "start-angle") is { } s && !s.Any(char.IsAsciiLetter) ? s + "deg" : Attr(element, Draw + "start-angle"));
            var end = OdfGraphicStyles.Angle(Attr(element, Draw + "end-angle") is { } e && !e.Any(char.IsAsciiLetter) ? e + "deg" : Attr(element, Draw + "end-angle"));
            var sweep = (end - start) % 360;
            if (sweep <= 0)
                sweep += 360;
            // ODF angles turn counter-clockwise as seen, Skia's clockwise.
            if (kind == "full" || box.Width <= 0 || box.Height <= 0)
            {
                path.AddOval(box);
                shape.Paths = [new SlidePath(path)];
            }
            else
            {
                if (kind == "section")
                    path.MoveTo(box.MidX, box.MidY);
                path.ArcTo(box, -start, -sweep, kind != "section");
                if (kind is "section" or "cut")
                    path.Close();
                shape.Paths = [new SlidePath(path, kind == "arc" ? PathFill.None : PathFill.Normal)];
            }
        }
        shape.Text = TextFrame(element, chain, context, scope, null, 0);
        return shape.Fill is null && shape.Line is null && shape.Text is null ? null : shape;
    }

    /// <summary>A line, a connector or a dimension line: straight from (x1, y1) to (x2, y2).</summary>
    private SlideShape? Line(XElement element, PageContext context, OdfScope scope)
    {
        _budget.Object();
        var a = new SKPoint(Points(Attr(element, Svg + "x1")) ?? 0, Points(Attr(element, Svg + "y1")) ?? 0);
        var b = new SKPoint(Points(Attr(element, Svg + "x2")) ?? 0, Points(Attr(element, Svg + "y2")) ?? 0);
        if (Attr(element, Draw + "transform") is { } transform)
        {
            var matrix = Transform(transform, SKMatrix.Identity);
            (a, b) = (matrix.MapPoint(a), matrix.MapPoint(b));
        }
        if (!float.IsFinite(a.X) || !float.IsFinite(a.Y) || !float.IsFinite(b.X) || !float.IsFinite(b.Y))
            return null;
        var props = OdfGraphicStyles.Props(StyleChain(element, context, scope), "graphic-properties");
        Shadow(props);
        var bounds = new SKRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        var path = new SKPath();
        path.MoveTo(a.X - bounds.Left, a.Y - bounds.Top);
        path.LineTo(b.X - bounds.Left, b.Y - bounds.Top);
        var shape = new SlideShape { Bounds = bounds, Paths = [new SlidePath(path, PathFill.None)], Line = _styles.Line(props, stroke: true) };
        return shape.Line is null ? null : shape;
    }

    /// <summary>draw:polyline, draw:polygon (draw:points) and draw:path (svg:d) in their svg:viewBox, stretched over the box.</summary>
    private SlideShape? PathShape(XElement element, PageContext context, OdfScope scope)
    {
        _budget.Object();
        if (Place(element, context) is not { } place)
            return null;
        var chain = StyleChain(element, context, scope);
        var props = OdfGraphicStyles.Props(chain, "graphic-properties");
        Shadow(props);
        var shape = Placed(place);
        var (vx, vy, vw, vh) = ViewBox(Attr(element, Svg + "viewBox"), shape.Bounds);
        var sx = vw > 0 ? shape.Bounds.Width / vw : 1;
        var sy = vh > 0 ? shape.Bounds.Height / vh : 1;
        SKPoint Map(double x, double y) => new((float)Math.Clamp((x - vx) * sx, -MaxLength, MaxLength), (float)Math.Clamp((y - vy) * sy, -MaxLength, MaxLength));

        SKPath path;
        bool closed;
        if (element.Name.LocalName == "path")
        {
            var d = Attr(element, Svg + "d") ?? "";
            path = OdfPathData.Parse(d, Map, sx, sy, OdfEnhancedGeometry.MaxTokens, out var commands);
            Commands(commands + 1);
            closed = d.Contains('z') || d.Contains('Z');
        }
        else
        {
            path = new SKPath();
            var points = OdfPathData.Points(Attr(element, Draw + "points"), OdfEnhancedGeometry.MaxTokens);
            Commands(points.Count + 1);
            for (var i = 0; i < points.Count; i++)
            {
                var point = Map(points[i].X, points[i].Y);
                if (i == 0)
                    path.MoveTo(point);
                else
                    path.LineTo(point);
            }
            closed = element.Name.LocalName == "polygon";
            if (closed && points.Count > 0)
                path.Close();
        }
        shape.Paths = [new SlidePath(path, closed ? PathFill.Normal : PathFill.None)];
        shape.Fill = closed ? _styles.Fill(props, solid: true) : null;
        shape.Line = _styles.Line(props, stroke: true);
        shape.Text = TextFrame(element, chain, context, scope, null, 0);
        return shape.Fill is null && shape.Line is null && shape.Text is null ? null : shape;
    }

    /// <summary>svg:viewBox ("x y width height"); without one, the box in 1/100 mm as LibreOffice writes it.</summary>
    private static (double X, double Y, double Width, double Height) ViewBox(string? value, SKRect bounds)
    {
        var parts = (value ?? "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? Math.Clamp(v, -1e12, 1e12) : 0)
            .ToArray();
        return parts is [var x, var y, var w, var h, ..] && w > 0 && h > 0 ? (x, y, w, h) : (0, 0, bounds.Width / 72 * 2540, bounds.Height / 72 * 2540);
    }

    private void Shadow(OdfProps props)
    {
        if (props[Draw + "shadow"] == "visible")
            Unsupported("shadows");
    }

    // ───────────────────────── Frames ─────────────────────────

    /// <summary>A frame: a text box, a picture, a table, or an embedded object (its replacement picture).</summary>
    private SlideElement? Frame(XElement element, PageContext context, OdfScope scope)
    {
        _budget.Object();
        if (Place(element, context) is not { } place)
            return null;
        var chain = StyleChain(element, context, scope);
        var props = OdfGraphicStyles.Props(chain, "graphic-properties");
        Shadow(props);
        var content = element.Elements().FirstOrDefault(e => e.Name == Draw + "text-box" || e.Name == Draw + "image" || e.Name == Draw + "object"
                                                             || e.Name == Draw + "object-ole" || e.Name == Table + "table");
        if (content is null)
            return null;
        if (content.Name == Table + "table")
            return OdfTable(content, place, chain, context, scope);

        var shape = Placed(place);
        shape.Fill = _styles.Fill(props, solid: false);
        shape.Line = _styles.Line(props, stroke: false);
        var radius = Math.Max(0, Points(Attr(content, Draw + "corner-radius") ?? Attr(element, Draw + "corner-radius")) ?? 0);
        shape.Paths = [Rectangle(shape.Bounds, radius)];
        if (content.Name == Draw + "text-box")
        {
            shape.Text = TextFrame(content, chain, context, scope, null, 0);
            return shape.Fill is null && shape.Line is null && shape.Text is null ? null : shape;
        }
        if (content.Name == Draw + "object" || content.Name == Draw + "object-ole")
            Unsupported(ObjectKind(content));

        // A picture, or an object's replacement picture: the first one Filee can draw.
        var images = element.Elements(Draw + "image").ToList();
        foreach (var image in images)
        {
            if (_styles.Picture(image) is { } bytes && SlideImages.CanDraw(bytes, _package.Limits))
            {
                shape.Fill = new PictureFill(new SlideImage(bytes), SKRect.Empty, SKRect.Empty);
                shape.IsPicture = true;
                return shape;
            }
        }
        if (images.Count > 0 && content.Name == Draw + "image")
            Unsupported(PictureKind(Attr(images[0], XLink + "href")));
        return shape.Fill is null && shape.Line is null ? null : shape;
    }

    /// <summary>What an embedded object is: a chart, or another kind of object.</summary>
    private string ObjectKind(XElement embedded)
    {
        if (embedded.Name == Draw + "object-ole")
            return "OLE objects";
        var root = embedded.Element(Office + "document") ?? _package.XmlPart(Attr(embedded, XLink + "href"), "content.xml");
        var body = root?.Element(Office + "body");
        return body?.Element(Office + "chart") is not null || root?.Descendants(Chart + "chart").Any() == true ? "charts" : "embedded objects";
    }

    private static string PictureKind(string? href) => Path.GetExtension(href ?? "").ToLowerInvariant() switch
    {
        ".svg" => "SVG pictures",
        ".tif" or ".tiff" => "TIFF pictures",
        _ => "pictures it can't decode",
    };
}
