// Mind maps → HDocument as an outline (#1543, #1004): its topics become the document's structure. The same reader
// reads a map for drawing (ReadForDrawing, MindMaps/MindMapDrawing): its sheets with each topic's look (MindStyle),
// folding, side, floating place, boundaries, summaries, clouds, arrow links, relationships and theme.
//  * FreeMind and Freeplane (.mm): XML, <map> holding <node> elements in <node> elements. A node's text is its TEXT
//    attribute (LOCALIZED_TEXT when it has none) or its rich text (richcontent TYPE="NODE", HTML); its notes and
//    details are rich text too (HTML, or Freeplane's plain text and Markdown); LINK is its link, <icon BUILTIN> its
//    icons and <attribute NAME VALUE> its attributes. A .mm that isn't XML at all is Objective-C++ source (same
//    extension), read as plain text.
//  * XMind (.xmind): a ZIP. XMind Zen and 2020 and later keep the sheets in content.json (rootTopic, children
//    attached / detached / summary / callout, title, notes plain or realHTML, href, labels, markers, image); XMind 8
//    and before in content.xml (sheet, topic, title, children/topics, notes, xlink:href, labels, marker-refs,
//    xhtml:img). Newer files also have a content.xml that only says to open them in a newer XMind: content.json wins.
//    A file saved with a password (encryption-data in the manifest) is refused with error.xmind_password.
// The central topic and the two levels below it are headings 1 to 3; deeper topics are bulleted lists nested a level
// per topic (past nine levels they stay at the ninth). Into OPML every topic is a heading (one level per topic, past 9
// at 9), so the outline keeps its whole tree there (MarkupConverter). Notes follow their topic, a list item's as its
// continuation; icons and markers with an obvious symbol come before the topic's text (① for priority 1, ✓ for done);
// links to the web stay links, links to other topics point at a bookmark on that topic; an XMind topic's picture is
// unpacked and follows the topic. Each sheet of an XMind file starts a new page.
// Untrusted input: the XML goes through SafeXml and DocumentPackage (no DTD, depth, element, part, package and inflate
// limits), the JSON is parsed with a depth limit, both trees are walked with a stack of their own rather than by
// recursion, and every paragraph and picture counts against DocumentLimits (DocumentBudget). Freeplane's external
// pictures are read only from inside the map's folder (DocumentFiles); relationships past what the drawing draws are
// refused while reading.

using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Infrastructure;
using Filee.Engines.MindMaps;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Reads FreeMind / Freeplane and XMind mind maps into the document model as an outline.</summary>
internal sealed class MindMapReader
{
    /// <summary>FreeMind and Freeplane maps.</summary>
    public const string FreeMind = "mm";

    /// <summary>XMind maps.</summary>
    public const string XMind = "xmind";

    /// <summary>The formats this reader reads.</summary>
    public static readonly string[] Formats = [FreeMind, XMind];

    /// <summary>The refusal of an XMind file saved with a password.</summary>
    public const string PasswordKey = "error.xmind_password";

    /// <summary>Levels of topics written as headings: the central topic, its main topics and their subtopics.</summary>
    public const int HeadingLevels = 3;

    /// <summary>Levels of nested lists below the headings; deeper topics stay at the deepest.</summary>
    private const int ListLevels = 9;

    /// <summary>JSON levels per topic level in content.json (a topic, its children object, the array of attached ones).</summary>
    private const int JsonLevelsPerTopic = 3;

    private static readonly string[] PictureExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg"];

    private readonly string _baseFolder;
    private readonly string _media;
    private readonly DocumentBudget _budget;
    private readonly int _headingLevels;
    private readonly List<HBlock> _blocks = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly HashSet<string> _targets = new(StringComparer.Ordinal);
    private HNumbering? _list;
    private bool _pageBreak;
    private int _pictures;
    private readonly List<Sheet> _sheets = [];

    /// <summary>Read for the drawing: the sheets are kept, with their themes and relationships.</summary>
    private bool _drawing;

    private MindMapReader(string baseFolder, string media, DocumentLimits limits, bool headingsOnly)
    {
        _baseFolder = baseFolder;
        _media = media;
        _budget = new DocumentBudget(limits);
        _headingLevels = headingsOnly ? int.MaxValue : HeadingLevels;
    }

    /// <summary>A topic as the map holds it, before it becomes paragraphs or is drawn (MindMaps/MindMapDrawing).</summary>
    internal sealed class Topic
    {
        public string? Id;
        public string Text = "";
        public string? Html;
        public string? Link;
        public bool NewPage;
        public int Details;
        public readonly List<string> Symbols = [];
        public readonly List<string> Labels = [];
        public readonly List<(string Name, string Value)> Attributes = [];
        public readonly List<Note> Notes = [];
        public readonly List<string> Pictures = [];
        public readonly List<Topic> Children = [];

        // What the drawn map takes besides: how the topic looks and where it goes.
        public MindStyle Style = MindStyle.Empty;
        public TopicKind Kind;

        /// <summary>Its subtopics are hidden (FreeMind FOLDED, XMind branch "folded").</summary>
        public bool Folded;

        /// <summary>The side of the central topic a main topic goes on: -1 left, 1 right, 0 the drawing's choice.</summary>
        public int Side;

        /// <summary>A floating topic's place, from the central topic's centre (XMind position), in points.</summary>
        public SKPoint? Position;

        /// <summary>The size XMind gives the topic's picture, in points; null = its own.</summary>
        public SKSize? PictureSize;

        /// <summary>An XMind equation's TeX.</summary>
        public string? Equation;

        public MindCloud? Cloud;
        public readonly List<MindBoundary> Boundaries = [];
        public readonly List<MindSummary> Summaries = [];

        /// <summary>FreeMind / Freeplane arrow links from this topic.</summary>
        public readonly List<MindLink> Links = [];
    }

    /// <summary>Where a topic hangs off its parent.</summary>
    internal enum TopicKind
    {
        Attached,

        /// <summary>A floating topic (XMind detached).</summary>
        Detached,

        /// <summary>The topic of a summary (XMind children.summary).</summary>
        Summary,

        Callout,
    }

    /// <summary>One sheet of a map, for the drawing: its central topic, relationships and theme.</summary>
    internal sealed class Sheet(Topic root)
    {
        public Topic Root { get; } = root;

        /// <summary>XMind's structure of the central topic ("org.xmind.ui.map.clockwise", "org.xmind.ui.logic.right", ...).</summary>
        public string? Structure { get; set; }

        public SKColor? Background { get; set; }

