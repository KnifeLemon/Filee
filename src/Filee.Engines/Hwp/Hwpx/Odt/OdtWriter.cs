// Writes ODT (OpenDocument text 1.3) from the HDocument model — the counterpart of OdtReader, written by hand like the
// DOCX and HWPX writers, so anything the readers understand becomes a LibreOffice Writer document without LibreOffice
// or Pandoc.
//
// Split over partial files:
//  * OdtWriter.cs         package (mimetype first and stored, the manifest, meta.xml), styles.xml with the named
//                         styles, list styles, page layouts and one master page per section (its headers and footers);
//  * OdtWriter.Body.cs    paragraphs and their automatic styles, lists, text, notes, fields and tables;
//  * OdtWriter.Objects.cs frames with pictures or text, and shapes.
//
// Formatting is written as automatic styles (one per distinct set of properties), lengths in points with two decimals
// (exactly one HWPUNIT in 1/100), so a file read back gives the same model.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Hwp.Hwpx.Odt;

/// <summary>Converts an <see cref="HDocument"/> into an .odt package.</summary>
internal sealed partial class OdtWriter
{
    private const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";

    /// <summary>A4 with one-inch margins and headers / footers 1.5 cm from the edge, as the DOCX writer has it.</summary>
    private static readonly HPage DefaultPage = new(59528, 84188, 7200, 7200, 4255, 4255, 2945, 2945, 0);

    /// <summary>Space between a header (or footer) and the body when the model's header area leaves room for it.</summary>
    private const int HeaderSpacing = 1417;

    /// <summary>Heading sizes (pt) of "Heading 1".."Heading 9", as the DOCX writer's.</summary>
    private static readonly int[] HeadingSizes = [16, 14, 13, 12, 11, 10, 10, 10, 10];

