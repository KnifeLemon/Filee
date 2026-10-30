// Reads HWPX (한글 / OWPML, KS X 6101) packages into the HDocument model — the inverse of HwpxWriter — so HWP and
// HWPX can become DOCX, HTML or EPUB without Hancom Office or LibreOffice (HWP is turned into HWPX by rhwp first).
//
// Files come from 한글 itself, from rhwp's export-hwpx and from Filee's own writer, which differ in details. The
// reader therefore matches elements by local name (any prefix, any namespace version), resolves hp:switch blocks and
// looks every idRef up in header.xml. Split over partial files:
//  * HwpxReader.cs         package: container, manifest and spine, pictures (BinData), section setup, XML helpers;
//  * HwpxReader.Header.cs  header.xml: fonts, character and paragraph shapes, numberings, bullets, borders, tab stops;
//  * HwpxReader.Body.cs    paragraphs, runs, text, fields (hyperlinks), notes, bookmarks and tables;
//  * HwpxReader.Objects.cs pictures, drawing objects, text boxes, groups and equations.
//
// Read() gives the model the writers expect. ReadForLayout() also keeps what only a renderer uses and that would
// change converted files: runs that differ only in layout detail stay apart, polygons, curves and arcs become shapes,
// and master pages are read. Both fill the rendering detail of formats and objects (HwpxModel.Layout.cs).

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Hwpx;

/// <summary>Reads an .hwpx file into the HWPX document model.</summary>
internal sealed partial class HwpxReader
{
    /// <summary>hp:case branches written for readers that know these namespaces hold the exact values.</summary>
    private const string HwpUnitCharNamespace = "HwpUnitChar";
    private const string Paragraph2016Namespace = "http://www.hancom.co.kr/hwpml/2016/paragraph";

    /// <summary>
    /// content.hpf metadata (opf:meta with this name and content "estimated") that marks the stored line layouts as
    /// estimated by Filee rather than laid out by 한글. HwpxWriter stores no line segments today; a writer that
    /// estimates them must add this marker so the layout engine does not trust them.
    /// </summary>
    internal const string EstimatedLinesMeta = "FileeLineSegments";

    private readonly DocumentPackage? _package;
    private readonly string _mediaFolder;
    private readonly bool _forLayout;

    /// <summary>Nesting, paragraphs and objects read so far, against the package's limits.</summary>
    private readonly DocumentBudget _budget;

    /// <summary>Manifest item id → zip entry (master page parts are found through it).</summary>
    private Dictionary<string, string> _items = new(StringComparer.Ordinal);

    /// <summary>The package says its stored line layouts were estimated by Filee.</summary>
    private bool _estimatedLines;

    /// <summary>Manifest item id → zip entry of embedded binaries (pictures).</summary>
    private readonly Dictionary<string, string> _binaryItems = new(StringComparer.Ordinal);

    /// <summary>Binary item id → extracted file (null when the item is missing).</summary>
    private readonly Dictionary<string, string?> _media = new(StringComparer.Ordinal);

    /// <summary>Elements the model cannot hold, by local name, with the number of times they were skipped.</summary>
    private readonly Dictionary<string, int> _skipped = new(StringComparer.Ordinal);

    private HwpxReader(DocumentPackage? package, string mediaFolder, bool forLayout)
    {
        _package = package;
        _mediaFolder = mediaFolder;
        _forLayout = forLayout;
        _budget = new DocumentBudget(package?.Limits ?? DocumentLimits.Default);
    }

    private DocumentPackage Package => _package ?? throw new InvalidOperationException("This reader only has a header.");

    // ───────────────────────── Entry points ─────────────────────────

    /// <summary>Reads <paramref name="path"/>; embedded pictures are extracted into <paramref name="mediaFolder"/>.</summary>
    public static HDocument Read(string path, string mediaFolder) => Read(path, mediaFolder, null);

    /// <param name="skipped">Receives the elements that were skipped because the model has no equivalent (local name → count).</param>
    public static HDocument Read(string path, string mediaFolder, IDictionary<string, int>? skipped) =>
        ReadPackage(path, mediaFolder, skipped, forLayout: false, DocumentLimits.Default);