        /// <summary>The sheet's theme by topic class: centralTopic, mainTopic, subTopic, floatingTopic, summaryTopic, calloutTopic, boundary, relationship.</summary>
        public Dictionary<string, MindStyle> Theme { get; } = new(StringComparer.Ordinal);

        public List<MindLink> Relationships { get; } = [];

        /// <summary>Colours of the main topics' branches in turn (XMind multi-line-colors); empty = the drawing's.</summary>
        public List<SKColor> BranchColors { get; } = [];
    }

    /// <summary>A map read for drawing: its sheets, and the text of each topic as the outline would write it.</summary>
    internal sealed class Drawn(List<Sheet> sheets, MindMapReader reader)
    {
        public List<Sheet> Sheets { get; } = sheets;

        /// <summary>The topic's text (rich text with its formatting, lines with line breaks) and the pictures its rich text holds.</summary>
        public (List<HInline> Inlines, List<string> Pictures) Text(Topic topic) => reader.TitleInlines(topic);

        /// <summary>The folder relative references are resolved against (Freeplane's external pictures).</summary>
        public string BaseFolder => reader._baseFolder;
    }

    internal enum NoteKind
    {
        Plain,
        Markdown,
        Html,
    }

    internal readonly record struct Note(NoteKind Kind, string Content);

    // ───────────────────────── Entry points ─────────────────────────

    /// <summary>Reads a map of <paramref name="format"/> (<see cref="FreeMind"/> or <see cref="XMind"/>).</summary>
    /// <param name="headingsOnly">Every topic a heading (for OPML), instead of lists below <see cref="HeadingLevels"/>.</param>
    public static HDocument Read(string path, string format, string media, bool headingsOnly = false) =>
        string.Equals(format, XMind, StringComparison.OrdinalIgnoreCase)
            ? ReadXMind(path, media, headingsOnly)
            : ReadFreeMind(path, media, headingsOnly);

    public static HDocument ReadFreeMind(string path, string media) => ReadFreeMind(path, media, headingsOnly: false);

    public static HDocument ReadXMind(string path, string media) => ReadXMind(path, media, headingsOnly: false);

