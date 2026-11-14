// PDF pictures (image XObjects and inline images) as PostScript images. The data is decoded by Filee (Flate, LZW,
// RunLength, ASCII filters, predictors), checked against the size the picture needs (short data is padded as
// viewers pad it) and deflated again; JPEG (DCT) and CCITT fax data go to PostScript as they are, with PostScript's
// own filters. Device and Indexed colour spaces stay; a one-component Separation picture becomes an Indexed one whose
// table holds the converted tints. Stencil masks become imagemask, colour-key masks ImageType 4, explicit masks and
// soft masks that are only fully opaque or fully transparent ImageType 3.
//
// Data a page reads once follows the image in the file (ASCII85) and is read through a filter that is flushed to
// its end afterwards, so nothing of a picture's data can ever be read as PostScript code. Data in a procedure run
// later (a pattern cell, a Type 3 glyph) is a string of at most 64 KB.
//
// What PostScript can't draw this way (soft masks with partial transparency, JBIG2, JPEG 2000, 16-bit pictures in
// converted spaces, Lab and DeviceN pictures) makes the page untranslatable, and the page is drawn as a picture.

using System.IO.Compression;
using Filee.Engines.Documents;
using PdfSharp.Pdf;

namespace Filee.Engines.Pdf.PostScript;

/// <summary>A picture's description and data.</summary>
internal sealed class ImageSource
{
    public int Width { get; private init; }
    public int Height { get; private init; }
    public int Bits { get; private init; }
    public bool IsMask { get; private init; }
    public PdfColorSpace? Space { get; private init; }
    public double[]? Decode { get; private init; }
    public bool Interpolate { get; private init; }
    public List<PdfFilter> Filters { get; private init; } = [];
    public byte[] Data { get; private init; } = [];

    /// <summary>A colour-key mask (ranges per component).</summary>
    public double[]? ColorKey { get; private init; }

    /// <summary>An explicit stencil mask.</summary>
    public ImageSource? StencilMask { get; private init; }

    /// <summary>A soft mask (grey picture of alpha values).</summary>
    public ImageSource? SoftMask { get; private init; }

    public bool HasMatte { get; private init; }

    public static ImageSource FromXObject(PdfDictionary image, PdfDictionary? resources, int depth = 0)
    {
        var isMask = PdfObjects.Boolean(image, "/ImageMask", false);
        var result = new ImageSource
        {
            Width = (int)PdfObjects.Number(image, "/Width", 0),
            Height = (int)PdfObjects.Number(image, "/Height", 0),
            Bits = isMask ? 1 : (int)PdfObjects.Number(image, "/BitsPerComponent", 8),
            IsMask = isMask,
            Space = isMask ? null : ReadSpace(PdfObjects.Get(image, "/ColorSpace"), image, resources),
            Decode = PdfObjects.Numbers(image, "/Decode"),
            Interpolate = PdfObjects.Boolean(image, "/Interpolate", false),
            Filters = PdfStreams.Filters(image),
            Data = image.Stream?.Value ?? [],
            ColorKey = PdfObjects.Get(image, "/Mask") is PdfArray key ? PdfObjects.Numbers(key) : null,
            StencilMask = depth == 0 && PdfObjects.Get(image, "/Mask") is PdfDictionary { Stream: not null } mask ? FromXObject(mask, resources, 1) : null,
            SoftMask = depth == 0 && PdfObjects.Get(image, "/SMask") is PdfDictionary { Stream: not null } soft ? FromXObject(soft, resources, 1) : null,
            HasMatte = PdfObjects.Get(image, "/Matte") is not null,
        };
        if (PdfObjects.Number(image, "/SMaskInData", 0) != 0)
            throw new PsUntranslatableException("A JPEG 2000 picture has transparency.");
        return result;
    }

    private static PdfColorSpace ReadSpace(PdfItem? space, PdfDictionary image, PdfDictionary? resources)
    {
        if (space is null)
        {
            // JPX pictures may leave the colour space to their own data.
            throw new PsUntranslatableException("A picture has no colour space.");
        }
        return PdfColorSpace.Read(space, resources);
    }

