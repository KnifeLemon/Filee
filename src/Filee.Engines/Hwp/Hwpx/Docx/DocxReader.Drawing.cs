// DOCX graphic objects: DrawingML pictures, text boxes (wps), groups and drawing canvases, plus legacy VML
// (w:pict / w:object) used by older documents and as the fallback of mc:AlternateContent. Charts are read from their
// chart part (ChartReader) and become a picture of the chart for the writers that carries the chart for the renderer;
// those ChartReader doesn't draw are left out and counted. SmartArt is read from the drawing Word saves with it (the
// diagram's dsp:drawing part, through the slide reader) and becomes a picture of it for the writers that carries the
// drawing for the renderer; SmartArt without one is left out and counted.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Charts;
using Filee.Engines.Documents;
using ImageMagick;

namespace Filee.Engines.Hwp.Hwpx.Docx;

internal sealed partial class DocxReader
{
    private static readonly XNamespace Wpc = "http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas";
    private static readonly XNamespace W10 = "urn:schemas-microsoft-com:office:word";
    private static readonly XNamespace O = "urn:schemas-microsoft-com:office:office";
    private static readonly XNamespace W14 = "http://schemas.microsoft.com/office/word/2010/wordml";
    private static readonly XNamespace Chart = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private static readonly XNamespace Dgm = "http://schemas.openxmlformats.org/drawingml/2006/diagram";

    /// <summary>Default DrawingML text box insets: 0.1" left/right, 0.05" top/bottom.</summary>
    private static readonly HInsets DefaultTextBoxPadding = new(720, 720, 360, 360);

    private readonly Dictionary<string, string> _themeColors = [];

    /// <summary>The theme part's root (chart colours and fonts), or null.</summary>
    private XElement? _theme;

    private Slides.DrawingColors? _chartColors;

    /// <summary>The slide reader that reads the SmartArt drawings, over this package; made for the first diagram.</summary>
    private Slides.PptxSlideReader? _diagrams;
    private readonly ChartBudget _charts = new();

    private IEnumerable<HInline> Drawing(XElement drawing)
    {
        foreach (var container in drawing.Elements())
        {
            var anchored = container.Name == Wp + "anchor";
            if (!anchored && container.Name != Wp + "inline")
                continue;
            var extent = container.Element(Wp + "extent");
            var width = HwpxUnits.FromEmu(Long(extent, "cx") ?? 0);
            var height = HwpxUnits.FromEmu(Long(extent, "cy") ?? 0);
            var anchor = anchored ? Anchor(container) : null;
            var data = container.Element(A + "graphic")?.Element(A + "graphicData");
            // Stacking order of floating objects (members of a group share the group's).
            var zOrder = anchored && Long(container, "relativeHeight") is { } height32 ? (int?)Math.Min(height32, int.MaxValue) : null;
            var distance = anchored ? WrapDistance(container) : default;
            foreach (var inline in GraphicObjects(data?.Elements() ?? [], width, height, anchor, (0, 0, 1, 1)))
            {
                if (anchored && inline is HObject floating)
                    floating.OuterMargin = distance;
                switch (inline)
                {
                    case HImage image:
                        image.ZOrder = zOrder;
                        break;
                    case HShape shape:
                        shape.ZOrder = zOrder;
                        break;
                    case HTextBox box:
                        box.ZOrder = zOrder;
                        break;
                }
                yield return inline;
            }
        }
    }

    /// <summary>
    /// Pictures and text boxes of a graphic frame. Members of groups and canvases become separate objects placed at
    /// their position inside the group (<paramref name="groupTransform"/>: offset and scale of the child coordinates).
    /// </summary>
    private IEnumerable<HInline> GraphicObjects(IEnumerable<XElement> objects, int width, int height, HAnchor? anchor,
        (double X, double Y, double ScaleX, double ScaleY) groupTransform)
    {
        foreach (var obj in objects)
        {
            _budget.Object();
            if (obj.Name == Pic + "pic")
            {
                if (Picture(obj, width, height, anchor) is { } picture)
                    yield return picture;
            }
            else if (obj.Name == Wps + "wsp")
            {
                if (Shape(obj, width, height, anchor) is { } shape)
                    yield return shape;
            }
            else if (obj.Name == Wpg + "wgp" || obj.Name == Wpg + "grpSp" || obj.Name == Wpc + "wpc")
            {
                foreach (var inline in Group(obj, width, height, anchor, groupTransform))
                    yield return inline;
            }
            // Charts ChartReader doesn't draw and SmartArt without the drawing Word saves with it have no picture in the
            // document part to fall back on: the renderer says so.
            else if (obj.Name == Chart + "chart" || obj.Name == ChartReader.Cx + "chart")
            {
                if (ChartPicture(obj, width, height, anchor) is { } chart)
                    yield return chart;
                else
                    _leftOut.Add(HLeftOutKind.Chart);
            }
            else if (obj.Name == Dgm + "relIds")
            {
                if (SmartArtPicture(obj, width, height, anchor) is { } smartArt)
                    yield return smartArt;
                else
                    _leftOut.Add(HLeftOutKind.SmartArt);
            }
        }
    }

    private IEnumerable<HInline> Group(XElement group, int width, int height, HAnchor? anchor,
        (double X, double Y, double ScaleX, double ScaleY) parent)
    {
        using var level = _budget.Enter();
        var xfrm = group.Element(Wpg + "grpSpPr")?.Element(A + "xfrm") ?? group.Element(A + "grpSpPr")?.Element(A + "xfrm");
        var chOff = xfrm?.Element(A + "chOff");
        var chExt = xfrm?.Element(A + "chExt");
        // Child coordinates → HWPUNIT relative to the group's top-left corner.
        var scaleX = (Long(chExt, "cx") ?? 0) > 0 ? width / (double)Long(chExt, "cx")! : 1 / 127.0;
        var scaleY = (Long(chExt, "cy") ?? 0) > 0 ? height / (double)Long(chExt, "cy")! : 1 / 127.0;
        var originX = Long(chOff, "x") ?? 0;
        var originY = Long(chOff, "y") ?? 0;

        foreach (var child in group.Elements())
        {
            var childXfrm = child.Descendants(A + "xfrm").FirstOrDefault();
            var off = childXfrm?.Element(A + "off");
            var childExt = childXfrm?.Element(A + "ext");
            var x = (int)(((Long(off, "x") ?? 0) - originX) * scaleX);
            var y = (int)(((Long(off, "y") ?? 0) - originY) * scaleY);
            var childWidth = (int)((Long(childExt, "cx") ?? 0) * scaleX);
            var childHeight = (int)((Long(childExt, "cy") ?? 0) * scaleY);

            // An inline group becomes objects floating over the paragraph at the same place.
            var childAnchor = anchor is null
                ? new HAnchor("COLUMN", "LEFT", x, "PARA", "TOP", y, "IN_FRONT_OF_TEXT", true)
                : anchor with
                {
                    HorizontalOffset = anchor.HorizontalOffset + AlignedOffset(anchor.HorizontalAlign, x, childWidth, width),
                    VerticalOffset = anchor.VerticalOffset + AlignedOffset(anchor.VerticalAlign, y, childHeight, height),
                    AllowOverlap = true,
                };
            foreach (var inline in GraphicObjects([child], childWidth, childHeight, childAnchor, parent))
                yield return inline;
        }
    }

