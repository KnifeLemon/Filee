// Decodes PDF streams with Filee's own filters and a limit on what they unpack to: Flate (with zlib header or raw
// deflate, data before damage kept as viewers keep it), LZW (EarlyChange), ASCIIHex, ASCII85 and RunLength, and the
// PNG and TIFF predictors of Flate and LZW. The picture filters (DCT, CCITT fax, JBIG2, JPX) are left to the caller,
// with what came before them decoded. Filters and their parameters come from a stream dictionary or from an inline
// image (abbreviated names), as name → filter and numbers.

using System.IO.Compression;
using Filee.Engines.Infrastructure;
using PdfSharp.Pdf;

namespace Filee.Engines.Pdf.PostScript;

/// <summary>One filter of a stream and its parameters (numbers; true and false as 1 and 0).</summary>
internal sealed record PdfFilter(string Name, IReadOnlyDictionary<string, double> Parameters)
{
    public double Parameter(string key, double fallback) => Parameters.TryGetValue(key, out var value) ? value : fallback;

    /// <summary>Picture filters PostScript reads itself (DCT, CCITT fax) or Filee can't decode here (JBIG2, JPX).</summary>
    public bool IsPictureFilter => Name is "DCTDecode" or "CCITTFaxDecode" or "JBIG2Decode" or "JPXDecode";
}

