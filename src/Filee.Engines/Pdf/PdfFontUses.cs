// Which character codes each font of a PDF draws, for PdfFontSubsetter: the content streams of the pages, of the forms
// they draw, of annotation appearances, tiling patterns, soft masks and Type 3 glyphs are read for Tf and the text
// operators (Tj, TJ, ', "). A font is "accounted for" only when every resource dictionary it is listed in was read
// without an error: a font Filee can't follow (in the default resources of interactive forms, set through a graphics
// state, or in a stream that didn't parse) keeps all its glyphs.
//
// The content is read by PdfContentLexer rather than PDFsharp's ContentReader, which turns hex strings that start
// with FE FF into UTF-16 text and so loses the codes of a two-byte font.
//
// The file is untrusted, so the walk is bounded and never refuses the job: a stream past a limit counts as one that
// didn't parse, and the fonts of its resources stay whole. Each stream is read once into the font names it selects,
// the codes it shows with each and the forms it draws; drawing it again with other resources only resolves those
// names again. Forms are followed from a work stack rather than by recursion, at most MaxDepth deep, and arrays and
// dictionaries in a stream nest at most PdfContentLexer.MaxDepth deep. A document is bounded by MaxStreams,
// MaxContentBytes decoded and MaxWork names and codes resolved.

using PdfSharp.Pdf;
using Name = Filee.Engines.Pdf.PdfContentLexer.Name;
using Operator = Filee.Engines.Pdf.PdfContentLexer.Operator;

namespace Filee.Engines.Pdf;

/// <summary>The codes each font draws, and the fonts whose use can't be followed.</summary>
internal sealed class PdfFontUses
{
    /// <summary>Streams followed per document at most (one drawn with other resources counts again).</summary>
    internal const int MaxStreams = 20_000;

    /// <summary>Forms drawing forms (and patterns, soft masks, Type 3 glyphs) followed this deep at most.</summary>
    internal const int MaxDepth = 32;

    /// <summary>Decoded content read per document at most.</summary>
    internal const long MaxContentBytes = 512L << 20;

    /// <summary>Font names, codes and form names resolved per document at most, against a big form drawn many times.</summary>
    internal const long MaxWork = 50_000_000;

    /// <summary>Names and codes one stream may hold; real pages hold a few thousand.</summary>
    internal const int MaxStreamSize = 1_000_000;

    /// <summary>Names and codes kept of forms that may be drawn again with other resources.</summary>
    private const long MaxKept = 4_000_000;

    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<PdfDictionary, HashSet<int>> _codes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfDictionary> _read = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfDictionary> _failed = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfDictionary> _unsure = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(PdfDictionary, PdfDictionary?)> _walked = [];
    private readonly HashSet<PdfDictionary> _resourcesRead = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfDictionary, Content> _kept = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<(PdfDictionary Stream, PdfDictionary? Resources, int Depth)> _pending = new();
    private int _streams;
    private long _bytes;
    private long _work;
    private long _keptSize;

    private PdfFontUses(CancellationToken cancellationToken) => _cancellationToken = cancellationToken;

    /// <summary>Character codes drawn with each font (two-byte codes for Type 0 fonts).</summary>
    public IReadOnlyDictionary<PdfDictionary, HashSet<int>> Codes => _codes;

    /// <summary>Fonts whose use couldn't be followed everywhere they are listed.</summary>
    public IReadOnlySet<PdfDictionary> Unsure => _unsure;

    public static PdfFontUses Find(PdfDocument document, CancellationToken cancellationToken)
    {
        var uses = new PdfFontUses(cancellationToken);
        foreach (var page in document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resources = page.Elements.GetDictionary("/Resources");
            uses.WalkPage(page, resources);
            uses.WalkAnnotations(page, resources);
            uses.Drain();
        }
        uses.CheckListings(document);
        return uses;
    }

