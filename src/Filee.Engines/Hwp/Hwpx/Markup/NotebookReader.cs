// Jupyter notebooks (.ipynb, nbformat 4 and the older 3) → HDocument, without Pandoc:
//  * markdown cells through MarkdownReader (Markdig), with their attached pictures ("attachment:plot.png");
//  * code cells as code blocks (one block per cell), raw cells as code too;
//  * the outputs of a code cell indented by OutputIndent under it: streams, results and errors (terminal colours
//    removed) as code, pictures (PNG, JPEG, GIF, SVG) as pictures, Markdown and HTML results (data frames) through
//    their readers. A result that has a picture is shown as the picture.
// Outputs are what makes notebooks big: an output keeps its first MaxOutputLines lines / MaxOutputCharacters
// characters and says how many lines were left out, outputs stop after MaxOutputsCharacters in all, and a picture
// of more than MaxPictureBytes is left out. JSON nested more than 64 levels or not shaped like a notebook is damage.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Reads Jupyter notebooks into the document model.</summary>
internal sealed partial class NotebookReader
{
    /// <summary>Notebooks larger than this are refused (they hold their pictures as base64 text).</summary>
    public const long MaxFileBytes = 256L * 1024 * 1024;

    /// <summary>Lines of one output that are kept.</summary>
    public const int MaxOutputLines = 2_000;

    /// <summary>Characters of one output that are kept.</summary>
    public const int MaxOutputCharacters = 200_000;

    /// <summary>Characters of all text outputs together; later outputs are left out.</summary>
    public const long MaxOutputsCharacters = 8_000_000;

    /// <summary>Bytes of one picture (decoded); bigger ones are left out.</summary>
    public const int MaxPictureBytes = 32 * 1024 * 1024;

    /// <summary>Left indent of a cell's outputs: tells them from the code (NotebookWriter writes them back as outputs).</summary>
    public const int OutputIndent = 800;

    private const int MaxJsonDepth = 64;

    private readonly string _baseFolder;
    private readonly string _media;
    private readonly DocumentBudget _budget;
    private readonly List<HBlock> _blocks = [];
    private long _outputCharacters;
    private bool _outputsCut;
    private int _pictures;

    private NotebookReader(string baseFolder, string media, DocumentLimits limits)
    {
        _baseFolder = baseFolder;
        _media = media;
        _budget = new DocumentBudget(limits);
    }

    public static HDocument Read(string path, string media)
    {
        if (new FileInfo(path).Length > MaxFileBytes)
            throw DocumentLimits.Exceeded($"The notebook is larger than {DocumentLimits.Megabytes(MaxFileBytes)}.");
        return Read(File.ReadAllBytes(path), Path.GetDirectoryName(Path.GetFullPath(path))!, media, DocumentLimits.Default);
    }

    /// <param name="baseFolder">Folder of the notebook: relative picture links in markdown cells are resolved against it.</param>
    internal static HDocument Read(byte[] json, string baseFolder, string media, DocumentLimits limits)
    {
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = MaxJsonDepth,
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The file is not a Jupyter notebook (its JSON can't be read).", ex);
        }
        using (parsed)
        {
            try
            {
                return FromJson(parsed.RootElement, new NotebookReader(baseFolder, media, limits));
            }
            catch (InvalidOperationException ex) when (ex.InnerException is DecoderFallbackException)
            {
                throw new InvalidDataException("The notebook holds text that is not UTF-8.", ex);
            }
        }
    }

    private static HDocument FromJson(JsonElement root, NotebookReader reader)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The file is not a Jupyter notebook.");
        var version3 = root.TryGetProperty("worksheets", out var worksheets) && worksheets.ValueKind == JsonValueKind.Array;
        if (version3)
        {
            foreach (var worksheet in worksheets.EnumerateArray())
                if (worksheet.ValueKind == JsonValueKind.Object && worksheet.TryGetProperty("cells", out var cells))
                    reader.Cells(cells, version3: true);
        }
        else if (root.TryGetProperty("cells", out var cells) && cells.ValueKind == JsonValueKind.Array)
        {
            reader.Cells(cells, version3: false);
        }
        else
        {
            throw new InvalidDataException("The file is not a Jupyter notebook (it has no cells).");
        }

