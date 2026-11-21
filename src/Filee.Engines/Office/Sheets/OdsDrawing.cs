// The pictures and shapes of an OpenDocument spreadsheet's tables (ODF 1.3 Part 3, 10.3 and 10.4), read for the sheet
// printer: frames holding a picture (PNG, JPEG, GIF, BMP, WebP; in the package or inline), rectangles, ellipses and
// circles, lines, custom shapes of a type the slide renderer has a preset for (draw:enhanced-geometry draw:type), and
// groups of them. Each is anchored to the cell it sits in (its svg:x / svg:y from the cell's top-left corner, its far
// corner in table:end-cell-address when it moves and sizes with the cells) or to the sheet (table:shapes), turned and
// mirrored by draw:transform and the geometry's mirror flags, filled and outlined in one colour each from its graphic
// style (with its parents and the default graphic style). Charts in frames are read by OdsReader (OdfChartReader).
//
// What the printer doesn't draw is named instead, as XlsxDrawing names it: text boxes, text in shapes, freeform shapes
// (polygons, paths, custom shapes of no known type), arcs, connectors, form controls, embedded objects, SVG / EMF /
// WMF / TIFF and linked pictures, cropped pictures, gradient, hatch and picture fills, arrowheads. SheetCapabilities
// asks for that name (with nothing unpacked), so such a workbook can go to LibreOffice where it is installed.
// Objects set to show on screen only (draw:display) are left out, as Calc leaves them off the printout.

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

    /// <summary>Calc's default graphic style: shapes are filled light blue and outlined dark blue when nothing else says.</summary>
    private static readonly SKColor DefaultFill = new(0x72, 0x9F, 0xCF);
    private static readonly SKColor DefaultLine = new(0x34, 0x65, 0xA4);

    private readonly DocumentPackage? _zip;
    private readonly bool _read;
    private readonly Func<string, XElement?> _style;
    private readonly XElement? _default;

    /// <summary>What is drawn, in drawing order; empty when only the check was asked for.</summary>
    public List<SheetDrawing> Drawings { get; } = [];

    /// <summary>The first thing the printer doesn't draw, or null.</summary>
    public string? FirstLeftOut { get; private set; }

    /// <param name="zip">The package the pictures are in.</param>
    /// <param name="read">Read the drawings; without it no picture is unpacked and only <see cref="FirstLeftOut"/> is found.</param>
    /// <param name="style">The graphic style (style:family "graphic") of a name, or null.</param>
    /// <param name="defaultStyle">The default graphic style (style:default-style), or null.</param>
    public OdsDrawing(DocumentPackage zip, bool read, Func<string, XElement?> style, XElement? defaultStyle)
    {
        _zip = zip;
        _read = read;
        _style = style;
        _default = defaultStyle;
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
                if (geometry is null || type is null or "non-primitive" || OdfEnhancedGeometry.Preset(type) is not { } preset)
                {
                    LeftOut("freeform shapes");
                    break;
                }
                Shape(element, preset, row, column, top, (string?)geometry.Attribute(Draw + "mirror-horizontal") == "true",
                    (string?)geometry.Attribute(Draw + "mirror-vertical") == "true", 0);
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
                LeftOut("freeform shapes");
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
        if (frame.Element(Draw + "text-box") is not null)
        {
            LeftOut("text boxes");
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
        if (properties.TryGetValue(Fo + "clip", out var clip) && clip.Contains("rect", StringComparison.Ordinal)
            && clip.Replace("rect", "", StringComparison.Ordinal).Trim('(', ')', ' ').Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Any(edge => OdsReader.Points(edge) is > 0.01))
            LeftOut("cropped pictures");
        if (!_read || Place(frame) is not { } place || place.Width <= 0 || place.Height <= 0 || Data(chosen) is not { } data)
            return;
        Add(Placed(new SheetPicture(data, row, column, place.X, place.Y, place.Width, place.Height), place, frame, top));
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

    private void Shape(XElement shape, string preset, int row, int column, bool top, bool flipH, bool flipV, double radius)
    {
        if (!PresetShapes.Known(preset))
        {
            LeftOut($"the preset shape {preset}");
            return;
        }
        // The shape is drawn; its text is not (yet).
        if (shape.Elements(Text + "p").Concat(shape.Elements(Text + "h")).Any(p => !string.IsNullOrWhiteSpace(p.Value)))
            LeftOut("text in shapes");
        var properties = Properties((string?)shape.Attribute(Draw + "style-name"));
        var fill = Fill(properties);
        var (line, width, dash) = Outline(properties);
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
            Fill = fill,
            Line = line,
            LineWidth = width,
            Dash = dash,
        };
        Add(Placed(drawing, place, shape, top) with { FlipH = place.FlipH ^ flipH, FlipV = place.FlipV ^ flipV });
    }

    /// <summary>A line from (svg:x1, svg:y1) to (svg:x2, svg:y2): the line preset across its box, mirrored to run the right way.</summary>
    private void Line(XElement element, int row, int column)
    {
        var properties = Properties((string?)element.Attribute(Draw + "style-name"));
        var (line, width, dash) = Outline(properties);
        if (element.Elements(Text + "p").Any(p => !string.IsNullOrWhiteSpace(p.Value)))
            LeftOut("text in shapes");
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

    private SKColor? Fill(Dictionary<XName, string> properties)
    {
        var color = Color(properties.GetValueOrDefault(Draw + "fill-color")) ?? DefaultFill;
        switch (properties.GetValueOrDefault(Draw + "fill") ?? "solid")
        {
            case "none":
                return null;
            case "gradient":
                LeftOut("gradient fills");
                break;
            case "hatch":
                LeftOut("pattern fills");
                break;
            case "bitmap":
                LeftOut("picture fills");
                return null;
        }
        return Opacity(color, properties.GetValueOrDefault(Draw + "opacity"));
    }

    private (SKColor? Color, double Width, float[]? Dash) Outline(Dictionary<XName, string> properties)
    {
        var stroke = properties.GetValueOrDefault(Draw + "stroke") ?? "solid";
        if (stroke == "none")
            return (null, 0, null);
        foreach (var end in new[] { Draw + "marker-start", Draw + "marker-end" })
        {
            if (properties.TryGetValue(end, out var marker) && marker.Length > 0)
                LeftOut("arrowheads");
        }
        var color = Opacity(Color(properties.GetValueOrDefault(Svg + "stroke-color")) ?? DefaultLine, properties.GetValueOrDefault(Svg + "stroke-opacity"));
        // Width 0 is a hairline: one pixel.
        var width = OdsReader.Points(properties.GetValueOrDefault(Svg + "stroke-width")) is { } points and > 0 ? Math.Min(points, 1584) : 0.75;
        return (color, width, stroke == "dash" ? [4, 3] : null);
    }

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