    private void WalkPage(PdfPage page, PdfDictionary? resources)
    {
        byte[] content;
        try
        {
            if (_bytes >= MaxContentBytes)
                throw new InvalidDataException("The document's content streams are too large to follow.");
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
        Apply(Lex(content), resources, depth: 0);
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
                        _pending.Push((form, pageResources, 1));
                        break;
                    case PdfDictionary states:
                        foreach (var state in states.Elements.Keys)
                        {
                            if (PdfCompressor.Resolve(states.Elements[state]) is PdfDictionary { Stream: not null } stateForm)
                                _pending.Push((stateForm, pageResources, 1));
                        }
                        break;
                }
            }
        }
    }

    /// <summary>Follows the forms, patterns, soft masks and Type 3 glyphs that the streams read so far draw.</summary>
    private void Drain()
    {
        while (_pending.TryPop(out var next))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            WalkStream(next.Stream, next.Resources, next.Depth);
        }
    }

    /// <summary>A form, pattern, soft mask group or Type 3 glyph: its own resources, else the ones it is drawn with.</summary>
    private void WalkStream(PdfDictionary stream, PdfDictionary? inherited, int depth)
    {
        var own = stream.Elements.GetDictionary("/Resources");
        var resources = own ?? inherited;
        if (!_walked.Add((stream, resources)))
            return;
        if (depth > MaxDepth || ++_streams > MaxStreams)
        {
            Fail(resources);
            return;
        }
        Apply(Read(stream, keep: own is null), resources, depth);
    }

    /// <summary>
    /// A stream's content. With <paramref name="keep"/> it is kept for the next time the stream is drawn: only a stream
    /// without resources of its own can be drawn again with other ones (the walk skips one drawn again with the same).
    /// </summary>
    private Content Read(PdfDictionary stream, bool keep)
    {
        if (_kept.TryGetValue(stream, out var kept))
            return kept;
        Content content;
        try
        {
            content = _bytes >= MaxContentBytes ? Content.Unreadable : Lex(stream.Stream.UnfilteredValue);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            content = Content.Unreadable;
        }
        if (keep && _keptSize + content.Size <= MaxKept)
        {
            _kept[stream] = content;
            _keptSize += content.Size;
        }
        return content;
    }

    private Content Lex(byte[] data)
    {
        _bytes += data.Length;
        return _bytes > MaxContentBytes ? Content.Unreadable : Content.Read(data, _cancellationToken);
    }

    /// <summary>Resolves a stream's font and form names in <paramref name="resources"/>.</summary>
    private void Apply(Content content, PdfDictionary? resources, int depth)
    {
        var fonts = resources is null ? null : PdfCompressor.Resolve(resources.Elements["/Font"]) as PdfDictionary;
        if (fonts is not null)
            _read.Add(fonts);
        // Patterns and soft masks can draw text wherever they are used: read them all.
        if (resources is not null && _resourcesRead.Add(resources))
        {
            foreach (var pattern in Values(resources, "/Pattern"))
            {
                if (pattern.Stream is not null)
                    _pending.Push((pattern, resources, depth + 1));
            }
            foreach (var state in Values(resources, "/ExtGState"))
            {
                if (PdfCompressor.Resolve(state.Elements["/SMask"]) is PdfDictionary mask && PdfCompressor.Resolve(mask.Elements["/G"]) is PdfDictionary { Stream: not null } group)
                    _pending.Push((group, resources, depth + 1));
            }
        }
        if (content.Failed)
            Fail(resources);
        _work += content.Size;
        if (_work > MaxWork)
        {
            Fail(resources);
            return;
        }

        foreach (var (name, shown) in content.Fonts)
        {
            if (fonts is null || PdfCompressor.Resolve(fonts.Elements["/" + name]) is not PdfDictionary font)
                continue;
            if (font.Elements.GetName("/Subtype") == "/Type3")
                WalkType3(font, resources, depth);
            Show(font, shown);
        }
        if (content.Forms.Count > 0 && resources?.Elements.GetDictionary("/XObject") is { } xobjects)
        {
            foreach (var name in content.Forms)
            {
                if (PdfCompressor.Resolve(xobjects.Elements["/" + name]) is PdfDictionary { Stream: not null } xobject
                    && xobject.Elements.GetName("/Subtype") == "/Form")
                    _pending.Push((xobject, resources, depth + 1));
            }
        }
    }

    private void WalkType3(PdfDictionary font, PdfDictionary? resources, int depth)
    {
        if (PdfCompressor.Resolve(font.Elements["/CharProcs"]) is not PdfDictionary procs)
            return;
        var own = font.Elements.GetDictionary("/Resources") ?? resources;
        foreach (var key in procs.Elements.Keys)
        {
            if (PdfCompressor.Resolve(procs.Elements[key]) is PdfDictionary { Stream: not null } glyph)
                _pending.Push((glyph, own, depth + 1));
        }
    }

    private void Show(PdfDictionary font, Shown shown)
    {
        if (!shown.Any)
            return;
        if (!_codes.TryGetValue(font, out var codes))
            _codes[font] = codes = [];
        if (font.Elements.GetName("/Subtype") == "/Type0")
        {
            if (shown.Odd)
                _unsure.Add(font);
            codes.UnionWith(shown.Pairs);
        }
        else
        {
            codes.UnionWith(shown.Bytes);
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

    /// <summary>The codes a stream shows with one font name, as single bytes and as two-byte pairs: the font decides which.</summary>
    private sealed class Shown
    {
        public HashSet<int> Bytes { get; } = [];
        public HashSet<int> Pairs { get; } = [];

        /// <summary>A string was shown.</summary>
        public bool Any { get; private set; }

        /// <summary>A string of odd length was shown, which a two-byte font can't read whole.</summary>
        public bool Odd { get; private set; }

        /// <summary>Adds a string's codes; returns how many are new.</summary>
        public int Add(byte[] text)
        {
            Any = true;
            var added = 0;
            foreach (var b in text)
                added += Bytes.Add(b) ? 1 : 0;
            if (text.Length % 2 != 0)
                Odd = true;
            for (var i = 0; i + 1 < text.Length; i += 2)
                added += Pairs.Add(text[i] << 8 | text[i + 1]) ? 1 : 0;
            return added;
        }
    }

    /// <summary>What one content stream does with fonts and forms, by resource name.</summary>
    private sealed class Content
    {
        /// <summary>Operands kept before an operator: none of the operators read here takes more.</summary>
        private const int MaxOperands = 16;

        /// <summary>A stream that couldn't be decoded, or past a limit: its resources' fonts stay whole.</summary>
        public static readonly Content Unreadable = new() { Failed = true };

        /// <summary>Every font name set with Tf, with the codes shown while it was set.</summary>
        public Dictionary<string, Shown> Fonts { get; } = [];

        /// <summary>Every XObject name drawn with Do.</summary>
        public HashSet<string> Forms { get; } = [];

        /// <summary>The stream broke off or ran past a limit; what came before stays.</summary>
        public bool Failed { get; private set; }

        /// <summary>Names and codes held: what resolving them costs.</summary>
        public long Size { get; private set; }

        public static Content Read(byte[] data, CancellationToken cancellationToken)
        {
            var content = new Content();
            var lexer = new PdfContentLexer(data);
            var operands = new List<object?>();
            var saved = new Stack<Shown?>();
            Shown? font = null;
            var tokens = 0;
            try
            {
                while (lexer.Next() is { } token)
                {
                    if (++tokens % 65_536 == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    if (token is not Operator op)
                    {
                        if (operands.Count == MaxOperands)
                            operands.RemoveAt(0);
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
                            if (!content.Fonts.TryGetValue(name.Value, out font))
                            {
                                content.Fonts[name.Value] = font = new Shown();
                                content.Size++;
                            }
                            break;
                        case "Tj" or "'" when operands.Count >= 1 && operands[^1] is byte[] text:
                            content.Show(font, text);
                            break;
                        case "\"" when operands.Count >= 3 && operands[^1] is byte[] text:
                            content.Show(font, text);
                            break;
                        case "TJ" when operands.Count >= 1 && operands[^1] is List<object?> array:
                            foreach (var part in array.OfType<byte[]>())
                                content.Show(font, part);
                            break;
                        case "Do" when operands.Count >= 1 && operands[^1] is Name name:
                            if (content.Forms.Add(name.Value))
                                content.Size++;
                            break;
                        case "BI":
                            lexer.SkipInlineImage();
                            break;
                    }
                    operands.Clear();
                    if (content.Size > MaxStreamSize)
                        throw new InvalidDataException("The content stream uses too many fonts, codes or forms.");
                }
            }
            catch (InvalidDataException)
            {
                content.Failed = true;
            }
            return content;
        }

        private void Show(Shown? font, byte[] text)
        {
            if (font is not null)
                Size += font.Add(text);
        }
    }
}
