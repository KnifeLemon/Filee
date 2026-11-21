// PDFsharp unpacks Flate and LZW streams into memory with no limit: the object and cross-reference streams of a file
// while PdfReader.Open reads it, a page's content streams when the page's content is read, drawn on or copied, and
// whatever Filee asks for with UnfilteredValue (fonts, CMaps, metadata). A few kilobytes of deflated zeros unpack to
// gigabytes, and the file never says which streams PDFsharp will pick: the cross-reference data names the object
// streams, and PDFsharp unpacks the stream at "startxref" before it looks at its dictionary.
//
// PDFsharp takes its decoders from static fields of Filtering. Install puts Filee's in their place: they unpack the
// same bytes the same way (the deflate data after the two-byte zlib header, PDFsharp's LZW, its PNG predictors, its
// ASCII85) but refuse a stream that unpacks to more than DocumentLimits.MaxPdfStreamBytes with
// error.document_too_large. Open runs PDFsharp's open inside a budget, MaxPackageBytes for every stream the file
// unpacks, and unpacks the pages' content streams there once, so a file whose pages would only pass the limits
// together when they are drawn later is refused when it is opened. PDFsharp swallows some errors while it opens a
// file; a refusal is remembered and thrown after the open all the same.
//
// PDFsharp stays at 6.2.4 (newer versions are AGPL), so its private fields don't move; a test checks they are replaced.

using System.IO.Compression;
using System.Reflection;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Filters;

namespace Filee.Engines.Pdf;