    /// <summary>Picture types kept as they are; others are converted to PNG.</summary>
    private static readonly Dictionary<string, string> PictureTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["bmp"] = "image/bmp",
        ["svg"] = "image/svg+xml",
        ["wmf"] = "image/x-wmf",
        ["emf"] = "image/x-emf",
    };

    private readonly AutomaticStyles _contentStyles = new("");
    private readonly AutomaticStyles _stylesStyles = new("M");
    private readonly StringBuilder _masterPages = new();
    private readonly StringBuilder _listStyles = new();
    private readonly Dictionary<string, (string Path, string Extension)> _media = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<HNumbering, string> _lists = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _bookmarks = new(StringComparer.Ordinal);
    private AutomaticStyles _styles;
    private int _pageLayouts;

    private OdtWriter() => _styles = _contentStyles;

    /// <summary>Writes <paramref name="document"/> as an .odt file (replaced atomically).</summary>
    public static void Write(HDocument document, string outputPath)
    {
        var writer = new OdtWriter();
        var body = writer.Body(document);
        writer.WritePackage(document, body, outputPath);
    }

    // ───────────────────────── Automatic styles ─────────────────────────

    /// <summary>The automatic styles of one part: one per distinct family, attributes and properties.</summary>
    private sealed class AutomaticStyles(string prefix)
    {
        private readonly StringBuilder _xml = new();
        private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public string Xml => _xml.ToString();

        /// <summary>The name of a style with these properties, added on first use.</summary>
        /// <param name="kind">Name prefix: P (paragraph), T (text), Table, Col, Row, Cell, fr, gr, Sect.</param>
        public string Add(string family, string kind, string attributes, string properties)
        {
            var key = family + "\n" + attributes + "\n" + properties;
            if (_names.TryGetValue(key, out var name))
                return name;
            var number = _counts.GetValueOrDefault(kind) + 1;
            _counts[kind] = number;
            name = prefix + kind + number.ToString(CultureInfo.InvariantCulture);
            _xml.Append($"<style:style style:name=\"{name}\" style:family=\"{family}\"{attributes}>{properties}</style:style>");
            return _names[key] = name;
        }

        public void Append(string xml) => _xml.Append(xml);
    }

    /// <summary>Runs <paramref name="write"/> with automatic styles going into styles.xml (headers and footers).</summary>
    private T InStyles<T>(Func<T> write)
    {
        var previous = _styles;
        _styles = _stylesStyles;
        try
        {
            return write();
        }
        finally
        {
            _styles = previous;
        }
    }

    // ───────────────────────── Package ─────────────────────────

    private void WritePackage(HDocument document, string body, string outputPath)
    {
        var content = Declaration +
                      $"<office:document-content {Odf.Namespaces} office:version=\"{Odf.Version}\">" +
                      $"<office:automatic-styles>{_contentStyles.Xml}</office:automatic-styles>" +
                      $"<office:body><office:text>{body}</office:text></office:body></office:document-content>";
        var styles = Declaration +
                     $"<office:document-styles {Odf.Namespaces} office:version=\"{Odf.Version}\">" +
                     $"<office:styles>{CommonStyles(document)}{_listStyles}</office:styles>" +
                     $"<office:automatic-styles>{_stylesStyles.Xml}</office:automatic-styles>" +
                     $"<office:master-styles>{_masterPages}</office:master-styles></office:document-styles>";
        var meta = Declaration +
                   $"<office:document-meta {Odf.Namespaces} office:version=\"{Odf.Version}\"><office:meta><meta:generator>Filee</meta:generator>" +
                   (string.IsNullOrWhiteSpace(document.Title) ? "" : $"<dc:title>{Escape(document.Title)}</dc:title>") +
                   "</office:meta></office:document-meta>";

        var manifest = new StringBuilder(Declaration)
            .Append($"<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"{Odf.Version}\">")
            .Append($"<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"{Odf.Version}\" manifest:media-type=\"{Odf.MimeType}\"/>");
        foreach (var part in new[] { "content.xml", "styles.xml", "meta.xml" })
            manifest.Append($"<manifest:file-entry manifest:full-path=\"{part}\" manifest:media-type=\"text/xml\"/>");
        foreach (var (path, extension) in _media.Values)
            manifest.Append($"<manifest:file-entry manifest:full-path=\"{path}\" manifest:media-type=\"{PictureTypes[extension]}\"/>");
        manifest.Append("</manifest:manifest>");

        var temp = outputPath + ".tmp";
        using (var file = File.Create(temp))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            // OpenDocument rule: "mimetype" first and stored uncompressed, so the type can be read at a fixed offset.
            WriteText(zip, "mimetype", Odf.MimeType, CompressionLevel.NoCompression);
            WriteText(zip, "content.xml", content);
            WriteText(zip, "styles.xml", styles);
            WriteText(zip, "meta.xml", meta);
            foreach (var (source, (path, extension)) in _media)
            {
                using var target = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
                if (string.Equals(Path.GetExtension(source).TrimStart('.'), extension, StringComparison.OrdinalIgnoreCase)
                    || (extension == "jpg" && Path.GetExtension(source).Equals(".jpeg", StringComparison.OrdinalIgnoreCase)))
                {
                    using var input = File.OpenRead(source);
                    input.CopyTo(target);
                }
                else
                {
                    // WEBP, TIFF, AVIF ... → PNG, which every OpenDocument reader shows.
                    using var image = new MagickImage(source);
                    DdsChannelOrder.Correct(source, image);
                    image.Write(target, MagickFormat.Png);
                }
            }
            WriteText(zip, "META-INF/manifest.xml", manifest.ToString());
        }
        File.Move(temp, outputPath, overwrite: true);
    }

    private static void WriteText(ZipArchive zip, string name, string text, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    /// <summary>
    /// Package path of a picture, added once. Formats not every OpenDocument reader shows are converted to PNG when the
    /// package is written; null when the file is no picture Magick can read.
    /// </summary>
    private string? MediaTarget(string path)
    {
        if (_media.TryGetValue(path, out var known))
            return known.Path;
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (extension == "jpeg")
            extension = "jpg";
        if (!PictureTypes.ContainsKey(extension))
        {
            try
            {
                _ = new MagickImageInfo(path);
            }
            catch (MagickException)
            {
                return null;
            }
            extension = "png";
        }
        var target = $"Pictures/image{_media.Count + 1}.{extension}";
        _media[path] = (target, extension);
        return target;
    }

    // ───────────────────────── Named styles ─────────────────────────

    /// <summary>Default and named styles: body text as the DOCX writer's Normal (10 pt, 123 % ≈ 160 % of the font size).</summary>
    private static string CommonStyles(HDocument document)
    {
        var sb = new StringBuilder();
        sb.Append("<style:default-style style:family=\"paragraph\">")
          .Append("<style:paragraph-properties fo:line-height=\"123%\" fo:orphans=\"2\" fo:widows=\"2\" style:tab-stop-distance=\"40pt\" style:writing-mode=\"page\"/>")
          .Append("<style:text-properties fo:font-family=\"Calibri\" style:font-family-asian=\"'맑은 고딕'\" style:font-family-complex=\"Calibri\" ")
          .Append("fo:font-size=\"10pt\" style:font-size-asian=\"10pt\" style:font-size-complex=\"10pt\"/></style:default-style>");
        sb.Append("<style:style style:name=\"Standard\" style:family=\"paragraph\" style:class=\"text\"/>");
        sb.Append("<style:style style:name=\"Heading\" style:family=\"paragraph\" style:parent-style-name=\"Standard\" style:next-style-name=\"Standard\" style:class=\"text\">")
          .Append("<style:paragraph-properties fo:keep-with-next=\"always\"/></style:style>");
        for (var level = 1; level <= 9; level++)
        {
            var size = $"{HeadingSizes[level - 1]}pt";
            sb.Append($"<style:style style:name=\"Heading_20_{level}\" style:display-name=\"Heading {level}\" style:family=\"paragraph\" style:parent-style-name=\"Heading\" ")
              .Append($"style:next-style-name=\"Standard\" style:default-outline-level=\"{level}\" style:class=\"text\">")
              .Append($"<style:paragraph-properties fo:margin-top=\"{(level <= 2 ? 12 : 8)}pt\" fo:margin-bottom=\"4pt\" fo:keep-together=\"always\"/>")
              .Append($"<style:text-properties fo:font-size=\"{size}\" style:font-size-asian=\"{size}\" style:font-size-complex=\"{size}\" ")
              .Append("fo:font-weight=\"bold\" style:font-weight-asian=\"bold\" style:font-weight-complex=\"bold\"/></style:style>");
        }
        foreach (var kind in new[] { "Footnote", "Endnote" })
        {
            sb.Append($"<style:style style:name=\"{kind}\" style:family=\"paragraph\" style:parent-style-name=\"Standard\" style:class=\"extra\">")
              .Append("<style:paragraph-properties fo:line-height=\"100%\"/>")
              .Append("<style:text-properties fo:font-size=\"9pt\" style:font-size-asian=\"9pt\" style:font-size-complex=\"9pt\"/></style:style>");
        }
        foreach (var kind in new[] { "Header", "Footer" })
        {
            sb.Append($"<style:style style:name=\"{kind}\" style:family=\"paragraph\" style:parent-style-name=\"Standard\" style:class=\"extra\">")
              .Append("<style:paragraph-properties fo:line-height=\"100%\"><style:tab-stops>")
              .Append("<style:tab-stop style:position=\"225.65pt\" style:type=\"center\"/><style:tab-stop style:position=\"451.3pt\" style:type=\"right\"/>")
              .Append("</style:tab-stops></style:paragraph-properties></style:style>");
        }

        // Headings are outline paragraphs without numbers.
        sb.Append("<text:outline-style style:name=\"Outline\">");
        for (var level = 1; level <= 10; level++)
            sb.Append($"<text:outline-level-style text:level=\"{level}\" style:num-format=\"\"/>");
        sb.Append("</text:outline-style>");

        // One note numbering for the whole document (ODF has no other): the first section's.
        var section = document.Sections.FirstOrDefault();
        sb.Append(NotesConfiguration("footnote", (section?.Footnotes ?? HNoteShape.Footnote).Format, "Footnote"));
        sb.Append(NotesConfiguration("endnote", (section?.Endnotes ?? HNoteShape.Endnote).Format, "Endnote"));
        return sb.ToString();
    }

    private static string NotesConfiguration(string kind, HNoteFormat format, string style) =>
        $"<text:notes-configuration text:note-class=\"{kind}\" text:default-style-name=\"{style}\" style:num-format=\"{Escape(Odf.NumberFormat(format.Type))}\"" +
        (format.Prefix.Length > 0 ? $" style:num-prefix=\"{Escape(format.Prefix)}\"" : "") +
        (format.Suffix.Length > 0 ? $" style:num-suffix=\"{Escape(format.Suffix)}\"" : "") +
        $" text:start-value=\"0\"{(kind == "footnote" ? " text:footnotes-position=\"page\" text:start-numbering-at=\"document\"" : "")}/>";

    // ───────────────────────── Lists ─────────────────────────

    /// <summary>Indent of list level <paramref name="level"/> (HWPUNIT): 400 twips per level with a hanging number, as the DOCX writer's.</summary>
    private static int ListIndent(int level) => (level + 1) * 2000;

    /// <summary>The list style of a list definition, added to styles.xml on first use.</summary>
    private string ListStyle(HNumbering numbering, out bool first)
    {
        first = !_lists.TryGetValue(numbering, out var name);
        if (!first)
            return name!;
        name = $"L{_lists.Count + 1}";
        _lists[numbering] = name;
        _listStyles.Append($"<text:list-style style:name=\"{name}\">");
        for (var level = 1; level <= 10; level++)
        {
            var definition = level <= numbering.Levels.Count ? numbering.Levels[level - 1] : new HNumberingLevel("DIGIT", $"^{level}.", 1, Bullet: false);
            var indent = ListIndent(level - 1);
            var position = $"<style:list-level-properties text:list-level-position-and-space-mode=\"label-alignment\">" +
                           $"<style:list-level-label-alignment text:label-followed-by=\"listtab\" text:list-tab-stop-position=\"{Odf.Points(indent)}\" " +
                           $"fo:text-indent=\"{Odf.Points(-2000)}\" fo:margin-left=\"{Odf.Points(indent)}\"/></style:list-level-properties>";
            if (definition.Bullet)
            {
                var bullet = definition.Text.Length > 0 ? StringInfo.GetNextTextElement(definition.Text) : "•";
                _listStyles.Append($"<text:list-level-style-bullet text:level=\"{level}\" text:bullet-char=\"{Escape(bullet)}\">{position}</text:list-level-style-bullet>");
                continue;
            }
            var (prefix, suffix, shown) = NumberText(definition.Text);
            var format = shown == 0 ? "" : Odf.NumberFormat(definition.Format);
            _listStyles.Append($"<text:list-level-style-number text:level=\"{level}\"")
                .Append(prefix.Length > 0 ? $" style:num-prefix=\"{Escape(prefix)}\"" : "")
                .Append(suffix.Length > 0 ? $" style:num-suffix=\"{Escape(suffix)}\"" : "")
                .Append($" style:num-format=\"{Escape(format)}\"")
                .Append(shown > 1 ? $" text:display-levels=\"{Math.Min(shown, level)}\"" : "")
                .Append(definition.Start != 1 ? $" text:start-value=\"{Math.Max(0, definition.Start)}\"" : "")
                .Append($">{position}</text:list-level-style-number>");
        }
        _listStyles.Append("</text:list-style>");
        return name;
    }

    /// <summary>"(^1.^2)" → ("(", ")", 2): the text around the numbers and how many levels they show.</summary>
    internal static (string Prefix, string Suffix, int Levels) NumberText(string text)
    {
        var first = -1;
        var last = -1;
        var count = 0;
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] != '^' || !char.IsAsciiDigit(text[i + 1]))
                continue;
            if (first < 0)
                first = i;
            last = i + 2;
            count++;
        }
        return first < 0 ? (text, "", 0) : (text[..first], text[last..], count);
    }

    // ───────────────────────── Master pages ─────────────────────────

    /// <summary>
    /// The master page of a section (its page layout, headers and footers) in styles.xml. ODF's top margin reaches the
    /// header, whose area (minimum height and spacing) comes before the body, as the model's Top and Header have it;
    /// without a header the body starts at the top margin, so the header area is added to it.
    /// </summary>
    private string MasterPage(HSection section, int index)
    {
        var page = section.Page ?? DefaultPage;
        var name = index == 0 ? "Standard" : $"Section_20_{index + 1}";
        var layout = $"pm{++_pageLayouts}";
        var width = page.TextWidth;

        var (header, headerLeft) = Pair(section.Headers);
        var (footer, footerLeft) = Pair(section.Footers);
        var hasHeader = header is not null;
        var hasFooter = footer is not null;

        var columns = section.Columns.Count > 1 ? Columns(section.Columns) : "";
        var props = new StringBuilder($"<style:page-layout-properties fo:page-width=\"{Odf.Points(page.Width)}\" fo:page-height=\"{Odf.Points(page.Height)}\"")
            .Append($" style:print-orientation=\"{(page.Landscape ? "landscape" : "portrait")}\"")
            .Append($" fo:margin-top=\"{Odf.Points(hasHeader ? page.Top : page.Top + page.Header)}\"")
            .Append($" fo:margin-bottom=\"{Odf.Points(hasFooter ? page.Bottom : page.Bottom + page.Footer)}\"")
            .Append($" fo:margin-left=\"{Odf.Points(page.Left + page.Gutter)}\" fo:margin-right=\"{Odf.Points(page.Right)}\" style:writing-mode=\"lr-tb\">")
            .Append(columns)
            .Append("<style:footnote-sep style:width=\"0.51pt\" style:distance-before-sep=\"2.87pt\" style:distance-after-sep=\"2.87pt\" style:line-style=\"solid\" style:adjustment=\"left\" style:rel-width=\"25%\" style:color=\"#000000\"/>")
            .Append("</style:page-layout-properties>");
        string Area(string kind, bool present, int height, string spacing)
        {
            if (!present)
                return $"<style:{kind}-style/>";
            var gap = Math.Min(height, HeaderSpacing);
            return $"<style:{kind}-style><style:header-footer-properties fo:min-height=\"{Odf.Points(height - gap)}\" fo:{spacing}=\"{Odf.Points(gap)}\"/></style:{kind}-style>";
        }
        _stylesStyles.Append($"<style:page-layout style:name=\"{layout}\">{props}{Area("header", hasHeader, page.Header, "margin-bottom")}{Area("footer", hasFooter, page.Footer, "margin-top")}</style:page-layout>");

        var master = new StringBuilder($"<style:master-page style:name=\"{name}\"{(index == 0 ? "" : $" style:display-name=\"Section {index + 1}\"")} style:page-layout-name=\"{layout}\">");
        void Part(string element, List<HBlock>? blocks, string style)
        {
            if (blocks is not null)
                master.Append($"<style:{element}>{InStyles(() => Nested(width, () => Blocks(blocks, style, Context.Header)))}</style:{element}>");
        }
        Part("header", header, "Header");
        Part("header-left", headerLeft, "Header");
        if (hasHeader && section.HideFirstHeader)
            master.Append("<style:header-first><text:p/></style:header-first>");
        Part("footer", footer, "Footer");
        Part("footer-left", footerLeft, "Footer");
        if (hasFooter && section.HideFirstFooter)
            master.Append("<style:footer-first><text:p/></style:footer-first>");
        _masterPages.Append(master.Append("</style:master-page>"));
        return name;
    }

    /// <summary>The header for every (or every right) page and the one for left (even) pages, if any.</summary>
    private static (List<HBlock>? All, List<HBlock>? Left) Pair(List<HHeaderFooter> items)
    {
        var both = items.LastOrDefault(i => i.Pages == HPageType.Both)?.Blocks;
        var odd = items.LastOrDefault(i => i.Pages == HPageType.Odd)?.Blocks;
        var even = items.LastOrDefault(i => i.Pages == HPageType.Even)?.Blocks;
        var all = odd ?? both;
        if (even is null)
            return (all, null);
        return (all ?? [], even);
    }

    /// <summary>style:columns of a page layout or section.</summary>
    private static string Columns(HColumns columns) =>
        $"<style:columns fo:column-count=\"{Math.Clamp(columns.Count, 1, 255)}\" fo:column-gap=\"{Odf.Points(columns.Gap)}\">" +
        (columns.Separator ? "<style:column-sep style:style=\"solid\" style:width=\"0.5pt\" style:color=\"#000000\" style:height=\"100%\" style:vertical-align=\"top\"/>" : "") +
        "</style:columns>";

    // ───────────────────────── Helpers ─────────────────────────

    private static string Escape(string text) => HwpxWriter.Escape(text);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>"#RRGGBB" or null when it is not a colour.</summary>
    private static string? Hex(string? color) => Odf.Color(color);
}
