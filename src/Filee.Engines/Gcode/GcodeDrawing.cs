// Draws a G-code toolpath (GcodeToolpath), from above or in an isometric view, at its real size in millimetres.
// Extrusion (a printer's) or cuts (a CNC machine's) are drawn wide, coloured by height from dark (low) to light (high)
// so the layers or depths read apart; travel moves (a CNC machine's rapids) are thin lines, blue for a printer and red
// for a CNC machine, as slicer and CAM previews draw them. The drawing is written as SVG, or drawn with SkiaSharp
// straight into a picture or a PDF: a large print's SVG runs to a hundred MB, too slow to read back.

using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using Filee.Core.Presets;
using SkiaSharp;

namespace Filee.Engines.Gcode;

internal static class GcodeDrawing
{
    /// <summary>Colours from low to high: dark violet to orange (part of "magma"), all readable on white.</summary>
    private static readonly Vector3[] Ramp =
    [
        new(0x2C, 0x11, 0x5F), new(0x72, 0x1F, 0x81), new(0xB7, 0x37, 0x79), new(0xF1, 0x60, 0x5D), new(0xFD, 0x95, 0x67),
    ];

    private static readonly SKColor TravelColor = SKColor.Parse("#1C7ED6");
    private static readonly SKColor RapidColor = SKColor.Parse("#E03131");

    /// <summary>Opacity of travel lines, so the part shows through them.</summary>
    private const float TravelOpacity = 0.6f;

    /// <summary>Height bands the colours are split in.</summary>
    private const int Bands = 24;

    /// <summary>
    /// Points closer than this share of the drawing's long side are merged: about a pixel of a 6000-pixel picture, so
    /// the drawing stays small without a visible change.
    /// </summary>
    private const float PixelShare = 1f / 6000;

    private static readonly float Cos30 = MathF.Sqrt(3) / 2;

    /// <summary>One colour of lines: polylines in millimetres from the drawing's top left corner.</summary>
    internal sealed record Stroke(List<List<Vector2>> Polylines, SKColor Color, float Width, float Opacity);

    /// <summary>A drawing: its size in millimetres and its strokes, in the order they are drawn.</summary>
    internal sealed record Scene(float Width, float Height, List<Stroke> Strokes);

    /// <summary>The toolpath seen from above (also for <see cref="GcodeView.Thumbnail"/>) or isometric.</summary>
    internal static Scene Build(GcodeToolpath.Toolpath toolpath, GcodeView view)
    {
        var iso = view == GcodeView.Isometric;
        Vector2 Project(Vector3 p) => iso
            ? new Vector2((p.X - p.Y) * Cos30, -((p.X + p.Y) * 0.5f + p.Z))
            : new Vector2(p.X, -p.Y);

        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
        float low = float.MaxValue, high = float.MinValue;
        foreach (var line in toolpath.Work)
            foreach (var point in line.Points)
            {
                var p = Project(point);
                left = MathF.Min(left, p.X);
                right = MathF.Max(right, p.X);
                top = MathF.Min(top, p.Y);
                bottom = MathF.Max(bottom, p.Y);
                low = MathF.Min(low, point.Z);
                high = MathF.Max(high, point.Z);
            }
        var extent = MathF.Max(MathF.Max(right - left, bottom - top), 1);
        var workWidth = toolpath.IsPrinter ? (toolpath.NozzleDiameter ?? 0.4f) * 1.1f : MathF.Max(extent / 500, 0.1f);
        var margin = MathF.Max(extent * 0.03f, workWidth * 2);
        left -= margin;
        top -= margin;
        float width = right - left + margin, height = bottom - top + margin;
        var tolerance = extent * PixelShare;
        var origin = new Vector2(left, top);

        // Runs of points per kind of move and height band, projected and with points closer than the tolerance merged.
        int BandOf(float z) => high - low < 1e-3f ? 0 : Math.Clamp((int)((z - low) / (high - low) * Bands), 0, Bands - 1);
        var runs = new List<List<Vector2>>[2, Bands];
        foreach (var line in toolpath.Lines)
        {
            var kind = (int)line.Kind;
            List<Vector2>? run = null;
            var band = -1;
            var last = Project(line.Points[0]) - origin;
            for (var i = 1; i < line.Points.Count; i++)
            {
                var b = BandOf(line.Points[i].Z);
                var p = Project(line.Points[i]) - origin;
                if (b == band && Vector2.DistanceSquared(p, last) < tolerance * tolerance && i < line.Points.Count - 1)
                    continue;
                if (b != band)
                {
                    // The next band goes on from where this one stopped.
                    band = b;
                    run = [last];
                    (runs[kind, b] ??= []).Add(run);
                }
                run!.Add(p);
                last = p;
            }
        }

        // Drawn from the lowest band up, work then travel in each: higher layers cover the travel under them, as on the
        // printed part, and the travel of a layer shows over its own moves. A segment that a later one of the same kind
        // covers exactly (to the tolerance) is left out: from above, the segments of a straight wall repeat on every
        // layer of it, and only the top one shows. Found from the last drawn back.
        const int Work = (int)GcodeToolpath.MoveKind.Work, Travel = (int)GcodeToolpath.MoveKind.Travel;
        var hidden = new Dictionary<List<Vector2>, BitArray>(ReferenceEqualityComparer.Instance);
        var drawn = new HashSet<long>();
        var grid = 1 / tolerance;
        long Key(Vector2 a, Vector2 b, int kind)
        {
            long Cell(Vector2 p) => Math.Clamp((long)(p.X * grid), 0, 0x7FFF) << 15 | Math.Clamp((long)(p.Y * grid), 0, 0x7FFF);
            long ca = Cell(a), cb = Cell(b);
            return (long)kind << 60 | Math.Min(ca, cb) << 30 | Math.Max(ca, cb);
        }
        for (var b = Bands - 1; b >= 0; b--)
            foreach (var kind in (int[])[Travel, Work])
            {
                if (runs[kind, b] is not { } kindRuns)
                    continue;
                for (var r = kindRuns.Count - 1; r >= 0; r--)
                {
                    var run = kindRuns[r];
                    BitArray? skip = null;
                    for (var i = run.Count - 2; i >= 0; i--)
                        if (!drawn.Add(Key(run[i], run[i + 1], kind)))
                            (skip ??= new BitArray(run.Count - 1))[i] = true;
                    if (skip is not null)
                        hidden[run] = skip;
                }
            }

        var strokes = new List<Stroke>();
        for (var b = 0; b < Bands; b++)
            foreach (var kind in (int[])[Work, Travel])
            {
                if (runs[kind, b] is not { } kindRuns)
                    continue;
                var polylines = new List<List<Vector2>>();
                foreach (var run in kindRuns)
                {
                    if (!hidden.TryGetValue(run, out var skip))
                    {
                        polylines.Add(run);
                        continue;
                    }
                    List<Vector2>? piece = null;
                    for (var i = 0; i + 1 < run.Count; i++)
                    {
                        if (skip[i])
                        {
                            piece = null;
                            continue;
                        }
                        if (piece is null)
                            polylines.Add(piece = [run[i]]);
                        piece.Add(run[i + 1]);
                    }
                }
                if (polylines.Count > 0)
                    strokes.Add(kind == Work
                        ? new Stroke(polylines, Color(b), workWidth, 1)
                        : new Stroke(polylines, toolpath.IsPrinter ? TravelColor : RapidColor, workWidth * 0.2f, TravelOpacity));
            }
        return new Scene(width, height, strokes);
    }

