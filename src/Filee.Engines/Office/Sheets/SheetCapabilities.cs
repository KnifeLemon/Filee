// What the sheet printer doesn't draw, named before a workbook is printed: SheetPrintConverter hands such a workbook to
// LibreOffice when it is installed, and otherwise prints it and says what was left out.
//
// Only what prints is looked at: visible sheets, and drawings not set to stay off the printout.
//  * XLSX: a visible chart sheet ("charts"); in each visible worksheet's drawing what XlsxDrawing leaves out (charts,
//    SmartArt, embedded objects, ink, text in shapes, freeform shapes, ...); form controls (ctrlProp parts, or legacy
//    VML controls); embedded OLE objects. Comments are not printed unless the page setup asks for them, and are
//    left out of the check.
//  * XLS: the reader takes no drawing at all, so every object of a visible sheet is named by its OBJ record's type
//    (charts, pictures, text boxes, shapes, form controls; comments aside), and a visible chart sheet.
//  * ODS: the reader takes no drawing either: pictures, charts and other embedded objects, shapes, text boxes and
//    form controls of a visible table.
// The parts are read within DocumentLimits; a file that can't be read gets no answer here (null), and the printer
// then refuses it with the reader's own reason.

using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Office.Ooxml;

namespace Filee.Engines.Office.Sheets;

