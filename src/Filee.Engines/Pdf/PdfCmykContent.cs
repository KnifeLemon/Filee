// Tells whether the pages of a PDF draw in CMYK: DeviceCMYK colours (the k / K operators), four-channel ICC colour
// spaces, spot colours (Separation, DeviceN) and CMYK pictures. Print files from Illustrator, InDesign and co. do;
// PdfiumConverter renders them through Ghostscript when the target holds CMYK (JPG, TIFF), so the ink values survive
// (issue #1027). Also finds the CMYK profile the document's output intent names, which the pictures then carry.

using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Tokens;

namespace Filee.Engines.Pdf;

internal static class PdfCmykContent
{
    /// <summary>Pages looked at: a print file shows its CMYK on the first ones, and a long book is not read twice.</summary>
    internal const int MaxPages = 20;

    /// <summary>Resource dictionaries looked at per page (forms in forms, patterns, ...), so a strange file can't keep it busy.</summary>
    private const int MaxResources = 500;

    /// <param name="UsesCmyk">The pages draw in CMYK.</param>
    /// <param name="OutputProfile">The CMYK ICC profile of the document's output intent, if it has one.</param>
    public readonly record struct Findings(bool UsesCmyk, byte[]? OutputProfile);

    /// <summary>
    /// Looks at <paramref name="pages"/> (zero-based, the first <see cref="MaxPages"/> of them). A PDF PdfPig can't
    /// read counts as not CMYK: PDFium renders it as before.
    /// </summary>
    public static Findings Inspect(string path, IReadOnlyList<int> pages)
    {
        try
        {
            using var pdf = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            var cmyk = pages.Where(p => p >= 0 && p < pdf.NumberOfPages).Take(MaxPages).Any(p => UsesCmyk(pdf, pdf.GetPage(p + 1)));
            return new Findings(cmyk, cmyk ? OutputProfile(pdf) : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return default;
        }
    }

    private static bool UsesCmyk(PdfDocument pdf, Page page)
    {
        if (page.Operations.Any(op => op is SetNonStrokeColorDeviceCmyk or SetStrokeColorDeviceCmyk
                || op is SetNonStrokeColorSpace { Name.Data: "DeviceCMYK" } or SetStrokeColorSpace { Name.Data: "DeviceCMYK" }))
            return true;
        // Colours set inside forms (Illustrator puts transparency groups there) show on what they drew.
        if (page.Letters.Any(l => IsCmyk(l.FillColor) || IsCmyk(l.StrokeColor))
            || page.Paths.Any(p => IsCmyk(p.FillColor) || IsCmyk(p.StrokeColor)))
            return true;
        if (page.GetImages().Any(image => IsCmyk(pdf, image.ColorSpaceDetails)))
            return true;
        return ResourcesUseCmyk(pdf, page.Dictionary);
    }

    private static bool IsCmyk(IColor? color) => color?.ColorSpace == ColorSpace.DeviceCMYK;

    private static bool IsCmyk(PdfDocument pdf, ColorSpaceDetails? details) => details switch
    {
        null => false,
        ICCBasedColorSpaceDetails icc => icc.NumberOfColorComponents == 4,
        IndexedColorSpaceDetails indexed => IsCmyk(pdf, indexed.BaseColorSpace),
        _ => details.Type is ColorSpace.DeviceCMYK or ColorSpace.Separation or ColorSpace.DeviceN,
    };

    /// <summary>
    /// The colour spaces the page's resources define, its shadings (gradients), the pictures and forms it draws and
    /// their own resources: a gradient or a spot colour shows nowhere else.
    /// </summary>
    private static bool ResourcesUseCmyk(PdfDocument pdf, DictionaryToken page)
    {
        var pending = new Stack<DictionaryToken>();
        if (Inherited(pdf, page, "Resources") is { } resources)
            pending.Push(resources);
        var seen = new HashSet<DictionaryToken>(ReferenceEqualityComparer.Instance);
        while (pending.Count > 0 && seen.Count < MaxResources)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
                continue;
            foreach (var space in Entries(pdf, current, "ColorSpace"))
            {
                if (IsCmykSpace(pdf, space, 0))
                    return true;
            }
            foreach (var shading in Entries(pdf, current, "Shading"))
            {
                if (IsCmykSpace(pdf, Get(pdf, Dictionary(pdf, shading), "ColorSpace"), 0))
                    return true;
            }
            foreach (var pattern in Entries(pdf, current, "Pattern"))
            {
                var dictionary = Dictionary(pdf, pattern);
                if (IsCmykSpace(pdf, Get(pdf, Dictionary(pdf, Get(pdf, dictionary, "Shading")), "ColorSpace"), 0))
                    return true;
                if (Dictionary(pdf, Get(pdf, dictionary, "Resources")) is { } inner)
                    pending.Push(inner);
            }
            foreach (var item in Entries(pdf, current, "XObject"))
            {
                var xobject = Dictionary(pdf, item);
                if (Get(pdf, xobject, "Subtype") is NameToken { Data: "Image" })
                {
                    if (IsCmykSpace(pdf, Get(pdf, xobject, "ColorSpace"), 0))
                        return true;
                }
                else if (Dictionary(pdf, Get(pdf, xobject, "Resources")) is { } inner)
                {
                    pending.Push(inner);
                }
            }
        }
        return false;
    }

