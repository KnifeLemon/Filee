// Writes an OpenDocument presentation (.odp, ODF 1.3) from the slide model (Slides/SlideModel.cs), so PowerPoint decks
// and the documents the PPTX writer lays out on slides become Impress files without LibreOffice. The counterpart of
// OdfDrawReader: a file written here reads back into the same model.
//
// Split over partial files:
//  * OdpWriter.cs         the package (OdfPackageWriter), styles.xml with the master pages, their page layouts and
//                         placeholders, content.xml with the pages and their speaker notes;
//  * OdpWriter.Shapes.cs  elements: shapes as draw:custom-shape with the outline as draw:enhanced-path, straight lines,
//                         pictures in frames, tables, groups; placement (svg:x / svg:y or draw:transform);
//  * OdpWriter.Styles.cs  automatic and named styles: fills (solid, gradient, hatch, bitmap), lines (dashes, markers),
//                         text frame settings, pictures (cropped, recoloured or mirrored ones drawn out once);
//  * OdpWriter.Text.cs    paragraphs and spans, bullets and numbers as text:list with a list style, fields, links.
//
// The model holds every slide as it is shown, the master's and the layout's shapes included. Shapes that start every
// slide of a group of slides alike (the same drawing, the same styles) become that group's master page, drawn behind
// its slides; the most common background of the group becomes the master's and a slide with another one keeps its own.
// Each master also gets Impress's title and outline placeholders, so slides added later in Impress have them.
//
// Hostile input: the model comes from bounded readers (sizes, counts, nesting); the writer adds a cap on the XML it
// makes, on group nesting and on the bytes of pictures, each refused through DocumentLimits, and stops when cancelled.

using System.Globalization;
using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Slides;
using SkiaSharp;
using OdfNames = Filee.Engines.Hwp.Hwpx.Odt.Odf;

namespace Filee.Engines.Odf.Draw;

/// <summary>Converts a <see cref="SlideDeck"/> into an .odp package.</summary>
internal sealed partial class OdpWriter : IDisposable
{
    private const string Version = "1.3";

    /// <summary>The namespaces of content.xml and styles.xml.</summary>
    private const string Namespaces =
        OdfNames.Namespaces + " " +
        "xmlns:presentation=\"urn:oasis:names:tc:opendocument:xmlns:presentation:1.0\" " +
        "xmlns:loext=\"urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0\"";

    /// <summary>Characters of XML written for one deck at most (content and styles together).</summary>
    internal const long MaxXmlCharacters = 512L * 1024 * 1024;

    /// <summary>The notes page: A4 portrait, as Impress makes it.</summary>
    private const float NotesWidth = 595.28f, NotesHeight = 841.89f;

    private readonly SlideDeck _deck;
    private readonly IReadOnlyList<IReadOnlyList<string>>? _notes;
    private readonly CancellationToken _token;
    private readonly DocumentLimits _limits;
    private readonly StyleSet _contentStyles = new("");
    private readonly StyleSet _masterStyles = new("M");
    private StyleSet _styles;
    private long _characters;

    private OdpWriter(SlideDeck deck, IReadOnlyList<IReadOnlyList<string>>? notes, DocumentLimits limits, CancellationToken token)
    {
        _deck = deck;
        _notes = notes;
        _limits = limits;
        _token = token;
        _styles = _contentStyles;
    }

    public void Dispose() => _images.Dispose();

    /// <summary>
    /// Writes <paramref name="deck"/> as an .odp file (replaced atomically); <paramref name="notes"/> holds the
    /// speaker notes of each slide as paragraphs of text.
    /// </summary>
    public static void Write(SlideDeck deck, string outputPath, IReadOnlyList<IReadOnlyList<string>>? notes = null,
        DocumentLimits? limits = null, CancellationToken cancellationToken = default)
    {
        using var writer = new OdpWriter(deck, notes, limits ?? DocumentLimits.Default, cancellationToken);
        var parts = writer.Parts();
        OdfPackageWriter.Write(outputPath, OdfNames.PresentationMimeType, Version, parts, cancellationToken);
    }

    // ───────────────────────── Masters ─────────────────────────

