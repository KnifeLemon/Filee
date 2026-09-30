// Anything → HWPX without Hancom Office: a reader turns the source into the HWPX document model and HwpxWriter
// writes the OWPML package.
//  * DOCX is read directly (Docx/DocxReader), keeping page setup, headers/footers, columns, text boxes and formatting.
//  * TXT needs no reader: one paragraph per line.
//  * Markdown is parsed with Markdig (MarkdownReader), no external engine needed.
//  * XLSX and CSV are read directly (Office/Sheets); every sheet becomes a table (SheetDocument).
//  * PPTX is read directly (Pptx/PptxReader): one page per slide with the objects floating at their places.
//  * HTML, ODT and RTF are parsed by Pandoc (optional engine) into its JSON AST (PandocAstReader).

using System.Text;
using System.Text.Json.Nodes;
using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Hwp.Hwpx.Pptx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Hwp.Hwpx;

/// <summary>Writes HWPX files with the built-in OWPML writer.</summary>
public sealed class HwpxConverter : IConverter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>Filee format id → Pandoc reader name.</summary>
    private static readonly Dictionary<string, string> PandocReaders = new()
    {
        ["odt"] = "odt",
        ["rtf"] = "rtf",
        ["html"] = "html",
    };

    private string? _pandoc;

    public string Id => "hwpx-writer";
    public string DisplayName => "HWPX writer (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges => BuildEdges(_pandoc is not null || PandocConverter.Locate() is not null);

    public EngineStatus GetStatus()
    {
        _pandoc = PandocConverter.Locate();
        return EngineStatus.Available(_pandoc, EngineVersions.BuiltIn);
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.1);
        var media = Path.Combine(step.WorkDirectory, $"media-{Guid.NewGuid():N}");
        var document = step.From switch
        {
            "docx" => DocxReader.Read(step.InputPath, media),
            "txt" => TextDocument(await File.ReadAllBytesAsync(step.InputPath, cancellationToken)),
            "md" => MarkdownReader.Read(DecodeText(await File.ReadAllBytesAsync(step.InputPath, cancellationToken)),
                Path.GetDirectoryName(Path.GetFullPath(step.InputPath))!),
            "xlsx" => SheetDocument.Build(XlsxReader.Read(step.InputPath)),
            "csv" => SheetDocument.Build(CsvFormat.Read(step.InputPath)),
            "xls" or "ods" or "tsv" => SheetDocument.Build(SpreadsheetConverter.Read(step.InputPath, step.From)),
            "pptx" => PptxReader.Read(step.InputPath, media),
            _ => await ReadWithPandocAsync(step, media, cancellationToken),
        };
        progress?.Report(0.6);

        var output = step.Output.Allocate("hwpx");
        if (output is null)
            return [];
        HwpxWriter.Write(document, output);
        progress?.Report(1);
        return [output];
    }

    private async Task<HDocument> ReadWithPandocAsync(ConversionStep step, string media, CancellationToken cancellationToken)
    {
        var pandoc = _pandoc ?? PandocConverter.Locate() ?? throw new InvalidOperationException("Pandoc was not found.");
        var json = Path.Combine(step.WorkDirectory, $"ast-{Guid.NewGuid():N}.json");

        // Working directory = the input's folder, so relative image paths in HTML resolve.
        var result = await ProcessRunner.RunAsync(pandoc,
            [step.InputPath, "-f", PandocReaders[step.From], "-t", "json", "--extract-media=" + media, "-o", json],
            Timeout, cancellationToken, workingDirectory: Path.GetDirectoryName(Path.GetFullPath(step.InputPath)));
        if (!File.Exists(json))
            throw new InvalidOperationException($"Pandoc failed (exit {result.ExitCode}). {result.StandardError.Trim()}");
        var ast = JsonNode.Parse(await File.ReadAllTextAsync(json, cancellationToken))
                  ?? throw new InvalidDataException("Pandoc produced an empty document.");
        var inputFolder = Path.GetDirectoryName(Path.GetFullPath(step.InputPath))!;
        return PandocAstReader.Read(ast, target => ResolveImage(target, inputFolder));
    }

    /// <summary>Images extracted by Pandoc have absolute paths; HTML may use paths relative to the input.</summary>
    private static string? ResolveImage(string target, string inputFolder)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Contains("://", StringComparison.Ordinal))
            return null;
        var path = Uri.UnescapeDataString(target);
        foreach (var candidate in new[] { path, Path.Combine(inputFolder, path) })
        {
            try
            {
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Plain text: one paragraph per line, text kept verbatim.</summary>
    internal static HDocument TextDocument(byte[] bytes)
    {
        var section = new HSection();
        foreach (var line in DecodeText(bytes).Replace("\r\n", "\n").Split('\n'))
        {
            var paragraph = new HParagraph();
            if (line.Length > 0)
                paragraph.Inlines.Add(new HText(line, default));
            section.Blocks.Add(paragraph);
        }
        var document = new HDocument();
        document.Sections.Add(section);
        return document;
    }

    /// <summary>UTF-8 (with or without BOM) or UTF-16 with BOM; anything else is read as CP949, common for Korean text.</summary>
    internal static string DecodeText(byte[] bytes)
    {
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
            return new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true).ReadToEnd();
        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(949).GetString(bytes);
        }
    }

    private static List<ConversionEdge> BuildEdges(bool pandoc)
    {
        List<ConversionEdge> edges = [new("txt", "hwpx"), new("docx", "hwpx"), new("md", "hwpx"), new("xlsx", "hwpx"), new("csv", "hwpx"), new("pptx", "hwpx"),
            new("xls", "hwpx"), new("ods", "hwpx"), new("tsv", "hwpx")];
        if (pandoc)
            edges.AddRange(PandocReaders.Keys.Select(from => new ConversionEdge(from, "hwpx")));
        return edges;
    }
}
