// The package of a PowerPoint presentation (PPTX, PresentationML / ECMA-376) as Filee's two deck writers write it by
// hand: the picture decks of PDF → PPTX (Pdf/PictureDeckWriter) and the slides of documents (PptxWriter). Parts go
// into the ZIP as they are written, each with its content type, and [Content_Types].xml lists them when the package
// is saved; the file appears under its name only then. The parts both writers share are here too: the theme, the
// notes master and notes slides, the presentation / view / table style properties and the document properties.
// The Open XML SDK is only used by the tests, which validate the output with it.

using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Filee.Engines.Hwp.Hwpx.Pptx;

/// <summary>A PPTX package being written part by part.</summary>
internal sealed class PresentationPackage : IDisposable
{
    /// <summary>EMU (English Metric Units) per point: 914400 per inch.</summary>
    public const double EmuPerPoint = 12700;

    /// <summary>Smallest slide side PowerPoint allows (1 inch, ST_SlideSizeCoordinate).</summary>
    public const long MinSlideSide = 914400;

    /// <summary>Largest slide side PowerPoint allows (56 inches, ST_SlideSizeCoordinate).</summary>
    public const long MaxSlideSide = 51206400;

    public const string RelationshipBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    public const string PresentationTypes = "application/vnd.openxmlformats-officedocument.presentationml.";
    public const string ThemeType = "application/vnd.openxmlformats-officedocument.theme+xml";

    /// <summary>Namespaces of every PresentationML part.</summary>
    public const string Namespaces =
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";

    /// <summary>The group every shape tree starts with.</summary>
    public const string TreeStart =
        "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
        "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    public const string ColorMap =
        "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" " +
        "accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>";

    /// <summary>Notes pages are portrait Letter-like, as in PowerPoint (7.5 × 10 inches).</summary>
    public const long NotesWidth = 6858000;
    public const long NotesHeight = 9144000;

