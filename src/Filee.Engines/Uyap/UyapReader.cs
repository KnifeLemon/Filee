// UYAP documents (.udf, #1338): the files of the UYAP Document Editor (UYAP Doküman Editörü), which Turkish courts,
// lawyers and citizens write petitions and decisions in. Read into the document model with no external engine, so the
// Filee renderer turns them into PDF and the writers into DOCX, ODT, HWPX, TXT and the rest.
//
// A UYAP document is a ZIP package with one part, content.xml (format_id 1.x), written by the editor's Java text
// component:
//   <template format_id="1.8">
//     <content><![CDATA[ the whole text, paragraphs ended by "\n" ]]></content>
//     <properties><pageFormat mediaSizeName leftMargin rightMargin topMargin bottomMargin paperOrientation
//                             headerFOffset footerFOffset/></properties>
//     <elements resolver="hvl-default">
//       <paragraph Alignment LeftIndent RightIndent FirstLineIndent SpaceAbove SpaceBelow LineSpacing TabSet>
//         <content startOffset length family size bold italic underline strikethrough foreground .../>
//         <image imageData="(Base64)" width height startOffset length/>
//       </paragraph>
//       <table columnCount columnSpans border><row><cell><paragraph .../></cell></row></table>
//       <header>/<footer> with paragraphs, <page-break/>
//     </elements>
//     <styles><style name family size bold .../></styles>
//   </template>
// The text lives once, in the CDATA block; every run names its stretch of it by offset and length (UTF-16 units, as
// Java counts). Lengths are points; sizes are points; colours are Java ARGB integers; alignment is Swing's (0 left,
// 1 centre, 2 right, 3 justified). A run takes what it doesn't say from its paragraph, then from the style the
// elements name (their resolver, "hvl-default": Times New Roman 12 pt); Java's logical fonts (Dialog, Serif, ...)
// become the fonts Java draws them with on Windows. Text is drawn without kerning, as Java draws it.
//
// Lines are as high as Java makes them (FontMetrics: the ascent rounded up, then descent and leading rounded up
// together), for the fonts UYAP documents use, as an "at least" height: a 12 pt Calibri line is 15 pt, as in the PDF the
// editor prints, so pages break where they break there. Swing's default tab stops are every 72 pt.
//
// ".udf" also names Universal Disk Format disc images. Which one a file is, is told by its content (IsUyap): a ZIP
// whose content.xml has a <template> root. A disc image given to a document route is refused with
// error.udf_not_uyap, which points to Extract; the archive engine unpacks it as the disc it is (ArchiveExtractor).
//
// The package is read within DocumentLimits (Documents/DocumentPackage: part sizes counted while unpacking, the
// inflate ratio, no DTD, depth and element limits); pictures are Base64 in the XML, decoded up to MaxPictureBytes each
// and MaxPictures in all, and left out when their header claims more pixels than the limits allow. Tables nested past
// Nesting.MaxDepth become plain paragraphs.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Uyap;

/// <summary>Reads UYAP documents (.udf) into the document model.</summary>
internal sealed class UyapReader
{
    /// <summary>The format id of UYAP documents.</summary>
    public const string Format = "udf";

    /// <summary>The refusal of a .udf that is not a UYAP document (a disc image, most likely).</summary>
    public const string NotUyapKey = "error.udf_not_uyap";

    private const string ContentPart = "content.xml";

    /// <summary>Largest picture decoded; bigger ones are left out.</summary>
    internal const int MaxPictureBytes = 32 * 1024 * 1024;

    /// <summary>Pictures written per document; further ones are left out.</summary>
    internal const int MaxPictures = 2_000;

    /// <summary>Swing's tab stops when a paragraph sets none: every 72 pt.</summary>
    private const int DefaultTabStop = 7200;

