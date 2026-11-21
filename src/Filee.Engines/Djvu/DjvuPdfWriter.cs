// DjVu pages as PDF pages, each at its size at the page's resolution (INFO), its turn as the page's /Rotate. The
// layers stay layers, so a scanned book stays small: the background as a JPEG at its own (reduced) resolution, the
// mask as 1-bit image masks painted in the foreground colour — one per palette colour, or the FG44 picture (a JPEG)
// shown through the mask (explicit masking). Each mask is coded the smallest way: JBIG2 (the mask's shapes as
// symbols, Jbig2Encoder — about the DjVu file's own size), CCITT G4 (CcittG4), or Flate for finely dithered pictures.
// Pages whose masks build on the same shared shape dictionary (Djbz) share one /JBIG2Globals stream holding its shapes
// (Jbig2SharedShapes), so a book's dictionary is in the PDF once. A page with more than MaxStencils palette colours is
// drawn whole as one JPEG instead. The hidden text becomes an
// invisible text layer (OcrPdfWriter's glyphless font), so the PDF can be searched and its text copied.
//
// Once all pages are in, their hyperlinks become link annotations (to a page, or a URI action for a web address) and
// the bookmarks the document outline: links into the document go to their page, web addresses open, anything else
// (a relative file, javascript:, file:) leaves the bookmark without an action and is no link. The first page's
// initial zoom becomes the PDF's open action and its metadata the document information; the display mode and the
// viewer's background colour have no PDF counterpart (the PDF keeps every layer).

using System.Globalization;
using System.IO.Compression;
using System.Text;
using Filee.Engines.Ocr;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

namespace Filee.Engines.Djvu;

/// <summary>The ways a mask may be coded in the PDF (all but for tests).</summary>
[Flags]
internal enum MaskCodings
{
    Jbig2 = 1,
    G4 = 2,
    Flate = 4,
    All = Jbig2 | G4 | Flate,
}

internal static class DjvuPdfWriter
{
    /// <summary>Palette colours drawn as separate masks at most; a page with more is drawn as one picture.</summary>
    internal const int MaxStencils = 64;

    /// <summary>Largest page side in points (PDF's own limit, 200 inches).</summary>
    private const double MaxSide = 14_400;

    private const int JpegQuality = 85;

    /// <summary>Outline levels written at most; deeper bookmarks go in at the deepest level.</summary>
    internal const int MaxOutlineDepth = 32;

    /// <summary>Points per page pixel.</summary>
    public static double Scale(DjvuPage page) => Math.Min(72.0 / page.Dpi, MaxSide / Math.Max(page.Width, page.Height));

    /// <summary>
    /// Adds <paramref name="page"/> to <paramref name="writer"/>'s document; its links come with <see cref="AddLinks"/>.
    /// Pages added with the same <paramref name="shared"/> share the JBIG2 shapes of their shared dictionaries.
    /// </summary>
    public static PdfPage AddPage(OcrPdfWriter writer, DjvuPage page, Jbig2SharedShapes? shared = null, CancellationToken cancellationToken = default) =>
        AddPage(writer, page, MaskCodings.All, shared, cancellationToken);

    /// <summary>Adds <paramref name="page"/>, its masks coded only in the ways <paramref name="codings"/> allows.</summary>
    internal static PdfPage AddPage(OcrPdfWriter writer, DjvuPage page, MaskCodings codings, Jbig2SharedShapes? shared, CancellationToken cancellationToken)
    {
        var document = writer.Document;
        var scale = Scale(page);
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
                    image.Elements["/Mask"] = Stencil(document, page, null, codings, shared, cancellationToken).Reference;
                    Draw(image);
                }
                else if (used is not null)
                {
                    foreach (var color in used)
                    {
                        var c = palette!.Colors[color];
                        Draw(Stencil(document, page, color, codings, shared, cancellationToken), $"{N(c.Red / 255.0)} {N(c.Green / 255.0)} {N(c.Blue / 255.0)} rg");
                    }
                }
                else
                {
                    Draw(Stencil(document, page, null, codings, shared, cancellationToken), "0 g");
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
        return pdfPage;
    }