    /// <summary>A master page: the shapes every slide on it starts with, and its background.</summary>
    private sealed class Master(string name, List<SlideElement> elements, SlideFill? background, string backgroundKey)
    {
        public string Name { get; } = name;
        public List<SlideElement> Elements { get; } = elements;
        public SlideFill? Background { get; } = background;
        public string BackgroundKey { get; } = backgroundKey;
    }

    /// <summary>
    /// The masters and which one each slide takes: slides whose first shapes are the same drawing share the shapes
    /// they all start with. A slide sharing its first shape with no other slide takes the plain master.
    /// </summary>
    private (List<Master> Masters, Master[] OfSlide, int[] Skip) Masters()
    {
        var slides = _deck.Slides;
        // The XML of every element, written with throwaway automatic styles: equal XML is the same drawing.
        var keys = new List<List<string>>(slides.Count);
        var backgrounds = new string[slides.Count];
        var scratch = new StyleSet("X");
        _styles = scratch;
        foreach (var slide in slides)
        {
            _token.ThrowIfCancellationRequested();
            var list = new List<string>(slide.Elements.Count);
            foreach (var element in slide.Elements)
            {
                var sb = new StringBuilder();
                Element(sb, element, SKMatrix.Identity, 0, onMaster: false);
                list.Add(sb.ToString());
            }
            keys.Add(list);
            backgrounds[keys.Count - 1] = PageFill(slide.Background);
        }
        _styles = _contentStyles;
        _characters = 0;

        var masters = new List<Master>();
        var ofSlide = new Master[slides.Count];
        var skip = new int[slides.Count];
        Master? plain = null;
        foreach (var group in Enumerable.Range(0, slides.Count).GroupBy(i => keys[i].Count > 0 ? keys[i][0] : ""))
        {
            var members = group.ToList();
            var shared = 0;
            if (group.Key.Length > 0 && members.Count > 1)
            {
                shared = keys[members[0]].Count;
                foreach (var i in members.Skip(1))
                {
                    var n = 0;
                    while (n < shared && n < keys[i].Count && keys[i][n] == keys[members[0]][n])
                        n++;
                    shared = n;
                }
            }
            // The background most of the group's slides have (the first one's when it is a tie).
            var background = members.GroupBy(i => backgrounds[i]).OrderByDescending(g => g.Count()).ThenBy(g => g.Min()).First().First();
            Master master;
            if (shared == 0)
            {
                master = plain ??= new Master("Default", [], slides[background].Background, backgrounds[background]);
                if (!masters.Contains(master))
                    masters.Add(master);
            }
            else
            {
                master = new Master($"Master{masters.Count(m => m != plain) + 1}", slides[members[0]].Elements.Take(shared).ToList(),
                    slides[background].Background, backgrounds[background]);
                masters.Add(master);
            }
            foreach (var i in members)
            {
                ofSlide[i] = master;
                skip[i] = shared;
            }
        }
        if (masters.Count == 0)
            masters.Add(new Master("Default", [], null, PageFill(null)));
        return (masters, ofSlide, skip);
    }

    // ───────────────────────── Parts ─────────────────────────

