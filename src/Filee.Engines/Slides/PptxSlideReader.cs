// PPTX / PPTM / POTX / PPSX → the slide model (SlideModel.cs), for pictures of each slide (PDF, PNG, JPG) drawn by
// SlideRenderer without LibreOffice. PptxReader (Hwp/Hwpx/Pptx) stays the reader for the document writers: it turns a
// deck into flowing pages of text boxes, which is right for DOCX / HWPX output but not for a picture of each slide.
//
// What is read:
// - The presentation's slide size and slides in order (hidden ones left out, as PowerPoint's own PDF export does).
// - Each slide's layout, master and theme: background (slide → layout → master; bgPr fills and bgRef theme styles),
//   the master's and the layout's own shapes under the slide's (showMasterSp), placeholders inheriting position,
//   geometry, fill, line, text body properties and list styles from the layout's and master's placeholders, the
//   master's text styles and the presentation's default text style; theme colours under the colour map (with the
//   layout's and slide's overrides), theme fonts (+mj-lt, +mn-ea, script fonts by language) and theme style
//   references (fillRef, lnRef, fontRef, bgRef).
// - Shapes (preset and custom geometry), connectors, pictures (crop, fill rectangle, tiles, recolouring), groups, tables
//   (with tableStyles.xml styles), SmartArt from the drawing PowerPoint keeps with it, OLE objects and charts as the
//   picture stored for them, mc:AlternateContent through its fallback.
// - Fills: solid, gradient (linear, circle, rectangle; group fill), picture, pattern. Lines: width, dash, cap, join,
//   compound, arrowheads.
// Shadows, glow, reflection, 3D and WordArt warps are left out; the capability check (SlideCapabilities) names them.
//
// Hostile input: parts through DocumentPackage (sizes, inflate ratio, XML depth and elements), DocumentBudget for
// paragraphs, objects and group nesting, and a limit on path commands for the whole deck.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Documents;
using SkiaSharp;

namespace Filee.Engines.Slides;

