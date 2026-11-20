// DjVu pages as PDF pages, each at its size at the page's resolution (INFO), its turn as the page's /Rotate. The
// layers stay layers, so a scanned book stays small: the background as a JPEG at its own (reduced) resolution, the
// mask as 1-bit image masks (Flate) painted in the foreground colour — one per palette colour, or the FG44 picture
// (a JPEG) shown through the mask (explicit masking). A page with more than MaxStencils palette colours is drawn
// whole as one JPEG instead. The hidden text becomes an invisible text layer (OcrPdfWriter's glyphless font), so the
// PDF can be searched and its text copied.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using Filee.Engines.Ocr;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

namespace Filee.Engines.Djvu;

internal static class DjvuPdfWriter
{
    /// <summary>Palette colours drawn as separate masks at most; a page with more is drawn as one picture.</summary>
    internal const int MaxStencils = 64;

    /// <summary>Largest page side in points (PDF's own limit, 200 inches).</summary>
    private const double MaxSide = 14_400;

    private const int JpegQuality = 85;

    /// <summary>Adds <paramref name="page"/> to <paramref name="writer"/>'s document.</summary>
    public static void AddPage(OcrPdfWriter writer, DjvuPage page, CancellationToken cancellationToken = default)
    {
        var document = writer.Document;
        var scale = 72.0 / page.Dpi;
        scale = Math.Min(scale, MaxSide / Math.Max(page.Width, page.Height));
        var width = page.Width * scale;
        var height = page.Height * scale;
        var pdfPage = document.AddPage();
        pdfPage.Width = XUnit.FromPoint(width);
        pdfPage.Height = XUnit.FromPoint(height);
        if (page.Rotation != 0)
            pdfPage.Elements.SetInteger("/Rotate", page.Rotation switch { 1 => 270, 2 => 180, _ => 90 });

        var content = new StringBuilder();
        var images = new List<(string Name, PdfDictionary Image)>();
        void Draw(PdfDictionary image, string? fill = null)
        {
            var name = "/Im" + images.Count.ToString(CultureInfo.InvariantCulture);
            images.Add((name, image));
            content.Append("q ");
            if (fill is not null)
                content.Append(fill).Append(' ');
            content.Append(CultureInfo.InvariantCulture, $"{N(width)} 0 0 {N(height)} 0 0 cm {name} Do Q\n");
        }

        var mask = page.Mask is { Blits.Count: > 0 } ? page.Mask : null;
        var palette = page.Palette is { Colors.Length: > 0 } p ? p : null;
        var used = mask is null || palette is null || page.Foreground is not null ? null : UsedColors(mask, palette);
        if (used is { Count: > MaxStencils })
        {
            // Too many colours to draw one mask each: the page as it looks.
            Draw(Jpeg(document, new DjvuPixmap(page.Width, page.Height, DjvuRenderer.Compose(page, cancellationToken))));
        }
        else
        {
            if (page.Background is { } background)
                Draw(Jpeg(document, background));
            if (mask is not null)
            {
                if (page.Foreground is { } foreground)
                {
                    var image = Jpeg(document, foreground);
                    image.Elements["/Mask"] = Stencil(document, page, null, cancellationToken).Reference;
                    Draw(image);
                }
                else if (used is not null)
                {
                    foreach (var color in used)
                    {
                        var c = palette!.Colors[color];
                        Draw(Stencil(document, page, color, cancellationToken), $"{N(c.Red / 255.0)} {N(c.Green / 255.0)} {N(c.Blue / 255.0)} rg");
                    }
                }
                else
                {
                    Draw(Stencil(document, page, null, cancellationToken), "0 g");
                }
            }
        }

        if (images.Count > 0)
        {
            if (pdfPage.Resources.Elements.GetDictionary("/XObject") is not { } objects)
            {
                objects = new PdfDictionary(document);
                pdfPage.Resources.Elements["/XObject"] = objects;
            }
            foreach (var (name, image) in images)
                objects.Elements.SetReference(name, image);
            pdfPage.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(content.ToString()));
        }
        writer.AddText(pdfPage, Lines(page), width, height);
    }

    /// <summary>The palette colours the mask's shapes use, in the order first used.</summary>
    private static List<int> UsedColors(Jb2Image mask, DjvuPalette palette)
    {
        var used = new List<int>();
        var seen = new HashSet<int>();
        for (var b = 0; b < mask.Blits.Count; b++)
        {
            var color = DjvuRenderer.ColorOf(palette.BlitColors, b, mask.Blits.Count);
            if (seen.Add(color))
            {
                used.Add(color);
                if (used.Count > MaxStencils)
                    break;
            }
        }
        return used;
    }

    /// <summary>The page's text lines as fractions of the page, as the OCR layer takes them.</summary>
    internal static List<OcrLine> Lines(DjvuPage page)
    {
        double w = page.Width, h = page.Height;
        var lines = new List<OcrLine>();
        foreach (var line in page.Text)
        {
            var box = line.Box;
            if (line.Words.Count == 0 || !LaidOut(line.Words))
            {
                lines.Add(OcrLine.Estimate(line.Text, box.Left / w, box.Bottom / h, box.Width / w, box.Height / h));
                continue;
            }
            var words = line.Words.Select(word => new OcrWord(word.Text, word.Box.Left / w, word.Box.Width / w)).ToList();
            lines.Add(new OcrLine(box.Left / w, box.Bottom / h, box.Width / w, box.Height / h, words));
        }
        return lines;
    }

    /// <summary>
    /// Whether a line's word boxes follow each other left to right. Some files give every word the box of its whole
    /// line; their words are spread over the line by their length instead, so they don't overlap in the PDF.
    /// </summary>
    private static bool LaidOut(IReadOnlyList<DjvuWord> words)
    {
        for (var i = 1; i < words.Count; i++)
        {
            var previous = words[i - 1].Box;
            var word = words[i].Box;
            if (previous.Left + previous.Width - word.Left > Math.Min(previous.Width, word.Width) / 2)
                return false;
        }
        return true;
    }

    /// <summary>An image mask of the page's shapes (one palette colour's, or all), a set sample painted.</summary>
    private static PdfDictionary Stencil(PdfDocument document, DjvuPage page, int? color, CancellationToken cancellationToken)
    {
        var bits = DjvuRenderer.MaskBits(page, color, cancellationToken);
        var image = Stream(document, Deflate(bits), "/FlateDecode");
        image.Elements.SetName("/Type", "/XObject");
        image.Elements.SetName("/Subtype", "/Image");
        image.Elements.SetInteger("/Width", page.Width);
        image.Elements.SetInteger("/Height", page.Height);
        image.Elements.SetBoolean("/ImageMask", true);
        image.Elements.SetInteger("/BitsPerComponent", 1);
        var decode = new PdfArray(document);
        decode.Elements.Add(new PdfInteger(1));
        decode.Elements.Add(new PdfInteger(0));
        image.Elements["/Decode"] = decode;
        return image;
    }

    /// <summary>A JPEG image of <paramref name="pixmap"/>, in grey when every pixel is grey.</summary>
    private static PdfDictionary Jpeg(PdfDocument document, DjvuPixmap pixmap)
    {
        var rgb = pixmap.Rgb;
        var grey = true;
        for (var i = 0; i < rgb.Length && grey; i += 3)
            grey = rgb[i] == rgb[i + 1] && rgb[i] == rgb[i + 2];
        byte[] jpeg;
        if (grey)
        {
            var pixels = new byte[(long)pixmap.Width * pixmap.Height];
            for (int i = 0, o = 0; o < pixels.Length; i += 3, o++)
                pixels[o] = rgb[i];
            jpeg = Encode(pixels, new SKImageInfo(pixmap.Width, pixmap.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        }
        else
        {
            var pixels = new byte[(long)pixmap.Width * pixmap.Height * 4];
            for (int i = 0, o = 0; o < pixels.Length; i += 3, o += 4)
            {
                pixels[o] = rgb[i];
                pixels[o + 1] = rgb[i + 1];
                pixels[o + 2] = rgb[i + 2];
                pixels[o + 3] = 255;
            }
            jpeg = Encode(pixels, new SKImageInfo(pixmap.Width, pixmap.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        }
        var image = Stream(document, jpeg, "/DCTDecode");
        image.Elements.SetName("/Type", "/XObject");
        image.Elements.SetName("/Subtype", "/Image");
        image.Elements.SetInteger("/Width", pixmap.Width);
        image.Elements.SetInteger("/Height", pixmap.Height);
        image.Elements.SetName("/ColorSpace", grey ? "/DeviceGray" : "/DeviceRGB");
        image.Elements.SetInteger("/BitsPerComponent", 8);
        return image;
    }

    private static byte[] Encode(byte[] pixels, SKImageInfo info)
    {
        using var image = SKImage.FromPixelCopy(info, pixels, info.RowBytes) ?? throw new InvalidOperationException("Skia can't hold the picture.");
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality) ?? throw new InvalidOperationException("Skia can't write the JPEG.");
        return data.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return buffer.ToArray();
    }

    private static PdfDictionary Stream(PdfDocument document, byte[] data, string filter)
    {
        var dictionary = new PdfDictionary(document);
        document.Internals.AddObject(dictionary);
        dictionary.CreateStream(data);
        dictionary.Elements.SetName("/Filter", filter);
        return dictionary;
    }

    private static string N(double value) => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
}