    public static ImageSource FromInline(Dictionary<string, object?> p, byte[] data, PdfDictionary? resources)
    {
        object? Get(string full, string abbreviation) => p.TryGetValue(full, out var v) ? v : p.GetValueOrDefault(abbreviation);
        double Number(string full, string abbreviation, double fallback) => Get(full, abbreviation) is double d ? d : fallback;
        bool Bool(string full, string abbreviation) => Get(full, abbreviation) is PdfContentLexer.Operator { Name: "true" };
        var isMask = Bool("ImageMask", "IM");
        var decode = Get("Decode", "D") is List<object?> d ? d.Select(v => v is double n ? n : 0).ToArray() : null;
        return new ImageSource
        {
            Width = (int)Number("Width", "W", 0),
            Height = (int)Number("Height", "H", 0),
            Bits = isMask ? 1 : (int)Number("BitsPerComponent", "BPC", 8),
            IsMask = isMask,
            Space = isMask ? null : PdfColorSpace.Read(Get("ColorSpace", "CS") ?? throw new InvalidDataException("An inline image has no colour space."), resources),
            Decode = decode,
            Interpolate = Bool("Interpolate", "I"),
            Filters = PdfStreams.Filters(Get("Filter", "F"), Get("DecodeParms", "DP")),
            Data = data,
        };
    }

    /// <summary>The length of an inline image's data when it has no filter, for the lexer to find its end.</summary>
    public static int? InlineLength(Dictionary<string, object?> p)
    {
        if (p.ContainsKey("F") || p.ContainsKey("Filter"))
            return null;
        object? Get(string full, string abbreviation) => p.TryGetValue(full, out var v) ? v : p.GetValueOrDefault(abbreviation);
        var isMask = Get("ImageMask", "IM") is PdfContentLexer.Operator { Name: "true" };
        if (Get("Width", "W") is not double w || Get("Height", "H") is not double h || w < 1 || h < 1 || w * h > 1e8)
            return null;
        var bits = isMask ? 1 : Get("BitsPerComponent", "BPC") is double b ? b : 8;
        var components = isMask ? 1 : Get("ColorSpace", "CS") switch
        {
            PdfContentLexer.Name { Value: "RGB" or "DeviceRGB" or "CalRGB" } => 3,
            PdfContentLexer.Name { Value: "CMYK" or "DeviceCMYK" } => 4,
            PdfContentLexer.Name { Value: "G" or "DeviceGray" or "CalGray" } => 1,
            List<object?> { Count: > 0 } l when l[0] is PdfContentLexer.Name { Value: "I" or "Indexed" } => 1,
            _ => -1,
        };
        if (components < 0)
            return null;
        return (int)Math.Ceiling(w * components * bits / 8) * (int)h;
    }

    public int Components => IsMask ? 1 : Space?.Components ?? 1;

    public long RowBytes => ((long)Width * Components * Bits + 7) / 8;
}

/// <summary>Draws pictures in PostScript.</summary>
internal static class PsImages
{
    /// <summary>Bytes of a PostScript string, the most a picture in a procedure may take.</summary>
    private const int MaxString = 65_535;