    /// <summary>The drawing as SVG at its real size, on white.</summary>
    internal static string ToSvg(Scene scene)
    {
        static string N(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var svg = new StringBuilder(1 << 20);
        svg.Append(CultureInfo.InvariantCulture,
            $"""<svg xmlns="http://www.w3.org/2000/svg" width="{N(scene.Width)}mm" height="{N(scene.Height)}mm" viewBox="0 0 {N(scene.Width)} {N(scene.Height)}">""");
        svg.Append(CultureInfo.InvariantCulture, $"""<rect width="{N(scene.Width)}" height="{N(scene.Height)}" fill="#FFFFFF"/>""");
        foreach (var stroke in scene.Strokes)
        {
            svg.Append("<path d=\"");
            foreach (var line in stroke.Polylines)
            {
                svg.Append(CultureInfo.InvariantCulture, $"M{N(line[0].X)} {N(line[0].Y)}");
                for (var i = 1; i < line.Count; i++)
                    svg.Append(CultureInfo.InvariantCulture, $"L{N(line[i].X)} {N(line[i].Y)}");
            }
            var color = $"#{stroke.Color.Red:X2}{stroke.Color.Green:X2}{stroke.Color.Blue:X2}";
            var opacity = stroke.Opacity < 1 ? $" stroke-opacity=\"{N(stroke.Opacity)}\"" : "";
            svg.Append('"');
            svg.Append(CultureInfo.InvariantCulture,
                $""" fill="none" stroke="{color}"{opacity} stroke-width="{N(stroke.Width)}" stroke-linecap="round" stroke-linejoin="round"/>""");
        }
        svg.Append("</svg>");
        return svg.ToString();
    }

    /// <summary>Draws the drawing on a canvas whose unit is the millimetre, white paper first.</summary>
    internal static void Draw(Scene scene, SKCanvas canvas)
    {
        using (var paper = new SKPaint { Color = SKColors.White })
            canvas.DrawRect(0, 0, scene.Width, scene.Height, paper);
        foreach (var stroke in scene.Strokes)
        {
            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeWidth = stroke.Width,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                Color = stroke.Color.WithAlpha((byte)Math.Round(stroke.Opacity * 255)),
            };
            using var path = new SKPath();
            foreach (var line in stroke.Polylines)
            {
                path.MoveTo(line[0].X, line[0].Y);
                for (var i = 1; i < line.Count; i++)
                    path.LineTo(line[i].X, line[i].Y);
            }
            canvas.DrawPath(path, paint);
        }
    }

    /// <summary>The drawing as a one-page vector PDF at its real size.</summary>
    internal static void WritePdf(Scene scene, string output, string title, bool pdfA)
    {
        const float pointsPerMm = 72 / 25.4f;
        var metadata = SKDocumentPdfMetadata.Default;
        metadata.Title = title;
        metadata.Creator = "Filee";
        metadata.Producer = "Filee (Skia PDF)";
        metadata.Creation = DateTime.Now;
        metadata.Modified = DateTime.Now;
        metadata.PdfA = pdfA;
        using (var stream = File.Create(output))
        using (var document = SKDocument.CreatePdf(stream, metadata))
        {
            var canvas = document.BeginPage(scene.Width * pointsPerMm, scene.Height * pointsPerMm);
            canvas.Scale(pointsPerMm);
            Draw(scene, canvas);
            document.EndPage();
            document.Close();
        }
        if (new FileInfo(output).Length == 0)
            throw new InvalidOperationException("Skia could not write the PDF.");
    }

    /// <summary>The colour of a height band, from the ramp.</summary>
    private static SKColor Color(int band)
    {
        var t = (float)band / (Bands - 1) * (Ramp.Length - 1);
        var i = Math.Min((int)t, Ramp.Length - 2);
        var c = Vector3.Lerp(Ramp[i], Ramp[i + 1], t - i);
        return new SKColor((byte)c.X, (byte)c.Y, (byte)c.Z);
    }
}
