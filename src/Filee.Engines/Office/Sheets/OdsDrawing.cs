// The pictures and shapes of an OpenDocument spreadsheet's tables (ODF 1.3 Part 3, 10.3 and 10.4), read for the sheet
// printer: frames holding a picture (PNG, JPEG, GIF, BMP, WebP; in the package or inline; cropped by fo:clip) or a
// text box, rectangles, ellipses and circles, lines, custom shapes (of a type the slide renderer has a preset for, or
// with their own draw:enhanced-path, evaluated by OdfEnhancedGeometry), polygons, polylines and paths (draw:points /
// svg:d in their svg:viewBox), and groups of them. Each is anchored to the cell it sits in (its svg:x / svg:y from the
// cell's top-left corner, its far corner in table:end-cell-address when it moves and sizes with the cells) or to the
// sheet (table:shapes), turned and mirrored by draw:transform and the geometry's mirror flags, filled (one colour, or a
// draw:gradient: linear, axial, radial, ellipsoid, square, rectangular) and outlined from its graphic style (with its
// parents and the default graphic style), with its text: paragraphs and spans in their paragraph and text styles over
// the graphic style's font, aligned by fo:text-align (else draw:textarea-horizontal-align), placed by
// draw:textarea-vertical-align inside the fo:padding. Charts in frames are read by OdsReader (OdfChartReader), which
// also takes the boxes of notes shown on the sheet from here (Note).
//
// What the printer doesn't draw is named instead, as XlsxDrawing names it: arcs, connectors, form controls, embedded
// objects, SVG / EMF / WMF / TIFF and linked pictures, hatch and picture fills, arrowheads, custom shapes of no known
// type without a path, text along a path (Fontwork). SheetCapabilities asks for that name (with nothing unpacked), so
// such a workbook can go to LibreOffice where it is installed. Objects set to show on screen only (draw:display) are
// left out, as Calc leaves them off the printout.
//
// Not checked against Calc: a cropped picture's clip is taken in the picture's size at 96 dpi (Calc takes the
// resolution the file names, when it names one), and text without a paragraph alignment is centred in shapes as Calc
// centres a shape's text area.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Odf.Draw;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Office.Sheets;

internal sealed class OdsDrawing
{
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Style = OdsNumberStyles.Style;
    private static readonly XNamespace Fo = OdsNumberStyles.Fo;
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    /// <summary>Groups inside groups followed at most; deeper ones are left out (and named).</summary>
    private const int MaxGroupDepth = 32;

    /// <summary>The largest position or size taken, in points (about 35 m); larger ones are clamped.</summary>
    private const double MaxLength = 100_000;

    /// <summary>Bytes of one picture read at most.</summary>
    private const long MaxPictureBytes = 64L * 1024 * 1024;

    /// <summary>Operations of one draw:transform read at most.</summary>
    private const int MaxTransformOperations = 16;

    /// <summary>Path commands and points of all one sheet's freeform shapes at most; a sheet with more is refused.</summary>
    private const long MaxCommands = 4_000_000;

    /// <summary>Paragraphs of one shape's text read at most; the rest is left out.</summary>
    private const int MaxParagraphs = 1000;

    /// <summary>Characters of one shape's text read at most.</summary>
    private const int MaxTextCharacters = 32_767;

    /// <summary>Calc's note box: light yellow, outlined black.</summary>
    private static readonly SKColor NoteFill = new(0xFF, 0xFF, 0xC0);

    /// <summary>Calc's default graphic style: shapes are filled light blue and outlined dark blue when nothing else says.</summary>
    private static readonly SKColor DefaultFill = new(0x72, 0x9F, 0xCF);
    private static readonly SKColor DefaultLine = new(0x34, 0x65, 0xA4);

    private readonly DocumentPackage? _zip;
    private readonly bool _read;
    private readonly Func<string, XElement?> _style;
    private readonly XElement? _default;
    private readonly Func<string, string, XElement?> _styles;
    private readonly Func<string, XElement?> _gradient;
    private long _commands;

    /// <summary>What is drawn, in drawing order; empty when only the check was asked for.</summary>
    public List<SheetDrawing> Drawings { get; } = [];

    /// <summary>The first thing the printer doesn't draw, or null.</summary>
    public string? FirstLeftOut { get; private set; }

    /// <param name="zip">The package the pictures are in.</param>
    /// <param name="read">Read the drawings; without it no picture is unpacked and only <see cref="FirstLeftOut"/> is found.</param>
    /// <param name="style">The graphic style (style:family "graphic") of a name, or null.</param>
    /// <param name="defaultStyle">The default graphic style (style:default-style), or null.</param>
    /// <param name="styles">A style of another family (paragraph, text) by family and name, or null; none when not given.</param>
    /// <param name="gradient">A draw:gradient by its draw:name, or null; none when not given.</param>
    public OdsDrawing(DocumentPackage zip, bool read, Func<string, XElement?> style, XElement? defaultStyle,
        Func<string, string, XElement?>? styles = null, Func<string, XElement?>? gradient = null)
    {
        _zip = zip;
        _read = read;
        _style = style;
        _default = defaultStyle;
        _styles = styles ?? ((_, _) => null);
        _gradient = gradient ?? (_ => null);
    }

