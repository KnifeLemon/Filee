// The picture an embedded OLE object (한글 OLE 개체: a Paint bitmap, an Excel sheet, a chart, ...) shows on the page.
// The object is a compound file (in 한글's BinData, after a 4-byte length), and the program that made it left a
// picture of it there for programs that can't run it (MS-OLEDS 2.3.4, OLEPresentationStream "\x02OlePres000"): a
// Windows metafile (CF_METAFILEPICT) or a device-independent bitmap (CF_DIB). An object without one may still be a
// Paint bitmap whose native data ("\x01Ole10Native") is the BMP file itself.
// A 한글 chart is such an object (HWP 5.0 format 4.3.9.6): since 한글 2018 its storage holds the chart as a DrawingML
// chart part ("OOXMLChartContents", a c:chartSpace) beside "Contents", the older chart (한글 Neo's object tree, whose
// binary layout Hancom's chart format document leaves out); Read gives that part out for ChartReader. An object is
// taken for a chart when it has that stream, or when its CompObj stream (MS-OLEDS 2.3.8: its user type and program
// id) names a chart (한글's, MS Graph's, Excel's); such a chart without the part is drawn from its picture.
//
// A document's objects share one budget (Budget): what all their compound files give out counts against the
// document's MaxPackageBytes together, and an object named again gives the picture it gave before without a new read.

using System.Buffers.Binary;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Binary;

internal static class OlePreview
{
    private const uint MetafilePicture = 3; // CF_METAFILEPICT
    private const uint DeviceIndependentBitmap = 8; // CF_DIB

    /// <summary>What Filee reads of an OLE object: its picture (a file), its DrawingML chart part, whether it is a chart.</summary>
    internal sealed record Content(string? Preview, byte[]? ChartPart, bool IsChart);

    /// <summary>
    /// Writes the picture of the OLE object in <paramref name="ole"/> next to <paramref name="target"/> (its extension
    /// replaced by .wmf or .bmp) and returns that file; null when the object holds no picture Filee can draw.
    /// </summary>
    public static string? Extract(string ole, string target, DocumentLimits? limits = null) => Read(ole, target, limits, null).Preview;

    /// <summary>The object's picture (written as <see cref="Extract(string, string, DocumentLimits?)"/> writes it), its chart part and whether it is a chart.</summary>
    public static Content Read(string ole, string target, DocumentLimits? limits = null) => Read(ole, target, limits, null);

    /// <param name="counted">Receives the bytes read: the object's file and each stream of it.</param>
    private static Content Read(string ole, string target, DocumentLimits? limits, Action<long>? counted)
    {
        var none = new Content(null, null, false);
        try
        {
            if (new FileInfo(ole).Length > (limits ?? DocumentLimits.Default).MaxPartBytes)
                return none; // larger than any part may be
            var bytes = File.ReadAllBytes(ole);
            counted?.Invoke(bytes.Length);
            // 한글 keeps the object's length before the compound file.
            var start = HasSignature(bytes, 0) ? 0 : HasSignature(bytes, 4) ? 4 : -1;
            if (start < 0)
                return none;
            using var stream = new MemoryStream(bytes, start, bytes.Length - start, writable: false);
            using var file = CompoundFile.Open(stream, limits);
            var chart = file.Streams.Contains("OOXMLChartContents", StringComparer.OrdinalIgnoreCase) ? file.Read("OOXMLChartContents") : null;
            counted?.Invoke(chart?.Length ?? 0);
            var isChart = chart is not null || NamesChart(file, counted);
            if (Presentation(file, counted) is var (data, wmf))
                return new Content(Write(target, wmf ? ".wmf" : ".bmp", data), chart, isChart);
            var native = file.Read("\u0001Ole10Native");
            counted?.Invoke(native?.Length ?? 0);
            if (native is { Length: > 6 } && native.AsSpan(4, 2).SequenceEqual("BM"u8))
                return new Content(Write(target, ".bmp", native.AsSpan(4).ToArray()), chart, isChart);
            return new Content(null, chart, isChart);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or Core.Conversion.ConversionRefusedException)
        {
            return none;
        }
    }