    /// <summary>
    /// Draws <paramref name="image"/> into the unit square of the current space. <paramref name="deferred"/> keeps the
    /// data in strings (for a procedure run later). Stencil masks paint the current fill colour, which the caller sets.
    /// </summary>
    public static void Draw(PsWriter w, ImageSource image, bool deferred)
    {
        if (image.Width < 1 || image.Height < 1 || (long)image.Width * image.Height > DocumentLimits.Default.MaxPicturePixels)
            throw new InvalidDataException("A picture has an unusual size.");
        if (image.Bits is not (1 or 2 or 4 or 8 or 16))
            throw new InvalidDataException("A picture has an unusual number of bits per component.");
        if (image.Space is PdfColorSpace.Tinted { PaintsNothing: true })
            return;

        var (spaceCode, decode, convert) = Space(image);
        var data = Samples(image, convert, out var pictureFilter, out var bits);
        var key = image.ColorKey;
        if (key is not null && image.Bits == 16 && bits == 8)
            key = [.. key.Select(v => Math.Floor(v / 256))];

        ImageSource? mask = image.StencilMask;
        byte[]? maskData = null;
        int maskWidth = 0, maskHeight = 0;
        var maskDecode = "[0 1]";
        if (image.SoftMask is { } soft)
        {
            if (image.HasMatte)
                throw new PsUntranslatableException("A picture has a pre-multiplied soft mask.");
            (maskData, maskWidth, maskHeight) = BinaryMask(soft);
        }
        else if (mask is not null)
        {
            maskData = Samples(mask, null, out var maskFilter, out _);
            if (maskFilter is not null)
                throw new PsUntranslatableException("An explicit mask is compressed with a picture filter.");
            (maskWidth, maskHeight) = (mask.Width, mask.Height);
            if (mask.Decode is [> 0.5, < 0.5, ..])
                maskDecode = "[1 0]";
        }

        // The data's PostScript filters, outermost first after ASCII85.
        var filters = pictureFilter is null ? "/FlateDecode filter" : PictureFilter(pictureFilter, image);
        var payload = pictureFilter is null ? Deflate(data) : data;
        var maskPayload = maskData is null ? null : Deflate(maskData);
        if (deferred && (payload.Length > MaxString || maskPayload?.Length > MaxString))
            throw new PsUntranslatableException("A picture in a pattern or Type 3 glyph is too large for PostScript.");

        w.NewLine().Token("gsave");
        if (spaceCode is not null)
            w.Token(spaceCode).Token("setcolorspace");
        if (maskPayload is not null && !deferred)
        {
            w.NewLine().Token("/FileeMask currentfile /ASCII85Decode filter /ReusableStreamDecode filter");
            w.Ascii85(maskPayload);
            w.Token("def");
        }
        if (!deferred)
            w.NewLine().Token("/FileeData currentfile /ASCII85Decode filter def");

        void DataSource()
        {
            w.Name("DataSource");
            if (deferred)
                w.Ascii85(payload, literal: true);
            else
                w.Token("FileeData");
            w.Token(filters);
        }

        void ImageDict(int width, int height, int bpc, string? decodeArray, Action source)
        {
            w.Token("<<").Name("ImageType").Number(1).Name("Width").Number(width).Name("Height").Number(height)
                .Name("BitsPerComponent").Number(bpc).Name("ImageMatrix").Array([width, 0, 0, -height, 0, height]);
            if (decodeArray is not null)
                w.Name("Decode").Token(decodeArray);
            if (image.Interpolate)
                w.Name("Interpolate").Token("true");
            source();
            w.Token(">>");
        }

        var imageDecode = image.IsMask
            ? image.Decode is [> 0.5, < 0.5, ..] ? "[1 0]" : "[0 1]"
            : decode ?? DefaultDecode(image, bits);
        if (image.IsMask)
        {
            ImageDict(image.Width, image.Height, 1, imageDecode, DataSource);
            w.Token(deferred ? "imagemask" : "FileeImageMask");
        }
        else if (maskPayload is not null)
        {
            w.Token("<<").Name("ImageType").Number(3).Name("InterleaveType").Number(3).Name("DataDict");
            ImageDict(image.Width, image.Height, bits, imageDecode, DataSource);
            w.Name("MaskDict");
            ImageDict(maskWidth, maskHeight, 1, maskDecode, () =>
            {
                w.Name("DataSource");
                if (deferred)
                    w.Ascii85(maskPayload, literal: true);
                else
                    w.Token("FileeMask");
                w.Token("/FlateDecode filter");
            });
            w.Token(">>").Token(deferred ? "image" : "FileeImage");
        }
        else if (key is not null)
        {
            w.Token("<<").Name("ImageType").Number(4).Name("MaskColor").Array([.. key.Select(v => Math.Round(v))])
                .Name("Width").Number(image.Width).Name("Height").Number(image.Height).Name("BitsPerComponent").Number(bits)
                .Name("ImageMatrix").Array([image.Width, 0, 0, -image.Height, 0, image.Height]).Name("Decode").Token(imageDecode);
            if (image.Interpolate)
                w.Name("Interpolate").Token("true");
            DataSource();
            w.Token(">>").Token(deferred ? "image" : "FileeImage");
        }
        else
        {
            ImageDict(image.Width, image.Height, bits, imageDecode, DataSource);
            w.Token(deferred ? "image" : "FileeImage");
        }
        if (!deferred)
            w.Ascii85(payload);
        w.Token("grestore").NewLine();
    }

