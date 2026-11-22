// What the ODP writer keeps of a shape's effects, 3D and charts:
//
// - Shadows as draw:shadow with its offset, colour and opacity (OpenDocument 1.3 Part 3, 20.158–20.162) and
//   LibreOffice's loext:shadow-blur; glow as loext:glow-radius / -color / -transparency; soft edges as
//   loext:softedge-radius (LibreOffice 7 draws all three, OdfDrawReader reads them back).
// - Reflections, which ODF has no attribute for, baked: a copy of the shape mirrored under its box (after the gap),
//   faded by a transparency gradient (draw:opacity) from the start to the end opacity over the end position's share.
// - 3D as extrusion attributes of draw:enhanced-geometry (19.148–19.169): draw:extrusion, the depth, the turn about x
//   and y (draw:extrusion-rotation-angle; the turn about z goes into the shape's draw:transform), dr3d:projection,
//   draw:extrusion-skew (none unless the model has an oblique view) and the extrusion colour as
//   draw:secondary-fill-color with draw:extrusion-color.
// - Charts the chart model can write as ODF (OdfChartWriter) as chart objects: a folder "Object N" with the chart's
//   content.xml and styles.xml, and its picture as the replacement "ObjectReplacements/Object N", in a frame with
//   draw:object and draw:image (as LibreOffice writes them). Other charts stay their picture.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Engines.Charts;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Odf.Draw;

internal sealed partial class OdpWriter
{
    /// <summary>The chart objects of the package: their parts, and their folder by chart and size.</summary>
    private readonly List<OdfPart> _objects = [];

    private readonly Dictionary<(Chart Chart, float Width, float Height), string?> _charts = [];

    /// <summary>Characters of a chart object's content.xml written at most, together.</summary>
    private const long MaxChartCharacters = 64L * 1024 * 1024;

    private long _chartCharacters;

    /// <summary>The graphic properties of a shape's shadow, glow and soft edges; empty for none.</summary>
    private static string EffectAttributes(SlideEffects? effects)
    {
        if (effects is null || effects.IsNone)
            return "";
        var sb = new StringBuilder();
        if (effects.Shadow is { } shadow)
        {
            sb.Append($" draw:shadow=\"visible\" draw:shadow-offset-x=\"{Pt(shadow.OffsetX)}\" draw:shadow-offset-y=\"{Pt(shadow.OffsetY)}\"")
              .Append($" draw:shadow-color=\"{Hex(shadow.Color)}\" draw:shadow-opacity=\"{Percent(shadow.Color.Alpha / 255.0)}\"");
            if (shadow.Blur > 0)
                sb.Append($" loext:shadow-blur=\"{Pt(shadow.Blur)}\"");
        }
        if (effects.Glow is { } glow)
            sb.Append($" loext:glow-radius=\"{Pt(glow.Radius)}\" loext:glow-color=\"{Hex(glow.Color)}\" loext:glow-transparency=\"{Percent(1 - glow.Color.Alpha / 255.0)}\"");
        if (effects.SoftEdge > 0)
            sb.Append($" loext:softedge-radius=\"{Pt(effects.SoftEdge)}\"");
        return sb.ToString();
    }

