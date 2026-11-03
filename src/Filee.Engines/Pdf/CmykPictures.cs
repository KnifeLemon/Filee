// CMYK pictures (print work: a CMYK PSD, TIFF or JPEG) go into a PDF as CMYK, with their own ICC profile (#1027).
// PDFsharp's image import takes grey and RGB only, so the image XObject is written here: a JPEG source as JPEG data
// (DCTDecode), anything else as the raw CMYK samples compressed losslessly (FlateDecode), transparency as a soft mask.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Filee.Engines.Pdf;

internal static class CmykPictures
{
    /// <summary>A picture that goes into the PDF as CMYK rather than through PDFsharp.</summary>
    public static bool Keeps(IMagickImage<byte> frame) => frame.ColorSpace == ColorSpace.CMYK;

    /// <summary>
    /// Draws <paramref name="frame"/> (CMYK) on <paramref name="page"/> into <paramref name="box"/> (points, from the
    /// top left as XGraphics measures). <paramref name="asJpeg"/> keeps a JPEG source as JPEG at
    /// <paramref name="quality"/>; a picture with transparency is always stored losslessly.
    /// </summary>
    public static void Place(PdfDocument document, PdfPage page, IMagickImage<byte> frame, bool asJpeg, uint quality, XRect box)
    {
        var image = new PdfDictionary(document);
        image.Elements.SetName("/Type", "/XObject");
        image.Elements.SetName("/Subtype", "/Image");
        image.Elements.SetInteger("/Width", (int)frame.Width);
        image.Elements.SetInteger("/Height", (int)frame.Height);
        image.Elements.SetInteger("/BitsPerComponent", 8);
        image.Elements["/ColorSpace"] = ColorSpaceOf(document, frame);

        if (asJpeg && !frame.HasAlpha)
        {
            using var jpeg = frame.Clone();
            jpeg.Quality = quality;
            var data = jpeg.ToByteArray(MagickFormat.Jpeg);
            image.Elements.SetName("/Filter", "/DCTDecode");
            // ImageMagick writes CMYK JPEGs as Photoshop does: inverted, with Adobe's APP14 marker. PDF reads JPEG data
            // as it is, so the inversion is undone with the Decode array.
            if (HasAdobeMarker(data))
                image.Elements["/Decode"] = Decode(4);
            image.CreateStream(data);
        }
        else
        {
            using var pixels = frame.GetPixels();
            image.Elements.SetName("/Filter", "/FlateDecode");
            image.CreateStream(Deflate(pixels.ToByteArray("CMYK")!));
            if (frame.HasAlpha)
                image.Elements["/SMask"] = SoftMask(document, frame, pixels.ToByteArray("A")!);
        }
        document.Internals.AddObject(image);

        var resources = page.Resources;
        if (resources.Elements.GetDictionary("/XObject") is not { } xobjects)
        {
            xobjects = new PdfDictionary(document);
            resources.Elements["/XObject"] = xobjects;
        }
        var name = $"/FileeCmyk{xobjects.Elements.Count + 1}";
        xobjects.Elements[name] = image.Reference;

        // PDF measures from the bottom left.
        var y = page.Height.Point - box.Y - box.Height;
        var draw = string.Create(CultureInfo.InvariantCulture,
            $"q {box.Width:0.###} 0 0 {box.Height:0.###} {box.X:0.###} {y:0.###} cm {name} Do Q\n");
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(draw));
    }

    /// <summary>The picture's own CMYK profile as an ICCBased colour space, else DeviceCMYK.</summary>
    private static PdfItem ColorSpaceOf(PdfDocument document, IMagickImage<byte> frame)
    {
        if (frame.GetColorProfile() is not { ColorSpace: ColorSpace.CMYK } profile || profile.ToByteArray() is not { Length: > 0 } bytes)
            return new PdfName("/DeviceCMYK");
        var icc = new PdfDictionary(document);
        icc.Elements.SetInteger("/N", 4);
        icc.Elements.SetName("/Alternate", "/DeviceCMYK");
        icc.Elements.SetName("/Filter", "/FlateDecode");
        icc.CreateStream(Deflate(bytes));
        document.Internals.AddObject(icc);
        var space = new PdfArray(document);
        space.Elements.Add(new PdfName("/ICCBased"));
        space.Elements.Add(icc.Reference!);
        return space;
    }

    private static PdfReference SoftMask(PdfDocument document, IMagickImage<byte> frame, byte[] alpha)
    {
        var mask = new PdfDictionary(document);
        mask.Elements.SetName("/Type", "/XObject");
        mask.Elements.SetName("/Subtype", "/Image");
        mask.Elements.SetInteger("/Width", (int)frame.Width);
        mask.Elements.SetInteger("/Height", (int)frame.Height);
        mask.Elements.SetInteger("/BitsPerComponent", 8);
        mask.Elements.SetName("/ColorSpace", "/DeviceGray");
        mask.Elements.SetName("/Filter", "/FlateDecode");
        mask.CreateStream(Deflate(alpha));
        document.Internals.AddObject(mask);
        return mask.Reference!;
    }

    private static PdfArray Decode(int components)
    {
        var decode = new PdfArray();
        for (var i = 0; i < components; i++)
        {
            decode.Elements.Add(new PdfInteger(1));
            decode.Elements.Add(new PdfInteger(0));
        }
        return decode;
    }

    /// <summary>True when the JPEG has Adobe's APP14 marker (its CMYK samples are stored inverted).</summary>
    internal static bool HasAdobeMarker(ReadOnlySpan<byte> jpeg)
    {
        // Markers follow the start of image: FF xx, then a big-endian length that counts itself, up to the scan.
        var at = 2;
        while (at + 4 <= jpeg.Length && jpeg[at] == 0xFF)
        {
            var marker = jpeg[at + 1];
            if (marker is 0xDA or 0xD9)
                return false;
            var length = (jpeg[at + 2] << 8) | jpeg[at + 3];
            if (marker == 0xEE && length >= 7 && at + 9 <= jpeg.Length && jpeg.Slice(at + 4, 5).SequenceEqual("Adobe"u8))
                return true;
            at += 2 + length;
        }
        return false;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
            zlib.Write(data);
        return output.ToArray();
    }
}