internal static partial class SheetCapabilities
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private const string OdfOffice = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private const string OdfTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string OdfStyle = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private const string OdfDraw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private const string XLink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";

    /// <summary>Bytes of a legacy VML drawing looked through at most (they hold comments and controls: kilobytes).</summary>
    private const long MaxVmlBytes = 16L * 1024 * 1024;

    /// <summary>The first thing in the workbook the sheet printer doesn't draw; null when it draws everything (or can't read the file).</summary>
    /// <param name="format">Format id: xlsx, xlsm, xltx, xls, xlt or ods.</param>
    public static string? FirstUnsupported(string path, string format)
    {
        try
        {
            return format switch
            {
                "xlsx" or "xlsm" or "xltx" => Xlsx(path),
                // Web apps often save XLSX with an .xls name; the XLSX reader reads those (XlsReader).
                "xls" or "xlt" => IsZip(path) ? Xlsx(path) : XlsLayout.FirstObject(path),
                "ods" => Ods(path),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or XmlException or ConversionRefusedException)
        {
            // A damaged or hostile file: the conversion stops with the reader's own message.
            return null;
        }
    }

    private static bool IsZip(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[2];
        return stream.ReadAtLeast(head, 2, throwOnEndOfStream: false) == 2 && head[0] == (byte)'P' && head[1] == (byte)'K';
    }

    // ───────────────────────── XLSX ─────────────────────────

    private static string? Xlsx(string path)
    {
        using var package = new OpcPackage(path, DocumentLimits.Default);
        var workbookPath = package.MainPartPath ?? "xl/workbook.xml";
        if (package.Part(workbookPath)?.Root is not { } workbook)
            return null;
        var theme = package.TargetOfType(workbookPath, "/theme") is { } themePath ? package.Part(themePath)?.Root : null;
        var relationships = package.Relationships(workbookPath);
        foreach (var sheet in workbook.Element(S + "sheets")?.Elements(S + "sheet") ?? [])
        {
            if ((string?)sheet.Attribute("state") is "hidden" or "veryHidden"
                || (string?)sheet.Attribute(R + "id") is not { } id || !relationships.TryGetValue(id, out var relationship) || relationship.External)
                continue;
            if (relationship.Type.EndsWith("/chartsheet", StringComparison.Ordinal))
                return "charts";
            if (!relationship.Type.EndsWith("/worksheet", StringComparison.Ordinal))
                continue;
            // The sheet's parts tell what it holds, without reading its cells.
            foreach (var part in package.Relationships(relationship.Target).Values.Where(r => !r.External))
            {
                var feature = part.Type switch
                {
                    var type when type.EndsWith("/drawing", StringComparison.Ordinal) => XlsxDrawing.Read(package, part.Target, theme, read: false).FirstLeftOut,
                    var type when type.EndsWith("/ctrlProp", StringComparison.Ordinal) => "form controls",
                    var type when type.EndsWith("/oleObject", StringComparison.Ordinal) || type.EndsWith("/package", StringComparison.Ordinal) => "embedded objects",
                    var type when type.EndsWith("/vmlDrawing", StringComparison.Ordinal) => Vml(package, part.Target),
                    _ => null,
                };
                if (feature is not null)
                    return feature;
            }
        }
        return null;
    }

    /// <summary>
    /// Form controls of a legacy VML drawing (x:ClientData of a type other than a comment's note or a picture's).
    /// VML is often not well-formed XML, so its text is searched rather than parsed.
    /// </summary>
    private static string? Vml(OpcPackage package, string part)
    {
        if (package.Bytes(part, MaxVmlBytes) is not { } bytes)
            return null;
        foreach (Match match in ObjectType().Matches(Encoding.UTF8.GetString(bytes)))
        {
            if (match.Groups[1].Value is not ("Note" or "Pict"))
                return "form controls";
        }
        return null;
    }

    [GeneratedRegex("ObjectType\\s*=\\s*[\"']([A-Za-z]+)[\"']")]
    private static partial Regex ObjectType();

    // ───────────────────────── ODS ─────────────────────────

    private static string? Ods(string path)
    {
        using var zip = DocumentPackage.Open(path, DocumentLimits.Default, () => new InvalidDataException("The file is not an OpenDocument spreadsheet (no ZIP)."));
        if (zip.GetEntry("content.xml") is not { } content)
            return null;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = DocumentLimits.Default.MaxPartBytes,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        using var stream = zip.Open(content);
        using var reader = XmlReader.Create(stream, settings);
        // Table styles that hide their table (table:display="false"), from the automatic styles before the body.
        var hiddenStyles = new HashSet<string>(StringComparer.Ordinal);
        string? style = null;
        var body = false;
        // After skipping a hidden table the reader is already on the next node.
        var skipped = false;
        while (skipped ? !reader.EOF : reader.Read())
        {
            skipped = false;
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            switch (reader.NamespaceURI, reader.LocalName)
            {
                case (OdfStyle, "style") when !body:
                    style = reader.GetAttribute("family", OdfStyle) == "table" ? reader.GetAttribute("name", OdfStyle) : null;
                    break;
                case (OdfStyle, "table-properties") when !body:
                    if (style is not null && reader.GetAttribute("display", OdfTable) == "false")
                        hiddenStyles.Add(style);
                    break;
                case (OdfOffice, "body"):
                    body = true;
                    break;
                case (OdfTable, "table") when body:
                    if (reader.GetAttribute("style-name", OdfTable) is { } name && hiddenStyles.Contains(name))
                    {
                        reader.Skip();
                        skipped = true;
                    }
                    break;
                case (OdfDraw, var element) when body:
                    if (OdsDrawing(zip, element, reader) is { } feature)
                        return feature;
                    break;
            }
        }
        return null;
    }

    /// <summary>What an element of the drawing namespace in a visible table is, named; null for a frame or another container.</summary>
    private static string? OdsDrawing(DocumentPackage zip, string element, XmlReader reader) => element switch
    {
        // An object's frame also holds its replacement picture, after the object: the object is named first.
        "object" or "object-ole" => IsChart(zip, reader.GetAttribute("href", XLink)) ? "charts" : "embedded objects",
        "image" => "pictures",
        "text-box" => "text boxes",
        "control" => "form controls",
        "plugin" or "applet" or "floating-frame" => "embedded objects",
        "rect" or "ellipse" or "circle" or "line" or "polyline" or "polygon" or "regular-polygon" or "path" or "custom-shape" or "connector"
            or "measure" or "caption" or "g" => "shapes",
        _ => null,
    };

    /// <summary>Whether the embedded object at <paramref name="href"/> ("./Object 1") is a chart, from the package manifest.</summary>
    private static bool IsChart(DocumentPackage zip, string? href)
    {
        if (href is null || zip.GetEntry("META-INF/manifest.xml") is not { } entry)
            return false;
        var folder = href.StartsWith("./", StringComparison.Ordinal) ? href[2..] : href;
        folder = folder.TrimEnd('/') + "/";
        return zip.LoadXml(entry).Root?.Elements(Manifest + "file-entry")
            .Any(e => (string?)e.Attribute(Manifest + "full-path") == folder
                && ((string?)e.Attribute(Manifest + "media-type"))?.EndsWith(".chart", StringComparison.Ordinal) == true) == true;
    }
}