    private static HDocument ReadFreeMind(string path, string media, bool headingsOnly)
    {
        var limits = DocumentLimits.Default;
        if (new FileInfo(path).Length > limits.MaxPartBytes)
            throw DocumentLimits.Exceeded($"The mind map is larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
        return ReadFreeMind(File.ReadAllBytes(path), Folder(path), media, limits, headingsOnly);
    }

    private static HDocument ReadXMind(string path, string media, bool headingsOnly)
    {
        using var stream = File.OpenRead(path);
        return ReadXMind(stream, Folder(path), media, DocumentLimits.Default, headingsOnly);
    }

    private static string Folder(string path) => Path.GetDirectoryName(Path.GetFullPath(path))!;

    /// <summary>
    /// Reads a map of <paramref name="format"/> for drawing (MindMaps/MindMapDrawing): its sheets with what they say
    /// about the look of each topic. A .mm that isn't XML (Objective-C++ source) is refused as no mind map.
    /// </summary>
    public static Drawn ReadForDrawing(string path, string format, string media)
    {
        var limits = DocumentLimits.Default;
        if (string.Equals(format, XMind, StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path);
            return DrawXMind(stream, Folder(path), media, limits);
        }
        if (new FileInfo(path).Length > limits.MaxPartBytes)
            throw DocumentLimits.Exceeded($"The mind map is larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
        return DrawFreeMind(File.ReadAllBytes(path), Folder(path), media, limits);
    }

    /// <summary>Whether the .mm file at <paramref name="path"/> starts like XML (a mind map) rather than source code.</summary>
    public static bool IsFreeMindMap(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[4096];
            return LooksLikeXml(head.AsSpan(0, stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ───────────────────────── FreeMind and Freeplane ─────────────────────────

    internal static HDocument ReadFreeMind(byte[] bytes, string baseFolder, string media, DocumentLimits limits, bool headingsOnly = false)
    {
        // Objective-C++ source shares the extension; a mind map is XML, so anything else is read as the text it is.
        if (!LooksLikeXml(bytes))
            return DocumentReaders.TextDocument(bytes);
        var reader = new MindMapReader(baseFolder, media, limits, headingsOnly);
        return reader.Build(reader.FreeMindRoots(bytes));
    }

    internal static Drawn DrawFreeMind(byte[] bytes, string baseFolder, string media, DocumentLimits limits)
    {
        if (!LooksLikeXml(bytes))
            throw new InvalidDataException("The file is not a FreeMind or Freeplane mind map (it is not XML).");
        var reader = new MindMapReader(baseFolder, media, limits, headingsOnly: false) { _drawing = true };
        return new Drawn([.. reader.FreeMindRoots(bytes).Select(root => new Sheet(root))], reader);
    }

    private List<Topic> FreeMindRoots(byte[] bytes)
    {
        var limits = _budget.Limits;
        XDocument xml;
        try
        {
            xml = SafeXml.Load(new MemoryStream(bytes, writable: false), limits.Xml(0, preserveWhitespace: false), DocumentLimits.Exceeded);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("The file is not a FreeMind or Freeplane mind map (its XML can't be read).", ex);
        }
        if (xml.Root is not { } root || root.Name.LocalName != "map")
            throw new InvalidDataException("The file is not a FreeMind or Freeplane mind map.");

        var roots = new List<Topic>();
        // Node elements and the topic they go into (null for the map's own), walked without recursion.
        var stack = new Stack<(XElement Element, Topic? Parent)>();
        foreach (var node in Nodes(root).Reverse())
            stack.Push((node, null));
        while (stack.Count > 0)
        {
            var (element, parent) = stack.Pop();
            var topic = FreeMindTopic(element);
            (parent?.Children ?? roots).Add(topic);
            foreach (var child in Nodes(element).Reverse())
                stack.Push((child, topic));
        }
        return roots;
    }

    private static IEnumerable<XElement> Nodes(XElement parent) => parent.Elements().Where(e => e.Name.LocalName == "node");

    private static bool LooksLikeXml(byte[] bytes)
    {
        var start = bytes.AsSpan();
        if (start.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            start = start[3..];
        else if (start.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || start.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return true; // UTF-16: XML Freeplane never writes, but no source file either
        foreach (var b in start)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                continue;
            return b == (byte)'<';
        }
        return false;
    }

    private Topic FreeMindTopic(XElement node)
    {
        var topic = new Topic
        {
            Id = (string?)node.Attribute("ID"),
            Text = (string?)node.Attribute("TEXT") ?? (string?)node.Attribute("LOCALIZED_TEXT") ?? "",
        };
        if (topic.Text.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            // FreeMind 0.8 kept rich text in TEXT itself.
            topic.Html = topic.Text;
            topic.Text = "";
        }
        Remember(topic);
        topic.Link = Link((string?)node.Attribute("LINK"));
        topic.Folded = string.Equals((string?)node.Attribute("FOLDED"), "true", StringComparison.OrdinalIgnoreCase);
        topic.Side = ((string?)node.Attribute("POSITION"))?.Trim().ToLowerInvariant() switch
        {
            "left" => -1,
            "right" => 1,
            _ => 0,
        };
        var style = new MindStyle
        {
            TextColor = MindStyle.Color((string?)node.Attribute("COLOR")),
            Fill = MindStyle.Color((string?)node.Attribute("BACKGROUND_COLOR")),
            BorderColor = MindStyle.Color((string?)node.Attribute("BORDER_COLOR")),
            BorderWidth = MindStyle.Length((string?)node.Attribute("BORDER_WIDTH")),
            Shape = FreeMindShape((string?)node.Attribute("STYLE")),
        };

        foreach (var child in node.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "font":
                    style = style with
                    {
                        FontSize = MindStyle.Length((string?)child.Attribute("SIZE")),
                        Bold = True(child, "BOLD"),
                        Italic = True(child, "ITALIC"),
                        Font = MindStyle.FontName((string?)child.Attribute("NAME") ?? ""),
                    };
                    break;
                case "edge":
                    style = style with
                    {
                        LineColor = MindStyle.Color((string?)child.Attribute("COLOR")),
                        LineWidth = ((string?)child.Attribute("WIDTH"))?.Trim() is "thin" ? 0.75f : MindStyle.Length((string?)child.Attribute("WIDTH")),
                        Line = ((string?)child.Attribute("STYLE"))?.Trim().ToLowerInvariant() switch
                        {
                            "linear" or "sharp_linear" => BranchLine.Straight,
                            "bezier" or "sharp_bezier" => BranchLine.Curve,
                            "horizontal" or "summary" => BranchLine.Elbow,
                            "hide_edge" => BranchLine.None,
                            _ => BranchLine.Default,
                        },
                    };
                    break;
                case "cloud":
                    topic.Cloud = new MindCloud(MindStyle.Color((string?)child.Attribute("COLOR")));
                    break;
                case "arrowlink" when ((string?)child.Attribute("DESTINATION"))?.Trim() is { Length: > 0 } destination:
                    topic.Links.Add(new MindLink(topic.Id ?? "", destination,
                        ((string?)child.Attribute("MIDDLE_LABEL"))?.Trim() is { Length: > 0 } label ? label : null,
                        new MindStyle
                        {
                            LineColor = MindStyle.Color((string?)child.Attribute("COLOR")),
                            LineWidth = MindStyle.Length((string?)child.Attribute("WIDTH")),
                            Dashed = ((string?)child.Attribute("DASH"))?.Trim() is { Length: > 0 } dash && dash != "SOLID" ? true : null,
                            ArrowStart = Arrow((string?)child.Attribute("STARTARROW"), LineEndKind.None),
                            ArrowEnd = Arrow((string?)child.Attribute("ENDARROW"), LineEndKind.Triangle),
                        }));
                    break;
                case "hook" when string.Equals((string?)child.Attribute("NAME"), "ExternalObject", StringComparison.Ordinal):
                    ExternalPicture((string?)child.Attribute("URI"), (string?)child.Attribute("SIZE"), topic);
                    break;
                case "richcontent":
                    var type = ((string?)child.Attribute("TYPE"))?.ToUpperInvariant();
                    var html = child.Elements().FirstOrDefault(e => e.Name.LocalName == "html");
                    if (type == "NODE")
                    {
                        if (html is not null)
                            topic.Html = html.ToString(SaveOptions.DisableFormatting);
                        else if (child.Value.Trim() is { Length: > 0 } text)
                            topic.Text = text;
                    }
                    else if (type is "NOTE" or "DETAILS")
                    {
                        var note = html is not null
                            ? new Note(NoteKind.Html, html.ToString(SaveOptions.DisableFormatting))
                            : new Note(((string?)child.Attribute("CONTENT-TYPE"))?.Contains("markdown", StringComparison.OrdinalIgnoreCase) == true
                                ? NoteKind.Markdown : NoteKind.Plain, child.Value);
                        // Details are shown under the node, notes in their own pane: details first.
                        if (type == "DETAILS")
                            topic.Notes.Insert(topic.Details++, note);
                        else
                            topic.Notes.Add(note);
                    }
                    break;
                case "icon" when Symbol((string?)child.Attribute("BUILTIN")) is { } symbol:
                    topic.Symbols.Add(symbol);
                    break;
                case "attribute" when ((string?)child.Attribute("NAME"))?.Trim() is { Length: > 0 } name:
                    topic.Attributes.Add((name, ((string?)child.Attribute("VALUE"))?.Trim() ?? ""));
                    break;
            }
        }
        topic.Style = style;
        return topic;
    }

    private static bool? True(XElement element, string name) =>
        (string?)element.Attribute(name) is { } value ? string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase) : null;

    /// <summary>A FreeMind / Freeplane node STYLE: fork (a line under the text), bubble, and Freeplane's other shapes.</summary>
    private static TopicShape FreeMindShape(string? style) => style?.Trim().ToLowerInvariant() switch
    {
        "fork" or "combined" => TopicShape.Underline,
        "bubble" => TopicShape.RoundedRect,
        "rectangle" => TopicShape.Rect,
        "oval" => TopicShape.Ellipse,
        "wide_hexagon" or "narrow_hexagon" => TopicShape.Hexagon,
        _ => TopicShape.Default,
    };

    /// <summary>An arrow link's end: "None" draws none, anything else (FreeMind's "Default") an arrowhead.</summary>
    private static LineEndKind Arrow(string? value, LineEndKind absent) =>
        value is null ? absent : string.Equals(value.Trim(), "None", StringComparison.OrdinalIgnoreCase) ? LineEndKind.None : LineEndKind.Triangle;

    /// <summary>A finite number, or null.</summary>
    private static float? Number(string? text) =>
        float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
        && float.IsFinite(number) ? number : null;

    /// <summary>
    /// A Freeplane external picture (hook ExternalObject URI, SIZE its zoom): a picture file beside the map, taken
    /// only from inside the map's folder (DocumentFiles) and when its size is within the picture limit.
    /// </summary>
    private void ExternalPicture(string? uri, string? size, Topic topic)
    {
        if (DocumentFiles.Find(uri, _baseFolder) is not { } file || !PictureExtensions.Contains(DocumentPackage.SafeExtension(file))
            || PictureSize.Read(file) is not { } pixels || (long)pixels.Width * pixels.Height > _budget.Limits.MaxPicturePixels)
            return;
        var (width, height) = pixels;
        _budget.Object();
        topic.Pictures.Add(file);
        if (Number(size) is { } zoom && zoom is > 0.01f and <= 10)
            topic.PictureSize = new SKSize(width * 0.75f * zoom, height * 0.75f * zoom);
    }

    // ───────────────────────── XMind ─────────────────────────

    internal static HDocument ReadXMind(Stream stream, string baseFolder, string media, DocumentLimits limits, bool headingsOnly = false)
    {
        var reader = new MindMapReader(baseFolder, media, limits, headingsOnly);
        return reader.Build(reader.XMindRoots(stream));
    }

    internal static Drawn DrawXMind(Stream stream, string baseFolder, string media, DocumentLimits limits)
    {
        var reader = new MindMapReader(baseFolder, media, limits, headingsOnly: false) { _drawing = true };
        reader.XMindRoots(stream);
        return new Drawn(reader._sheets, reader);
    }

    private List<Topic> XMindRoots(Stream stream)
    {
        using var package = DocumentPackage.Open(stream, _budget.Limits, () => new InvalidDataException("The file is not an XMind mind map (it is not a ZIP package)."));
        foreach (var name in new[] { "META-INF/manifest.xml", "manifest.json" })
        {
            if (package.GetEntry(name) is { } manifest && package.ReadBytes(manifest, 4 * 1024 * 1024) is { } bytes
                && bytes.AsSpan().IndexOf("encryption-data"u8) >= 0)
                throw new ConversionRefusedException(PasswordKey);
        }

        if (package.GetEntry("content.json") is { } json)
            return XMindJson(package, json);
        if (package.GetEntry("content.xml") is { } xml)
            return XMindXml(package, xml);
        throw new InvalidDataException("The file is not an XMind mind map (it has no content.json or content.xml).");
    }

    private List<Topic> XMindJson(DocumentPackage package, System.IO.Compression.ZipArchiveEntry entry)
    {
        var limits = _budget.Limits;
        var bytes = package.ReadBytes(entry, limits.MaxPartBytes)
                    ?? throw DocumentLimits.Exceeded($"content.json is larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
        // Strings that aren't UTF-8 would fail only when read, as no damage the caller knows.
        if (!System.Text.Unicode.Utf8.IsValid(bytes))
            throw new InvalidDataException("The file is not an XMind mind map (its content.json isn't UTF-8 text).");
        var maxDepth = JsonLevelsPerTopic * limits.MaxXmlDepth + 16;
        if (NestsDeeper(bytes, maxDepth))
            throw DocumentLimits.Exceeded($"The mind map nests topics more than {limits.MaxXmlDepth} levels deep.");
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = maxDepth + 1,
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The file is not an XMind mind map (its content.json can't be read).", ex);
        }

        using (parsed)
        {
            var sheets = parsed.RootElement;
            if (sheets.ValueKind == JsonValueKind.Object)
                sheets = sheets.TryGetProperty("sheets", out var inner) ? inner : default;
            if (sheets.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The file is not an XMind mind map (content.json holds no sheets).");

            var roots = new List<Topic>();
            var stack = new Stack<(JsonElement Element, Topic? Parent, int Depth, TopicKind Kind)>();
            foreach (var sheet in sheets.EnumerateArray())
            {
                if (sheet.ValueKind != JsonValueKind.Object || !sheet.TryGetProperty("rootTopic", out var rootTopic) || rootTopic.ValueKind != JsonValueKind.Object)
                    continue;
                var first = roots.Count;
                stack.Push((rootTopic, null, 0, TopicKind.Attached));
                while (stack.Count > 0)
                {
                    var (element, parent, depth, kind) = stack.Pop();
                    if (depth > limits.MaxXmlDepth)
                        throw DocumentLimits.Exceeded($"The mind map nests topics more than {limits.MaxXmlDepth} levels deep.");
                    var topic = JsonTopic(package, element);
                    topic.Kind = kind;
                    (parent?.Children ?? roots).Add(topic);
                    if (!element.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Object)
                        continue;
                    var kids = new List<(JsonElement, TopicKind)>();
                    foreach (var (name, childKind) in new[]
                             {
                                 ("attached", TopicKind.Attached), ("detached", TopicKind.Detached), ("summary", TopicKind.Summary), ("callout", TopicKind.Callout),
                             })
                    {
                        if (children.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
                            kids.AddRange(list.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object).Select(t => (t, childKind)));
                    }
                    for (var i = kids.Count - 1; i >= 0; i--)
                        stack.Push((kids[i].Item1, topic, depth + 1, kids[i].Item2));
                }
                if (roots.Count <= first)
                    continue;
                var root = roots[first];
                if (first > 0)
                    root.NewPage = true;
                if (root.Text.Trim().Length == 0 && root.Html is null && String(sheet, "title") is { } sheetTitle)
                    root.Text = sheetTitle;
                if (_drawing)
                    _sheets.Add(JsonSheet(sheet, rootTopic, root));
            }
            return roots;
        }
    }

    /// <summary>A content.json sheet for the drawing: structure, background, theme, colours of the branches, relationships.</summary>
    private static Sheet JsonSheet(JsonElement sheet, JsonElement rootTopic, Topic root)
    {
        var drawn = new Sheet(root) { Structure = String(rootTopic, "structureClass") };
        var sheetStyle = JsonStyle(sheet);
        if (sheetStyle.Fill is { Alpha: > 0 } background)
            drawn.Background = background;
        if (sheet.TryGetProperty("theme", out var theme) && theme.ValueKind == JsonValueKind.Object)
        {
            foreach (var part in theme.EnumerateObject())
            {
                if (part.Value.ValueKind != JsonValueKind.Object)
                    continue;
                drawn.Theme[part.Name] = JsonStyle(part.Value);
                if (Properties(part.Value).FirstOrDefault(p => p.Key is "multi-line-colors").Value is { } colors)
                    drawn.BranchColors.AddRange(BranchColors(colors));
            }
        }
        if (sheet.TryGetProperty("relationships", out var relationships) && relationships.ValueKind == JsonValueKind.Array)
        {
            foreach (var relationship in relationships.EnumerateArray())
            {
                if (String(relationship, "end1Id") is not { Length: > 0 } from || String(relationship, "end2Id") is not { Length: > 0 } to)
                    continue;
                Relate(drawn, new MindLink(from, to, String(relationship, "title"), JsonStyle(relationship)));
            }
        }
        return drawn;
    }

    /// <summary>Adds a relationship to the sheet; more than the drawing draws are refused as it would refuse them.</summary>
    private static void Relate(Sheet sheet, MindLink link)
    {
        if (sheet.Relationships.Count >= MindMapDrawing.MaxMarks)
            throw DocumentLimits.Exceeded($"The mind map has more than {DocumentLimits.Count(MindMapDrawing.MaxMarks)} relationships, boundaries and summaries to draw.");
        sheet.Relationships.Add(link);
    }

    /// <summary>XMind's multi-line-colors: colours separated by spaces.</summary>
    private static IEnumerable<SKColor> BranchColors(string colors) =>
        colors.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(64).Select(MindStyle.Color).OfType<SKColor>().Where(c => c.Alpha > 0);

    /// <summary>The string properties of an element's style (style.properties; a theme's parts hold properties themselves).</summary>
    private static IEnumerable<KeyValuePair<string, string>> Properties(JsonElement owner)
    {
        if (owner.ValueKind != JsonValueKind.Object)
            return [];
        if (!owner.TryGetProperty("properties", out var properties)
            && (!owner.TryGetProperty("style", out var style) || style.ValueKind != JsonValueKind.Object || !style.TryGetProperty("properties", out properties)))
            return [];
        if (properties.ValueKind != JsonValueKind.Object)
            return [];
        return properties.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
            .Select(p => KeyValuePair.Create(p.Name, p.Value.GetString()!)).ToList();
    }

    private static MindStyle JsonStyle(JsonElement owner) => MindStyle.FromXMind(Properties(owner));

    private static float? JsonNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var number) && float.IsFinite(number)
            ? number
            : null;

    /// <summary>An XMind range of subtopics, "(0,2)" (from 0, both ends in), or "master" for the whole topic.</summary>
    private static (int Start, int End, bool Master)? Range(string? range)
    {
        var text = range?.Trim();
        if (text is "master")
            return (0, 0, true);
        var parts = text?.Trim('(', ')', '[', ']').Split(',', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 2 } || !int.TryParse(parts[0], out var start) || !int.TryParse(parts[1], out var end) || start < 0 || end < start)
            return null;
        return (start, end, false);
    }

    /// <summary>
    /// True when the arrays and objects of <paramref name="json"/> nest deeper than <paramref name="limit"/> (strings
    /// skipped): told apart from damage before the parser, whose error for either is the same exception.
    /// </summary>
    private static bool NestsDeeper(ReadOnlySpan<byte> json, int limit)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < json.Length; i++)
        {
            var b = json[i];
            if (inString)
            {
                if (b == (byte)'\\')
                    i++;
                else if (b == (byte)'"')
                    inString = false;
                continue;
            }
            switch (b)
            {
                case (byte)'"':
                    inString = true;
                    break;
                case (byte)'[' or (byte)'{':
                    if (++depth > limit)
                        return true;
                    break;
                case (byte)']' or (byte)'}':
                    depth--;
                    break;
            }
        }
        return false;
    }

    private Topic JsonTopic(DocumentPackage package, JsonElement element)
    {
        var topic = new Topic { Id = String(element, "id"), Text = String(element, "title") ?? "" };
        Remember(topic);
        topic.Link = Link(String(element, "href"));
        if (element.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Object)
        {
            if (notes.TryGetProperty("realHTML", out var html) && String(html, "content") is { } content && content.Trim().Length > 0)
                topic.Notes.Add(new Note(NoteKind.Html, content));
            else if (notes.TryGetProperty("plain", out var plain) && String(plain, "content") is { } text && text.Trim().Length > 0)
                topic.Notes.Add(new Note(NoteKind.Plain, text));
        }
        if (element.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array)
        {
            foreach (var label in labels.EnumerateArray())
            {
                if (label.ValueKind == JsonValueKind.String && label.GetString()!.Trim() is { Length: > 0 } text)
                    topic.Labels.Add(text);
            }
        }
        if (element.TryGetProperty("markers", out var markers) && markers.ValueKind == JsonValueKind.Array)
        {
            foreach (var marker in markers.EnumerateArray())
            {
                if (marker.ValueKind == JsonValueKind.Object && Symbol(String(marker, "markerId")) is { } symbol)
                    topic.Symbols.Add(symbol);
            }
        }
        if (element.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object)
        {
            Picture(package, String(image, "src"), topic);
            if (JsonNumber(image, "width") is { } width && JsonNumber(image, "height") is { } height && width > 0 && height > 0)
                topic.PictureSize = new SKSize(Math.Min(width, 2000), Math.Min(height, 2000));
        }

        // What the drawing takes: the look, folding, a floating topic's place, boundaries, summaries, an equation.
        topic.Style = JsonStyle(element);
        topic.Folded = String(element, "branch") is "folded";
        if (element.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.Object
            && JsonNumber(position, "x") is { } px && JsonNumber(position, "y") is { } py)
            topic.Position = new SKPoint(px, py);
        if (element.TryGetProperty("boundaries", out var boundaries) && boundaries.ValueKind == JsonValueKind.Array)
        {
            foreach (var boundary in boundaries.EnumerateArray())
            {
                if (Range(String(boundary, "range")) is (var start, var end, var master))
                    topic.Boundaries.Add(new MindBoundary(start, end, master, String(boundary, "title"), JsonStyle(boundary)));
            }
        }
        if (element.TryGetProperty("summaries", out var summaries) && summaries.ValueKind == JsonValueKind.Array)
        {
            foreach (var summary in summaries.EnumerateArray())
            {
                if (Range(String(summary, "range")) is (var start, var end, false))
                    topic.Summaries.Add(new MindSummary(start, end, String(summary, "topicId")));
            }
        }
        topic.Equation = JsonEquation(element);
        return topic;
    }

    /// <summary>
    /// The TeX of a topic's equation: an extension whose provider names math (XMind's LaTeX equations), its content the
    /// first string found in it (looked through a few levels at most); null without one.
    /// </summary>
    private static string? JsonEquation(JsonElement element)
    {
        if (!element.TryGetProperty("extensions", out var extensions) || extensions.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var extension in extensions.EnumerateArray())
        {
            if (String(extension, "provider") is not { } provider || !provider.Contains("math", StringComparison.OrdinalIgnoreCase)
                || !extension.TryGetProperty("content", out var content))
                continue;
            var queue = new Queue<(JsonElement Value, int Depth)>();
            queue.Enqueue((content, 0));
            while (queue.Count > 0)
            {
                var (value, depth) = queue.Dequeue();
                switch (value.ValueKind)
                {
                    case JsonValueKind.String when value.GetString()!.Trim() is { Length: > 0 and <= 4096 } tex:
                        return tex;
                    case JsonValueKind.Object when depth < 4:
                        foreach (var property in value.EnumerateObject().Take(16))
                            queue.Enqueue((property.Value, depth + 1));
                        break;
                    case JsonValueKind.Array when depth < 4:
                        foreach (var item in value.EnumerateArray().Take(16))
                            queue.Enqueue((item, depth + 1));
                        break;
                }
            }
        }
        return null;
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private List<Topic> XMindXml(DocumentPackage package, System.IO.Compression.ZipArchiveEntry entry)
    {
        XDocument xml;
        try
        {
            xml = package.LoadXml(entry, preserveWhitespace: false);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("The file is not an XMind mind map (its content.xml can't be read).", ex);
        }
        if (xml.Root is not { } root || root.Name.LocalName != "xmap-content")
            throw new InvalidDataException("The file is not an XMind mind map.");
        var styles = XmlStyles(package);

        var roots = new List<Topic>();
        var stack = new Stack<(XElement Element, Topic? Parent, TopicKind Kind)>();
        foreach (var sheet in Children(root, "sheet"))
        {
            var first = roots.Count;
            foreach (var rootTopic in Children(sheet, "topic").Take(1))
                stack.Push((rootTopic, null, TopicKind.Attached));
            while (stack.Count > 0)
            {
                var (element, parent, kind) = stack.Pop();
                var topic = XmlTopic(package, element, styles);
                topic.Kind = kind;
                (parent?.Children ?? roots).Add(topic);
                var kids = Children(element, "children").SelectMany(c => Children(c, "topics"))
                    .OrderBy(t => XmlKind((string?)t.Attribute("type")))
                    .SelectMany(t => Children(t, "topic").Select(topic => (topic, XmlKind((string?)t.Attribute("type"))))).ToList();
                for (var i = kids.Count - 1; i >= 0; i--)
                    stack.Push((kids[i].topic, topic, kids[i].Item2));
            }
            if (roots.Count <= first)
                continue;
            var sheetRoot = roots[first];
            if (first > 0)
                sheetRoot.NewPage = true;
            if (sheetRoot.Text.Trim().Length == 0 && Children(sheet, "title").FirstOrDefault()?.Value is { } sheetTitle)
                sheetRoot.Text = sheetTitle;
            if (_drawing)
                _sheets.Add(XmlSheet(sheet, sheetRoot, styles));
        }
        return roots;
    }

    private static TopicKind XmlKind(string? type) => type switch
    {
        "detached" => TopicKind.Detached,
        "summary" => TopicKind.Summary,
        "callout" => TopicKind.Callout,
        _ => TopicKind.Attached,
    };

    /// <summary>XMind 8's styles.xml: each style's properties by id, and a theme's default style id by topic class.</summary>
    private sealed record XmlStyleSheet(Dictionary<string, MindStyle> Styles, Dictionary<string, Dictionary<string, string>> Themes);

    private static XmlStyleSheet XmlStyles(DocumentPackage package)
    {
        var sheet = new XmlStyleSheet(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        if (package.GetEntry("styles.xml") is not { } entry)
            return sheet;
        XDocument xml;
        try
        {
            xml = package.LoadXml(entry, preserveWhitespace: false);
        }
        catch (XmlException)
        {
            return sheet; // damaged styles: the map is drawn with the drawing's own
        }
        foreach (var style in xml.Descendants().Where(e => e.Name.LocalName == "style"))
        {
            if ((string?)style.Attribute("id") is not { Length: > 0 } id)
                continue;
            var properties = style.Elements().Where(e => e.Name.LocalName.EndsWith("-properties", StringComparison.Ordinal)).ToList();
            sheet.Styles[id] = MindStyle.FromXMind(properties.SelectMany(p => p.Attributes())
                .Select(a => KeyValuePair.Create(a.Name.LocalName, a.Value)));
            var defaults = properties.SelectMany(p => p.Elements().Where(e => e.Name.LocalName == "default-style"))
                .Select(d => ((string?)d.Attribute("style-family"), (string?)d.Attribute("style-id")))
                .Where(d => d.Item1 is { Length: > 0 } && d.Item2 is { Length: > 0 })
                .ToList();
            if (defaults.Count > 0)
                sheet.Themes[id] = defaults.GroupBy(d => d.Item1!).ToDictionary(g => g.Key, g => g.First().Item2!, StringComparer.Ordinal);
        }
        return sheet;
    }

    private static MindStyle XmlStyle(XElement element, XmlStyleSheet styles) =>
        (string?)element.Attribute("style-id") is { } id && styles.Styles.TryGetValue(id, out var style) ? style : MindStyle.Empty;

    /// <summary>A content.xml sheet for the drawing: structure, background, the theme's styles, relationships.</summary>
    private static Sheet XmlSheet(XElement sheet, Topic root, XmlStyleSheet styles)
    {
        var rootTopic = Children(sheet, "topic").First();
        var drawn = new Sheet(root) { Structure = (string?)rootTopic.Attribute("structure-class") };
        if (XmlStyle(sheet, styles).Fill is { Alpha: > 0 } background)
            drawn.Background = background;
        if ((string?)sheet.Attribute("theme") is { } themeId && styles.Themes.TryGetValue(themeId, out var theme))
        {
            foreach (var (family, id) in theme)
            {
                if (styles.Styles.TryGetValue(id, out var style))
                    drawn.Theme[family] = style;
            }
        }
        foreach (var relationship in Children(sheet, "relationships").SelectMany(r => Children(r, "relationship")))
        {
            if ((string?)relationship.Attribute("end1") is not { Length: > 0 } from || (string?)relationship.Attribute("end2") is not { Length: > 0 } to)
                continue;
            Relate(drawn, new MindLink(from, to, Children(relationship, "title").FirstOrDefault()?.Value, XmlStyle(relationship, styles)));
        }
        return drawn;
    }

    private static IEnumerable<XElement> Children(XElement parent, string name) => parent.Elements().Where(e => e.Name.LocalName == name);

    private Topic XmlTopic(DocumentPackage package, XElement element, XmlStyleSheet styles)
    {
        var topic = new Topic
        {
            Id = (string?)element.Attribute("id"),
            Text = Children(element, "title").FirstOrDefault()?.Value ?? "",
        };
        Remember(topic);
        topic.Link = Link((string?)element.Attributes().FirstOrDefault(a => a.Name.LocalName == "href"));
        if (Children(element, "notes").FirstOrDefault() is { } notes && Children(notes, "plain").FirstOrDefault()?.Value is { } plain
            && plain.Trim().Length > 0)
            topic.Notes.Add(new Note(NoteKind.Plain, plain));
        foreach (var label in Children(element, "labels").SelectMany(l => Children(l, "label")))
        {
            if (label.Value.Trim() is { Length: > 0 } text)
                topic.Labels.Add(text);
        }
        foreach (var marker in Children(element, "marker-refs").SelectMany(m => Children(m, "marker-ref")))
        {
            if (Symbol((string?)marker.Attribute("marker-id")) is { } symbol)
                topic.Symbols.Add(symbol);
        }
        foreach (var img in Children(element, "img"))
        {
            Picture(package, (string?)img.Attributes().FirstOrDefault(a => a.Name.LocalName == "src"), topic);
            if (Number((string?)img.Attributes().FirstOrDefault(a => a.Name.LocalName == "width")) is { } width
                && Number((string?)img.Attributes().FirstOrDefault(a => a.Name.LocalName == "height")) is { } height && width > 0 && height > 0)
                topic.PictureSize = new SKSize(Math.Min(width, 2000), Math.Min(height, 2000));
        }

        topic.Style = XmlStyle(element, styles);
        topic.Folded = (string?)element.Attribute("branch") is "folded";
        if (Children(element, "position").FirstOrDefault() is { } position
            && Number((string?)position.Attributes().FirstOrDefault(a => a.Name.LocalName == "x")) is { } px
            && Number((string?)position.Attributes().FirstOrDefault(a => a.Name.LocalName == "y")) is { } py)
            topic.Position = new SKPoint(px, py);
        foreach (var boundary in Children(element, "boundaries").SelectMany(b => Children(b, "boundary")))
        {
            if (Range((string?)boundary.Attribute("range")) is (var start, var end, var master))
                topic.Boundaries.Add(new MindBoundary(start, end, master, Children(boundary, "title").FirstOrDefault()?.Value, XmlStyle(boundary, styles)));
        }
        foreach (var summary in Children(element, "summaries").SelectMany(s => Children(s, "summary")))
        {
            if (Range((string?)summary.Attribute("range")) is (var start, var end, false))
                topic.Summaries.Add(new MindSummary(start, end, (string?)summary.Attribute("topic-id")));
        }
        return topic;
    }

    /// <summary>Unpacks a topic's picture (xap:resources/…, xap:attachments/…) into the media folder.</summary>
    private void Picture(DocumentPackage package, string? source, Topic topic)
    {
        if (source is null || !source.StartsWith("xap:", StringComparison.OrdinalIgnoreCase))
            return;
        var name = source[4..].TrimStart('/');
        var extension = DocumentPackage.SafeExtension(name);
        if (!PictureExtensions.Contains(extension) || package.GetEntry(name) is not { } entry)
            return;
        _budget.Object();
        Directory.CreateDirectory(_media);
        var file = Path.Combine(_media, $"xmind-{++_pictures}{extension}");
        if (package.ExtractPicture(entry, file))
            topic.Pictures.Add(file);
    }

    // ───────────────────────── Shared: links, icons ─────────────────────────

    private void Remember(Topic topic)
    {
        if (topic.Id is { Length: > 0 } id)
            _ids.Add(id);
    }

    /// <summary>A link to keep: the web, mail, a file, or another topic ("#ID", XMind's "xmind:#ID"); null for others.</summary>
    private string? Link(string? raw)
    {
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw))
            return null;
        var topic = raw.StartsWith('#') ? raw[1..] : raw.StartsWith("xmind:#", StringComparison.OrdinalIgnoreCase) ? raw[7..] : null;
        if (topic is not null)
        {
            if (topic.Length == 0)
                return null;
            _targets.Add(topic);
            return "#" + topic;
        }
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return uri.Scheme is "http" or "https" or "mailto" or "ftp" or "file" ? raw : null;
        // A relative path (another map, a document next to it); anything with a scheme of its own is left out.
        return raw.Contains(':', StringComparison.Ordinal) ? null : raw;
    }

    /// <summary>The symbol of a FreeMind icon or XMind marker that has an obvious one, else null.</summary>
    internal static string? Symbol(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        name = name.Trim().ToLowerInvariant();
        // Priorities: XMind "priority-1" … "priority-9", FreeMind "full-1" … "full-9".
        foreach (var prefix in new[] { "priority-", "full-" })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length == prefix.Length + 1 && name[^1] is >= '1' and <= '9')
                return ((char)('①' + (name[^1] - '1'))).ToString();
        }
        return name switch
        {
            "task-done" or "button_ok" or "symbol-right" => "✓",
            "button_cancel" or "symbol-wrong" => "×",
            "task-half" or "task-quarter" or "task-3quar" or "task-start" or "task-oct" or "task-3oct" or "task-5oct" or "task-7oct" => "◑",
            "help" or "symbol-question" => "?",
            "yes" or "symbol-exclam" => "!",
            "messagebox_warning" or "symbol-attention" => "⚠",
            "bookmark" or "star" => "★",
            "back" or "arrow-left" => "←",
            "forward" or "arrow-right" => "→",
            "up" or "arrow-up" => "↑",
            "down" or "arrow-down" => "↓",
            "symbol-plus" => "+",
            "symbol-minus" => "−",
            _ when name.StartsWith("star-", StringComparison.Ordinal) => "★",
            _ => null,
        };
    }

