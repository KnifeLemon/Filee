// What the XPS reader makes of a fixed page before it is drawn: canvases, paths and glyph runs with their transforms,
// clips, opacity and brushes, in the page's own units (1/96 inch). XpsPainter draws them with Skia, XpsPageWriter
// hands them to the page model (PageContent) for PDF and pictures.

using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Xps;

/// <summary>A drawn element: Canvas, Path or Glyphs.</summary>
internal abstract class XpsNode
{
    /// <summary>RenderTransform: the element's coordinates in its parent's.</summary>
    public SKMatrix Transform { get; set; } = SKMatrix.Identity;

    /// <summary>Clip in the element's coordinates (after <see cref="Transform"/>); null = none.</summary>
    public XpsGeometry? Clip { get; set; }

    public float Opacity { get; set; } = 1;

    /// <summary>A brush whose alpha masks the element; null = none.</summary>
    public XpsBrush? OpacityMask { get; set; }

    /// <summary>RenderOptions.EdgeMode="Aliased": edges drawn without anti-aliasing.</summary>
    public bool Aliased { get; set; }

    /// <summary>FixedPage.NavigateUri: a link (URL, or "#Name" in the document).</summary>
    public string? NavigateUri { get; set; }

    /// <summary>The element's Name, a link target when the document lists it.</summary>
    public string? Name { get; set; }

    /// <summary>True when drawing the element needs a layer (group opacity or a mask).</summary>
    public bool NeedsLayer => Opacity < 1 || OpacityMask is not null;
}

internal sealed class XpsCanvas : XpsNode
{
    public List<XpsNode> Children { get; } = [];
}

/// <summary>Stroke settings of a path.</summary>
internal sealed record XpsStrokeStyle(
    float Thickness = 1,
    SKStrokeCap StartCap = SKStrokeCap.Butt,
    SKStrokeCap EndCap = SKStrokeCap.Butt,
    SKStrokeCap DashCap = SKStrokeCap.Square,
    SKStrokeJoin Join = SKStrokeJoin.Miter,
    float MiterLimit = 10,
    float[]? Dashes = null,
    float DashOffset = 0);

internal sealed class XpsPath : XpsNode
{
    public required XpsGeometry Data { get; init; }

    public XpsBrush? Fill { get; init; }

    public XpsBrush? Stroke { get; init; }

    public XpsStrokeStyle StrokeStyle { get; init; } = new();
}

/// <summary>A glyph run with its glyphs placed (baseline origins in the element's coordinates).</summary>
internal sealed class XpsGlyphs : XpsNode
{
    public required PageFont Font { get; init; }

    public required float Size { get; init; }

    public required ushort[] Glyphs { get; init; }

    public required SKPoint[] Positions { get; init; }

    /// <summary>UnicodeString, "" when the run has none.</summary>
    public required string Text { get; init; }

    /// <summary>Per glyph, the index in <see cref="Text"/> where its cluster starts.</summary>
    public required int[] Clusters { get; init; }

    public XpsBrush? Fill { get; init; }

    public bool Bold { get; init; }

    public bool Italic { get; init; }

    /// <summary>A Skia font of the run as PageContent draws it (unhinted, subpixel positions).</summary>
    public SKFont SkiaFont(SKTypeface typeface) => new(typeface, Size)
    {
        Embolden = Bold,
        SkewX = Italic ? GlyphRun.SyntheticSlant : 0,
        Subpixel = true,
        Edging = SKFontEdging.Antialias,
        Hinting = SKFontHinting.None,
        LinearMetrics = true,
    };
}

/// <summary>How a brush's tile repeats (TileMode).</summary>
internal enum XpsTile
{
    None,
    Tile,
    FlipX,
    FlipY,
    FlipXY,
}

/// <summary>A brush: what fills a path or glyphs, in the element's coordinates through <see cref="Transform"/>.</summary>
internal abstract class XpsBrush
{
    public float Opacity { get; init; } = 1;

    public SKMatrix Transform { get; init; } = SKMatrix.Identity;
}

internal sealed class XpsSolidBrush(SKColor color) : XpsBrush
{
    public SKColor Color { get; } = color;

    /// <summary>The colour with the brush's opacity and <paramref name="opacity"/> in its alpha.</summary>
    public SKColor WithOpacity(float opacity) =>
        Color.WithAlpha((byte)Math.Clamp(MathF.Round(Color.Alpha * Opacity * opacity), 0, 255));
}

/// <summary>A linear or radial gradient; stops are sorted and lie within 0…1.</summary>
internal sealed class XpsGradientBrush : XpsBrush
{
    public required SKColor[] Colors { get; init; }

    public required float[] Offsets { get; init; }

    public SKShaderTileMode Spread { get; init; } = SKShaderTileMode.Clamp;

    /// <summary>Linear: StartPoint and EndPoint. Radial: GradientOrigin and Center.</summary>
    public SKPoint Start { get; init; }

    public SKPoint End { get; init; }

    /// <summary>Radial gradients only: RadiusX and RadiusY; null for a linear gradient.</summary>
    public SKSize? Radius { get; init; }
}

/// <summary>A tile (an image or a visual) placed by its Viewbox into its Viewport.</summary>
internal abstract class XpsTileBrush : XpsBrush
{
    /// <summary>The part of the content used, in its own units.</summary>
    public SKRect Viewbox { get; init; }

    /// <summary>Where the viewbox goes, in the element's coordinates (before <see cref="XpsBrush.Transform"/>).</summary>
    public SKRect Viewport { get; init; }

    public XpsTile Tile { get; init; }

    /// <summary>Viewbox → viewport.</summary>
    public SKMatrix ViewboxToViewport =>
        SKMatrix.CreateTranslation(Viewport.Left, Viewport.Top)
            .PreConcat(SKMatrix.CreateScale(Viewport.Width / Viewbox.Width, Viewport.Height / Viewbox.Height))
            .PreConcat(SKMatrix.CreateTranslation(-Viewbox.Left, -Viewbox.Top));

    /// <summary>False for an empty viewbox or viewport: the brush draws nothing.</summary>
    public bool IsDrawable => Viewbox.Width > 0 && Viewbox.Height > 0 && Viewport.Width > 0 && Viewport.Height > 0;

    public (SKShaderTileMode X, SKShaderTileMode Y) TileModes => Tile switch
    {
        XpsTile.Tile => (SKShaderTileMode.Repeat, SKShaderTileMode.Repeat),
        XpsTile.FlipX => (SKShaderTileMode.Mirror, SKShaderTileMode.Repeat),
        XpsTile.FlipY => (SKShaderTileMode.Repeat, SKShaderTileMode.Mirror),
        XpsTile.FlipXY => (SKShaderTileMode.Mirror, SKShaderTileMode.Mirror),
        _ => (SKShaderTileMode.Decal, SKShaderTileMode.Decal),
    };
}

/// <summary>A decoded picture and its size in 1/96 inch (from its resolution; 96 dpi when it has none).</summary>
internal sealed record XpsImage(SKImage Image, float Width, float Height);

internal sealed class XpsImageBrush : XpsTileBrush
{
    public required XpsImage Image { get; init; }

    /// <summary>Picture pixels → its 1/96-inch units (the viewbox's).</summary>
    public SKMatrix PixelsToUnits => SKMatrix.CreateScale(Image.Width / Image.Image.Width, Image.Height / Image.Image.Height);
}

internal sealed class XpsVisualBrush : XpsTileBrush
{
    public required XpsNode Visual { get; init; }
}
