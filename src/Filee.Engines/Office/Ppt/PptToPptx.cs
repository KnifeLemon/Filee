// PowerPoint 97–2003 (.ppt, .pps, .pot) → the PPTX package PowerPoint would save, so the slide renderer
// (Slides/PptxSlideReader), the document readers (PptxReader) and PPTX output all take a binary deck the way they take
// PPTX, as DOC → DOCX and HWP → HWPX do. Our own reading of [MS-PPT] and [MS-ODRAW]; PptFile has the streams and lists.
//
// The parts:
// - presentation.xml: the slide size (DocumentAtom), the slides in the slide list's order (hidden ones marked), the
//   default text style (the Environment's text master style).
// - Masters (PptToPptx.Masters): each main master becomes a slide master with its theme (the colour scheme, the
//   title and body fonts), its background and shapes, and its text styles. A slide with a colour scheme of its own gets
//   a copy of its master whose theme holds that scheme, so its colours (scheme indexes, written as theme colours) come
//   out as PowerPoint shows them. Each master has one layout of its own (title and body placeholders), and one for each
//   title master on it, holding the title master's shapes.
// - Slides and notes (PptToPptx.Shapes, .Text, .Geometry, .Fills): the drawing's shapes in order (preset
//   and custom geometry, solid, picture, pattern and gradient fills, shadows, lines with dashes and arrowheads, pictures
//   with crops, groups, WordArt), placeholders (OEPlaceholderAtom) with their text from the slide list's outline or the
//   shape's own text box, the background; notes pages as notes slides with their text.
//
// Left for later: adjust values of presets (they take their default shape), tables as tables (they are drawn as the
// group of shapes PowerPoint 97–2003 keeps them as), headers and footers on slides, numbered lists, hyperlinks,
// animations, sounds, OLE objects other than their picture.
// What the translation leaves out that shows is in Dropped, which the capability check names.
//
// Hostile input: everything PptFile bounds, plus DocumentBudget for shapes (MaxObjects), paragraphs (MaxParagraphs)
// and group nesting (MaxNestingDepth); copies of masters for colour schemes are capped (MaxVariants); pictures inflate
// through OfficeArtBlips with the document's counter and limits.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx.Pptx;
using Filee.Engines.Office.OfficeArt;

namespace Filee.Engines.Office.Ppt;

internal sealed partial class PptToPptx
{
    /// <summary>EMU per master unit (576 per inch).</summary>
    private const double EmuPerMasterUnit = 914400.0 / 576;

    /// <summary>Theme colours of the colour scheme's eight entries (background, text, shadows, title, fills, accents).</summary>
    private static readonly string[] SchemeSlots = ["bg1", "tx1", "bg2", "tx2", "accent1", "accent2", "accent3", "accent4"];

    private const string PresentationTypes = PresentationPackage.PresentationTypes;
    private const string Namespaces = PresentationPackage.Namespaces;

