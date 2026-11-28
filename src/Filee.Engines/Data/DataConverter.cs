// Structured data between JSON, JSON Lines, YAML, TOML and XML (and CSV for tables), plus vCard contacts and iCalendar ↔ CSV,
// and NEXUS, FASTA and PHYLIP biological sequence alignments ↔ each other, in-process. Every tree format is read into
// one tree (DataTree) and written from it, so structure and types carry over as far as the target can hold them; see the
// format files for what each one loses (TOML has no null, XML no types, CSV only tables). Output is UTF-8 without BOM
// (CSV: with BOM, like the spreadsheet engine), indented by two spaces. Spreadsheets reach JSON and the others through
// CSV (XLSX → CSV → JSON), contacts and calendars through CSV as well. HAR network logs are read as JSON, and become a
// table of their requests (HarData). Property lists (.plist, XML or binary) are read by PlistData and reach the same
// targets, and plain text as the YAML listing of their tree. Sequence alignments are read and written by SequenceAlignment.

using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Data;

/// <summary>JSON, JSON Lines, YAML, TOML, XML and CSV into each other; vCard and iCalendar ↔ CSV; HAR and property lists → all of them; NEXUS, FASTA and PHYLIP alignments ↔ each other.</summary>
public sealed class DataConverter : IConverter
{
    private static readonly string[] Trees = ["json", "jsonl", "yaml", "toml", "xml"];
    private static readonly string[] Alignments = ["nex", "fasta", "phy"];