    /// <summary>The extrusion attributes of a shape's draw:enhanced-geometry; empty for a flat shape.</summary>
    private static string ExtrusionAttributes(SlideThreeD? three)
    {
        if (three is null || three.IsFlat)
            return "";
        static string Degrees(float value) => (float.IsFinite(value) ? Math.Round(value % 360, 2) : 0).ToString("0.##", CultureInfo.InvariantCulture);
        var depth = float.IsFinite(three.Depth) ? Math.Clamp(three.Depth, 0, 10_000) : 0;
        // ODF turns with y up: a positive turn about x brings the top forward, the model's tilts it away.
        var sb = new StringBuilder($" draw:extrusion=\"true\" draw:extrusion-depth=\"{Pt(depth)} 0\"")
            .Append($" draw:extrusion-rotation-angle=\"{Degrees(-three.RotationX)} {Degrees(three.RotationY)}\"")
            .Append($" dr3d:projection=\"{(three.Perspective ? "perspective" : "parallel")}\"");
        if (!three.Perspective)
            sb.Append($" draw:extrusion-skew=\"{(Math.Clamp(three.SkewAmount, 0, 10) * 100).ToString("0.##", CultureInfo.InvariantCulture)} {Degrees(three.SkewAngle)}\"");
        if (three.Color is not null)
            sb.Append(" draw:extrusion-color=\"true\"");
        return sb.ToString();
    }

    /// <summary>The extrusion colour in the shape's graphic properties; empty without one.</summary>
    private static string ExtrusionColor(SlideThreeD? three) =>
        three is { IsFlat: false, Color: { } color } ? $" draw:secondary-fill-color=\"{Hex(color)}\"" : "";

    /// <summary>
    /// The baked reflection of a shape: its outlines mirrored under its box, faded out downwards by a transparency
    /// gradient. Its text is not reflected (as the renderer draws it).
    /// </summary>
    private void Reflection(StringBuilder sb, SlideShape shape, SKMatrix matrix)
    {
        if (shape.Effects?.Reflection is not { } reflection || shape.Fill is null && shape.Line is null)
            return;
        var h = shape.Bounds.Height;
        var gap = float.IsFinite(reflection.Distance) ? Math.Clamp(reflection.Distance, -h, 10 * Math.Max(h, 1)) : 0;
        var mirror = matrix.PreConcat(SKMatrix.CreateTranslation(0, 2 * h + gap)).PreConcat(SKMatrix.CreateScale(1, -1));
        var copy = new SlideShape { Bounds = shape.Bounds, Paths = shape.Paths, Fill = shape.Fill, Line = shape.Line };
        // ODF's linear transparency: at 180° the start is at the bottom, the border keeps it over that share; the
        // copy's top, under the shape, is the reflection's start.
        static double Opacity(float a) => Math.Clamp(float.IsFinite(a) ? a : 0, 0, 1);
        var end = Math.Clamp(float.IsFinite(reflection.EndPosition) ? reflection.EndPosition : 1, 0, 1);
        var fade = _named.Add("opacity", "Transparency",
            $" draw:style=\"linear\" draw:start=\"{Percent(Opacity(reflection.EndAlpha))}\" draw:end=\"{Percent(Opacity(reflection.StartAlpha))}\" draw:angle=\"180deg\" draw:border=\"{Percent(1 - end)}\"");
        Shape(sb, copy, mirror, fade);
    }

    /// <summary>The fill attributes with a transparency gradient instead of a uniform opacity.</summary>
    private static string Faded(string fill, string? fade) =>
        fade is null ? fill : Regex.Replace(fill, " draw:opacity=\"[^\"]*\"", "") + $" draw:opacity-name=\"{fade}\"";

    /// <summary>
    /// A chart as an ODF chart object in a frame placed by <paramref name="place"/>, with its picture as the
    /// replacement; false when the chart can't be written as one (the caller writes the picture).
    /// </summary>
    private bool ChartObject(StringBuilder sb, SlideShape shape, Chart chart, Place place, string effects)
    {
        var (w, h) = (shape.Bounds.Width, shape.Bounds.Height);
        if (!_charts.TryGetValue((chart, w, h), out var folder))
        {
            folder = null;
            if (OdfChartWriter.Content(chart, w, h) is { } content && ChartPicture.Png(chart, w, h) is { } png)
            {
                _chartCharacters += content.Length;
                if (_chartCharacters > MaxChartCharacters)
                    throw Documents.DocumentLimits.TooLarge($"The presentation's charts would need more than {Documents.DocumentLimits.Count(MaxChartCharacters)} characters of XML.");
                folder = $"Object {(_charts.Count(c => c.Value is not null) + 1).ToString(CultureInfo.InvariantCulture)}";
                _objects.Add(new OdfPart(folder + "/", OdfChartWriter.MediaType));
                _objects.Add(new OdfPart(folder + "/content.xml", "text/xml", Text: content));
                _objects.Add(new OdfPart(folder + "/styles.xml", "text/xml", Text: OdfChartWriter.Styles()));
                _objects.Add(new OdfPart("ObjectReplacements/" + folder, "image/png", Bytes: png));
            }
            _charts[(chart, w, h)] = folder;
        }
        if (folder is null)
            return false;
        var style = Graphic(" draw:fill=\"none\"" + Stroke(shape.Line) + effects + " draw:auto-grow-height=\"false\" draw:auto-grow-width=\"false\"");
        var href = OdfXml.Escape(folder);
        sb.Append($"<draw:frame draw:style-name=\"{style}\"{_layer} {Placement(place)}>")
          .Append($"<draw:object xlink:href=\"./{href}\" xlink:type=\"simple\" xlink:show=\"embed\" xlink:actuate=\"onLoad\"/>")
          .Append($"<draw:image xlink:href=\"./ObjectReplacements/{href}\" xlink:type=\"simple\" xlink:show=\"embed\" xlink:actuate=\"onLoad\"/>")
          .Append("</draw:frame>");
        return true;
    }
}