    private readonly PptFile _file;
    private readonly ZipArchive _zip;
    private readonly DocumentBudget _budget;
    private readonly CancellationToken _cancellationToken;
    private readonly List<(string Part, string Type)> _overrides = [];
    private readonly Dictionary<string, string> _defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rels"] = "application/vnd.openxmlformats-package.relationships+xml",
        ["xml"] = "application/xml",
    };

    private PptToPptx(PptFile file, ZipArchive zip, CancellationToken cancellationToken)
    {
        _file = file;
        _zip = zip;
        _budget = new DocumentBudget(file.Limits);
        _cancellationToken = cancellationToken;
    }

    /// <summary>The first thing in the deck the translation leaves out that would show on a slide, or null.</summary>
    public string? Dropped { get; private set; }

    /// <summary>Writes the PPTX package of <paramref name="file"/> to <paramref name="output"/> (left open).</summary>
    /// <returns>What the translation left out that shows (for the capability check), or null.</returns>
    public static string? Write(PptFile file, Stream output, CancellationToken cancellationToken = default)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var writer = new PptToPptx(file, zip, cancellationToken);
        writer.WritePackage();
        return writer.Dropped;
    }

    private void Drop(string feature) => Dropped ??= feature;

    // ───────────────────────── Package ─────────────────────────

    private void WritePackage()
    {
        ReadMasters();
        var slides = PlanSlides();
        var notes = slides.Count(s => s.Notes is not null);

        foreach (var variant in _variants)
            WriteMaster(variant);
        for (var i = 0; i < slides.Count; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            WriteSlide(slides[i], i + 1);
        }
        var notesMaster = notes > 0;
        if (notesMaster)
            WriteNotesMaster(_variants.Count + 1);

        var rels = new List<(string Id, string Type, string Target)>();
        var masters = new StringBuilder();
        foreach (var variant in _variants)
        {
            rels.Add(($"rId{rels.Count + 1}", "slideMaster", $"slideMasters/slideMaster{variant.Number}.xml"));
            masters.Append(CultureInfo.InvariantCulture, $"<p:sldMasterId id=\"{variant.Id}\" r:id=\"rId{rels.Count}\"/>");
        }
        var list = new StringBuilder();
        for (var i = 0; i < slides.Count; i++)
        {
            rels.Add(($"rId{rels.Count + 1}", "slide", $"slides/slide{i + 1}.xml"));
            list.Append(CultureInfo.InvariantCulture, $"<p:sldId id=\"{256 + i}\" r:id=\"rId{rels.Count}\"/>");
        }
        var notesList = "";
        if (notesMaster)
        {
            rels.Add(($"rId{rels.Count + 1}", "notesMaster", "notesMasters/notesMaster1.xml"));
            notesList = $"<p:notesMasterIdLst><p:notesMasterId r:id=\"rId{rels.Count}\"/></p:notesMasterIdLst>";
        }
        rels.Add(($"rId{rels.Count + 1}", "theme", $"theme/theme{_variants[0].Number}.xml"));
        rels.Add(($"rId{rels.Count + 1}", "presProps", "presProps.xml"));
        rels.Add(($"rId{rels.Count + 1}", "viewProps", "viewProps.xml"));
        rels.Add(($"rId{rels.Count + 1}", "tableStyles", "tableStyles.xml"));

        var (width, height) = SlideSize();
        var defaults = new StringBuilder("<p:defaultTextStyle>");
        if (_file.DefaultTextStyle is { } style)
            WriteLevels(defaults, ReadMasterStyle(style), full: true);
        defaults.Append("</p:defaultTextStyle>");
        Write("ppt/presentation.xml",
            $"<p:presentation {Namespaces} saveSubsetFonts=\"1\"><p:sldMasterIdLst>{masters}</p:sldMasterIdLst>{notesList}" +
            (slides.Count > 0 ? $"<p:sldIdLst>{list}</p:sldIdLst>" : "") +
            $"<p:sldSz cx=\"{width}\" cy=\"{height}\"/><p:notesSz cx=\"{PresentationPackage.NotesWidth}\" cy=\"{PresentationPackage.NotesHeight}\"/>" +
            $"{defaults}</p:presentation>",
            PresentationTypes + "presentation.main+xml");
        Write("ppt/_rels/presentation.xml.rels", PresentationPackage.Rels([.. rels]));

        Write("ppt/presProps.xml", $"<p:presentationPr {Namespaces}/>", PresentationTypes + "presProps+xml");
        Write("ppt/viewProps.xml", $"<p:viewPr {Namespaces}><p:gridSpacing cx=\"76200\" cy=\"76200\"/></p:viewPr>", PresentationTypes + "viewProps+xml");
        Write("ppt/tableStyles.xml",
            "<a:tblStyleLst xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" def=\"{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}\"/>",
            PresentationTypes + "tableStyles+xml");
        Write("docProps/app.xml",
            "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\">" +
            $"<Application>Filee</Application><Slides>{slides.Count}</Slides><Notes>{notes}</Notes></Properties>",
            "application/vnd.openxmlformats-officedocument.extended-properties+xml");
        Write("_rels/.rels", PresentationPackage.Rels(
            ("rId1", "officeDocument", "ppt/presentation.xml"),
            ("rId2", "extended-properties", "docProps/app.xml")));

        var types = new StringBuilder("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        foreach (var (extension, type) in _defaults)
            types.Append(CultureInfo.InvariantCulture, $"<Default Extension=\"{extension}\" ContentType=\"{type}\"/>");
        foreach (var (part, type) in _overrides)
            types.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"/{part}\" ContentType=\"{type}\"/>");
        Write("[Content_Types].xml", types.Append("</Types>").ToString());
    }

    /// <summary>The slide size in EMU, within what PowerPoint allows; 10 × 7.5 inches when the file's is impossible.</summary>
    private (long Width, long Height) SlideSize()
    {
        static long Side(int masterUnits, long fallback)
        {
            var emu = (long)Math.Round(masterUnits * EmuPerMasterUnit);
            return emu is >= PresentationPackage.MinSlideSide and <= PresentationPackage.MaxSlideSide ? emu : fallback;
        }
        return (Side(_file.SlideSize.Width, 9144000), Side(_file.SlideSize.Height, 6858000));
    }

    /// <summary>Writes an XML part; with a content type it gets an override in [Content_Types].xml.</summary>
    private void Write(string name, string xml, string? contentType = null)
    {
        using (var writer = new StreamWriter(_zip.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false)))
        {
            writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            writer.Write(xml);
        }
        if (contentType is not null)
            _overrides.Add((name, contentType));
    }

    // ───────────────────────── Pictures ─────────────────────────

    /// <summary>Media parts written, by the BLIP store index they come from (null: the picture can't be read).</summary>
    private readonly Dictionary<uint, string?> _media = [];

    /// <summary>The media part of picture <paramref name="pib"/>, written the first time it is used; null when it can't be read.</summary>
    private string? MediaPart(uint pib)
    {
        if (_media.TryGetValue(pib, out var cached))
            return cached;
        string? name = null;
        if (_file.Picture(pib) is { } picture)
        {
            name = $"ppt/media/image{_media.Count + 1}.{picture.Extension}";
            _defaults.TryAdd(picture.Extension, picture.Extension switch
            {
                "png" => "image/png",
                "jpg" => "image/jpeg",
                "emf" => "image/x-emf",
                "wmf" => "image/x-wmf",
                "bmp" => "image/bmp",
                "tif" => "image/tiff",
                _ => "application/octet-stream",
            });
            var compress = picture.Extension is "emf" or "wmf" or "bmp" or "tif";
            using var stream = _zip.CreateEntry(name, compress ? CompressionLevel.Fastest : CompressionLevel.NoCompression).Open();
            stream.Write(picture.Bytes);
        }
        else
        {
            Drop("pictures in a format Filee doesn't read (PICT)");
        }
        _media[pib] = name;
        return name;
    }

    // ───────────────────────── Parts being written ─────────────────────────

    /// <summary>A slide, layout, master or notes slide being written: its relationships and shape ids.</summary>
    private sealed class Sheet(string part, MasterStyles styles)
    {
        public string Part { get; } = part;

        public MasterStyles Styles { get; } = styles;

        public List<(string Id, string Type, string Target)> Rels { get; } = [];

        private readonly Dictionary<string, string> _targets = new(StringComparer.Ordinal);

        /// <summary>The outline text of a slide's placeholders (OutlineTextRefAtom indexes into it).</summary>
        public List<List<OfficeArtRecord>> Outline { get; init; } = [];

        /// <summary>The colour scheme the sheet is drawn in (RRGGBB), for colours Filee works out itself (pattern pictures).</summary>
        public uint[] Scheme { get; init; } = DefaultScheme;

        private int _nextId = 2;

        public int NextId() => _nextId++;

        /// <summary>The relationship id to <paramref name="target"/> (a part name relative to the package), added once.</summary>
        public string Relationship(string type, string target)
        {
            var relative = Relative(target);
            if (_targets.TryGetValue(type + "|" + relative, out var id))
                return id;
            id = $"rId{Rels.Count + 1}";
            Rels.Add((id, type, relative));
            _targets[type + "|" + relative] = id;
            return id;
        }

        private string Relative(string target)
        {
            // Parts sit in ppt/<folder>/: a target elsewhere in ppt/ is ../<folder>/<name>.
            var from = Part[..Part.LastIndexOf('/')];
            var to = target[..target.LastIndexOf('/')];
            return from == to ? target[(target.LastIndexOf('/') + 1)..] : "../" + target["ppt/".Length..];
        }

        public string RelsPart => $"{Part[..Part.LastIndexOf('/')]}/_rels/{Part[(Part.LastIndexOf('/') + 1)..]}.rels";
    }
}
