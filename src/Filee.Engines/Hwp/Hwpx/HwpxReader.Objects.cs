// Objects inside runs: pictures (inline or floating), rectangles / ellipses / lines, text boxes (drawing objects with
// hp:drawText), groups (hp:container), equations and other objects that only carry text. Objects the model cannot
// hold (OLE, charts, video, form controls, free-form shapes without text) are skipped and counted. Read for layout,
// polygons, curves and arcs become shapes with their outline. Every object keeps its rotation, mirroring and z-order,
// pictures their crop.

using System.Globalization;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class HwpxReader
{
    /// <summary>Drawing objects that may appear in a run or a group.</summary>
    private static readonly HashSet<string> ShapeNames = ["pic", "rect", "ellipse", "line", "arc", "polygon", "curve", "connectLine", "container"];

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
            foreach (var inline in Shapes(element, placement))
            {
                // Laid out like a character, 한글 sets the object like a character as high as it is with its margins.
                if (_forLayout && inline is HObject item)
                {
                    item.OuterMargin = margin;
                    if (placement.Anchor is null)
                        item.BaseLine = 85;
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
            case "ole" when _forLayout && OlePicture(element) is { } picture:
                builder.Add(picture);
                break;
            default:
                Skip(element); // ole, chart, video, form controls, ...
                break;
        }
    }

    /// <summary>
    /// An OLE object (read for layout) as the picture its program left of it (Binary/OlePreview.cs), in the object's
    /// place and size; null when it has none.
    /// </summary>
    private HImage? OlePicture(XElement ole)
    {
        if (ExtractBinary(Attr(ole, "binaryItemIDRef")) is not { } path || Binary.OlePreview.Extract(path, path + "-preview", Package.Limits) is not { } preview)
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
                {
                    var start = Child(element, "startPt");
                    var end = Child(element, "endPt");
                    var points = start is not null && end is not null;
                    yield return new HShape(HShapeKind.Line)
                    {
                        // A horizontal or vertical line still has a size of one unit across in the file.
                        Width = points && Int(start, "x") == Int(end, "x") ? 0 : placement.Width,
                        Height = points && Int(start, "y") == Int(end, "y") ? 0 : placement.Height,
                        Anchor = placement.Anchor,
                        Line = LineShape(Child(element, "lineShape")),
                        // A line runs corner to corner of its box; the end points say which corners.
                        FlipHorizontal = Int(start, "x") > Int(end, "x"),
                        FlipVertical = Int(start, "y") > Int(end, "y"),
                        Transform = Transform(element),
                        ZOrder = ZOrder(element),
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
                // Arcs, polygons, curves and connectors: only their text survives, in a box without outline.
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
        var line = LineShape(Child(element, "lineShape"));
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
        };
        if (margin is not null)
            box.Padding = new HInsets(Int(margin, "left"), Int(margin, "right"), Int(margin, "top"), Int(margin, "bottom"));
        box.Blocks.AddRange(ReadSubList(subList));
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

    /// <summary>Size of a group member: its current size, else its original size scaled by its rendering matrix.</summary>
    private static (int Width, int Height) MemberSize(XElement member)
    {
        var current = Child(member, "curSz");
        if (Int(current, "width") > 0 && Int(current, "height") > 0)
            return (Int(current, "width"), Int(current, "height"));
        var original = Child(member, "orgSz");
        var scale = Child(Child(member, "renderingInfo"), "scaMatrix");
        return ((int)Math.Round(Int(original, "width") * Double(scale, "e1", 1)), (int)Math.Round(Int(original, "height") * Double(scale, "e5", 1)));
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
            AllowOverlap: Flag(pos, "allowOverlap"));
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
