// Which character codes each font of a PDF draws, for PdfFontSubsetter: the content streams of the pages, of the forms
// they draw, of annotation appearances, tiling patterns, soft masks and Type 3 glyphs are read for Tf and the text
// operators (Tj, TJ, ', "). A font is "accounted for" only when every resource dictionary it is listed in was read
// without an error: a font Filee can't follow (in the default resources of interactive forms, set through a graphics
// state, or in a stream that didn't parse) keeps all its glyphs.
//
// The content is read by PdfContentLexer rather than PDFsharp's ContentReader, which turns hex strings that start
// with FE FF into UTF-16 text and so loses the codes of a two-byte font.

using PdfSharp.Pdf;
using Name = Filee.Engines.Pdf.PdfContentLexer.Name;
using Operator = Filee.Engines.Pdf.PdfContentLexer.Operator;

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

        var lexer = new PdfContentLexer(content);
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
}