    /// <summary>Counts path commands and points against the sheet's budget.</summary>
    private void Commands(long count)
    {
        _commands += Math.Max(0, count);
        if (_commands > MaxCommands)
            throw DocumentLimits.Exceeded($"The sheet's shapes have more than {DocumentLimits.Count(MaxCommands)} path commands and points.");
    }

    /// <summary>Whether <paramref name="element"/> is a frame holding an embedded object (a chart): OdsReader reads those.</summary>
    public static bool IsObjectFrame(XElement element) =>
        element.Name == Draw + "frame" && element.Element(Draw + "object") is not null;

    /// <summary>A drawing element of a cell at <paramref name="row"/>, <paramref name="column"/> (or of the sheet: 0, 0).</summary>
    public void Read(XElement element, int row, int column) => Element(element, row, column, 0, top: true);

    private void LeftOut(string what) => FirstLeftOut ??= what;

    private void Add(SheetDrawing drawing)
    {
        if (Drawings.Count < XlsxDrawing.MaxDrawings)
            Drawings.Add(drawing);
        else
            LeftOut("more drawings than Filee prints on one sheet");
    }

    private void Element(XElement element, int row, int column, int depth, bool top)
    {
        if (element.Name.Namespace != Draw || (string?)element.Attribute(Draw + "display") is "screen" or "none")
            return;
        switch (element.Name.LocalName)
        {
            case "frame":
                Frame(element, row, column, top);
                break;
            case "rect":
                var radius = OdsReader.Points((string?)element.Attribute(Draw + "corner-radius")) ?? 0;
                Shape(element, radius > 0 ? "roundRect" : "rect", row, column, top, false, false, radius);
                break;
            case "ellipse" or "circle":
                if ((string?)element.Attribute(Draw + "kind") is { } kind && kind != "full")
                    LeftOut("arcs");
                else
                    Shape(element, "ellipse", row, column, top, false, false, 0);
                break;
            case "line":
                Line(element, row, column);
                break;
            case "custom-shape":
                var geometry = element.Element(Draw + "enhanced-geometry");
                var type = (string?)geometry?.Attribute(Draw + "type");
                var (mirrorH, mirrorV) = ((string?)geometry?.Attribute(Draw + "mirror-horizontal") == "true", (string?)geometry?.Attribute(Draw + "mirror-vertical") == "true");
                if (geometry is not null && type is not (null or "non-primitive") && OdfEnhancedGeometry.Preset(type) is { } preset)
                {
                    Shape(element, preset, row, column, top, mirrorH, mirrorV, 0);
                    break;
                }
                // A shape of its own: its path, evaluated at the size it prints.
                var own = geometry is null ? null : OdfEnhancedGeometry.Parse(geometry);
                if (own is not { HasPath: true })
                {
                    LeftOut("freeform shapes");
                    break;
                }
                if (own.Fontwork)
                    LeftOut("text along a path");
                Commands(own.Cost);
                Shape(element, "rect", row, column, top, mirrorH, mirrorV, 0,
                    (properties, filled, stroked) => (width, height) => own.Evaluate(width, height, filled, stroked));
                break;
            case "g" or "a":
                if (depth >= MaxGroupDepth)
                {
                    LeftOut("grouped shapes");
                    break;
                }
                // A group's children are placed in the same space as the group (a link holds its frame).
                foreach (var child in element.Elements())
                    Element(child, row, column, depth + 1, top: false);
                break;
            case "polygon" or "polyline" or "path":
                Freeform(element, row, column, top);
                break;
            case "control":
                LeftOut("form controls");
                break;
            case "connector" or "measure" or "caption" or "regular-polygon" or "page-thumbnail" or "scene":
                LeftOut("shapes");
                break;
        }
    }

    // ───────────────────────── Frames ─────────────────────────

    private void Frame(XElement frame, int row, int column, bool top)
    {
        if (frame.Element(Draw + "object") is not null)
        {
            // A chart or another embedded object: OdsReader and SheetCapabilities see to those of a cell or the sheet;
            // one inside a group is named.
            if (!top)
                LeftOut("grouped charts and objects");
            return;
        }
        if (frame.Element(Draw + "object-ole") is not null || frame.Element(Draw + "plugin") is not null
            || frame.Element(Draw + "applet") is not null || frame.Element(Draw + "floating-frame") is not null)
        {
            LeftOut("embedded objects");
            return;
        }
        if (frame.Element(Draw + "text-box") is { } textBox)
        {
            TextBox(frame, textBox, row, column, top);
            return;
        }
        var images = frame.Elements(Draw + "image").ToList();
        if (images.Count == 0)
            return;
        // Calc keeps an SVG with a PNG beside it: the first picture the printer draws is taken.
        string? named = null;
        XElement? chosen = null;
        foreach (var image in images)
        {
            if (Kind(image) is { } why)
                named ??= why;
            else
            {
                chosen = image;
                break;
            }
        }
        if (chosen is null)
        {
            LeftOut(named ?? "pictures");
            return;
        }
        var properties = Properties((string?)frame.Attribute(Draw + "style-name"));
        if (!_read || Place(frame) is not { } place || place.Width <= 0 || place.Height <= 0 || Data(chosen) is not { } data)
            return;
        var picture = new SheetPicture(data, row, column, place.X, place.Y, place.Width, place.Height);
        if (properties.TryGetValue(Fo + "clip", out var clip) && Crop(clip, data) is { } crop)
            picture = picture with { Crop = crop };
        Add(Placed(picture, place, frame, top));
    }