    /// <summary>
    /// The PostScript colour space, a Decode array that replaces the picture's (null: keep it) and a converter for the
    /// samples (null: as they are).
    /// </summary>
    private static (string? Space, string? Decode, Func<byte[], byte[]>? Convert) Space(ImageSource image)
    {
        switch (image.Space)
        {
            case null:
                return (null, null, null);
            case { Device: { } kind }:
                return ($"/{DeviceColor.SpaceName(kind)}", null, null);
            case PdfColorSpace.Indexed indexed:
                {
                    if (indexed.Base.Device is { } baseKind)
                        return (IndexedCode(baseKind, indexed.High, indexed.Table), null, null);
                    var table = new List<byte>();
                    for (var i = 0; i <= indexed.High; i++)
                        AddRgb(table, indexed.Base.ToDevice(indexed.Entry(i)));
                    return (IndexedCode(DeviceKind.Rgb, indexed.High, [.. table]), null, null);
                }
            case PdfColorSpace.Tinted { Components: 1 } tinted when image.Bits <= 8:
                {
                    // Each sample value's tint, converted: the picture becomes an Indexed one.
                    var high = (1 << image.Bits) - 1;
                    var (d0, d1) = image.Decode is { Length: >= 2 } d ? (d[0], d[1]) : (0.0, 1.0);
                    var table = new List<byte>();
                    for (var v = 0; v <= high; v++)
                        AddRgb(table, tinted.ToDevice([d0 + v * (d1 - d0) / high]));
                    return (IndexedCode(DeviceKind.Rgb, high, [.. table]), $"[0 {high}]", null);
                }
            default:
                throw new PsUntranslatableException("A picture's colour space can't be drawn in PostScript.");
        }
    }

    private static void AddRgb(List<byte> table, DeviceColor color)
    {
        var rgb = color.ToRgb();
        table.Add((byte)Math.Round(Math.Clamp(rgb.C0, 0, 1) * 255));
        table.Add((byte)Math.Round(Math.Clamp(rgb.C1, 0, 1) * 255));
        table.Add((byte)Math.Round(Math.Clamp(rgb.C2, 0, 1) * 255));
    }

    private static string IndexedCode(DeviceKind kind, int high, byte[] table)
    {
        var w = new System.Text.StringBuilder();
        w.Append($"[/Indexed /{DeviceColor.SpaceName(kind)} {high} <");
        w.Append(Convert.ToHexStringLower(table));
        w.Append(">]");
        // keep lines short: a newline every 100 hex digits inside the hex string is white space
        for (var i = w.ToString().IndexOf('<') + 101; i < w.Length - 2; i += 101)
            w.Insert(i, '\n');
        return w.ToString();
    }

    private static string DefaultDecode(ImageSource image, int bits)
    {
        if (image.Decode is { } decode && decode.Length == 2 * image.Components)
            return "[" + string.Join(' ', decode.Select(v => PsWriter.Format(v))) + "]";
        if (image.Space is PdfColorSpace.Indexed)
            return $"[0 {(1 << bits) - 1}]";
        return "[" + string.Join(' ', Enumerable.Repeat("0 1", image.Components)) + "]";
    }

