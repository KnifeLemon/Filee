// Which character codes each font of a PDF draws, for PdfFontSubsetter: the content streams of the pages, of the forms
// they draw, of annotation appearances, tiling patterns, soft masks and Type 3 glyphs are read for Tf and the text
// operators (Tj, TJ, ', "). A font is "accounted for" only when every resource dictionary it is listed in was read
// without an error: a font Filee can't follow (in the default resources of interactive forms, set through a graphics
// state, or in a stream that didn't parse) keeps all its glyphs.
//
// The content is read by a small lexer of its own rather than PDFsharp's ContentReader, which turns hex strings that
// start with FE FF into UTF-16 text and so loses the codes of a two-byte font.

using System.Text;
using PdfSharp.Pdf;

namespace Filee.Engines.Pdf;

/// <summary>The codes each font draws, and the fonts whose use can't be followed.</summary>
internal sealed class PdfFontUses
{
    /// <summary>Forms followed per document at most, against forms drawing each other many times over.</summary>
    private const int MaxStreams = 20_000;

    private readonly Dictionary<PdfDictionary, HashSet<int>> _codes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfDictionary> _read = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfDictionary> _failed = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfDictionary> _unsure = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(PdfDictionary, PdfDictionary?)> _walked = [];
    private int _streams;

    /// <summary>Character codes drawn with each font (two-byte codes for Type 0 fonts).</summary>
    public IReadOnlyDictionary<PdfDictionary, HashSet<int>> Codes => _codes;

    /// <summary>Fonts whose use couldn't be followed everywhere they are listed.</summary>
    public IReadOnlySet<PdfDictionary> Unsure => _unsure;

    public static PdfFontUses Find(PdfDocument document, CancellationToken cancellationToken)
    {
        var uses = new PdfFontUses();
        foreach (var page in document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resources = page.Elements.GetDictionary("/Resources");
            uses.WalkPage(page, resources);
            uses.WalkAnnotations(page, resources);
        }
        uses.CheckListings(document);
        return uses;
    }

    private void WalkPage(PdfPage page, PdfDictionary? resources)
    {
        byte[] content;
        try
        {
            using var stream = new MemoryStream();
            foreach (var part in Parts(page.Elements["/Contents"]))
                stream.Write(part.Stream.UnfilteredValue);
            content = stream.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(resources);
            return;
        }
        Walk(content, resources);
    }

    private static IEnumerable<PdfDictionary> Parts(PdfItem? contents) => PdfCompressor.Resolve(contents) switch
    {
        PdfDictionary { Stream: not null } stream => [stream],
        PdfArray array => array.Elements.Select(PdfCompressor.Resolve).OfType<PdfDictionary>().Where(d => d.Stream is not null),
        _ => [],
    };

    private void WalkAnnotations(PdfPage page, PdfDictionary? pageResources)
    {
        if (page.Elements.GetArray("/Annots") is not { } annotations)
            return;
        foreach (var item in annotations.Elements)
        {
            if (PdfCompressor.Resolve(item) is not PdfDictionary annotation || annotation.Elements.GetDictionary("/AP") is not { } appearances)
                continue;
            foreach (var key in appearances.Elements.Keys)
            {
                switch (PdfCompressor.Resolve(appearances.Elements[key]))
                {
                    case PdfDictionary { Stream: not null } form:
                        WalkStream(form, pageResources);
                        break;
                    case PdfDictionary states:
                        foreach (var state in states.Elements.Keys)
                        {
                            if (PdfCompressor.Resolve(states.Elements[state]) is PdfDictionary { Stream: not null } stateForm)
                                WalkStream(stateForm, pageResources);
                        }
                        break;
                }
            }
        }
    }

    /// <summary>A form, pattern, soft mask group or Type 3 glyph: its own resources, else the ones it is drawn with.</summary>
    private void WalkStream(PdfDictionary stream, PdfDictionary? inherited)
    {
        var resources = stream.Elements.GetDictionary("/Resources") ?? inherited;
        if (!_walked.Add((stream, resources)))
            return;
        if (++_streams > MaxStreams)
        {
            Fail(resources);
            return;
        }
        byte[] content;
        try
        {
            content = stream.Stream.UnfilteredValue;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(resources);
            return;
        }
        Walk(content, resources);
    }

