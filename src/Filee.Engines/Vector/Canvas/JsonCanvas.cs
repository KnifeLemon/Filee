// Obsidian's visual boards (.canvas): JSON Canvas 1.0 (jsoncanvas.org, MIT), read by Filee itself. The file is one JSON
// object with a "nodes" array (cards: Markdown text, a file of the vault, a web link, or a group framing other cards) and
// an "edges" array (lines between two cards, from a side to a side, with arrow heads and a label). Nodes come in z-order,
// the first one at the bottom.
//
// Read defensively: the file's size, the number of cards and lines, their coordinates and the length of their text are
// checked before anything is laid out, and a .canvas that isn't a JSON Canvas (no "nodes" or "edges" array) is refused
// with a reason. Cards of a type the specification doesn't have yet are left out, and so are lines to a card that isn't
// there, as Obsidian does.

using System.Text.Json;
using Filee.Core.Conversion;

namespace Filee.Engines.Vector.Canvas;

/// <summary>What a card of the canvas holds.</summary>
internal enum CanvasNodeKind
{
    Text,
    File,
    Link,
    Group,
}

/// <summary>The side of a card a line starts or ends at; <see cref="Auto"/> when the file doesn't say.</summary>
internal enum CanvasSide
{
    Auto,
    Top,
    Right,
    Bottom,
    Left,
}

/// <summary>A card of the canvas, in canvas pixels.</summary>
/// <param name="Content">The Markdown of a text card, the vault path of a file card, the URL of a link card, or a group's label.</param>
/// <param name="Subpath">The heading or block a file card shows (<c>#Heading</c>), or null.</param>
/// <param name="Background">The vault path of a group's background picture, or null.</param>
/// <param name="BackgroundStyle"><c>cover</c>, <c>ratio</c> or <c>repeat</c> (a group's background), or null.</param>
internal sealed record CanvasNode(
    string Id, CanvasNodeKind Kind, double X, double Y, double Width, double Height, string? Color, string Content,
    string? Subpath = null, string? Background = null, string? BackgroundStyle = null)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + Width / 2;

    public double CenterY => Y + Height / 2;
}

/// <summary>A line between two cards.</summary>
internal sealed record CanvasEdge(
    string Id, CanvasNode From, CanvasSide FromSide, bool FromArrow, CanvasNode To, CanvasSide ToSide, bool ToArrow,
    string? Color, string? Label);

/// <summary>A JSON Canvas board: its cards in z-order and its lines.</summary>
internal sealed record JsonCanvas(IReadOnlyList<CanvasNode> Nodes, IReadOnlyList<CanvasEdge> Edges)
{
    /// <summary>Largest file read: canvases are small (a busy board is a few hundred kB), so more is not a canvas.</summary>
    public const long MaxFileBytes = 32L * 1024 * 1024;

    /// <summary>Most cards and lines read; Obsidian itself slows down long before.</summary>
    public const int MaxNodes = 10_000;

    public const int MaxEdges = 20_000;

    /// <summary>Largest coordinate and card size, in canvas pixels.</summary>
    public const double MaxCoordinate = 1_000_000;

    public const double MaxNodeSide = 100_000;

    /// <summary>Largest board drawn, in canvas pixels a side (about 50 m in a PDF at 96 dpi).</summary>
    public const double MaxExtent = 200_000;

    /// <summary>Longest text of one card; more is cut, since the card can't show it anyway.</summary>
    public const int MaxTextLength = 100_000;

    /// <summary>
    /// Longest file path, subpath, URL or background kept; more is cut. Paths over CanvasFiles.MaxPathLength are not
    /// looked for at all, and a card shows only the beginning of an address.
    /// </summary>
    public const int MaxReferenceLength = 4096;