/// <summary>Bounds what PDFsharp unpacks from a PDF's streams.</summary>
internal static class PdfStreamLimits
{
    private static readonly Lazy<bool> Installed = new(InstallDecoders, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>PDFsharp's predictor step (internal to it), used as its Flate and LZW filters use it.</summary>
    private static readonly Func<byte[], PdfDictionary, byte[]> Predictors =
        (typeof(Filter).Assembly.GetType("PdfSharp.Pdf.Filters.StreamDecoder", throwOnError: true)!
            .GetMethod("Decode", BindingFlags.Public | BindingFlags.Static, [typeof(byte[]), typeof(PdfDictionary)])
            ?? throw new MissingMethodException("PdfSharp.Pdf.Filters.StreamDecoder", "Decode"))
        .CreateDelegate<Func<byte[], PdfDictionary, byte[]>>();

    [ThreadStatic]
    private static Budget? t_budget;

    /// <summary>Puts Filee's decoders in PDFsharp's place (once; every later call returns at once).</summary>
    public static void Install() => _ = Installed.Value;

    /// <summary>True when PDFsharp unpacks through Filee's decoders.</summary>
    internal static bool IsInstalled =>
        Installed.IsValueCreated && Filtering.FlateDecode is BoundedFlate && Filtering.LzwDecode is BoundedLzw && Filtering.Ascii85Decode is BoundedAscii85;

    private static bool InstallDecoders()
    {
        Replace("_flateDecode", new BoundedFlate());
        Replace("_lzwDecode", new BoundedLzw());
        Replace("_ascii85Decode", new BoundedAscii85());
        return true;

        static void Replace(string name, Filter filter) =>
            (typeof(Filtering).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
             ?? throw new MissingFieldException(nameof(Filtering), name)).SetValue(null, filter);
    }

    /// <summary>
    /// Opens a PDF with <paramref name="open"/> (a PDFsharp open) inside a budget for the streams it unpacks, then
    /// unpacks each page's content streams once inside the same budget.
    /// </summary>
    /// <exception cref="ConversionRefusedException">A stream, or all of them, unpack to more than the limits.</exception>
    public static PdfDocument Open(Func<PdfDocument> open, CancellationToken cancellationToken,
        long? perStream = null, long? total = null)
    {
        PdfDocument? document = null;
        try
        {
            return Run(() =>
            {
                document = open();
                ReadContents(document, cancellationToken);
                return document;
            }, cancellationToken, perStream, total);
        }
        catch
        {
            document?.Dispose();
            throw;
        }
    }

    /// <summary>Runs <paramref name="action"/> with a budget for what PDFsharp unpacks on this thread meanwhile.</summary>
    /// <exception cref="ConversionRefusedException">A stream, or all of them, unpack to more than the limits.</exception>
    public static T Run<T>(Func<T> action, CancellationToken cancellationToken, long? perStream = null, long? total = null)
    {
        Install();
        var limits = DocumentLimits.Default;
        var budget = new Budget(perStream ?? limits.MaxPdfStreamBytes, total ?? limits.MaxPackageBytes, cancellationToken);
        var outer = t_budget;
        t_budget = budget;
        try
        {
            var result = action();
            if (budget.Refusal is { } refusal)
                throw refusal; // PDFsharp caught it on the way
            return result;
        }
        catch (Exception ex) when (budget.Refusal is { } refusal && !ReferenceEquals(ex, refusal) && ex is not OperationCanceledException)
        {
            throw refusal; // PDFsharp caught it and failed in another way
        }
        finally
        {
            t_budget = outer;
        }
    }

    /// <summary>Unpacks the streams of every page's /Contents once (they count against the budget).</summary>
    private static void ReadContents(PdfDocument document, CancellationToken cancellationToken)
    {
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var item in document.Internals.GetAllObjects())
        {
            if (item is not PdfDictionary dictionary || dictionary.Elements["/Contents"] is not { } contents)
                continue;
            foreach (var stream in Streams(contents))
            {
                if (!seen.Add(stream))
                    continue;
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    _ = stream.Stream.UnfilteredValue;
                }
                catch (Exception ex) when (ex is not ConversionRefusedException and not OperationCanceledException)
                {
                    // A filter PDFsharp can't unpack: it fails the same way when the page is read.
                }
            }
        }
    }

    /// <summary>The streams a /Contents entry names: one, or an array of them, directly or by reference.</summary>
    private static IEnumerable<PdfDictionary> Streams(PdfItem item)
    {
        switch (item is PdfReference reference ? reference.Value : item)
        {
            case PdfDictionary { Stream: not null } stream:
                yield return stream;
                break;
            case PdfArray array:
                foreach (var element in array.Elements)
                {
                    if ((element is PdfReference part ? part.Value : element) is PdfDictionary { Stream: not null } partStream)
                        yield return partStream;
                }
                break;
        }
    }

    // ───────────────────────── Budget ─────────────────────────

    /// <summary>What may be unpacked on this thread; outside a budget, one stream's limit only.</summary>
    private sealed class Budget(long perStream, long total, CancellationToken cancellationToken)
    {
        public long PerStream { get; } = perStream;

        public long Total { get; } = total;

        public long Spent { get; set; }

        public CancellationToken Token { get; } = cancellationToken;

        public ConversionRefusedException? Refusal { get; set; }
    }

    /// <summary>Bytes the next stream may unpack to.</summary>
    private static long Allowed => t_budget is { } budget
        ? Math.Max(0, Math.Min(budget.PerStream, budget.Total - budget.Spent))
        : DocumentLimits.Default.MaxPdfStreamBytes;

    /// <summary>Counts <paramref name="bytes"/> unpacked against the budget.</summary>
    private static void Spend(long bytes)
    {
        if (t_budget is { } budget)
            budget.Spent += bytes;
    }

    private static void ThrowIfCancelled() => t_budget?.Token.ThrowIfCancellationRequested();

    /// <summary>The refusal for a stream that unpacks to more than <see cref="Allowed"/>; remembered for after PDFsharp.</summary>
    private static ConversionRefusedException TooLarge()
    {
        var budget = t_budget;
        var perStream = budget?.PerStream ?? DocumentLimits.Default.MaxPdfStreamBytes;
        var refusal = budget is not null && budget.Total - budget.Spent < perStream
            ? DocumentLimits.TooLarge($"The PDF's streams unpack to more than {DocumentLimits.Megabytes(budget.Total)} together.")
            : DocumentLimits.TooLarge($"A stream of the PDF unpacks to more than {DocumentLimits.Megabytes(perStream)}.");
        if (budget is not null)
            budget.Refusal ??= refusal;
        return refusal;
    }

    // ───────────────────────── Decoders ─────────────────────────

    /// <summary>PDFsharp's FlateDecode, with a limit: deflate after the two-byte zlib header, then the predictor.</summary>
    private sealed class BoundedFlate : FlateDecode
    {
        public override byte[] Decode(byte[] data, FilterParms? parms)
        {
            var allowed = Allowed;
            using var input = new MemoryStream(data);
            input.ReadByte();
            input.ReadByte();
            using var output = new MemoryStream();
            using (var inflater = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > allowed)
                        throw TooLarge();
                    output.Write(buffer, 0, read);
                    if ((output.Length & ((1 << 20) - 1)) < read)
                        ThrowIfCancelled();
                }
            }
            output.Capacity = (int)output.Length;
            var unpacked = output.GetBuffer();
            Spend(unpacked.Length);
            return parms?.DecodeParms is { } decodeParms ? Predict(unpacked, decodeParms) : unpacked;
        }
    }

    /// <summary>PDFsharp's LZWDecode, run once what it unpacks to is known to be within the limit.</summary>
    private sealed class BoundedLzw : LzwDecode
    {
        public override byte[] Decode(byte[] data, FilterParms? parms)
        {
            if (LzwSize(data, Allowed) is null)
                throw TooLarge();
            ThrowIfCancelled();
            var unpacked = new LzwDecode().Decode(data, (FilterParms?)null); // a new one each time: PDFsharp's keeps its state in fields
            Spend(unpacked.Length);
            return parms?.DecodeParms is { } decodeParms ? Predict(unpacked, decodeParms) : unpacked;
        }
    }

    /// <summary>PDFsharp's ASCII85Decode ("z" stands for four bytes), once its output is known to fit.</summary>
    private sealed class BoundedAscii85 : Ascii85Decode
    {
        public override byte[] Decode(byte[] data, FilterParms? parms)
        {
            if (Ascii85Bound(data) > Allowed)
                throw TooLarge();
            var unpacked = base.Decode(data, parms);
            Spend(unpacked.Length);
            return unpacked;
        }
    }

    /// <summary>
    /// PDFsharp's PNG predictors, except for data shorter than one row, which PDFsharp fails on (or, when there is none,
    /// makes nothing of) only after allocating two rows of the size /Columns gives: hundreds of megabytes for a few bytes.
    /// </summary>
    private static byte[] Predict(byte[] data, PdfDictionary parms)
    {
        var elements = parms.Elements;
        var predictor = elements.GetInteger("/Predictor");
        var colors = Math.Max(1, elements.GetInteger("/Colors"));
        var bits = elements.GetInteger("/BitsPerComponent") is var b and >= 1 ? b : 8;
        var columns = Math.Max(1, elements.GetInteger("/Columns"));
        if (predictor is >= 10 and <= 15 && bits is 1 or 2 or 4 or 8 or 16)
        {
            var rowBytes = unchecked((bits * colors * columns + 7) / 8); // as PDFsharp works it out, overflow included
            if (rowBytes > 0 && data.Length < rowBytes + 1)
                return data.Length == 0 ? [] : throw new ArgumentException("The stream's data is shorter than one row of its predictor.");
        }
        return Predictors(data, parms);
    }

    /// <summary>
    /// What PDFsharp's LZW decoder writes for <paramref name="data"/> (its table, code widths and the errors it stops
    /// at, lengths only); null once that passes <paramref name="limit"/>.
    /// </summary>
    internal static long? LzwSize(byte[] data, long limit)
    {
        if (data.Length == 0 || data[0] == 0 && (data.Length < 2 || data[1] == 1))
            return 0; // PDFsharp fails before it writes anything ("LZW flavor not supported")
        var lengths = new int[8192]; // 0 for the codes PDFsharp has no string for
        void Reset(ref int next, ref int width)
        {
            Array.Clear(lengths);
            lengths.AsSpan(0, 256).Fill(1);
            next = 258;
            width = 9;
        }
        int tableIndex = 0, bitsToGet = 0, bytePointer = 0, nextData = 0, nextBits = 0;
        Reset(ref tableIndex, ref bitsToGet);
        int NextCode()
        {
            if (bytePointer >= data.Length)
                return 257;
            nextData = nextData << 8 | data[bytePointer++];
            nextBits += 8;
            if (nextBits < bitsToGet)
            {
                if (bytePointer >= data.Length)
                    return 257;
                nextData = nextData << 8 | data[bytePointer++];
                nextBits += 8;
            }
            var code = (nextData >> ((nextBits - bitsToGet) & 31)) & ((1 << bitsToGet) - 1);
            nextBits -= bitsToGet;
            return code;
        }
        // False where PDFsharp's table is full and it fails.
        bool Add(int length)
        {
            if (tableIndex >= lengths.Length)
                return false;
            lengths[tableIndex++] = length;
            bitsToGet = tableIndex switch { 511 => 10, 1023 => 11, 2047 => 12, _ => bitsToGet };
            return true;
        }

        long written = 0;
        var oldCode = 0;
        int code;
        while ((code = NextCode()) != 257)
        {
            if (code == 256)
            {
                Reset(ref tableIndex, ref bitsToGet);
                code = NextCode();
                if (code == 257)
                    break;
                if (code >= 256)
                    return written; // no string for it: PDFsharp fails
                written++;
                oldCode = code;
                continue;
            }
            var length = code < tableIndex ? lengths[code] : lengths[oldCode];
            if (length == 0 || lengths[oldCode] == 0)
                return written;
            written += length;
            if (written > limit)
                return null;
            if (!Add(lengths[oldCode] + 1))
                return written;
            oldCode = code;
        }
        return written;
    }

    /// <summary>The most an ASCII85 decoder writes for <paramref name="data"/>: 4 bytes per "z" or "y", 4 per 5 digits.</summary>
    internal static long Ascii85Bound(byte[] data)
    {
        long words = 0, digits = 0;
        foreach (var b in data)
        {
            if (b is (byte)'z' or (byte)'y')
                words++;
            else if (b is >= (byte)'!' and <= (byte)'u')
                digits++;
        }
        return 4 * words + 4 * (digits / 5) + 4;
    }
}