        var section = new HSection();
        section.Blocks.AddRange(reader._blocks);
        var document = new HDocument { Title = Title(root) };
        document.Sections.Add(section);
        return document;
    }

    private static string? Title(JsonElement root) =>
        root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
        && (metadata.TryGetProperty("title", out var title) || metadata.TryGetProperty("name", out title))
        && title.ValueKind == JsonValueKind.String && title.GetString()!.Trim() is { Length: > 0 } text
            ? text
            : null;

    // ───────────────────────── Cells ─────────────────────────

    private void Cells(JsonElement cells, bool version3)
    {
        if (cells.ValueKind != JsonValueKind.Array)
            return;
        foreach (var cell in cells.EnumerateArray())
        {
            if (cell.ValueKind != JsonValueKind.Object)
                continue;
            var type = cell.TryGetProperty("cell_type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            switch (type)
            {
                case "markdown":
                    Markdown(Text(cell, "source"), cell);
                    break;
                case "heading": // nbformat 3
                    {
                        var level = cell.TryGetProperty("level", out var l) && l.TryGetInt32(out var value) ? Math.Clamp(value, 1, 6) : 1;
                        Markdown(new string('#', level) + " " + Text(cell, "source").Replace('\n', ' '), cell);
                        break;
                    }
                case "code":
                    Code(Text(cell, version3 ? "input" : "source"));
                    if (cell.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
                        foreach (var output in outputs.EnumerateArray())
                            Output(output, version3);
                    break;
                case "raw":
                    Code(Text(cell, "source"));
                    break;
            }
        }
    }

    private void Markdown(string source, JsonElement cell)
    {
        if (source.Trim().Length == 0)
            return;
        // Lines bound the blocks Markdig makes (table cells aside, which are counted after): check before parsing.
        CheckLines(MarkupBlocks.LineCount(source));
        if (cell.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Object)
        {
            foreach (var attachment in attachments.EnumerateObject())
            {
                if (Picture(attachment.Value) is { } file)
                    source = source.Replace("attachment:" + attachment.Name, new Uri(file).AbsoluteUri, StringComparison.Ordinal);
            }
        }
        var blocks = MarkdownReader.Read(source, _baseFolder).Sections.SelectMany(s => s.Blocks).ToList();
        MarkupBlocks.Count(blocks, _budget);
        if (blocks.Count > 0 && MarkupBlocks.IsCode(blocks[0]))
            MarkupBlocks.SeparateCode(_blocks, _budget);
        _blocks.AddRange(blocks);
    }

    private void Code(string source)
    {
        if (source.Length == 0)
            return;
        MarkupBlocks.SeparateCode(_blocks, _budget);
        var lines = source.TrimEnd('\n').Split('\n');
        CheckLines(lines.Length);
        MarkupBlocks.CodeLines(lines, _blocks, _budget);
    }

    /// <summary>Refuses a cell of more lines than paragraphs are left, before anything is built of it.</summary>
    private void CheckLines(int lines)
    {
        if (_budget.Paragraphs + (long)lines > _budget.Limits.MaxParagraphs)
            throw DocumentLimits.Exceeded($"The document has more than {DocumentLimits.Count(_budget.Limits.MaxParagraphs)} paragraphs.");
    }

    // ───────────────────────── Outputs ─────────────────────────

    private void Output(JsonElement output, bool version3)
    {
        if (output.ValueKind != JsonValueKind.Object)
            return;
        var type = output.TryGetProperty("output_type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        switch (type)
        {
            case "stream":
                OutputText(Text(output, "text"));
                break;
            case "error" or "pyerr":
                {
                    var traceback = output.TryGetProperty("traceback", out var lines) && lines.ValueKind == JsonValueKind.Array
                        ? string.Join("\n", lines.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()))
                        : "";
                    if (traceback.Trim().Length == 0)
                        traceback = $"{Text(output, "ename")}: {Text(output, "evalue")}";
                    OutputText(traceback);
                    break;
                }
            case "execute_result" or "display_data" or "pyout":
                // nbformat 4 keeps the representations under "data" by media type; 3 has them on the output itself.
                var data = version3 ? output : output.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
                if (data.ValueKind == JsonValueKind.Object)
                    Data(data, version3);
                break;
        }
    }

    private void Data(JsonElement data, bool version3)
    {
        string Key(string mediaType) => !version3 ? mediaType : mediaType switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpeg",
            "image/svg+xml" => "svg",
            "text/html" => "html",
            "text/markdown" => "markdown",
            "text/plain" => "text",
            _ => mediaType,
        };

        foreach (var mediaType in new[] { "image/png", "image/jpeg", "image/gif", "image/svg+xml" })
        {
            if (!data.TryGetProperty(Key(mediaType), out var picture))
                continue;
            if (Picture(mediaType, picture) is { } file)
            {
                _budget.Paragraph();
                _budget.Object();
                var paragraph = new HParagraph { Format = new HParaFormat(Left: OutputIndent) };
                paragraph.Inlines.Add(new HImage(file));
                _blocks.Add(paragraph);
                return;
            }
        }
        if (data.TryGetProperty(Key("text/markdown"), out var markdown) && Joined(markdown) is { Length: > 0 and <= MaxOutputCharacters } md && Spend(md.Length))
        {
            Outputs(MarkdownReader.Read(md, _baseFolder).Sections.SelectMany(s => s.Blocks).ToList());
            return;
        }
        if (data.TryGetProperty(Key("text/html"), out var html) && Joined(html) is { Length: > 0 and <= MaxOutputCharacters } page && Spend(page.Length))
        {
            var content = HtmlReader.Parse(page, new HtmlReadOptions { BaseFolder = _baseFolder, MediaFolder = _media });
            // Remote pictures and scripts give nothing: a widget's HTML may hold no text at all.
            if (content.Blocks.Any(b => !MarkupBlocks.IsEmpty(b)))
            {
                Outputs(content.Blocks);
                return;
            }
        }
        if (data.TryGetProperty(Key("text/plain"), out var plain))
            OutputText(Joined(plain) ?? "");
    }

    /// <summary>Blocks another reader made of an output, indented as outputs.</summary>
    private void Outputs(List<HBlock> blocks)
    {
        MarkupBlocks.Count(blocks, _budget);
        MarkupBlocks.SeparateCode(_blocks, _budget);
        foreach (var block in blocks)
        {
            if (block is HParagraph paragraph)
                paragraph.Format = paragraph.Format with { Left = (paragraph.Format.Left ?? 0) + OutputIndent };
            else if (block is HTable table)
                table.Indent += OutputIndent;
            _blocks.Add(block);
        }
    }

    /// <summary>Text of an output as indented code, cut to the output limits.</summary>
    private void OutputText(string text)
    {
        text = Ansi().Replace(MarkupBlocks.Lines(text), "").TrimEnd('\n');
        if (text.Length == 0)
            return;
        var lines = new List<string>();
        var characters = 0;
        var start = 0;
        var total = MarkupBlocks.LineCount(text);
        var cut = false;
        while (start <= text.Length && lines.Count < MaxOutputLines && characters < MaxOutputCharacters)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0)
                end = text.Length;
            var line = text[start..Math.Min(end, start + MaxOutputCharacters - characters)];
            cut |= start + line.Length < end;
            lines.Add(line);
            characters += line.Length;
            start = end + 1;
        }
        if (!Spend(characters))
            return;
        if (lines.Count < total)
            lines.Add($"… ({DocumentLimits.Count(total - lines.Count)} more lines left out)");
        else if (cut)
            lines.Add($"… (cut at {DocumentLimits.Count(MaxOutputCharacters)} characters)");
        MarkupBlocks.SeparateCode(_blocks, _budget);
        MarkupBlocks.CodeLines(lines, _blocks, _budget, OutputIndent);
    }

    /// <summary>
    /// Counts output text against <see cref="MaxOutputsCharacters"/>; past it, says once that outputs were left out.
    /// </summary>
    private bool Spend(int characters)
    {
        if (_outputsCut)
            return false;
        if (_outputCharacters + characters <= MaxOutputsCharacters)
        {
            _outputCharacters += characters;
            return true;
        }
        _outputsCut = true;
        _budget.Paragraph();
        var notice = new HParagraph { Format = new HParaFormat(Left: OutputIndent) };
        notice.Inlines.Add(new HText($"(Further outputs left out: the notebook's outputs pass {DocumentLimits.Count(MaxOutputsCharacters)} characters.)", new HCharFormat(Italic: true)));
        _blocks.Add(notice);
        return false;
    }

    // ───────────────────────── Pictures ─────────────────────────

    /// <summary>An attachment of a markdown cell ({"image/png": "base64"}) written to the media folder.</summary>
    private string? Picture(JsonElement attachment)
    {
        if (attachment.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in attachment.EnumerateObject())
            if (Picture(property.Name, property.Value) is { } file)
                return file;
        return null;
    }

    /// <summary>
    /// A picture of a media type (base64 for raster pictures, the markup itself for SVG) written to the media folder;
    /// null when it is no picture, too big or not readable.
    /// </summary>
    private string? Picture(string mediaType, JsonElement value)
    {
        var extension = mediaType switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpg",
            "image/gif" => "gif",
            "image/svg+xml" => "svg",
            _ => null,
        };
        if (extension is null || Joined(value) is not { Length: > 0 } text)
            return null;
        byte[] bytes;
        if (extension == "svg")
        {
            if (text.Length > MaxPictureBytes)
                return null;
            bytes = Encoding.UTF8.GetBytes(text);
        }
        else
        {
            // Base64 is 4 characters per 3 bytes (with line breaks in some notebooks).
            if (text.Length / 4L * 3 > MaxPictureBytes + 1024)
                return null;
            try
            {
                bytes = Convert.FromBase64String(text.Trim());
            }
            catch (FormatException)
            {
                return null;
            }
            if (bytes.Length > MaxPictureBytes)
                return null;
        }
        Directory.CreateDirectory(_media);
        var path = Path.Combine(_media, $"output-{++_pictures}.{extension}");
        File.WriteAllBytes(path, bytes);
        // Not the picture it says it is, or one that would take gigabytes once decoded.
        if (extension != "svg" && (PictureSize.Read(path) is not { } size || (long)size.Width * size.Height > _budget.Limits.MaxPicturePixels))
            return null;
        return HtmlReader.UsableImage(path, _media);
    }

    // ───────────────────────── JSON ─────────────────────────

    /// <summary>A string member or a list of strings joined (nbformat splits multi-line text into lines).</summary>
    private static string Text(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) ? MarkupBlocks.Lines(Joined(value) ?? "") : "";

    private static string? Joined(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Array => string.Concat(value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString())),
        _ => null,
    };

    /// <summary>Terminal colour and cursor codes (ESC [ ... letter) in tracebacks and progress bars.</summary>
    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B[@-Z\\-_]")]
    private static partial Regex Ansi();
}
