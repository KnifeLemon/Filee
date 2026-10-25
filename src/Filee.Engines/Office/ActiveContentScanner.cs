// Finds active content in office documents before LibreOffice opens them: macros (and whether one starts by itself),
// script libraries, ActiveX controls, links to outside content, DDE fields and embedded objects. Read-only and quick:
// it looks at package parts and records, never lays out the document.
//
//  * Office Open XML (DOCX/DOCM, XLSX/XLSM, PPTX/PPTM, …): macro parts by content type, VBA module names and their
//    source (decompressed per MS-OVBA 2.4.1) for AutoOpen / Document_Open / Workbook_Open, ActiveX parts, every
//    external relationship but plain hyperlinks (a remote template is a known way to pull in macros), DDE fields.
//  * OpenDocument: Basic modules, Python / JavaScript / BeanShell libraries, event bindings ("on load" starts a macro by
//    itself), links to outside files or addresses, DDE connections. Charts and other embedded objects are left out:
//    almost every document with a chart has one.
//  * Office 97–2003 (DOC, XLS): the VBA storage with its modules, and Excel 4.0 macro sheets (BOUNDSHEET records).
//    PowerPoint 97–2003 keeps its VBA project inside the document stream and is not checked.
//  * RTF: embedded OLE objects (\objdata) and links updated on load (\objupdate).
//
// LibreOffice's conversion mode doesn't run document macros; this check lets the user decide before a document that
// carries them is opened at all (see JobQueue.ScreenActiveContentAsync).

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Filee.Core.Conversion;
using OpenMcdf;

namespace Filee.Engines.Office;

/// <summary>Finds macros, ActiveX controls, external links, DDE fields and embedded objects in office documents.</summary>
public sealed partial class ActiveContentScanner : IActiveContentScanner
{
    /// <summary>Largest VBA project read (the whole storage is loaded into memory).</summary>
    private const long MaxVbaProject = 64L << 20;

    /// <summary>How much of an RTF file is searched for embedded objects.</summary>
    private const int MaxRtfChars = 32 << 20;