    /// <summary>
    /// The samples, decoded and of the length the picture needs (padded with zeros, cut when longer). A JPEG or CCITT
    /// fax picture is left encoded and its filter returned. 16-bit samples become 8-bit (PostScript has no 16-bit pictures).
    /// </summary>
    private static byte[] Samples(ImageSource image, Func<byte[], byte[]>? convert, out PdfFilter? pictureFilter, out int bits)
    {
        bits = image.Bits;
        var rowBytes = image.RowBytes;
        var expected = rowBytes * image.Height;
        if (expected > 1024L * 1024 * 1024)
            throw new InvalidDataException("A picture is too large.");
        var (data, rest) = PdfStreams.DecodeGeneral(image.Data, image.Filters, expected + 1024);
        pictureFilter = null;
        if (rest.Count > 0)
        {
            if (rest.Count > 1 || rest[0].Name is not ("DCTDecode" or "CCITTFaxDecode"))
                throw new PsUntranslatableException($"Pictures compressed with {rest[0].Name} can't be drawn in PostScript.");
            if (image.Bits == 16)
                throw new PsUntranslatableException("A 16-bit JPEG picture can't be drawn in PostScript.");
            pictureFilter = rest[0];
            return data;
        }
        if (data.LongLength != expected)
        {
            var exact = new byte[expected];
            data.AsSpan(0, (int)Math.Min(data.LongLength, expected)).CopyTo(exact);
            data = exact;
        }
        if (image.Bits == 16)
        {
            var eight = new byte[data.Length / 2];
            for (var i = 0; i < eight.Length; i++)
                eight[i] = data[2 * i];
            data = eight;
            bits = 8;
        }
        return convert?.Invoke(data) ?? data;
    }

    private static string PictureFilter(PdfFilter filter, ImageSource image)
    {
        if (filter.Name == "DCTDecode")
        {
            var transform = filter.Parameters.TryGetValue("ColorTransform", out var t) ? $"<< /ColorTransform {(int)t} >> " : "";
            return transform + "/DCTDecode filter";
        }
        var parameters = new System.Text.StringBuilder("<<");
        foreach (var key in (string[])["K", "EndOfLine", "EncodedByteAlign", "Rows", "EndOfBlock", "BlackIs1", "DamagedRowsBeforeError"])
        {
            if (!filter.Parameters.TryGetValue(key, out var value))
                continue;
            var text = key is "EndOfLine" or "EncodedByteAlign" or "EndOfBlock" or "BlackIs1" ? (value != 0 ? "true" : "false") : PsWriter.Format(Math.Round(value));
            parameters.Append($" /{key} {text}");
        }
        parameters.Append($" /Columns {(int)filter.Parameter("Columns", 1728)} >>");
        _ = image;
        return parameters + " /CCITTFaxDecode filter";
    }

    /// <summary>A soft mask that is only 0 and 1 as a 1-bit mask (1 = transparent), with its size; else untranslatable.</summary>
    private static (byte[] Data, int Width, int Height) BinaryMask(ImageSource soft)
    {
        if (soft.Space?.Device != DeviceKind.Gray || soft.Bits > 8)
            throw new PsUntranslatableException("A soft mask isn't a grey picture.");
        var samples = Samples(soft, null, out var filter, out var bits);
        if (filter is not null)
            throw new PsUntranslatableException("A soft mask is compressed with a picture filter.");
        var (d0, d1) = soft.Decode is { Length: >= 2 } d ? (d[0], d[1]) : (0.0, 1.0);
        var max = (1 << bits) - 1;
        var rowBytes = (soft.Width + 7) / 8;
        var mask = new byte[rowBytes * soft.Height];
        var inRow = (int)soft.RowBytes;
        for (var y = 0; y < soft.Height; y++)
        {
            for (var x = 0; x < soft.Width; x++)
            {
                var bitAt = (long)x * bits;
                var b = samples[y * inRow + (int)(bitAt >> 3)];
                var value = (b >> (8 - bits - (int)(bitAt & 7))) & max;
                var alpha = d0 + value / (double)max * (d1 - d0);
                if (alpha is > 0.002 and < 0.998)
                    throw new PsUntranslatableException("A picture is partly transparent.");
                if (alpha < 0.5)
                    mask[y * rowBytes + x / 8] |= (byte)(0x80 >> (x % 8));
            }
        }
        return (mask, soft.Width, soft.Height);
    }

    internal static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(data);
        return output.ToArray();
    }
}