    /// <summary>Reads within other <paramref name="limits"/> than <see cref="DocumentLimits.Default"/> (for tests).</summary>
    internal static HDocument Read(string path, string mediaFolder, IDictionary<string, int>? skipped, DocumentLimits limits) =>
        ReadPackage(path, mediaFolder, skipped, forLayout: false, limits);

    /// <summary>
    /// Reads <paramref name="path"/> for the layout engine: like <see cref="Read(string, string, IDictionary{string, int}?)"/>,
    /// plus what only a renderer uses (runs apart by layout detail, polygons / curves / arcs, master pages).
    /// </summary>
    public static HDocument ReadForLayout(string path, string mediaFolder, IDictionary<string, int>? skipped = null, DocumentLimits? limits = null) =>
        ReadPackage(path, mediaFolder, skipped, forLayout: true, limits ?? DocumentLimits.Default);

    /// <summary>A reader of header.xml alone, for its character and paragraph shapes (see <see cref="StyleResolver"/>).</summary>
    internal static HwpxReader ForHeader(XElement head)
    {
        var reader = new HwpxReader(null, "", forLayout: true);
        reader.LoadHeader(head);
        return reader;
    }

    private static HDocument ReadPackage(string path, string mediaFolder, IDictionary<string, int>? skipped, bool forLayout, DocumentLimits limits)
    {
        using (var package = DocumentPackage.Open(path, limits, () => NotAPackage(path)))
        {
            var reader = new HwpxReader(package, mediaFolder, forLayout);
            var document = reader.ReadDocument();
            if (skipped is not null)
            {
                foreach (var (name, count) in reader._skipped)
                    skipped[name] = count;
            }
            return document;
        }
    }

    /// <summary>A clear message for files that are not a zip package: HWP 5.0 binaries and DRM-wrapped files.</summary>
    private static InvalidDataException NotAPackage(string path)
    {
        var header = new byte[8];
        using (var stream = File.OpenRead(path))
            _ = stream.Read(header);
        return header is [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]
            ? new InvalidDataException("The file is an HWP 5.0 (binary) document, not HWPX. Rename it to .hwp and convert it again.")
            : new InvalidDataException("The file is not an HWPX package. It may be damaged or protected by document security (DRM) software.");
    }

    // ───────────────────────── Package ─────────────────────────

    private HDocument ReadDocument()
    {
        RejectEncrypted();

        var packagePath = RootFile();
        var package = LoadXml(packagePath, required: false);
        var folder = packagePath.Contains('/') ? packagePath[..(packagePath.LastIndexOf('/') + 1)] : "";

        // Manifest items: id → zip path. Hrefs are relative to the package root in files from 한글, but resolve them
        // against the manifest's folder too for other producers.
        var items = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in Descendants(package?.Root, "item"))
        {
            if (Attr(item, "id") is not { Length: > 0 } id || Attr(item, "href") is not { Length: > 0 } href)
                continue;
            var entry = EntryPath(href, folder);
            if (entry is null)
                continue;
            items[id] = entry;
            if (entry.StartsWith("BinData/", StringComparison.OrdinalIgnoreCase) && Attr(item, "isEmbeded") is not "0")
                _binaryItems[id] = entry;
        }
        _items = items;
        _estimatedLines = Descendants(package?.Root, "meta").Any(m => Attr(m, "name") == EstimatedLinesMeta && (Attr(m, "content") ?? m.Value) == "estimated");

        var header = LoadXml(items.Values.FirstOrDefault(p => p.EndsWith("header.xml", StringComparison.OrdinalIgnoreCase)) ?? "Contents/header.xml", required: true)!;
        LoadHeader(header.Root!);

