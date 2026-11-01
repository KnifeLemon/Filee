// HDocument → Jupyter notebook (nbformat 4.5): code blocks become code cells, everything between them markdown cells
// (written by MarkdownWriter, a new cell at each heading), with their pictures attached to the cell (base64 PNG or
// JPEG, "attachment:image1.png"), so the notebook is one file. What NotebookReader read as a cell's outputs (indented
// by NotebookReader.OutputIndent right after the code) is written back as outputs: text as a stdout stream, pictures as
// display data. The notebook names no kernel; Jupyter asks for one when it is opened.

using System.Text.Json;
using Filee.Engines.Ebooks;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Writes the document model as a Jupyter notebook.</summary>
internal sealed class NotebookWriter
{
    private readonly string _work;
    private readonly bool _pictures;
    private readonly List<Dictionary<string, object?>> _cells = [];

    private NotebookWriter(string work, bool pictures)
    {
        _work = work;
        _pictures = pictures;
    }

    /// <summary>The notebook's JSON.</summary>
    /// <param name="work">Scratch folder for pictures converted to PNG or JPEG.</param>
    /// <param name="pictures">False leaves the pictures out (DocumentOptions.LeaveOutPictures).</param>
    public static string Write(HDocument document, string work, bool pictures = true)
    {
        var writer = new NotebookWriter(work, pictures);
        writer.Cells(document.Sections.SelectMany(s => s.Blocks).ToList());
        var metadata = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(document.Title))
            metadata["title"] = document.Title.Trim();
        var notebook = new Dictionary<string, object?>
        {
            ["cells"] = writer._cells,
            ["metadata"] = metadata,
            ["nbformat"] = 4,
            ["nbformat_minor"] = 5,
        };
        return JsonSerializer.Serialize(notebook, Json) + "\n";
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Text stays readable in the file (Korean, accents); only what JSON must escape is escaped.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static bool IsOutput(HBlock block) =>
        block is HParagraph paragraph && paragraph.Format.Left == NotebookReader.OutputIndent;

    private void Cells(List<HBlock> blocks)
    {
        var markdown = new List<HBlock>();
        for (var i = 0; i < blocks.Count;)
        {
            if (MarkupBlocks.IsCode(blocks[i]) && !IsOutput(blocks[i]))
            {
                Markdown(markdown);
                var code = new List<string>();
                for (; i < blocks.Count && MarkupBlocks.IsCode(blocks[i]) && !IsOutput(blocks[i]); i++)
                    code.Add(Text((HParagraph)blocks[i]));
                var outputs = new List<Dictionary<string, object?>>();
                i = Outputs(blocks, i, outputs);
                _cells.Add(new Dictionary<string, object?>
                {
                    ["cell_type"] = "code",
                    ["execution_count"] = null,
                    ["id"] = Id(),
                    ["metadata"] = new Dictionary<string, object?>(),
                    ["outputs"] = outputs,
                    ["source"] = Source(string.Join("\n", code)),
                });
                continue;
            }
            if (blocks[i] is HParagraph { HeadingLevel: > 0 })
                Markdown(markdown);
            markdown.Add(blocks[i]);
            i++;
        }
        Markdown(markdown);
    }

    /// <summary>The outputs after a code cell (separators skipped); returns the index after them.</summary>
    private int Outputs(List<HBlock> blocks, int start, List<Dictionary<string, object?>> outputs)
    {
        var i = start;
        while (true)
        {
            var next = i;
            while (next < blocks.Count && MarkupBlocks.IsEmpty(blocks[next]) && !IsOutput(blocks[next]))
                next++;
            if (next >= blocks.Count || !IsOutput(blocks[next]))
                return i;
            i = next;
            if (MarkupBlocks.IsCode(blocks[i]))
            {
                var text = new List<string>();
                for (; i < blocks.Count && MarkupBlocks.IsCode(blocks[i]) && IsOutput(blocks[i]); i++)
                    text.Add(Text((HParagraph)blocks[i]));
                outputs.Add(new Dictionary<string, object?>
                {
                    ["name"] = "stdout",
                    ["output_type"] = "stream",
                    ["text"] = Source(string.Join("\n", text) + "\n"),
                });
                continue;
            }
            var paragraph = (HParagraph)blocks[i];
            var images = paragraph.Inlines.OfType<HImage>().ToList();
            if (images.Count == 0 || paragraph.Inlines.Any(x => x is not HImage and not HBookmark))
                return i; // an output read by another reader (a table, Markdown): written as markdown
            foreach (var image in images)
            {
                if (Picture(image.Path) is not ({ } data, { } mediaType))
                    continue;
                outputs.Add(new Dictionary<string, object?>
                {
                    ["data"] = new Dictionary<string, object?> { [mediaType] = data, ["text/plain"] = Source("<Figure>") },
                    ["metadata"] = new Dictionary<string, object?>(),
                    ["output_type"] = "display_data",
                });
            }
            i++;
        }
    }

    /// <summary>Blocks gathered for a markdown cell, written as one (nothing when they hold no text).</summary>
    private void Markdown(List<HBlock> blocks)
    {
        if (blocks.Count == 0)
            return;
        var attachments = new Dictionary<string, object?>();
        // Outputs that end up here (tables, Markdown results, pictures without code) are written as Markdown: their
        // indent is less than a quote's.
        var section = new HSection();
        section.Blocks.AddRange(blocks);
        var document = new HDocument();
        document.Sections.Add(section);
        var text = MarkdownWriter.Write(document, path =>
        {
            if (!_pictures || Picture(path) is not ({ } data, { } mediaType))
                return null;
            var name = $"image{attachments.Count + 1}.{(mediaType == "image/png" ? "png" : "jpg")}";
            attachments[name] = new Dictionary<string, object?> { [mediaType] = data };
            return "attachment:" + name;
        }).TrimEnd();
        blocks.Clear();
        if (text.Length == 0)
            return;
        var cell = new Dictionary<string, object?>
        {
            ["cell_type"] = "markdown",
            ["id"] = Id(),
            ["metadata"] = new Dictionary<string, object?>(),
            ["source"] = Source(text),
        };
        if (attachments.Count > 0)
            cell["attachments"] = attachments;
        _cells.Add(cell);
    }

    /// <summary>A picture as base64 PNG or JPEG with its media type, or null when it can't be read.</summary>
    private (string Data, string MediaType)? Picture(string path)
    {
        if (!_pictures || EbookFiles.CommonImage(path, _work, gifAndSvg: false) is not ({ } file, { } mediaType))
            return null;
        return (Convert.ToBase64String(File.ReadAllBytes(file)), mediaType);
    }

    private string Id() => $"cell-{_cells.Count + 1}";

    private static string Text(HParagraph paragraph) => string.Concat(paragraph.Inlines.OfType<HText>().Select(t => t.Text));

    /// <summary>Multi-line text as nbformat stores it: one string per line, each but the last ending in "\n".</summary>
    private static List<string> Source(string text)
    {
        var lines = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0)
            {
                lines.Add(text[start..]);
                break;
            }
            lines.Add(text[start..(end + 1)]);
            start = end + 1;
        }
        return lines;
    }
}