    private void Walk(byte[] content, PdfDictionary? resources)
    {
        var fonts = resources is null ? null : PdfCompressor.Resolve(resources.Elements["/Font"]) as PdfDictionary;
        if (fonts is not null)
            _read.Add(fonts);
        // Patterns and soft masks can draw text wherever they are used: read them all.
        if (resources is not null)
        {
            foreach (var pattern in Values(resources, "/Pattern"))
            {
                if (pattern.Stream is not null)
                    WalkStream(pattern, resources);
            }
            foreach (var state in Values(resources, "/ExtGState"))
            {
                if (PdfCompressor.Resolve(state.Elements["/SMask"]) is PdfDictionary mask && PdfCompressor.Resolve(mask.Elements["/G"]) is PdfDictionary { Stream: not null } group)
                    WalkStream(group, resources);
            }
        }

        var lexer = new Lexer(content);
        var operands = new List<object?>();
        var saved = new Stack<PdfDictionary?>();
        PdfDictionary? font = null;
        try
        {
            while (lexer.Next() is { } token)
            {
                if (token is not Operator op)
                {
                    operands.Add(token);
                    continue;
                }
                switch (op.Name)
                {
                    case "q":
                        saved.Push(font);
                        break;
                    case "Q":
                        if (saved.Count > 0)
                            font = saved.Pop();
                        break;
                    case "Tf" when operands.Count >= 2 && operands[^2] is Name name:
                        font = fonts is null ? null : PdfCompressor.Resolve(fonts.Elements["/" + name.Value]) as PdfDictionary;
                        if (font is not null && font.Elements.GetName("/Subtype") == "/Type3")
                            WalkType3(font, resources);
                        break;
                    case "Tj" or "'" when operands.Count >= 1 && operands[^1] is byte[] text:
                        Show(font, text);
                        break;
                    case "\"" when operands.Count >= 3 && operands[^1] is byte[] text:
                        Show(font, text);
                        break;
                    case "TJ" when operands.Count >= 1 && operands[^1] is List<object?> array:
                        foreach (var part in array.OfType<byte[]>())
                            Show(font, part);
                        break;
                    case "Do" when operands.Count >= 1 && operands[^1] is Name name:
                        if (resources?.Elements.GetDictionary("/XObject") is { } xobjects
                            && PdfCompressor.Resolve(xobjects.Elements["/" + name.Value]) is PdfDictionary { Stream: not null } xobject
                            && xobject.Elements.GetName("/Subtype") == "/Form")
                            WalkStream(xobject, resources);
                        break;
                    case "BI":
                        lexer.SkipInlineImage();
                        break;
                }
                operands.Clear();
            }
        }
        catch (InvalidDataException)
        {
            Fail(resources);
        }
    }

    private void WalkType3(PdfDictionary font, PdfDictionary? resources)
    {
        if (PdfCompressor.Resolve(font.Elements["/CharProcs"]) is not PdfDictionary procs)
            return;
        var own = font.Elements.GetDictionary("/Resources") ?? resources;
        foreach (var key in procs.Elements.Keys)
        {
            if (PdfCompressor.Resolve(procs.Elements[key]) is PdfDictionary { Stream: not null } glyph)
                WalkStream(glyph, own);
        }
    }

    private void Show(PdfDictionary? font, byte[] text)
    {
        if (font is null)
            return;
        if (!_codes.TryGetValue(font, out var codes))
            _codes[font] = codes = [];
        if (font.Elements.GetName("/Subtype") == "/Type0")
        {
            if (text.Length % 2 != 0)
                _unsure.Add(font);
            for (var i = 0; i + 1 < text.Length; i += 2)
                codes.Add(text[i] << 8 | text[i + 1]);
        }
        else
        {
            foreach (var b in text)
                codes.Add(b);
        }
    }

    private void Fail(PdfDictionary? resources)
    {
        if (resources is not null && PdfCompressor.Resolve(resources.Elements["/Font"]) is PdfDictionary fonts)
            _failed.Add(fonts);
    }