internal sealed partial class PptxSlideReader
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = DrawingColors.A;
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Dsp = "http://schemas.microsoft.com/office/drawing/2008/diagram";

    /// <summary>EMU per point.</summary>
    private const double EmuPerPoint = 12700;

    /// <summary>Path commands of custom geometry in one deck at most.</summary>
    private const int MaxDeckCommands = 2_000_000;

    private readonly SlidePackage _package;
    private readonly DocumentBudget _budget;
    private readonly CancellationToken _cancellationToken;
    private XElement? _defaultTextStyle;
    private XElement? _tableStyles;
    private int _commands;

    private PptxSlideReader(SlidePackage package, CancellationToken cancellationToken)
    {
        _package = package;
        _budget = new DocumentBudget(package.Limits);
        _cancellationToken = cancellationToken;
    }

    /// <summary>Reads the deck at <paramref name="path"/>.</summary>
    public static SlideDeck Read(string path, DocumentLimits? limits = null, CancellationToken cancellationToken = default)
    {
        using var package = SlidePackage.Open(path, limits);
        return new PptxSlideReader(package, cancellationToken).ReadDeck();
    }

    private static float Points(double emu) => (float)(emu / EmuPerPoint);

    private static double Number(XAttribute? attribute, double fallback = 0) =>
        double.TryParse((string?)attribute, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? Math.Clamp(value, -1e13, 1e13)
            : fallback;

    private static bool Flag(XAttribute? attribute, bool fallback = false) =>
        (string?)attribute switch
        {
            "1" or "true" or "on" => true,
            "0" or "false" or "off" => false,
            _ => fallback,
        };

    // ───────────────────────── Deck and slides ─────────────────────────

    private SlideDeck ReadDeck()
    {
        var presentationPart = _package.MainPart ?? "ppt/presentation.xml";
        var presentation = _package.Part(presentationPart);
        if (presentation is null || presentation.Name != P + "presentation")
            throw new InvalidDataException("The file is not a PowerPoint presentation (no ppt/presentation.xml).");
        _defaultTextStyle = presentation.Element(P + "defaultTextStyle");
        _tableStyles = _package.Part(_package.TargetOfType(presentationPart, "/tableStyles"));
        var size = presentation.Element(P + "sldSz");
        var width = Number(size?.Attribute("cx")) is >= 914400 / 10 and <= 51206400 and var cx ? cx : 12192000;
        var height = Number(size?.Attribute("cy")) is >= 914400 / 10 and <= 51206400 and var cy ? cy : 6858000;
        var deck = new SlideDeck { Width = Points(width), Height = Points(height) };

        var number = 0;
        foreach (var id in presentation.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [])
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var slidePart = _package.Target(presentationPart, (string?)id.Attribute(R + "id"));
            if (_package.Part(slidePart) is not { } slide || slide.Name != P + "sld")
                continue;
            number++;
            if (!Flag(slide.Attribute("show"), true))
                continue;
            deck.Slides.Add(ReadSlide(slidePart!, slide, number));
        }
        return deck;
    }

    /// <summary>What a slide's parts share: its layout, master and theme, colours, fonts and theme styles.</summary>
    private sealed class SlideContext
    {
        public required string SlidePart { get; init; }
        public required XElement Slide { get; init; }
        public string? LayoutPart { get; init; }
        public XElement? Layout { get; init; }
        public string? MasterPart { get; init; }
        public XElement? Master { get; init; }
        public required DrawingColors Colors { get; init; }
        public required ThemeFonts Fonts { get; init; }
        public XElement? FormatScheme { get; init; }
        public int Number { get; init; }
    }

    private Slide ReadSlide(string slidePart, XElement slide, int number)
    {
        var layoutPart = _package.TargetOfType(slidePart, "/slideLayout");
        var layout = _package.Part(layoutPart);
        var masterPart = layoutPart is null ? null : _package.TargetOfType(layoutPart, "/slideMaster");
        var master = _package.Part(masterPart);
        var theme = masterPart is null ? null : _package.Part(_package.TargetOfType(masterPart, "/theme"));
        // The master's colour map, unless the layout or the slide overrides it.
        var map = Override(slide) ?? Override(layout) ?? master?.Element(P + "clrMap");
        var context = new SlideContext
        {
            SlidePart = slidePart,
            Slide = slide,
            LayoutPart = layoutPart,
            Layout = layout,
            MasterPart = masterPart,
            Master = master,
            Colors = DrawingColors.FromTheme(theme).WithMap(map),
            Fonts = ThemeFonts.From(theme),
            FormatScheme = theme?.Descendants(A + "fmtScheme").FirstOrDefault(),
            Number = number,
        };

        var result = new Slide { Background = Background(context) };
        var showMaster = Flag(slide.Attribute("showMasterSp"), true) && Flag(layout?.Attribute("showMasterSp"), true);
        if (showMaster && master is not null)
            Elements(context, masterPart!, Tree(master), result.Elements, templates: true, null);
        if (layout is not null)
            Elements(context, layoutPart!, Tree(layout), result.Elements, templates: true, null);
        Elements(context, slidePart, Tree(slide), result.Elements, templates: false, null);
        return result;
    }

    private static XElement? Override(XElement? part) => part?.Element(P + "clrMapOvr")?.Element(A + "overrideClrMapping");

    private static XElement? Tree(XElement part) => part.Element(P + "cSld")?.Element(P + "spTree");

    /// <summary>The first background of slide, layout and master; null for white.</summary>
    private SlideFill? Background(SlideContext context)
    {
        foreach (var (part, name) in new[] { (context.Slide, context.SlidePart), (context.Layout, context.LayoutPart), (context.Master, context.MasterPart) })
        {
            if (part?.Element(P + "cSld")?.Element(P + "bg") is not { } bg || name is null)
                continue;
            if (bg.Element(P + "bgPr") is { } properties)
                return Fill(context, name, properties, null, null, out _);
            if (bg.Element(P + "bgRef") is { } reference)
                return ThemeFill(context, name, reference);
        }
        return null;
    }

    // ───────────────────────── Elements ─────────────────────────

    /// <summary>Reads the children of a shape tree or group into <paramref name="output"/>.</summary>
    /// <param name="templates">The tree is a master's or layout's: its placeholders are templates and are not drawn.</param>
    /// <param name="groupFill">The fill of the group the elements are in (a:grpFill).</param>
    private void Elements(SlideContext context, string part, XElement? tree, List<SlideElement> output, bool templates, SlideFill? groupFill)
    {
        foreach (var element in tree?.Elements() ?? [])
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var item = element;
            // Newer kinds of content come with a fallback older readers understand; Filee reads the fallback.
            if (element.Name.LocalName == "AlternateContent")
            {
                var chosen = element.Elements().FirstOrDefault(e => e.Name.LocalName == "Fallback")
                             ?? element.Elements().FirstOrDefault(e => e.Name.LocalName == "Choice");
                if (chosen is not null)
                    Elements(context, part, chosen, output, templates, groupFill);
                continue;
            }
            if (templates && Placeholder(item) is not null)
                continue;
            var read = item.Name.LocalName switch
            {
                "sp" or "cxnSp" => Shape(context, part, item, groupFill),
                "pic" => Picture(context, part, item, null),
                "grpSp" => Group(context, part, item, templates, groupFill),
                "graphicFrame" => Frame(context, part, item),
                _ => null,
            };
            if (read is not null)
            {
                _budget.Object();
                output.Add(read);
            }
        }
    }

    private SlideGroup? Group(SlideContext context, string part, XElement group, bool templates, SlideFill? outerFill)
    {
        using var level = _budget.Enter();
        var properties = group.Element(P + "grpSpPr") ?? group.Element(Dsp + "grpSpPr");
        var xfrm = properties?.Element(A + "xfrm");
        var result = new SlideGroup();
        Place(result, xfrm);
        var childOffset = xfrm?.Element(A + "chOff");
        var childSize = xfrm?.Element(A + "chExt");
        result.ChildBounds = childSize is null
            ? result.Bounds
            : SKRect.Create(Points(Number(childOffset?.Attribute("x"))), Points(Number(childOffset?.Attribute("y"))),
                Points(Number(childSize.Attribute("cx"))), Points(Number(childSize.Attribute("cy"))));
        var fill = Fill(context, part, properties, null, outerFill, out _) ?? outerFill;
        Elements(context, part, group, result.Children, templates, fill);
        return result;
    }

    /// <summary>Sets an element's box, rotation and mirroring from an a:xfrm.</summary>
    private static void Place(SlideElement element, XElement? xfrm)
    {
        var offset = xfrm?.Element(A + "off");
        var size = xfrm?.Element(A + "ext");
        element.Bounds = SKRect.Create(Points(Number(offset?.Attribute("x"))), Points(Number(offset?.Attribute("y"))),
            Math.Max(0, Points(Number(size?.Attribute("cx")))), Math.Max(0, Points(Number(size?.Attribute("cy")))));
        element.Rotation = (float)(Number(xfrm?.Attribute("rot")) / 60000 % 360);
        element.FlipHorizontal = Flag(xfrm?.Attribute("flipH"));
        element.FlipVertical = Flag(xfrm?.Attribute("flipV"));
    }

    /// <summary>The p:ph of a shape, picture or frame, if it is a placeholder.</summary>
    private static XElement? Placeholder(XElement element) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("nv", StringComparison.Ordinal))?.Element(P + "nvPr")?.Element(P + "ph");

    /// <summary>
    /// The layout's and master's versions of a slide placeholder: by index, else by type (ctrTitle ↔ title, subTitle,
    /// obj and untyped ↔ body); the layout's own placeholder finds the master's by type.
    /// </summary>
    private static (XElement? Layout, XElement? Master) Inherited(SlideContext context, string part, XElement? ph)
    {
        if (ph is null || part == context.MasterPart)
            return (null, null);
        var type = Kind((string?)ph.Attribute("type"));
        var index = (string?)ph.Attribute("idx");

        static XElement? Find(XElement? owner, string type, string? index, bool byIndex)
        {
            var candidates = (owner is null ? null : Tree(owner))?.Elements().Where(e => e.Name.LocalName is "sp" or "pic" or "graphicFrame" && Placeholder(e) is not null).ToList() ?? [];
            if (byIndex && index is not null && candidates.FirstOrDefault(c => (string?)Placeholder(c)!.Attribute("idx") == index) is { } byIdx)
                return byIdx;
            return candidates.FirstOrDefault(c => Kind((string?)Placeholder(c)!.Attribute("type")) == type);
        }

        var layout = part == context.SlidePart ? Find(context.Layout, type, index, byIndex: true) : null;
        var masterType = layout is not null && Placeholder(layout) is { } layoutPh ? Kind((string?)layoutPh.Attribute("type")) : type;
        return (layout, Find(context.Master, masterType, null, byIndex: false));
    }

    /// <summary>A placeholder type as the master's text styles group them.</summary>
    private static string Kind(string? type) => type switch
    {
        "ctrTitle" or "title" => "title",
        null or "" or "obj" or "subTitle" or "body" => "body",
        _ => type,
    };

    /// <summary>A shape's properties: p:spPr, or dsp:spPr in a SmartArt drawing.</summary>
    private static XElement? Properties(XElement element) =>
        element.Element(P + "spPr") ?? element.Element(Dsp + "spPr");

    // ───────────────────────── Shapes ─────────────────────────

    private SlideShape? Shape(SlideContext context, string part, XElement shape, SlideFill? groupFill)
    {
        var ph = Placeholder(shape);
        var (layoutPh, masterPh) = Inherited(context, part, ph);
        var chain = new[] { shape, layoutPh, masterPh }.OfType<XElement>().ToList();
        var xfrm = chain.Select(e => Properties(e)?.Element(A + "xfrm")).FirstOrDefault(x => x?.Element(A + "ext") is not null);
        if (xfrm is null)
            return null;
        var result = new SlideShape();
        Place(result, xfrm);

        var properties = Properties(shape);
        var style = shape.Element(P + "style") ?? shape.Element(Dsp + "style");
        var geometry = Geometry(chain.Select(Properties).OfType<XElement>(), result.Bounds);
        result.Paths = geometry.Paths;

        // A fill or line of the shape's own, else of the placeholder it inherits from, else of its theme style.
        SlideFill? fill = null;
        var specified = false;
        foreach (var owner in chain)
        {
            fill = Fill(context, part, Properties(owner), null, groupFill, out specified);
            if (specified)
                break;
        }
        if (!specified && style?.Element(A + "fillRef") is { } fillRef)
            fill = ThemeFill(context, part, fillRef);
        result.Fill = fill;
        result.Line = Line(context, part, chain.Select(e => Properties(e)?.Element(A + "ln")).OfType<XElement>().ToList(), style?.Element(A + "lnRef"));

        var body = shape.Element(P + "txBody") ?? shape.Element(Dsp + "txBody");
        if (body is not null)
        {
            var textArea = geometry.TextRect;
            // SmartArt drawings keep the text's own box (dsp:txXfrm) in the drawing's coordinates.
            if (shape.Element(Dsp + "txXfrm") is { } textXfrm)
            {
                var offset = textXfrm.Element(A + "off");
                var size = textXfrm.Element(A + "ext");
                textArea = SKRect.Create(Points(Number(offset?.Attribute("x"))) - result.Bounds.Left, Points(Number(offset?.Attribute("y"))) - result.Bounds.Top,
                    Points(Number(size?.Attribute("cx"))), Points(Number(size?.Attribute("cy"))));
            }
            var frame = TextFrame(context, part, body, ph, layoutPh, masterPh, style?.Element(A + "fontRef"));
            if (frame is not null)
            {
                frame.Area = textArea;
                result.Text = frame;
            }
        }
        if (result.Fill is null && result.Line is null && result.Text is null)
            return null;
        return result;
    }

    private SlideShape? Picture(SlideContext context, string part, XElement picture, SKRect? frame)
    {
        var ph = Placeholder(picture);
        var (layoutPh, masterPh) = Inherited(context, part, ph);
        var chain = new[] { picture, layoutPh, masterPh }.OfType<XElement>().ToList();
        var result = new SlideShape { IsPicture = true };
        if (frame is { } area)
        {
            result.Bounds = area;
        }
        else
        {
            var xfrm = chain.Select(e => Properties(e)?.Element(A + "xfrm")).FirstOrDefault(x => x?.Element(A + "ext") is not null);
            if (xfrm is null)
                return null;
            Place(result, xfrm);
        }
        var blipFill = picture.Element(P + "blipFill") ?? picture.Element(A + "blipFill");
        if (blipFill is null || PictureFill(context, part, blipFill) is not { } fill)
            return null;
        result.Fill = fill;
        result.Paths = Geometry(chain.Select(Properties).OfType<XElement>(), result.Bounds).Paths;
        result.Line = Line(context, part, chain.Select(e => Properties(e)?.Element(A + "ln")).OfType<XElement>().ToList(),
            picture.Element(P + "style")?.Element(A + "lnRef"));
        return result;
    }

    /// <summary>A graphic frame: a table, SmartArt (its stored drawing), or the picture of an OLE object or chart.</summary>
    private SlideElement? Frame(SlideContext context, string part, XElement frame)
    {
        var xfrm = frame.Element(P + "xfrm");
        var data = frame.Element(A + "graphic")?.Element(A + "graphicData");
        if (xfrm is null || data is null)
            return null;
        var uri = (string?)data.Attribute("uri") ?? "";
        if (data.Element(A + "tbl") is { } table)
            return Table(context, part, table, xfrm);
        if (uri.EndsWith("/diagram", StringComparison.Ordinal))
            return SmartArt(context, part, data, xfrm);
        // An OLE object, chart or other content with a picture of it (p:oleObj/p:pic, or a fallback picture).
        if (data.Descendants(P + "pic").FirstOrDefault() is { } picture)
        {
            var placed = new SlideShape();
            Place(placed, xfrm);
            return Picture(context, part, picture, placed.Bounds);
        }
        return null;
    }

    /// <summary>
    /// SmartArt as the drawing PowerPoint stores with it (the data part's dsp:dataModelExt names it), in the frame's
    /// coordinates; null when there is none.
    /// </summary>
    private SlideGroup? SmartArt(SlideContext context, string part, XElement data, XElement xfrm)
    {
        var dataPart = _package.Target(part, (string?)data.Descendants().FirstOrDefault(e => e.Name.LocalName == "relIds")?.Attribute(R + "dm"));
        var drawingId = (string?)_package.Part(dataPart)?.Descendants().FirstOrDefault(e => e.Name.LocalName == "dataModelExt")?.Attribute("relId");
        var drawingPart = _package.Target(part, drawingId);
        if (_package.Part(drawingPart)?.Element(Dsp + "spTree") is not { } tree)
            return null;
        using var level = _budget.Enter();
        var group = new SlideGroup();
        Place(group, xfrm);
        group.ChildBounds = SKRect.Create(0, 0, group.Bounds.Width, group.Bounds.Height);
        Elements(context, drawingPart!, tree, group.Children, templates: false, null);
        return group;
    }

    // ───────────────────────── Geometry ─────────────────────────

    /// <summary>The geometry of the first properties that have one (prstGeom or custGeom); a rectangle by default.</summary>
    private GeometryResult Geometry(IEnumerable<XElement> properties, SKRect bounds)
    {
        var width = bounds.Width * EmuPerPoint;
        var height = bounds.Height * EmuPerPoint;
        foreach (var owner in properties)
        {
            if (owner.Element(A + "prstGeom") is { } preset)
            {
                var definition = PresetShapes.Find((string?)preset.Attribute("prst")) ?? PresetShapes.Find("rect")!;
                return ShapeGeometry.Evaluate(definition, width, height, Adjust(preset.Element(A + "avLst")), 1 / EmuPerPoint);
            }
            if (owner.Element(A + "custGeom") is { } custom)
                return ShapeGeometry.Evaluate(Custom(custom), width, height, null, 1 / EmuPerPoint);
        }
        return ShapeGeometry.Evaluate(PresetShapes.Find("rect")!, width, height, null, 1 / EmuPerPoint);
    }

    private static Dictionary<string, string>? Adjust(XElement? list)
    {
        if (list is null || !list.HasElements)
            return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var guide in list.Elements(A + "gd").Take(ShapeGeometry.MaxGuides))
        {
            if ((string?)guide.Attribute("name") is { } name && (string?)guide.Attribute("fmla") is { } formula)
                result[name] = formula;
        }
        return result;
    }

    /// <summary>Custom geometry (a:custGeom) as a definition.</summary>
    private GeometryDefinition Custom(XElement custom)
    {
        var definition = new GeometryDefinition();
        foreach (var (list, target) in new[] { ("avLst", definition.Adjust), ("gdLst", definition.Guides) })
        {
            foreach (var guide in custom.Element(A + list)?.Elements(A + "gd").Take(ShapeGeometry.MaxGuides) ?? [])
                target.Add(((string?)guide.Attribute("name") ?? "", (string?)guide.Attribute("fmla") ?? ""));
        }
        if (custom.Element(A + "rect") is { } rect)
            definition.TextRect = ((string?)rect.Attribute("l") ?? "l", (string?)rect.Attribute("t") ?? "t", (string?)rect.Attribute("r") ?? "r", (string?)rect.Attribute("b") ?? "b");
        foreach (var path in custom.Element(A + "pathLst")?.Elements(A + "path") ?? [])
        {
            var commands = new List<GeometryCommand>();
            foreach (var command in path.Elements())
            {
                if (++_commands > MaxDeckCommands)
                    throw DocumentLimits.Exceeded($"The presentation's drawings have more than {DocumentLimits.Count(MaxDeckCommands)} path commands.");
                var points = command.Elements(A + "pt").SelectMany(p => new[] { (string?)p.Attribute("x") ?? "0", (string?)p.Attribute("y") ?? "0" }).ToArray();
                switch (command.Name.LocalName)
                {
                    case "moveTo":
                        commands.Add(new GeometryCommand('M', points));
                        break;
                    case "lnTo":
                        commands.Add(new GeometryCommand('L', points));
                        break;
                    case "quadBezTo":
                        commands.Add(new GeometryCommand('Q', points));
                        break;
                    case "cubicBezTo":
                        commands.Add(new GeometryCommand('C', points));
                        break;
                    case "arcTo":
                        commands.Add(new GeometryCommand('A', [(string?)command.Attribute("wR") ?? "0", (string?)command.Attribute("hR") ?? "0",
                            (string?)command.Attribute("stAng") ?? "0", (string?)command.Attribute("swAng") ?? "0"]));
                        break;
                    case "close":
                        commands.Add(new GeometryCommand('Z', []));
                        break;
                }
            }
            definition.Paths.Add(new GeometryPathDefinition(Number(path.Attribute("w")), Number(path.Attribute("h")),
                ShapeGeometry.FillMode((string?)path.Attribute("fill")), Flag(path.Attribute("stroke"), true), commands));
        }
        return definition;
    }

    // ───────────────────────── Fills ─────────────────────────

    /// <summary>
    /// The fill among the children of <paramref name="owner"/> (spPr, bgPr, tcPr ...): null for noFill or none;
    /// <paramref name="specified"/> tells whether the owner has a fill element at all.
    /// </summary>
    /// <param name="placeholder">The colour of a theme style reference (phClr).</param>
    private SlideFill? Fill(SlideContext context, string part, XElement? owner, SKColor? placeholder, SlideFill? groupFill, out bool specified)
    {
        specified = true;
        foreach (var element in owner?.Elements() ?? [])
        {
            switch (element.Name.LocalName)
            {
                case "noFill":
                    return null;
                case "solidFill":
                    return context.Colors.Color(element, placeholder) is { } color ? new SolidFill(color) : null;
                case "gradFill":
                    return Gradient(context, element, placeholder);
                case "blipFill":
                    return PictureFill(context, part, element);
                case "pattFill":
                    return new PatternFill((string?)element.Attribute("prst") ?? "pct5",
                        context.Colors.Color(element.Element(A + "fgClr"), placeholder) ?? SKColors.Black,
                        context.Colors.Color(element.Element(A + "bgClr"), placeholder) ?? SKColors.White);
                case "grpFill":
                    return groupFill;
            }
        }
        specified = false;
        return null;
    }

    private static GradientFill? Gradient(SlideContext context, XElement gradient, SKColor? placeholder)
    {
        var stops = gradient.Element(A + "gsLst")?.Elements(A + "gs")
            .Select(gs => new GradientStop((float)Math.Clamp(Number(gs.Attribute("pos")) / 100000, 0, 1), context.Colors.Color(gs, placeholder) ?? SKColors.Black))
            .OrderBy(s => s.Position)
            .ToList() ?? [];
        if (stops.Count == 0)
            return null;
        if (stops.Count == 1)
            stops.Add(stops[0] with { Position = 1 });
        var rotate = Flag(gradient.Attribute("rotWithShape"), true);
        if (gradient.Element(A + "path") is { } path)
        {
            var focus = path.Element(A + "fillToRect");
            var rect = new SKRect((float)(Number(focus?.Attribute("l")) / 100000), (float)(Number(focus?.Attribute("t")) / 100000),
                (float)(Number(focus?.Attribute("r")) / 100000), (float)(Number(focus?.Attribute("b")) / 100000));
            var shape = (string?)path.Attribute("path") == "circle" ? GradientShape.Circle : GradientShape.Rectangle;
            return new GradientFill(stops, shape, 0, false, rect, rotate);
        }
        var linear = gradient.Element(A + "lin");
        return new GradientFill(stops, GradientShape.Linear, (float)(Number(linear?.Attribute("ang")) / 60000), Flag(linear?.Attribute("scaled")),
            SKRect.Empty, rotate);
    }

    /// <summary>A picture fill (a:blipFill / p:blipFill); null when the picture is missing or can't be drawn.</summary>
    private PictureFill? PictureFill(SlideContext context, string part, XElement blipFill)
    {
        var blip = blipFill.Element(A + "blip");
        var target = _package.Target(part, (string?)blip?.Attribute(R + "embed"));
        if (_package.Bytes(target) is not { } bytes || !SlideImages.CanDraw(bytes, _package.Limits))
            return null;
        static SKRect Insets(XElement? rect) => rect is null ? SKRect.Empty
            : new SKRect((float)(Number(rect.Attribute("l")) / 100000), (float)(Number(rect.Attribute("t")) / 100000),
                (float)(Number(rect.Attribute("r")) / 100000), (float)(Number(rect.Attribute("b")) / 100000));
        PictureTile? tile = null;
        if (blipFill.Element(A + "tile") is { } t)
            tile = new PictureTile(Points(Number(t.Attribute("tx"))), Points(Number(t.Attribute("ty"))), (float)(Number(t.Attribute("sx"), 100000) / 100000),
                (float)(Number(t.Attribute("sy"), 100000) / 100000), (string?)t.Attribute("flip") is "x" or "xy", (string?)t.Attribute("flip") is "y" or "xy",
                (string?)t.Attribute("algn") ?? "tl");
        return new PictureFill(new SlideImage(bytes), Insets(blipFill.Element(A + "srcRect")), Insets(blipFill.Element(A + "stretch")?.Element(A + "fillRect")),
            tile, Effects(context, blip!));
    }

    /// <summary>Recolouring of a picture (a:blip children).</summary>
    private static PictureEffects? Effects(SlideContext context, XElement blip)
    {
        var effects = PictureEffects.None;
        foreach (var effect in blip.Elements())
        {
            effects = effect.Name.LocalName switch
            {
                "alphaModFix" => effects with { Alpha = (float)Math.Clamp(Number(effect.Attribute("amt"), 100000) / 100000, 0, 1) },
                "grayscl" => effects with { Grayscale = true },
                "biLevel" => effects with { BiLevel = (float)Math.Clamp(Number(effect.Attribute("thresh"), 50000) / 100000, 0, 1) },
                "duotone" when effect.Elements().ToList() is [var dark, var light] =>
                    effects with { Duotone = (context.Colors.Color(new XElement("c", dark)) ?? SKColors.Black, context.Colors.Color(new XElement("c", light)) ?? SKColors.White) },
                "lum" => effects with
                {
                    Brightness = (float)Math.Clamp(Number(effect.Attribute("bright")) / 100000, -1, 1),
                    Contrast = (float)Math.Clamp(Number(effect.Attribute("contrast")) / 100000, -1, 1),
                },
                _ => effects,
            };
        }
        return effects.IsNone ? null : effects;
    }

    /// <summary>A theme fill style by reference (fillRef idx 1–3, bgRef idx 1001–1003) in the reference's colour.</summary>
    private SlideFill? ThemeFill(SlideContext context, string part, XElement reference)
    {
        var index = (int)Number(reference.Attribute("idx"));
        var color = context.Colors.Color(reference);
        var list = index >= 1001 ? "bgFillStyleLst" : "fillStyleLst";
        var position = index >= 1001 ? index - 1001 : index - 1;
        if (index == 0 || index == 1000)
            return null;
        var style = context.FormatScheme?.Element(A + list)?.Elements().ElementAtOrDefault(position);
        if (style is null)
            return color is { } c ? new SolidFill(c) : null;
        return Fill(context, part, new XElement("fill", style), color, null, out _);
    }

    // ───────────────────────── Lines ─────────────────────────

    /// <summary>
    /// A line from the a:ln elements of the shape and the placeholders it inherits from (the first that says wins, per
    /// property) over the theme line style it references; null when it has no fill.
    /// </summary>
    private SlideLine? Line(SlideContext context, string part, List<XElement> lines, XElement? reference)
    {
        var chain = new List<XElement>(lines);
        SKColor? placeholder = null;
        if (reference is not null && (int)Number(reference.Attribute("idx")) is >= 1 and var index
            && context.FormatScheme?.Element(A + "lnStyleLst")?.Elements(A + "ln").ElementAtOrDefault(index - 1) is { } themeLine)
        {
            chain.Add(themeLine);
            placeholder = context.Colors.Color(reference);
        }
        if (chain.Count == 0)
            return null;

        SlideFill? fill = null;
        var specified = false;
        foreach (var line in chain)
        {
            fill = Fill(context, part, line, placeholder, null, out specified);
            if (specified)
                break;
        }
        if (!specified && reference is not null && placeholder is null && context.Colors.Color(reference) is { } refColor && Number(reference.Attribute("idx")) > 0)
            fill = new SolidFill(refColor);
        if (fill is null)
            return null;

        string? Attribute(string name) => chain.Select(l => (string?)l.Attribute(name)).FirstOrDefault(v => v is not null);
        XElement? Child(string name) => chain.Select(l => l.Element(A + name)).FirstOrDefault(e => e is not null);
        var width = Points(double.TryParse(Attribute("w"), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && double.IsFinite(w) ? Math.Clamp(w, 0, 20116800) : 9525);

        float[]? dash = null;
        var dashElement = chain.Select(l => l.Element(A + "prstDash") ?? l.Element(A + "custDash")).FirstOrDefault(e => e is not null);
        if (dashElement?.Name.LocalName == "custDash")
            dash = [.. dashElement.Elements(A + "ds").SelectMany(ds => new[] { (float)(Number(ds.Attribute("d")) / 100000), (float)(Number(ds.Attribute("sp")) / 100000) }).Take(64)];
        else if ((string?)dashElement?.Attribute("val") is { } preset)
            dash = Dash(preset);
        if (dash is { Length: 0 } || dash?.Any(d => d < 0) == true || dash?.Sum() <= 0)
            dash = null;

        var join = chain.Select(l => l.Elements().FirstOrDefault(e => e.Name.LocalName is "round" or "bevel" or "miter")).FirstOrDefault(e => e is not null)?.Name.LocalName switch
        {
            "bevel" => SKStrokeJoin.Bevel,
            "miter" => SKStrokeJoin.Miter,
            _ => SKStrokeJoin.Round,
        };
        return new SlideLine(fill, width, dash,
            Attribute("cap") switch
            {
                "rnd" => SKStrokeCap.Round,
                "sq" => SKStrokeCap.Square,
                _ => SKStrokeCap.Butt,
            },
            join, End(Child("headEnd")), End(Child("tailEnd")),
            Attribute("cmpd") switch
            {
                "dbl" => LineCompound.Double,
                "thickThin" => LineCompound.ThickThin,
                "thinThick" => LineCompound.ThinThick,
                "tri" => LineCompound.Triple,
                _ => LineCompound.Single,
            });
    }

    /// <summary>DrawingML's preset dashes as multiples of the line width.</summary>
    private static float[]? Dash(string preset) => preset switch
    {
        "dot" => [1, 3],
        "dash" => [4, 3],
        "lgDash" => [8, 3],
        "dashDot" => [4, 3, 1, 3],
        "lgDashDot" => [8, 3, 1, 3],
        "lgDashDotDot" => [8, 3, 1, 3, 1, 3],
        "sysDot" => [1, 1],
        "sysDash" => [3, 1],
        "sysDashDot" => [3, 1, 1, 1],
        "sysDashDotDot" => [3, 1, 1, 1, 1, 1],
        _ => null,
    };

    private static LineEnd End(XElement? end)
    {
        var kind = (string?)end?.Attribute("type") switch
        {
            "triangle" => LineEndKind.Triangle,
            "stealth" => LineEndKind.Stealth,
            "diamond" => LineEndKind.Diamond,
            "oval" => LineEndKind.Oval,
            "arrow" => LineEndKind.Arrow,
            _ => LineEndKind.None,
        };
        static float Size(string? value) => value switch
        {
            "sm" => 2,
            "lg" => 5,
            _ => 3,
        };
        return kind == LineEndKind.None ? LineEnd.None : new LineEnd(kind, Size((string?)end!.Attribute("w")), Size((string?)end.Attribute("len")));
    }
}
