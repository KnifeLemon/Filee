// Writes a PowerPoint presentation (PPTX, PresentationML / ECMA-376) whose slides each show one picture, fitted and
// centred, with optional speaker notes: the package PDF → PPTX needs. Written by hand like the DOCX writer, on the
// package code it shares with the document slide writer (Hwp/Hwpx/Pptx/PresentationPackage), as small as PowerPoint,
// LibreOffice Impress and Keynote accept: presentation, one slide master with one blank layout, a theme,
// presentation / view / table style properties, the slides with their pictures, a notes master and notes slides when
// there is text, and the document properties.
//
// Slides and pictures are written into the ZIP as they come, so a long PDF never has all its pages in memory; the
// parts that list them (presentation, content types) follow at the end.

using System.Text;
using Filee.Engines.Hwp.Hwpx.Pptx;
using static Filee.Engines.Hwp.Hwpx.Pptx.PresentationPackage;

namespace Filee.Engines.Pdf;

/// <summary>A PPTX of picture slides, written slide by slide.</summary>
internal sealed class PictureDeckWriter : IDisposable
{
    /// <summary>EMU (English Metric Units) per point: 914400 per inch.</summary>
    public const double EmuPerPoint = PresentationPackage.EmuPerPoint;

    /// <summary>Smallest slide side PowerPoint allows (1 inch, ST_SlideSizeCoordinate).</summary>
    public const long MinSlideSide = PresentationPackage.MinSlideSide;

    /// <summary>Largest slide side PowerPoint allows (56 inches, ST_SlideSizeCoordinate).</summary>
    public const long MaxSlideSide = PresentationPackage.MaxSlideSide;

    private readonly PresentationPackage _package;
    private readonly List<bool> _slideHasNotes = [];

    /// <param name="path">The PPTX to write; it appears only once <see cref="Save"/> has finished.</param>
    /// <param name="slideWidth">Slide width in EMU, within <see cref="MinSlideSide"/>..<see cref="MaxSlideSide"/>.</param>
    /// <param name="slideHeight">Slide height in EMU.</param>
    public PictureDeckWriter(string path, long slideWidth, long slideHeight)
    {
        SlideWidth = Math.Clamp(slideWidth, MinSlideSide, MaxSlideSide);
        SlideHeight = Math.Clamp(slideHeight, MinSlideSide, MaxSlideSide);
        _package = new PresentationPackage(path);
    }

    public long SlideWidth { get; }
    public long SlideHeight { get; }

    /// <summary>
    /// Slide size for a page of <paramref name="width"/> × <paramref name="height"/> points: the page's own size, so an
    /// A4 page gives an A4 portrait slide and a 16:9 page a 16:9 slide. Pages larger than PowerPoint's 56 inches are
    /// scaled down and pages smaller than 1 inch scaled up, keeping the shape as far as those limits allow.
    /// </summary>
    public static (long Width, long Height) SlideSize(double width, double height)
    {
        double cx = Math.Max(1, width) * EmuPerPoint;
        double cy = Math.Max(1, height) * EmuPerPoint;
        var shrink = Math.Min(1, MaxSlideSide / Math.Max(cx, cy));
        cx *= shrink;
        cy *= shrink;
        var grow = Math.Max(1, MinSlideSide / Math.Min(cx, cy));
        cx *= grow;
        cy *= grow;
        return (Math.Clamp((long)Math.Round(cx), MinSlideSide, MaxSlideSide), Math.Clamp((long)Math.Round(cy), MinSlideSide, MaxSlideSide));
    }

    /// <summary>
    /// Where a picture of <paramref name="width"/> × <paramref name="height"/> (any unit) goes on the slide: as large as
    /// fits without cropping, centred (EMU).
    /// </summary>
    public (long X, long Y, long Width, long Height) Fit(double width, double height)
    {
        var scale = Math.Min(SlideWidth / Math.Max(width, 1e-6), SlideHeight / Math.Max(height, 1e-6));
        var cx = Math.Clamp((long)Math.Round(width * scale), 1, SlideWidth);
        var cy = Math.Clamp((long)Math.Round(height * scale), 1, SlideHeight);
        return ((SlideWidth - cx) / 2, (SlideHeight - cy) / 2, cx, cy);
    }