    /// <summary>
    /// Ascent, descent and line gap (hhea, in em) of the fonts UYAP documents use, for Java's line height; regular and
    /// bold share them. A font not listed takes the font's own single line height.
    /// </summary>
    private static readonly Dictionary<string, (double Ascent, double Descent, double Gap)> JavaMetrics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Times New Roman"] = (0.8911, 0.2163, 0.0425),
        ["Calibri"] = (0.75, 0.25, 0.2207),
        ["Arial"] = (0.9053, 0.2119, 0.0327),
        ["Courier New"] = (0.8325, 0.3003, 0),
        ["Tahoma"] = (1.0005, 0.2065, 0),
        ["Verdana"] = (1.0054, 0.21, 0),
        ["Cambria"] = (0.9502, 0.2222, 0),
        ["Georgia"] = (0.917, 0.2192, 0),
        ["Segoe UI"] = (1.0791, 0.251, 0),
    };

    private readonly string _text;
    private readonly XElement? _baseStyle;
    private readonly string _mediaFolder;
    private readonly DocumentBudget _budget = new(DocumentLimits.Default);
    private int _pictures;
    private bool _breakBefore;

    private UyapReader(string text, Dictionary<string, XElement> styles, string? resolver, string mediaFolder)
    {
        _text = text;
        _baseStyle = resolver is not null && styles.TryGetValue(resolver, out var style) ? style : null;
        _mediaFolder = mediaFolder;
    }

    // ───────────────────────── Detection ─────────────────────────

    /// <summary>
    /// True when <paramref name="path"/> is a UYAP document: a ZIP package with a content.xml whose root is
    /// &lt;template&gt;. A disc image (Universal Disk Format), another ZIP or a file that can't be read is not.
    /// </summary>
    public static bool IsUyap(string path)
    {
        try
        {
            if (!IsZip(path))
                return false;
            using var package = DocumentPackage.Open(path);
            if (Content(package) is not { } entry)
                return false;
            using var stream = package.Open(entry);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                CheckCharacters = false,
            });
            return reader.MoveToContent() == XmlNodeType.Element && reader.LocalName == "template";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException
                                       or ConversionRefusedException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>True when the file starts like a ZIP package (a disc image starts with its zeroed system area).</summary>
    internal static bool IsZip(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[4];
        return stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4 && head is [0x50, 0x4B, 0x03, 0x04];
    }

    private static System.IO.Compression.ZipArchiveEntry? Content(DocumentPackage package) =>
        package.GetEntry(ContentPart)
        ?? package.Entries.FirstOrDefault(e => e.FullName.Equals(ContentPart, StringComparison.OrdinalIgnoreCase));

    private static ConversionRefusedException NotUyap() => new(NotUyapKey);

    // ───────────────────────── Document ─────────────────────────

    /// <summary>Reads the UYAP document at <paramref name="path"/>; pictures go into <paramref name="mediaFolder"/>.</summary>
    public static HDocument Read(string path, string mediaFolder)
    {
        if (!IsZip(path))
            throw NotUyap();
        XDocument xml;
        using (var package = DocumentPackage.Open(path, notAPackage: NotUyap))
        {
            var entry = Content(package) ?? throw NotUyap();
            using var stream = package.Open(entry);
            try
            {
                // Java writes whatever the text holds into the CDATA block, control characters too.
                xml = SafeXml.Load(stream, package.Limits.Xml(0, preserveWhitespace: true) with { CheckCharacters = false },
                    message => DocumentLimits.Exceeded($"{ContentPart}: {message}"));
            }
            catch (XmlException ex)
            {
                throw new InvalidDataException($"The {ContentPart} of the UYAP document is not well-formed XML ({ex.Message}). It may be damaged.", ex);
            }
        }
        var root = xml.Root;
        if (root is null || root.Name.LocalName != "template")
            throw NotUyap();
        return Nesting.OnLargeStack(() => Build(root, mediaFolder));
    }

    private static HDocument Build(XElement root, string mediaFolder)
    {
        var text = Child(root, "content")?.Value ?? "";
        var styles = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var style in Children(Child(root, "styles"), "style"))
        {
            if (Attr(style, "name") is { Length: > 0 } name)
                styles.TryAdd(name, style);
        }
        var elements = Child(root, "elements");
        var reader = new UyapReader(text, styles, Attr(elements, "resolver") ?? "hvl-default", mediaFolder);

        var section = new HSection { Page = Page(Child(Child(root, "properties"), "pageFormat")) };
        if (elements is not null)
            reader.Blocks(elements, section.Blocks, section, depth: 0);
        // A file without paragraph elements: its text, a paragraph per line.
        if (section.Blocks.Count == 0 && text.Length > 0)
        {
            var format = reader.CharFormat(null, null);
            foreach (var line in text.Split('\n'))
            {
                reader._budget.Paragraph();
                var paragraph = new HParagraph { Format = reader.ParaFormat(null, [format]), MarkFormat = format };
                reader.AddText(paragraph, line, format, last: true);
                section.Blocks.Add(paragraph);
            }
        }
        var document = new HDocument { DefaultTabStop = DefaultTabStop };
        document.Sections.Add(section);
        return document;
    }

    /// <summary>
    /// The page: A4 (UYAP's mediaSizeName 1, and what the editor uses for others Filee hasn't seen), turned for
    /// Java's landscape orientations (0 and 2); margins and the header and footer distances in points.
    /// </summary>
    private static HPage Page(XElement? format)
    {
        int width = 59528, height = 84189;
        if (Number(format, "paperOrientation") is 0 or 2)
            (width, height) = (height, width);
        int Margin(string name, double fallback) => HwpxUnits.FromPoints(Math.Clamp(Number(format, name) ?? fallback, 0, 1000));
        var left = Margin("leftMargin", 42.525);
        var right = Margin("rightMargin", 42.525);
        var top = Margin("topMargin", 42.525);
        var bottom = Margin("bottomMargin", 42.525);
        var header = Math.Min(top, Margin("headerFOffset", 20));
        var footer = Math.Min(bottom, Margin("footerFOffset", 20));
        // The text keeps at least a fifth of the paper.
        if (left + right > width * 4 / 5)
            left = right = width / 10;
        if (top + bottom > height * 4 / 5)
            top = bottom = header = footer = height / 10;
        return new HPage(width, height, left, right, header, footer, top - header, bottom - footer, 0);
    }

    // ───────────────────────── Blocks ─────────────────────────

    private void Blocks(XElement container, List<HBlock> output, HSection? section, int depth)
    {
        foreach (var element in container.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "paragraph":
                    output.Add(Paragraph(element));
                    break;
                case "table":
                    Table(element, output, depth);
                    break;
                case "page-break" or "pageBreak":
                    _breakBefore = true;
                    break;
                case "header" or "footer" when section is not null:
                    {
                        var part = new HHeaderFooter(HPageType.Both);
                        var breakBefore = _breakBefore;
                        Blocks(element, part.Blocks, null, depth);
                        _breakBefore = breakBefore;
                        if (part.Blocks.Count > 0)
                            (element.Name.LocalName == "header" ? section.Headers : section.Footers).Add(part);
                        break;
                    }
            }
        }
    }

    private HParagraph Paragraph(XElement element)
    {
        _budget.Paragraph();
        var paragraph = new HParagraph { PageBreakBefore = _breakBefore };
        _breakBefore = false;
        var formats = new List<HCharFormat>();
        HCharFormat? mark = null;
        var runs = element.Elements().ToList();
        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            switch (run.Name.LocalName)
            {
                case "image":
                    Image(run, paragraph);
                    break;
                case "page-break" or "pageBreak":
                    if (paragraph.Inlines.Count == 0)
                        paragraph.PageBreakBefore = true;
                    else
                        _breakBefore = true;
                    break;
                case "field" when FieldKind(run) is { } kind:
                    {
                        var format = CharFormat(run, element);
                        formats.Add(format);
                        paragraph.Inlines.Add(new HField(kind, format));
                        break;
                    }
                default:
                    if (run.Attribute("startOffset") is not null)
                    {
                        var format = CharFormat(run, element);
                        formats.Add(format);
                        mark = format;
                        AddText(paragraph, Slice(run), format, last: i == runs.Count - 1);
                    }
                    break;
            }
        }
        mark ??= CharFormat(null, element);
        if (formats.Count == 0)
            formats.Add(mark.Value);
        paragraph.MarkFormat = mark.Value;
        paragraph.Format = ParaFormat(element, formats);
        return paragraph;
    }

    /// <summary>The stretch of the text a run names, cut to the text.</summary>
    private string Slice(XElement run)
    {
        var start = (int)Math.Clamp(Number(run, "startOffset") ?? 0, 0, _text.Length);
        var length = (int)Math.Clamp(Number(run, "length") ?? 0, 0, _text.Length - start);
        return _text.Substring(start, length);
    }

    /// <summary>Text with its tabs; the newline that ends the paragraph goes, one inside it breaks the line.</summary>
    private void AddText(HParagraph paragraph, string text, HCharFormat format, bool last)
    {
        if (last && text.EndsWith('\n'))
            text = text[..^1];
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : '\0';
            if (i < text.Length && !char.IsControl(c))
                continue;
            if (i > start)
                paragraph.Inlines.Add(new HText(text[start..i], format));
            start = i + 1;
            if (c == '\t')
                paragraph.Inlines.Add(new HTab(format));
            else if (c is '\n' or '\v' && i < text.Length)
                paragraph.Inlines.Add(new HLineBreak(format));
        }
    }

    private static HFieldKind? FieldKind(XElement field)
    {
        var name = string.Join(' ', field.Attributes().Where(a => a.Name.LocalName is "fieldName" or "type" or "name").Select(a => a.Value)).ToLowerInvariant();
        if (!name.Contains("page") && !name.Contains("sayfa"))
            return null;
        return name.Contains("count") || name.Contains("total") || name.Contains("toplam") ? HFieldKind.TotalPages : HFieldKind.PageNumber;
    }

    // ───────────────────────── Tables ─────────────────────────

    private void Table(XElement element, List<HBlock> output, int depth)
    {
        var rows = Children(element, "row").ToList();
        if (depth >= Nesting.MaxDepth)
        {
            // Nested past what the writers follow: the cells' content as paragraphs.
            foreach (var cell in rows.SelectMany(row => Children(row, "cell")))
                Blocks(cell, output, null, depth);
            return;
        }
        _budget.Object();
        var limits = _budget.Limits;
        var table = new HTable { PageBreakBefore = _breakBefore };
        _breakBefore = false;
        var bordered = Attr(element, "border") is not { } border || !border.Contains("none", StringComparison.OrdinalIgnoreCase);
        table.Borders = bordered ? HBorders.Grid : HBorders.Empty;
        foreach (var row in rows)
        {
            var model = new HRow();
            foreach (var cell in Children(row, "cell"))
            {
                var modelCell = new HCell
                {
                    ColSpan = (int)Math.Clamp(Number(cell, "colspan") ?? Number(cell, "colSpan") ?? Number(cell, "columnSpan") ?? 1, 1, limits.MaxTableColumns),
                    RowSpan = (int)Math.Clamp(Number(cell, "rowspan") ?? Number(cell, "rowSpan") ?? 1, 1, 100_000),
                    Fill = Color(cell, "bgColor") ?? Color(cell, "background"),
                    VerticalAlign = (Attr(cell, "verticalAlignment") ?? Attr(cell, "vAlign"))?.ToLowerInvariant() switch
                    {
                        "1" or "center" or "middle" => HVerticalAlign.Center,
                        "2" or "bottom" => HVerticalAlign.Bottom,
                        _ => HVerticalAlign.Top,
                    },
                };
                if (!bordered)
                    modelCell.Borders = HBorders.Empty;
                Blocks(cell, modelCell.Blocks, null, depth + 1);
                if (modelCell.Blocks.Count == 0)
                    modelCell.Blocks.Add(new HParagraph { MarkFormat = CharFormat(null, null) });
                model.Cells.Add(modelCell);
            }
            if (model.Cells.Count > 0)
                table.Rows.Add(model);
        }
        if (table.Rows.Count == 0)
            return;
        var columns = table.Rows.Max(r => r.Cells.Sum(c => c.ColSpan));
        columns = Math.Clamp(Math.Max(columns, (int)Math.Clamp(Number(element, "columnCount") ?? 0, 0, limits.MaxTableColumns)), 1, limits.MaxTableColumns);
        table.ColumnCount = columns;
        // columnSpans: the columns' widths, taken as shares of the text width.
        var spans = (Attr(element, "columnSpans") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(columns)
            .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 && double.IsFinite(v) ? v : 0)
            .ToArray();
        if (spans.Length == columns && spans.Sum() is > 0 and var total)
            table.RelativeWidths = [.. spans.Select(s => s / total)];
        output.Add(table);
    }

    // ───────────────────────── Pictures ─────────────────────────

    private void Image(XElement element, HParagraph paragraph)
    {
        var data = Attr(element, "imageData") ?? Attr(element, "data");
        if (string.IsNullOrWhiteSpace(data) || _pictures >= MaxPictures || data.Length / 4L * 3 > MaxPictureBytes)
            return;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(data.Trim());
        }
        catch (FormatException)
        {
            return;
        }
        var extension = bytes switch
        {
            [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => ".png",
            [0xFF, 0xD8, ..] => ".jpg",
            [(byte)'G', (byte)'I', (byte)'F', ..] => ".gif",
            [(byte)'B', (byte)'M', ..] => ".bmp",
            _ => null,
        };
        if (extension is null)
            return;
        _budget.Object();
        Directory.CreateDirectory(_mediaFolder);
        var file = Path.Combine(_mediaFolder, $"udf-image{++_pictures:0000}{extension}");
        File.WriteAllBytes(file, bytes);
        if (PictureSize.Read(file) is not { } size || (long)size.Width * size.Height > _budget.Limits.MaxPicturePixels)
        {
            File.Delete(file);
            return;
        }
        var image = new HImage(file);
        if (Number(element, "width") is > 0 and < 10_000 and var width && Number(element, "height") is > 0 and < 10_000 and var height)
        {
            image.Width = HwpxUnits.FromPoints(width);
            image.Height = HwpxUnits.FromPoints(height);
        }
        paragraph.Inlines.Add(image);
    }

    // ───────────────────────── Formats ─────────────────────────

    /// <summary>
    /// What a run says about <paramref name="name"/>, else its paragraph, else the elements' style. The editor's own
    /// "default" style (Dialog, dark grey) is its window's, not the document's: the editor prints black Times New Roman.
    /// </summary>
    private string? Lookup(XElement? run, XElement? paragraph, string name) =>
        Attr(run, name) ?? Attr(paragraph, name) ?? Attr(_baseStyle, name);

    private HCharFormat CharFormat(XElement? run, XElement? paragraph)
    {
        bool Flag(string name) => string.Equals(Lookup(run, paragraph, name), "true", StringComparison.OrdinalIgnoreCase);
        var size = double.TryParse(Lookup(run, paragraph, "size"), NumberStyles.Float, CultureInfo.InvariantCulture, out var points)
                   && points is > 0 and < 1000 ? points : 12;
        var shade = Color(run, "background") ?? Color(paragraph, "background");
        return new HCharFormat(
            Bold: Flag("bold"),
            Italic: Flag("italic"),
            Underline: Flag("underline"),
            Strike: Flag("strikethrough"),
            Superscript: Flag("superscript") ? true : null,
            Subscript: Flag("subscript") ? true : null,
            Size: (int)Math.Round(size * 100),
            Font: FontName(Lookup(run, paragraph, "family")),
            Color: ColorOf(Lookup(run, paragraph, "foreground")) ?? "#000000",
            Shade: shade is "#FFFFFF" ? null : shade,
            // Java draws text without the font's kerning.
            Layout: NoKerning);
    }

    private static readonly HCharLayout NoKerning = new(Kerning: false);

    /// <summary>A font family, with Java's logical fonts as Java draws them on Windows.</summary>
    private static string FontName(string? family) => family?.Trim() switch
    {
        null or "" or "Serif" or "serif" => "Times New Roman",
        "Dialog" or "SansSerif" or "sansserif" or "dialog" => "Arial",
        "DialogInput" or "Monospaced" or "monospaced" => "Courier New",
        var name => name,
    };

    private HParaFormat ParaFormat(XElement? element, IReadOnlyList<HCharFormat> formats)
    {
        double Points(string name, double min, double max) => Math.Clamp(Number(element, name) ?? 0, min, max);
        var spacing = Math.Clamp(Number(element, "LineSpacing") ?? 0, 0, 10);
        HLineSpacing lineSpacing;
        HLineHeight lineHeight;
        if (JavaLineHeight(formats) is { } height)
        {
            var value = (int)Math.Round(height * (1 + spacing));
            lineSpacing = new HLineSpacing(HLineSpacingKind.AtLeast, value);
            lineHeight = new HLineHeight(HLineHeightRule.AtLeast, value);
        }
        else
        {
            lineSpacing = new HLineSpacing(HLineSpacingKind.Percent, (int)Math.Round(120 * (1 + spacing)));
            lineHeight = new HLineHeight(HLineHeightRule.Multiple, (int)Math.Round(240 * (1 + spacing)));
        }
        return new HParaFormat(
            Align: (int?)Number(element, "Alignment") switch
            {
                1 => HAlign.Center,
                2 => HAlign.Right,
                3 => HAlign.Justify,
                _ => HAlign.Left,
            },
            Left: HwpxUnits.FromPoints(Points("LeftIndent", -1000, 1000)),
            Right: HwpxUnits.FromPoints(Points("RightIndent", -1000, 1000)),
            FirstLine: HwpxUnits.FromPoints(Points("FirstLineIndent", -1000, 1000)),
            Before: HwpxUnits.FromPoints(Points("SpaceAbove", 0, 1000)),
            After: HwpxUnits.FromPoints(Points("SpaceBelow", 0, 1000)),
            LineSpacing: lineSpacing,
            KeepWithNext: false,
            KeepLines: false,
            WidowOrphan: false,
            Tabs: Tabs(Attr(element, "TabSet")),
            Layout: new HParaLayout(LineHeight: lineHeight, SnapToGrid: false));
    }

    /// <summary>
    /// The height Java gives a line of these runs (HWPUNIT): the tallest of ascent rounded up plus descent and leading
    /// rounded up, as FontMetrics.getHeight does; null when a run's font has no metrics here.
    /// </summary>
    private static int? JavaLineHeight(IReadOnlyList<HCharFormat> formats)
    {
        var tallest = 0;
        foreach (var format in formats)
        {
            if (format.Font is not { } font || !JavaMetrics.TryGetValue(font, out var metrics))
                return null;
            var size = (format.Size ?? 1200) / 100.0;
            var ascent = (int)(0.95 + metrics.Ascent * size);
            tallest = Math.Max(tallest, ascent + (int)(0.95 + (metrics.Descent + metrics.Gap) * size));
        }
        return tallest > 0 ? tallest * 100 : null;
    }

    /// <summary>
    /// Swing's TabSet ("position:alignment:leader", comma separated; positions in points from the left indent).
    /// Alignment 1 right, 2 centre, 4 decimal; leader 1 dots, 2 hyphens, 3 underline, 4 thick line, 5 equals.
    /// </summary>
    private static List<HTabStop>? Tabs(string? tabSet)
    {
        if (string.IsNullOrWhiteSpace(tabSet))
            return null;
        var stops = new List<HTabStop>();
        foreach (var item in tabSet.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(64))
        {
            var parts = item.Split(':');
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var position) || position is <= 0 or > 2000)
                continue;
            var kind = parts.Length > 1 ? parts[1] switch
            {
                "1" => HTabKind.Right,
                "2" => HTabKind.Center,
                "4" => HTabKind.Decimal,
                _ => HTabKind.Left,
            } : HTabKind.Left;
            var leader = parts.Length > 2 ? parts[2] switch
            {
                "1" => "DOT",
                "2" => "DASH",
                "3" or "4" or "5" => "SOLID",
                _ => "NONE",
            } : "NONE";
            stops.Add(new HTabStop(HwpxUnits.FromPoints(position), kind, leader));
        }
        return stops.Count == 0 ? null : [.. stops.OrderBy(s => s.Position)];
    }

    /// <summary>A Java colour (signed ARGB integer, or #RRGGBB) as "#RRGGBB"; null when absent or fully transparent.</summary>
    internal static string? ColorOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        if (value.StartsWith('#') && value.Length == 7 && int.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return $"#{hex:X6}";
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return null;
        var argb = unchecked((uint)number);
        // A value without alpha bits is an RGB colour; one with them but none of alpha is transparent.
        if (number is >= 0 and <= 0xFFFFFF)
            return $"#{argb:X6}";
        return (argb >> 24) == 0 ? null : $"#{argb & 0xFFFFFF:X6}";
    }

    private static string? Color(XElement? element, string name) => ColorOf(Attr(element, name));

    // ───────────────────────── XML ─────────────────────────

    private static XElement? Child(XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Children(XElement? parent, string name) =>
        parent?.Elements().Where(e => e.Name.LocalName == name) ?? [];

    private static string? Attr(XElement? element, string name) => element?.Attribute(name)?.Value;

    private static double? Number(XElement? element, string name) =>
        double.TryParse(Attr(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
}
