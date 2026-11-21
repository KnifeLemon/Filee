// PDF/A-2b (ISO 19005-2, level B) without Ghostscript: a PDF opened with PDFsharp is changed in place to keep the rules
// below, then saved by PdfAMetadata, which gives it the XMP and document information PDF/A asks for. Used for every
// PDF the job queue makes PDF/A (PdfSharpConverter's IPdfArchiver, before Ghostscript in the engine order) and by the
// Filee renderer's page writer, which writes its PDFs as PDF/A directly when the preset asks for it.
//
// What is changed (clause of ISO 19005-2):
// - 6.1.3/6.1.7: no encryption, no streams kept in external files, no LZW (such streams are unpacked and deflated).
// - 6.2.2-6.2.4: one GTS_PDFA1 output intent. The sRGB profile made in code (SrgbProfile) unless the PDF already has
//   a PDF/A intent of the device colours it uses; a PDF drawn in CMYK keeps a CMYK intent it brings. The other device
//   space (DeviceCMYK under an RGB intent, DeviceRGB under a CMYK one) is made device independent (6.2.4.3): its
//   names in objects (pictures, shadings, groups, colour spaces) become an ICCBased space, and every resource
//   dictionary gets DefaultCMYK (CmykProfile, made in code) or DefaultRGB (sRGB) for the operators in content.
// - 6.2.5: graphics states lose transfer functions (TR, TR2 other than Default), halftone overrides and unknown
//   rendering intents and blend modes. 6.2.8/6.2.9: pictures lose alternates, OPI and interpolation; forms lose OPI,
//   PostScript and their reference to another PDF (their own content stays).
// - 6.3: annotations of types PDF/A forbids (3D, sound, screen, movie, rich media, file attachments for level B) and
//   hidden ones are left out; every other one prints and keeps only its normal appearance, as the rules want. One
//   without an appearance gets one drawn from its own entries (PdfAAppearances); links and annotations whose
//   rectangle has no width and no height need none.
// - 6.2.11.4: fonts drawn with that aren't embedded are embedded from installed fonts of the same widths (PdfAFonts).
// - 6.4: forms lose XFA, NeedAppearances and the actions of fields and widgets; 6.5: JavaScript, launch and the other forbidden actions, and every
//   additional-actions entry, are removed (links, go-to and URI actions stay); 6.8: embedded files go (level B).
// - 6.9: optional content configurations get unique names, no AS entry and an order listing every group.
// - Other XMP packets (pages, pictures) go: the document's own is written new by PdfAMetadata.
//
// What makes it give up (ConversionRefusedException with a pdfa.reason.* key; the job queue then tries Ghostscript,
// and without it the file says why it isn't PDF/A): a font drawn with that isn't embedded and has no installed match
// (the font's name as the detail), an annotation Filee can't draw an appearance for (text its font can't encode),
// XFA-only forms, PostScript, external stream data, LZW mixed with filters PDFsharp can't unpack, inline pictures
// with LZW or interpolation, and content it can't read. A document past the walk's limits is refused as too complex.
//
// Not checked (docs/ENGINES.md lists them): that fonts embedded by the PDF hold every glyph drawn and their widths
// match, the symbolic/non-symbolic TrueType encodings of those fonts, ICC profiles of the document's own ICCBased
// colour spaces, JPEG 2000 details and the implementation limits of 6.1.13 (string, name and number sizes, q nesting).

using Filee.Core.Conversion;
using Filee.Engines.Typesetting;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using Name = Filee.Engines.Pdf.PdfContentLexer.Name;
using Operator = Filee.Engines.Pdf.PdfContentLexer.Operator;

namespace Filee.Engines.Pdf;

/// <summary>Makes a PDF opened with PDFsharp keep the rules of PDF/A-2b.</summary>
internal static class PdfAConformance
{
    /// <summary>Why a PDF can't become PDF/A: a font it draws with isn't embedded.</summary>
    public const string FontNotEmbedded = "pdfa.reason.font_not_embedded";

    /// <summary>An annotation or form field has no appearance of its own.</summary>
    public const string NoAppearance = "pdfa.reason.no_appearance";

    /// <summary>It holds something PDF/A forbids that Filee can't take out (XFA-only forms, PostScript, external data).</summary>
    public const string Unsupported = "pdfa.reason.unsupported";

    /// <summary>Filee couldn't read it.</summary>
    public const string Unreadable = "pdfa.reason.unreadable";

    /// <summary>It is locked with a password.</summary>
    public const string Locked = "pdfa.reason.locked";

    /// <summary>Objects walked at most; a real document has some ten thousand.</summary>
    internal const int MaxObjects = 2_000_000;

    /// <summary>Fields, outline items and actions in a chain followed at most.</summary>
    private const int MaxTreeNodes = 100_000;

    private const int MaxActionChain = 32;

    private const int Invisible = 1;
    private const int Hidden = 2;
    private const int Print = 4;
    private const int NoView = 32;
    private const int ToggleNoView = 256;
    private const int HiddenFlags = Invisible | Hidden | NoView | ToggleNoView;

