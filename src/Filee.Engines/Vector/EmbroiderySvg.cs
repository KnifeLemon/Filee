// Embroidery designs (DST, JEF) drawn as SVG: the stitched runs of each thread as lines about as thick as a thread,
// at the design's real size, for the vector engine to render to PNG, JPG or PDF.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Vector;

internal static class EmbroiderySvg
{
    /// <summary>Colours for threads whose format names none (DST has no colours at all), in turn.</summary>
    private static readonly string[] Palette =
        ["#1D3557", "#E63946", "#2A9D8F", "#F4A261", "#6A4C93", "#264653", "#E9C46A", "#8D99AE"];

    /// <summary>The colour of thread <paramref name="thread"/> when the design names none.</summary>
    public static string Fallback(int thread) => Palette[thread % Palette.Length];

    /// <summary>
    /// <paramref name="threads"/> (runs of (x, y) in 0.1 mm, Y up) as SVG on white with 2 mm around the design, thread
    /// <c>t</c> in <paramref name="colorOf"/>(t). <paramref name="format"/> names the format in the error for a design
    /// without stitches.
    /// </summary>
    public static string Write(List<List<List<(int X, int Y)>>> threads, Func<int, string> colorOf, string format)
    {
        var points = threads.SelectMany(t => t).SelectMany(r => r).ToList();
        if (points.Count == 0)
            throw new InvalidDataException($"The {format} file has no stitches.");
        const int margin = 20; // 2 mm around the design
        int minX = points.Min(p => p.X) - margin, maxX = points.Max(p => p.X) + margin;
        int minY = points.Min(p => p.Y) - margin, maxY = points.Max(p => p.Y) + margin;
        int width = maxX - minX, height = maxY - minY;
        string Mm(int tenths) => (tenths / 10.0).ToString("0.#", CultureInfo.InvariantCulture);

        var svg = new StringBuilder();
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Mm(width)}mm\" height=\"{Mm(height)}mm\" viewBox=\"0 0 {width} {height}\">");
        svg.Append($"<rect width=\"{width}\" height=\"{height}\" fill=\"#FFFFFF\"/>");
        for (var t = 0; t < threads.Count; t++)
        {
            // A thread about 0.4 mm thick; Y flipped, embroidery formats count it upwards.
            svg.Append($"<g fill=\"none\" stroke=\"{colorOf(t)}\" stroke-width=\"4\" stroke-linecap=\"round\" stroke-linejoin=\"round\">");
            foreach (var run in threads[t])
                svg.Append("<polyline points=\"").AppendJoin(' ', run.Select(p => $"{p.X - minX},{maxY - p.Y}")).Append("\"/>");
            svg.Append("</g>");
        }
        return svg.Append("</svg>").ToString();
    }
}