    public string Id => "data";
    public string DisplayName => "Data (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        .. Trees.SelectMany(from => Trees.Where(to => to != from).Select(to => new ConversionEdge(from, to))),
        .. Trees.Select(from => new ConversionEdge(from, "csv")),
        .. Trees.Select(to => new ConversionEdge("csv", to)),
        new("vcf", "csv"),
        new("csv", "vcf"),
        .. Trees.Append("csv").Select(to => new ConversionEdge("har", to)),
        .. Trees.Append("csv").Append("txt").Select(to => new ConversionEdge("plist", to)),
        new("ics", "csv"),
        new("csv", "ics"),
        new("txt", "csv", DirectOnly: true),
        .. Alignments.SelectMany(from => Alignments.Where(to => to != from).Select(to => new ConversionEdge(from, to))),
    ];

    public EngineStatus GetStatus() =>
        EngineStatus.Available("JSON, JSON Lines, YAML, TOML, XML, CSV ↔ each other; vCard and iCalendar ↔ CSV; property lists → each of them and TXT; NEXUS, FASTA and PHYLIP alignments ↔ each other",
            $"{EngineVersions.BuiltIn} · {EngineVersions.Library("YamlDotNet", typeof(YamlDotNet.RepresentationModel.YamlStream))}"
            + $" · {EngineVersions.Library("Tomlyn", typeof(Tomlyn.TomlSerializer))}");

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            progress?.Report(0.1);
            if (step.From is "nex" or "fasta" or "phy")
            {
                var source = HwpxConverter.DecodeText(File.ReadAllBytes(step.InputPath));
                var alignment = SequenceAlignment.Read(source, step.From, cancellationToken);
                progress?.Report(0.6);
                var rendered = SequenceAlignment.Write(alignment, step.To, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (step.Output.Allocate(step.To) is not { } outputPath)
                    return [];
                WriteAtomically(outputPath, rendered, cancellationToken);
                progress?.Report(1);
                return [outputPath];
            }
            if (step.From == "txt" && step.To == "csv")
            {
                var source = HwpxConverter.DecodeText(File.ReadAllBytes(step.InputPath));
                var table = FreeMasterText.Read(source);
                cancellationToken.ThrowIfCancellationRequested();
                if (step.Output.Allocate("csv") is not { } csv)
                    return [];
                CsvFormat.WriteRows(table, csv);
                progress?.Report(1);
                return [csv];
            }
            if (step.From == "vcf")
            {
                var contacts = VCard.Read(File.ReadAllBytes(step.InputPath));
                if (contacts.Count == 0)
                    throw new InvalidDataException("The file has no vCard contacts.");
                cancellationToken.ThrowIfCancellationRequested();
                if (step.Output.Allocate("csv") is not { } csv)
                    return [];
                CsvFormat.WriteRows(VCard.ToRows(contacts), csv);
                progress?.Report(1);
                return [csv];
            }
            if (step.To == "vcf")
            {
                var contacts = VCard.FromRows(CsvData.ReadRows(step.InputPath));
                cancellationToken.ThrowIfCancellationRequested();
                if (step.Output.Allocate("vcf") is not { } vcf)
                    return [];
                File.WriteAllText(vcf, VCard.Write(contacts), Utf8);
                progress?.Report(1);
                return [vcf];
            }
            if (step.From == "ics")
            {
                var components = ICalendar.Read(File.ReadAllBytes(step.InputPath));
                if (components.Count == 0)
                    throw new InvalidDataException("The file has no iCalendar events or to-dos.");
                cancellationToken.ThrowIfCancellationRequested();
                if (step.Output.Allocate("csv") is not { } csv)
                    return [];
                CsvFormat.WriteRows(ICalendar.ToRows(components), csv);
                progress?.Report(1);
                return [csv];
            }
            if (step.To == "ics")
            {
                var components = ICalendar.FromRows(CsvData.ReadRows(step.InputPath));
                if (components.Count == 0)
                    throw new InvalidDataException("The table has no iCalendar events or to-dos.");
                cancellationToken.ThrowIfCancellationRequested();
                if (step.Output.Allocate("ics") is not { } ics)
                    return [];
                File.WriteAllText(ics, ICalendar.Write(components), Utf8);
                progress?.Report(1);
                return [ics];
            }

            var tree = Read(step.InputPath, step.From);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.6);
            if (step.To == "txt")
            {
                // A property list as text to read: the indented key: value listing of YAML, UTF-8 with a BOM like other TXT files.
                var listing = YamlData.Write(tree);
                if (step.Output.Allocate("txt") is not { } txt)
                    return [];
                File.WriteAllText(txt, listing, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                progress?.Report(1);
                return [txt];
            }
            // Built before a path is taken, so a tree the target can't hold leaves no empty file behind.
            var text = step.To switch
            {
                "csv" => null,
                "json" => JsonData.Write(tree),
                "jsonl" => JsonData.WriteLines(tree),
                "yaml" => YamlData.Write(tree),
                "toml" => TomlData.Write(tree),
                "xml" => XmlData.Write(tree),
                _ => throw new NotSupportedException($"Data → {step.To} is not supported."),
            };
            var rows = step.To != "csv" ? null : step.From == "har" ? HarData.Table(tree) : CsvData.Table(tree);
            if (step.Output.Allocate(step.To) is not { } output)
                return [];
            if (rows is not null)
                CsvFormat.WriteRows(rows, output);
            else
                File.WriteAllText(output, text, Utf8);
            progress?.Report(1);
            return [output];
        }, cancellationToken);

    /// <summary>Reads a data file of a format id this engine accepts into the common tree.</summary>
    internal static object? Read(string path, string format) => format switch
    {
        "xml" => XmlData.Read(path),
        "csv" => CsvData.Read(path),
        "plist" => PlistData.Read(path),
        _ => ReadText(HwpxConverter.DecodeText(File.ReadAllBytes(path)), format),
    };

    private static void WriteAtomically(string outputPath, string text, CancellationToken cancellationToken)
    {
        var temp = outputPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temp, text, Utf8);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, outputPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    private static object? ReadText(string text, string format) => format switch
    {
        "json" or "har" => JsonData.Read(text),
        "jsonl" => JsonData.ReadLines(text),
        "yaml" => YamlData.Read(text),
        "toml" => TomlData.Read(text),
        _ => throw new NotSupportedException($"{format} → data is not supported."),
    };
}