/// <summary>Stream decoding with limits.</summary>
internal static class PdfStreams
{
    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.Ordinal)
    {
        ["Fl"] = "FlateDecode",
        ["LZW"] = "LZWDecode",
        ["AHx"] = "ASCIIHexDecode",
        ["A85"] = "ASCII85Decode",
        ["RL"] = "RunLengthDecode",
        ["DCT"] = "DCTDecode",
        ["CCF"] = "CCITTFaxDecode",
    };

    /// <summary>The filters of a stream dictionary, in the order they are applied when decoding.</summary>
    public static List<PdfFilter> Filters(PdfDictionary stream) =>
        Filters(Wrap(PdfObjects.Get(stream, "/Filter")), Wrap(PdfObjects.Get(stream, "/DecodeParms")));

    /// <summary>The filters of an inline image's parameters (<c>/F</c> and <c>/DP</c>, or their long names).</summary>
    public static List<PdfFilter> Filters(object? filter, object? parameters) =>
        Filters(Wrap(filter), Wrap(parameters));

    private static List<PdfFilter> Filters(List<object?> names, List<object?> parameters)
    {
        var filters = new List<PdfFilter>();
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i] switch
            {
                PdfName n => n.Value.TrimStart('/'),
                PdfContentLexer.Name n => n.Value,
                string s => s,
                _ => throw new InvalidDataException("A stream filter isn't a name."),
            };
            name = Abbreviations.GetValueOrDefault(name, name);
            filters.Add(new PdfFilter(name, Parameters(i < parameters.Count ? parameters[i] : null)));
        }
        if (filters.Count > 8)
            throw new InvalidDataException("A stream has too many filters.");
        return filters;
    }

    private static List<object?> Wrap(object? item) => item switch
    {
        null => [],
        PdfNull => [],
        PdfArray array => [.. array.Elements.Select(e => (object?)PdfObjects.Resolve(e))],
        List<object?> list => list,
        _ => [item],
    };

    private static Dictionary<string, double> Parameters(object? item)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        switch (item)
        {
            case PdfDictionary dictionary:
                foreach (var key in dictionary.Elements.Keys)
                {
                    var value = PdfObjects.Resolve(dictionary.Elements[key]);
                    if (value is PdfBoolean b)
                        result[key.TrimStart('/')] = b.Value ? 1 : 0;
                    else if (PdfObjects.Number(value) is { } n)
                        result[key.TrimStart('/')] = n;
                }
                break;
            case Dictionary<string, object?> inline:
                foreach (var (key, value) in inline)
                {
                    if (value is double n)
                        result[key] = n;
                    else if (value is PdfContentLexer.Operator { Name: "true" or "false" } b)
                        result[key] = b.Name == "true" ? 1 : 0;
                }
                break;
        }
        return result;
    }

    /// <summary>Decodes every filter of <paramref name="stream"/>; a picture filter is refused.</summary>
    /// <exception cref="InvalidDataException">The data is damaged beyond use, or unpacks to more than <paramref name="limit"/> bytes.</exception>
    public static byte[] Decode(PdfDictionary stream, long limit)
    {
        var (data, rest) = DecodeGeneral(stream.Stream.Value ?? [], Filters(stream), limit);
        if (rest.Count > 0)
            throw new InvalidDataException($"The stream uses the {rest[0].Name} filter where it isn't expected.");
        return data;
    }

    /// <summary>
    /// Decodes the filters before the first picture filter; returns the data and the filters left (the picture filter
    /// and whatever follows it).
    /// </summary>
    public static (byte[] Data, List<PdfFilter> Remaining) DecodeGeneral(byte[] raw, IReadOnlyList<PdfFilter> filters, long limit)
    {
        var data = raw;
        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters[i];
            if (filter.IsPictureFilter)
                return (data, [.. filters.Skip(i)]);
            data = filter.Name switch
            {
                "FlateDecode" => Predict(Inflate(data, limit), filter, limit),
                "LZWDecode" => Predict(Lzw(data, filter.Parameter("EarlyChange", 1) != 0, limit), filter, limit),
                "ASCIIHexDecode" => AsciiHex(data),
                "ASCII85Decode" => Ascii85(data, limit),
                "RunLengthDecode" => RunLength(data, limit),
                "Crypt" => data, // PDFsharp decrypted the stream when it opened the document
                _ => throw new InvalidDataException($"The {filter.Name} filter isn't supported."),
            };
        }
        return (data, []);
    }

    /// <summary>Inflates zlib data (or raw deflate); what came before damage is kept, as viewers keep it.</summary>
    public static byte[] Inflate(byte[] data, long limit)
    {
        var withHeader = data.Length >= 2 && (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0;
        var result = Inflate(data, withHeader, limit);
        if (result.Length == 0 && withHeader)
            result = Inflate(data, false, limit);
        return result;
    }

    private static byte[] Inflate(byte[] data, bool withHeader, long limit)
    {
        using var output = new MemoryStream();
        try
        {
            using var input = new MemoryStream(data);
            using Stream inflater = withHeader ? new ZLibStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
            using var limited = new LimitedStream(inflater, limit, () => Exceeded(limit));
            limited.CopyTo(output);
        }
        catch (InvalidDataException) when (output.Length > 0 && output.Length <= limit)
        {
            // damaged after some data: keep what came out
        }
        catch (InvalidDataException) when (output.Length == 0)
        {
            return [];
        }
        return output.ToArray();
    }

    private static InvalidDataException Exceeded(long limit) =>
        new($"A stream of the PDF unpacks to more than {limit / (1024 * 1024)} MB.");

    private static byte[] Lzw(byte[] data, bool earlyChange, long limit)
    {
        var output = new MemoryStream();
        var table = new List<byte[]>(4096);
        void Reset()
        {
            table.Clear();
            for (var i = 0; i < 256; i++)
                table.Add([(byte)i]);
            table.Add([]); // 256 clear
            table.Add([]); // 257 end
        }
        Reset();
        int bits = 9, buffer = 0, held = 0;
        byte[]? previous = null;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            held += 8;
            while (held >= bits)
            {
                var code = (buffer >> (held - bits)) & ((1 << bits) - 1);
                held -= bits;
                if (code == 256)
                {
                    Reset();
                    bits = 9;
                    previous = null;
                    continue;
                }
                if (code == 257)
                    return output.ToArray();
                byte[] entry;
                if (code < table.Count)
                    entry = table[code];
                else if (code == table.Count && previous is not null)
                    entry = [.. previous, previous[0]];
                else
                    return output.ToArray(); // damaged: keep what came out
                output.Write(entry);
                if (output.Length > limit)
                    throw Exceeded(limit);
                if (previous is not null && table.Count < 4096)
                    table.Add([.. previous, entry[0]]);
                previous = entry;
                var next = table.Count + (earlyChange ? 1 : 0);
                bits = next >= 2048 ? 12 : next >= 1024 ? 11 : next >= 512 ? 10 : 9;
            }
        }
        return output.ToArray();
    }

    private static byte[] AsciiHex(byte[] data)
    {
        var output = new List<byte>(data.Length / 2);
        var high = -1;
        foreach (var b in data)
        {
            if (b == '>')
                break;
            var digit = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                _ => -1,
            };
            if (digit < 0)
                continue;
            if (high < 0)
            {
                high = digit;
            }
            else
            {
                output.Add((byte)(high << 4 | digit));
                high = -1;
            }
        }
        if (high >= 0)
            output.Add((byte)(high << 4));
        return [.. output];
    }

    private static byte[] Ascii85(byte[] data, long limit)
    {
        var output = new MemoryStream();
        Span<int> group = stackalloc int[5];
        var count = 0;
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b == '~')
                break;
            if (b == 'z' && count == 0)
            {
                output.Write([0, 0, 0, 0]);
            }
            else if (b is >= (byte)'!' and <= (byte)'u')
            {
                group[count++] = b - '!';
                if (count == 5)
                {
                    WriteGroup(output, group, 4);
                    count = 0;
                }
            }
            if (output.Length > limit)
                throw Exceeded(limit);
        }
        if (count > 1)
        {
            for (var i = count; i < 5; i++)
                group[i] = 84;
            WriteGroup(output, group, count - 1);
        }
        return output.ToArray();

        static void WriteGroup(MemoryStream output, Span<int> group, int bytes)
        {
            long value = 0;
            foreach (var digit in group)
                value = value * 85 + digit;
            for (var i = 0; i < bytes; i++)
                output.WriteByte((byte)(value >> (24 - 8 * i)));
        }
    }

    private static byte[] RunLength(byte[] data, long limit)
    {
        var output = new MemoryStream();
        for (var i = 0; i < data.Length;)
        {
            int length = data[i++];
            if (length == 128)
                break;
            if (length < 128)
            {
                var n = Math.Min(length + 1, data.Length - i);
                output.Write(data, i, n);
                i += n;
            }
            else if (i < data.Length)
            {
                var value = data[i++];
                for (var k = 0; k < 257 - length; k++)
                    output.WriteByte(value);
            }
            if (output.Length > limit)
                throw Exceeded(limit);
        }
        return output.ToArray();
    }

    /// <summary>Undoes the PNG (10–15) or TIFF (2) predictor of Flate / LZW data.</summary>
    private static byte[] Predict(byte[] data, PdfFilter filter, long limit)
    {
        var predictor = (int)filter.Parameter("Predictor", 1);
        if (predictor <= 1)
            return data;
        var colors = Math.Clamp((int)filter.Parameter("Colors", 1), 1, 32);
        var bpc = (int)filter.Parameter("BitsPerComponent", 8);
        if (bpc is not (1 or 2 or 4 or 8 or 16))
            throw new InvalidDataException("A predictor has an unusual number of bits per component.");
        var columns = (long)filter.Parameter("Columns", 1);
        if (columns < 1 || columns * colors * bpc > 8L * 1024 * 1024 * 8)
            throw new InvalidDataException("A predictor has an unusual number of columns.");
        var stride = (int)((columns * colors * bpc + 7) / 8);
        var bpp = Math.Max(1, colors * bpc / 8);
        if (predictor == 2)
        {
            if (bpc != 8)
                return data; // TIFF prediction of other depths is rare; leave the data as it is
            var result = (byte[])data.Clone();
            for (var row = 0; row * stride < result.Length; row++)
            {
                var start = row * stride;
                for (var i = bpp; i < stride && start + i < result.Length; i++)
                    result[start + i] += result[start + i - bpp];
            }
            return result;
        }
        var rows = data.Length / (stride + 1);
        var output = new byte[(long)rows * stride];
        if (output.LongLength > limit)
            throw Exceeded(limit);
        var prior = new byte[stride];
        for (var row = 0; row < rows; row++)
        {
            var type = data[row * (stride + 1)];
            var line = output.AsSpan(row * stride, stride);
            data.AsSpan(row * (stride + 1) + 1, stride).CopyTo(line);
            for (var i = 0; i < stride; i++)
            {
                int left = i >= bpp ? line[i - bpp] : 0, up = prior[i], upLeft = i >= bpp ? prior[i - bpp] : 0;
                line[i] = (byte)(type switch
                {
                    1 => line[i] + left,
                    2 => line[i] + up,
                    3 => line[i] + (left + up) / 2,
                    4 => line[i] + Paeth(left, up, upLeft),
                    _ => line[i],
                });
            }
            line.CopyTo(prior);
        }
        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