    private List<OdfPart> Parts()
    {
        var (masters, ofSlide, skip) = Masters();

        // styles.xml: the masters, with their automatic styles.
        _styles = _masterStyles;
        var slideLayout = _masterStyles.Add("page-layout", "PM", "",
            $"<style:page-layout-properties fo:margin-top=\"0pt\" fo:margin-bottom=\"0pt\" fo:margin-left=\"0pt\" fo:margin-right=\"0pt\" fo:page-width=\"{Pt(_deck.Width)}\" fo:page-height=\"{Pt(_deck.Height)}\" style:print-orientation=\"{(_deck.Width >= _deck.Height ? "landscape" : "portrait")}\"/>");
        var notesLayout = _masterStyles.Add("page-layout", "PM", "",
            $"<style:page-layout-properties fo:margin-top=\"0pt\" fo:margin-bottom=\"0pt\" fo:margin-left=\"0pt\" fo:margin-right=\"0pt\" fo:page-width=\"{Pt(NotesWidth)}\" fo:page-height=\"{Pt(NotesHeight)}\" style:print-orientation=\"portrait\"/>");
        var masterPages = new StringBuilder();
        foreach (var master in masters)
        {
            _token.ThrowIfCancellationRequested();
            var pageStyle = _masterStyles.Add("drawing-page", "Mdp", "", $"<style:drawing-page-properties{PageFill(master.Background)}/>");
            masterPages.Append($"<style:master-page style:name=\"{OdfXml.Escape(master.Name)}\" style:page-layout-name=\"{slideLayout}\" draw:style-name=\"{pageStyle}\">");
            foreach (var element in master.Elements)
                Element(masterPages, element, SKMatrix.Identity, 0, onMaster: true);
            masterPages.Append(MasterPlaceholders(master.Name));
            masterPages.Append($"<presentation:notes style:page-layout-name=\"{notesLayout}\">")
                .Append(NotesThumbnail(null))
                .Append($"<draw:frame presentation:style-name=\"{OdfXml.Escape(master.Name)}-notes\" draw:layer=\"backgroundobjects\" {NotesBox} presentation:class=\"notes\" presentation:placeholder=\"true\"><draw:text-box/></draw:frame>")
                .Append("</presentation:notes></style:master-page>");
            Count(masterPages.Length);
        }

        // content.xml: the pages.
        _styles = _contentStyles;
        var pages = new StringBuilder();
        for (var i = 0; i < _deck.Slides.Count; i++)
        {
            _token.ThrowIfCancellationRequested();
            var slide = _deck.Slides[i];
            var master = ofSlide[i];
            var own = PageFill(slide.Background);
            var pageProperties = "presentation:display-footer=\"false\" presentation:display-page-number=\"false\" presentation:display-date-time=\"false\"" +
                                 (own == master.BackgroundKey ? "" : own);
            var pageStyle = _contentStyles.Add("drawing-page", "dp", "", $"<style:drawing-page-properties {pageProperties}/>");
            var start = pages.Length;
            pages.Append($"<draw:page draw:name=\"page{N(i + 1)}\" draw:style-name=\"{pageStyle}\" draw:master-page-name=\"{OdfXml.Escape(master.Name)}\">");
            for (var e = skip[i]; e < slide.Elements.Count; e++)
                Element(pages, slide.Elements[e], SKMatrix.Identity, 0, onMaster: false);
            pages.Append(Notes(i, master.Name)).Append("</draw:page>");
            Count(pages.Length - start);
        }

        var content = OdfPackageWriter.Xml(
            $"<office:document-content {Namespaces} office:version=\"{Version}\">" +
            $"<office:automatic-styles>{_contentStyles.Xml}</office:automatic-styles>" +
            $"<office:body><office:presentation>{pages}</office:presentation></office:body></office:document-content>");
        var styles = OdfPackageWriter.Xml(
            $"<office:document-styles {Namespaces} office:version=\"{Version}\">" +
            $"<office:styles>{DefaultStyles()}{_named.Xml}{PresentationStyles(masters)}</office:styles>" +
            $"<office:automatic-styles>{_masterStyles.Xml}</office:automatic-styles>" +
            "<office:master-styles><draw:layer-set><draw:layer draw:name=\"layout\"/><draw:layer draw:name=\"background\"/>" +
            "<draw:layer draw:name=\"backgroundobjects\"/><draw:layer draw:name=\"controls\"/><draw:layer draw:name=\"measurelines\"/></draw:layer-set>" +
            $"{masterPages}</office:master-styles></office:document-styles>");
        var meta = OdfPackageWriter.Xml(
            $"<office:document-meta {Namespaces} office:version=\"{Version}\"><office:meta><meta:generator>Filee</meta:generator>" +
            (string.IsNullOrWhiteSpace(_deck.Title) ? "" : $"<dc:title>{OdfXml.Escape(_deck.Title).Trim()}</dc:title>") +
            "</office:meta></office:document-meta>");
        Count((long)content.Length + styles.Length);

        var parts = new List<OdfPart>
        {
            new("content.xml", "text/xml", Text: content),
            new("styles.xml", "text/xml", Text: styles),
            new("meta.xml", "text/xml", Text: meta),
        };
        parts.AddRange(_media.Parts);
        return parts;
    }

