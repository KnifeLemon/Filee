// PowerPoint slides as Markdown a reveal.js deck reads (issue #1702): one section per slide, separated by "---"; the
// slide's title as a heading ("#" on a title slide, "##" otherwise), its text with bullet and numbered lists at their
// levels, links, bold and italic, tables as pipe tables, pictures saved next to the file, and the speaker notes after
// a "Note:" line. A slide with nothing to show (a blank or a chart only) stays as an empty section, so the slide count
// matches. Read straight from the package, in the order the slide lists its objects (title first), unlike
// PptxReader, which places every object on a page for PDF and HWPX.
//
// Not converted: charts, SmartArt, equations, animations and hidden slides.

using System.Text;
using System.Xml.Linq;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Office.Ooxml;

namespace Filee.Engines.Office.Slides;

internal sealed class SlidesMarkdown
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private readonly OpcPackage _package;
    private readonly Func<string, string?> _pictures;
    private readonly string _work;
    private readonly CancellationToken _cancellationToken;

    private SlidesMarkdown(OpcPackage package, Func<string, string?> pictures, string work, CancellationToken cancellationToken)
    {
        _package = package;
        _pictures = pictures;
        _work = work;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// The presentation at <paramref name="path"/> as Markdown. <paramref name="pictures"/> takes a picture file and
    /// returns its link (null leaves the picture out); pictures are unpacked into <paramref name="work"/> first.
    /// </summary>
    public static string Write(string path, Func<string, string?> pictures, string work, CancellationToken cancellationToken)
    {
        using var package = new OpcPackage(path);
        return new SlidesMarkdown(package, pictures, work, cancellationToken).Write();
    }

    private string Write()
    {
        var presentationPath = _package.MainPartPath ?? "ppt/presentation.xml";
        var presentation = _package.Part(presentationPath)?.Root
                           ?? throw new InvalidDataException("The file is not a PowerPoint presentation (no ppt/presentation.xml).");
        var slides = new List<string>();
        foreach (var id in presentation.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [])
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var slidePath = _package.Target(presentationPath, (string?)id.Attribute(R + "id"));
            if (slidePath is null || _package.Part(slidePath)?.Root is not { } slide || (string?)slide.Attribute("show") is "0" or "false")
                continue;
            slides.Add(Slide(slidePath, slide));
        }
        return string.Join("\n\n---\n\n", slides).Trim('\n') + "\n";
    }

    private string Slide(string slidePath, XElement slide)
    {
        var blocks = new List<string>();
        var objects = Objects(slide.Element(P + "cSld")?.Element(P + "spTree")).ToList();
        // The title leads the slide, wherever the slide lists it.
        var title = objects.FirstOrDefault(o => PlaceholderType(o) is "title" or "ctrTitle");
        if (title is not null)
        {
            var text = string.Join(" ", Paragraphs(title).Select(p => Runs(p, slidePath)).Where(t => t.Length > 0));
            if (text.Length > 0)
                blocks.Add((PlaceholderType(title) == "ctrTitle" ? "# " : "## ") + text);
        }
        foreach (var shape in objects.Where(o => !ReferenceEquals(o, title)))
        {
            if (shape.Name == P + "pic")
                AddPicture(shape, slidePath, blocks);
            else if (shape.Name == P + "graphicFrame" && shape.Descendants(A + "tbl").FirstOrDefault() is { } table)
                blocks.Add(Table(table, slidePath));
            else if (shape.Name == P + "sp" && shape.Element(P + "txBody") is { } body)
                blocks.AddRange(Text(body, slidePath, Bulleted(shape)));
        }
        if (Notes(slidePath) is { Length: > 0 } notes)
            blocks.Add("Note:\n" + notes);
        return string.Join("\n\n", blocks);
    }

    /// <summary>The slide's objects in order, the members of groups in their place.</summary>
    private static IEnumerable<XElement> Objects(XElement? tree)
    {
        foreach (var element in tree?.Elements() ?? [])
        {
            if (element.Name == P + "grpSp")
            {
                foreach (var inner in Objects(element))
                    yield return inner;
            }
            else if (element.Name == P + "sp" || element.Name == P + "pic" || element.Name == P + "graphicFrame")
            {
                yield return element;
            }
            else if (element.Name.LocalName == "AlternateContent")
            {
                // Newer content with a fallback (equations, 3D models): the fallback's objects.
                foreach (var inner in Objects(element.Elements().LastOrDefault()))
                    yield return inner;
            }
        }
    }

    private static XElement? Placeholder(XElement shape) =>
        shape.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("nv", StringComparison.Ordinal))?.Element(P + "nvPr")?.Element(P + "ph");

    /// <summary>The placeholder's type; a placeholder without one is a content placeholder ("body").</summary>
    private static string? PlaceholderType(XElement shape) =>
        Placeholder(shape) is { } ph ? (string?)ph.Attribute("type") ?? "body" : null;

    /// <summary>Body and content placeholders show bullets unless a paragraph turns them off; text boxes don't.</summary>
    private static bool Bulleted(XElement shape) => PlaceholderType(shape) is "body" or "obj";

    private static IEnumerable<XElement> Paragraphs(XElement shape) =>
        shape.Element(P + "txBody")?.Elements(A + "p") ?? [];

    /// <summary>A text body as Markdown blocks: runs of list items, and paragraphs.</summary>
    private List<string> Text(XElement body, string slidePath, bool bulleted)
    {
        var blocks = new List<string>();
        var list = new StringBuilder();
        var numbers = new Dictionary<int, int>();
        foreach (var paragraph in body.Elements(A + "p"))
        {
            var text = Runs(paragraph, slidePath);
            var pPr = paragraph.Element(A + "pPr");
            var level = Math.Clamp((int?)pPr?.Attribute("lvl") ?? 0, 0, 8);
            var numbered = pPr?.Element(A + "buAutoNum") is not null;
            var bullet = numbered || pPr?.Element(A + "buChar") is not null || pPr?.Element(A + "buBlip") is not null
                         || bulleted && pPr?.Element(A + "buNone") is null;
            if (text.Length == 0)
            {
                // An empty paragraph ends a list, and restarts its numbering.
                Flush();
                continue;
            }
            if (!bullet)
            {
                Flush();
                blocks.Add(MarkdownWriter.EscapeLineStart(text));
                continue;
            }
            foreach (var deeper in numbers.Keys.Where(l => l > level).ToList())
                numbers.Remove(deeper);
            var marker = numbered ? $"{numbers[level] = numbers.GetValueOrDefault(level) + 1}." : "-";
            list.Append(new string(' ', level * 2)).Append(marker).Append(' ').Append(text).Append('\n');
        }
        Flush();
        return blocks;

        void Flush()
        {
            if (list.Length > 0)
                blocks.Add(list.ToString().TrimEnd('\n'));
            list.Clear();
            numbers.Clear();
        }
    }

    /// <summary>A paragraph's runs, line breaks and fields, with links, bold and italic.</summary>
    private string Runs(XElement paragraph, string slidePath)
    {
        var sb = new StringBuilder();
        foreach (var element in paragraph.Elements())
        {
            if (element.Name == A + "br")
            {
                sb.Append("<br>");
                continue;
            }
            if (element.Name != A + "r" && element.Name != A + "fld")
                continue;
            var text = (string?)element.Element(A + "t") ?? "";
            if (text.Trim().Length == 0)
            {
                sb.Append(text);
                continue;
            }
            var rPr = element.Element(A + "rPr");
            var core = MarkdownWriter.Escape(text.Trim());
            if ((string?)rPr?.Attribute("i") is "1" or "true")
                core = $"*{core}*";
            if ((string?)rPr?.Attribute("b") is "1" or "true")
                core = $"**{core}**";
            if (Link(rPr, slidePath) is { } url)
                core = $"[{core}]({url})";
            // Emphasis and link markers touch the text: spaces stay outside.
            sb.Append(text[..(text.Length - text.TrimStart().Length)]).Append(core).Append(text[text.TrimEnd().Length..]);
        }
        return sb.ToString().Trim();
    }

    /// <summary>A run's hyperlink to a web or mail address; links to other slides or files are left out.</summary>
    private string? Link(XElement? rPr, string slidePath)
    {
        var id = (string?)rPr?.Element(A + "hlinkClick")?.Attribute(R + "id");
        if (id is null || !_package.Relationships(slidePath).TryGetValue(id, out var relationship) || !relationship.External)
            return null;
        var target = relationship.Target;
        return Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto"
            ? uri.AbsoluteUri.Replace(" ", "%20", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal)
            : null;
    }

    /// <summary>A table as a pipe table, the first row as its header; a cell's paragraphs are joined by line breaks.</summary>
    private string Table(XElement table, string slidePath)
    {
        var rows = table.Elements(A + "tr")
            .Select(row => row.Elements(A + "tc")
                // A cell merged into its neighbour is drawn by that neighbour.
                .Where(cell => (string?)cell.Attribute("hMerge") is not ("1" or "true"))
                .Select(cell => string.Join("<br>", cell.Descendants(A + "p").Select(p => Runs(p, slidePath)).Where(t => t.Length > 0)))
                .ToList())
            .Where(row => row.Count > 0)
            .ToList();
        if (rows.Count == 0)
            return "";
        var columns = rows.Max(r => r.Count);
        string Row(List<string> cells) =>
            "| " + string.Join(" | ", Enumerable.Range(0, columns).Select(i => i < cells.Count ? cells[i] : "")) + " |";
        var sb = new StringBuilder();
        sb.Append(Row(rows[0])).Append('\n');
        sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", columns))).Append('\n');
        foreach (var row in rows.Skip(1))
            sb.Append(Row(row)).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    private void AddPicture(XElement picture, string slidePath, List<string> blocks)
    {
        var id = (string?)picture.Element(P + "blipFill")?.Element(A + "blip")?.Attribute(R + "embed");
        if (_package.Target(slidePath, id) is not { } part || _package.Extract(part, _work) is not { } file || _pictures(file) is not { } link)
            return;
        var properties = picture.Element(P + "nvPicPr")?.Element(P + "cNvPr");
        var alt = (string?)properties?.Attribute("descr") is { Length: > 0 } description ? description : (string?)properties?.Attribute("name") ?? "";
        blocks.Add($"![{MarkdownWriter.Escape(alt.ReplaceLineEndings(" "))}]({link})");
    }

    /// <summary>The speaker notes: the text of the notes page's body placeholder, one line per paragraph.</summary>
    private string? Notes(string slidePath)
    {
        var notesPath = _package.TargetOfType(slidePath, "/notesSlide");
        if (notesPath is null || _package.Part(notesPath)?.Root is not { } notes)
            return null;
        var body = Objects(notes.Element(P + "cSld")?.Element(P + "spTree")).FirstOrDefault(o => PlaceholderType(o) == "body");
        if (body is null)
            return null;
        var lines = Paragraphs(body).Select(p => Runs(p, notesPath)).Where(t => t.Length > 0).Select(MarkdownWriter.EscapeLineStart);
        return string.Join("\n", lines);
    }
}