    /// <summary>
    /// Offset of a group member when the group is aligned (centered, right-aligned ...) rather than placed at an
    /// offset: the member keeps its distance from the group's center or far edge.
    /// </summary>
    private static int AlignedOffset(string align, int position, int size, int groupSize) => align switch
    {
        "CENTER" => position + size / 2 - groupSize / 2,
        "RIGHT" or "BOTTOM" => position + size - groupSize,
        _ => position,
    };

    /// <summary>A chart from its part, as a picture of it (for the writers) carrying the chart (for the renderer); null when it isn't drawn.</summary>
    private HImage? ChartPicture(XElement chart, int width, int height, HAnchor? anchor)
    {
        var id = Attr(chart, "id", R);
        if (id is null || !_part.Relationships.TryGetValue(id, out var rel) || rel.External || width <= 0 || height <= 0)
            return null;
        _chartColors ??= Slides.DrawingColors.FromTheme(_theme);
        if (ChartReader.Read(LoadPart(rel.Target)?.Xml.Root, _chartColors, ChartReader.ThemeFont(_theme)).Chart is not { } drawn)
            return null;
        _charts.Charge(drawn);
        // Points are HWPUNIT / 100.
        if (Charts.ChartPicture.Png(drawn, width / 100f, height / 100f, 150) is not { } png)
            return null;
        Directory.CreateDirectory(_mediaFolder);
        var path = Path.Combine(_mediaFolder, $"docx-chart-{_media.Count + 1}.png");
        File.WriteAllBytes(path, png);
        _media[$"chart|{path}"] = path;
        return new HImage(path) { Width = width, Height = height, Anchor = anchor, Chart = drawn };
    }