        var document = new HDocument
        {
            Title = Descendants(package?.Root, "title").FirstOrDefault()?.Value is { } title && !string.IsNullOrWhiteSpace(title) ? title.Trim() : null,
        };
        foreach (var sectionPath in SectionPaths(package, items))
        {
            var root = LoadXml(sectionPath, required: true)!.Root!;
            ReadSection(root, document);
        }
        if (document.Sections.Count == 0)
            throw new InvalidDataException("The HWPX file has no sections (Contents/section0.xml is missing).");
        return document;
    }

    /// <summary>Password-protected HWPX files keep their parts encrypted, which is declared in META-INF/manifest.xml.</summary>
    private void RejectEncrypted()
    {
        var manifest = LoadXml("META-INF/manifest.xml", required: false);
        if (Descendants(manifest?.Root, "encryption-data").Any())
            throw new InvalidDataException("The HWPX file is password-protected. Open it in 한글 (Hangul), remove the password, save it and convert it again.");
    }

    /// <summary>The package document (content.hpf) named in META-INF/container.xml.</summary>
    private string RootFile()
    {
        var container = LoadXml("META-INF/container.xml", required: false);
        var rootFiles = Descendants(container?.Root, "rootfile").ToList();
        var hpf = rootFiles.FirstOrDefault(r => Attr(r, "media-type") == "application/hwpml-package+xml")
                  ?? rootFiles.FirstOrDefault(r => Attr(r, "full-path")?.EndsWith(".hpf", StringComparison.OrdinalIgnoreCase) == true);
        return Attr(hpf, "full-path") ?? "Contents/content.hpf";
    }

    /// <summary>Section parts in reading order: the spine, else Contents/sectionN.xml by number.</summary>
    private List<string> SectionPaths(XDocument? package, Dictionary<string, string> items)
    {
        static bool IsSection(string path) =>
            Path.GetFileName(path) is var name && name.StartsWith("section", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

        var result = new List<string>();
        foreach (var itemRef in Descendants(package?.Root, "itemref"))
        {
            if (items.TryGetValue(Attr(itemRef, "idref") ?? "", out var path) && IsSection(path) && !result.Contains(path))
                result.Add(path);
        }
        if (result.Count > 0)
            return result;

        return Package.Entries
            .Where(e => e.FullName.StartsWith("Contents/", StringComparison.OrdinalIgnoreCase) && IsSection(e.FullName))
            .OrderBy(e => int.TryParse(Path.GetFileNameWithoutExtension(e.Name).AsSpan(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue)
            .Select(e => e.FullName)
            .ToList();
    }

    private string? EntryPath(string href, string folder)
    {
        var path = Uri.UnescapeDataString(href).Replace('\\', '/').TrimStart('/');
        if (Package.GetEntry(path) is not null)
            return path;
        return folder.Length > 0 && Package.GetEntry(folder + path) is not null ? folder + path : path;
    }

    private XDocument? LoadXml(string path, bool required)
    {
        var entry = Package.GetEntry(path);
        if (entry is null)
        {
            return required
                ? throw new InvalidDataException($"The HWPX file is incomplete: {path} is missing.")
                : null;
        }
        try
        {
            // Whitespace matters: <hp:t> </hp:t> is a real space.
            return Package.LoadXml(entry, preserveWhitespace: true);
        }
        catch (XmlException ex)
        {
            // Encrypted (distribution / DRM) documents have binary parts where XML is expected.
            throw new InvalidDataException($"The HWPX file cannot be read ({path} is not valid XML). It may be encrypted or damaged. {ex.Message}", ex);
        }
    }

    /// <summary>Extracts a BinData item (by manifest id) into the media folder once; null when it is missing.</summary>
    private string? ExtractBinary(string? itemId)
    {
        if (string.IsNullOrEmpty(itemId))
            return null;
        if (_media.TryGetValue(itemId, out var cached))
            return cached;

        // Without a manifest entry, 한글's naming (BinData/<id>.<ext>) still finds the file.
        var entryPath = _binaryItems.GetValueOrDefault(itemId)
                        ?? Package.Entries.FirstOrDefault(e => e.FullName.StartsWith("BinData/", StringComparison.OrdinalIgnoreCase) &&
                                                            string.Equals(Path.GetFileNameWithoutExtension(e.Name), itemId, StringComparison.OrdinalIgnoreCase))?.FullName;
        string? path = null;
        if (entryPath is not null && Package.GetEntry(entryPath) is { } entry)
        {
            Directory.CreateDirectory(_mediaFolder);
            path = Path.Combine(_mediaFolder, $"hwpx-media-{_media.Count + 1}{DocumentPackage.SafeExtension(entry.Name)}");
            if (!Package.ExtractPicture(entry, path))
                path = null; // larger than any picture should be: left out
        }
        _media[itemId] = path;
        return path;
    }

    // ───────────────────────── Sections ─────────────────────────

    /// <summary>
    /// State of the section being read. A secPr starts a 한글 section (구역); its first colPr gives the columns and
    /// later ones change them mid-page. Header, footer and page number controls may sit in any body paragraph.
    /// </summary>
    private sealed class SectionState(HDocument document)
    {
        public HSection? Section;
        public bool ColumnsSet;
        public bool StartNumberSet;
        public XElement? PageNumber;

        public HSection Current => Section ?? Begin(null);

        public HSection Begin(HSection? section)
        {
            Finish();
            Section = section ?? new HSection();
            ColumnsSet = false;
            StartNumberSet = Section.StartPageNumber is not null;
            PageNumber = null;
            document.Sections.Add(Section);
            return Section;
        }

        /// <summary>Turns an automatic page number (hp:pageNum, "쪽 번호 매기기") into a header or footer paragraph.</summary>
        public void Finish()
        {
            if (Section is null || PageNumber is not { } control)
                return;
            PageNumber = null;
            var position = Attr(control, "pos") ?? "BOTTOM_CENTER";
            if (position == "NONE")
                return;

            var side = Attr(control, "sideChar") is { Length: > 0 } s && s != "\0" ? s : null;
            var paragraph = new HParagraph
            {
                Format = new HParaFormat(Align: position switch
                {
                    _ when position.EndsWith("LEFT", StringComparison.Ordinal) || position.StartsWith("INSIDE", StringComparison.Ordinal) => HAlign.Left,
                    _ when position.EndsWith("RIGHT", StringComparison.Ordinal) || position.StartsWith("OUTSIDE", StringComparison.Ordinal) => HAlign.Right,
                    _ => HAlign.Center,
                }),
            };
            if (side is not null)
                paragraph.Inlines.Add(new HText(side + " ", default));
            paragraph.Inlines.Add(new HField(HFieldKind.PageNumber, default));
            if (side is not null)
                paragraph.Inlines.Add(new HText(" " + side, default));

            var top = position.Contains("TOP", StringComparison.Ordinal);
            var target = top ? Section.Headers : Section.Footers;
            if (target.Count == 0)
                target.Add(new HHeaderFooter(HPageType.Both));
            foreach (var item in target)
                item.Blocks.Add(paragraph);
        }
    }

    private void ReadSection(XElement root, HDocument document)
    {
        var state = new SectionState(document);
        foreach (var element in Elements(root))
        {
            if (element.Name.LocalName == "p")
                ReadTopParagraph(element, state);
            else
                Skip(element);
        }
        if (state.Section is null)
            state.Begin(null); // a section part without paragraphs is still a section
        state.Finish();
    }

    /// <summary>A body paragraph: a secPr in it starts a new section (normally only in a part's first paragraph).</summary>
    private void ReadTopParagraph(XElement p, SectionState state)
    {
        var secPr = Elements(p).Where(e => e.Name.LocalName == "run").SelectMany(Elements).FirstOrDefault(e => e.Name.LocalName == "secPr");
        if (secPr is not null)
        {
            var section = state.Begin(Section(secPr));
            _outlineNumberingId = Attr(secPr, "outlineShapeIDRef");
            if (_forLayout)
                MasterPages(secPr, section);
        }
        ReadParagraph(p, state.Current.Blocks, state);
    }

    /// <summary>
    /// Master pages (바탕쪽): the secPr refers to them by manifest id (hp:masterPage idRef), each a part of its own
    /// whose root holds the type and a paragraph list.
    /// </summary>
    private void MasterPages(XElement secPr, HSection section)
    {
        foreach (var reference in Descendants(secPr, "masterPage"))
        {
            var root = Attr(reference, "idRef") is { Length: > 0 } id
                ? LoadXml(_items.GetValueOrDefault(id) ?? $"Contents/{id}.xml", required: false)?.Root
                : reference;
            if (root is null)
                continue;
            var page = new HMasterPage(Attr(root, "type") switch
            {
                "EVEN" => HMasterPageKind.Even,
                "ODD" => HMasterPageKind.Odd,
                "LAST_PAGE" => HMasterPageKind.LastPage,
                "OPTIONAL_PAGE" => HMasterPageKind.OptionalPage,
                _ => HMasterPageKind.Both,
            })
            {
                PageNumber = Int(root, "pageNumber"),
                Duplicate = Flag(root, "pageDuplicate"),
                Front = Flag(root, "pageFront"),
            };
            page.Blocks.AddRange(ReadSubList(Child(root, "subList")));
            section.MasterPages.Add(page);
        }
    }

    private static HSection Section(XElement secPr)
    {
        var pagePr = Child(secPr, "pagePr");
        var margin = Child(pagePr, "margin");
        var width = Int(pagePr, "width", 59530);
        var height = Int(pagePr, "height", 84190);
        // Width and height describe the portrait sheet; "NARROWLY" turns it (landscape).
        if (Attr(pagePr, "landscape") == "NARROWLY")
            (width, height) = (Math.Max(width, height), Math.Min(width, height));

        var visibility = Child(secPr, "visibility");
        var start = Int(Child(secPr, "startNum"), "page");
        var grid = Child(secPr, "grid");
        var (lineGrid, charGrid) = (Math.Max(0, Int(grid, "lineGrid")), Math.Max(0, Int(grid, "charGrid")));
        return new HSection
        {
            Grid = lineGrid > 0 || charGrid > 0
                ? new HDocumentGrid(charGrid == 0 ? HGridKind.Lines : lineGrid == 0 ? HGridKind.SnapToChars : HGridKind.LinesAndChars, lineGrid, charGrid)
                : null,
            Page = new HPage(width, height,
                Left: Int(margin, "left", 7200), Right: Int(margin, "right", 7200),
                Top: Int(margin, "top", 4255), Bottom: Int(margin, "bottom", 4960),
                Header: Int(margin, "header", 4250), Footer: Int(margin, "footer", 2240),
                Gutter: Int(margin, "gutter")),
            HideFirstHeader = Flag(visibility, "hideFirstHeader"),
            HideFirstFooter = Flag(visibility, "hideFirstFooter"),
            StartPageNumber = start > 0 ? start : null,
            Footnotes = NoteShape(Child(secPr, "footNotePr"), HNoteShape.Footnote),
            Endnotes = NoteShape(Child(secPr, "endNotePr"), HNoteShape.Endnote),
        };
    }

    /// <summary>A section's footnote or endnote shape (hp:footNotePr, hp:endNotePr).</summary>
    private static HNoteShape NoteShape(XElement? shape, HNoteShape fallback)
    {
        if (shape is null)
            return fallback;
        var line = Child(shape, "noteLine");
        var spacing = Child(shape, "noteSpacing");
        return new HNoteShape(
            NoteFormat(Child(shape, "autoNumFormat")) ?? fallback.Format,
            line is null || Attr(line, "type") == "NONE" ? 0 : Int(line, "length", fallback.Line),
            Int(spacing, "betweenNotes", fallback.Between), Int(spacing, "belowLine", fallback.Below), Int(spacing, "aboveLine", fallback.Above),
            Flag(Child(shape, "placement"), "beneathText"));
    }

    /// <summary>A note number's format (hp:autoNumFormat); null when there is none.</summary>
    private static HNoteFormat? NoteFormat(XElement? format) => format is null
        ? null
        : new HNoteFormat(Attr(format, "type") ?? "DIGIT", Attr(format, "prefixChar") ?? "", Attr(format, "suffixChar") ?? "", Flag(format, "supscript"));

    private static HColumns Columns(XElement colPr)
    {
        var count = Math.Max(1, Int(colPr, "colCount", 1));
        if (count == 1)
            return HColumns.Single;
        // Columns of different widths (sameSz="0") list each width and gap; the model keeps one gap.
        var gap = Flag(colPr, "sameSz", true) ? Int(colPr, "sameGap") : Int(Child(colPr, "colSz"), "gap", Int(colPr, "sameGap"));
        var line = Child(colPr, "colLine");
        return new HColumns(count, gap, line is not null && Attr(line, "type") is not (null or "NONE"));
    }

    // ───────────────────────── XML helpers ─────────────────────────

    /// <summary>Child elements with hp:switch blocks replaced by the branch this reader understands.</summary>
    private static IEnumerable<XElement> Elements(XElement? parent)
    {
        if (parent is null)
            yield break;
        foreach (var child in parent.Elements())
        {
            if (child.Name.LocalName == "switch")
            {
                foreach (var inner in Elements(Branch(child).Branch))
                    yield return inner;
            }
            else
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// The hp:case whose required namespace is known (exact HWPUNIT values, 2016 paragraph features), else hp:default.
    /// <c>Exact</c> is false for hp:default, where 한글 doubles lengths for older readers.
    /// </summary>
    private static (XElement? Branch, bool Exact) Branch(XElement switchElement)
    {
        foreach (var branch in switchElement.Elements())
        {
            if (branch.Name.LocalName != "case")
                continue;
            var required = branch.Attributes().FirstOrDefault(a => a.Name.LocalName == "required-namespace")?.Value ?? "";
            if (required.Contains(HwpUnitCharNamespace, StringComparison.Ordinal) || required == Paragraph2016Namespace)
                return (branch, true);
        }
        return (switchElement.Elements().FirstOrDefault(e => e.Name.LocalName == "default"), false);
    }

    /// <summary>The first child with this local name, looking into hp:switch blocks (a loop: it runs for every property).</summary>
    private static XElement? Child(XElement? parent, string name)
    {
        if (parent is null)
            return null;
        foreach (var child in parent.Elements())
        {
            var local = child.Name.LocalName;
            if (local == name)
                return child;
            if (local == "switch" && Child(Branch(child).Branch, name) is { } inner)
                return inner;
        }
        return null;
    }

    private static IEnumerable<XElement> Children(XElement? parent, string name) => Elements(parent).Where(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Descendants(XElement? root, string name) =>
        root?.Descendants().Where(e => e.Name.LocalName == name) ?? [];

    private static string? Attr(XElement? element, string name) => (string?)element?.Attribute(name);

    /// <summary>
    /// An integer attribute. Offsets are unsigned 32-bit values in the file, so negative positions written by some
    /// producers arrive as numbers above 2³¹ and are wrapped back.
    /// </summary>
    private static int Int(XElement? element, string name, int fallback = 0)
    {
        if (!long.TryParse(Attr(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return double.TryParse(Attr(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
                ? (int)Math.Clamp(Math.Round(real), int.MinValue, int.MaxValue)
                : fallback;
        }
        if (value is > int.MaxValue and <= uint.MaxValue)
            value -= 1L << 32;
        return (int)Math.Clamp(value, int.MinValue, int.MaxValue);
    }

    private static bool Flag(XElement? element, string name, bool fallback = false) => Attr(element, name) switch
    {
        null => fallback,
        "1" or "true" => true,
        _ => false,
    };

    /// <summary>"#RRGGBB" in upper case; null for "none" or unknown values. 한글 may write "#AARRGGBB".</summary>
    private static string? Color(string? value)
    {
        if (value is null || !value.StartsWith('#'))
            return null;
        var hex = value.Length == 9 ? value[3..] : value[1..];
        return hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ? "#" + hex.ToUpperInvariant() : null;
    }

    /// <summary>Counts an element the model cannot hold (see <see cref="Read(string, string, IDictionary{string, int}?)"/>).</summary>
    private void Skip(XElement element) => Skip(element.Name.LocalName);

    private void Skip(string name) => _skipped[name] = _skipped.GetValueOrDefault(name) + 1;
}