    /// <summary>Annotation types of ISO 32000-1 that PDF/A-2b keeps (6.3.1); file attachments go with embedded files.</summary>
    private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal)
    {
        "/Text", "/Link", "/FreeText", "/Line", "/Square", "/Circle", "/Polygon", "/PolyLine", "/Highlight", "/Underline",
        "/Squiggly", "/StrikeOut", "/Stamp", "/Caret", "/Ink", "/Popup", "/Widget", "/PrinterMark", "/TrapNet",
        "/Watermark", "/Redact",
    };

    /// <summary>Actions PDF/A-2 allows (6.5.1); named actions only for these four pages.</summary>
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "/GoTo", "/GoToR", "/GoToE", "/Thread", "/URI", "/SubmitForm", "/Named",
    };

    private static readonly HashSet<string> NamedActions = new(StringComparer.Ordinal) { "/NextPage", "/PrevPage", "/FirstPage", "/LastPage" };

    private static readonly HashSet<string> BlendModes = new(StringComparer.Ordinal)
    {
        "/Normal", "/Compatible", "/Multiply", "/Screen", "/Overlay", "/Darken", "/Lighten", "/ColorDodge", "/ColorBurn",
        "/HardLight", "/SoftLight", "/Difference", "/Exclusion", "/Hue", "/Saturation", "/Color", "/Luminosity",
    };

    private static readonly HashSet<string> RenderingIntents = new(StringComparer.Ordinal)
    {
        "/RelativeColorimetric", "/AbsoluteColorimetric", "/Perceptual", "/Saturation",
    };

    private static ConversionRefusedException Refuse(string reason) => new(reason);

    /// <summary>
    /// Changes <paramref name="document"/> to keep the rules of PDF/A-2b and gives it its output intent. Save it with
    /// <see cref="PdfAMetadata.Save"/>, which writes the XMP and document information to match.
    /// </summary>
    /// <param name="fonts">The fonts to embed missing ones from; the installed ones when null.</param>
    /// <exception cref="ConversionRefusedException">It can't keep them (a <c>pdfa.reason.*</c> key, or
    /// <c>error.document_too_complex</c>, or <c>error.document_too_large</c> for its content streams' bytes). The
    /// document may be changed in part then.</exception>
    public static void Prepare(PdfDocument document, CancellationToken cancellationToken = default, IFontCollection? fonts = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = document.Internals.Catalog;
        var intents = ExistingIntents(catalog);
        catalog.Elements.Remove("/OutputIntents");
        catalog.Elements.Remove("/Metadata");
        CleanCatalog(catalog);
        CleanForm(catalog);
        CleanOutline(catalog);
        CleanOptionalContent(catalog);
        var form = Resolve(catalog.Elements["/AcroForm"]) as PdfDictionary;
        foreach (var page in document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CleanPage(document, page, form);
        }

        var walk = Walk(catalog, cancellationToken);
        foreach (var dictionary in walk.Dictionaries)
            CleanObject(dictionary);
        foreach (var state in walk.GraphicsStates)
            CleanGraphicsState(state);
        PdfAFonts.Embed(document, [.. walk.Fonts.Where(f => !PdfAFonts.IsEmbedded(f))], fonts ?? SystemFonts.Instance, cancellationToken);
        foreach (var font in walk.Fonts)
            CheckFont(font);

        var colours = new Colours();
        colours.ReadNames(walk);
        colours.ReadContent(walk.ContentStreams, cancellationToken);
        catalog.Elements["/OutputIntents"] = OutputIntents(document, intents, colours, walk);

        if (document.SecuritySettings.IsEncrypted)
            document.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
        // PDF/A-2 is based on PDF 1.7 (6.1.2); a PDF 2.0 file is written as 1.7.
        if (document.Version > 17)
            document.Version = 17;
    }

    private static PdfItem? Resolve(PdfItem? item) => PdfCompressor.Resolve(item);

    private static string Name(PdfDictionary dictionary, string key) => Resolve(dictionary.Elements[key]) is PdfName name ? name.Value : "";

    // ---- the catalog, forms, outline and optional content

    private static void CleanCatalog(PdfDictionary catalog)
    {
        if (Resolve(catalog.Elements["/NeedsRendering"]) is PdfBoolean { Value: true } or PdfBooleanObject { Value: true })
            throw Refuse(Unsupported); // an XFA form only: nothing to show without the XFA
        foreach (var key in new[] { "/AA", "/NeedsRendering", "/Requirements", "/AF", "/Collection" })
            catalog.Elements.Remove(key);
        if (Resolve(catalog.Elements["/Names"]) is PdfDictionary names)
        {
            foreach (var key in new[] { "/JavaScript", "/EmbeddedFiles", "/AlternatePresentations" })
                names.Elements.Remove(key);
        }
        if (Resolve(catalog.Elements["/OpenAction"]) is PdfDictionary { Stream: null } action && !CleanAction(action))
            catalog.Elements.Remove("/OpenAction");
        if (Resolve(catalog.Elements["/Perms"]) is PdfDictionary permissions)
        {
            foreach (var key in permissions.Elements.Keys.Where(k => k is not "/UR3" and not "/DocMDP").ToList())
                permissions.Elements.Remove(key);
        }
        if (Resolve(catalog.Elements["/Version"]) is PdfName { Value: not ("/1.0" or "/1.1" or "/1.2" or "/1.3" or "/1.4" or "/1.5" or "/1.6" or "/1.7") })
            catalog.Elements.Remove("/Version");
    }

    /// <summary>
    /// Whether <paramref name="action"/> may stay; actions chained after it (<c>/Next</c>) that may not are taken out of
    /// the chain.
    /// </summary>
    private static bool CleanAction(PdfDictionary action, int depth = 0)
    {
        var type = Name(action, "/S");
        if (!Actions.Contains(type) || (type == "/Named" && !NamedActions.Contains(Name(action, "/N"))))
            return false;
        switch (Resolve(action.Elements["/Next"]))
        {
            case PdfDictionary next when depth >= MaxActionChain || !CleanAction(next, depth + 1):
                action.Elements.Remove("/Next");
                break;
            case PdfArray chain:
                for (var i = chain.Elements.Count - 1; i >= 0; i--)
                {
                    if (depth >= MaxActionChain || Resolve(chain.Elements[i]) is not PdfDictionary next || !CleanAction(next, depth + 1))
                        chain.Elements.RemoveAt(i);
                }
                if (chain.Elements.Count == 0)
                    action.Elements.Remove("/Next");
                break;
        }
        return true;
    }

    /// <summary>The entry's action, removed when PDF/A forbids it.</summary>
    private static void CleanActionOf(PdfDictionary dictionary)
    {
        dictionary.Elements.Remove("/AA");
        if (Resolve(dictionary.Elements["/A"]) is PdfDictionary { Stream: null } action && !CleanAction(action))
            dictionary.Elements.Remove("/A");
    }

    private static void CleanForm(PdfDictionary catalog)
    {
        if (Resolve(catalog.Elements["/AcroForm"]) is not PdfDictionary form)
            return;
        form.Elements.Remove("/XFA");
        form.Elements.Remove("/NeedAppearances");
        // 6.4.1: fields and their widgets have no actions at all.
        foreach (var field in Tree(form.Elements["/Fields"], "/Kids"))
        {
            field.Elements.Remove("/A");
            field.Elements.Remove("/AA");
        }
    }

    private static void CleanOutline(PdfDictionary catalog)
    {
        if (Resolve(catalog.Elements["/Outlines"]) is not PdfDictionary outlines)
            return;
        var pending = new Stack<PdfItem?>([outlines.Elements["/First"]]);
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        while (pending.TryPop(out var next) && seen.Count < MaxTreeNodes)
        {
            if (Resolve(next) is not PdfDictionary item || !seen.Add(item))
                continue;
            CleanActionOf(item);
            pending.Push(item.Elements["/Next"]);
            pending.Push(item.Elements["/First"]);
        }
    }

    /// <summary>The dictionaries of a tree of arrays (form fields and their kids), each once, within the limits.</summary>
    private static List<PdfDictionary> Tree(PdfItem? root, string children)
    {
        var found = new List<PdfDictionary>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<PdfItem?>([root]);
        while (pending.TryPop(out var item) && found.Count < MaxTreeNodes)
        {
            switch (Resolve(item))
            {
                case PdfArray array:
                    foreach (var element in array.Elements)
                        pending.Push(element);
                    break;
                case PdfDictionary dictionary when seen.Add(dictionary):
                    found.Add(dictionary);
                    pending.Push(dictionary.Elements[children]);
                    break;
            }
        }
        return found;
    }

    /// <summary>6.9: every configuration named, uniquely, without AS, its order listing every group.</summary>
    private static void CleanOptionalContent(PdfDictionary catalog)
    {
        if (Resolve(catalog.Elements["/OCProperties"]) is not PdfDictionary properties)
            return;
        var configurations = new List<PdfDictionary>();
        if (Resolve(properties.Elements["/D"]) is PdfDictionary standard)
            configurations.Add(standard);
        if (Resolve(properties.Elements["/Configs"]) is PdfArray others)
            configurations.AddRange(others.Elements.Select(Resolve).OfType<PdfDictionary>());
        var groups = Resolve(properties.Elements["/OCGs"]) is PdfArray all
            ? all.Elements.Where(e => Resolve(e) is PdfDictionary).ToList()
            : [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        var number = 0;
        foreach (var configuration in configurations.Distinct(ReferenceEqualityComparer.Instance).Cast<PdfDictionary>())
        {
            configuration.Elements.Remove("/AS");
            var name = configuration.Elements.GetString("/Name");
            if (name.Length == 0 || !names.Add(name))
            {
                do
                    name = $"Configuration {++number}";
                while (!names.Add(name));
                configuration.Elements.SetString("/Name", name);
            }
            if (Resolve(configuration.Elements["/Order"]) is not PdfArray order)
                continue;
            var listed = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            foreach (var group in Tree(order, "/None"))
                listed.Add(group);
            foreach (var group in groups.Where(g => !listed.Contains((PdfDictionary)Resolve(g)!)))
                order.Elements.Add(group);
        }
    }

    // ---- pages and annotations

    private static void CleanPage(PdfDocument document, PdfPage page, PdfDictionary? form)
    {
        page.Elements.Remove("/AA");
        page.Elements.Remove("/PresSteps");
        if (Resolve(page.Elements["/Annots"]) is not PdfArray annotations)
            return;
        for (var i = annotations.Elements.Count - 1; i >= 0; i--)
        {
            if (Resolve(annotations.Elements[i]) is not PdfDictionary annotation || !KeepAnnotation(document, annotation, form))
                annotations.Elements.RemoveAt(i);
        }
    }

    /// <summary>Whether the annotation may stay; it is made to keep the rules when it does.</summary>
    private static bool KeepAnnotation(PdfDocument document, PdfDictionary annotation, PdfDictionary? form)
    {
        var subtype = Name(annotation, "/Subtype");
        if (!Annotations.Contains(subtype))
            return false;
        CleanActionOf(annotation);
        if (subtype == "/Widget")
            annotation.Elements.Remove("/A"); // 6.4.1
        var flags = Resolve(annotation.Elements["/F"]) switch
        {
            null => 0,
            PdfInteger value => value.Value,
            PdfIntegerObject value => value.Value,
            _ => -1,
        };
        if (subtype == "/Popup")
        {
            // A popup needs no flags; ones it may not have go (6.3.2), and it opens with its parent as before.
            if (flags < 0 || (flags & HiddenFlags) != 0)
                annotation.Elements.Remove("/F");
            else if (annotation.Elements.ContainsKey("/F"))
                annotation.Elements["/F"] = new PdfInteger(flags | Print);
            annotation.Elements.Remove("/AP");
            return true;
        }
        if (flags < 0)
            flags = 0;
        if ((flags & HiddenFlags) != 0)
            return false; // never shown: PDF/A has no hidden annotations
        annotation.Elements["/F"] = new PdfInteger(flags | Print);
        CleanAppearance(document, annotation, subtype, form);
        return true;
    }

    /// <summary>
    /// 6.3.3: an appearance dictionary with only the normal appearance, a stream, or for buttons a set of states; one
    /// drawn from the annotation's entries when it has none that fits.
    /// </summary>
    private static void CleanAppearance(PdfDocument document, PdfDictionary annotation, string subtype, PdfDictionary? form)
    {
        var button = subtype == "/Widget" && FieldType(annotation) == "/Btn";
        if (Resolve(annotation.Elements["/AP"]) is PdfDictionary appearance && Resolve(appearance.Elements["/N"]) is PdfDictionary normal)
        {
            foreach (var key in appearance.Elements.Keys.Where(k => k != "/N").ToList())
                appearance.Elements.Remove(key);
            var state = Name(annotation, "/AS");
            if (normal.Stream is not null && button)
            {
                // A button's appearance is a set of states: the one it has becomes its only state.
                if (state.Length == 0)
                    state = "/Off";
                var states = new PdfDictionary(document);
                states.Elements[state] = appearance.Elements["/N"];
                appearance.Elements["/N"] = states;
                annotation.Elements.SetName("/AS", state);
                return;
            }
            if (normal.Stream is not null || (button && normal.Elements.Values.Any(v => Resolve(v) is PdfDictionary { Stream: not null })))
                return;
            // States outside a button: the one it shows becomes its appearance.
            if (!button && state.Length > 0 && Resolve(normal.Elements[state]) is PdfDictionary { Stream: not null })
            {
                appearance.Elements["/N"] = normal.Elements[state];
                return;
            }
        }
        if (HasNoArea(annotation))
        {
            annotation.Elements.Remove("/AP");
            return;
        }
        var made = PdfAAppearances.Make(document, annotation, subtype, form);
        if (made is null)
        {
            if (subtype != "/Link")
                throw Refuse(NoAppearance);
            annotation.Elements.Remove("/AP"); // a link needs none (6.3.3)
            return;
        }
        var drawn = new PdfDictionary(document);
        drawn.Elements["/N"] = made;
        annotation.Elements["/AP"] = drawn;
    }

    /// <summary>A rectangle of no width and no height (6.3.3 asks no appearance of those).</summary>
    private static bool HasNoArea(PdfDictionary annotation)
    {
        if (Resolve(annotation.Elements["/Rect"]) is not PdfArray { Elements.Count: 4 } rect)
            return true;
        double At(int i) => Resolve(rect.Elements[i]) switch
        {
            PdfInteger value => value.Value,
            PdfReal value => value.Value,
            PdfIntegerObject value => value.Value,
            PdfRealObject value => value.Value,
            _ => 0,
        };
        return At(0) == At(2) && At(1) == At(3);
    }

    /// <summary>A widget's field type, from it or the fields it is a kid of.</summary>
    private static string FieldType(PdfDictionary widget)
    {
        PdfDictionary? field = widget;
        for (var depth = 0; field is not null && depth < 64; depth++)
        {
            if (Name(field, "/FT") is { Length: > 0 } type)
                return type;
            field = Resolve(field.Elements["/Parent"]) as PdfDictionary;
        }
        return "";
    }

    // ---- every object

    /// <summary>What the walk from the catalog reached.</summary>
    private sealed class Reached
    {
        public List<PdfDictionary> Dictionaries { get; } = [];
        public List<PdfArray> Arrays { get; } = [];
        public HashSet<PdfDictionary> Fonts { get; } = new(ReferenceEqualityComparer.Instance);
        public HashSet<PdfDictionary> GraphicsStates { get; } = new(ReferenceEqualityComparer.Instance);
        public HashSet<PdfDictionary> ContentStreams { get; } = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>
    /// Every dictionary and array the catalog leads to, each once (from a work stack, not by recursion), and among
    /// them the fonts and graphics states resources list and the content streams.
    /// </summary>
    private static Reached Walk(PdfDictionary catalog, CancellationToken cancellationToken)
    {
        var reached = new Reached();
        var seen = new HashSet<PdfItem>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<PdfItem?>([catalog]);
        while (pending.TryPop(out var item))
        {
            switch (Resolve(item))
            {
                case PdfDictionary dictionary when seen.Add(dictionary):
                    reached.Dictionaries.Add(dictionary);
                    foreach (var (_, value) in dictionary.Elements)
                        pending.Push(value);
                    break;
                case PdfArray array when seen.Add(array):
                    reached.Arrays.Add(array);
                    foreach (var value in array.Elements)
                        pending.Push(value);
                    break;
                default:
                    continue;
            }
            if (seen.Count > MaxObjects)
                throw new ConversionRefusedException("error.document_too_complex");
            if (seen.Count % 4096 == 0)
                cancellationToken.ThrowIfCancellationRequested();
        }

        foreach (var dictionary in reached.Dictionaries)
        {
            // AcroForm's /DR lists fonts for filling in fields, not for drawing: only /Resources count.
            if (Resolve(dictionary.Elements["/Resources"]) is PdfDictionary resources)
            {
                Add(reached.Fonts, resources.Elements["/Font"]);
                Add(reached.GraphicsStates, resources.Elements["/ExtGState"]);
            }
            if (dictionary.Stream is not null && (Name(dictionary, "/Subtype") == "/Form" || IsTilingPattern(dictionary)))
                reached.ContentStreams.Add(dictionary);
            if (Name(dictionary, "/Type") == "/Page")
                AddStreams(reached.ContentStreams, dictionary.Elements["/Contents"]);
            if (Name(dictionary, "/Subtype") == "/Type3" && Resolve(dictionary.Elements["/CharProcs"]) is PdfDictionary glyphs)
                AddStreams(reached.ContentStreams, glyphs);
            if (Resolve(dictionary.Elements["/AP"]) is PdfDictionary appearance)
                AddStreams(reached.ContentStreams, appearance.Elements["/N"]);
        }
        return reached;

        static void Add(HashSet<PdfDictionary> set, PdfItem? listing)
        {
            if (Resolve(listing) is PdfDictionary entries)
            {
                foreach (var (_, value) in entries.Elements)
                {
                    if (Resolve(value) is PdfDictionary entry)
                        set.Add(entry);
                }
            }
        }
    }

    private static bool IsTilingPattern(PdfDictionary dictionary) =>
        Resolve(dictionary.Elements["/PatternType"]) is PdfInteger { Value: 1 } or PdfIntegerObject { Value: 1 };

    /// <summary>A stream, an array of streams or a dictionary of them (states, Type 3 glyphs).</summary>
    private static void AddStreams(HashSet<PdfDictionary> set, PdfItem? item)
    {
        switch (Resolve(item))
        {
            case PdfDictionary { Stream: not null } stream:
                set.Add(stream);
                break;
            case PdfDictionary states:
                foreach (var (_, value) in states.Elements)
                {
                    if (Resolve(value) is PdfDictionary { Stream: not null } state)
                        set.Add(state);
                }
                break;
            case PdfArray parts:
                foreach (var part in parts.Elements)
                {
                    if (Resolve(part) is PdfDictionary { Stream: not null } stream)
                        set.Add(stream);
                }
                break;
        }
    }

    /// <summary>Streams, pictures, forms and XMP packets of one object.</summary>
    private static void CleanObject(PdfDictionary dictionary)
    {
        // Only the document has an XMP packet (written new on saving).
        dictionary.Elements.Remove("/Metadata");
        var subtype = Name(dictionary, "/Subtype");
        if (dictionary.Stream is { } stream)
        {
            if (dictionary.Elements.ContainsKey("/F") || dictionary.Elements.ContainsKey("/FFilter") || dictionary.Elements.ContainsKey("/FDecodeParms"))
                throw Refuse(Unsupported); // data in another file (6.1.7.1)
            var filters = Filters(dictionary);
            if (filters.Contains("/Crypt"))
                throw Refuse(Unsupported);
            if (filters.Contains("/LZWDecode"))
            {
                // 6.1.7.2: no LZW. Unpacked (with the filters around it, when PDFsharp knows them all) and deflated.
                if (!filters.All(UnpackedFilters.Contains))
                    throw Refuse(Unsupported);
                var data = stream.UnfilteredValue;
                dictionary.Elements.Remove("/Filter");
                dictionary.Elements.Remove("/DecodeParms");
                stream.Value = data;
                stream.Zip();
            }
            if (subtype == "/PS")
                throw Refuse(Unsupported); // a PostScript XObject (6.2.9)
        }
        switch (subtype)
        {
            case "/Image":
                dictionary.Elements.Remove("/Alternates");
                dictionary.Elements.Remove("/OPI");
                dictionary.Elements.Remove("/Interpolate");
                break;
            case "/Form":
                if (Name(dictionary, "/Subtype2") == "/PS")
                    throw Refuse(Unsupported);
                dictionary.Elements.Remove("/OPI");
                dictionary.Elements.Remove("/PS");
                dictionary.Elements.Remove("/Ref"); // the form's own content is the stand-in
                break;
        }
    }

    /// <summary>Filters PDFsharp unpacks, predictors and all.</summary>
    private static readonly HashSet<string> UnpackedFilters = new(StringComparer.Ordinal)
    {
        "/LZWDecode", "/FlateDecode", "/ASCII85Decode", "/ASCIIHexDecode",
    };

    private static string[] Filters(PdfDictionary dictionary) => Resolve(dictionary.Elements["/Filter"]) switch
    {
        PdfName name => [name.Value],
        PdfArray array => [.. array.Elements.Select(e => Resolve(e) is PdfName n ? n.Value : "?")],
        _ => [],
    };

    /// <summary>6.2.5: no transfer functions, halftone overrides, unknown rendering intents or blend modes.</summary>
    private static void CleanGraphicsState(PdfDictionary state)
    {
        state.Elements.Remove("/TR");
        if (state.Elements.ContainsKey("/TR2") && Name(state, "/TR2") != "/Default")
            state.Elements.SetName("/TR2", "/Default");
        state.Elements.Remove("/HTO");
        state.Elements.Remove("/HT"); // the device's own halftone; it only matters on a printing press
        if (state.Elements.ContainsKey("/RI") && !RenderingIntents.Contains(Name(state, "/RI")))
            state.Elements.Remove("/RI");
        switch (Resolve(state.Elements["/BM"]))
        {
            case null:
                break;
            case PdfName name when BlendModes.Contains(name.Value):
                break;
            case PdfArray modes:
                // A list: the first one a reader knows is used, so that one stays.
                var known = modes.Elements.Select(Resolve).OfType<PdfName>().FirstOrDefault(m => BlendModes.Contains(m.Value))?.Value;
                state.Elements.SetName("/BM", known ?? "/Normal");
                break;
            default:
                state.Elements.SetName("/BM", "/Normal");
                break;
        }
    }

    /// <summary>6.2.11.3.2: a CIDFontType2 says how CIDs map to glyphs; Identity is what PDF readers assume without it.</summary>
    private static void CheckFont(PdfDictionary font)
    {
        if (Name(font, "/Subtype") != "/Type0" || Resolve(font.Elements["/DescendantFonts"]) is not PdfArray descendants)
            return;
        foreach (var cidFont in descendants.Elements.Select(Resolve).OfType<PdfDictionary>())
        {
            if (Name(cidFont, "/Subtype") == "/CIDFontType2" && !cidFont.Elements.ContainsKey("/CIDToGIDMap"))
                cidFont.Elements.SetName("/CIDToGIDMap", "/Identity");
        }
    }

    // ---- colours and the output intent

    /// <summary>The device colour spaces a document draws in, from its objects and its content streams.</summary>
    private sealed class Colours
    {
        private long _bytes;
        private long _tokens;

        public bool Rgb { get; private set; }
        public bool Cmyk { get; private set; }

        /// <summary>Content streams that draw in a device space (DeviceRGB or DeviceCMYK), and whether they use resources.</summary>
        public Dictionary<PdfDictionary, (bool Rgb, bool Cmyk, bool Resources)> Streams { get; } = new(ReferenceEqualityComparer.Instance);

        private bool _streamRgb;
        private bool _streamCmyk;

        private void See(string name)
        {
            switch (name)
            {
                case "DeviceRGB" or "RGB":
                    Rgb = _streamRgb = true;
                    break;
                case "DeviceCMYK" or "CMYK":
                    Cmyk = _streamCmyk = true;
                    break;
            }
        }

        /// <summary>Colour space names in the objects: pictures, colour spaces of resources, shadings, groups.</summary>
        public void ReadNames(Reached reached)
        {
            foreach (var dictionary in reached.Dictionaries)
            {
                var icc = dictionary.Stream is not null && dictionary.Elements.ContainsKey("/N");
                foreach (var (key, value) in dictionary.Elements)
                {
                    // An ICC profile's alternate is used only when the profile can't be: not a use of its own.
                    if (key == "/Alternate" && icc)
                        continue;
                    if (Resolve(value) is PdfName { Value: "/DeviceRGB" or "/DeviceCMYK" } name)
                        See(name.Value[1..]);
                }
            }
            foreach (var array in reached.Arrays)
            {
                foreach (var value in array.Elements)
                {
                    if (Resolve(value) is PdfName { Value: "/DeviceRGB" or "/DeviceCMYK" } name)
                        See(name.Value[1..]);
                }
            }
        }

        /// <summary>Colour operators and colour space names in the content streams, inline pictures' too.</summary>
        public void ReadContent(IEnumerable<PdfDictionary> streams, CancellationToken cancellationToken)
        {
            foreach (var stream in streams)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] data;
                try
                {
                    data = stream.Stream!.UnfilteredValue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not ConversionRefusedException)
                {
                    throw Refuse(Unreadable);
                }
                _bytes += data.Length;
                if (_bytes > PdfFontUses.MaxContentBytes)
                    throw new ConversionRefusedException(Documents.DocumentLimits.TooLargeKey);
                bool resources;
                (_streamRgb, _streamCmyk) = (false, false);
                try
                {
                    resources = Read(new PdfContentLexer(data));
                }
                catch (InvalidDataException)
                {
                    throw Refuse(Unreadable);
                }
                if (_streamRgb || _streamCmyk)
                    Streams[stream] = (_streamRgb, _streamCmyk, resources);
            }
        }

        /// <summary>Reads the colours of one stream; true when it names resources (fonts, forms, states, spaces …).</summary>
        private bool Read(PdfContentLexer lexer)
        {
            var resources = false;
            object? previous = null;
            while (lexer.Next() is { } token)
            {
                if (++_tokens > PdfFontUses.MaxWork)
                    throw new ConversionRefusedException("error.document_too_complex");
                switch (token)
                {
                    case Operator { Name: "k" or "K" }:
                        See("CMYK");
                        break;
                    case Operator { Name: "rg" or "RG" }:
                        See("RGB");
                        break;
                    case Name { Value: "DeviceRGB" or "DeviceCMYK" } name:
                        See(name.Value);
                        break;
                    case Operator { Name: "BI" }:
                        InlineImage(lexer.ReadInlineImage(_ => null).Parameters);
                        break;
                    case Operator { Name: "cs" or "CS" } when previous is Name { Value: "DeviceRGB" or "DeviceCMYK" or "DeviceGray" }:
                        break;
                    case Operator { Name: "Tf" or "Do" or "gs" or "sh" or "cs" or "CS" or "scn" or "SCN" or "BDC" or "DP" }:
                        resources = true;
                        break;
                }
                previous = token;
            }
            return resources;
        }

        private void InlineImage(Dictionary<string, object?> parameters)
        {
            foreach (var (key, value) in parameters)
            {
                switch (key)
                {
                    case "CS" or "ColorSpace":
                        Names(value, See);
                        break;
                    case "F" or "Filter":
                        Names(value, filter =>
                        {
                            if (filter is "LZW" or "LZWDecode")
                                throw Refuse(Unsupported); // content can't be rewritten here
                        });
                        break;
                    case "I" or "Interpolate" when value is Operator { Name: "true" }:
                        throw Refuse(Unsupported);
                }
            }
        }

        private static void Names(object? value, Action<string> each)
        {
            switch (value)
            {
                case Name name:
                    each(name.Value);
                    break;
                case List<object?> list:
                    foreach (var item in list)
                    {
                        if (item is Name inner)
                            each(inner.Value);
                    }
                    break;
            }
        }
    }

    /// <summary>The output intents a PDF brings, with the components of their profiles.</summary>
    private static List<(PdfDictionary Intent, PdfDictionary Profile, int Components, bool PdfA)> ExistingIntents(PdfDictionary catalog)
    {
        var found = new List<(PdfDictionary, PdfDictionary, int, bool)>();
        if (Resolve(catalog.Elements["/OutputIntents"]) is not PdfArray intents)
            return found;
        foreach (var item in intents.Elements)
        {
            if (Resolve(item) is not PdfDictionary intent || Resolve(intent.Elements["/DestOutputProfile"]) is not PdfDictionary { Stream: not null } profile)
                continue;
            byte[] bytes;
            try
            {
                bytes = profile.Stream!.UnfilteredValue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ConversionRefusedException)
            {
                continue;
            }
            // 6.2.3: an output or display profile of ICC version 4 at most, of RGB or CMYK.
            if (bytes.Length < 128 || bytes[8] > 4 || System.Text.Encoding.ASCII.GetString(bytes, 12, 4) is not ("prtr" or "mntr"))
                continue;
            var components = System.Text.Encoding.ASCII.GetString(bytes, 16, 4) switch
            {
                "RGB " => 3,
                "CMYK" => 4,
                _ => 0,
            };
            if (components == 0 || profile.Elements.GetInteger("/N") != components)
                continue;
            found.Add((intent, profile, components, Name(intent, "/S") == "/GTS_PDFA1"));
        }
        return found;
    }

    /// <summary>
    /// The PDF/A output intent: a CMYK one the PDF brings when it draws in device CMYK, else an RGB one it brings, else
    /// sRGB (a PDF/A intent first). The device space the intent isn't for is made device independent
    /// (<see cref="MakeIndependent"/>).
    /// </summary>
    private static PdfArray OutputIntents(PdfDocument document, List<(PdfDictionary Intent, PdfDictionary Profile, int Components, bool PdfA)> existing,
        Colours colours, Reached walk)
    {
        var kept = colours.Cmyk ? existing.Where(e => e.Components == 4).OrderByDescending(e => e.PdfA).FirstOrDefault() : default;
        var cmyk = kept.Profile is not null;
        if (!cmyk)
            kept = existing.Where(e => e.Components == 3).OrderByDescending(e => e.PdfA).FirstOrDefault();
        // PDFsharp gives every page without one a transparency group in this colour space when it saves.
        document.Options.ColorMode = cmyk ? PdfColorMode.Cmyk : PdfColorMode.Rgb;
        if (cmyk && colours.Rgb)
            MakeIndependent(document, walk, colours, rgb: true);
        else if (!cmyk && colours.Cmyk)
            MakeIndependent(document, walk, colours, rgb: false);

        var intent = new PdfDictionary(document);
        intent.Elements.SetName("/Type", "/OutputIntent");
        intent.Elements.SetName("/S", "/GTS_PDFA1");
        if (kept.Profile is not null)
        {
            var identifier = kept.Intent.Elements.GetString("/OutputConditionIdentifier");
            intent.Elements.SetString("/OutputConditionIdentifier", identifier.Length > 0 ? identifier : "Custom");
            foreach (var key in new[] { "/OutputCondition", "/RegistryName", "/Info" })
            {
                if (kept.Intent.Elements.GetString(key) is { Length: > 0 } text)
                    intent.Elements.SetString(key, text);
            }
            intent.Elements["/DestOutputProfile"] = kept.Profile.Reference ?? (PdfItem)kept.Profile;
        }
        else
        {
            intent.Elements.SetString("/OutputConditionIdentifier", SrgbProfile.Name);
            intent.Elements.SetString("/RegistryName", "http://www.color.org");
            intent.Elements.SetString("/Info", SrgbProfile.Name);
            intent.Elements["/DestOutputProfile"] = Profile(document, SrgbProfile.Bytes, 3).Reference;
        }
        var intents = new PdfArray(document);
        intents.Elements.Add(intent);
        return intents;
    }

    private static PdfDictionary Profile(PdfDocument document, byte[] bytes, int components)
    {
        var profile = new PdfDictionary(document);
        profile.Elements.SetInteger("/N", components);
        profile.CreateStream(bytes);
        profile.Stream.Zip();
        document.Internals.AddObject(profile);
        return profile;
    }

    /// <summary>
    /// 6.2.4.3: DeviceCMYK under an RGB intent (or DeviceRGB under a CMYK one) used device independently. Where an
    /// object names the device space (a picture's, shading's or group's colour space, a colour space resource, the base
    /// or alternate of another space), it names an ICCBased space of <see cref="CmykProfile"/> (or sRGB) instead; for
    /// the operators of content streams (k, K, the device space by name, inline pictures), every resource dictionary
    /// gets the same space as DefaultCMYK (or DefaultRGB), and so does a page without resources, or a form that draws
    /// in the device space and names no resources.
    /// </summary>
    private static void MakeIndependent(PdfDocument document, Reached walk, Colours colours, bool rgb)
    {
        var device = rgb ? "/DeviceRGB" : "/DeviceCMYK";
        var key = rgb ? "/DefaultRGB" : "/DefaultCMYK";
        var space = new PdfArray(document);
        space.Elements.Add(new PdfName("/ICCBased"));
        space.Elements.Add(Profile(document, rgb ? SrgbProfile.Bytes : CmykProfile.Bytes, rgb ? 3 : 4).Reference!);
        document.Internals.AddObject(space);

        foreach (var dictionary in walk.Dictionaries)
        {
            // An ICC profile's alternate is used only when the profile can't be.
            var icc = dictionary.Stream is not null && dictionary.Elements.ContainsKey("/N");
            foreach (var name in dictionary.Elements.Keys.ToList())
            {
                if (!(name == "/Alternate" && icc) && Resolve(dictionary.Elements[name]) is PdfName value && value.Value == device)
                    dictionary.Elements[name] = space.Reference!;
            }
        }
        foreach (var array in walk.Arrays)
        {
            for (var i = 0; i < array.Elements.Count; i++)
            {
                if (Resolve(array.Elements[i]) is PdfName value && value.Value == device)
                    array.Elements[i] = space.Reference!;
            }
        }

        void SetDefault(PdfDictionary resources)
        {
            if (Resolve(resources.Elements["/ColorSpace"]) is not PdfDictionary spaces)
            {
                spaces = new PdfDictionary(document);
                resources.Elements["/ColorSpace"] = spaces;
            }
            if (!spaces.Elements.ContainsKey(key))
                spaces.Elements[key] = space.Reference!;
        }

        foreach (var dictionary in walk.Dictionaries)
        {
            if (Resolve(dictionary.Elements["/Resources"]) is PdfDictionary resources)
                SetDefault(resources);
        }
        foreach (var (stream, use) in colours.Streams)
        {
            if (!(rgb ? use.Rgb : use.Cmyk) || stream.Elements.ContainsKey("/Resources") || use.Resources)
                continue;
            var resources = new PdfDictionary(document);
            SetDefault(resources);
            stream.Elements["/Resources"] = resources;
        }
        foreach (var page in document.Pages)
        {
            if (!HasResources(page))
            {
                var resources = new PdfDictionary(document);
                SetDefault(resources);
                page.Elements["/Resources"] = resources;
            }
        }

        static bool HasResources(PdfDictionary page)
        {
            PdfDictionary? node = page;
            for (var depth = 0; node is not null && depth < 64; depth++)
            {
                if (Resolve(node.Elements["/Resources"]) is PdfDictionary)
                    return true;
                node = Resolve(node.Elements["/Parent"]) as PdfDictionary;
            }
            return false;
        }
    }
}
