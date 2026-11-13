// A JSON Canvas board as a Markdown document, for reading it in order or taking its text elsewhere (DOCX, HTML, plain
// text through the Markdown engines): the board's name as the title, each group as a section (a group inside another
// as a subsection) with its cards from top to bottom and left to right, the cards outside groups first, and the lines
// between cards listed at the end ("A → B: label"). A text card keeps its Markdown, its headings moved below the
// section's; a file card is a link to the file (a picture beside the canvas is copied next to the Markdown file, as
// Filee does for every Markdown it writes); a link card is its address.

using System.Text;
using System.Text.RegularExpressions;
using Filee.Engines.Text;

namespace Filee.Engines.Vector.Canvas;

internal static partial class CanvasMarkdown
{
    /// <summary>Longest name a card gets in the list of lines.</summary>
    private const int NameLength = 60;

    /// <param name="title">The board's name (the canvas file's name).</param>
    /// <param name="pictures">Where pictures beside the canvas are copied to, or null to only link them.</param>
    public static string Write(JsonCanvas canvas, string title, CanvasFiles files, MarkdownPictures? pictures, CancellationToken ct)
    {
        var markdown = new StringBuilder();
        markdown.Append("# ").Append(OneLine(title)).Append("\n\n");

        // Card ids are unique (JsonCanvas checks), so the group of each card is named by its id.
        var children = canvas.Nodes.ToLookup(n => canvas.GroupOf(n)?.Id);
        Section(markdown, null, 1, children, files, pictures, ct);

        if (canvas.Edges.Count > 0)
        {
            markdown.Append("## Connections\n\n");
            foreach (var edge in canvas.Edges)
            {
                ct.ThrowIfCancellationRequested();
                var arrow = (edge.FromArrow, edge.ToArrow) switch
                {
                    (true, true) => "↔",
                    (false, true) => "→",
                    (true, false) => "←",
                    _ => "—",
                };
                markdown.Append("- **").Append(Escape(Name(edge.From))).Append("** ").Append(arrow)
                    .Append(" **").Append(Escape(Name(edge.To))).Append("**");
                if (edge.Label is { } label)
                    markdown.Append(": ").Append(Escape(OneLine(label)));
                markdown.Append('\n');
            }
        }
        return markdown.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>
    /// Deepest group nesting written as nested sections; groups deeper than this are listed one after another at the
    /// last level, without recursion (10,000 groups one inside the other fit inside the budgets).
    /// </summary>
    private const int MaxGroupDepth = 16;

    private static void Section(StringBuilder markdown, CanvasNode? group, int level, ILookup<string?, CanvasNode> children,
        CanvasFiles files, MarkdownPictures? pictures, CancellationToken ct)
    {
        var inside = Inside(group, children);
        Cards(markdown, inside, level, files, pictures, ct);
        foreach (var subgroup in inside.Where(n => n.Kind == CanvasNodeKind.Group))
        {
            Heading(markdown, subgroup, level + 1);
            if (level < MaxGroupDepth)
            {
                Section(markdown, subgroup, level + 1, children, files, pictures, ct);
                continue;
            }
            // At the last level: this group's cards, then every group inside it in the same order, each at this level.
            var stack = new Stack<CanvasNode>();
            Cards(markdown, Inside(subgroup, children), level + 1, files, pictures, ct);
            foreach (var deeper in Inside(subgroup, children).Where(n => n.Kind == CanvasNodeKind.Group).Reverse())
                stack.Push(deeper);
            while (stack.TryPop(out var next))
            {
                ct.ThrowIfCancellationRequested();
                Heading(markdown, next, level + 1);
                var nextInside = Inside(next, children);
                Cards(markdown, nextInside, level + 1, files, pictures, ct);
                foreach (var deeper in nextInside.Where(n => n.Kind == CanvasNodeKind.Group).Reverse())
                    stack.Push(deeper);
            }
        }
    }

    private static List<CanvasNode> Inside(CanvasNode? group, ILookup<string?, CanvasNode> children) =>
        children[group?.Id].OrderBy(n => n.Y).ThenBy(n => n.X).ToList();

    private static void Heading(StringBuilder markdown, CanvasNode group, int level) =>
        markdown.Append(new string('#', Math.Min(6, level))).Append(' ')
            .Append(group.Content.Length > 0 ? OneLine(group.Content) : "Group").Append("\n\n");

    private static void Cards(StringBuilder markdown, List<CanvasNode> inside, int level, CanvasFiles files,
        MarkdownPictures? pictures, CancellationToken ct)
    {
        foreach (var card in inside.Where(n => n.Kind != CanvasNodeKind.Group))
        {
            ct.ThrowIfCancellationRequested();
            markdown.Append(Card(card, level, files, pictures).TrimEnd()).Append("\n\n");
        }
    }

    private static string Card(CanvasNode card, int level, CanvasFiles files, MarkdownPictures? pictures)
    {
        switch (card.Kind)
        {
            case CanvasNodeKind.Text:
                return Demote(card.Content, level + 1);
            case CanvasNodeKind.Link:
                return $"<{card.Content.Trim().Replace(">", "%3E", StringComparison.Ordinal)}>";
            default:
                {
                    var name = Path.GetFileName(card.Content.Replace('\\', '/'));
                    var shown = Escape(CanvasFiles.IsNote(name) ? name[..^3] : name);
                    if (CanvasFiles.IsPicture(name) && files.Find(card.Content) is { } local && pictures?.Add(local) is { } link)
                        return $"![{shown}]({link})";
                    // A heading usually has spaces, and a link destination with a space isn't a link in CommonMark.
                    var target = string.Join('/', card.Content.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString))
                        + (card.Subpath is { Length: > 1 } subpath ? "#" + Uri.EscapeDataString(subpath.TrimStart('#')) : "");
                    return $"[{shown}]({target})";
                }
        }
    }