    /// <summary>
    /// Whether the object's CompObj stream (its user type, clipboard format and program id: "HwpChart", "MSGraph.Chart.8",
    /// "Excel.Chart.8", ...) names a chart.
    /// </summary>
    private static bool NamesChart(CompoundFile file, Action<long>? counted)
    {
        if (!file.Streams.Contains("\u0001CompObj", StringComparer.Ordinal) || file.Read("\u0001CompObj") is not { } compObj)
            return false;
        counted?.Invoke(compObj.Length);
        // The names are ANSI strings after a 28-byte header; a stream of a few hundred bytes.
        var text = System.Text.Encoding.Latin1.GetString(compObj, 0, Math.Min(compObj.Length, 4096));
        return text.Contains("chart", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSignature(byte[] bytes, int at) =>
        bytes.Length >= at + 8 && bytes.AsSpan(at, 8).SequenceEqual((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);

    /// <summary>The first presentation stream's picture: a metafile (WMF records) or a BMP file made of its DIB.</summary>
    private static (byte[] Data, bool Wmf)? Presentation(CompoundFile file, Action<long>? counted)
    {
        foreach (var name in file.Streams.Where(s => s.StartsWith("\u0002OlePres", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var data = file.Read(name);
            counted?.Invoke(data?.Length ?? 0);
            if (data is null || data.Length < 8)
                continue;
            var span = data.AsSpan();
            // AnsiClipboardFormat: 0xFFFFFFFF and a standard format; a format by name (a length) isn't drawn.
            if (BinaryPrimitives.ReadUInt32LittleEndian(span) != 0xFFFFFFFF)
                continue;
            var format = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
            var at = 8;
            // TargetDeviceSize counts itself; a target device follows when it is more than 4.
            if (span.Length < at + 4)
                continue;
            var device = BinaryPrimitives.ReadUInt32LittleEndian(span[at..]);
            if (device < 4 || device > span.Length - at)
                continue;
            at += (int)device;
            // Aspect, Lindex, Advf, Reserved1, Width, Height, Size.
            if (span.Length < at + 28)
                continue;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 24)..]);
            at += 28;
            if (size == 0 || size > span.Length - at)
                continue;
            var picture = span.Slice(at, (int)size);
            if (format == MetafilePicture && Metafiles.WmfHeader.IsMetaHeader(picture, 0))
                return (picture.ToArray(), true);
            if (format == DeviceIndependentBitmap && Bitmap(picture) is { } bmp)
                return (bmp, false);
        }
        return null;
    }

    /// <summary>A BMP file of a packed DIB: the 14-byte file header, then the DIB with its bits after its colour table.</summary>
    private static byte[]? Bitmap(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40)
            return null;
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        if (headerSize < 12 || headerSize > dib.Length)
            return null;
        var bitCount = headerSize == 12 ? BinaryPrimitives.ReadUInt16LittleEndian(dib[10..]) : BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = headerSize >= 40 ? BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]) : 0;
        var used = headerSize >= 40 ? BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]) : 0;
        long colors = bitCount <= 8 ? (used > 0 ? used : 1u << bitCount) : used;
        long entry = headerSize == 12 ? 3 : 4;
        long masks = headerSize == 40 && compression == 3 ? 12 : 0; // BI_BITFIELDS after a BITMAPINFOHEADER
        var offset = 14 + headerSize + masks + colors * entry;
        if (offset > 14 + dib.Length)
            return null;
        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), (uint)offset);
        dib.CopyTo(bmp.AsSpan(14));
        return bmp;
    }

    private static string Write(string target, string extension, byte[] data)
    {
        var path = Path.ChangeExtension(target, extension);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>
    /// The previews of one document: every object's compound file may give out only what is left of the document's
    /// <see cref="DocumentLimits.MaxPackageBytes"/>, and each object is read once. Once the budget is spent the other
    /// objects have no picture, like an object whose picture can't be read.
    /// </summary>
    internal sealed class Budget(DocumentLimits limits)
    {
        private readonly Dictionary<string, Content> _done = new(StringComparer.Ordinal);
        private long _left = limits.MaxPackageBytes;

        /// <summary>Bytes the previews may still read.</summary>
        public long Left => _left;

        /// <summary>The picture of the object in <paramref name="ole"/>, as <see cref="OlePreview.Extract(string, string, DocumentLimits?)"/> gives it.</summary>
        public string? Extract(string ole, string target) => Read(ole, target).Preview;

        /// <summary>What <see cref="OlePreview.Read(string, string, DocumentLimits?)"/> gives of the object in <paramref name="ole"/>.</summary>
        public Content Read(string ole, string target)
        {
            if (_done.TryGetValue(ole, out var known))
                return known;
            var content = new Content(null, null, false);
            if (_left > 0)
            {
                var read = 0L;
                content = OlePreview.Read(ole, target, limits with { MaxPackageBytes = _left }, bytes => read += bytes);
                // An object refused part way still spent what it read.
                _left = read >= _left ? 0 : _left - read;
            }
            _done[ole] = content;
            return content;
        }
    }
}