    private static readonly JsonDocumentOptions Options = new()
    {
        MaxDepth = 64,
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads the canvas file at <paramref name="path"/>.</summary>
    /// <exception cref="ConversionRefusedException">The file is not a JSON Canvas, has no cards, or is too large.</exception>
    /// <exception cref="InvalidDataException">The file is a damaged JSON Canvas.</exception>
    public static JsonCanvas Read(string path, CancellationToken ct = default)
    {
        var length = new FileInfo(path).Length;
        if (length > MaxFileBytes)
            throw new ConversionRefusedException("error.canvas_too_large", "32 MB");
        return Parse(File.ReadAllBytes(path), ct);
    }

    /// <summary>Reads a canvas from its bytes (UTF-8 JSON, with or without a byte order mark).</summary>
    public static JsonCanvas Parse(byte[] data, CancellationToken ct = default)
    {
        if (data.LongLength > MaxFileBytes)
            throw new ConversionRefusedException("error.canvas_too_large", "32 MB");
        var json = data.AsMemory();
        if (json.Span.StartsWith("\xEF\xBB\xBF"u8))
            json = json[3..];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException ex)
        {
            // A board cut short (a sync conflict, a full disk) still starts like one; anything else isn't a canvas.
            if (LooksLikeCanvas(json.Span))
                throw new InvalidDataException("The canvas file is damaged or cut short: " + ex.Message, ex);
            throw new ConversionRefusedException("error.canvas_not_canvas");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !(HasArray(root, "nodes") || HasArray(root, "edges"))
                || HasWrongType(root, "nodes") || HasWrongType(root, "edges"))
                throw new ConversionRefusedException("error.canvas_not_canvas");

            var nodes = ReadNodes(root, ct);
            if (nodes.Count == 0)
                throw new ConversionRefusedException("error.canvas_empty");
            var edges = ReadEdges(root, nodes, ct);
            CheckExtent(nodes);
            return new JsonCanvas(nodes, edges);
        }
    }

    /// <summary>The cards that are not groups, in z-order.</summary>
    public IEnumerable<CanvasNode> Cards => Nodes.Where(n => n.Kind != CanvasNodeKind.Group);

    /// <summary>The groups, in z-order.</summary>
    public IEnumerable<CanvasNode> Groups => Nodes.Where(n => n.Kind == CanvasNodeKind.Group);

    /// <summary>
    /// The smallest group <paramref name="node"/> sits in (its centre inside the group's frame, as a card dragged into a
    /// group in Obsidian), or null.
    /// </summary>
    public CanvasNode? GroupOf(CanvasNode node) =>
        Groups.Where(g => !ReferenceEquals(g, node) && g.Width * g.Height > node.Width * node.Height
                && node.CenterX >= g.X && node.CenterX <= g.Right && node.CenterY >= g.Y && node.CenterY <= g.Bottom)
            .MinBy(g => g.Width * g.Height);

    private static bool HasArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array;

    /// <summary>"nodes" or "edges" there but not an array (or null): another program's JSON.</summary>
    private static bool HasWrongType(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null);

    /// <summary>Whether the start of the text is a JSON object whose first key is "nodes" or "edges".</summary>
    private static bool LooksLikeCanvas(ReadOnlySpan<byte> json)
    {
        json = json.TrimStart(" \t\r\n"u8);
        if (json.IsEmpty || json[0] != (byte)'{')
            return false;
        json = json[1..].TrimStart(" \t\r\n"u8);
        return json.StartsWith("\"nodes\""u8) || json.StartsWith("\"edges\""u8);
    }

    private static List<CanvasNode> ReadNodes(JsonElement root, CancellationToken ct)
    {
        var nodes = new List<CanvasNode>();
        if (!root.TryGetProperty("nodes", out var array) || array.ValueKind != JsonValueKind.Array)
            return nodes;
        var count = array.GetArrayLength();
        if (count > MaxNodes)
            throw new ConversionRefusedException("error.canvas_too_large", $"{count:N0} cards, at most {MaxNodes:N0}");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            index++;
            if (element.ValueKind != JsonValueKind.Object)
                throw Damaged($"card {index} is not an object");
            var id = RequiredString(element, "id", "card", index);
            var type = RequiredString(element, "type", "card", index);
            CanvasNodeKind? kind = type switch
            {
                "text" => CanvasNodeKind.Text,
                "file" => CanvasNodeKind.File,
                "link" => CanvasNodeKind.Link,
                "group" => CanvasNodeKind.Group,
                _ => null,
            };
            if (kind is null)
                continue; // a card type of a later version of the specification (or a plugin's): not drawn
            if (!ids.Add(id))
                throw Damaged($"card {index} has the same id as an earlier card ({Shorten(id)})");

            var x = Number(element, "x", index, MaxCoordinate);
            var y = Number(element, "y", index, MaxCoordinate);
            var width = Number(element, "width", index, MaxNodeSide);
            var height = Number(element, "height", index, MaxNodeSide);
            if (width <= 0 || height <= 0)
                throw Damaged($"card {index} ({Shorten(id)}) has no size");
            var color = OptionalString(element, "color");
            var node = kind.Value switch
            {
                CanvasNodeKind.Text => new CanvasNode(id, kind.Value, x, y, width, height, color,
                    Cut(RequiredString(element, "text", "card", index))),
                CanvasNodeKind.File => new CanvasNode(id, kind.Value, x, y, width, height, color,
                    Short(RequiredString(element, "file", "card", index)), Subpath: Short(OptionalString(element, "subpath"))),
                CanvasNodeKind.Link => new CanvasNode(id, kind.Value, x, y, width, height, color,
                    Short(RequiredString(element, "url", "card", index))),
                _ => new CanvasNode(id, kind.Value, x, y, width, height, color, Cut(OptionalString(element, "label") ?? ""),
                    Background: Short(OptionalString(element, "background")), BackgroundStyle: OptionalString(element, "backgroundStyle")),
            };
            nodes.Add(node);
        }
        return nodes;
    }

    private static List<CanvasEdge> ReadEdges(JsonElement root, List<CanvasNode> nodes, CancellationToken ct)
    {
        var edges = new List<CanvasEdge>();
        if (!root.TryGetProperty("edges", out var array) || array.ValueKind != JsonValueKind.Array)
            return edges;
        var count = array.GetArrayLength();
        if (count > MaxEdges)
            throw new ConversionRefusedException("error.canvas_too_large", $"{count:N0} lines, at most {MaxEdges:N0}");

        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            index++;
            if (element.ValueKind != JsonValueKind.Object)
                throw Damaged($"line {index} is not an object");
            var id = RequiredString(element, "id", "line", index);
            var from = RequiredString(element, "fromNode", "line", index);
            var to = RequiredString(element, "toNode", "line", index);
            // Obsidian drops a line whose card is gone; so does Filee (also lines to cards of an unknown type).
            if (!byId.TryGetValue(from, out var fromNode) || !byId.TryGetValue(to, out var toNode))
                continue;
            edges.Add(new CanvasEdge(id,
                fromNode, Side(OptionalString(element, "fromSide")), OptionalString(element, "fromEnd") == "arrow",
                toNode, Side(OptionalString(element, "toSide")), OptionalString(element, "toEnd") != "none",
                OptionalString(element, "color"), OptionalString(element, "label") is { Length: > 0 } label ? Cut(label) : null));
        }
        return edges;
    }

    private static void CheckExtent(List<CanvasNode> nodes)
    {
        double left = nodes.Min(n => n.X), top = nodes.Min(n => n.Y);
        double right = nodes.Max(n => n.Right), bottom = nodes.Max(n => n.Bottom);
        if (right - left > MaxExtent || bottom - top > MaxExtent)
            throw new ConversionRefusedException("error.canvas_too_large",
                $"{right - left:N0} × {bottom - top:N0} px, at most {MaxExtent:N0} a side");
    }

    private static CanvasSide Side(string? side) => side switch
    {
        "top" => CanvasSide.Top,
        "right" => CanvasSide.Right,
        "bottom" => CanvasSide.Bottom,
        "left" => CanvasSide.Left,
        _ => CanvasSide.Auto,
    };

    private static string RequiredString(JsonElement element, string name, string what, int index)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw Damaged($"{what} {index} has no \"{name}\"");
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A coordinate or size: a finite number (integers in the specification; some tools write decimals).</summary>
    private static double Number(JsonElement element, string name, int index, double limit)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw Damaged($"card {index} has no \"{name}\"");
        if (Math.Abs(number) > limit)
            throw new ConversionRefusedException("error.canvas_too_large", $"card {index}: {name} {number:N0}, at most {limit:N0}");
        return number;
    }

    private static string Cut(string text) => text.Length > MaxTextLength ? text[..MaxTextLength] : text;

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(text))]
    private static string? Short(string? text) => text is { Length: > MaxReferenceLength } ? text[..MaxReferenceLength] : text;

    private static string Shorten(string text) => text.Length > 40 ? text[..40] + "…" : text;

    private static InvalidDataException Damaged(string what) => new($"The canvas file is damaged: {what}.");
}