    /// <summary>
    /// SmartArt as the drawing Word saved with it (the data part's dsp:dataModelExt names it): a picture of it for the
    /// writers that carries the drawing for the renderer; null when there is none.
    /// </summary>
    private HImage? SmartArtPicture(XElement relIds, int width, int height, HAnchor? anchor)
    {
        if (relIds.Parent is not { } graphicData || width <= 0 || height <= 0)
            return null;
        Slides.SlideDeck? deck;
        try
        {
            _diagrams ??= Slides.PptxSlideReader.ForDiagrams(Slides.SlidePackage.Borrow(_package));
            // Points are HWPUNIT / 100.
            deck = _diagrams.Diagram(_part.Path, graphicData, width / 100f, height / 100f);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        if (deck is null)
            return null;
        // 150 dpi, at most 4000 pixels a side (as a chart's picture).
        var dpi = Math.Min(150f, 4000 * 72f / Math.Max(deck.Width, deck.Height));
        using var bitmap = Slides.SlideOutput.ToBitmaps(deck, dpi).FirstOrDefault();
        using var png = bitmap?.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        if (png is null)
            return null;
        Directory.CreateDirectory(_mediaFolder);
        var path = Path.Combine(_mediaFolder, $"docx-smartart-{_media.Count + 1}.png");
        File.WriteAllBytes(path, png.ToArray());
        _media[$"smartart|{path}"] = path;
        return new HImage(path) { Width = width, Height = height, Anchor = anchor, Drawing = deck };
    }

    private HImage? Picture(XElement pic, int width, int height, HAnchor? anchor)
    {
        var blip = pic.Descendants(A + "blip").FirstOrDefault();
        var path = ExtractImage(Attr(blip, "embed", R));
        // The writers get a copy with the cut edges gone; a picture that copy can't be made of (EMF) keeps the crop
        // in the model, for the renderer.
        var sourceRect = pic.Descendants(A + "srcRect").FirstOrDefault();
        var cropped = path is not null && sourceRect is not null ? CroppedImage(path, sourceRect) : path;
        return cropped is null ? null : new HImage(cropped)
        {
            Width = width > 0 ? width : null,
            Height = height > 0 ? height : null,
            Anchor = anchor,
            Crop = cropped == path ? Crop(sourceRect) : null,
            Transform = Transform(pic.Element(Pic + "spPr")?.Element(A + "xfrm")),
            Effects = BlipEffects(blip),
        };
    }

    /// <summary>
    /// Colour effects of a DrawingML picture: a:lum (Word's brightness and contrast; washout is +70 % / -70 %),
    /// a:grayscl, a:biLevel and a:alphaModFix; null when there are none.
    /// </summary>
    private static HPictureEffects? BlipEffects(XElement? blip)
    {
        if (blip is null)
            return null;
        static double Fraction(XElement? element, string name, double fallback) =>
            Long(element, name) is { } value ? Math.Clamp(value / 100000.0, -1, 1) : fallback;
        var lum = blip.Element(A + "lum");
        var contrast = Fraction(lum, "contrast", 0);
        var effects = new HPictureEffects(
            Contrast: contrast < 0 ? 1 + contrast : 1 / Math.Max(0.01, 1 - contrast),
            Brightness: Fraction(lum, "bright", 0) / 2,
            Grayscale: blip.Element(A + "grayscl") is not null,
            BlackAndWhite: blip.Element(A + "biLevel") is { } level ? Math.Clamp(Fraction(level, "thresh", 0.5), 0, 1) : null,
            Opacity: blip.Element(A + "alphaModFix") is { } alpha ? Math.Clamp(Fraction(alpha, "amt", 1), 0, 1) : 1);
        return effects.IsEmpty ? null : effects;
    }

    /// <summary>a:srcRect: the part of the picture cut away at each edge, in 1/1000 percent.</summary>
    private static HCrop? Crop(XElement? sourceRect)
    {
        double Edge(string name) => Math.Clamp((Long(sourceRect, name) ?? 0) / 100000.0, 0, 1);
        var crop = new HCrop(Edge("l"), Edge("t"), Edge("r"), Edge("b"));
        return sourceRect is null || crop.IsEmpty ? null : crop;
    }

    /// <summary>Rotation (a:xfrm rot, 1/60000 degree, clockwise) and mirroring; null when there is neither.</summary>
    private static HTransform? Transform(XElement? xfrm)
    {
        var transform = new HTransform((Long(xfrm, "rot") ?? 0) / 60000.0, Attr(xfrm, "flipH") is "1" or "true", Attr(xfrm, "flipV") is "1" or "true");
        return transform.IsIdentity ? null : transform;
    }

    /// <summary>A text box, or a rectangle / ellipse / line without text. Other geometries are skipped.</summary>
    private HInline? Shape(XElement wsp, int width, int height, HAnchor? anchor)
    {
        var spPr = wsp.Element(Wps + "spPr");
        var style = wsp.Element(Wps + "style");
        var (fill, gradient) = FillOf(spPr, style);
        var geometry = Attr(spPr?.Element(A + "prstGeom"), "prst");
        if (wsp.Element(Wps + "txbx")?.Element(W + "txbxContent") is { } content)
        {
            if (geometry is "rect" or "roundRect" or "flowChartProcess" or "flowChartAlternateProcess" or "ellipse" or "flowChartConnector")
            {
                var framed = TextBox(wsp, content, width, height, anchor, fill ?? gradient?.Colors[0]);
                if (geometry is "ellipse" or "flowChartConnector")
                    framed.Shape = HShapeKind.Ellipse;
                else if (geometry is "roundRect" or "flowChartAlternateProcess")
                    framed.CornerRatio = 20;
                return framed;
            }
            // Text on a free-form or decorative shape: keep the text, drop the outline 한글 cannot draw.
            if (string.IsNullOrWhiteSpace(content.Value))
                return null;
            var box = TextBox(wsp, content, width, height, anchor, fill: null);
            box.Line = HBorder.None;
            return box;
        }

        HShapeKind? kind = geometry switch
        {
            "rect" or "roundRect" or "snip1Rect" or "snip2SameRect" or "round1Rect" or "round2SameRect"
                or "flowChartProcess" or "flowChartAlternateProcess" or "frame" => HShapeKind.Rectangle,
            "ellipse" or "flowChartConnector" => HShapeKind.Ellipse,
            "line" or "straightConnector1" => HShapeKind.Line,
            _ => null, // free-form and other preset shapes have no 한글 equivalent
        };
        if (kind is null)
            return null;
        var xfrm = spPr?.Element(A + "xfrm");
        var line = kind == HShapeKind.Line;
        var ln = spPr?.Element(A + "ln");
        var transform = Transform(xfrm);
        // A line's flips say which corners it joins (FlipHorizontal / FlipVertical); mirroring it again would undo them.
        if (line && transform is not null)
            transform = transform.Rotation == 0 ? null : new HTransform(transform.Rotation);
        return new HShape(kind.Value)
        {
            Width = width,
            Height = height,
            Anchor = anchor,
            Fill = line ? null : fill,
            Gradient = line ? null : gradient,
            Line = Line(ln, style),
            CornerRatio = geometry is "roundRect" or "round1Rect" or "round2SameRect" or "flowChartAlternateProcess" ? 20 : 0,
            FlipHorizontal = Attr(xfrm, "flipH") is "1" or "true",
            FlipVertical = Attr(xfrm, "flipV") is "1" or "true",
            Transform = transform,
            Head = line ? LineEnd(ln?.Element(A + "headEnd")) : null,
            Tail = line ? LineEnd(ln?.Element(A + "tailEnd")) : null,
        };
    }

    /// <summary>
    /// An arrowhead from a:headEnd / a:tailEnd: type (triangle, stealth, diamond, oval, arrow; none), w and len (sm,
    /// med, lg; med when left out). Null for none.
    /// </summary>
    private static HArrow? LineEnd(XElement? end)
    {
        HArrowKind? kind = Attr(end, "type") switch
        {
            "triangle" => HArrowKind.Triangle,
            "stealth" => HArrowKind.Stealth,
            "diamond" => HArrowKind.Diamond,
            "oval" => HArrowKind.Oval,
            "arrow" => HArrowKind.Open,
            _ => null,
        };
        static HArrowSize Size(string? value) => value switch
        {
            "sm" => HArrowSize.Small,
            "lg" => HArrowSize.Large,
            _ => HArrowSize.Medium,
        };
        return kind is { } k ? new HArrow(k, Size(Attr(end, "w")), Size(Attr(end, "len"))) : null;
    }

    /// <summary>
    /// An arrowhead from v:stroke startarrow / endarrow (block, classic, diamond, oval, open; none) with its
    /// …arrowwidth (narrow, medium, wide) and …arrowlength (short, medium, long). Null for none.
    /// </summary>
    private static HArrow? VmlArrow(XElement? stroke, string end)
    {
        HArrowKind? kind = (string?)stroke?.Attribute(end + "arrow") switch
        {
            "block" => HArrowKind.Triangle,
            "classic" => HArrowKind.Stealth,
            "diamond" => HArrowKind.Diamond,
            "oval" => HArrowKind.Oval,
            "open" => HArrowKind.Open,
            _ => null,
        };
        var width = (string?)stroke?.Attribute(end + "arrowwidth") switch
        {
            "narrow" => HArrowSize.Small,
            "wide" => HArrowSize.Large,
            _ => HArrowSize.Medium,
        };
        var length = (string?)stroke?.Attribute(end + "arrowlength") switch
        {
            "short" => HArrowSize.Small,
            "long" => HArrowSize.Large,
            _ => HArrowSize.Medium,
        };
        return kind is { } k ? new HArrow(k, width, length) : null;
    }

    private HTextBox TextBox(XElement wsp, XElement content, int width, int height, HAnchor? anchor, string? fill)
    {
        var spPr = wsp.Element(Wps + "spPr");
        var bodyPr = wsp.Element(Wps + "bodyPr");
        var box = new TextBoxBuilder(width, height, anchor)
        {
            Fill = fill,
            Line = Line(spPr?.Element(A + "ln"), wsp.Element(Wps + "style")),
            Padding = new HInsets(
                Inset(bodyPr, "lIns", DefaultTextBoxPadding.Left), Inset(bodyPr, "rIns", DefaultTextBoxPadding.Right),
                Inset(bodyPr, "tIns", DefaultTextBoxPadding.Top), Inset(bodyPr, "bIns", DefaultTextBoxPadding.Bottom)),
            VerticalAlign = Attr(bodyPr, "anchor") switch
            {
                "ctr" => HVerticalAlign.Center,
                "b" => HVerticalAlign.Bottom,
                _ => HVerticalAlign.Top,
            },
        }.Build();
        box.Transform = Transform(spPr?.Element(A + "xfrm"));
        // WordArt: a text warp other than "no shape" stretches the text over the box.
        var warp = Attr(bodyPr?.Element(A + "prstTxWarp"), "prst");
        if (warp is not null || Attr(bodyPr, "fromWordArt") is "1" or "true")
            box.TextArt = new HTextArt(warp is not (null or "textNoShape"), TextOpacity(content));
        // Text written top to bottom (turned, or East Asian vertical) is turned into the box.
        box.TextFlow = DrawingTextFlow(Attr(bodyPr, "vert"));
        ReadTextBoxContent(content, box.Blocks);
        return box;
    }

    /// <summary>
    /// How opaque WordArt text is: its first run's text fill (w14:textFill) with w14:alpha, which Word stores as the
    /// transparency (50000 = half see-through); 1 when it has none.
    /// </summary>
    private static double TextOpacity(XElement content)
    {
        var color = content.Descendants(W14 + "textFill").FirstOrDefault()?.Element(W14 + "solidFill")?.Elements().FirstOrDefault();
        return long.TryParse(Attr(color?.Element(W14 + "alpha"), "val", W14), NumberStyles.Integer, CultureInfo.InvariantCulture, out var alpha)
            ? 1 - Math.Clamp(alpha / 100000.0, 0, 1)
            : 1;
    }

    /// <summary>
    /// Text box paragraphs; Word's document grid does not apply inside text boxes, nor does the style of a table the
    /// text box sits in.
    /// </summary>
    private void ReadTextBoxContent(XElement content, List<HBlock> output)
    {
        using var level = _budget.Enter();
        _textBoxDepth++;
        var tableStyleLayer = _tableStyleLayer;
        _tableStyleLayer = null;
        try
        {
            ReadNestedBlocks(content, output);
        }
        finally
        {
            _textBoxDepth--;
            _tableStyleLayer = tableStyleLayer;
        }
    }

    /// <summary>Collects text box settings before the HTextBox (whose Blocks list is read-only) is created.</summary>
    private sealed class TextBoxBuilder(int width, int height, HAnchor? anchor)
    {
        public string? Fill { get; init; }
        public HBorder Line { get; init; } = HBorder.Thin;
        public HInsets Padding { get; init; } = DefaultTextBoxPadding;
        public HVerticalAlign VerticalAlign { get; init; }

        public HTextBox Build() => new()
        {
            Width = Math.Max(width, 1000),
            Height = Math.Max(height, 600),
            Anchor = anchor,
            Fill = Fill,
            Line = Line,
            Padding = Padding,
            VerticalAlign = VerticalAlign,
        };
    }

    private static int Inset(XElement? bodyPr, string name, int fallback) =>
        Long(bodyPr, name) is { } emu ? HwpxUnits.FromEmu(emu) : fallback;

    /// <summary>Solid colour or linear gradient of a shape; without its own fill the shape style decides.</summary>
    private (string? Solid, HGradient? Gradient) FillOf(XElement? spPr, XElement? style)
    {
        if (spPr?.Element(A + "noFill") is not null)
            return (null, null);
        if (spPr?.Element(A + "solidFill") is { } solid)
            return (Color(solid), null);
        if (spPr?.Element(A + "gradFill") is { } gradient)
        {
            var colors = gradient.Element(A + "gsLst")?.Elements(A + "gs")
                .OrderBy(gs => Long(gs, "pos") ?? 0)
                .Select(Color)
                .OfType<string>()
                .ToList() ?? [];
            var angle = (int)((Long(gradient.Element(A + "lin"), "ang") ?? 0) / 60000);
            return colors.Count >= 2 ? (null, new HGradient(colors, angle)) : (colors.FirstOrDefault(), null);
        }
        var fillRef = style?.Element(A + "fillRef");
        return fillRef is not null && Attr(fillRef, "idx") is not (null or "0") ? (Color(fillRef), null) : (null, null);
    }

    private HBorder Line(XElement? ln, XElement? style)
    {
        if (ln is null)
        {
            // No explicit outline: the shape style decides (lnRef idx 0 = no line).
            var lnRef = style?.Element(A + "lnRef");
            return lnRef is not null && Attr(lnRef, "idx") is not (null or "0")
                ? HBorder.Thin with { Color = Color(lnRef) ?? "#000000" }
                : HBorder.None;
        }
        if (ln.Element(A + "noFill") is not null)
            return HBorder.None;
        var widthMm = Long(ln, "w") is { } emu ? emu / 36000.0 : 0.12;
        var dash = Attr(ln.Element(A + "prstDash"), "val");
        var borderStyle = dash switch
        {
            null or "solid" => HBorderStyle.Solid,
            "dot" or "sysDot" => HBorderStyle.Dot,
            "dashDot" or "sysDashDot" or "lgDashDot" => HBorderStyle.DashDot,
            _ => HBorderStyle.Dash,
        };
        return new HBorder(borderStyle, widthMm, Color(ln.Element(A + "solidFill")) ?? "#000000");
    }

    /// <summary>Colour of a fill element: srgbClr, sysClr or a theme colour (luminance modifiers are ignored).</summary>
    private string? Color(XElement? fill)
    {
        if (fill is null)
            return null;
        if (fill.Element(A + "srgbClr") is { } rgb)
            return "#" + (Val(rgb) ?? "000000").ToUpperInvariant();
        if (fill.Element(A + "sysClr") is { } sys)
            return "#" + (Attr(sys, "lastClr") ?? "000000").ToUpperInvariant();
        if (fill.Element(A + "schemeClr") is { } scheme)
        {
            var name = Val(scheme) switch
            {
                "bg1" => "lt1",
                "tx1" => "dk1",
                "bg2" => "lt2",
                "tx2" => "dk2",
                var other => other ?? "dk1",
            };
            return _themeColors.GetValueOrDefault(name, name.StartsWith("lt", StringComparison.Ordinal) ? "#FFFFFF" : "#000000");
        }
        return null;
    }

    private void LoadThemeColors(Part? theme)
    {
        _theme = theme?.Xml.Root;
        foreach (var color in theme?.Xml.Descendants(A + "clrScheme").FirstOrDefault()?.Elements() ?? [])
        {
            var value = Val(color.Element(A + "srgbClr")) ?? Attr(color.Element(A + "sysClr"), "lastClr");
            if (value is { Length: 6 })
                _themeColors[color.Name.LocalName] = "#" + value.ToUpperInvariant();
        }
    }

    /// <summary>Position of a floating DrawingML object (wp:anchor).</summary>
    private static HAnchor Anchor(XElement anchor)
    {
        var horizontal = anchor.Element(Wp + "positionH");
        var vertical = anchor.Element(Wp + "positionV");
        var wrap = anchor.Elements().FirstOrDefault(e => e.Name.Namespace == Wp && e.Name.LocalName.StartsWith("wrap", StringComparison.Ordinal))?.Name.LocalName;
        var behind = Attr(anchor, "behindDoc") is "1" or "true";
        return new HAnchor(
            HorizontalRelativeTo: Attr(horizontal, "relativeFrom") switch
            {
                "page" or "leftMargin" or "rightMargin" or "insideMargin" or "outsideMargin" => "PAPER",
                "margin" => "PAGE",
                "character" => "PARA",
                _ => "COLUMN",
            },
            HorizontalAlign: horizontal?.Element(Wp + "align")?.Value switch
            {
                "center" => "CENTER",
                "right" => "RIGHT",
                "inside" => "INSIDE",
                "outside" => "OUTSIDE",
                _ => "LEFT",
            },
            HorizontalOffset: OffsetOf(horizontal),
            VerticalRelativeTo: Attr(vertical, "relativeFrom") switch
            {
                "page" or "topMargin" or "bottomMargin" or "insideMargin" or "outsideMargin" => "PAPER",
                "margin" => "PAGE",
                _ => "PARA",
            },
            VerticalAlign: vertical?.Element(Wp + "align")?.Value switch
            {
                "center" => "CENTER",
                "bottom" => "BOTTOM",
                "inside" => "INSIDE",
                "outside" => "OUTSIDE",
                _ => "TOP",
            },
            VerticalOffset: OffsetOf(vertical),
            Wrap: wrap switch
            {
                "wrapSquare" or "wrapTight" or "wrapThrough" => "SQUARE",
                "wrapTopAndBottom" => "TOP_AND_BOTTOM",
                _ => behind ? "BEHIND_TEXT" : "IN_FRONT_OF_TEXT",
            },
            AllowOverlap: Attr(anchor, "allowOverlap") is not ("0" or "false"))
        {
            TextFlow = WrapSide(Attr(anchor.Element(Wp + (wrap ?? "wrapNone")), "wrapText")),
            WrapPolygon = wrap is "wrapTight" or "wrapThrough" ? WrapPolygon(anchor.Element(Wp + wrap)?.Element(Wp + "wrapPolygon")) : null,
        };
    }

    /// <summary>Word's wrapText (DrawingML) or VML w10:wrap side as the sides text goes on (HAnchor.TextFlow).</summary>
    private static string WrapSide(string? side) => side switch
    {
        "left" => "LEFT_ONLY",
        "right" => "RIGHT_ONLY",
        "largest" => "LARGEST_ONLY",
        _ => "BOTH_SIDES",
    };

    /// <summary>The most points of a wrap polygon read; one with more wraps at the object's box.</summary>
    private const int MaxWrapPolygonPoints = 256;

    /// <summary>
    /// A tight or through wrap's polygon (wp:wrapPolygon) as fractions of the object's size: Word writes its points in
    /// a 21600 × 21600 square over the object. Null when it is missing, has fewer than three points or too many.
    /// </summary>
    private static List<(double X, double Y)>? WrapPolygon(XElement? polygon)
    {
        if (polygon is null)
            return null;
        var points = new List<(double X, double Y)>();
        foreach (var point in polygon.Elements())
        {
            if (point.Name != Wp + "start" && point.Name != Wp + "lineTo")
                continue;
            if (points.Count == MaxWrapPolygonPoints)
                return null;
            points.Add((Math.Clamp(Long(point, "x") ?? 0, -21600L * 4, 21600L * 5) / 21600.0, Math.Clamp(Long(point, "y") ?? 0, -21600L * 4, 21600L * 5) / 21600.0));
        }
        return points.Count >= 3 ? points : null;
    }

    /// <summary>
    /// The distance a floating DrawingML object keeps from the text around it: its wrap element's distT / distB / distL /
    /// distR, else the anchor's (EMU), never negative.
    /// </summary>
    private static HInsets WrapDistance(XElement anchor)
    {
        var wrap = anchor.Elements().FirstOrDefault(e => e.Name.Namespace == Wp && e.Name.LocalName.StartsWith("wrap", StringComparison.Ordinal));
        int Distance(string name) => HwpxUnits.FromEmu(Math.Clamp(Long(wrap, name) ?? Long(anchor, name) ?? 0, 0, MaxWrapDistanceEmu));
        return new HInsets(Distance("distL"), Distance("distR"), Distance("distT"), Distance("distB"));
    }

    /// <summary>The largest distance from text read (about 56 cm, a sheet of A2): more is a broken or hostile value.</summary>
    private const long MaxWrapDistanceEmu = 20_000_000;

    private static int OffsetOf(XElement? position) =>
        long.TryParse(position?.Element(Wp + "posOffset")?.Value, out var emu) ? HwpxUnits.FromEmu(emu) : 0;

    private string? ExtractImage(string? relationshipId)
    {
        if (relationshipId is null || !_part.Relationships.TryGetValue(relationshipId, out var rel) || rel.External)
            return null;
        if (_media.TryGetValue(rel.Target, out var existing))
            return existing;
        if (_package.GetEntry(rel.Target) is not { } entry)
            return null;
        Directory.CreateDirectory(_mediaFolder);
        string? path = Path.Combine(_mediaFolder, $"docx-media-{_media.Count + 1}{DocumentPackage.SafeExtension(rel.Target)}");
        if (!_package.ExtractPicture(entry, path, _unpack))
            path = null; // larger than any picture should be: left out
        _media[rel.Target] = path;
        return path;
    }

    /// <summary>
    /// A copy of a picture with a:srcRect's edges cut off (each in 1/1000 percent of the picture), or the picture itself
    /// when nothing is cut or it can't be read (EMF). Word keeps the whole picture in the file and shows the rest; a
    /// negative edge adds space around it, which is not done here.
    /// </summary>
    private string CroppedImage(string path, XElement crop)
    {
        static double Edge(XElement rect, string name) =>
            int.TryParse((string?)rect.Attribute(name), out var value) ? Math.Clamp(value, 0, 100_000) / 100_000.0 : 0;
        var (left, top, right, bottom) = (Edge(crop, "l"), Edge(crop, "t"), Edge(crop, "r"), Edge(crop, "b"));
        if (left + top + right + bottom == 0 || left + right >= 1 || top + bottom >= 1)
            return path;
        var key = $"{path}|{left}|{top}|{right}|{bottom}";
        if (_media.TryGetValue(key, out var existing) && existing is not null)
            return existing;
        try
        {
            using var image = new MagickImage(path);
            var (width, height) = ((double)image.Width, (double)image.Height);
            image.Crop(new MagickGeometry(
                (int)Math.Round(width * left), (int)Math.Round(height * top),
                (uint)Math.Max(1, Math.Round(width * (1 - left - right))), (uint)Math.Max(1, Math.Round(height * (1 - top - bottom)))));
            image.ResetPage();
            var cropped = Path.Combine(_mediaFolder, $"docx-media-{_media.Count + 1}{Path.GetExtension(path)}");
            image.Write(cropped);
            _media[key] = cropped;
            return cropped;
        }
        catch (MagickException)
        {
            return path;
        }
    }

    // ───────────────────────── VML (legacy) ─────────────────────────

    /// <param name="format">Formatting of the run holding the objects (WordArt text starts from it).</param>
    private IEnumerable<HInline> Vml(XElement container, HCharFormat format)
    {
        foreach (var shape in container.Elements())
        {
            if (shape.Name.Namespace != V)
                continue;
            if (shape.Name.LocalName == "group")
            {
                using var level = _budget.Enter();
                foreach (var inline in Vml(shape, format))
                    yield return inline;
                continue;
            }
            _budget.Object();
            if (shape.Name.LocalName is "shapetype")
                continue;

            var style = VmlStyle((string?)shape.Attribute("style"));
            // Word doesn't draw a hidden shape (a stamp's text kept for later, a template's spare WordArt).
            if (style.GetValueOrDefault("visibility") == "hidden")
                continue;
            var width = HwpxUnits.ParseLength(style.GetValueOrDefault("width")) ?? 0;
            var height = HwpxUnits.ParseLength(style.GetValueOrDefault("height")) ?? 0;
            var anchor = style.GetValueOrDefault("position") == "absolute" ? VmlAnchor(shape, style) : null;
            var transform = VmlTransform(style);
            int? zOrder = anchor is not null && int.TryParse(style.GetValueOrDefault("z-index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var z) ? z : null;
            var distance = anchor is not null ? VmlWrapDistance(style) : default;

            if (shape.Element(V + "imagedata") is { } imageData && ExtractImage(Attr(imageData, "id", R)) is { } path)
            {
                yield return new HImage(path)
                {
                    Width = width > 0 ? width : null,
                    Height = height > 0 ? height : null,
                    Anchor = anchor,
                    Transform = transform,
                    ZOrder = zOrder,
                    Effects = VmlImageEffects(imageData),
                    OuterMargin = distance,
                };
            }
            else if (shape.Element(V + "textbox") is { } textbox && textbox.Element(W + "txbxContent") is { } content)
            {
                // A VML text box written top to bottom (layout-flow: vertical or vertical-ideographic; bottom to top with
                // mso-layout-flow-alt: bottom-to-top) is turned into the box.
                var textStyle = VmlStyle((string?)textbox.Attribute("style"));
                var vertical = textStyle.GetValueOrDefault("layout-flow") is { } flow && flow.StartsWith("vertical", StringComparison.Ordinal);
                var box = new TextBoxBuilder(width, height, anchor)
                {
                    Fill = VmlFill(shape),
                    Line = VmlLine(shape),
                }.Build();
                (box.Transform, box.ZOrder, box.OuterMargin) = (transform, zOrder, distance);
                if (vertical)
                    box.TextFlow = textStyle.GetValueOrDefault("mso-layout-flow-alt") == "bottom-to-top" ? HTextFlow.Up : HTextFlow.Vertical;
                ReadTextBoxContent(content, box.Blocks);
                yield return box;
            }
            else if (WordArt(shape, width, height, format) is ({ } text, var fit))
            {
                if (anchor is null)
                {
                    yield return text;
                }
                else
                {
                    // A floating WordArt (Word's watermarks): its text drawn turned with the shape, as see-through as its
                    // fill, stretched over the box when the text path fits the shape.
                    var box = new TextBoxBuilder(width, height, anchor) { Line = HBorder.None, Padding = new HInsets(0, 0, 0, 0), VerticalAlign = HVerticalAlign.Center }.Build();
                    (box.Transform, box.ZOrder, box.OuterMargin) = (transform, zOrder, distance);
                    var fill = shape.Element(V + "fill");
                    box.TextArt = new HTextArt(fit, Math.Clamp(VmlFraction((string?)fill?.Attribute("opacity") ?? (string?)shape.Attribute(O + "opacity")) ?? 1, 0, 1));
                    // No paragraph spacing: the box holds the text's lines alone.
                    var paragraph = new HParagraph
                    {
                        Format = new HParaFormat(Align: HAlign.Center, Left: 0, Right: 0, FirstLine: 0, Before: 0, After: 0,
                            Layout: new HParaLayout(LineHeight: new HLineHeight(HLineHeightRule.Multiple, 240), SnapToGrid: false)),
                    };
                    paragraph.Inlines.Add(text);
                    box.Blocks.Add(paragraph);
                    yield return box;
                }
            }
            else if (shape.Name.LocalName is "rect" or "roundrect" or "oval" or "line" || VmlStraightLine(shape))
            {
                var kind = shape.Name.LocalName switch
                {
                    "oval" => HShapeKind.Ellipse,
                    "rect" or "roundrect" => HShapeKind.Rectangle,
                    _ => HShapeKind.Line,
                };
                var (flipHorizontal, flipVertical) = (false, false);
                if (shape.Name.LocalName == "line")
                {
                    // v:line has from="x,y" to="x,y" instead of a size; it runs from the one to the other.
                    var from = VmlPoint((string?)shape.Attribute("from"));
                    var to = VmlPoint((string?)shape.Attribute("to"));
                    width = Math.Abs(to.X - from.X);
                    height = Math.Abs(to.Y - from.Y);
                    (flipHorizontal, flipVertical) = (to.X < from.X, to.Y < from.Y);
                    var lineAnchor = anchor ?? VmlAnchor(shape, style);
                    distance = VmlWrapDistance(style);
                    anchor = lineAnchor with
                    {
                        HorizontalOffset = lineAnchor.HorizontalOffset + Math.Min(from.X, to.X),
                        VerticalOffset = lineAnchor.VerticalOffset + Math.Min(from.Y, to.Y),
                    };
                }
                else if (kind == HShapeKind.Line)
                {
                    // A straight connector (type 32) or line (20) runs corner to corner of its box; "flip" picks the corners.
                    (flipHorizontal, flipVertical) = (transform?.FlipHorizontal == true, transform?.FlipVertical == true);
                    transform = transform is { Rotation: not 0 } turned ? new HTransform(turned.Rotation) : null;
                }
                var stroke = shape.Element(V + "stroke");
                yield return new HShape(kind)
                {
                    Width = width,
                    Height = height,
                    Anchor = anchor,
                    Fill = kind == HShapeKind.Line ? null : VmlFill(shape),
                    Line = VmlLine(shape),
                    CornerRatio = shape.Name.LocalName == "roundrect" ? 20 : 0,
                    Transform = transform,
                    ZOrder = zOrder,
                    OuterMargin = distance,
                    FlipHorizontal = flipHorizontal,
                    FlipVertical = flipVertical,
                    Head = kind == HShapeKind.Line ? VmlArrow(stroke, "start") : null,
                    Tail = kind == HShapeKind.Line ? VmlArrow(stroke, "end") : null,
                };
            }
        }
    }

    /// <summary>
    /// Legacy WordArt (a VML shape with v:textpath, as WPS and older Word write it) as plain text in its font and
    /// fill colour. 한글 has no text effects, so the text keeps the run's other formatting; when the text is stretched
    /// to the shape (fitpath / fitshape), the font size is chosen so it fills about the same box. The text path's
    /// settings the shape leaves out come from its v:shapetype (Word's watermarks keep on and fitshape there).
    /// </summary>
    /// <returns>The text, and whether it is stretched over the shape; null when the shape has no text path.</returns>
    private (HText Text, bool Fit)? WordArt(XElement shape, int width, int height, HCharFormat format)
    {
        var path = shape.Element(V + "textpath");
        var typePath = ShapeType(shape)?.Element(V + "textpath");
        string? Setting(string name) => (string?)path?.Attribute(name) ?? (string?)typePath?.Attribute(name);
        var text = (string?)path?.Attribute("string");
        if (path is null || Setting("on") is "f" or "false" || string.IsNullOrWhiteSpace(text))
            return null;

        var font = VmlStyle((string?)path.Attribute("style"));
        var family = font.GetValueOrDefault("font-family")?.Trim('"', '\'', ' ');
        var size = HwpxUnits.ParseLength(font.GetValueOrDefault("font-size")) is { } points and > 0 ? points : format.Size ?? 1000;
        var stretched = Setting("fitpath") is "t" or "true" || Setting("fitshape") is "t" or "true";
        if (stretched && width > 0 && height > 0)
        {
            // The glyphs fill the shape: capitals and digits are about 0.72 em tall, Han characters about 0.9 em;
            // East Asian characters are 1 em wide, others about half.
            var wide = text.Count(c => c is >= '⺀' and <= '鿿' or >= '가' and <= '힣' or >= '＀' and <= '｠');
            var ems = wide + (text.Length - wide) * 0.5;
            var byHeight = height / (wide > 0 ? 0.9 : 0.72);
            size = (int)Math.Min(byHeight, width / Math.Max(ems, 1));
        }
        var color = (string?)shape.Attribute("filled") is "f" or "false"
            ? VmlColor((string?)shape.Attribute("strokecolor"))
            : VmlColor((string?)shape.Attribute("fillcolor"));
        return (new HText(text.Replace('\n', ' ').Replace('\r', ' '), format with
        {
            Size = Math.Max(size, 100),
            Font = family ?? format.Font,
            EastAsianFont = family ?? format.EastAsianFont,
            Bold = font.GetValueOrDefault("font-weight") is { } weight ? weight == "bold" || int.TryParse(weight, out var w) && w >= 600 : format.Bold,
            Italic = font.GetValueOrDefault("font-style") is { } italic ? italic == "italic" : format.Italic,
            Color = color ?? format.Color,
            Layout = family is null ? format.Layout : (format.Layout ?? new HCharLayout()) with { WordFonts = new HWordFonts(family, family, family, family) },
        }), stretched);
    }

    /// <summary>
    /// A v:shape (without text or picture) that is a straight line: its type (o:spt of its v:shapetype, or the
    /// "#_x0000_tN" Word names its built-in types by) is 20 (line) or 32 (straight connector, as Word writes arrows).
    /// </summary>
    private bool VmlStraightLine(XElement shape)
    {
        if (shape.Name.LocalName != "shape")
            return false;
        var spt = (string?)shape.Attribute(O + "spt") ?? (string?)ShapeType(shape)?.Attribute(O + "spt");
        if (spt is null && (string?)shape.Attribute("type") is { } type && type.StartsWith("#_x0000_t", StringComparison.Ordinal))
            spt = type["#_x0000_t".Length..];
        return spt is "20" or "32";
    }

    /// <summary>The v:shapetype a VML shape's type ("#id") names, from anywhere in its part; null when there is none.</summary>
    private XElement? ShapeType(XElement shape)
    {
        if ((string?)shape.Attribute("type") is not ['#', _, ..] type || shape.Document is not { } document)
            return null;
        if (!_shapeTypes.TryGetValue(document, out var types))
        {
            types = [];
            foreach (var shapeType in document.Descendants(V + "shapetype"))
            {
                if ((string?)shapeType.Attribute("id") is { } id)
                    types.TryAdd(id, shapeType);
            }
            _shapeTypes[document] = types;
        }
        return types.GetValueOrDefault(type[1..]);
    }

    /// <summary>The shape types of each part read so far, by id.</summary>
    private readonly Dictionary<XDocument, Dictionary<string, XElement>> _shapeTypes = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Word's picture adjustments on v:imagedata: gain (contrast) and blacklevel (brightness; washout is gain 0.3,
    /// blacklevel 0.35), grayscale and bilevel; null when there are none.
    /// </summary>
    private static HPictureEffects? VmlImageEffects(XElement imageData)
    {
        static bool On(XElement element, string name) => (string?)element.Attribute(name) is "t" or "true";
        var effects = new HPictureEffects(
            Contrast: Math.Clamp(VmlFraction((string?)imageData.Attribute("gain")) ?? 1, 0, 100),
            Brightness: Math.Clamp(VmlFraction((string?)imageData.Attribute("blacklevel")) ?? 0, -1, 1),
            Grayscale: On(imageData, "grayscale"),
            BlackAndWhite: On(imageData, "bilevel") ? 0.5 : null);
        return effects.IsEmpty ? null : effects;
    }

    /// <summary>A VML fraction: "0.5", ".5", "50%" or 16.16 fixed point ("32768f"); null when it is none of these.</summary>
    private static double? VmlFraction(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        var divisor = 1.0;
        if (text.EndsWith('f'))
            (text, divisor) = (text[..^1], 65536);
        else if (text.EndsWith('%'))
            (text, divisor) = (text[..^1], 100);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number / divisor : null;
    }

    /// <summary>VML rotation (style "rotation", degrees clockwise) and mirroring ("flip: x y"); null when there is neither.</summary>
    private static HTransform? VmlTransform(Dictionary<string, string> style)
    {
        var rotation = style.GetValueOrDefault("rotation")?.TrimEnd('f', 'd') is { } value &&
                       double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees) ? degrees : 0;
        // "fd" values are in 1/65536 degree.
        if (style.GetValueOrDefault("rotation")?.EndsWith("fd", StringComparison.Ordinal) == true)
            rotation /= 65536;
        var flip = style.GetValueOrDefault("flip")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var transform = new HTransform(rotation, flip.Contains("x"), flip.Contains("y"));
        return transform.IsIdentity ? null : transform;
    }

    private static string? VmlFill(XElement shape) =>
        (string?)shape.Attribute("filled") is "f" or "false" ? null : VmlColor((string?)shape.Attribute("fillcolor")) ?? "#FFFFFF";

    private static HBorder VmlLine(XElement shape) =>
        (string?)shape.Attribute("stroked") is "f" or "false"
            ? HBorder.None
            : HBorder.Thin with
            {
                Color = VmlColor((string?)shape.Attribute("strokecolor")) ?? "#000000",
                WidthMm = HwpxUnits.ParseLength((string?)shape.Attribute("strokeweight")) is { } weight ? weight / HwpxUnits.PerMm : 0.12,
            };

    /// <summary>"x,y" of a VML line end; bare numbers are points.</summary>
    private static (int X, int Y) VmlPoint(string? point)
    {
        var parts = (point ?? "0,0").Split(',');
        int Coordinate(int index)
        {
            var text = parts.ElementAtOrDefault(index)?.Trim() ?? "0";
            if (text.Length == 0)
                text = "0";
            return HwpxUnits.ParseLength(char.IsDigit(text[^1]) ? text + "pt" : text) ?? 0;
        }
        return (Coordinate(0), Coordinate(1));
    }

    private static Dictionary<string, string> VmlStyle(string? style) =>
        (style ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split(':', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Last()[1].Trim());

    private static HAnchor VmlAnchor(XElement shape, Dictionary<string, string> style)
    {
        var wrap = (string?)shape.Element(W10 + "wrap")?.Attribute("type");
        return new HAnchor(
            HorizontalRelativeTo: style.GetValueOrDefault("mso-position-horizontal-relative") switch
            {
                "page" => "PAPER",
                "margin" => "PAGE",
                "char" => "PARA",
                _ => "COLUMN",
            },
            HorizontalAlign: style.GetValueOrDefault("mso-position-horizontal") switch
            {
                "center" => "CENTER",
                "right" => "RIGHT",
                _ => "LEFT",
            },
            HorizontalOffset: HwpxUnits.ParseLength(style.GetValueOrDefault("margin-left")) ?? 0,
            VerticalRelativeTo: style.GetValueOrDefault("mso-position-vertical-relative") switch
            {
                "page" => "PAPER",
                "margin" => "PAGE",
                _ => "PARA",
            },
            VerticalAlign: style.GetValueOrDefault("mso-position-vertical") switch
            {
                "center" => "CENTER",
                "bottom" => "BOTTOM",
                _ => "TOP",
            },
            VerticalOffset: HwpxUnits.ParseLength(style.GetValueOrDefault("margin-top")) ?? 0,
            Wrap: wrap switch
            {
                "square" or "tight" or "through" => "SQUARE",
                "topAndBottom" => "TOP_AND_BOTTOM",
                _ => style.TryGetValue("z-index", out var z) && z.StartsWith('-') ? "BEHIND_TEXT" : "IN_FRONT_OF_TEXT",
            },
            AllowOverlap: true)
        {
            TextFlow = WrapSide((string?)shape.Element(W10 + "wrap")?.Attribute("side")),
        };
    }

    /// <summary>
    /// The distance a floating VML shape keeps from the text around it: style mso-wrap-distance-left / -right / -top /
    /// -bottom, by default 9 pt beside it and none above and below (as Word draws them).
    /// </summary>
    private static HInsets VmlWrapDistance(Dictionary<string, string> style)
    {
        int Distance(string side, int fallback) =>
            Math.Clamp(HwpxUnits.ParseLength(style.GetValueOrDefault("mso-wrap-distance-" + side)) ?? fallback, 0, HwpxUnits.FromEmu(MaxWrapDistanceEmu));
        return new HInsets(Distance("left", 900), Distance("right", 900), Distance("top", 0), Distance("bottom", 0));
    }

    /// <summary>VML colours: "#RRGGBB", "#RGB", "#RRGGBB [index]" or one of the sixteen named colours ("silver").</summary>
    private static string? VmlColor(string? value)
    {
        var color = value?.Split(' ')[0].Trim();
        if (color is not null && NamedColors.TryGetValue(color, out var named))
            return named;
        if (color is null || !color.StartsWith('#'))
            return null;
        if (color.Length == 4)
            color = $"#{color[1]}{color[1]}{color[2]}{color[2]}{color[3]}{color[3]}";
        return color.Length == 7 && int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, null, out _) ? color.ToUpperInvariant() : null;
    }

    /// <summary>The colour names VML takes (HTML's sixteen); Word's watermarks are "silver".</summary>
    private static readonly Dictionary<string, string> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "#000000",
        ["silver"] = "#C0C0C0",
        ["gray"] = "#808080",
        ["white"] = "#FFFFFF",
        ["maroon"] = "#800000",
        ["red"] = "#FF0000",
        ["purple"] = "#800080",
        ["fuchsia"] = "#FF00FF",
        ["green"] = "#008000",
        ["lime"] = "#00FF00",
        ["olive"] = "#808000",
        ["yellow"] = "#FFFF00",
        ["navy"] = "#000080",
        ["blue"] = "#0000FF",
        ["teal"] = "#008080",
        ["aqua"] = "#00FFFF",
    };
}