    /// <summary>
    /// Every font listed in a font resource dictionary that wasn't read, or failed, is unsure; so is every font a
    /// graphics state sets (gs), and every font of the interactive form's default resources (viewers type with them).
    /// </summary>
    private void CheckListings(PdfDocument document)
    {
        foreach (var item in document.Internals.GetAllObjects())
        {
            if (item is PdfDictionary dictionary)
                CheckListings(dictionary, depth: 0);
        }
        if (PdfCompressor.Resolve(document.Internals.Catalog.Elements["/AcroForm"]) is PdfDictionary form
            && PdfCompressor.Resolve(form.Elements["/DR"]) is PdfDictionary defaults
            && PdfCompressor.Resolve(defaults.Elements["/Font"]) is PdfDictionary formFonts)
            MarkAll(formFonts);
    }

    private void CheckListings(PdfDictionary dictionary, int depth)
    {
        if (depth > 6)
            return;
        switch (PdfCompressor.Resolve(dictionary.Elements["/Font"]))
        {
            case PdfDictionary fonts when !_read.Contains(fonts) || _failed.Contains(fonts):
                MarkAll(fonts);
                break;
            case PdfArray { Elements.Count: > 0 } setByState when PdfCompressor.Resolve(setByState.Elements[0]) is PdfDictionary font:
                _unsure.Add(font);
                break;
        }
        foreach (var value in dictionary.Elements.Values)
        {
            if (value is PdfDictionary { Reference: null } inner)
                CheckListings(inner, depth + 1);
        }
    }

    private void MarkAll(PdfDictionary fonts)
    {
        foreach (var value in fonts.Elements.Values)
        {
            if (PdfCompressor.Resolve(value) is PdfDictionary font)
                _unsure.Add(font);
        }
    }

    private static IEnumerable<PdfDictionary> Values(PdfDictionary resources, string key) =>
        PdfCompressor.Resolve(resources.Elements[key]) is PdfDictionary group
            ? group.Elements.Values.Select(PdfCompressor.Resolve).OfType<PdfDictionary>()
            : [];

    private sealed record Name(string Value);

    private sealed record Operator(string Name);

    /// <summary>
    /// Content stream tokens: numbers (as double), names, strings (as their bytes), arrays (as lists), dictionaries
    /// (skipped, as null) and operators. Damaged syntax throws <see cref="InvalidDataException"/>.
    /// </summary>
    private sealed class Lexer(byte[] data)
    {
        private int _at;

        public object? Next()
        {
            while (true)
            {
                SkipSpace();
                if (_at >= data.Length)
                    return null;
                var token = Token();
                if (token is not ArrayEnd and not DictEnd)
                    return token;
            }
        }

        private sealed record ArrayEnd;

        private sealed record DictEnd;

        private object? Token()
        {
            SkipSpace();
            if (_at >= data.Length)
                throw new InvalidDataException("The content stream ends inside an object.");
            var c = data[_at];
            switch (c)
            {
                case (byte)'(':
                    return Literal();
                case (byte)'<' when _at + 1 < data.Length && data[_at + 1] == '<':
                    _at += 2;
                    while (Token() is not DictEnd)
                    {
                    }
                    return null;
                case (byte)'<':
                    return Hex();
                case (byte)'>' when _at + 1 < data.Length && data[_at + 1] == '>':
                    _at += 2;
                    return new DictEnd();
                case (byte)'[':
                    _at++;
                    var list = new List<object?>();
                    while (Token() is var item and not ArrayEnd)
                        list.Add(item);
                    return list;
                case (byte)']':
                    _at++;
                    return new ArrayEnd();
                case (byte)'/':
                    _at++;
                    return new Name(Regular());
                case (byte)'{' or (byte)'}' or (byte)'>' or (byte)')':
                    _at++;
                    return new Operator(((char)c).ToString());
            }
            var word = Regular();
            if (word.Length == 0)
                throw new InvalidDataException("The content stream has a stray character.");
            return word[0] is >= '0' and <= '9' or '+' or '-' or '.' ? double.TryParse(word, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0 : new Operator(word);
        }