    /// <summary>
    /// Adds a slide showing <paramref name="jpeg"/>, fitted and centred like <see cref="Fit"/> for a picture of
    /// <paramref name="width"/> × <paramref name="height"/>, with <paramref name="notes"/> (lines separated by CR LF or
    /// LF) as its speaker notes when there are any.
    /// </summary>
    public void AddSlide(ReadOnlySpan<byte> jpeg, double width, double height, string name, string? notes)
    {
        var number = _slideHasNotes.Count + 1;
        var hasNotes = !string.IsNullOrWhiteSpace(notes);
        _slideHasNotes.Add(hasNotes);

        using (var media = _package.Create($"ppt/media/image{number}.jpeg", "jpeg", "image/jpeg", compress: false))
            media.Write(jpeg);

        var (x, y, cx, cy) = Fit(width, height);
        var slide = new StringBuilder($"<p:sld {Namespaces}><p:cSld>").Append(TreeStart)
            .Append("<p:pic><p:nvPicPr>")
            .Append($"<p:cNvPr id=\"2\" name=\"{Escape(name)}\" descr=\"{Escape(name)}\"/>")
            .Append("<p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>")
            .Append("<p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>")
            .Append($"<p:spPr><a:xfrm><a:off x=\"{N(x)}\" y=\"{N(y)}\"/><a:ext cx=\"{N(cx)}\" cy=\"{N(cy)}\"/></a:xfrm>")
            .Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>")
            .Append("</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
        _package.Write($"ppt/slides/slide{number}.xml", slide.ToString(), PresentationTypes + "slide+xml");
        List<(string, string, string)> relationships =
        [
            ("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"),
            ("rId2", "image", $"../media/image{number}.jpeg"),
        ];
        if (hasNotes)
            relationships.Add(("rId3", "notesSlide", $"../notesSlides/notesSlide{number}.xml"));
        _package.Write($"ppt/slides/_rels/slide{number}.xml.rels", Rels([.. relationships]));

        if (hasNotes)
            _package.WriteNotesSlide(number, notes!.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
    }

    /// <summary>Writes the parts that list the slides and moves the finished file into place.</summary>
    public void Save()
    {
        if (_slideHasNotes.Count == 0)
            throw new InvalidOperationException("A presentation needs at least one slide.");
        var notes = _slideHasNotes.Contains(true);
        var slides = _slideHasNotes.Count;

        // Presentation: master, notes master, slides, sizes. Relationship ids: 1-5 fixed parts, 6 the notes master,
        // slides from 7.
        var presentation = new StringBuilder($"<p:presentation {Namespaces} saveSubsetFonts=\"1\">")
            .Append("<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>");
        if (notes)
            presentation.Append("<p:notesMasterIdLst><p:notesMasterId r:id=\"rId6\"/></p:notesMasterIdLst>");
        presentation.Append("<p:sldIdLst>");
        for (var i = 0; i < slides; i++)
            presentation.Append($"<p:sldId id=\"{256 + i}\" r:id=\"rId{7 + i}\"/>");
        presentation.Append("</p:sldIdLst>")
            .Append($"<p:sldSz cx=\"{N(SlideWidth)}\" cy=\"{N(SlideHeight)}\"/>")
            .Append($"<p:notesSz cx=\"{N(NotesWidth)}\" cy=\"{N(NotesHeight)}\"/>")
            .Append("</p:presentation>");
        _package.Write("ppt/presentation.xml", presentation.ToString(), PresentationTypes + "presentation.main+xml");

        var relationships = new List<(string, string, string)>
        {
            ("rId1", "slideMaster", "slideMasters/slideMaster1.xml"),
            ("rId2", "theme", "theme/theme1.xml"),
            ("rId3", "presProps", "presProps.xml"),
            ("rId4", "viewProps", "viewProps.xml"),
            ("rId5", "tableStyles", "tableStyles.xml"),
        };
        if (notes)
            relationships.Add(("rId6", "notesMaster", "notesMasters/notesMaster1.xml"));
        for (var i = 0; i < slides; i++)
            relationships.Add(($"rId{7 + i}", "slide", $"slides/slide{i + 1}.xml"));
        _package.Write("ppt/_rels/presentation.xml.rels", Rels([.. relationships]));

        _package.Write("ppt/slideMasters/slideMaster1.xml", SlideMaster(), PresentationTypes + "slideMaster+xml");
        _package.Write("ppt/slideMasters/_rels/slideMaster1.xml.rels", Rels(
            ("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"),
            ("rId2", "theme", "../theme/theme1.xml")));
        _package.Write("ppt/slideLayouts/slideLayout1.xml",
            $"<p:sldLayout {Namespaces} type=\"blank\" preserve=\"1\"><p:cSld name=\"Blank\">{TreeStart}</p:spTree></p:cSld>" +
            "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>", PresentationTypes + "slideLayout+xml");
        _package.Write("ppt/slideLayouts/_rels/slideLayout1.xml.rels", Rels(("rId1", "slideMaster", "../slideMasters/slideMaster1.xml")));
        _package.Write("ppt/theme/theme1.xml", Theme(), ThemeType);
        if (notes)
            _package.WriteNotesMaster(SlideWidth, SlideHeight);
        _package.WriteCommonParts(null, [], slides, _slideHasNotes.Count(n => n));
        _package.Save();
    }

    public void Dispose() => _package.Dispose();

    /// <summary>A white background, no placeholders and default text styles: the slides only hold their picture.</summary>
    private static string SlideMaster()
    {
        static string Level(string size, string font, string align = "") =>
            $"<a:lvl1pPr{align}><a:defRPr sz=\"{size}\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill>" +
            $"<a:latin typeface=\"+{font}-lt\"/><a:ea typeface=\"+{font}-ea\"/><a:cs typeface=\"+{font}-cs\"/></a:defRPr></a:lvl1pPr>";

        return $"<p:sldMaster {Namespaces}><p:cSld>" +
               "<p:bg><p:bgPr><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg>" +
               $"{TreeStart}</p:spTree></p:cSld>{ColorMap}" +
               "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst>" +
               $"<p:txStyles><p:titleStyle>{Level("4400", "mj", " algn=\"ctr\"")}</p:titleStyle>" +
               $"<p:bodyStyle>{Level("2800", "mn")}</p:bodyStyle><p:otherStyle>{Level("1800", "mn")}</p:otherStyle></p:txStyles>" +
               "</p:sldMaster>";
    }
}
