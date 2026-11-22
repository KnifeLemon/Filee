// Objects inside runs: pictures (inline or floating), rectangles / ellipses / lines, connectors (hp:connectLine: straight
// ones as lines, bent and curved ones as open paths through their control points), text boxes (drawing objects with
// hp:drawText), groups (hp:container), equations and other objects that only carry text. Charts (hp:chart, whose
// chartIDRef names an OOXML chart part in the package) are read by ChartReader and become a picture of the chart that
// carries it, so the renderer draws it as vectors and the writers get the picture. Objects the model cannot hold (OLE,
// charts ChartReader doesn't draw, video, form controls, free-form shapes without text) are skipped and counted. Read for layout,
// polygons, curves and arcs become shapes with their outline, and pictures, shapes and text boxes keep their caption.
// Every object keeps its rotation, mirroring and z-order, pictures their crop.

using System.Globalization;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class HwpxReader
{
    /// <summary>Drawing objects that may appear in a run or a group.</summary>
    private static readonly HashSet<string> ShapeNames = ["pic", "rect", "ellipse", "line", "arc", "polygon", "curve", "connectLine", "container"];

    /// <summary>Chart points drawn in the document.</summary>
    private readonly Charts.ChartBudget _charts = new();

    /// <summary>Size and position of an object: <c>Anchor</c> is null for objects laid out like a character.</summary>
    private readonly record struct Placement(int Width, int Height, HAnchor? Anchor);

    private void DrawingObject(XElement element, ParagraphBuilder builder, HCharFormat format)
    {
        var name = element.Name.LocalName;
        if (ShapeNames.Contains(name))
        {
            var placement = PlacementOf(element);
            var margin = Child(element, "outMargin") is { } outMargin
                ? new HInsets(Int(outMargin, "left"), Int(outMargin, "right"), Int(outMargin, "top"), Int(outMargin, "bottom"))
                : default;
            var caption = _forLayout && name != "container" ? Caption(Child(element, "caption")) : null;
            foreach (var inline in Shapes(element, placement))
            {
                // Laid out like a character, 한글 sets the object like a character as high as it is with its margins.
                if (_forLayout && inline is HObject item)
                {
                    item.OuterMargin = margin;
                    if (placement.Anchor is null)
                        item.BaseLine = 85;
                    item.Caption = caption;
                }
                builder.Add(inline);
            }
            return;
        }
        switch (name)
        {
            case "equation":
                if (Child(element, "script")?.Value.Trim() is not { Length: > 0 } script)
                    break;
                if (_forLayout)
                {
                    // Read for layout: the equation tree in the box 한글 laid it out in.
                    var size = Child(element, "sz");
                    builder.Add(new HEquation(EquationParser.Parse(script), format)
                    {
                        Width = Int(size, "width") is > 0 and var width ? width : null,
                        Height = Int(size, "height") is > 0 and var height ? height : null,
                        BaseLine = Attr(element, "baseLine") is not null ? Math.Clamp(Int(element, "baseLine"), 0, 100) : null,
                        Size = Int(element, "baseUnit") is > 0 and var unit ? unit : null,
                        Color = Color(Attr(element, "textColor")),
                    });
                    break;
                }
                // The equation script (한글's equation language, e.g. "x over y") is the closest text form.
                builder.AddText(script, format);
                break;
            case "textart":
                if ((Attr(element, "text") ?? Child(element, "text")?.Value) is { Length: > 0 } art)
                    builder.AddText(art, format);
                break;
            case "compose":
                // Overlapping characters (글자 겹치기): the characters one after the other.
                if ((Attr(element, "composeText") ?? Child(element, "composeText")?.Value) is { Length: > 0 } composed)
                    builder.AddText(composed, format);
                break;
            case "dutmal":
                // Ruby text (덧말): the main text; the small text above it has no place in the model.
                if ((Attr(element, "mainText") ?? Child(element, "mainText")?.Value) is { Length: > 0 } main)
                    builder.AddText(main, format);
                break;
            case "btn" or "checkBtn" or "radioBtn" or "edit" or "comboBox" when _forLayout:
                builder.Add(FormControl(element));
                break;
            case "chart" when ChartPicture(element) is { } chart:
                builder.Add(chart);
                break;
            case "chart" when OleFallback(element) is { } ole:
                // A chart ChartReader doesn't draw: the OLE object 한글 saved beside it (hp:switch's hp:default).
                DrawingObject(ole, builder, format);
                break;
            case "ole" when _forLayout && OleChart(element) is { } oleChart:
                // A 한글 chart's DrawingML part, kept in its OLE object (OOXMLChartContents).
                builder.Add(oleChart);
                break;
            case "ole" when _forLayout && OlePicture(element) is { } picture:
                builder.Add(picture);
                // A chart whose own data isn't read (한글 Neo's older chart object) is drawn from its picture, and named so.
                _leftOut.Add(OleContent(element).IsChart ? HLeftOutKind.ChartPictured : HLeftOutKind.ObjectPictured);
                break;
            default:
                if (name switch
                {
                    "ole" => _forLayout && OleContent(element).IsChart ? HLeftOutKind.Chart : HLeftOutKind.ObjectLeftOut,
                    "chart" => HLeftOutKind.Chart,
                    "video" => HLeftOutKind.Video,
                    _ => (HLeftOutKind?)null,
                } is { } leftOut)
                    _leftOut.Add(leftOut);
                Skip(element); // ole, chart, video, form controls, ...
                break;
        }
    }

    /// <summary>An object's caption (hp:caption): its side, its gap to the object, its width beside it and its paragraphs.</summary>
    private HCaption? Caption(XElement? element)
    {
        if (element is null || Child(element, "subList") is not { } subList)
            return null;
        var caption = new HCaption(Attr(element, "side") switch
        {
            "TOP" => HCaptionSide.Top,
            "LEFT" => HCaptionSide.Left,
            "RIGHT" => HCaptionSide.Right,
            _ => HCaptionSide.Bottom,
        }, Math.Max(0, Int(element, "gap")), Math.Max(0, Int(element, "width")));
        caption.Blocks.AddRange(ReadSubList(subList));
        return caption.Blocks.Count > 0 ? caption : null;
    }

    /// <summary>
    /// A form control (read for layout) as 한글 prints it: a box of the control's size and background with its text in
    /// the control's character format and colour, a button's caption centred in a framed box, a check box's or radio
    /// button's caption after a small square or circle (with a check or a dot in it when it is on: value CHECKED), an
    /// edit box's text (masked by its password character) or a
    /// combo box's text in a frame.
    /// </summary>
    private HTextBox FormControl(XElement control)
    {
        var name = control.Name.LocalName;
        var size = Child(control, "sz");
        var anchor = Anchor(control);
        var format = CharFormat(Attr(Child(control, "formCharPr"), "charPrIDRef"));
        if (Color(Attr(control, "foreColor")) is { } color)
            format = format with { Color = color };
        var framed = name is "btn" or "edit" or "comboBox" && Attr(control, "drawFrame") != "0";
        var box = new HTextBox
        {
            Width = Int(size, "width"),
            Height = Int(size, "height"),
            Anchor = anchor,
            BaseLine = anchor is null ? 85 : null,
            Fill = name is "checkBtn" or "radioBtn" && Attr(control, "backStyle") == "TRANSPARENT" ? null : Color(Attr(control, "backColor")),
            Line = framed ? new HBorder(HBorderStyle.Solid, 0.12, "#808080") : HBorder.None,
            Padding = new HInsets(141, 141, 0, 0),
            VerticalAlign = HVerticalAlign.Center,
            OuterMargin = Child(control, "outMargin") is { } outMargin
                ? new HInsets(Int(outMargin, "left"), Int(outMargin, "right"), Int(outMargin, "top"), Int(outMargin, "bottom"))
                : default,
        };
        var paragraph = new HParagraph { Format = new HParaFormat(Align: name == "btn" ? HAlign.Center : HAlign.Left, Left: 0, Right: 0, FirstLine: 0) };
        if (name is "checkBtn" or "radioBtn")
        {
            // The mark is drawn, not written: a square or a circle as high as the text.
            var mark = Math.Max(500, (format.Size ?? 1000) * 9 / 10);
            var kind = name == "checkBtn" ? HShapeKind.Rectangle : HShapeKind.Ellipse;
            var frame = new HBorder(HBorderStyle.Solid, 0.12, "#808080");
            if (Attr(control, "value") == "CHECKED")
            {
                // On: the same square or circle with a check or a dot in it, as the control shows its value.
                var on = new HTextBox { Width = mark, Height = mark, Shape = kind, Fill = "#FFFFFF", Line = frame, BaseLine = 85, Padding = default, VerticalAlign = HVerticalAlign.Center };
                var inside = new HParagraph { Format = new HParaFormat(Align: HAlign.Center, Left: 0, Right: 0, FirstLine: 0) };
                inside.Inlines.Add(new HText(kind == HShapeKind.Rectangle ? "✓" : "●", format with { Size = mark * 3 / 4 }));
                on.Blocks.Add(inside);
                paragraph.Inlines.Add(on);
            }
            else
            {
                paragraph.Inlines.Add(new HShape(kind) { Width = mark, Height = mark, Fill = "#FFFFFF", Line = frame, BaseLine = 85 });
            }
            paragraph.Inlines.Add(new HText(" ", format));
        }
        var text = name is "edit" or "comboBox" ? Child(control, "text")?.Value ?? "" : Attr(control, "caption") ?? "";
        if (name == "edit" && Attr(control, "passwordChar") is { Length: > 0 } password && text.Length > 0)
            text = new string(password[0], text.Length);
        if (text.Length > 0)
            paragraph.Inlines.Add(new HText(text, format));
        box.Blocks.Add(paragraph);
        return box;
    }

    /// <summary>
    /// An OLE object (read for layout) as the picture its program left of it (Binary/OlePreview.cs), in the object's
    /// place and size; null when it has none.
    /// </summary>
    private HImage? OlePicture(XElement ole)
    {
        if (OleContent(ole).Preview is not { } preview)
            return null;
        _budget.Object();
        var placement = PlacementOf(ole);
        return new HImage(preview)
        {
            Width = placement.Width > 0 ? placement.Width : null,
            Height = placement.Height > 0 ? placement.Height : null,
            Anchor = placement.Anchor,
            Transform = Transform(ole),
            ZOrder = ZOrder(ole),
            OuterMargin = Child(ole, "outMargin") is { } outMargin
                ? new HInsets(Int(outMargin, "left"), Int(outMargin, "right"), Int(outMargin, "top"), Int(outMargin, "bottom"))
                : default,
            BaseLine = placement.Anchor is null ? 85 : null,
        };
    }

    /// <summary>What an OLE object holds (read once a document through <see cref="OlePreviews"/>): its picture, its chart part, whether it is a chart.</summary>
    private Binary.OlePreview.Content OleContent(XElement ole) =>
        ExtractBinary(Attr(ole, "binaryItemIDRef")) is { } path ? OlePreviews.Read(path, path + "-preview") : new(null, null, false);

    /// <summary>
    /// An OLE object that is a 한글 chart with its DrawingML part (OOXMLChartContents) ChartReader draws, as a chart
    /// picture like <see cref="ChartPicture"/>'s; null otherwise. The part is read within the XML limits.
    /// </summary>
    private HImage? OleChart(XElement ole)
    {
        if (OleContent(ole).ChartPart is not { } bytes)
            return null;
        XElement? root;
        try
        {
            root = Infrastructure.BoundedXml.Bytes(bytes, "OOXMLChartContents", LoadOptions.None).Root;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException)
        {
            return null;
        }
        var placement = PlacementOf(ole);
        if (placement.Width <= 0 || placement.Height <= 0 || Charts.ChartReader.Read(root, Slides.DrawingColors.FromTheme(null)).Chart is not { } chart)
            return null;
        return ChartImage(ole, chart, placement);
    }

    /// <summary>
    /// An hp:chart drawn from its OOXML chart part (chartIDRef, a path in the package): a PNG of it for the writers that
    /// carries the chart for the renderer; null when the part is missing or ChartReader doesn't draw it. HWPX has no
    /// DrawingML theme: scheme colours are Office's.
    /// </summary>
    private HImage? ChartPicture(XElement element)
    {
        var part = Attr(element, "chartIDRef")?.Trim().TrimStart('/');
        if (part is not { Length: > 0 } || part.Contains("..", StringComparison.Ordinal) || part.Contains(':', StringComparison.Ordinal))
            return null;
        var placement = PlacementOf(element);
        if (placement.Width <= 0 || placement.Height <= 0
            || Charts.ChartReader.Read(ChartPart(part), Slides.DrawingColors.FromTheme(null)).Chart is not { } chart)
            return null;
        return ChartImage(element, chart, placement);
    }

    /// <summary>A chart drawn as a PNG in the object's place and size, carrying the chart for the renderer.</summary>
    private HImage? ChartImage(XElement element, Charts.Chart chart, Placement placement)
    {
        _budget.Object();
        _charts.Charge(chart);
        // Points are HWPUNIT / 100.
        if (Charts.ChartPicture.Png(chart, placement.Width / 100f, placement.Height / 100f, 150) is not { } png)
            return null;
        Directory.CreateDirectory(_mediaFolder);
        var path = Path.Combine(_mediaFolder, $"hwpx-chart-{_media.Count + 1}.png");
        File.WriteAllBytes(path, png);
        _media[$"chart|{path}"] = path;
        return new HImage(path)
        {
            Width = placement.Width,
            Height = placement.Height,
            Anchor = placement.Anchor,
            Transform = Transform(element),
            ZOrder = ZOrder(element),
            OuterMargin = Child(element, "outMargin") is { } outMargin
                ? new HInsets(Int(outMargin, "left"), Int(outMargin, "right"), Int(outMargin, "top"), Int(outMargin, "bottom"))
                : default,
            BaseLine = placement.Anchor is null ? 85 : null,
            Chart = chart,
        };
    }

    /// <summary>The OLE object in the hp:default branch of the hp:switch whose hp:case holds <paramref name="chart"/>, or null.</summary>
    private static XElement? OleFallback(XElement chart) =>
        chart.Parent is { Name.LocalName: "case" } branch && branch.Parent is { Name.LocalName: "switch" } choice
            ? choice.Elements().FirstOrDefault(e => e.Name.LocalName == "default")?.Elements().FirstOrDefault(e => e.Name.LocalName == "ole")
            : null;

    /// <summary>A chart part's root; null when it is missing or isn't well-formed (the chart is then counted, not drawn).</summary>
    private XElement? ChartPart(string part)
    {
        try
        {
            return LoadXml(part, required: false)?.Root;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The model objects for one drawing object; groups give one object per member.</summary>
    private IEnumerable<HInline> Shapes(XElement element, Placement placement)
    {
        _budget.Object();
        switch (element.Name.LocalName)
        {
            case "pic":
                if (Picture(element, placement) is { } picture)
                    yield return picture;
                else
                    Skip("pic");
                break;
            case "rect":
                yield return Box(element, HShapeKind.Rectangle, placement);
                break;
            case "ellipse":
                yield return Box(element, HShapeKind.Ellipse, placement);
                break;
            case "line":
            case "connectLine" when ConnectorPath(element) is null:
                {
                    // A straight connector (or a bent or curved one without control points) is drawn as a line between its ends.
                    var start = Child(element, "startPt");
                    var end = Child(element, "endPt");
                    var points = start is not null && end is not null;
                    var lineShape = Child(element, "lineShape");
                    yield return new HShape(HShapeKind.Line)
                    {
                        // A horizontal or vertical line still has a size of one unit across in the file.
                        Width = points && Int(start, "x") == Int(end, "x") ? 0 : placement.Width,
                        Height = points && Int(start, "y") == Int(end, "y") ? 0 : placement.Height,
                        Anchor = placement.Anchor,
                        Line = LineShape(lineShape),
                        // A line runs corner to corner of its box; the end points say which corners.
                        FlipHorizontal = Int(start, "x") > Int(end, "x"),
                        FlipVertical = Int(start, "y") > Int(end, "y"),
                        Transform = Transform(element),
                        ZOrder = ZOrder(element),
                        Head = Arrow(lineShape, "head"),
                        Tail = Arrow(lineShape, "tail"),
                    };
                    break;
                }
            case "connectLine" when ConnectorPath(element) is { } connector:
                {
                    // A bent (STROKE_*) or curved (ARC_*) connector: its path through the control points.
                    var original = Child(element, "orgSz");
                    var lineShape = Child(element, "lineShape");
                    yield return new HShape(HShapeKind.Path)
                    {
                        Width = placement.Width,
                        Height = placement.Height,
                        Anchor = placement.Anchor,
                        Geometry = new HGeometry(Int(original, "width", placement.Width), Int(original, "height", placement.Height), Path: connector),
                        Line = LineShape(lineShape),
                        Transform = Transform(element),
                        ZOrder = ZOrder(element),
                        Head = Arrow(lineShape, "head"),
                        Tail = Arrow(lineShape, "tail"),
                    };
                    break;
                }
            case "container":
                foreach (var member in Group(element, placement))
                    yield return member;
                break;
            case "polygon" or "curve" or "arc" when _forLayout:
                {
                    var kind = element.Name.LocalName switch
                    {
                        "polygon" => HShapeKind.Polygon,
                        "curve" => HShapeKind.Curve,
                        _ => HShapeKind.Arc,
                    };
                    yield return Box(element, kind, placement, Geometry(element, kind, placement));
                    break;
                }
            default:
                // Arcs, polygons and curves: only their text survives, in a box without outline.
                if (Child(element, "drawText") is not null)
                {
                    var box = (HTextBox)Box(element, HShapeKind.Rectangle, placement);
                    box.Line = HBorder.None;
                    box.Fill = null;
                    box.Gradient = null;
                    yield return box;
                }
                else
                {
                    Skip(element);
                }
                break;
        }
    }

    private HImage? Picture(XElement pic, Placement placement)
    {
        var image = pic.Descendants().FirstOrDefault(e => e.Name.LocalName == "img" && e.Attribute("binaryItemIDRef") is not null);
        return ExtractBinary(Attr(image, "binaryItemIDRef")) is { } path
            ? new HImage(path)
            {
                Width = placement.Width > 0 ? placement.Width : null,
                Height = placement.Height > 0 ? placement.Height : null,
                Anchor = placement.Anchor,
                Crop = Crop(pic),
                Transform = Transform(pic),
                ZOrder = ZOrder(pic),
                Margin = Child(pic, "inMargin") is { } margin
                    ? new HInsets(Int(margin, "left"), Int(margin, "right"), Int(margin, "top"), Int(margin, "bottom"))
                    : default,
                Line = LineShape(Child(pic, "lineShape")),
            }
            : null;
    }

    /// <summary>
    /// The part of the picture shown: hp:imgClip is a rectangle in the picture's original size (hp:imgDim), which
    /// spans the whole picture when nothing is cut away. Files from Filee write zeros for both: no crop.
    /// </summary>
    private static HCrop? Crop(XElement pic)
    {
        var clip = Child(pic, "imgClip");
        var dimension = Child(pic, "imgDim");
        var (width, height) = (Int(dimension, "dimwidth"), Int(dimension, "dimheight"));
        var (right, bottom) = (Int(clip, "right"), Int(clip, "bottom"));
        if (clip is null || width <= 0 || height <= 0 || right <= 0 || bottom <= 0)
            return null;
        static double Fraction(int part, int whole) => Math.Clamp(part / (double)whole, 0, 1);
        var crop = new HCrop(Fraction(Int(clip, "left"), width), Fraction(Int(clip, "top"), height), Fraction(width - right, width), Fraction(height - bottom, height));
        return crop.IsEmpty ? null : crop;
    }

    /// <summary>Rotation (hp:rotationInfo) and mirroring (hp:flip) of a drawing object; null when it has neither.</summary>
    private static HTransform? Transform(XElement element)
    {
        var flip = Child(element, "flip");
        var transform = new HTransform(Int(Child(element, "rotationInfo"), "angle"), Flag(flip, "horizontal"), Flag(flip, "vertical"));
        return transform.IsIdentity ? null : transform;
    }

    private static int? ZOrder(XElement element) => Attr(element, "zOrder") is not null ? Int(element, "zOrder") : null;

    /// <summary>
    /// The path of a bent (type STROKE_*) or curved (ARC_*) hp:connectLine in its original size, from hp:startPt
    /// through hp:controlPoints to hp:endPt; null for a straight one or one without control points (read as a line).
    /// OWPML leaves the control points' type numbers undescribed; as other readers of 한글's files take them, 3 marks
    /// the start, 26 the end and 2 a point between. A bent connector runs straight through its points; a curved one
    /// takes its points between as Bézier controls (two per cubic, see OpenPaths.FromControls).
    /// </summary>
    private static HPath? ConnectorPath(XElement element)
    {
        var type = Attr(element, "type") ?? "";
        var curved = type.StartsWith("ARC", StringComparison.Ordinal);
        if (!curved && !type.StartsWith("STROKE", StringComparison.Ordinal))
            return null;
        static HPoint Point(XElement? point) => new(Int(point, "x"), Int(point, "y"));
        var points = Children(Child(element, "controlPoints"), "point").Take(OpenPaths.MaxSegments * 2 + 2).ToList();
        if (points.Count == 0 || points.Count > OpenPaths.MaxSegments * 2)
            return null;
        var start = points[0] is var first && Int(first, "type") == 3 ? Point(first) : Point(Child(element, "startPt"));
        var end = points[^1] is var last && Int(last, "type") == 26 ? Point(last) : Point(Child(element, "endPt"));
        var between = points.Where(p => Int(p, "type") is not (3 or 26)).Select(Point).ToList();
        return curved
            ? OpenPaths.FromControls(start, end, between)
            : new HPath(start, [.. between.Select(p => new HPathSegment(p)), new HPathSegment(end)]);
    }

    /// <summary>Outline of a polygon (hc:pt), curve (hp:seg) or arc (center, ax1, ax2) in its original size (hp:orgSz).</summary>
    private static HGeometry Geometry(XElement element, HShapeKind kind, Placement placement)
    {
        var original = Child(element, "orgSz");
        var (width, height) = (Int(original, "width", placement.Width), Int(original, "height", placement.Height));
        static HPoint Point(XElement? point) => new(Int(point, "x"), Int(point, "y"));
        return kind switch
        {
            HShapeKind.Polygon => new HGeometry(width, height, Points: Children(element, "pt").Select(Point).ToList()),
            HShapeKind.Curve => new HGeometry(width, height, Segments: Children(element, "seg")
                .Select(s => new HCurveSegment(Attr(s, "type") == "CURVE", new HPoint(Int(s, "x1"), Int(s, "y1")), new HPoint(Int(s, "x2"), Int(s, "y2"))))
                .ToList()),
            _ => new HGeometry(width, height, Arc: new HArc(Attr(element, "type") switch
            {
                "PIE" => HArcKind.Pie,
                "CHORD" => HArcKind.Chord,
                _ => HArcKind.Normal,
            }, Point(Child(element, "center")), Point(Child(element, "ax1")), Point(Child(element, "ax2")))),
        };
    }

    /// <summary>
    /// A rectangle or ellipse (read for layout also a polygon, curve or arc with its <paramref name="geometry"/>): a
    /// text box when it has text (hp:drawText), else a plain shape.
    /// </summary>
    private HInline Box(XElement element, HShapeKind kind, Placement placement, HGeometry? geometry = null)
    {
        var lineShape = Child(element, "lineShape");
        var line = LineShape(lineShape);
        var (fill, gradient) = FillBrush(Child(element, "fillBrush"));
        var ratio = kind == HShapeKind.Rectangle ? Int(element, "ratio") : 0;
        if (Child(element, "drawText") is not { } drawText)
        {
            return new HShape(kind)
            {
                Width = placement.Width,
                Height = placement.Height,
                Anchor = placement.Anchor,
                Fill = fill,
                Gradient = gradient,
                Line = line,
                CornerRatio = ratio,
                Transform = Transform(element),
                ZOrder = ZOrder(element),
                Geometry = geometry,
                // Open curves and arcs have ends for arrowheads (the renderer leaves them off closed outlines).
                Head = kind is HShapeKind.Curve or HShapeKind.Arc or HShapeKind.Polygon ? Arrow(lineShape, "head") : null,
                Tail = kind is HShapeKind.Curve or HShapeKind.Arc or HShapeKind.Polygon ? Arrow(lineShape, "tail") : null,
            };
        }

        var subList = Child(drawText, "subList");
        var margin = Child(drawText, "textMargin");
        var box = new HTextBox
        {
            Width = placement.Width,
            Height = placement.Height,
            Anchor = placement.Anchor,
            Fill = fill,
            Gradient = gradient,
            Line = line,
            Shape = kind,
            CornerRatio = ratio,
            VerticalAlign = VerticalAlign(Attr(subList, "vertAlign")),
            Transform = Transform(element),
            ZOrder = ZOrder(element),
            Geometry = geometry,
            TextFlow = TextFlow(subList),
        };
        if (margin is not null)
            box.Padding = new HInsets(Int(margin, "left"), Int(margin, "right"), Int(margin, "top"), Int(margin, "bottom"));
        box.Blocks.AddRange(ReadSubList(subList, turned: true));
        return box;
    }

    /// <summary>
    /// Members of a group (묶음 개체) become separate objects at their place inside the group. An inline group floats
    /// over its paragraph instead, like the DOCX reader does with inline groups.
    /// </summary>
    private IEnumerable<HInline> Group(XElement container, Placement placement)
    {
        using var level = _budget.Enter();
        var anchor = placement.Anchor ?? new HAnchor("COLUMN", "LEFT", 0, "PARA", "TOP", 0, "IN_FRONT_OF_TEXT", true);
        foreach (var member in Elements(container))
        {
            if (!ShapeNames.Contains(member.Name.LocalName))
                continue; // the group's own size, position and rendering properties
            var offset = Child(member, "offset");
            var (x, y) = (Int(offset, "x"), Int(offset, "y"));
            var (width, height) = MemberSize(member);
            var memberAnchor = anchor with
            {
                HorizontalOffset = anchor.HorizontalOffset + AlignedOffset(anchor.HorizontalAlign, x, width, placement.Width),
                VerticalOffset = anchor.VerticalOffset + AlignedOffset(anchor.VerticalAlign, y, height, placement.Height),
                AllowOverlap = true,
            };
            foreach (var inline in Shapes(member, new Placement(width, height, memberAnchor)))
                yield return inline;
        }
    }

    /// <summary>Keeps a member's distance from the group's centre or far edge when the group is aligned, not placed.</summary>
    private static int AlignedOffset(string align, int position, int size, int groupSize) => align switch
    {
        "CENTER" => position + size / 2 - groupSize / 2,
        "RIGHT" or "BOTTOM" => position + size - groupSize,
        _ => position,
    };

    /// <summary>
    /// Size of a group member: its current size, else its original size scaled by its rendering matrices. A member
    /// has a scale matrix for every group level it is in (the group's, then its own: a picture resized inside its group
    /// keeps the identity first and its own scale second), so the size takes all of them.
    /// </summary>
    private static (int Width, int Height) MemberSize(XElement member)
    {
        var current = Child(member, "curSz");
        if (Int(current, "width") > 0 && Int(current, "height") > 0)
            return (Int(current, "width"), Int(current, "height"));
        var original = Child(member, "orgSz");
        var scales = Children(Child(member, "renderingInfo"), "scaMatrix").ToList();
        double x = 1, y = 1;
        foreach (var scale in scales)
        {
            x *= Double(scale, "e1", 1);
            y *= Double(scale, "e5", 1);
        }
        return ((int)Math.Round(Int(original, "width") * x), (int)Math.Round(Int(original, "height") * y));
    }

    /// <summary>
    /// An object's size and position. Drawings other than pictures take the size 한글 laid them out at (hp:curSz) where
    /// it is given: hp:sz may be relative (a share of the column) or not grown to the text; a picture's hp:sz is its box
    /// with the inner margin, which hp:curSz leaves out.
    /// </summary>
    private static Placement PlacementOf(XElement element)
    {
        var size = Child(element, "sz");
        var (width, height) = (Int(size, "width"), Int(size, "height"));
        if (element.Name.LocalName != "pic" && Child(element, "curSz") is { } current)
        {
            if (Int(current, "width") is > 0 and var w)
                width = w;
            if (Int(current, "height") is > 0 and var h)
                height = h;
        }
        if (width <= 0 || height <= 0)
            (width, height) = MemberSize(element);
        return new Placement(width, height, Anchor(element));
    }

    /// <summary>Position of a floating object (hp:pos); null when it is laid out like a character (treatAsChar).</summary>
    private static HAnchor? Anchor(XElement element)
    {
        var pos = Child(element, "pos");
        if (pos is null || Flag(pos, "treatAsChar"))
            return null;
        return new HAnchor(
            HorizontalRelativeTo: Attr(pos, "horzRelTo") ?? "COLUMN",
            HorizontalAlign: Attr(pos, "horzAlign") ?? "LEFT",
            HorizontalOffset: Int(pos, "horzOffset"),
            VerticalRelativeTo: Attr(pos, "vertRelTo") ?? "PARA",
            VerticalAlign: Attr(pos, "vertAlign") ?? "TOP",
            VerticalOffset: Int(pos, "vertOffset"),
            Wrap: Attr(element, "textWrap") switch
            {
                "SQUARE" or "TIGHT" or "THROUGH" => "SQUARE",
                "BEHIND_TEXT" => "BEHIND_TEXT",
                "IN_FRONT_OF_TEXT" => "IN_FRONT_OF_TEXT",
                _ => "TOP_AND_BOTTOM",
            },
            AllowOverlap: Flag(pos, "allowOverlap"))
        {
            TextFlow = Attr(element, "textFlow") is ("LEFT_ONLY" or "RIGHT_ONLY" or "LARGEST_ONLY") and var flow ? flow : "BOTH_SIDES",
        };
    }

    /// <summary>Outline of a drawing object; its width is in HWPUNIT (a width of 0 is 한글's thinnest line).</summary>
    private static HBorder LineShape(XElement? lineShape)
    {
        var style = LineStyle(Attr(lineShape, "style"));
        if (lineShape is null || style == HBorderStyle.None)
            return HBorder.None;
        var width = Int(lineShape, "width");
        return new HBorder(style, width > 0 ? width / HwpxUnits.PerMm : 0.1, Color(Attr(lineShape, "color")) ?? "#000000");
    }

    /// <summary>
    /// The arrowhead at a drawing's start (<paramref name="end"/> "head") or end ("tail"): hp:lineShape headStyle (ARROW,
    /// SPEAR, CONCAVE_ARROW, EMPTY_ / FILLED_ DIAMOND, CIRCLE, BOX; NORMAL = none) and headSz (width_length, SMALL /
    /// MEDIUM / LARGE). Null for none or no outline.
    /// </summary>
    private static HArrow? Arrow(XElement? lineShape, string end)
    {
        if (lineShape is null || LineStyle(Attr(lineShape, "style")) == HBorderStyle.None)
            return null;
        (HArrowKind Kind, bool Hollow)? style = Attr(lineShape, end + "Style") switch
        {
            "ARROW" => (HArrowKind.Triangle, false),
            "SPEAR" => (HArrowKind.Spear, false),
            "CONCAVE_ARROW" => (HArrowKind.Stealth, false),
            "EMPTY_DIAMOND" => (HArrowKind.Diamond, true),
            "EMPTY_CIRCLE" => (HArrowKind.Oval, true),
            "EMPTY_BOX" => (HArrowKind.Square, true),
            "FILLED_DIAMOND" or "DIAMOND" => (HArrowKind.Diamond, false),
            "FILLED_CIRCLE" or "CIRCLE" => (HArrowKind.Oval, false),
            "FILLED_BOX" or "BOX" => (HArrowKind.Square, false),
            _ => null,
        };
        if (style is not { } s)
            return null;
        var size = (Attr(lineShape, end + "Sz") ?? "MEDIUM_MEDIUM").Split('_');
        static HArrowSize Size(string? value) => value switch
        {
            "SMALL" => HArrowSize.Small,
            "LARGE" => HArrowSize.Large,
            _ => HArrowSize.Medium,
        };
        return new HArrow(s.Kind, Size(size.ElementAtOrDefault(0)), Size(size.ElementAtOrDefault(1)), s.Hollow);
    }

    /// <summary>A solid colour (hc:winBrush) or linear gradient (hc:gradation); picture fills are not kept.</summary>
    private static (string? Fill, HGradient? Gradient) FillBrush(XElement? fillBrush)
    {
        if (Child(fillBrush, "gradation") is { } gradation)
        {
            var colors = Children(gradation, "color").Select(c => Color(Attr(c, "value"))).OfType<string>().ToList();
            if (colors.Count >= 2)
                return (null, new HGradient(colors, Int(gradation, "angle")));
            if (colors.Count == 1)
                return (colors[0], null);
        }
        return (Color(Attr(Child(fillBrush, "winBrush"), "faceColor")), null);
    }

    private static double Double(XElement? element, string name, double fallback) =>
        double.TryParse(Attr(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value != 0 ? value : fallback;
}
