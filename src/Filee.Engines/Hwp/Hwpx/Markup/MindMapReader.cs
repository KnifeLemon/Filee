// Mind maps → HDocument as an outline (#1543, #1004): the map isn't drawn, its topics become the document's structure.
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
// recursion, and every paragraph and picture counts against DocumentLimits (DocumentBudget).

using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Infrastructure;

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

    private MindMapReader(string baseFolder, string media, DocumentLimits limits, bool headingsOnly)
    {
        _baseFolder = baseFolder;
        _media = media;
        _budget = new DocumentBudget(limits);
        _headingLevels = headingsOnly ? int.MaxValue : HeadingLevels;
    }

    /// <summary>A topic as the map holds it, before it becomes paragraphs.</summary>
    private sealed class Topic
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
    }

    private enum NoteKind
    {
        Plain,
        Markdown,
        Html,
    }

    private readonly record struct Note(NoteKind Kind, string Content);

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

    // ───────────────────────── FreeMind and Freeplane ─────────────────────────

    internal static HDocument ReadFreeMind(byte[] bytes, string baseFolder, string media, DocumentLimits limits, bool headingsOnly = false)
    {
        // Objective-C++ source shares the extension; a mind map is XML, so anything else is read as the text it is.
        if (!LooksLikeXml(bytes))
            return DocumentReaders.TextDocument(bytes);
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

        var reader = new MindMapReader(baseFolder, media, limits, headingsOnly);
        var roots = new List<Topic>();
        // Node elements and the topic they go into (null for the map's own), walked without recursion.
        var stack = new Stack<(XElement Element, Topic? Parent)>();
        foreach (var node in Nodes(root).Reverse())
            stack.Push((node, null));
        while (stack.Count > 0)
        {
            var (element, parent) = stack.Pop();
            var topic = reader.FreeMindTopic(element);
            (parent?.Children ?? roots).Add(topic);
            foreach (var child in Nodes(element).Reverse())
                stack.Push((child, topic));
        }
        return reader.Build(roots);
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

        foreach (var child in node.Elements())
        {
            switch (child.Name.LocalName)
            {
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
        return topic;
    }

    // ───────────────────────── XMind ─────────────────────────

    internal static HDocument ReadXMind(Stream stream, string baseFolder, string media, DocumentLimits limits, bool headingsOnly = false)
    {
        using var package = DocumentPackage.Open(stream, limits, () => new InvalidDataException("The file is not an XMind mind map (it is not a ZIP package)."));
        foreach (var name in new[] { "META-INF/manifest.xml", "manifest.json" })
        {
            if (package.GetEntry(name) is { } manifest && package.ReadBytes(manifest, 4 * 1024 * 1024) is { } bytes
                && bytes.AsSpan().IndexOf("encryption-data"u8) >= 0)
                throw new ConversionRefusedException(PasswordKey);
        }

        var reader = new MindMapReader(baseFolder, media, limits, headingsOnly);
        List<Topic> roots;
        if (package.GetEntry("content.json") is { } json)
            roots = reader.XMindJson(package, json);
        else if (package.GetEntry("content.xml") is { } xml)
            roots = reader.XMindXml(package, xml);
        else
            throw new InvalidDataException("The file is not an XMind mind map (it has no content.json or content.xml).");
        return reader.Build(roots);
    }

    private List<Topic> XMindJson(DocumentPackage package, System.IO.Compression.ZipArchiveEntry entry)
    {
        var limits = _budget.Limits;
        var bytes = package.ReadBytes(entry, limits.MaxPartBytes)
                    ?? throw DocumentLimits.Exceeded($"content.json is larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
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
            var stack = new Stack<(JsonElement Element, Topic? Parent, int Depth)>();
            foreach (var sheet in sheets.EnumerateArray())
            {
                if (sheet.ValueKind != JsonValueKind.Object || !sheet.TryGetProperty("rootTopic", out var rootTopic) || rootTopic.ValueKind != JsonValueKind.Object)
                    continue;
                var first = roots.Count;
                stack.Push((rootTopic, null, 0));
                while (stack.Count > 0)
                {
                    var (element, parent, depth) = stack.Pop();
                    if (depth > limits.MaxXmlDepth)
                        throw DocumentLimits.Exceeded($"The mind map nests topics more than {limits.MaxXmlDepth} levels deep.");
                    var topic = JsonTopic(package, element);
                    (parent?.Children ?? roots).Add(topic);
                    if (!element.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Object)
                        continue;
                    var kids = new List<JsonElement>();
                    foreach (var kind in new[] { "attached", "detached", "summary", "callout" })
                    {
                        if (children.TryGetProperty(kind, out var list) && list.ValueKind == JsonValueKind.Array)
                            kids.AddRange(list.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object));
                    }
                    for (var i = kids.Count - 1; i >= 0; i--)
                        stack.Push((kids[i], topic, depth + 1));
                }
                if (roots.Count > first && first > 0)
                    roots[first].NewPage = true;
                if (roots.Count > first && roots[first].Text.Trim().Length == 0 && roots[first].Html is null
                    && String(sheet, "title") is { } sheetTitle)
                    roots[first].Text = sheetTitle;
            }
            return roots;
        }
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
            Picture(package, String(image, "src"), topic);
        return topic;
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

        var roots = new List<Topic>();
        var stack = new Stack<(XElement Element, Topic? Parent)>();
        foreach (var sheet in Children(root, "sheet"))
        {
            var first = roots.Count;
            foreach (var rootTopic in Children(sheet, "topic").Take(1))
                stack.Push((rootTopic, null));
            while (stack.Count > 0)
            {
                var (element, parent) = stack.Pop();
                var topic = XmlTopic(package, element);
                (parent?.Children ?? roots).Add(topic);
                var kids = Children(element, "children").SelectMany(c => Children(c, "topics")).SelectMany(t => Children(t, "topic")).ToList();
                for (var i = kids.Count - 1; i >= 0; i--)
                    stack.Push((kids[i], topic));
            }
            if (roots.Count > first && first > 0)
                roots[first].NewPage = true;
            if (roots.Count > first && roots[first].Text.Trim().Length == 0 && Children(sheet, "title").FirstOrDefault()?.Value is { } sheetTitle)
                roots[first].Text = sheetTitle;
        }
        return roots;
    }

    private static IEnumerable<XElement> Children(XElement parent, string name) => parent.Elements().Where(e => e.Name.LocalName == name);

    private Topic XmlTopic(DocumentPackage package, XElement element)
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
            Picture(package, (string?)img.Attributes().FirstOrDefault(a => a.Name.LocalName == "src"), topic);
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