    // ───────────────────────── The outline ─────────────────────────

    private HDocument Build(List<Topic> roots)
    {
        var stack = new Stack<(Topic Topic, int Depth)>();
        for (var i = roots.Count - 1; i >= 0; i--)
            stack.Push((roots[i], 0));
        while (stack.Count > 0)
        {
            var (topic, depth) = stack.Pop();
            Emit(topic, depth);
            for (var i = topic.Children.Count - 1; i >= 0; i--)
                stack.Push((topic.Children[i], depth + 1));
        }

        var section = new HSection();
        section.Blocks.AddRange(_blocks);
        if (section.Blocks.FirstOrDefault() is HParagraph first)
            first.PageBreakBefore = false;
        // The central topic (of the first sheet) is the title.
        var title = roots.Count == 0 ? ""
            : roots[0].Html is null ? Plain(roots[0].Text)
            : string.Join(' ', TitleInlines(roots[0]).Inlines.OfType<HText>().Select(t => t.Text.Trim()).Where(t => t.Length > 0));
        var document = new HDocument { Title = title.Length > 0 ? title : null };
        document.Sections.Add(section);
        return document;
    }

    private static string Plain(string text) => string.Join(' ', MarkupBlocks.Lines(text).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    private void Emit(Topic topic, int depth)
    {
        var heading = depth < _headingLevels;
        HListRef? item = null;
        if (heading)
        {
            _list = null; // a list after the next heading is a list of its own
        }
        else
        {
            _list ??= MarkupBlocks.Numbering(ordered: false);
            item = new HListRef(_list, Math.Min(depth - _headingLevels, ListLevels - 1), Numbered: true);
        }
        var below = item is null ? null : item with { Numbered = false };

        var (inlines, pictures) = TitleInlines(topic);
        _pageBreak |= topic.NewPage;
        var line = new HParagraph { HeadingLevel = heading ? Math.Min(depth + 1, 9) : 0, List = item };
        if (topic.Id is { } id && _targets.Contains(id))
            line.Inlines.Add(new HBookmark(Bookmark(id)));
        if (topic.Symbols.Count > 0 && inlines.Count > 0)
            line.Inlines.Add(new HText(string.Concat(topic.Symbols) + " ", default));
        if (topic.Link is { } target && (!target.StartsWith('#') || _ids.Contains(target[1..])))
        {
            var link = new HLink(target.StartsWith('#') ? "#" + Bookmark(target[1..]) : target);
            link.Content.AddRange(inlines.Count > 0 ? inlines : [new HText(target, default)]);
            line.Inlines.Add(link);
        }
        else
        {
            line.Inlines.AddRange(inlines);
        }
        // A topic without text has no line of its own (as OPML's outlines); what is under it stays under it.
        if (line.Inlines.Any(i => i is not HBookmark))
        {
            _budget.Paragraph();
            Add(line);
        }

        foreach (var picture in topic.Pictures.Concat(pictures))
        {
            _budget.Paragraph();
            var paragraph = new HParagraph { List = below };
            paragraph.Inlines.Add(new HImage(picture));
            Add(paragraph);
        }
        if (topic.Labels.Count > 0)
            Line("Labels: " + string.Join(", ", topic.Labels), below);
        foreach (var (name, value) in topic.Attributes)
            Line(value.Length > 0 ? $"{name}: {value}" : name, below);
        foreach (var note in topic.Notes)
            NoteBlocks(note, below);
    }

    private void Line(string text, HListRef? below)
    {
        _budget.Paragraph();
        var paragraph = new HParagraph { List = below };
        paragraph.Inlines.Add(new HText(text, default));
        Add(paragraph);
    }

    /// <summary>Adds a block; the first one of a sheet after the first starts a new page.</summary>
    private void Add(HBlock block)
    {
        if (_pageBreak && block is HParagraph paragraph)
        {
            paragraph.PageBreakBefore = true;
            _pageBreak = false;
        }
        _blocks.Add(block);
    }

    /// <summary>A bookmark name for a topic id that every writer takes (letters, digits, '_' and '-').</summary>
    private static string Bookmark(string id)
    {
        var name = new StringBuilder("topic-", id.Length + 6);
        foreach (var c in id.Length > 64 ? id[..64] : id)
            name.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        return name.ToString();
    }

    /// <summary>The text of a topic's line, and the pictures its rich text holds (they follow the line).</summary>
    private (List<HInline> Inlines, List<string> Pictures) TitleInlines(Topic topic)
    {
        var inlines = new List<HInline>();
        var pictures = new List<string>();
        if (topic.Html is { } html)
        {
            foreach (var paragraph in Html(html).OfType<HParagraph>())
            {
                var content = paragraph.Inlines.Where(i => i is not HBookmark).ToList();
                pictures.AddRange(content.OfType<HImage>().Select(i => i.Path));
                content.RemoveAll(i => i is HImage);
                if (content.Count == 0)
                    continue;
                if (inlines.Count > 0)
                    inlines.Add(new HLineBreak(default));
                inlines.AddRange(content);
            }
            return (inlines, pictures);
        }
        var lines = MarkupBlocks.Lines(topic.Text).Split('\n').Select(l => l.Trim()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        while (lines.Count > 0 && lines[0].Length == 0)
            lines.RemoveAt(0);
        foreach (var text in lines)
        {
            if (inlines.Count > 0)
                inlines.Add(new HLineBreak(default));
            if (text.Length > 0)
                inlines.Add(new HText(text, default));
        }
        return (inlines, pictures);
    }

    private List<HBlock> Html(string html) =>
        HtmlReader.Parse(html, new HtmlReadOptions { BaseFolder = _baseFolder, MediaFolder = _media }, xhtml: html.StartsWith('<')).Blocks;

    /// <summary>A note's blocks after its topic: plain lines, Markdown or HTML, headings in it made plain paragraphs.</summary>
    private void NoteBlocks(Note note, HListRef? below)
    {
        var content = MarkupBlocks.Lines(note.Content).Trim('\n');
        if (content.Trim().Length == 0)
            return;
        if (note.Kind != NoteKind.Html && _budget.Paragraphs + (long)MarkupBlocks.LineCount(content) > _budget.Limits.MaxParagraphs)
            throw DocumentLimits.Exceeded($"The document has more than {DocumentLimits.Count(_budget.Limits.MaxParagraphs)} paragraphs.");
        List<HBlock> blocks;
        switch (note.Kind)
        {
            case NoteKind.Html:
                blocks = Html(note.Content);
                MarkupBlocks.Count(blocks, _budget);
                break;
            case NoteKind.Markdown:
                blocks = [.. MarkdownReader.Read(content, _baseFolder).Sections.SelectMany(s => s.Blocks)];
                MarkupBlocks.Count(blocks, _budget);
                break;
            default:
                blocks = [];
                foreach (var text in content.Split('\n'))
                {
                    _budget.Paragraph();
                    var paragraph = new HParagraph();
                    if (text.Length > 0)
                        paragraph.Inlines.Add(new HText(text, default));
                    blocks.Add(paragraph);
                }
                break;
        }
        foreach (var paragraph in blocks.OfType<HParagraph>())
        {
            // The outline is the topics': a heading in a note would take a place in it.
            paragraph.HeadingLevel = 0;
            paragraph.PageBreakBefore = false;
            if (below is not null && paragraph.List is null)
                paragraph.List = below;
        }
        foreach (var block in blocks)
            Add(block);
    }
}