    /// <summary>Counts XML against <see cref="MaxXmlCharacters"/>.</summary>
    private void Count(long characters)
    {
        _characters += characters;
        if (_characters > MaxXmlCharacters)
            throw DocumentLimits.Exceeded($"The presentation would need more than {DocumentLimits.Count(MaxXmlCharacters)} characters of XML.");
    }

    /// <summary>The default graphic and paragraph styles: Impress's text distances, no shadow, text 18 pt.</summary>
    private static string DefaultStyles() =>
        "<style:default-style style:family=\"graphic\"><style:graphic-properties svg:stroke-color=\"#000000\" draw:fill-color=\"#ffffff\" " +
        "draw:shadow=\"hidden\" fo:padding-top=\"3.6pt\" fo:padding-bottom=\"3.6pt\" fo:padding-left=\"7.2pt\" fo:padding-right=\"7.2pt\"/>" +
        "<style:paragraph-properties style:writing-mode=\"lr-tb\" style:tab-stop-distance=\"72pt\"/>" +
        "<style:text-properties fo:font-size=\"18pt\" style:font-size-asian=\"18pt\" style:font-size-complex=\"18pt\" fo:language=\"none\" fo:country=\"none\"/></style:default-style>" +
        "<style:style style:name=\"standard\" style:family=\"graphic\"><style:graphic-properties draw:stroke=\"solid\" draw:fill=\"solid\"/></style:style>";

    // ───────────────────────── Placeholders and notes ─────────────────────────

    /// <summary>The title and outline placeholders of a master, where Impress puts them on a slide of this size.</summary>
    private string MasterPlaceholders(string master)
    {
        var name = OdfXml.Escape(master);
        var (w, h) = (_deck.Width, _deck.Height);
        string Box(float x, float y, float width, float height) =>
            $"svg:width=\"{Pt(width * w)}\" svg:height=\"{Pt(height * h)}\" svg:x=\"{Pt(x * w)}\" svg:y=\"{Pt(y * h)}\"";
        return $"<draw:frame presentation:style-name=\"{name}-title\" draw:layer=\"backgroundobjects\" {Box(0.05f, 0.04f, 0.9f, 0.167f)} presentation:class=\"title\" presentation:placeholder=\"true\"><draw:text-box/></draw:frame>" +
               $"<draw:frame presentation:style-name=\"{name}-outline1\" draw:layer=\"backgroundobjects\" {Box(0.05f, 0.234f, 0.9f, 0.58f)} presentation:class=\"outline\" presentation:placeholder=\"true\"><draw:text-box/></draw:frame>";
    }

    /// <summary>Each master's presentation styles: title, outline levels and notes, sized as Impress's defaults.</summary>
    private static string PresentationStyles(List<Master> masters)
    {
        var sb = new StringBuilder();
        const string Frame = "<style:graphic-properties draw:stroke=\"none\" draw:fill=\"none\" draw:auto-grow-height=\"false\"/>";
        int[] sizes = [32, 28, 24, 20, 20, 20, 20, 20, 20];
        foreach (var master in masters)
        {
            var name = OdfXml.Escape(master.Name);
            sb.Append($"<style:style style:name=\"{name}-title\" style:family=\"presentation\">")
              .Append("<style:graphic-properties draw:stroke=\"none\" draw:fill=\"none\" draw:textarea-vertical-align=\"middle\" draw:auto-grow-height=\"false\"/>")
              .Append("<style:paragraph-properties fo:text-align=\"center\"/><style:text-properties fo:font-size=\"44pt\" style:font-size-asian=\"44pt\" style:font-size-complex=\"44pt\"/></style:style>");
            for (var level = 1; level <= sizes.Length; level++)
            {
                var parent = level == 1 ? "" : $" style:parent-style-name=\"{name}-outline{level - 1}\"";
                var size = Pt(sizes[level - 1]);
                sb.Append($"<style:style style:name=\"{name}-outline{level}\" style:family=\"presentation\"{parent}>");
                if (level == 1)
                {
                    sb.Append("<style:graphic-properties draw:stroke=\"none\" draw:fill=\"none\" draw:auto-grow-height=\"false\"><text:list-style>");
                    for (var l = 1; l <= sizes.Length; l++)
                    {
                        sb.Append($"<text:list-level-style-bullet text:level=\"{N(l)}\" text:bullet-char=\"{(l % 2 == 1 ? "•" : "–")}\">")
                          .Append($"<style:list-level-properties text:space-before=\"{Pt(27 * (l - 1))}\" text:min-label-width=\"{Pt(20)}\"/>")
                          .Append("<style:text-properties fo:font-family=\"Arial\" style:use-window-font-color=\"true\" fo:font-size=\"100%\"/></text:list-level-style-bullet>");
                    }
                    sb.Append("</text:list-style></style:graphic-properties>");
                }
                sb.Append($"<style:paragraph-properties fo:margin-top=\"{Pt(level == 1 ? 10 : 5)}\"/>")
                  .Append($"<style:text-properties fo:font-size=\"{size}\" style:font-size-asian=\"{size}\" style:font-size-complex=\"{size}\"/></style:style>");
            }
            sb.Append($"<style:style style:name=\"{name}-notes\" style:family=\"presentation\">{Frame}")
              .Append($"<style:text-properties fo:font-size=\"{Pt(12)}\" style:font-size-asian=\"{Pt(12)}\" style:font-size-complex=\"{Pt(12)}\"/></style:style>");
        }
        return sb.ToString();
    }