    /// <summary>
    /// Adds a page's <paramref name="links"/> (page pixels × <paramref name="scale"/>, as <see cref="Scale"/> gave for
    /// it) to <paramref name="pdfPage"/>, a link into the document going to its page among <paramref name="pages"/>.
    /// </summary>
    public static void AddLinks(PdfDocument document, PdfPage pdfPage, IReadOnlyList<DjvuLink> links, double scale, IReadOnlyList<PdfPage> pages)
    {
        double width = pdfPage.Width.Point, height = pdfPage.Height.Point;
        var annotations = new PdfArray(document);
        foreach (var link in links)
        {
            var left = Math.Clamp(link.Left * scale, 0, width);
            var bottom = Math.Clamp(link.Bottom * scale, 0, height);
            var right = Math.Clamp((link.Left + (double)link.Width) * scale, 0, width);
            var top = Math.Clamp((link.Bottom + (double)link.Height) * scale, 0, height);
            if (right - left < 0.5 || top - bottom < 0.5)
                continue;
            var annotation = new PdfDictionary(document);
            annotation.Elements.SetName("/Type", "/Annot");
            annotation.Elements.SetName("/Subtype", "/Link");
            annotation.Elements["/Rect"] = Numbers(document, left, bottom, right, top);
            annotation.Elements["/Border"] = Numbers(document, 0, 0, 0);
            annotation.Elements.SetInteger("/F", 4); // printed (PDF/A wants it on every annotation)
            if (!Target(document, annotation, link.Url, link.Page, pages))
                continue;
            document.Internals.AddObject(annotation);
            annotations.Elements.Add(annotation.Reference!);
        }
        if (annotations.Elements.Count > 0)
            pdfPage.Elements["/Annots"] = annotations;
    }

    /// <summary>
    /// The document outline from <paramref name="bookmarks"/>: a bookmark into the document goes to the page
    /// <paramref name="pageOf"/> gives, one with a web address opens it, others are only titles. The first level is
    /// shown open.
    /// </summary>
    public static void AddOutline(PdfDocument document, IReadOnlyList<PdfPage> pages, IReadOnlyList<DjvuBookmark> bookmarks, Func<string, int> pageOf, CancellationToken cancellationToken = default)
    {
        if (bookmarks.Count == 0 || pages.Count == 0)
            return;
        var root = new OutlineItem(new PdfDictionary(document), -1);
        root.Dictionary.Elements.SetName("/Type", "/Outlines");
        var pending = new Stack<(DjvuBookmark Bookmark, OutlineItem Parent)>();
        for (var i = bookmarks.Count - 1; i >= 0; i--)
            pending.Push((bookmarks[i], root));
        var items = new List<OutlineItem> { root };
        while (pending.Count > 0)
        {
            if ((items.Count & 0xFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var (bookmark, parent) = pending.Pop();
            var item = new OutlineItem(new PdfDictionary(document), parent.Level + 1);
            var title = bookmark.Title.Length > 0 ? bookmark.Title : bookmark.Url;
            item.Dictionary.Elements["/Title"] = new PdfString(title.Length > 1000 ? title[..1000] : title, PdfStringEncoding.Unicode);
            var page = bookmark.Url.StartsWith('#') ? pageOf(bookmark.Url) : -1;
            Target(document, item.Dictionary, bookmark.Url, page, pages);
            parent.Children.Add(item);
            items.Add(item);
            // Past the deepest level, children follow their parent as its siblings.
            var holder = item.Level < MaxOutlineDepth - 1 ? item : parent;
            for (var i = bookmark.Children.Count - 1; i >= 0; i--)
                pending.Push((bookmark.Children[i], holder));
        }
        foreach (var item in items)
            document.Internals.AddObject(item.Dictionary);
        foreach (var item in items)
        {
            var children = item.Children;
            if (children.Count == 0)
                continue;
            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i].Dictionary;
                child.Elements["/Parent"] = item.Dictionary.Reference;
                if (i > 0)
                    child.Elements["/Prev"] = children[i - 1].Dictionary.Reference;
                if (i + 1 < children.Count)
                    child.Elements["/Next"] = children[i + 1].Dictionary.Reference;
            }
            item.Dictionary.Elements["/First"] = children[0].Dictionary.Reference;
            item.Dictionary.Elements["/Last"] = children[^1].Dictionary.Reference;
            // The first level is open, its children shown; deeper items are closed (a negative count).
            item.Dictionary.Elements.SetInteger("/Count", item.Level switch
            {
                -1 => children.Count + children.Sum(c => c.Children.Count),
                0 => children.Count,
                _ => -children.Count,
            });
        }
        document.Internals.Catalog.Elements["/Outlines"] = root.Dictionary.Reference;
        document.PageMode = PdfPageMode.UseOutlines;
    }

