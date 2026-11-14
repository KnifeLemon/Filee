// Small helpers for reading PDF objects through PDFsharp: references resolved, numbers of any kind, names, arrays,
// rectangles and the bytes of strings and streams. Streams are decoded by Filee's own filters with limits
// (PdfStreams), never by PDFsharp's UnfilteredValue, which inflates without a limit.

using System.Text;
using PdfSharp.Pdf;

namespace Filee.Engines.Pdf.PostScript;

/// <summary>PDF objects as plain values.</summary>
internal static class PdfObjects
{
    public static PdfItem? Resolve(PdfItem? item) => PdfCompressor.Resolve(item);

    public static PdfItem? Get(PdfDictionary? dictionary, string key) =>
        dictionary is null ? null : Resolve(dictionary.Elements[key]);

    public static PdfDictionary? Dictionary(PdfDictionary? dictionary, string key) => Get(dictionary, key) as PdfDictionary;

    public static PdfArray? Array(PdfDictionary? dictionary, string key) => Get(dictionary, key) as PdfArray;

    /// <summary>The name without its slash, or null.</summary>
    public static string? Name(PdfItem? item) => Resolve(item) is PdfName name ? name.Value.TrimStart('/') : null;

    public static string? Name(PdfDictionary? dictionary, string key) => Name(Get(dictionary, key));

    /// <summary>A number of any kind, or null. NaN and infinity count as missing.</summary>
    public static double? Number(PdfItem? item)
    {
        var value = Resolve(item) switch
        {
            PdfInteger i => i.Value,
            PdfLongInteger l => l.Value,
            PdfReal r => r.Value,
            _ => (double?)null,
        };
        return value is { } v && double.IsFinite(v) ? v : null;
    }

    public static double Number(PdfDictionary? dictionary, string key, double fallback) => Number(Get(dictionary, key)) ?? fallback;

    public static bool Boolean(PdfDictionary? dictionary, string key, bool fallback) =>
        Get(dictionary, key) is PdfBoolean b ? b.Value : fallback;

    /// <summary>The numbers of an array (missing or non-numeric elements as 0), or null when it isn't an array.</summary>
    public static double[]? Numbers(PdfItem? item) =>
        Resolve(item) is PdfArray array ? [.. array.Elements.Select(e => Number(e) ?? 0)] : null;

    public static double[]? Numbers(PdfDictionary? dictionary, string key) => Numbers(Get(dictionary, key));

    /// <summary>A rectangle as (left, bottom, right, top), normalised; null when it isn't four numbers.</summary>
    public static (double X0, double Y0, double X1, double Y1)? Rectangle(PdfItem? item)
    {
        // PDFsharp reads the page boxes as PdfRectangle, other rectangles as arrays.
        if (Resolve(item) is PdfRectangle box)
            return (Math.Min(box.X1, box.X2), Math.Min(box.Y1, box.Y2), Math.Max(box.X1, box.X2), Math.Max(box.Y1, box.Y2));
        if (Numbers(item) is not { Length: 4 } r)
            return null;
        return (Math.Min(r[0], r[2]), Math.Min(r[1], r[3]), Math.Max(r[0], r[2]), Math.Max(r[1], r[3]));
    }

    /// <summary>
    /// The bytes of a string. PDFsharp keeps a string as text: a string that starts with FE FF is read as UTF-16 and
    /// is turned back into its bytes here, every other one has one character per byte.
    /// </summary>
    public static byte[] Bytes(PdfString text)
    {
        if (text.Encoding == PdfStringEncoding.Unicode)
            return [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text.Value)];
        var value = text.Value;
        var bytes = new byte[value.Length];
        for (var i = 0; i < value.Length; i++)
            bytes[i] = (byte)value[i];
        return bytes;
    }

    /// <summary>The bytes of a string or a stream (decoded), or null.</summary>
    public static byte[]? StringOrStream(PdfItem? item, long limit) => Resolve(item) switch
    {
        PdfString s => Bytes(s),
        PdfDictionary { Stream: not null } stream => PdfStreams.Decode(stream, limit),
        _ => null,
    };
}
