// DOCX graphic objects: DrawingML pictures, text boxes (wps), groups and drawing canvases, plus legacy VML
// (w:pict / w:object) used by older documents and as the fallback of mc:AlternateContent.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Documents;
using ImageMagick;

namespace Filee.Engines.Hwp.Hwpx.Docx;

internal sealed partial class DocxReader
{
    private static readonly XNamespace Wpc = "http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas";
    private static readonly XNamespace W10 = "urn:schemas-microsoft-com:office:word";
    private static readonly XNamespace O = "urn:schemas-microsoft-com:office:office";
    private static readonly XNamespace W14 = "http://schemas.microsoft.com/office/word/2010/wordml";

    /// <summary>Default DrawingML text box insets: 0.1" left/right, 0.05" top/bottom.</summary>
    private static readonly HInsets DefaultTextBoxPadding = new(720, 720, 360, 360);

    private readonly Dictionary<string, string> _themeColors = [];

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
            foreach (var inline in GraphicObjects(data?.Elements() ?? [], width, height, anchor, (0, 0, 1, 1)))
            {
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
        return new HShape(kind.Value)
        {
            Width = width,
            Height = height,
            Anchor = anchor,
            Fill = kind == HShapeKind.Line ? null : fill,
            Gradient = kind == HShapeKind.Line ? null : gradient,
            Line = Line(spPr?.Element(A + "ln"), style),
            CornerRatio = geometry is "roundRect" or "round1Rect" or "round2SameRect" or "flowChartAlternateProcess" ? 20 : 0,
            FlipHorizontal = Attr(xfrm, "flipH") is "1" or "true",
            FlipVertical = Attr(xfrm, "flipV") is "1" or "true",
            Transform = Transform(xfrm),
        };
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
            AllowOverlap: Attr(anchor, "allowOverlap") is not ("0" or "false"));
    }

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
            var width = HwpxUnits.ParseLength(style.GetValueOrDefault("width")) ?? 0;
            var height = HwpxUnits.ParseLength(style.GetValueOrDefault("height")) ?? 0;
            var anchor = style.GetValueOrDefault("position") == "absolute" ? VmlAnchor(shape, style) : null;
            var transform = VmlTransform(style);
            int? zOrder = anchor is not null && int.TryParse(style.GetValueOrDefault("z-index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var z) ? z : null;

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
                };
            }
            else if (shape.Element(V + "textbox")?.Element(W + "txbxContent") is { } content)
            {
                var box = new TextBoxBuilder(width, height, anchor)
                {
                    Fill = VmlFill(shape),
                    Line = VmlLine(shape),
                }.Build();
                (box.Transform, box.ZOrder) = (transform, zOrder);
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
                    (box.Transform, box.ZOrder) = (transform, zOrder);
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
            else if (shape.Name.LocalName is "rect" or "roundrect" or "oval" or "line")
            {
                var kind = shape.Name.LocalName switch
                {
                    "oval" => HShapeKind.Ellipse,
                    "line" => HShapeKind.Line,
                    _ => HShapeKind.Rectangle,
                };
                if (kind == HShapeKind.Line)
                {
                    // v:line has from="x,y" to="x,y" instead of a size.
                    var from = VmlPoint((string?)shape.Attribute("from"));
                    var to = VmlPoint((string?)shape.Attribute("to"));
                    width = Math.Abs(to.X - from.X);
                    height = Math.Abs(to.Y - from.Y);
                    var lineAnchor = anchor ?? VmlAnchor(shape, style);
                    anchor = lineAnchor with
                    {
                        HorizontalOffset = lineAnchor.HorizontalOffset + Math.Min(from.X, to.X),
                        VerticalOffset = lineAnchor.VerticalOffset + Math.Min(from.Y, to.Y),
                    };
                }
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
            AllowOverlap: true);
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