    /// <summary>
    /// What fo:clip "rect(top, right, bottom, left)" cuts off the picture's edges, as fractions of its size (taken at
    /// 96 dpi); null when it cuts nothing or the picture's size can't be read.
    /// </summary>
    private static (double Left, double Top, double Right, double Bottom)? Crop(string clip, byte[] data)
    {
        if (!clip.Contains("rect", StringComparison.Ordinal))
            return null;
        var edges = clip.Replace("rect", "", StringComparison.Ordinal).Trim('(', ')', ' ').Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(edge => edge == "auto" ? 0 : Math.Clamp(OdsReader.Points(edge) ?? 0, 0, MaxLength)).ToArray();
        if (edges.Length != 4 || edges.All(edge => edge <= 0.01))
            return null;
        using var stream = new MemoryStream(data, writable: false);
        if (PictureSize.Read(stream) is not { Width: > 0, Height: > 0 } size)
            return null;
        var (width, height) = (size.Width * 0.75, size.Height * 0.75);
        double Part(double edge, double whole) => Math.Clamp(edge / whole, 0, 0.99);
        var crop = (Left: Part(edges[3], width), Top: Part(edges[0], height), Right: Part(edges[1], width), Bottom: Part(edges[2], height));
        return crop.Left + crop.Right >= 0.99 || crop.Top + crop.Bottom >= 0.99 ? null : crop;
    }

    /// <summary>A frame with a text box: its text in the frame's box, filled and outlined only when its style says so.</summary>
    private void TextBox(XElement frame, XElement textBox, int row, int column, bool top)
    {
        var properties = Properties((string?)frame.Attribute(Draw + "style-name"));
        var fill = Fill(properties, solid: false);
        var (line, width, dash) = Outline(properties, stroke: false);
        var text = TextOf(textBox, properties, CellVerticalAlign.Top, CellAlign.Left, (0, 0, 0, 0));
        if (!_read || Place(frame) is not { } place || text is null && fill.Color is null && line is null)
            return;
        var drawing = new SheetShape("rect", row, column, place.X, place.Y, place.Width, place.Height)
        {
            Fill = fill.Color,
            Gradient = fill.Gradient,
            Line = line,
            LineWidth = width,
            Dash = dash,
            Text = text,
        };
        Add(Placed(drawing, place, frame, top));
    }

    /// <summary>
    /// The box of a note (office:annotation) shown on the sheet: svg:x / svg:y from the sheet's corner, its size, its
    /// graphic style's fill (Calc's light yellow when it sets none) and outline, and its text; null without a place.
    /// </summary>
    public SheetShape? Note(XElement annotation)
    {
        var properties = Properties((string?)annotation.Attribute(Draw + "style-name"));
        if (!properties.ContainsKey(Draw + "fill-color"))
            properties[Draw + "fill-color"] = "#ffffc0";
        if (!properties.ContainsKey(Svg + "stroke-color"))
            properties[Svg + "stroke-color"] = "#000000";
        var fill = Fill(properties, solid: true);
        var (line, width, dash) = Outline(properties, stroke: true);
        var text = TextOf(annotation, properties, CellVerticalAlign.Top, CellAlign.Left, (2.8, 2.8, 2.8, 2.8));
        if (Place(annotation) is not { Width: > 0, Height: > 0 } place)
            return null;
        return new SheetShape("rect", 0, 0, place.X, place.Y, place.Width, place.Height)
        {
            Fill = fill.Color ?? NoteFill,
            Gradient = fill.Gradient,
            Line = line,
            LineWidth = width,
            Dash = dash,
            Text = text,
        };
    }

