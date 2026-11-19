// PDFsharp's object parser reads arrays and dictionaries recursively, with no limit, and PdfReader.Open reads every
// object of a file at once, the ones packed in object streams and the ones nothing refers to included: about 1,900
// "[" in one object (a few kilobytes) overflow a 1 MB stack, and a stack overflow ends the process with every
// conversion in it. Every PDFsharp open of a file Filee did not write itself goes through Open here, which reads the
// file first and refuses one nested more than MaxDepth deep with error.document_too_complex. PdfContentNesting does
// the same for content streams before PDFsharp's ContentReader.
//
// PDFsharp jumps to each object from the cross-reference table, so reading the file from front to back is not enough:
// a string that never closes, or the data of a stream, would hide an object from such a reading while PDFsharp still
// parses it. The check therefore reads from every "obj" and "trailer" keyword in the file, wherever it is (in a string,
// in stream data), the way PDFsharp's parser reads from there: strings with balanced parentheses and escapes,
// comments, hex strings and names, up to the end of the object (a keyword such as endobj or stream at the top level).
// Readings overlap only in damaged or crafted files; the bytes read in all are capped at a few times the file.
//
// Object streams hold objects without an "obj" keyword. PDFsharp finds them through the cross-reference stream and
// doesn't need /Type /ObjStm, so every stream whose dictionary has /N is unpacked (with PDFsharp's own filters, which
// it also applies without decrypting first, so the check sees exactly what the parser gets) and each object read from
// the offset the stream's header gives.

using System.Globalization;
using System.Text;
using Filee.Engines.Documents;
using Microsoft.Win32.SafeHandles;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Filters;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Pdf;

/// <summary>Opens PDFs with PDFsharp once their arrays and dictionaries are known to nest few enough levels.</summary>
internal static class PdfObjectNesting
{
    /// <summary>
    /// Arrays and dictionaries inside each other at most. Real files nest a handful of levels (a page tree, an outline
    /// and a structure tree link their entries by reference); PDFsharp overflowed a 1 MB stack at about 1,900.
    /// </summary>
    public const int MaxDepth = 256;

    /// <summary>How many times its size (and its unpacked object streams) a file may be read in all.</summary>
    private const int MaxReadsPerByte = 4;

    /// <summary>What may always be read, whatever the file's size.</summary>
    private const long ReadFloor = 16L * 1024 * 1024;

    /// <summary>Opens <paramref name="path"/> with PDFsharp after checking it.</summary>
    /// <exception cref="Core.Conversion.ConversionRefusedException">The file nests too deeply.</exception>
    public static PdfDocument Open(string path, PdfDocumentOpenMode mode, CancellationToken cancellationToken = default)
    {
        Check(path, cancellationToken);
        return PdfReader.Open(path, mode);
    }

    /// <summary>Opens a PDF in memory with PDFsharp after checking it.</summary>
    /// <exception cref="Core.Conversion.ConversionRefusedException">The file nests too deeply.</exception>
    public static PdfDocument Open(byte[] pdf, PdfDocumentOpenMode mode, CancellationToken cancellationToken = default)
    {
        Check(pdf, cancellationToken);
        return PdfReader.Open(new MemoryStream(pdf), mode);
    }