    /// <summary>
    /// The document's open action from the first page's initial zoom (<paramref name="scale"/>: points per pixel of
    /// that page) and its document information from the page's metadata.
    /// </summary>
    public static void AddView(PdfDocument document, IReadOnlyList<PdfPage> pages, DjvuDisplay display, double scale)
    {
        foreach (var (key, value) in display.Metadata)
        {
            switch (key.ToLowerInvariant())
            {
                case "title":
                    document.Info.Title = value;
                    break;
                case "author":
                    document.Info.Author = value;
                    break;
                case "subject":
                    document.Info.Subject = value;
                    break;
                case "keywords":
                    document.Info.Keywords = value;
                    break;
            }
        }
        if (display.Zoom is not { } zoom || pages.Count == 0)
            return;
        var destination = new PdfArray(document);
        destination.Elements.Add(pages[0].Reference!);
        double? factor = zoom switch
        {
            "width" or "page" or "stretch" => null,
            // One page pixel on one screen pixel of a 96 dpi screen.
            "one2one" => 0.75 / scale,
            _ => int.Parse(zoom.AsSpan(1), CultureInfo.InvariantCulture) / 100.0,
        };
        if (zoom == "width")
        {
            destination.Elements.Add(new PdfName("/FitH"));
            destination.Elements.Add(new PdfReal(Math.Round(pages[0].Height.Point, 3)));
        }
        else if (factor is null)
        {
            destination.Elements.Add(new PdfName("/Fit"));
        }
        else
        {
            destination.Elements.Add(new PdfName("/XYZ"));
            destination.Elements.Add(PdfNull.Value);
            destination.Elements.Add(PdfNull.Value);
            destination.Elements.Add(new PdfReal(Math.Round(Math.Clamp(factor.Value, 0.01, 64), 4)));
        }
        document.Internals.Catalog.Elements["/OpenAction"] = destination;
    }

    private sealed class OutlineItem(PdfDictionary dictionary, int level)
    {
        public PdfDictionary Dictionary { get; } = dictionary;
        public int Level { get; } = level;
        public List<OutlineItem> Children { get; } = [];
    }

    /// <summary>
    /// Points a link or bookmark at page <paramref name="page"/> (/Dest) or at a web address (a URI action); false when
    /// it goes nowhere Filee links to.
    /// </summary>
    private static bool Target(PdfDocument document, PdfDictionary item, string url, int page, IReadOnlyList<PdfPage> pages)
    {
        if (page >= 0 && page < pages.Count)
        {
            var destination = new PdfArray(document);
            destination.Elements.Add(pages[page].Reference!);
            destination.Elements.Add(new PdfName("/Fit"));
            item.Elements["/Dest"] = destination;
            return true;
        }
        if (!url.StartsWith('#') && DjvuAnnotations.IsWebAddress(url))
        {
            var action = new PdfDictionary(document);
            action.Elements.SetName("/S", "/URI");
            action.Elements["/URI"] = new PdfString(new Uri(url).AbsoluteUri);
            item.Elements["/A"] = action;
            return true;
        }
        return false;
    }