    /// <summary>Relationship types of plain hyperlinks: clicking one opens it, loading the document doesn't.</summary>
    private static readonly string[] HyperlinkTypes = ["/hyperlink"];

    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <inheritdoc />
    public ActiveContentReport? Scan(string path, string formatId)
    {
        var findings = new List<ActiveContentFinding>();
        var head = ReadHead(path, 8);
        if (head.Length >= 2 && head[0] == 'P' && head[1] == 'K')
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.GetEntry("[Content_Types].xml") is not null)
                ScanOoxml(zip, findings);
            else if (IsOpenDocument(zip))
                ScanOpenDocument(zip, findings);
        }
        else if (head.AsSpan().SequenceEqual(OleSignature))
        {
            ScanCompoundFile(path, findings);
        }
        else if (head.Length >= 5 && Encoding.ASCII.GetString(head, 0, 5) == "{\\rtf")
        {
            ScanRtf(path, findings);
        }
        var distinct = findings.Distinct().ToList();
        return distinct.Count == 0 ? null : new ActiveContentReport(path, distinct);
    }

    private static byte[] ReadHead(string path, int count)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[count];
        var read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
        return buffer[..read];
    }

    // ───────────────────────── Office Open XML ─────────────────────────

    private static void ScanOoxml(ZipArchive zip, List<ActiveContentFinding> findings)
    {
        foreach (var part in MacroParts(zip))
        {
            if (part.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase) && zip.GetEntry(part.TrimStart('/')) is { } vba)
            {
                if (vba.Length > MaxVbaProject)
                {
                    findings.Add(new(ActiveContentKind.Macro, part));
                    continue;
                }
                using var buffer = new MemoryStream();
                using (var entry = vba.Open())
                    entry.CopyTo(buffer);
                buffer.Position = 0;
                using var root = RootStorage.Open(buffer, StorageModeFlags.LeaveOpen);
                if (!AddVbaModules(root, findings))
                    findings.Add(new(ActiveContentKind.Macro, part));
            }
            else
            {
                // Excel 4.0 macro sheets and Word's VBA data: macros without a VBA project.
                findings.Add(new(ActiveContentKind.Macro, part));
            }
        }

        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name.Contains("/activeX/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                findings.Add(new(ActiveContentKind.ActiveX, Path.GetFileNameWithoutExtension(name)));
            else if (name.Contains("/embeddings/", StringComparison.OrdinalIgnoreCase))
                findings.Add(new(ActiveContentKind.EmbeddedObject, Path.GetFileName(name)));
            else if (name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                AddExternalRelationships(entry, findings);
            else if (IsTextPart(name))
                AddDdeFields(entry, findings);
        }
    }

    /// <summary>Parts whose content type is a macro type (<see cref="OoxmlConverter.MacroContentTypes"/>).</summary>
    private static List<string> MacroParts(ZipArchive zip)
    {
        var parts = new List<string>();
        using var stream = zip.GetEntry("[Content_Types].xml")!.Open();
        using var reader = XmlReader.Create(stream, SafeXml);
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            var type = reader.GetAttribute("ContentType");
            if (type is null || !OoxmlConverter.MacroContentTypes.Contains(type))
                continue;
            if (reader.LocalName == "Override" && reader.GetAttribute("PartName") is { } part)
                parts.Add(part);
            else if (reader.LocalName == "Default" && reader.GetAttribute("Extension") is { } extension)
                defaults[extension] = type;
        }
        // A Default by extension ("bin" → vbaProject) covers every part with that extension.
        parts.AddRange(zip.Entries
            .Where(e => defaults.ContainsKey(Path.GetExtension(e.FullName).TrimStart('.')))
            .Select(e => "/" + e.FullName));
        return parts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddExternalRelationships(ZipArchiveEntry rels, List<ActiveContentFinding> findings)
    {
        using var stream = rels.Open();
        using var reader = XmlReader.Create(stream, SafeXml);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship"
                || !string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                continue;
            var type = reader.GetAttribute("Type") ?? "";
            if (HyperlinkTypes.Any(h => type.EndsWith(h, StringComparison.OrdinalIgnoreCase)))
                continue;
            var shortType = type[(type.LastIndexOf('/') + 1)..];
            findings.Add(new(ActiveContentKind.ExternalLink, shortType, reader.GetAttribute("Target")));
        }
    }

    /// <summary>Document, header, footer, sheet and external-link parts: where DDE fields and links live.</summary>
    private static bool IsTextPart(string name) =>
        name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
        && (name.StartsWith("word/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("xl/externalLinks/", StringComparison.OrdinalIgnoreCase));

    private static void AddDdeFields(ZipArchiveEntry part, List<ActiveContentFinding> findings)
    {
        using var stream = part.Open();
        using var reader = XmlReader.Create(stream, SafeXml);
        var inInstruction = false;
        var formula = false;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when reader.LocalName == "ddeLink":
                    findings.Add(new(ActiveContentKind.Dde, reader.GetAttribute("ddeService") ?? "DDE",
                        reader.GetAttribute("ddeTopic")));
                    break;
                case XmlNodeType.Element when reader.LocalName == "fldSimple" && reader.GetAttribute("w:instr") is { } simple:
                    AddDdeInstruction(simple, findings);
                    break;
                case XmlNodeType.Element:
                    inInstruction = reader.LocalName == "instrText";
                    formula = reader.LocalName == "f";
                    break;
                case XmlNodeType.Text when inInstruction:
                    AddDdeInstruction(reader.Value, findings);
                    break;
                case XmlNodeType.Text when formula && DdeFormula().Match(reader.Value) is { Success: true } match:
                    findings.Add(new(ActiveContentKind.Dde, match.Groups[1].Value, match.Value));
                    break;
                case XmlNodeType.EndElement:
                    inInstruction = formula = false;
                    break;
            }
        }
    }

    private static void AddDdeInstruction(string instruction, List<ActiveContentFinding> findings)
    {
        var text = instruction.Trim();
        if (text.StartsWith("DDEAUTO", StringComparison.OrdinalIgnoreCase) || text.StartsWith("DDE ", StringComparison.OrdinalIgnoreCase))
            findings.Add(new(ActiveContentKind.Dde, text.Split(' ', 2)[0].ToUpperInvariant(), Shorten(text)));
    }

    /// <summary>An Excel formula that asks another program for data: <c>=cmd|'/c calc'!A0</c>.</summary>
    [GeneratedRegex(@"([A-Za-z0-9_.]+)\|'[^']*'!", RegexOptions.CultureInvariant)]
    private static partial Regex DdeFormula();

    // ───────────────────────── OpenDocument ─────────────────────────

    private static bool IsOpenDocument(ZipArchive zip)
    {
        if (zip.GetEntry("mimetype") is not { } mimetype)
            return zip.GetEntry("META-INF/manifest.xml") is not null && zip.GetEntry("content.xml") is not null;
        using var reader = new StreamReader(mimetype.Open(), Encoding.ASCII);
        return reader.ReadToEnd().StartsWith("application/vnd.oasis.opendocument", StringComparison.Ordinal);
    }

    private static void ScanOpenDocument(ZipArchive zip, List<ActiveContentFinding> findings)
    {
        foreach (var entry in zip.Entries)
        {
            var parts = entry.FullName.Split('/');
            // Basic/<library>/<module>.xml, next to the library's script-lb.xml.
            if (parts is ["Basic", var library, var file] && file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && !file.StartsWith("script-l", StringComparison.OrdinalIgnoreCase) && !file.StartsWith("dialog-l", StringComparison.OrdinalIgnoreCase))
                findings.Add(new(ActiveContentKind.Macro, $"{library}.{Path.GetFileNameWithoutExtension(file)}"));
            else if (parts is ["Scripts", var language, ..] && entry.Length > 0 && !entry.FullName.EndsWith('/'))
                findings.Add(new(ActiveContentKind.Script, $"{language}: {string.Join('/', parts[2..])}"));
        }
        foreach (var name in new[] { "content.xml", "styles.xml" })
        {
            if (zip.GetEntry(name) is { } part)
                ScanOpenDocumentXml(part, findings);
        }
    }

    private static void ScanOpenDocumentXml(ZipArchiveEntry part, List<ActiveContentFinding> findings)
    {
        using var stream = part.Open();
        using var reader = XmlReader.Create(stream, SafeXml);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            switch (reader.LocalName)
            {
                case "event-listener":
                    var eventName = reader.GetAttribute("event-name", "urn:oasis:names:tc:opendocument:xmlns:script:1.0") ?? "";
                    var macro = reader.GetAttribute("href", "http://www.w3.org/1999/xlink")
                                ?? reader.GetAttribute("macro-name", "urn:oasis:names:tc:opendocument:xmlns:script:1.0");
                    var onLoad = eventName.EndsWith(":load", StringComparison.OrdinalIgnoreCase)
                                 || eventName.Contains("load-finished", StringComparison.OrdinalIgnoreCase);
                    findings.Add(new(onLoad ? ActiveContentKind.AutoRunMacro : ActiveContentKind.Macro, eventName, macro));
                    break;
                case "dde-connection-decl" or "dde-link" or "dde-source":
                    findings.Add(new(ActiveContentKind.Dde, reader.GetAttribute("dde-application", "urn:oasis:names:tc:opendocument:xmlns:office:1.0") ?? reader.LocalName,
                        reader.GetAttribute("dde-topic", "urn:oasis:names:tc:opendocument:xmlns:office:1.0")));
                    break;
                default:
                    if (reader.GetAttribute("href", "http://www.w3.org/1999/xlink") is { } href && IsOutside(href)
                        && reader.LocalName is "image" or "object" or "object-ole" or "section-source" or "table-source" or "plugin" or "applet" or "frame")
                        findings.Add(new(ActiveContentKind.ExternalLink, reader.LocalName, Shorten(href)));
                    break;
            }
        }
    }

    /// <summary>
    /// An address outside the package: a web, FTP, SMB or file address, a network or drive path, or a path that leaves
    /// the package (<c>../</c>). Parts of the package (<c>Pictures/…</c>, <c>./Object 1</c>), <c>data:</c> pictures and
    /// LibreOffice's internal references are not.
    /// </summary>
    internal static bool IsOutside(string href) => OutsideAddress().IsMatch(href);

    [GeneratedRegex(@"^(?:(?:https?|ftp|file|smb|webdav|dav)s?:|\\\\|//|\.\./|[A-Za-z]:[\\/])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OutsideAddress();

    // ───────────────────────── Office 97–2003 ─────────────────────────

    private static void ScanCompoundFile(string path, List<ActiveContentFinding> findings)
    {
        using var root = RootStorage.OpenRead(path);
        // Word keeps its VBA project in "Macros", Excel in "_VBA_PROJECT_CUR".
        foreach (var name in new[] { "Macros", "_VBA_PROJECT_CUR" })
        {
            if (root.TryOpenStorage(name, out var project) && !AddVbaModules(project, findings))
                findings.Add(new(ActiveContentKind.Macro, name));
        }
        if (root.TryOpenStream("Workbook", out var workbook) || root.TryOpenStream("Book", out workbook))
        {
            using (workbook)
                AddExcel4MacroSheets(workbook, findings);
        }
    }

    /// <summary>
    /// Excel 4.0 (XLM) macro sheets: BOUNDSHEET records whose sheet type is 1. They run without any VBA project and are
    /// a common way to hide macros.
    /// </summary>
    internal static void AddExcel4MacroSheets(Stream workbook, List<ActiveContentFinding> findings)
    {
        var header = new byte[4];
        while (workbook.ReadAtLeast(header, 4, throwOnEndOfStream: false) == 4)
        {
            var type = BitConverter.ToUInt16(header, 0);
            var length = BitConverter.ToUInt16(header, 2);
            if (type == 0x0085 && length >= 8)
            {
                var data = new byte[length];
                if (workbook.ReadAtLeast(data, length, throwOnEndOfStream: false) < length)
                    break;
                if (data[5] == 0x01)
                    findings.Add(new(ActiveContentKind.Macro, "Excel 4.0 macro sheet", SheetName(data)));
            }
            else if (type == 0x000A)
            {
                break; // EOF of the workbook globals: every BOUNDSHEET comes before it.
            }
            else
            {
                workbook.Seek(length, SeekOrigin.Current);
            }
        }

        static string SheetName(byte[] data)
        {
            int count = data[6];
            var unicode = data.Length > 7 && (data[7] & 1) == 1;
            var start = 8;
            var bytes = Math.Min(unicode ? count * 2 : count, data.Length - start);
            return bytes <= 0 ? "" : (unicode ? Encoding.Unicode : Encoding.Latin1).GetString(data, start, bytes);
        }
    }

    // ───────────────────────── VBA (MS-OVBA) ─────────────────────────

    /// <summary>Procedures Office runs by itself when a document opens, is created or closes.</summary>
    [GeneratedRegex(@"\b(?:Sub|Function)\s+(AutoOpen|Auto_Open|AutoExec|AutoNew|AutoClose|Auto_Close|Document_Open|Document_New|Document_Close|DocumentOpen|Workbook_Open|Workbook_Activate|Workbook_BeforeClose|Auto_Activate)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AutoRunProcedure();

    /// <summary>
    /// Adds one finding per VBA module of the project in <paramref name="storage"/> (a storage holding "VBA"), with an
    /// auto-run finding for every procedure Office starts by itself. False when the project can't be read.
    /// </summary>
    private static bool AddVbaModules(Storage storage, List<ActiveContentFinding> findings)
    {
        if (!storage.TryOpenStorage("VBA", out var vba) || !vba.TryOpenStream("dir", out var dirStream))
            return false;
        byte[] dir;
        using (dirStream)
            dir = Decompress(ReadAll(dirStream), 0);
        var modules = ModuleOffsets(dir);
        if (modules.Count == 0)
            return false;
        foreach (var (streamName, offset) in modules)
        {
            findings.Add(new(ActiveContentKind.Macro, streamName));
            if (!vba.TryOpenStream(streamName, out var moduleStream))
                continue;
            string source;
            using (moduleStream)
            {
                var bytes = ReadAll(moduleStream);
                if (offset >= bytes.Length)
                    continue;
                source = Encoding.Latin1.GetString(Decompress(bytes, (int)offset));
            }
            foreach (Match match in AutoRunProcedure().Matches(source))
                findings.Add(new(ActiveContentKind.AutoRunMacro, match.Groups[1].Value, streamName));
        }
        return true;
    }

    /// <summary>Module stream name → offset of its compressed source, from the decompressed "dir" stream (MS-OVBA 2.3.4.2).</summary>
    internal static List<(string Stream, uint Offset)> ModuleOffsets(byte[] dir)
    {
        var modules = new List<(string, uint)>();
        string? streamName = null;
        var position = 0;
        while (position + 6 <= dir.Length)
        {
            var id = BitConverter.ToUInt16(dir, position);
            var size = BitConverter.ToUInt32(dir, position + 2);
            // PROJECTVERSION says 4 but is followed by 6 bytes (MS-OVBA 2.3.4.2.1.11).
            var length = id == 0x0009 ? 6u : size;
            var data = position + 6;
            if (data + length > dir.Length)
                break;
            if (id == 0x001A)
                streamName = Encoding.Latin1.GetString(dir, data, (int)length);
            else if (id == 0x0031 && length == 4 && streamName is not null)
                modules.Add((streamName, BitConverter.ToUInt32(dir, data)));
            position = data + (int)length;
        }
        return modules;
    }

    /// <summary>Decompresses an MS-OVBA compressed container starting at <paramref name="start"/> (2.4.1).</summary>
    internal static byte[] Decompress(byte[] data, int start)
    {
        var output = new List<byte>(data.Length * 2);
        if (start >= data.Length || data[start] != 0x01)
            return [];
        var position = start + 1;
        while (position + 2 <= data.Length)
        {
            var header = BitConverter.ToUInt16(data, position);
            var chunkEnd = Math.Min(data.Length, position + (header & 0x0FFF) + 3);
            var compressed = (header & 0x8000) != 0;
            position += 2;
            var chunkStart = output.Count;
            if (!compressed)
            {
                var raw = Math.Min(4096, data.Length - position);
                output.AddRange(data.AsSpan(position, raw).ToArray());
                position += raw;
                continue;
            }
            while (position < chunkEnd)
            {
                var flags = data[position++];
                for (var bit = 0; bit < 8 && position < chunkEnd; bit++)
                {
                    if ((flags & (1 << bit)) == 0)
                    {
                        output.Add(data[position++]);
                        continue;
                    }
                    if (position + 2 > chunkEnd)
                    {
                        position = chunkEnd;
                        break;
                    }
                    var token = BitConverter.ToUInt16(data, position);
                    position += 2;
                    var difference = output.Count - chunkStart;
                    var bitCount = 4;
                    while ((1 << bitCount) < difference && bitCount < 12)
                        bitCount++;
                    var lengthMask = 0xFFFF >> bitCount;
                    var copyLength = (token & lengthMask) + 3;
                    var offset = (token >> (16 - bitCount)) + 1;
                    var from = output.Count - offset;
                    if (from < 0)
                        return [.. output];
                    for (var i = 0; i < copyLength; i++)
                        output.Add(output[from + i]);
                }
            }
        }
        return [.. output];
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    // ───────────────────────── RTF ─────────────────────────

    private static void ScanRtf(string path, List<ActiveContentFinding> findings)
    {
        using var reader = new StreamReader(path, Encoding.Latin1);
        var buffer = new char[Math.Min(MaxRtfChars, (int)Math.Min(int.MaxValue, new FileInfo(path).Length))];
        var text = new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length));
        if (text.Contains(@"\objdata", StringComparison.Ordinal))
            findings.Add(new(ActiveContentKind.EmbeddedObject, @"\objdata", ObjectClass(text)));
        if (text.Contains(@"\objupdate", StringComparison.Ordinal) || text.Contains(@"\objautlink", StringComparison.Ordinal))
            findings.Add(new(ActiveContentKind.ExternalLink, @"\objupdate"));

        // The class of the first object ("Equation.3", "Package"), when the file names it.
        static string? ObjectClass(string text)
        {
            var match = Regex.Match(text, @"\\objclass\s+([^}\\]+)");
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>No DTDs, no external entities: a document must not make the check itself load anything.</summary>
    private static readonly XmlReaderSettings SafeXml = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
    };

    private static string Shorten(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