    /// <summary>Checks the PDF at <paramref name="path"/>, reading it in windows (it is not loaded whole).</summary>
    /// <exception cref="Core.Conversion.ConversionRefusedException">The file nests too deeply.</exception>
    public static void Check(string path, CancellationToken cancellationToken = default)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        new Checker(new Source(file), cancellationToken).Run();
    }

    /// <summary>Checks a PDF in memory.</summary>
    /// <exception cref="Core.Conversion.ConversionRefusedException">The file nests too deeply.</exception>
    public static void Check(byte[] pdf, CancellationToken cancellationToken = default) =>
        new Checker(new Source(pdf), cancellationToken).Run();

    private static Core.Conversion.ConversionRefusedException TooDeep() =>
        DocumentLimits.Exceeded($"The PDF nests arrays or dictionaries more than {MaxDepth} deep.");

    // ───────────────────────── The check ─────────────────────────

    private sealed class Checker(Source file, CancellationToken cancellationToken)
    {
        /// <summary>Where the value of each numbered object starts (after "N G obj"); more than one after an update.</summary>
        private readonly Dictionary<long, List<long>> _objects = [];

        private long _read;
        private long _allowed = file.Length * MaxReadsPerByte + ReadFloor;

        public void Run()
        {
            var starts = new List<long>();
            Keywords("obj"u8, starts, numbered: true);
            Keywords("trailer"u8, starts, numbered: false);
            starts.Sort();
            foreach (var start in starts)
            {
                var (streamAt, hasN) = Read(file, start);
                if (streamAt >= 0 && hasN)
                    ObjectStream(start, streamAt);
            }
        }

        /// <summary>Every <paramref name="keyword"/> standing as a token of its own, with where the reading starts after it.</summary>
        private void Keywords(ReadOnlySpan<byte> keyword, List<long> starts, bool numbered)
        {
            for (var at = file.IndexOf(keyword, 0); at >= 0; at = file.IndexOf(keyword, at + 1))
            {
                if (!Ends(file[at - 1]) || !Ends(file[at + keyword.Length]))
                    continue;
                var start = at + keyword.Length;
                starts.Add(start);
                if (numbered && Number(at) is { } number)
                {
                    if (!_objects.TryGetValue(number, out var list))
                        _objects[number] = list = [];
                    list.Add(start);
                }
                if ((starts.Count & 0xFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
            }
        }

        /// <summary>The object number of "N G obj" whose keyword is at <paramref name="keyword"/>.</summary>
        private long? Number(long keyword)
        {
            var at = keyword - 1;
            return Digits(ref at, out _, out _) && Digits(ref at, out var from, out var to)
                && long.TryParse(Text(file, from, to - from), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;
        }

        /// <summary>Steps back over white space and then up to 12 digits, which are at [from, to); false without any digit.</summary>
        private bool Digits(ref long at, out long from, out long to)
        {
            while (at >= 0 && IsSpace(file[at]))
                at--;
            to = at + 1;
            while (at >= 0 && file[at] is >= '0' and <= '9' && to - at <= 12)
                at--;
            from = at + 1;
            return from < to;
        }

        /// <summary>
        /// Reads one object as PDFsharp would from <paramref name="at"/>, refusing it when it nests too deeply. Returns
        /// where its stream data begins (after "stream", else -1) and whether its dictionary has /N. An object after
        /// "obj" ends at a keyword such as endobj; with <paramref name="oneValue"/> (in an object stream) after its value.
        /// </summary>
        private (long StreamAt, bool HasN) Read(Source source, long at, bool oneValue = false)
        {
            var checkpoint = at;
            var depth = 0;
            var topIsDictionary = false;
            var hasN = false;
            while (true)
            {
                if (at - checkpoint > 1 << 20)
                {
                    Spend(at - checkpoint);
                    checkpoint = at;
                }
                var kind = Next(source, ref at, out var start, out var length);
                switch (kind)
                {
                    case Kind.End:
                        Spend(at - checkpoint);
                        return (-1, hasN);
                    case Kind.DictionaryOpen or Kind.ArrayOpen:
                        if (++depth > MaxDepth)
                            throw TooDeep();
                        if (depth == 1)
                            topIsDictionary = kind == Kind.DictionaryOpen;
                        break;
                    case Kind.DictionaryClose or Kind.ArrayClose:
                        if (depth == 0)
                        {
                            Spend(at - checkpoint);
                            return (-1, hasN); // a stray closer: the parser stops with an error
                        }
                        depth--;
                        break;
                    case Kind.Name when depth == 1 && topIsDictionary && NameIs(source, start, length, "N"):
                        hasN = true;
                        break;
                    case Kind.Regular when depth == 0 && length <= 9:
                        var word = Text(source, start, length);
                        if (word is "stream" or "endobj" or "obj" or "endstream" or "trailer" or "xref" or "startxref")
                        {
                            Spend(at - checkpoint);
                            return (word == "stream" ? at : -1, hasN);
                        }
                        break;
                }
                if (oneValue && depth == 0)
                {
                    Spend(at - checkpoint);
                    return (-1, hasN);
                }
            }
        }

        /// <summary>Counts bytes read; refuses a file that would be read too many times over.</summary>
        private void Spend(long bytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _read += bytes;
            if (_read > _allowed)
                throw DocumentLimits.Exceeded("The PDF's objects overlap each other too often to be checked safely.");
        }

        // ───────────────────────── Object streams ─────────────────────────

        /// <summary>Unpacks the stream of the object at <paramref name="start"/> and reads the objects its header lists.</summary>
        private void ObjectStream(long start, long streamAt)
        {
            var at = start;
            if (Value(file, ref at) is not Dictionary<string, object?> dictionary)
                return;
            var counts = Integers(dictionary.GetValueOrDefault("N"));
            if (counts.Count == 0)
                return; // no object can be looked up in it
            // PDFsharp takes /N and /First only as integers in the dictionary itself (it fails on one by reference);
            // references are followed here all the same.
            var firsts = dictionary.TryGetValue("First", out var first) ? Integers(first) : [0];
            if (firsts.Count == 0)
                return;

            if (Unpack(dictionary, streamAt) is not { } data)
                return; // PDFsharp can't unpack it either, so it parses nothing of it
            _allowed += (long)data.Length * MaxReadsPerByte;
            var unpacked = new Source(data);

            // The header: pairs of object number and offset (from /First), as many as /N says (and the data can hold).
            var offsets = new List<long>();
            var pairs = Math.Min(counts.Max(), data.Length / 4 + 1);
            long position = 0;
            for (var i = 0L; i < pairs * 2; i++)
            {
                if (Next(unpacked, ref position, out var tokenStart, out var length) != Kind.Regular
                    || !long.TryParse(Text(unpacked, tokenStart, length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    break;
                if (i % 2 == 1)
                    offsets.Add(number);
            }
            foreach (var from in firsts)
            {
                foreach (var offset in offsets)
                {
                    var objectAt = from + offset;
                    if (objectAt >= 0 && objectAt < data.Length)
                        Read(unpacked, objectAt, oneValue: true);
                }
            }
        }

        /// <summary>The stream's data unpacked with PDFsharp's filters, or null when they can't unpack it.</summary>
        private byte[]? Unpack(Dictionary<string, object?> dictionary, long streamAt)
        {
            // The data starts after the end of line that follows "stream".
            var dataAt = streamAt;
            if (file[dataAt] == '\r')
                dataAt++;
            if (file[dataAt] == '\n')
                dataAt++;
            // Its end: the longest of /Length and the next "endstream" (PDFsharp's filters stop at the end of the
            // packed data, so more bytes than it takes never unpack to less).
            var end = file.IndexOf("endstream"u8, dataAt);
            foreach (var length in Integers(dictionary.GetValueOrDefault("Length")))
            {
                if (length >= 0 && length <= file.Length - dataAt)
                    end = Math.Max(end, dataAt + length);
            }
            if (end < 0)
                end = file.Length;
            if (end - dataAt > DocumentLimits.Default.MaxPartBytes)
                throw DocumentLimits.Exceeded($"An object stream of the PDF is larger than {DocumentLimits.Megabytes(DocumentLimits.Default.MaxPartBytes)}.");
            var data = file.Read(dataAt, (int)(end - dataAt));

            var filters = Resolve(dictionary.GetValueOrDefault("Filter")) switch
            {
                PdfNameValue name => [name.Value],
                List<object?> list => list.Select(Resolve).OfType<PdfNameValue>().Select(n => n.Value).ToList(),
                _ => new List<string>(),
            };
            var parameters = Resolve(dictionary.GetValueOrDefault("DecodeParms")) switch
            {
                Dictionary<string, object?> one => [one],
                List<object?> list => list.Select(p => Resolve(p) as Dictionary<string, object?>).ToList(),
                _ => new List<Dictionary<string, object?>?>(),
            };
            try
            {
                for (var i = 0; i < filters.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var parms = i < parameters.Count && parameters[i] is { } given ? new FilterParms(Parameters(given)) : null;
                    data = (parms is null ? Filtering.Decode(data, "/" + filters[i]) : Filtering.Decode(data, "/" + filters[i], parms))!;
                    if (data is null)
                        return null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
            return data;
        }

        /// <summary>The integer entries of decode parameters (/Predictor, /Columns ...) as PDFsharp's filters take them.</summary>
        private static PdfDictionary Parameters(Dictionary<string, object?> given)
        {
            var parameters = new PdfDictionary();
            foreach (var (key, value) in given)
            {
                if (value is long number && number is >= int.MinValue and <= int.MaxValue)
                    parameters.Elements.SetInteger("/" + key, (int)number);
            }
            return parameters;
        }

        /// <summary>The integers <paramref name="value"/> stands for: itself, or what the objects it refers to hold.</summary>
        private List<long> Integers(object? value) => value switch
        {
            long number => [number],
            Reference reference => Targets(reference).OfType<long>().ToList(),
            _ => [],
        };

        /// <summary><paramref name="value"/>, or the first thing the object it refers to holds.</summary>
        private object? Resolve(object? value) => value is Reference reference ? Targets(reference).FirstOrDefault() : value;

        /// <summary>The values of every object numbered as <paramref name="reference"/> says (more than one after an update).</summary>
        private IEnumerable<object?> Targets(Reference reference)
        {
            if (!_objects.TryGetValue(reference.Number, out var starts))
                yield break;
            foreach (var start in starts)
            {
                var at = start;
                if (Value(file, ref at) is var value and not Reference)
                    yield return value;
            }
        }
    }

    // ───────────────────────── Values (object stream dictionaries) ─────────────────────────

    private sealed record PdfNameValue(string Value);

    private sealed record Reference(long Number);

    /// <summary>Tokens one <see cref="Value(Source, ref long)"/> reads at most (the dictionaries it is for have a few dozen).</summary>
    private const int MaxValueTokens = 100_000;

    /// <summary>
    /// One value read from <paramref name="at"/>: an integer (long), a real (double), a name, an array (list), a dictionary
    /// (by key without the slash), a reference, or null for strings, keywords and damage. Past
    /// <see cref="MaxValueTokens"/> tokens the rest is left out.
    /// </summary>
    private static object? Value(Source source, ref long at)
    {
        var tokens = MaxValueTokens;
        return Value(source, ref at, 0, ref tokens);
    }

    private static object? Value(Source source, ref long at, int depth, ref int tokens)
    {
        if (--tokens < 0)
            return null;
        var kind = Next(source, ref at, out var start, out var length);
        switch (kind)
        {
            case Kind.DictionaryOpen:
                {
                    if (depth >= MaxDepth)
                        throw TooDeep();
                    var dictionary = new Dictionary<string, object?>();
                    while (tokens-- > 0)
                    {
                        var before = at;
                        var next = Next(source, ref at, out var keyStart, out var keyLength);
                        if (next is Kind.DictionaryClose or Kind.End)
                            return dictionary;
                        if (next != Kind.Name)
                        {
                            at = before;
                            Value(source, ref at, depth + 1, ref tokens); // damage, which the parser fails on anyway
                            continue;
                        }
                        var key = NameText(source, keyStart, keyLength);
                        dictionary.TryAdd(key, Value(source, ref at, depth + 1, ref tokens));
                    }
                    return dictionary;
                }
            case Kind.ArrayOpen:
                {
                    if (depth >= MaxDepth)
                        throw TooDeep();
                    var list = new List<object?>();
                    while (tokens-- > 0)
                    {
                        var before = at;
                        if (Next(source, ref at, out _, out _) is Kind.ArrayClose or Kind.End)
                            return list;
                        at = before;
                        list.Add(Value(source, ref at, depth + 1, ref tokens));
                    }
                    return list;
                }
            case Kind.Name:
                return new PdfNameValue(NameText(source, start, length));
            case Kind.Regular when length <= 32:
                var text = Text(source, start, length);
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                {
                    // "N G R" is a reference.
                    var after = at;
                    if (Next(source, ref after, out var genStart, out var genLength) == Kind.Regular
                        && long.TryParse(Text(source, genStart, Math.Min(genLength, 12)), NumberStyles.None, CultureInfo.InvariantCulture, out _)
                        && Next(source, ref after, out var rStart, out var rLength) == Kind.Regular && rLength == 1 && source[rStart] == 'R')
                    {
                        at = after;
                        return new Reference(number);
                    }
                    return number;
                }
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ? real : null;
            default:
                return null;
        }
    }

    // ───────────────────────── Tokens ─────────────────────────

    private enum Kind { End, DictionaryOpen, DictionaryClose, ArrayOpen, ArrayClose, Name, String, Regular, Other }

    /// <summary>The next token from <paramref name="at"/>, as PDFsharp's lexer splits the file; white space and comments are skipped.</summary>
    private static Kind Next(Source source, ref long at, out long start, out long length)
    {
        while (true)
        {
            var c = source[at];
            start = at;
            if (c < 0)
            {
                length = 0;
                return Kind.End;
            }
            if (IsSpace(c))
            {
                at++;
                continue;
            }
            if (c == '%')
            {
                while (source[at] is >= 0 and not '\n' and not '\r')
                    at++;
                continue;
            }
            Kind kind;
            switch (c)
            {
                case '(':
                    at = AfterString(source, at);
                    kind = Kind.String;
                    break;
                case '<' when source[at + 1] == '<':
                    at += 2;
                    kind = Kind.DictionaryOpen;
                    break;
                case '<':
                    while (source[at] is >= 0 and not '>')
                        at++;
                    if (source[at] == '>')
                        at++;
                    kind = Kind.String;
                    break;
                case '>' when source[at + 1] == '>':
                    at += 2;
                    kind = Kind.DictionaryClose;
                    break;
                case '[':
                    at++;
                    kind = Kind.ArrayOpen;
                    break;
                case ']':
                    at++;
                    kind = Kind.ArrayClose;
                    break;
                case '/':
                    at++;
                    while (IsRegular(source[at]))
                        at++;
                    kind = Kind.Name;
                    break;
                default:
                    if (IsRegular(c))
                    {
                        while (IsRegular(source[at]))
                            at++;
                        kind = Kind.Regular;
                    }
                    else
                    {
                        at++; // ")", a lone ">", "{" or "}": the parser fails on them
                        kind = Kind.Other;
                    }
                    break;
            }
            length = at - start;
            return kind;
        }
    }

    /// <summary>After the literal string at <paramref name="at"/>: parentheses in it balance, a backslash escapes the next byte.</summary>
    private static long AfterString(Source source, long at)
    {
        var open = 0;
        for (; ; at++)
        {
            switch (source[at])
            {
                case < 0:
                    return at;
                case '\\':
                    at++;
                    break;
                case '(':
                    open++;
                    break;
                case ')' when --open == 0:
                    return at + 1;
            }
        }
    }

    /// <summary>The token's bytes as text (Latin-1), at most 128 of them.</summary>
    private static string Text(Source source, long start, long length)
    {
        var count = (int)Math.Min(length, 128);
        var chars = new char[count];
        for (var i = 0; i < count; i++)
            chars[i] = (char)source[start + i];
        return new string(chars);
    }

    /// <summary>A name token without its slash, <c>#xx</c> escapes decoded.</summary>
    private static string NameText(Source source, long start, long length)
    {
        var raw = Text(source, start + 1, length - 1);
        if (!raw.Contains('#'))
            return raw;
        var text = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '#' && i + 2 < raw.Length && int.TryParse(raw.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
            {
                text.Append((char)code);
                i += 2;
            }
            else
            {
                text.Append(raw[i]);
            }
        }
        return text.ToString();
    }

    private static bool NameIs(Source source, long start, long length, string name) =>
        length <= 3 * name.Length + 1 && NameText(source, start, length) == name;

    private static bool IsSpace(int c) => c is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(int c) => c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    private static bool IsRegular(int c) => c >= 0 && !IsSpace(c) && !IsDelimiter(c);

    /// <summary>True where a token ends: white space, a delimiter, or outside the data.</summary>
    private static bool Ends(int c) => c < 0 || IsSpace(c) || IsDelimiter(c);

    // ───────────────────────── Bytes ─────────────────────────

    /// <summary>The bytes of a PDF in memory, or of a file read through a window that follows the reading.</summary>
    private sealed class Source
    {
        private const int WindowBytes = 1 << 20;

        /// <summary>Kept before a position when the window moves, for the number before "obj".</summary>
        private const int Behind = 64;

        private readonly SafeFileHandle? _file;
        private readonly byte[] _window;
        private long _start;
        private int _count;

        public Source(byte[] data)
        {
            _window = data;
            _count = data.Length;
            Length = data.Length;
        }

        public Source(SafeFileHandle file)
        {
            _file = file;
            Length = RandomAccess.GetLength(file);
            _window = new byte[WindowBytes];
        }

        public long Length { get; }

        /// <summary>The byte at <paramref name="at"/>, or -1 outside the data.</summary>
        public int this[long at]
        {
            get
            {
                var i = at - _start;
                return (ulong)i < (ulong)_count ? _window[i] : Load(at);
            }
        }

        private int Load(long at)
        {
            if (_file is null || at < 0 || at >= Length)
                return -1;
            Move(at);
            var i = at - _start;
            return i < _count ? _window[i] : -1;
        }

        /// <summary>Reads the window around <paramref name="at"/> (from a little before it).</summary>
        private void Move(long at)
        {
            _start = Math.Max(0, at - Behind);
            _count = Fill(_window, _start);
        }

        private int Fill(Span<byte> buffer, long at)
        {
            var done = 0;
            while (done < buffer.Length)
            {
                var read = RandomAccess.Read(_file!, buffer[done..], at + done);
                if (read <= 0)
                    break;
                done += read;
            }
            return done;
        }

        /// <summary>Where <paramref name="needle"/> next occurs from <paramref name="from"/>, or -1.</summary>
        public long IndexOf(ReadOnlySpan<byte> needle, long from)
        {
            if (_file is null)
            {
                if (from < 0 || from >= _count)
                    return -1;
                var found = _window.AsSpan((int)from).IndexOf(needle);
                return found < 0 ? -1 : from + found;
            }
            while (from >= 0 && from < Length)
            {
                if ((ulong)(from - _start) >= (ulong)_count)
                    Move(from);
                var offset = (int)(from - _start);
                if (offset >= _count)
                    return -1;
                var found = _window.AsSpan(offset, _count - offset).IndexOf(needle);
                if (found >= 0)
                    return from + found;
                if (_start + _count >= Length)
                    return -1;
                // The next window overlaps this one by the needle, so a match across the edge is found.
                from = Math.Max(from + 1, _start + _count - needle.Length + 1);
                Move(from);
            }
            return -1;
        }

        /// <summary><paramref name="count"/> bytes from <paramref name="at"/> (fewer at the end of the data).</summary>
        public byte[] Read(long at, int count)
        {
            count = (int)Math.Max(0, Math.Min(count, Length - at));
            var bytes = new byte[count];
            if (_file is null)
                Array.Copy(_window, at, bytes, 0, count);
            else
                Array.Resize(ref bytes, Fill(bytes, at));
            return bytes;
        }
    }
}