    /// <summary>Where the notes text goes on the notes page (Impress's place, under the slide's picture).</summary>
    private static readonly string NotesBox =
        $"svg:width=\"{Pt(481.9f)}\" svg:height=\"{Pt(354.3f)}\" svg:x=\"{Pt(56.7f)}\" svg:y=\"{Pt(405.3f)}\"";

    /// <summary>The picture of the slide on its notes page.</summary>
    private string NotesThumbnail(int? number)
    {
        // As wide as the page's text area, the slide's own proportions, at the top.
        var width = 481.9f;
        var height = Math.Min(width * _deck.Height / Math.Max(_deck.Width, 1), 340f);
        width = height * _deck.Width / Math.Max(_deck.Height, 1);
        var x = (NotesWidth - width) / 2;
        var page = number is { } n ? $" draw:page-number=\"{N(n)}\"" : "";
        return $"<draw:page-thumbnail draw:layer=\"backgroundobjects\" svg:width=\"{Pt(width)}\" svg:height=\"{Pt(height)}\" svg:x=\"{Pt(x)}\" svg:y=\"{Pt(42.5f)}\"{page} presentation:class=\"page\"/>";
    }

    /// <summary>A page's speaker notes: the notes page with the slide's picture and the text.</summary>
    private string Notes(int index, string master)
    {
        var lines = _notes is not null && index < _notes.Count ? _notes[index] : [];
        var sb = new StringBuilder("<presentation:notes>").Append(NotesThumbnail(index + 1));
        sb.Append($"<draw:frame presentation:style-name=\"{OdfXml.Escape(master)}-notes\" {NotesBox} presentation:class=\"notes\"");
        if (lines.Count == 0)
            return sb.Append(" presentation:placeholder=\"true\"><draw:text-box/></draw:frame></presentation:notes>").ToString();
        sb.Append("><draw:text-box>");
        var paragraph = _styles.Add("paragraph", "P", "", $"<style:text-properties fo:font-size=\"{Pt(12)}\" style:font-size-asian=\"{Pt(12)}\" style:font-size-complex=\"{Pt(12)}\"/>");
        foreach (var line in lines)
        {
            sb.Append($"<text:p text:style-name=\"{paragraph}\">");
            PlainText(sb, line);
            sb.Append("</text:p>");
        }
        return sb.Append("</draw:text-box></draw:frame></presentation:notes>").ToString();
    }

    // ───────────────────────── Numbers ─────────────────────────

    /// <summary>A length in points as ODF writes it ("12.5pt"), cut at ±10 km.</summary>
    private static string Pt(double points) =>
        (double.IsFinite(points) ? Math.Clamp(Math.Round(points, 2), -28_000_000, 28_000_000) : 0).ToString("0.##", CultureInfo.InvariantCulture) + "pt";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Percent(double fraction) =>
        (double.IsFinite(fraction) ? Math.Clamp(Math.Round(fraction * 100, 1), -100_000, 100_000) : 0).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
