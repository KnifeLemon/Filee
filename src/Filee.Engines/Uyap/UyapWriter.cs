// Writes UYAP documents (.udf, #1546, #1633) from the document model, the counterpart of UyapReader: whatever Filee
// reads (Word, ODT, RTF, 한글, Markdown, HTML, PDF ...) becomes a file the UYAP Document Editor opens. Written by hand
// from the format as the editor writes it (the sample on #1338 and what is known of the format), the way the reader
// reads it:
//
//   content.xml in a ZIP:
//   <template format_id="1.8">
//     <content><![CDATA[ the text of the header, the body and the footer, every paragraph ended by "\n" ]]></content>
//     <properties><pageFormat mediaSizeName="1" leftMargin ... headerFOffset footerFOffset/></properties>
//     <elements resolver="hvl-default">
//       <header> paragraphs </header>
//       <paragraph Alignment LeftIndent RightIndent FirstLineIndent SpaceAbove SpaceBelow LineSpacing TabSet
//                  Numbered|Bulleted NumberType|BulletType ListId ListLevel>
//         <content startOffset length family size bold italic underline strikethrough foreground background/>
//         <image imageData width height startOffset length="1"/>   (on U+FFFC in the text)
//       </paragraph>
//       <table tableName columnCount columnSpans border><row rowName rowType height><cell colspan align fillColor
//              border borderSpec ...> paragraphs </cell></row></table>
//       <page-break><paragraph><content .../></paragraph></page-break>
//       <footer> paragraphs </footer>
//     </elements>
//     <styles> the editor's "default" and "hvl-default" </styles>
//   </template>
//
// Offsets and lengths count UTF-16 units, as Java does. Lengths are points, colours Java's signed ARGB integers.
// Runs say only what differs from hvl-default (Times New Roman 12 pt, black); adjacent runs alike are joined, and the
// "\n" ending a paragraph is in its last run when that run is formatted like the paragraph mark, as the editor does;
// the text ends with one "\n" more that no element covers, as in the editor's files.
//
// What UYAP has no place for: the paper is A4 (the only size Filee has seen, mediaSizeName 1) turned for landscape;
// one header and one footer (the first section's, both-pages before odd before even); later sections start on a new
// page; page-number fields are left out (the editor keeps its page numbers on the footer, in a form not known yet);
// a list's start number goes; notes become numbers in the text with their text after the section's body; text boxes
// become their paragraphs after the paragraph they were in; shapes, bookmarks and link targets go (link text stays).
// Merged rows become cells under the first one without the borders between them (UYAP merges columns only); nested
// tables stay nested. Pictures are PNG or JPEG (others are turned into PNG) at their size, never wider than the text.
//
// The XML is written safely: characters XML can't hold are left out of the text and the attributes ("\r" too, which
// an XML parser would turn into "\n" and so shift every offset after it), lone surrogates become U+FFFD, and "]]>" in
// the text splits the CDATA block. Limits: content.xml stays within what the reader takes (DocumentLimits.MaxPartBytes,
// the paragraph, object and XML element counts, and an inflate ratio it accepts, stored uncompressed when the text is
// so repetitive that it would pass the ratio); pictures past the reader's limits are left out; tables pass
// HwpxWriter.PlaceCells' cell limit; the model is nested no deeper than Nesting.MaxDepth. The elements are written to
// a temporary file first (the text before them is only known once they are), the package through a temporary file of
// its own; a failure or a cancellation leaves no file.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Uyap;

/// <summary>Converts an <see cref="HDocument"/> into a UYAP document (.udf).</summary>
internal sealed class UyapWriter
{
    /// <summary>The page when the model gives none: A4 with the editor's own margins (42.525 pt, header and footer 20 pt).</summary>
    private static readonly HPage DefaultPage = new(59528, 84189, 4253, 4253, 2000, 2000, 2253, 2253, 0);

    /// <summary>The image placeholder character in the text.</summary>
    private const char ObjectCharacter = '\uFFFC';

    /// <summary>Heading sizes (pt) where the heading's runs give none, and their space above (HWPUNIT).</summary>
    private static readonly int[] HeadingSizes = [16, 14, 13, 12, 12, 12, 12, 12, 12];

    private static readonly UTF8Encoding Utf8 = new(false);

    private const string Prologue = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?> \n\n<template format_id=\"1.8\" >\n<content><![CDATA[";

    private const string Styles = "<styles><style name=\"default\" description=\"Geçerli\" family=\"Dialog\" size=\"12\" bold=\"false\" italic=\"false\" foreground=\"-13421773\" FONT_ATTRIBUTE_KEY=\"javax.swing.plaf.FontUIResource[family=Dialog,name=Dialog,style=plain,size=12]\" /><style name=\"hvl-default\" family=\"Times New Roman\" size=\"12\" description=\"Gövde\" /></styles>\n</template>\n";