        private string Regular()
        {
            var text = new StringBuilder();
            while (_at < data.Length && !IsSpace(data[_at]) && !IsDelimiter(data[_at]))
            {
                if (data[_at] == '#' && _at + 2 < data.Length && Uri.IsHexDigit((char)data[_at + 1]) && Uri.IsHexDigit((char)data[_at + 2]))
                {
                    text.Append((char)Convert.ToByte(Encoding.ASCII.GetString(data, _at + 1, 2), 16));
                    _at += 3;
                    continue;
                }
                text.Append((char)data[_at++]);
            }
            return text.ToString();
        }

        private byte[] Literal()
        {
            _at++; // (
            var bytes = new List<byte>();
            var depth = 1;
            while (_at < data.Length)
            {
                var c = data[_at++];
                if (c == '\\' && _at < data.Length)
                {
                    var e = data[_at++];
                    switch (e)
                    {
                        case (byte)'n': bytes.Add(10); break;
                        case (byte)'r': bytes.Add(13); break;
                        case (byte)'t': bytes.Add(9); break;
                        case (byte)'b': bytes.Add(8); break;
                        case (byte)'f': bytes.Add(12); break;
                        case 13:
                            if (_at < data.Length && data[_at] == 10)
                                _at++;
                            break;
                        case 10:
                            break;
                        case >= (byte)'0' and <= (byte)'7':
                            var value = e - '0';
                            for (var i = 0; i < 2 && _at < data.Length && data[_at] is >= (byte)'0' and <= (byte)'7'; i++)
                                value = value * 8 + (data[_at++] - '0');
                            bytes.Add((byte)value);
                            break;
                        default:
                            bytes.Add(e);
                            break;
                    }
                    continue;
                }
                if (c == '(')
                    depth++;
                else if (c == ')' && --depth == 0)
                    return [.. bytes];
                bytes.Add(c);
            }
            throw new InvalidDataException("A string in the content stream isn't closed.");
        }

        private byte[] Hex()
        {
            _at++; // <
            var bytes = new List<byte>();
            var high = -1;
            while (_at < data.Length && data[_at] != '>')
            {
                var c = (char)data[_at++];
                if (IsSpace((byte)c))
                    continue;
                if (!Uri.IsHexDigit(c))
                    throw new InvalidDataException("A hex string in the content stream has a stray character.");
                var digit = Convert.ToInt32(c.ToString(), 16);
                if (high < 0)
                {
                    high = digit;
                }
                else
                {
                    bytes.Add((byte)(high << 4 | digit));
                    high = -1;
                }
            }
            if (_at >= data.Length)
                throw new InvalidDataException("A hex string in the content stream isn't closed.");
            _at++; // >
            if (high >= 0)
                bytes.Add((byte)(high << 4));
            return [.. bytes];
        }

        /// <summary>Skips an inline image's parameters, "ID", its data and "EI".</summary>
        public void SkipInlineImage()
        {
            while (Next() is var token && token is not Operator { Name: "ID" })
            {
                if (token is null)
                    throw new InvalidDataException("An inline image has no data.");
            }
            _at++; // the single white-space character after ID
            // The data ends at "EI" between white space (or the end): the usual way readers find it.
            for (var i = _at; i + 1 < data.Length; i++)
            {
                if (data[i] == 'E' && data[i + 1] == 'I' && IsSpace(data[i - 1]) && (i + 2 == data.Length || IsSpace(data[i + 2])))
                {
                    _at = i + 2;
                    return;
                }
            }
            throw new InvalidDataException("An inline image has no end.");
        }

        private void SkipSpace()
        {
            while (_at < data.Length)
            {
                if (data[_at] == '%')
                {
                    while (_at < data.Length && data[_at] is not 10 and not 13)
                        _at++;
                }
                else if (IsSpace(data[_at]))
                {
                    _at++;
                }
                else
                {
                    return;
                }
            }
        }

        private static bool IsSpace(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;

        private static bool IsDelimiter(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
    }
}