    /// <summary>A colour space as the file writes it: a name, or an array such as [/ICCBased 12 0 R].</summary>
    private static bool IsCmykSpace(PdfDocument pdf, IToken? token, int depth)
    {
        switch (Resolve(pdf, token))
        {
            case NameToken name:
                return name.Data is "DeviceCMYK" or "CMYK";
            case ArrayToken { Length: > 0 } array when Resolve(pdf, array[0]) is NameToken family:
                return family.Data switch
                {
                    "ICCBased" => array.Length > 1 && Get(pdf, Dictionary(pdf, array[1]), "N") is NumericToken { Int: 4 },
                    "Separation" or "DeviceN" => true,
                    // An indexed palette or a pattern's colours: what the base colour space is.
                    "Indexed" or "I" or "Pattern" => array.Length > 1 && depth < 4 && IsCmykSpace(pdf, array[1], depth + 1),
                    _ => false,
                };
            default:
                return false;
        }
    }

    /// <summary>
    /// The ICC profile of the first output intent whose profile is CMYK (a PDF/X file names the printing condition it
    /// was made for that way), checked by the colour space in the profile's header.
    /// </summary>
    private static byte[]? OutputProfile(PdfDocument pdf)
    {
        if (Get(pdf, pdf.Structure.Catalog.CatalogDictionary, "OutputIntents") is not ArrayToken intents)
            return null;
        foreach (var intent in intents.Data)
        {
            if (Get(pdf, Dictionary(pdf, intent), "DestOutputProfile") is not StreamToken stream)
                continue;
            var profile = stream.Decode(pdf.Structure.FilterProvider, pdf.Structure.TokenScanner).ToArray();
            if (profile.Length >= 128 && profile.AsSpan(16, 4).SequenceEqual("CMYK"u8))
                return profile;
        }
        return null;
    }

    /// <summary>A page attribute that may sit on the page or on one of its parent page-tree nodes.</summary>
    private static DictionaryToken? Inherited(PdfDocument pdf, DictionaryToken page, string key)
    {
        var node = page;
        for (var level = 0; node is not null && level < 32; level++)
        {
            if (Dictionary(pdf, Get(pdf, node, key)) is { } value)
                return value;
            node = Dictionary(pdf, Get(pdf, node, "Parent"));
        }
        return null;
    }

    /// <summary>The values of the sub-dictionary <paramref name="key"/> (a resource category such as /XObject).</summary>
    private static IEnumerable<IToken> Entries(PdfDocument pdf, DictionaryToken dictionary, string key) =>
        Dictionary(pdf, Get(pdf, dictionary, key))?.Data.Values ?? [];

    private static IToken? Get(PdfDocument pdf, DictionaryToken? dictionary, string key) =>
        dictionary is not null && dictionary.Data.TryGetValue(key, out var value) ? Resolve(pdf, value) : null;

    private static DictionaryToken? Dictionary(PdfDocument pdf, IToken? token) => Resolve(pdf, token) switch
    {
        DictionaryToken dictionary => dictionary,
        StreamToken stream => stream.StreamDictionary,
        _ => null,
    };

    private static IToken? Resolve(PdfDocument pdf, IToken? token)
    {
        for (var hops = 0; token is IndirectReferenceToken reference && hops < 8; hops++)
            token = pdf.Structure.GetObject(reference.Data)?.Data;
        return token is IndirectReferenceToken ? null : token;
    }
}