    private readonly HDocument _document;
    private readonly DocumentLimits _limits;
    private readonly CancellationToken _token;
    private readonly StringBuilder _text = new();
    private StreamWriter _xml = null!;
    private long _textBytes;
    private long _xmlBytes;
    private long _elements;
    private int _paragraphs;
    private int _objects;
    private int _pictures;
    private int _width;
    private bool _firstBlock = true;

    /// <summary>Notes of the section being written, written after its body.</summary>
    private readonly List<(int Number, HNote Note)> _notes = [];
    private int _noteNumber;

    private readonly Dictionary<HNumbering, int> _listIds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, (byte[] Bytes, int Width, int Height)?> _pictureData = new(StringComparer.OrdinalIgnoreCase);

    private UyapWriter(HDocument document, DocumentLimits limits, CancellationToken token)
    {
        _document = document;
        _limits = limits;
        _token = token;
    }

    /// <summary>Writes <paramref name="document"/> as a .udf file (replaced atomically, through a temporary file of its own).</summary>
    public static void Write(HDocument document, string outputPath, CancellationToken cancellationToken = default) =>
        Write(document, outputPath, DocumentLimits.Default, cancellationToken);

    /// <summary>Writes within other <paramref name="limits"/> than <see cref="DocumentLimits.Default"/> (for tests).</summary>
    internal static void Write(HDocument document, string outputPath, DocumentLimits limits, CancellationToken cancellationToken = default)
    {
        Nesting.Limit(document); // the writer recurses per table and text box level
        var writer = new UyapWriter(document, limits, cancellationToken);
        using var elements = WriterTempFile.Create(outputPath, out var elementsPath);
        try
        {
            writer._xml = new StreamWriter(elements, Utf8, 1 << 16, leaveOpen: true);
            var page = Nesting.OnLargeStack(() => writer.Elements());
            writer._xml.Flush();
            // "]]>" can't be in a CDATA block: the block ends after "]]" and a new one starts with ">".
            var text = writer._text.ToString().Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal);
            var properties = Properties(page);
            var size = Utf8.GetByteCount(Prologue) + Utf8.GetByteCount(text) + 64 + Utf8.GetByteCount(properties) + elements.Length + Utf8.GetByteCount(Styles);
            if (size > limits.MaxPartBytes)
                throw TooLarge(limits);
            WriterTempFile.Write(outputPath, file =>
            {
                Package(file, text, properties, elements, CompressionLevel.Optimal, cancellationToken);
                // A part so repetitive that it unpacks to more than the readers' inflate ratio allows is stored as it is.
                if (size > limits.InflateRatioFloor && (file.Length - 1024) * limits.MaxInflateRatio < size)
                {
                    file.SetLength(0);
                    Package(file, text, properties, elements, CompressionLevel.NoCompression, cancellationToken);
                }
            });
        }
        finally
        {
            elements.Dispose();
            WriterTempFile.Discard(elementsPath);
        }
    }

    private static ConversionRefusedException TooLarge(DocumentLimits limits) =>
        DocumentLimits.TooLarge($"The UYAP document's content.xml would be larger than {limits.MaxPartBytes / (1024 * 1024)} MB.");

    /// <summary>The ZIP with its one part: the text (ready for its CDATA block), the page, the elements written before, the styles.</summary>
    private static void Package(Stream file, string text, string properties, FileStream elements, CompressionLevel level, CancellationToken token)
    {
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
        using var part = zip.CreateEntry("content.xml", level).Open();
        using (var writer = new StreamWriter(part, Utf8, 1 << 16, leaveOpen: true))
        {
            writer.Write(Prologue);
            writer.Write(text);
            writer.Write("]]></content>");
            writer.Write(properties);
            writer.Write("\n<elements resolver=\"hvl-default\" >\n");
        }
        token.ThrowIfCancellationRequested();
        elements.Position = 0;
        elements.CopyTo(part);
        using (var writer = new StreamWriter(part, Utf8, 1 << 12, leaveOpen: true))
        {
            writer.Write("\n</elements>\n");
            writer.Write(Styles);
        }
    }

    /// <summary>The page: always A4 (turned for landscape); margins, the header's and footer's distances in points.</summary>
    private static string Properties(HPage page)
    {
        var landscape = page.Landscape;
        return $"<properties><pageFormat mediaSizeName=\"1\" leftMargin=\"{Points(page.Left + page.Gutter)}\" rightMargin=\"{Points(page.Right)}\" "
               + $"topMargin=\"{Points(page.Top + page.Header)}\" bottomMargin=\"{Points(page.Bottom + page.Footer)}\" paperOrientation=\"{(landscape ? 0 : 1)}\" "
               + $"headerFOffset=\"{Points(page.Top)}\" footerFOffset=\"{Points(page.Bottom)}\" /></properties>";
    }

    // ───────────────────────── Output ─────────────────────────

    /// <summary>XML into the elements file, counted against the part's limit.</summary>
    private void Xml(string xml)
    {
        _xmlBytes += Utf8.GetByteCount(xml);
        if (_xmlBytes + _textBytes > _limits.MaxPartBytes)
            throw TooLarge(_limits);
        _xml.Write(xml);
    }

    /// <summary>Counts an element against the readers' element limit.</summary>
    private void Element(int count = 1)
    {
        _elements += count;
        if (_elements > _limits.MaxXmlElements)
            throw DocumentLimits.Exceeded($"The UYAP document would have more than {_limits.MaxXmlElements} XML elements.");
    }

    private void CountParagraph()
    {
        if (++_paragraphs > _limits.MaxParagraphs)
            throw DocumentLimits.Exceeded($"The UYAP document would have more than {_limits.MaxParagraphs} paragraphs.");
        if (_paragraphs % 256 == 0)
            _token.ThrowIfCancellationRequested();
    }

    private void CountObject()
    {
        if (++_objects > _limits.MaxObjects)
            throw DocumentLimits.Exceeded($"The UYAP document would have more than {_limits.MaxObjects} tables and pictures.");
    }

    /// <summary>Appends <paramref name="text"/> (made safe for XML) to the text; returns its offset and length there.</summary>
    private (int Start, int Length) Append(string text)
    {
        var start = _text.Length;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                _text.Append(c).Append(text[++i]);
                _textBytes += 4;
                continue;
            }
            if (char.IsSurrogate(c))
                c = '\uFFFD';
            else if (c is < ' ' and not ('\t' or '\n') or '\uFFFE' or '\uFFFF')
                continue;
            _text.Append(c);
            _textBytes += c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
        }
        if (_xmlBytes + _textBytes > _limits.MaxPartBytes)
            throw TooLarge(_limits);
        return (start, _text.Length - start);
    }

    /// <summary>An attribute value: XML's special characters escaped, characters it can't hold left out.</summary>
    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                case '"':
                    sb.Append("&quot;");
                    break;
                default:
                    if (c >= ' ' && !char.IsSurrogate(c) && c is not ('\uFFFE' or '\uFFFF'))
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>HWPUNIT as points the way the editor writes them ("42.525", "47.0").</summary>
    private static string Points(double hwpUnits) => (hwpUnits / 100).ToString("0.0##", CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.0###", CultureInfo.InvariantCulture);

    /// <summary>"#RRGGBB" as Java's signed ARGB integer, opaque.</summary>
    internal static string JavaColor(string color)
    {
        var rgb = color.Length == 7 && int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : 0;
        return unchecked((int)(0xFF000000u | (uint)rgb)).ToString(CultureInfo.InvariantCulture);
    }

    // ───────────────────────── Document ─────────────────────────

    /// <summary>The elements: header, body of every section, notes, footer. Returns the page of the file.</summary>
    private HPage Elements()
    {
        var first = _document.Sections.FirstOrDefault();
        var page = first?.Page ?? DefaultPage;
        _width = page.TextWidth;

        if (first is not null && Pick(first.Headers) is { } header)
            Story("header", header.Blocks);
        foreach (var section in _document.Sections)
        {
            if (section != first && section.Blocks.Count > 0 && !_firstBlock)
                PageBreak();
            Blocks(section.Blocks, 0);
            Notes();
        }
        if (_firstBlock)
            Paragraph(new HParagraph(), 0); // the editor wants a paragraph
        if (first is not null && Pick(first.Footers) is { } footer)
            Story("footer", footer.Blocks);
        // The text ends with one "\n" more than the elements cover, as the editor's files do (the end of its document).
        Append("\n");
        return page;
    }

    /// <summary>The header or footer UYAP's one band holds: the both-pages one, else the odd one, else the first.</summary>
    private static HHeaderFooter? Pick(List<HHeaderFooter> parts)
    {
        var filled = parts.Where(p => p.Blocks.Count > 0).ToList();
        return filled.FirstOrDefault(p => p.Pages == HPageType.Both) ?? filled.FirstOrDefault(p => p.Pages == HPageType.Odd) ?? filled.FirstOrDefault();
    }

    private void Story(string name, List<HBlock> blocks)
    {
        Element();
        Xml($"<{name} background=\"-1\" foreground=\"-16777216\" >");
        var first = _firstBlock;
        _firstBlock = true;
        Blocks(blocks, 1);
        Notes();
        _firstBlock = first;
        Xml($"</{name}>");
    }

    /// <summary>A page break: the editor's page-break element with its own (empty) paragraph.</summary>
    private void PageBreak()
    {
        CountParagraph();
        Element(3);
        var (start, length) = Append("\n");
        Xml($"<page-break><paragraph><content startOffset=\"{start}\" length=\"{length}\" /></paragraph></page-break>");
    }

    /// <param name="depth">0 for the body, 1 in the header and footer and more in table cells: only the body breaks pages.</param>
    private void Blocks(List<HBlock> blocks, int depth)
    {
        foreach (var block in blocks)
        {
            _token.ThrowIfCancellationRequested();
            switch (block)
            {
                case HParagraph paragraph:
                    if (paragraph.PageBreakBefore && depth == 0 && !_firstBlock)
                        PageBreak();
                    Paragraph(paragraph, depth);
                    break;
                case HTable table when table.Rows.Count > 0:
                    if (table.PageBreakBefore && depth == 0 && !_firstBlock)
                        PageBreak();
                    if (table.Caption.Count > 0)
                        Blocks(table.Caption, depth);
                    Table(table, depth);
                    break;
            }
            _firstBlock = false;
        }
    }

    /// <summary>The notes of the section (or header, footer) just written: "n text" paragraphs, n raised.</summary>
    private void Notes()
    {
        var notes = _notes.ToList();
        _notes.Clear();
        foreach (var (number, note) in notes)
        {
            var blocks = note.Blocks.Count > 0 ? note.Blocks : [new HParagraph()];
            for (var i = 0; i < blocks.Count; i++)
            {
                if (i == 0 && blocks[i] is HParagraph paragraph)
                {
                    var copy = new HParagraph { Format = paragraph.Format, MarkFormat = paragraph.MarkFormat };
                    var format = paragraph.Inlines.OfType<HText>().FirstOrDefault()?.Format ?? paragraph.MarkFormat;
                    copy.Inlines.Add(new HText(number.ToString(CultureInfo.InvariantCulture), format with { Superscript = true }));
                    copy.Inlines.Add(new HText(" ", format));
                    copy.Inlines.AddRange(paragraph.Inlines);
                    Paragraph(copy, 1);
                }
                else
                {
                    Blocks([blocks[i]], 1);
                }
            }
        }
    }

    // ───────────────────────── Paragraphs ─────────────────────────

    /// <summary>A piece of a paragraph: text with its run attributes, or a picture.</summary>
    private readonly record struct Piece(string? Text, string Attributes, HImage? Image);

    private void Paragraph(HParagraph paragraph, int depth)
    {
        CountParagraph();
        var heading = paragraph.HeadingLevel is >= 1 and <= 9 ? paragraph.HeadingLevel : 0;
        var pieces = new List<Piece>();
        var formats = new List<HCharFormat>();
        var boxes = new List<HTextBox>();
        Inlines(paragraph.Inlines, pieces, formats, boxes, heading);
        var mark = Heading(paragraph.MarkFormat, heading);
        if (formats.Count == 0)
            formats.Add(mark);

        Element();
        var sb = new StringBuilder("<paragraph");
        ParagraphAttributes(sb, paragraph, formats, heading);
        sb.Append('>');
        Xml(sb.ToString());

        // Text runs alike are joined; the "\n" ending the paragraph goes in the last one when it is formatted like the mark.
        var markAttributes = RunAttributes(mark, "");
        string? runText = null;
        var runAttributes = "";
        void Flush(bool end)
        {
            if (runText is null)
                return;
            var (start, length) = Append(runText);
            runText = null;
            if (length == 0 && !end)
                return;
            Element();
            Xml($"<content{runAttributes} startOffset=\"{start}\" length=\"{length}\" />");
        }
        foreach (var piece in pieces)
        {
            if (piece.Image is { } image)
            {
                Flush(end: false);
                Picture(image);
                continue;
            }
            if (runText is not null && runAttributes == piece.Attributes)
            {
                runText += piece.Text;
                continue;
            }
            Flush(end: false);
            (runText, runAttributes) = (piece.Text!, piece.Attributes);
        }
        if (runText is not null && runAttributes == markAttributes)
        {
            runText += "\n";
            Flush(end: true);
        }
        else
        {
            Flush(end: false);
            (runText, runAttributes) = ("\n", markAttributes);
            Flush(end: true);
        }
        Xml("</paragraph>");

        // Text boxes have no place in UYAP: their paragraphs follow the one they were in.
        foreach (var box in boxes)
            Blocks(box.Blocks, Math.Max(depth, 1));
    }

    /// <summary>A heading's runs are bold and of the heading's size where they don't say otherwise.</summary>
    private static HCharFormat Heading(HCharFormat format, int heading) =>
        heading == 0 ? format : format with { Bold = format.Bold ?? true, Size = format.Size ?? HeadingSizes[heading - 1] * 100 };

    private void Inlines(IEnumerable<HInline> inlines, List<Piece> pieces, List<HCharFormat> formats, List<HTextBox> boxes, int heading)
    {
        void Text(string text, HCharFormat format)
        {
            if (text.Length == 0)
                return;
            format = Heading(format, heading);
            formats.Add(format);
            pieces.Add(new Piece(text, RunAttributes(format, text), null));
        }
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HText text:
                    Text(text.Text, text.Format);
                    break;
                case HTab tab:
                    Text("\t", tab.Format);
                    break;
                case HLineBreak lineBreak:
                    // Inside the paragraph's runs the editor's text component breaks the line there; Filee reads a line break.
                    Text("\n", lineBreak.Format);
                    break;
                case HField field when field.Stored is { Length: > 0 } stored:
                    Text(stored, field.Format);
                    break;
                case HLink link:
                    Inlines(link.Content, pieces, formats, boxes, heading);
                    break;
                case HNote note:
                    {
                        var number = ++_noteNumber;
                        _notes.Add((number, note));
                        var format = note.Format ?? formats.LastOrDefault();
                        Text(number.ToString(CultureInfo.InvariantCulture), format with { Superscript = true, Subscript = null });
                        break;
                    }
                case HImage image:
                    pieces.Add(new Piece(null, "", image));
                    break;
                case HTextBox box:
                    boxes.Add(box);
                    break;
            }
        }
    }

    /// <summary>A run's attributes: what differs from hvl-default (Times New Roman 12 pt, black, plain).</summary>
    private static string RunAttributes(HCharFormat f, string text)
    {
        var sb = new StringBuilder();
        if (f.Bold == true)
            sb.Append(" bold=\"true\"");
        if (f.Italic == true)
            sb.Append(" italic=\"true\"");
        if (f.Underline == true)
            sb.Append(" underline=\"true\"");
        if (f.Strike == true)
            sb.Append(" strikethrough=\"true\"");
        if (f.Superscript == true)
            sb.Append(" superscript=\"true\"");
        else if (f.Subscript == true)
            sb.Append(" subscript=\"true\"");
        if (Family(f, text) is { Length: > 0 } family)
            sb.Append(" family=\"").Append(Escape(family)).Append('"');
        if (f.Size is > 0 and var size && size != 1200)
            sb.Append(" size=\"").Append((Math.Clamp(size, 100, 99_900) / 100.0).ToString("0.##", CultureInfo.InvariantCulture)).Append('"');
        if (f.Color is { Length: 7 } color && color != "#000000")
            sb.Append(" foreground=\"").Append(JavaColor(color)).Append('"');
        if (f.Shade is { Length: 7 } shade && shade != "#FFFFFF")
            sb.Append(" background=\"").Append(JavaColor(shade)).Append('"');
        return sb.ToString();
    }

    /// <summary>The font of a run: its East Asian one for text with Hangul, kana or Han characters, else its Latin one.</summary>
    private static string? Family(HCharFormat f, string text)
    {
        if (f.EastAsianFont is { } eastAsian && text.Any(IsEastAsian))
            return eastAsian;
        return f.Font ?? f.EastAsianFont;
    }

    private static bool IsEastAsian(char c) =>
        c is (>= '\u1100' and <= '\u11FF') or (>= '\u3040' and <= '\u30FF') or (>= '\u3130' and <= '\u318F')
            or (>= '\u3400' and <= '\u9FFF') or (>= '\uAC00' and <= '\uD7AF') or (>= '\uF900' and <= '\uFAFF');

    private void ParagraphAttributes(StringBuilder sb, HParagraph paragraph, List<HCharFormat> formats, int heading)
    {
        var f = paragraph.Format;
        // Right to left, the model's Left and Right alignment name the start and the end; UYAP's are the sides.
        var align = f.Align ?? HAlign.Left;
        if (f.RightToLeft == true && align is HAlign.Left or HAlign.Right)
            align = align == HAlign.Left ? HAlign.Right : HAlign.Left;
        sb.Append(CultureInfo.InvariantCulture, $" Alignment=\"{align switch { HAlign.Center => 1, HAlign.Right => 2, HAlign.Justify or HAlign.Distribute => 3, _ => 0 }}\"");

        var (left, firstLine) = (f.Left, f.FirstLine);
        if (paragraph.List is { } list && left is null)
        {
            // A list item without its own indent takes its level's, its number hanging.
            left = 1800 * (Math.Clamp(list.Level, 0, 8) + 1);
            firstLine ??= list.Numbered ? -1800 : 0;
        }
        // Indents of 0 are left out, as the editor does.
        if (left is { } start && start != 0)
            sb.Append($" LeftIndent=\"{Points(start)}\"");
        if (f.Right is { } end && end != 0)
            sb.Append($" RightIndent=\"{Points(end)}\"");
        if (firstLine is { } first && first != 0)
            sb.Append($" FirstLineIndent=\"{Points(first)}\"");
        var before = f.Before ?? (heading is > 0 and <= 2 ? 1200 : heading > 0 ? 800 : 0);
        if (before > 0)
            sb.Append($" SpaceAbove=\"{Points(before)}\"");
        if (f.After is > 0 and var after)
            sb.Append($" SpaceBelow=\"{Points(after)}\"");
        if (LineSpacing(paragraph, formats) is > 0.005 and var spacing)
            sb.Append(" LineSpacing=\"").Append(Number(Math.Round(spacing, 4))).Append('"');

        var stops = (f.Tabs ?? []).Where(t => t.Position > 0).OrderBy(t => t.Position).Take(64).ToList();
        if (stops.Count > 0)
        {
            sb.Append(" TabSet=\"");
            sb.AppendJoin(',', stops.Select(t =>
                $"{Points(t.Position)}:{t.Kind switch { HTabKind.Right => 1, HTabKind.Center => 2, HTabKind.Decimal => 4, _ => 0 }}:"
                + (t.Leader switch { "DOT" => 1, "DASH" => 2, "SOLID" => 3, _ => 0 }).ToString(CultureInfo.InvariantCulture)));
            sb.Append('"');
        }
        else if (firstLine < 0)
        {
            // As the editor writes a hanging paragraph: a stop at the indent, so "1.<tab>" lines up with the lines under it.
            sb.Append(" TabSet=\"0.0:0:0\"");
        }

        if (paragraph.List is { Numbered: true } item && item.Numbering.Levels.Count > 0)
        {
            var level = item.Numbering.Levels[Math.Min(item.Level, item.Numbering.Levels.Count - 1)];
            if (!_listIds.TryGetValue(item.Numbering, out var id))
                _listIds[item.Numbering] = id = _listIds.Count + 1;
            if (level.Bullet)
            {
                var type = UyapReader.BulletTypes.FirstOrDefault(b => b.Bullet == level.Text.Trim()).Type ?? BulletType(level.Text);
                sb.Append($" Bulleted=\"true\" BulletType=\"{type}\"");
            }
            else
            {
                var close = level.Text.TrimEnd().EndsWith(')') ? ")" : ".";
                var format = level.Format is "LATIN_SMALL" or "LATIN_CAPITAL" or "ROMAN_SMALL" or "ROMAN_CAPITAL" ? level.Format : "DIGIT";
                var type = UyapReader.NumberTypes.First(t => t.Format == format && t.After == close).Type;
                sb.Append($" Numbered=\"true\" NumberType=\"{type}\"");
            }
            sb.Append(CultureInfo.InvariantCulture, $" ListId=\"{id}\" ListLevel=\"{Math.Clamp(item.Level, 0, 8)}\"");
        }
    }

    /// <summary>The bullet type closest to a bullet character the editor has no type for.</summary>
    private static string BulletType(string bullet) => bullet.Trim() switch
    {
        "■" or "▫" => "BULLET_TYPE_RECTANGLE",
        "➤" or "▶" or "→" or "⇒" or "➔" => "BULLET_TYPE_ARROW",
        "◇" or "♦" => "BULLET_TYPE_DIAMOND",
        "▲" or "▸" or "‣" => "BULLET_TYPE_TRIANGLE",
        "☐" or "❑" => "BULLET_TYPE_RECTANGLE_D",
        _ => "BULLET_TYPE_ELLIPSE",
    };

    /// <summary>
    /// The extra space between lines, in lines (Swing's LineSpacing: 0.5 for one and a half lines): from the source's
    /// own multiple where it has one, else from the model's line spacing, a percentage in 한글's terms (130 = a line,
    /// as the DOCX writer has it) or a height against the line Java draws for these runs (as UyapReader reads it).
    /// </summary>
    private static double LineSpacing(HParagraph paragraph, List<HCharFormat> formats)
    {
        if (paragraph.Format.Layout?.LineHeight is { Rule: HLineHeightRule.Multiple } multiple)
            return Math.Clamp(multiple.Value / 240.0 - 1, 0, 10);
        if (paragraph.Format.LineSpacing is not { } line)
            return 0;
        if (line.Kind == HLineSpacingKind.Percent)
            return Math.Clamp(line.Value / 130.0 - 1, 0, 10);
        var resolved = formats.Select(f => f with { Font = f.Font ?? "Times New Roman", Size = f.Size ?? 1200 }).ToList();
        var height = UyapReader.JavaLineHeight(resolved) ?? (int)Math.Round(resolved.Max(f => f.Size!.Value) * 1.2);
        return height > 0 ? Math.Clamp((double)line.Value / height - 1, 0, 10) : 0;
    }

    // ───────────────────────── Pictures ─────────────────────────

    private void Picture(HImage image)
    {
        if (_pictures >= UyapReader.MaxPictures || PictureData(image.Path) is not { } picture)
            return; // missing, unreadable or past the limits: left out rather than written broken
        _pictures++;
        CountObject();
        var (width, height) = (image.Width, image.Height);
        if (width is null || height is null)
        {
            if (width is null && height is null)
                (width, height) = ((int)(picture.Width * HwpxUnits.PerPixel), (int)(picture.Height * HwpxUnits.PerPixel));
            else if (width is not null)
                height = (int)(width.Value * (double)picture.Height / Math.Max(1, picture.Width));
            else
                width = (int)(height!.Value * (double)picture.Width / Math.Max(1, picture.Height));
        }
        var w = Math.Max(100, width!.Value);
        var h = Math.Max(100, height!.Value);
        if (w > _width)
        {
            // Pictures in the text never overflow its width.
            h = Math.Max(100, (int)((long)h * _width / w));
            w = _width;
        }
        var (start, length) = Append(ObjectCharacter.ToString());
        Element();
        Xml("<image imageData=\"");
        // Base64 in pieces, so a large picture is never one large string.
        const int Chunk = 3 * 16384;
        for (var i = 0; i < picture.Bytes.Length; i += Chunk)
        {
            _token.ThrowIfCancellationRequested();
            Xml(Convert.ToBase64String(picture.Bytes, i, Math.Min(Chunk, picture.Bytes.Length - i)));
        }
        Xml($"\" width=\"{Points(w)}\" height=\"{Points(h)}\" startOffset=\"{start}\" length=\"{length}\" />");
    }

    private (byte[] Bytes, int Width, int Height)? PictureData(string path)
    {
        if (_pictureData.TryGetValue(path, out var known))
            return known;
        return _pictureData[path] = ReadPicture(path);
    }

    /// <summary>
    /// A picture as PNG or JPEG bytes with its pixel size; null when it is missing, larger than the reader takes, claims
    /// more pixels than the limits allow, or can't be decoded.
    /// </summary>
    private (byte[] Bytes, int Width, int Height)? ReadPicture(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > Math.Min(_limits.MaxPartBytes, UyapReader.MaxPictureBytes))
            return null;
        var size = PictureSize.Read(path);
        if (size is { } claimed && (long)claimed.Width * claimed.Height > _limits.MaxPicturePixels)
            return null;
        var bytes = File.ReadAllBytes(path);
        if (size is { Width: > 0, Height: > 0 } pixels && bytes is [0x89, (byte)'P', (byte)'N', (byte)'G', ..] or [0xFF, 0xD8, 0xFF, ..])
            return (bytes, pixels.Width, pixels.Height);
        // GIF, BMP, WebP, TIFF, metafiles ... → PNG, which the editor shows.
        try
        {
            var info = new MagickImageInfo(path);
            if ((long)info.Width * info.Height > _limits.MaxPicturePixels)
                return null;
            using var image = new MagickImage(path);
            DdsChannelOrder.Correct(path, image);
            var png = image.ToByteArray(MagickFormat.Png);
            return png.Length > UyapReader.MaxPictureBytes ? null : (png, (int)image.Width, (int)image.Height);
        }
        catch (MagickException)
        {
            return null;
        }
    }

    // ───────────────────────── Tables ─────────────────────────

    /// <summary>A cell's sides in borderSpec: 1 top, 2 right, 4 bottom, 8 left.</summary>
    private const int Top = 1, Right = 2, Bottom = 4, Left = 8;

    private void Table(HTable table, int depth)
    {
        CountObject();
        var (placed, columns) = HwpxWriter.PlaceCells(table);
        columns = Math.Max(1, columns);
        var widths = HwpxWriter.ColumnWidths(table, columns, Math.Max(2000, _width - table.Indent));
        Element();
        Xml($"<table tableName=\"Sabit\" columnCount=\"{columns}\" columnSpans=\"{string.Join(',', widths.Select(w => Points(Math.Max(100, w))))}\" border=\"borderCell\" >");

        var tableBorders = table.Borders ?? HBorders.Grid;
        // Per column: the cell spanning rows down over it, its first column and its last row.
        var spans = new (HCell Cell, int Column, int LastRow)?[columns];
        var outerWidth = _width;
        for (var r = 0; r < placed.Count; r++)
        {
            _token.ThrowIfCancellationRequested();
            var row = table.Rows[r];
            Element();
            var height = row.Height is > 0 and var h ? $" height=\"{Points(h)}\"" : "";
            Xml($"<row rowName=\"row{r + 1}\" rowType=\"{(row.Header ? "headerRow" : "dataRow")}\"{height} >");
            var cells = placed[r].OrderBy(p => p.Column).ToList();
            var next = 0;
            var column = 0;
            while (column < columns)
            {
                while (next < cells.Count && cells[next].Column < column)
                    next++; // PlaceCells never overlaps cells; a cell under another would be lost, not misplace the row
                if (next < cells.Count && cells[next].Column == column)
                {
                    var (cell, _) = cells[next++];
                    var colSpan = Math.Clamp(cell.ColSpan, 1, columns - column);
                    var sides = Sides(cell.Borders ?? tableBorders);
                    if (cell.RowSpan > 1)
                    {
                        // UYAP merges columns only: the rows under it get cells of their own without the borders between.
                        for (var c = column; c < column + colSpan; c++)
                            spans[c] = (cell, column, r + cell.RowSpan - 1);
                        sides &= ~Bottom;
                    }
                    _width = Math.Max(1000, widths.Skip(column).Take(colSpan).Sum() - 1080);
                    Cell(cell, colSpan, sides, cell.Borders ?? tableBorders, cell.Blocks, depth);
                    column += colSpan;
                }
                else if (spans[column] is { } span && span.Column == column && span.LastRow >= r)
                {
                    var colSpan = Math.Clamp(span.Cell.ColSpan, 1, columns - column);
                    var sides = Sides(span.Cell.Borders ?? tableBorders) & ~Top;
                    if (span.LastRow > r)
                        sides &= ~Bottom;
                    Cell(span.Cell, colSpan, sides, span.Cell.Borders ?? tableBorders, [], depth);
                    column += colSpan;
                }
                else
                {
                    // A place no cell takes: an empty cell keeps the columns in line.
                    Cell(null, 1, Sides(tableBorders), tableBorders, [], depth);
                    column++;
                }
            }
            Xml("</row>");
        }
        _width = outerWidth;
        Xml("</table>");
    }

    private static int Sides(HBorders borders) =>
        (borders.Top.Style != HBorderStyle.None ? Top : 0) | (borders.Right.Style != HBorderStyle.None ? Right : 0)
        | (borders.Bottom.Style != HBorderStyle.None ? Bottom : 0) | (borders.Left.Style != HBorderStyle.None ? Left : 0);

    /// <param name="cell">The model cell whose fill and alignment the cell takes; null for a filler.</param>
    private void Cell(HCell? cell, int colSpan, int sides, HBorders borders, List<HBlock> blocks, int depth)
    {
        Element();
        var sb = new StringBuilder($"<cell colspan=\"{colSpan}\"");
        sb.Append(" align=\"").Append(cell?.VerticalAlign switch { HVerticalAlign.Center => "vcenter", HVerticalAlign.Bottom => "bottom", _ => "top" }).Append('"');
        if (cell?.Fill is { Length: 7 } fill)
            sb.Append(" fillColor=\"").Append(JavaColor(fill)).Append('"');
        if (sides == 0)
        {
            sb.Append(" border=\"borderNone\"");
        }
        else
        {
            var line = new[] { borders.Top, borders.Right, borders.Bottom, borders.Left }.First(b => b.Style != HBorderStyle.None);
            var style = line.Style switch
            {
                HBorderStyle.Dot => "borderStyle-dotted",
                HBorderStyle.Dash or HBorderStyle.DashDot => "borderStyle-dashed",
                HBorderStyle.Double => "borderStyle-double",
                _ => "borderStyle-solid",
            };
            sb.Append($" border=\"borderCell\" borderStyle=\"{style}\" borderWidth=\"{Number(Math.Round(Math.Clamp(line.WidthMm, 0.05, 5) * 72 / 25.4, 2))}\"")
              .Append($" borderColor=\"{JavaColor(line.Color)}\" borderSpec=\"{sides}\"");
        }
        sb.Append(" >");
        Xml(sb.ToString());
        var first = _firstBlock;
        if (blocks.Count == 0)
            Paragraph(new HParagraph { MarkFormat = cell is null ? default : FirstMark(cell) }, depth + 1);
        else
            Blocks(blocks, depth + 1);
        _firstBlock = first;
        Xml("</cell>");
    }

    /// <summary>The mark of a cell's first paragraph, for the empty cells under a cell spanning rows.</summary>
    private static HCharFormat FirstMark(HCell cell) => cell.Blocks.OfType<HParagraph>().FirstOrDefault()?.MarkFormat ?? default;
}