    /// <summary>A card's name in the list of lines: its first line of text, its file's name, its address or its label.</summary>
    public static string Name(CanvasNode node)
    {
        var name = node.Kind switch
        {
            CanvasNodeKind.Text => node.Content.Split('\n').Where(l => !IsFence(l)).Select(Plain).FirstOrDefault(l => l.Length > 0) ?? "",
            CanvasNodeKind.File => Path.GetFileNameWithoutExtension(node.Content.Replace('\\', '/')),
            CanvasNodeKind.Link => node.Content.Trim(),
            _ => node.Content.Length > 0 ? OneLine(node.Content) : "Group",
        };
        if (name.Length == 0)
            name = "Card";
        return name.Length > NameLength ? name[..NameLength].TrimEnd() + "…" : name;
    }

    /// <summary>A line of Markdown without its marks: heading signs, list bullets, emphasis, links.</summary>
    private static string Plain(string line)
    {
        line = LeadingMarks().Replace(line.Trim(), "");
        line = WikiLink().Replace(line, m => m.Groups["alias"].Success ? m.Groups["alias"].Value : m.Groups["target"].Value);
        line = MarkdownLink().Replace(line, "$1");
        return Emphasis().Replace(line, "").Trim();
    }

    /// <summary>The text with its headings moved down so the highest is at <paramref name="level"/> (code blocks left alone).</summary>
    private static string Demote(string text, int level)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var fence = false;
        var top = 7;
        foreach (var line in lines)
        {
            if (IsFence(line))
                fence = !fence;
            else if (!fence && HeadingLevel(line) is > 0 and var heading)
                top = Math.Min(top, heading);
        }
        var shift = top <= 6 ? Math.Max(0, level - top) : 0;
        if (shift == 0)
            return text;
        fence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsFence(lines[i]))
                fence = !fence;
            else if (!fence && HeadingLevel(lines[i]) is > 0 and var heading)
            {
                var trimmed = lines[i].TrimStart();
                lines[i] = new string('#', Math.Min(6, heading + shift)) + trimmed[heading..];
            }
        }
        return string.Join('\n', lines);
    }

    private static bool IsFence(string line) =>
        line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal);

    private static int HeadingLevel(string line)
    {
        if (line.Length - line.TrimStart().Length > 3)
            return 0;
        var trimmed = line.TrimStart();
        var level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
            level++;
        return level is >= 1 and <= 6 && (level == trimmed.Length || trimmed[level] is ' ' or '\t') ? level : 0;
    }

    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string Escape(string text) => MarkdownSpecial().Replace(text, @"\$0");

    [GeneratedRegex(@"^(#{1,6}\s+|[-*+]\s+(\[[ xX]\]\s+)?|\d+[.)]\s+|>\s*)+")]
    private static partial Regex LeadingMarks();

    [GeneratedRegex(@"!?\[\[(?<target>[^\[\]|]+)(?:\|(?<alias>[^\[\]]+))?\]\]")]
    private static partial Regex WikiLink();

    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"\*\*|__|==|~~|`|(?<!\w)[*_](?=\S)|(?<=\S)[*_](?!\w)")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"[\\`*_\[\]<>]")]
    private static partial Regex MarkdownSpecial();
}