    private static PdfArray Numbers(PdfDocument document, params double[] values)
    {
        var array = new PdfArray(document);
        foreach (var value in values)
            array.Elements.Add(new PdfReal(Math.Round(value, 3)));
        return array;
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

    /// <summary>
    /// An image mask of the page's shapes (one palette colour's, or all), black painted, in the smallest of: JBIG2
    /// (the shapes as symbols, see Jbig2Encoder), CCITT G4, and — when neither packs the mask well (fine dithering) —
    /// Flate.
    /// </summary>
    private static PdfDictionary Stencil(PdfDocument document, DjvuPage page, int? color, MaskCodings codings, Jbig2SharedShapes? shared, CancellationToken cancellationToken)
    {
        var mask = page.Mask!;
        var blits = color is null ? mask.Blits : [.. mask.Blits.Where((_, b) => DjvuRenderer.ColorOf(page.Palette?.BlitColors, b, mask.Blits.Count) == color)];
        byte[]? jbig2 = null;
        var usesGlobals = false;
        if (codings.HasFlag(MaskCodings.Jbig2))
        {
            var sharedShapes = shared is not null && mask.Dictionary is { } dictionary ? shared.Of(dictionary) : null;
            jbig2 = Jbig2Encoder.Encode(page.Width, page.Height, mask.Shapes, blits, sharedShapes, out usesGlobals, cancellationToken);
        }
        var bits = DjvuRenderer.MaskBits(page, color, cancellationToken);
        var g4 = codings.HasFlag(MaskCodings.G4) ? CcittG4.Encode(bits, page.Width, page.Height, cancellationToken) : null;
        var best = Math.Min(jbig2?.Length ?? int.MaxValue, g4?.Length ?? int.MaxValue);
        var flate = codings.HasFlag(MaskCodings.Flate) && best > bits.Length / 16 ? Deflate(bits) : null;
        PdfDictionary image;
        if (flate is not null && flate.Length < best)
        {
            image = Stream(document, flate, "/FlateDecode");
            var decode = new PdfArray(document);
            decode.Elements.Add(new PdfInteger(1));
            decode.Elements.Add(new PdfInteger(0));
            image.Elements["/Decode"] = decode;
        }
        else if (jbig2 is not null && jbig2.Length == best)
        {
            // JBIG2's black samples come out as 0, which an image mask paints by default.
            image = Stream(document, jbig2, "/JBIG2Decode");
            if (usesGlobals)
            {
                var parameters = new PdfDictionary(document);
                parameters.Elements["/JBIG2Globals"] = shared!.Use(document, mask.Dictionary!, blits, cancellationToken).Reference;
                image.Elements["/DecodeParms"] = parameters;
            }
        }
        else
        {
            // So do G4's (/BlackIs1 false).
            image = Stream(document, g4!, "/CCITTFaxDecode");
            var parameters = new PdfDictionary(document);
            parameters.Elements.SetInteger("/K", -1);
            parameters.Elements.SetInteger("/Columns", page.Width);
            parameters.Elements.SetInteger("/Rows", page.Height);
            image.Elements["/DecodeParms"] = parameters;
        }
        image.Elements.SetName("/Type", "/XObject");
        image.Elements.SetName("/Subtype", "/Image");
        image.Elements.SetInteger("/Width", page.Width);
        image.Elements.SetInteger("/Height", page.Height);
        image.Elements.SetBoolean("/ImageMask", true);
        image.Elements.SetInteger("/BitsPerComponent", 1);
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

/// <summary>
/// The /JBIG2Globals of a PDF's shared DjVu shape dictionaries: each dictionary's symbol ids are fixed when a page first
/// builds on it; its stream is made when a mask first uses it and filled with the shapes pages drew when it leaves the
/// few kept (as DjvuDocument keeps a few decoded dictionaries) or at <see cref="Finish"/>, before the PDF is saved.
/// </summary>
internal sealed class Jbig2SharedShapes
{
    private const int Kept = 8;

    private sealed class Entry(Jbig2Globals globals)
    {
        public Jbig2Globals Globals { get; } = globals;
        public PdfDictionary? Stream { get; set; }
    }

    private readonly Dictionary<Jb2Dictionary, Entry> _entries = new(ReferenceEqualityComparer.Instance);

    /// <summary>The shared symbols of <paramref name="dictionary"/>'s shapes.</summary>
    public Jbig2Globals Of(Jb2Dictionary dictionary)
    {
        if (!_entries.TryGetValue(dictionary, out var entry))
        {
            if (_entries.Count >= Kept)
                Finish();
            entry = new Entry(new Jbig2Globals(dictionary.Shapes, dictionary.Shapes.Count));
            _entries[dictionary] = entry;
        }
        return entry.Globals;
    }

    /// <summary>
    /// The stream of <paramref name="dictionary"/>'s shared segments in <paramref name="document"/>, for a mask that
    /// draws <paramref name="blits"/>.
    /// </summary>
    public PdfDictionary Use(PdfDocument document, Jb2Dictionary dictionary, IEnumerable<Jb2Blit> blits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = _entries[dictionary];
        entry.Globals.Use(blits.Select(blit => blit.Shape));
        if (entry.Stream is null)
        {
            entry.Stream = new PdfDictionary(document);
            document.Internals.AddObject(entry.Stream);
        }
        return entry.Stream;
    }

    /// <summary>Writes the streams of the dictionaries masks used and forgets them all.</summary>
    public void Finish(CancellationToken cancellationToken = default)
    {
        foreach (var entry in _entries.Values)
            entry.Stream?.CreateStream(entry.Globals.Encode(cancellationToken));
        _entries.Clear();
    }
}