    private const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    private readonly string _path;
    private readonly string _temp;
    private readonly FileStream _file;
    private readonly ZipArchive _zip;
    private readonly List<(string Part, string Type)> _overrides = [];
    private readonly Dictionary<string, string> _defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rels"] = "application/vnd.openxmlformats-package.relationships+xml",
        ["xml"] = "application/xml",
    };
    private bool _saved;

    /// <param name="path">The PPTX to write; it appears only once <see cref="Save"/> has finished.</param>
    public PresentationPackage(string path)
    {
        _path = path;
        _temp = path + ".tmp";
        _file = File.Create(_temp);
        _zip = new ZipArchive(_file, ZipArchiveMode.Create);
    }

    /// <summary>Writes an XML part; with a content type it gets an override in [Content_Types].xml.</summary>
    public void Write(string name, string xml, string? contentType = null)
    {
        using (var writer = new StreamWriter(_zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false)))
        {
            writer.Write(Declaration);
            writer.Write(xml);
        }
        if (contentType is not null)
            _overrides.Add((name, contentType));
    }

    /// <summary>Opens a binary part (a picture) whose type is the default of its extension.</summary>
    public Stream Create(string name, string extension, string contentType, bool compress)
    {
        _defaults.TryAdd(extension, contentType);
        return _zip.CreateEntry(name, compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression).Open();
    }

    /// <summary>Writes the content types and moves the finished file into place.</summary>
    public void Save()
    {
        var sb = new StringBuilder("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        foreach (var (extension, type) in _defaults)
            sb.Append($"<Default Extension=\"{extension}\" ContentType=\"{type}\"/>");
        foreach (var (part, type) in _overrides)
            sb.Append($"<Override PartName=\"/{part}\" ContentType=\"{type}\"/>");
        Write("[Content_Types].xml", sb.Append("</Types>").ToString());

        _zip.Dispose();
        _file.Dispose();
        File.Move(_temp, _path, overwrite: true);
        _saved = true;
    }

    public void Dispose()
    {
        if (_saved)
            return;
        _zip.Dispose();
        _file.Dispose();
        try { File.Delete(_temp); } catch (IOException) { }
    }

    // ───────────────────────── Shared parts ─────────────────────────

    /// <summary>
    /// The package relationships, document properties and the presentation's property parts (presProps, viewProps,
    /// tableStyles), which every deck has alike.
    /// </summary>
    public void WriteCommonParts(string? title, IReadOnlyList<string> authors, int slides, int notes)
    {
        Write("ppt/presProps.xml", $"<p:presentationPr {Namespaces}/>", PresentationTypes + "presProps+xml");
        Write("ppt/viewProps.xml",
            $"<p:viewPr {Namespaces}><p:normalViewPr><p:restoredLeft sz=\"15620\"/><p:restoredTop sz=\"94660\"/></p:normalViewPr>" +
            "<p:gridSpacing cx=\"76200\" cy=\"76200\"/></p:viewPr>", PresentationTypes + "viewProps+xml");
        Write("ppt/tableStyles.xml",
            "<a:tblStyleLst xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" def=\"{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}\"/>",
            PresentationTypes + "tableStyles+xml");

        var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        Write("docProps/core.xml",
            "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
            "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" " +
            "xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            (string.IsNullOrWhiteSpace(title) ? "" : $"<dc:title>{Escape(title)}</dc:title>") +
            (authors.Count == 0 ? "" : $"<dc:creator>{Escape(string.Join("; ", authors))}</dc:creator>") +
            $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:created>" +
            $"<dcterms:modified xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:modified></cp:coreProperties>",
            "application/vnd.openxmlformats-package.core-properties+xml");
        Write("docProps/app.xml",
            "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\">" +
            $"<Application>Filee</Application><Slides>{N(slides)}</Slides><Notes>{N(notes)}</Notes></Properties>",
            "application/vnd.openxmlformats-officedocument.extended-properties+xml");

        Write("_rels/.rels", Rels(
            ("rId1", "officeDocument", "ppt/presentation.xml"),
            ("rId2", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml"),
            ("rId3", "extended-properties", "docProps/app.xml")));
    }

    /// <summary>
    /// The notes master and its theme (a master of its own needs a theme of its own; PowerPoint's notes masters use
    /// theme2 as well): the slide in the upper part of the page, shaped like a slide of <paramref name="slideWidth"/> ×
    /// <paramref name="slideHeight"/>, the notes below.
    /// </summary>
    public void WriteNotesMaster(long slideWidth, long slideHeight)
    {
        // The slide image fills a 6 × 4 inch box at the top, centred; the notes take the lower half of the page.
        const long boxLeft = 685800, boxTop = 457200, boxWidth = 5486400, boxHeight = 3657600;
        var scale = Math.Min((double)boxWidth / slideWidth, (double)boxHeight / slideHeight);
        var cx = (long)Math.Round(slideWidth * scale);
        var cy = (long)Math.Round(slideHeight * scale);
        var x = boxLeft + (boxWidth - cx) / 2;
        var y = boxTop + (boxHeight - cy) / 2;
        Write("ppt/notesMasters/notesMaster1.xml",
            $"<p:notesMaster {Namespaces}><p:cSld>{TreeStart}" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Slide Image Placeholder 1\"/><p:cNvSpPr><a:spLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\"/></p:cNvSpPr>" +
            "<p:nvPr><p:ph type=\"sldImg\" idx=\"2\"/></p:nvPr></p:nvSpPr>" +
            $"<p:spPr><a:xfrm><a:off x=\"{N(x)}\" y=\"{N(y)}\"/><a:ext cx=\"{N(cx)}\" cy=\"{N(cy)}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" +
            "<a:noFill/><a:ln w=\"12700\"><a:solidFill><a:prstClr val=\"black\"/></a:solidFill></a:ln></p:spPr></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Notes Placeholder 2\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr>" +
            "<p:nvPr><p:ph type=\"body\" sz=\"quarter\" idx=\"1\"/></p:nvPr></p:nvSpPr>" +
            "<p:spPr><a:xfrm><a:off x=\"685800\" y=\"4343400\"/><a:ext cx=\"5486400\" cy=\"4114800\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>" +
            "<p:txBody><a:bodyPr vert=\"horz\" lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" rtlCol=\"0\"/><a:lstStyle/><a:p/></p:txBody></p:sp>" +
            $"</p:spTree></p:cSld>{ColorMap}" +
            "<p:notesStyle><a:lvl1pPr marL=\"0\" algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\">" +
            "<a:defRPr sz=\"1200\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill>" +
            "<a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl1pPr></p:notesStyle>" +
            "</p:notesMaster>", PresentationTypes + "notesMaster+xml");
        Write("ppt/notesMasters/_rels/notesMaster1.xml.rels", Rels(("rId1", "theme", "../theme/theme2.xml")));
        Write("ppt/theme/theme2.xml", Theme(), ThemeType);
    }

    /// <summary>The notes slide of slide <paramref name="number"/>: one paragraph per line of <paramref name="lines"/>.</summary>
    public void WriteNotesSlide(int number, IEnumerable<string> lines)
    {
        var body = new StringBuilder();
        foreach (var line in lines)
            body.Append(line.Length == 0 ? "<a:p/>" : $"<a:p><a:r><a:rPr lang=\"en-US\" dirty=\"0\"/><a:t>{Escape(line)}</a:t></a:r></a:p>");
        if (body.Length == 0)
            body.Append("<a:p/>");
        Write($"ppt/notesSlides/notesSlide{number}.xml",
            $"<p:notes {Namespaces}><p:cSld>{TreeStart}" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Slide Image Placeholder 1\"/><p:cNvSpPr><a:spLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\"/></p:cNvSpPr>" +
            "<p:nvPr><p:ph type=\"sldImg\"/></p:nvPr></p:nvSpPr><p:spPr/></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Notes Placeholder 2\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr>" +
            "<p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
            $"<p:txBody><a:bodyPr/><a:lstStyle/>{body}</p:txBody></p:sp>" +
            "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>", PresentationTypes + "notesSlide+xml");
        Write($"ppt/notesSlides/_rels/notesSlide{number}.xml.rels", Rels(
            ("rId1", "notesMaster", "../notesMasters/notesMaster1.xml"),
            ("rId2", "slide", $"../slides/slide{number}.xml")));
    }

    /// <summary>Office's default colours and fonts; three plain entries in each style list, the least the schema takes.</summary>
    public static string Theme()
    {
        static string Color(string name, string value) => $"<a:{name}><a:srgbClr val=\"{value}\"/></a:{name}>";
        const string fill = "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>";
        var lines = string.Concat(new[] { "6350", "12700", "19050" }.Select(w => $"<a:ln w=\"{w}\">{fill}</a:ln>"));
        return "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Office Theme\"><a:themeElements>" +
               "<a:clrScheme name=\"Office\">" +
               "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
               Color("dk2", "44546A") + Color("lt2", "E7E6E6") + Color("accent1", "4472C4") + Color("accent2", "ED7D31") +
               Color("accent3", "A5A5A5") + Color("accent4", "FFC000") + Color("accent5", "5B9BD5") + Color("accent6", "70AD47") +
               Color("hlink", "0563C1") + Color("folHlink", "954F72") + "</a:clrScheme>" +
               "<a:fontScheme name=\"Office\">" +
               "<a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
               "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
               "<a:fmtScheme name=\"Office\">" +
               $"<a:fillStyleLst>{fill}{fill}{fill}</a:fillStyleLst>" +
               $"<a:lnStyleLst>{lines}</a:lnStyleLst>" +
               "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
               $"<a:bgFillStyleLst>{fill}{fill}{fill}</a:bgFillStyleLst></a:fmtScheme>" +
               "</a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";
    }

    /// <summary>A relationships part; a relationship type without a scheme is one of the officeDocument types.</summary>
    public static string Rels(params (string Id, string Type, string Target)[] relationships) =>
        Rels(relationships.Select(r => (r.Id, r.Type, r.Target, false)));

    /// <summary>A relationships part whose relationships may point outside the package (hyperlinks).</summary>
    public static string Rels(IEnumerable<(string Id, string Type, string Target, bool External)> relationships)
    {
        var sb = new StringBuilder("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        foreach (var (id, type, target, external) in relationships)
        {
            var fullType = type.Contains("://", StringComparison.Ordinal) ? type : RelationshipBase + type;
            sb.Append($"<Relationship Id=\"{id}\" Type=\"{fullType}\" Target=\"{Escape(target)}\"{(external ? " TargetMode=\"External\"" : "")}/>");
        }
        return sb.Append("</Relationships>").ToString();
    }

    public static string Escape(string text) => HwpxWriter.Escape(text);

    public static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}