    /// <summary>Why a draw:image isn't drawn (its format, or a link), or null when it is.</summary>
    private string? Kind(XElement image)
    {
        if (image.Element(Office + "binary-data") is not null)
            return null;
        var href = Href(image);
        if (href is null)
            return (string?)image.Attribute(XLink + "href") is { Length: > 0 } ? "linked pictures" : "pictures";
        return Path.GetExtension(href).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".jpe" or ".gif" or ".bmp" or ".webp" => _zip?.GetEntry(href) is not null ? null : "pictures",
            ".svg" or ".svgz" => "SVG pictures",
            ".emf" or ".wmf" or ".emz" or ".wmz" or ".svm" => "EMF and WMF pictures",
            ".tif" or ".tiff" => "TIFF pictures",
            _ => "pictures",
        };
    }

    /// <summary>The package path of a picture ("Pictures/x.png", "./Pictures/x.png"); null for a link outside it.</summary>
    private static string? Href(XElement image)
    {
        var href = ((string?)image.Attribute(XLink + "href"))?.Trim();
        if (string.IsNullOrEmpty(href))
            return null;
        if (href.StartsWith("./", StringComparison.Ordinal))
            href = href[2..];
        return href.Contains(':', StringComparison.Ordinal) || href.StartsWith('/') || href.StartsWith('.') || href.Contains("..", StringComparison.Ordinal)
            ? null
            : Uri.UnescapeDataString(href);
    }

    private byte[]? Data(XElement image)
    {
        if (image.Element(Office + "binary-data") is { } inline)
        {
            var text = inline.Value;
            if (text.Length > MaxPictureBytes / 3 * 4 + 1024)
                return null;
            try
            {
                return Convert.FromBase64String(text);
            }
            catch (FormatException)
            {
                return null;
            }
        }
        return Href(image) is { } href && _zip?.GetEntry(href) is { } entry ? _zip.ReadBytes(entry, MaxPictureBytes) : null;
    }

    // ───────────────────────── Shapes ─────────────────────────

    /// <param name="geometry">For a shape of its own, its outline from the style's properties and whether it fills and strokes.</param>
    private void Shape(XElement shape, string preset, int row, int column, bool top, bool flipH, bool flipV, double radius,
        Func<Dictionary<XName, string>, bool, bool, Func<double, double, GeometryResult>>? geometry = null)
    {
        if (!PresetShapes.Known(preset))
        {
            LeftOut($"the preset shape {preset}");
            return;
        }
        var properties = Properties((string?)shape.Attribute(Draw + "style-name"));
        var fill = Fill(properties, solid: true);
        var (line, width, dash) = Outline(properties, stroke: true);
        var text = TextOf(shape, properties, CellVerticalAlign.Center, CellAlign.Center, (7.09, 3.54, 7.09, 3.54));
        if (!_read || Place(shape) is not { } place)
            return;
        var adjust = radius > 0 && Math.Min(place.Width, place.Height) > 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["adj"] = "val " + Math.Round(Math.Clamp(radius / Math.Min(place.Width, place.Height), 0, 0.5) * 100_000).ToString(CultureInfo.InvariantCulture),
            }
            : null;
        var drawing = new SheetShape(preset, row, column, place.X, place.Y, place.Width, place.Height)
        {
            Adjust = adjust,
            Fill = fill.Color,
            Gradient = fill.Gradient,
            Line = line,
            LineWidth = width,
            Dash = dash,
            Geometry = geometry?.Invoke(properties, fill.Color is not null, line is not null),
            Text = text,
        };
        Add(Placed(drawing, place, shape, top) with { FlipH = place.FlipH ^ flipH, FlipV = place.FlipV ^ flipV });
    }

    /// <summary>
    /// A polygon, polyline or path: its points or path data in its svg:viewBox (read once), stretched over its box when
    /// it prints; a polyline isn't filled.
    /// </summary>
    private void Freeform(XElement element, int row, int column, bool top)
    {
        var properties = Properties((string?)element.Attribute(Draw + "style-name"));
        var open = element.Name.LocalName == "polyline";
        (SKColor? Color, GradientFill? Gradient) fill = open ? (null, null) : Fill(properties, solid: true);
        var (line, width, dash) = Outline(properties, stroke: true);
        var text = TextOf(element, properties, CellVerticalAlign.Center, CellAlign.Center, (7.09, 3.54, 7.09, 3.54));
        if (!_read || Place(element) is not { } place || fill.Color is null && line is null && text is null)
            return;
        var view = ((string?)element.Attribute(Svg + "viewBox") ?? "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? Math.Clamp(v, -1e9, 1e9) : 0)
            .ToArray();
        // Without a view box the coordinates are 1/100 mm, as Calc writes them.
        var (vx, vy, vw, vh) = view is [var x, var y, var w, var h, ..] && w > 0 && h > 0 ? (x, y, w, h) : (0, 0, place.Width / 72 * 2540, place.Height / 72 * 2540);
        if (!(vw > 0 && vh > 0))
            return;
        var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        if (element.Name.LocalName == "path")
        {
            using var parsed = OdfPathData.Parse((string?)element.Attribute(Svg + "d") ?? "", (px, py) => new SKPoint((float)px, (float)py), 1, 1,
                OdfEnhancedGeometry.MaxTokens, out var commands);
            Commands(commands + 1);
            path.AddPath(parsed);
        }
        else
        {
            var points = OdfPathData.Points((string?)element.Attribute(Draw + "points"), OdfEnhancedGeometry.MaxTokens);
            Commands(points.Count + 1);
            for (var i = 0; i < points.Count; i++)
            {
                if (i == 0)
                    path.MoveTo((float)points[i].X, (float)points[i].Y);
                else
                    path.LineTo((float)points[i].X, (float)points[i].Y);
            }
            if (!open && points.Count > 0)
                path.Close();
        }
        var drawing = new SheetShape("rect", row, column, place.X, place.Y, place.Width, place.Height)
        {
            Fill = fill.Color,
            Gradient = fill.Gradient,
            Line = line,
            LineWidth = width,
            Dash = dash,
            Text = text,
            Geometry = (boxWidth, boxHeight) =>
            {
                var shown = new SKPath(path);
                shown.Transform(SKMatrix.CreateTranslation((float)-vx, (float)-vy).PostConcat(SKMatrix.CreateScale((float)(boxWidth / vw), (float)(boxHeight / vh))));
                return new GeometryResult([new SlidePath(shown, open ? PathFill.None : PathFill.Normal)], null);
            },
        };
        Add(Placed(drawing, place, element, top));
    }

    /// <summary>A line from (svg:x1, svg:y1) to (svg:x2, svg:y2): the line preset across its box, mirrored to run the right way.</summary>
    private void Line(XElement element, int row, int column)
    {
        var properties = Properties((string?)element.Attribute(Draw + "style-name"));
        var (line, width, dash) = Outline(properties, stroke: true);
        if (element.Elements(Text + "p").Any(p => !string.IsNullOrWhiteSpace(p.Value)))
            LeftOut("text on lines");
        double Coordinate(string name) => Math.Clamp(OdsReader.Points((string?)element.Attribute(Svg + name)) ?? 0, -MaxLength, MaxLength);
        var (x1, y1, x2, y2) = (Coordinate("x1"), Coordinate("y1"), Coordinate("x2"), Coordinate("y2"));
        if (element.Attribute(Draw + "transform") is not null)
            LeftOut("turned lines");
        if (!_read || line is null)
            return;
        var (x, y) = (Math.Max(0, Math.Min(x1, x2)), Math.Max(0, Math.Min(y1, y2)));
        Add(new SheetShape("line", row, column, x, y, Math.Abs(x2 - x1), Math.Abs(y2 - y1))
        {
            Line = line,
            LineWidth = width,
            Dash = dash,
            FlipH = (x2 < x1) != (y2 < y1),
        });
    }

    /// <summary>A graphic style's properties with its parents' and the default style's under them.</summary>
    private Dictionary<XName, string> Properties(string? name)
    {
        var chain = new List<XElement>();
        for (var style = name is null ? null : _style(name); style is not null && chain.Count < 20;
             style = (string?)style.Attribute(Style + "parent-style-name") is { } parent ? _style(parent) : null)
            chain.Insert(0, style);
        if (_default is not null)
            chain.Insert(0, _default);
        var properties = new Dictionary<XName, string>();
        foreach (var style in chain)
        {
            foreach (var attribute in style.Element(Style + "graphic-properties")?.Attributes() ?? [])
                properties[attribute.Name] = attribute.Value;
        }
        return properties;
    }

    /// <param name="solid">Fill when the style says nothing (shapes); frames fill only when told.</param>
    private (SKColor? Color, GradientFill? Gradient) Fill(Dictionary<XName, string> properties, bool solid)
    {
        var color = Color(properties.GetValueOrDefault(Draw + "fill-color")) ?? DefaultFill;
        var opacity = properties.GetValueOrDefault(Draw + "opacity");
        switch (properties.GetValueOrDefault(Draw + "fill") ?? (solid ? "solid" : "none"))
        {
            case "none":
                return (null, null);
            case "gradient":
                if (properties.GetValueOrDefault(Draw + "fill-gradient-name") is { } name && _gradient(name) is { } definition)
                {
                    var gradient = Gradient(definition, opacity);
                    return (SlidePaint.Representative(gradient), gradient);
                }
                break;
            case "hatch":
                LeftOut("pattern fills");
                break;
            case "bitmap":
                LeftOut("picture fills");
                return (null, null);
        }
        return (Opacity(color, opacity), null);
    }

    /// <summary>
    /// A draw:gradient as a fill: start and end colours (with their intensities and the fill's opacity), border, centre
    /// and angle (counter-clockwise; at 0 a linear gradient runs from the start colour at the top down).
    /// </summary>
    private static GradientFill Gradient(XElement gradient, string? opacity)
    {
        string? Get(string attribute) => (string?)gradient.Attribute(Draw + attribute);
        double Percent(string attribute, double fallback) => Hwp.Hwpx.Odt.Odf.Percent(Get(attribute)) is { } p ? Math.Clamp(p, 0, 100) / 100 : fallback;
        var alpha = Hwp.Hwpx.Odt.Odf.Percent(opacity) is { } o ? Math.Clamp(o, 0, 100) / 100 : 1;
        SKColor Intense(SKColor c, string attribute)
        {
            var intensity = Percent(attribute, 1);
            return new SKColor((byte)(c.Red * intensity), (byte)(c.Green * intensity), (byte)(c.Blue * intensity), (byte)Math.Round(255 * alpha));
        }
        var start = Intense(Color(Get("start-color")) ?? SKColors.Black, "start-intensity");
        var end = Intense(Color(Get("end-color")) ?? SKColors.White, "end-intensity");
        var border = (float)Percent("border", 0);
        var (cx, cy) = ((float)Percent("cx", 0.5), (float)Percent("cy", 0.5));
        var focus = new SKRect(cx, cy, 1 - cx, 1 - cy);
        var angle = 90 - OdfGraphicStyles.Angle(Get("angle"));
        return Get("style") switch
        {
            // The end colour along the middle, the start colour at both sides.
            "axial" => new GradientFill([new(0, start), new(border / 2, start), new(0.5f, end), new(1 - border / 2, start), new(1, start)],
                GradientShape.Linear, angle, false, SKRect.Empty),
            "radial" => new GradientFill([new(0, end), new(1 - border, start), new(1, start)], GradientShape.Circle, 0, false, focus),
            "ellipsoid" => new GradientFill([new(0, end), new(1 - border, start), new(1, start)], GradientShape.Ellipse, 0, false, focus),
            "square" or "rectangular" => new GradientFill([new(0, end), new(1 - border, start), new(1, start)], GradientShape.Rectangle, 0, false, focus),
            _ => new GradientFill([new(0, start), new(border, start), new(1, end)], GradientShape.Linear, angle, false, SKRect.Empty),
        };
    }

    /// <param name="stroke">Outline when the style says nothing (shapes, lines); frames only when told.</param>
    private (SKColor? Color, double Width, float[]? Dash) Outline(Dictionary<XName, string> properties, bool stroke)
    {
        var kind = properties.GetValueOrDefault(Draw + "stroke") ?? (stroke ? "solid" : "none");
        if (kind == "none")
            return (null, 0, null);
        foreach (var end in new[] { Draw + "marker-start", Draw + "marker-end" })
        {
            if (properties.TryGetValue(end, out var marker) && marker.Length > 0)
                LeftOut("arrowheads");
        }
        var color = Opacity(Color(properties.GetValueOrDefault(Svg + "stroke-color")) ?? DefaultLine, properties.GetValueOrDefault(Svg + "stroke-opacity"));
        // Width 0 is a hairline: one pixel.
        var width = OdsReader.Points(properties.GetValueOrDefault(Svg + "stroke-width")) is { } points and > 0 ? Math.Min(points, 1584) : 0.75;
        return (color, width, kind == "dash" ? [4, 3] : null);
    }

    // ───────────────────────── Text ─────────────────────────

    /// <summary>
    /// The text of a shape, text box or note: its paragraphs (and those of its lists) with their runs; null when it
    /// has no text. The graphic style's font is the base, the paragraph and text styles' fonts go over it.
    /// </summary>
    private ShapeText? TextOf(XElement element, Dictionary<XName, string> properties, CellVerticalAlign anchor, CellAlign align,
        (double Left, double Top, double Right, double Bottom) insets)
    {
        var paragraphs = Paragraphs(element).Take(MaxParagraphs).ToList();
        if (paragraphs.All(p => string.IsNullOrWhiteSpace(p.Value)))
            return null;
        var font = Font(GraphicTextProperties((string?)element.Attribute(Draw + "style-name")), new CellRun(""));
        var areaAlign = properties.GetValueOrDefault(Draw + "textarea-horizontal-align") switch
        {
            "left" => CellAlign.Left,
            "right" => CellAlign.Right,
            "center" => CellAlign.Center,
            _ => align,
        };
        var characters = 0;
        var result = new List<ShapeParagraph>();
        foreach (var paragraph in paragraphs)
        {
            var paragraphStyle = (string?)paragraph.Attribute(Text + "style-name");
            var paragraphFont = Font(StyleProperties("paragraph", paragraphStyle, "text-properties"), new CellRun(""));
            var runs = new List<CellRun>();
            Runs(paragraph, paragraphFont, runs, ref characters, 0);
            var textAlign = Attribute(StyleProperties("paragraph", paragraphStyle, "paragraph-properties"), Fo + "text-align")
                ?? properties.GetValueOrDefault(Fo + "text-align");
            result.Add(new ShapeParagraph(runs, textAlign switch
            {
                "center" => CellAlign.Center,
                "end" or "right" => CellAlign.Right,
                "start" or "left" or "justify" => CellAlign.Left,
                _ => areaAlign,
            }));
        }
        double Padding(string side, double fallback) => properties.TryGetValue(Fo + ("padding-" + side), out var value) || properties.TryGetValue(Fo + "padding", out value)
            ? Math.Clamp(OdsReader.Points(value) ?? fallback, 0, 1000)
            : fallback;
        return new ShapeText(result)
        {
            Font = new CellStyle
            {
                FontName = font.FontName,
                FontSize = font.FontSize,
                Color = font.Color,
                Bold = font.Bold ?? false,
                Italic = font.Italic ?? false,
                Underline = font.Underline ?? false,
                Strike = font.Strike ?? false,
            },
            Anchor = properties.GetValueOrDefault(Draw + "textarea-vertical-align") switch
            {
                "top" => CellVerticalAlign.Top,
                "bottom" => CellVerticalAlign.Bottom,
                "middle" => CellVerticalAlign.Center,
                _ => anchor,
            },
            Insets = (Padding("left", insets.Left), Padding("top", insets.Top), Padding("right", insets.Right), Padding("bottom", insets.Bottom)),
            Wrap = properties.GetValueOrDefault(Fo + "wrap-option") != "no-wrap",
        };
    }

    /// <summary>The text:p and text:h of an element, also inside its text:list items.</summary>
    private static IEnumerable<XElement> Paragraphs(XElement element)
    {
        foreach (var child in element.Elements())
        {
            if (child.Name == Text + "p" || child.Name == Text + "h")
                yield return child;
            else if (child.Name == Text + "list" || child.Name == Text + "list-item" || child.Name == Text + "list-header")
            {
                foreach (var inner in Paragraphs(child))
                    yield return inner;
            }
        }
    }

    /// <summary>The runs of a paragraph or span in <paramref name="font"/>, spans in their text style's font over it.</summary>
    private void Runs(XElement element, CellRun font, List<CellRun> runs, ref int characters, int depth)
    {
        foreach (var node in element.Nodes())
        {
            if (characters >= MaxTextCharacters)
                return;
            switch (node)
            {
                case XText text:
                    var value = string.Join(' ', text.Value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.None)).Replace("  ", " ", StringComparison.Ordinal);
                    Append(runs, font, value, ref characters);
                    break;
                case XElement child when child.Name == Text + "s":
                    Append(runs, font, new string(' ', Math.Clamp(int.TryParse((string?)child.Attribute(Text + "c"), out var count) ? count : 1, 1, 1024)), ref characters);
                    break;
                case XElement child when child.Name == Text + "tab":
                    Append(runs, font, "\t", ref characters);
                    break;
                case XElement child when child.Name == Text + "line-break":
                    Append(runs, font, "\n", ref characters);
                    break;
                case XElement child when depth < MaxGroupDepth && child.Name.Namespace == Text:
                    var span = child.Name == Text + "span" ? Font(StyleProperties("text", (string?)child.Attribute(Text + "style-name"), "text-properties"), font) : font;
                    Runs(child, span, runs, ref characters, depth + 1);
                    break;
            }
        }
    }

    /// <summary>ODF's white space in text: tabs and line feeds are spaces, a run of spaces is one (text:s adds more).</summary>
    private static string Collapse(string text, List<CellRun> before)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var space = before.Count == 0 || before[^1].Text.EndsWith(' ');
        foreach (var ch in text)
        {
            var c = ch is '\t' or '\r' or '\n' ? ' ' : ch;
            if (c == ' ' && space)
                continue;
            sb.Append(c);
            space = c == ' ';
        }
        return sb.ToString();
    }

    private static void Append(List<CellRun> runs, CellRun font, string text, ref int characters)
    {
        if (text.Length == 0)
            return;
        text = text[..Math.Min(text.Length, MaxTextCharacters - characters)];
        characters += text.Length;
        if (runs.Count > 0 && runs[^1] with { Text = "" } == font with { Text = "" })
            runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
        else
            runs.Add(font with { Text = text });
    }

    /// <summary>Text properties as a run's font over <paramref name="under"/> (what they don't set stays).</summary>
    private static CellRun Font(XElement? properties, CellRun under)
    {
        if (properties is null)
            return under;
        string? Get(XName name) => (string?)properties.Attribute(name);
        var weight = Get(Fo + "font-weight");
        var size = Get(Fo + "font-size");
        return under with
        {
            Bold = weight is null ? under.Bold : weight is "bold" or "600" or "700" or "800" or "900",
            Italic = Get(Fo + "font-style") is { } style ? style is "italic" or "oblique" : under.Italic,
            Underline = Get(Style + "text-underline-style") is { } underline ? underline != "none" : under.Underline,
            Strike = Get(Style + "text-line-through-style") is { } strike ? strike != "none" : under.Strike,
            FontSize = size is not null && !size.EndsWith('%') && OdsReader.Points(size) is { } points and > 0 and <= 409 ? points : under.FontSize,
            Color = Get(Fo + "color") is { } color && Color(color) is not null ? color.ToUpperInvariant() : under.Color,
            FontName = (Get(Style + "font-name") ?? Get(Fo + "font-family"))?.Trim('\'', '"') is { Length: > 0 } name ? name : under.FontName,
        };
    }

    /// <summary>The style:text-properties of a graphic style with its parents' and the default graphic style's under them.</summary>
    private XElement? GraphicTextProperties(string? name)
    {
        var merged = new XElement(Style + "text-properties");
        var chain = new List<XElement>();
        for (var style = name is null ? null : _style(name); style is not null && chain.Count < 20;
             style = (string?)style.Attribute(Style + "parent-style-name") is { } parent ? _style(parent) : null)
            chain.Insert(0, style);
        if (_default is not null)
            chain.Insert(0, _default);
        foreach (var style in chain)
        {
            foreach (var attribute in style.Element(Style + "text-properties")?.Attributes() ?? [])
                merged.SetAttributeValue(attribute.Name, attribute.Value);
        }
        return merged.HasAttributes ? merged : null;
    }

    /// <summary>A style's properties element of one kind with its parents' under it (paragraph and text styles).</summary>
    private XElement? StyleProperties(string family, string? name, string kind)
    {
        if (name is null)
            return null;
        var merged = new XElement(Style + kind);
        var chain = new List<XElement>();
        for (var style = _styles(family, name); style is not null && chain.Count < 20;
             style = (string?)style.Attribute(Style + "parent-style-name") is { } parent ? _styles(family, parent) : null)
            chain.Insert(0, style);
        foreach (var style in chain)
        {
            foreach (var attribute in style.Element(Style + kind)?.Attributes() ?? [])
                merged.SetAttributeValue(attribute.Name, attribute.Value);
        }
        return merged.HasAttributes ? merged : null;
    }

    private static string? Attribute(XElement? element, XName name) => (string?)element?.Attribute(name);

    private static SKColor? Color(string? value) =>
        value is { Length: 7 } && value[0] == '#' && SKColor.TryParse(value, out var color) ? color : null;

    /// <summary>A colour with an ODF opacity ("50%") applied.</summary>
    private static SKColor Opacity(SKColor color, string? opacity) =>
        opacity is { Length: > 1 } && opacity.EndsWith('%')
        && double.TryParse(opacity[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) && double.IsFinite(percent)
            ? color.WithAlpha((byte)Math.Round(Math.Clamp(percent, 0, 100) * 2.55))
            : color;

    // ───────────────────────── Placement ─────────────────────────

    /// <summary>The unturned box (offset from the anchor and size, points), the clockwise turn and the mirroring.</summary>
    private readonly record struct Placement(double X, double Y, double Width, double Height, double Rotation, bool FlipH, bool FlipV);

    /// <summary>
    /// Where a shape or frame is: svg:x, svg:y, svg:width and svg:height, then draw:transform (rotate in radians,
    /// counter-clockwise as seen; translate; scale; matrix), applied after the position.
    /// </summary>
    private static Placement? Place(XElement element)
    {
        double Length(string name, double low) => Math.Clamp(OdsReader.Points((string?)element.Attribute(Svg + name)) ?? 0, low, MaxLength);
        var (width, height) = (Length("width", 0), Length("height", 0));
        var matrix = SKMatrix.CreateTranslation((float)Length("x", -MaxLength), (float)Length("y", -MaxLength));
        if ((string?)element.Attribute(Draw + "transform") is { } transform)
            matrix = Transform(transform, matrix);
        var ex = matrix.MapVector(1, 0);
        var ey = matrix.MapVector(0, 1);
        var center = matrix.MapPoint((float)width / 2, (float)height / 2);
        if (!float.IsFinite(ex.Length) || !float.IsFinite(ey.Length) || !float.IsFinite(center.X) || !float.IsFinite(center.Y))
            return null;
        var (w, h) = (Math.Clamp(width * ex.Length, 0, MaxLength), Math.Clamp(height * ey.Length, 0, MaxLength));
        var rotation = ex.Length > 0 ? Math.Atan2(ex.Y, ex.X) * 180 / Math.PI : 0;
        if (rotation < 0)
            rotation += 360;
        if (Math.Abs(rotation) < 1e-4 || Math.Abs(rotation - 360) < 1e-4)
            rotation = 0;
        var mirrored = ex.X * ey.Y - ex.Y * ey.X < 0;
        // Offsets before the anchor's corner aren't printed: the drawing starts at the corner.
        return new Placement(Math.Clamp(center.X - w / 2, 0, MaxLength), Math.Clamp(center.Y - h / 2, 0, MaxLength), w, h, rotation, false, mirrored);
    }

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
            float Length(int i) => i < arguments.Length ? (float)Math.Clamp(OdsReader.Points(arguments[i]) ?? 0, -MaxLength, MaxLength) : 0;
            SKMatrix? operation = name switch
            {
                "rotate" => SKMatrix.CreateRotation((float)-Number(0)),
                "translate" => SKMatrix.CreateTranslation(Length(0), Length(1)),
                "scale" => SKMatrix.CreateScale((float)Number(0), (float)(arguments.Length > 1 ? Number(1) : Number(0))),
                "matrix" => new SKMatrix((float)Number(0), (float)Number(2), Length(4), (float)Number(1), (float)Number(3), Length(5), 0, 0, 1),
                _ => null,
            };
            if (operation is { } op)
                matrix = matrix.PostConcat(op);
        }
        return matrix;
    }

    /// <summary>
    /// The drawing with its turn and mirroring; one that moves and sizes with its cells (a frame or shape of a cell with
    /// table:end-cell-address, unturned) names the cell its far corner is in instead of its size.
    /// </summary>
    private static SheetDrawing Placed(SheetDrawing drawing, Placement place, XElement element, bool top)
    {
        drawing = drawing with { Rotation = place.Rotation, FlipH = place.FlipH, FlipV = place.FlipV };
        if (place.Rotation != 0)
            return drawing with { Size = (place.Width, place.Height) };
        if (top && OdsReader.PrintRanges((string?)element.Attribute(Table + "end-cell-address")) is [var end]
            && end.Top >= drawing.Row && end.Left >= drawing.Column && element.Attribute(Draw + "transform") is null)
        {
            double Offset(string name) => Math.Clamp(OdsReader.Points((string?)element.Attribute(Table + name)) ?? 0, 0, MaxLength);
            return drawing with { To = (end.Top, end.Left, Offset("end-x"), Offset("end-y")) };
        }
        return drawing;
    }
}
