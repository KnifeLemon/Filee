// Interactive whiteboard files (.iwb) written in code for IwbTests and IwbFuzzTests: content.xml with the iwb root and
// its svg, and the package's other files (pictures, SVG pictures, media).

using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace Filee.Engines.Tests;

internal static class IwbBuilder
{
    public const string Namespaces =
        "xmlns=\"http://www.becta.org.uk/iwb\" xmlns:iwb=\"http://www.becta.org.uk/iwb\" xmlns:svg=\"http://www.w3.org/2000/svg\" " +
        "xmlns:xlink=\"http://www.w3.org/1999/xlink\"";

    /// <summary>content.xml: the iwb root around <paramref name="svg"/> and the iwb:element entries after it.</summary>
    public static string Content(string svg, string elements = "") =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><iwb {Namespaces} version=\"1.0\">{svg}{elements}</iwb>";

    /// <summary>A package of content.xml and the other files (text as UTF-8).</summary>
    public static byte[] Package(string content, IReadOnlyDictionary<string, byte[]>? files = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "content.xml", Encoding.UTF8.GetBytes(content));
            foreach (var (name, data) in files ?? new Dictionary<string, byte[]>())
                Add(zip, name, data);
        }
        return stream.ToArray();
    }

    private static void Add(ZipArchive zip, string name, byte[] data)
    {
        using var entry = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        entry.Write(data);
    }

    public static string Save(string path, byte[] package)
    {
        File.WriteAllBytes(path, package);
        return path;
    }

    /// <summary>A PNG of <paramref name="width"/> × <paramref name="height"/>: left half <paramref name="left"/>, right half <paramref name="right"/>.</summary>
    public static byte[] Png(int width, int height, SKColor left, SKColor right)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint { Color = left };
            canvas.DrawRect(0, 0, width / 2f, height, paint);
            paint.Color = right;
            canvas.DrawRect(width / 2f, 0, width - width / 2f, height, paint);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Two pages of 800 × 600 px (600 × 450 pt): a banner drawn on every page, then shapes, a polyline, text, a
    /// textArea, a picture, a hidden rectangle, a video and a sound; then a turned group with a gradient and text, an SVG
    /// picture (with a DOCTYPE and a picture of its own) and a use of the first page's box.
    /// </summary>
    public static byte[] Lesson()
    {
        const string svg =
            "<svg:svg width=\"800\" height=\"600\" viewBox=\"0 0 800 600\" viewport-fill=\"#FFFFE0\">" +
            "<svg:title>Fractions</svg:title>" +
            "<svg:defs><svg:linearGradient id=\"sky\"><svg:stop offset=\"0\" stop-color=\"#0000FF\"/><svg:stop offset=\"1\" stop-color=\"#00FF00\"/></svg:linearGradient></svg:defs>" +
            "<svg:rect id=\"banner\" x=\"0\" y=\"560\" width=\"800\" height=\"40\" fill=\"#336699\"/>" +
            "<svg:pageset>" +
            "<svg:page id=\"p1\">" +
            "<svg:rect id=\"box\" x=\"100\" y=\"100\" width=\"200\" height=\"100\" fill=\"#FF0000\" stroke=\"#000000\" stroke-width=\"4\"/>" +
            "<svg:circle cx=\"500\" cy=\"150\" r=\"50\" style=\"fill:rgb(0,128,0)\"/>" +
            "<svg:polyline points=\"100,300 200,350 300,300\" fill=\"none\" stroke=\"blue\" stroke-width=\"6\" stroke-linecap=\"round\"/>" +
            "<svg:text x=\"100\" y=\"450\" font-family=\"Arial\" font-size=\"40\" fill=\"#000080\">Half <svg:tspan font-weight=\"bold\">and</svg:tspan> quarter</svg:text>" +
            "<svg:textarea x=\"450\" y=\"300\" width=\"300\" height=\"200\" font-size=\"24\" text-align=\"center\">First line<svg:tbreak/>Second line</svg:textarea>" +
            "<svg:image x=\"600\" y=\"400\" width=\"80\" height=\"40\" xlink:href=\"images/pic.png\"/>" +
            "<svg:rect id=\"secret\" x=\"0\" y=\"0\" width=\"50\" height=\"50\" fill=\"#FF00FF\"/>" +
            "<svg:video x=\"0\" y=\"0\" width=\"10\" height=\"10\" xlink:href=\"videos/clip.mp4\"/>" +
            "<svg:audio xlink:href=\"sounds/bell.mp3\"/>" +
            "</svg:page>" +
            "<svg:page id=\"p2\" viewport-fill=\"#E0E0FF\">" +
            "<svg:g transform=\"translate(400 300) rotate(90)\">" +
            "<svg:rect x=\"-50\" y=\"-25\" width=\"100\" height=\"50\" fill=\"url(#sky)\"/>" +
            "<svg:text x=\"0\" y=\"0\" text-anchor=\"middle\" font-size=\"20\">Turned</svg:text>" +
            "</svg:g>" +
            "<svg:image x=\"50\" y=\"50\" width=\"200\" height=\"200\" xlink:href=\"images/star.svg\"/>" +
            "<svg:use xlink:href=\"#box\" transform=\"translate(0 200)\"/>" +
            "</svg:page>" +
            "</svg:pageset>" +
            "</svg:svg>";
        const string star =
            "<?xml version=\"1.0\"?><!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">" +
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"100\" height=\"100\" viewBox=\"0 0 100 100\">" +
            "<polygon points=\"50,0 100,100 0,100\" fill=\"#FFA500\"/><image x=\"40\" y=\"60\" width=\"20\" height=\"20\" xlink:href=\"dot.png\"/></svg>";
        return Package(Content(svg, "<iwb:element ref=\"secret\" invisible=\"true\"/><iwb:element ref=\"banner\" background=\"true\" locked=\"true\"/>"),
            new Dictionary<string, byte[]>
            {
                ["images/pic.png"] = Png(40, 20, SKColors.Yellow, SKColors.Cyan),
                ["images/star.svg"] = Encoding.UTF8.GetBytes(star),
                ["images/dot.png"] = Png(4, 4, SKColors.Black, SKColors.Black),
                ["videos/clip.mp4"] = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p'],
                ["sounds/bell.mp3"] = [0x49, 0x44, 0x33],
            });
    }

    /// <summary>
    /// A SMART Notebook package: each page an SVG file (page0.svg, ...) with <paramref name="pages"/>' bodies, listed in
    /// imsmanifest.xml in <paramref name="manifestOrder"/> (no manifest when null), and a picture.
    /// </summary>
    public static byte[] Notebook(string[] pages, int[]? manifestOrder)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < pages.Length; i++)
            {
                Add(zip, $"page{i}.svg", Encoding.UTF8.GetBytes(
                    "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
                    $"xmlns:notebook=\"http://www.smarttech.com/2008/notebook\" width=\"1000\" height=\"750\">{pages[i]}</svg>"));
            }
            if (manifestOrder is not null)
            {
                Add(zip, "imsmanifest.xml", Encoding.UTF8.GetBytes(
                    "<manifest xmlns=\"http://www.imsglobal.org/xsd/imscp_v1p1\" identifier=\"group0_pages\"><resources>" +
                    "<resource identifier=\"group0_pages\" href=\"page0.svg\" type=\"webcontent\" adlcp:scormType=\"asset\" xmlns:adlcp=\"http://www.adlnet.org/xsd/adlcp_rootv1p2\">" +
                    string.Concat(manifestOrder.Select(i => $"<file href=\"page{i}.svg\"/>")) +
                    "</resource><resource identifier=\"images\"><file href=\"images/pic.png\"/></resource></resources></manifest>"));
            }
            Add(zip, "images/pic.png", Png(10, 10, SKColors.Red, SKColors.Red));
            Add(zip, "metadata.xml", Encoding.UTF8.GetBytes("<metadata/>"));
        }
        return stream.ToArray();
    }

    /// <summary>One page of 400 × 300 px with <paramref name="body"/> in its svg (no pageSet).</summary>
    public static byte[] Single(string body, IReadOnlyDictionary<string, byte[]>? files = null) =>
        Package(Content($"<svg:svg width=\"400\" height=\"300\">{body}</svg:svg>"), files);
}
