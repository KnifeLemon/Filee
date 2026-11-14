// DOCX / HTML → XLSX (#1016): the tables of a Word document or a web page, one sheet each, so they can be calculated
// with. The readers that turn these files into HWPX and DOCX already know the rows, merged cells and column widths;
// text outside the tables is left out.
//
// - Every table is its own sheet ("Table 1", "Table 2" …), a table inside a cell too. Unlike PDF → XLSX nothing is
//   joined: a Word table that runs over a page break is one table already, and sections aren't pages.
// - A table without any text (a web page's layout table around another one) gets no sheet.
// - Numbers and dates become real values as in PDF → XLSX (TableSheets). Cells keep their own borders and shading;
//   leading header rows (DOCX "repeat as header row", HTML <thead> or all-<th> rows) stay in view while scrolling.
// - A file without tables fails with a message instead of an empty workbook.
// - DOC isn't read here: it needs LibreOffice, and this edge is direct only (see Edges).

using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Office;

/// <summary>DOCX / HTML → XLSX: each table of the document on its own sheet.</summary>
public sealed class DocumentTablesConverter : IConverter
{
    /// <summary>Word documents (and their macro and template variants), web pages and saved web pages.</summary>
    private static readonly string[] Sources = ["docx", "docm", "dotx", "dotm", "html", "mhtml"];

    public string Id => "document-tables";
    public string DisplayName => "Document tables (built-in)";
    public int MaxParallelism => 0;

    /// <summary>
    /// Only from the document itself, and never a step on a longer route: a PDF or a picture turned into a DOCX would
    /// reach spreadsheets this way (and show spreadsheet presets on every file that becomes a Word document), and a
    /// workbook of a document's tables must not go on to JSON or XML.
    /// </summary>
    public IReadOnlyList<ConversionEdge> Edges { get; } =
        [.. Sources.SelectMany(from => new[] { "xlsx", "ods", "csv" }.Select(to => new ConversionEdge(from, to, DirectOnly: true)))];

    public EngineStatus GetStatus() => EngineStatus.Available("DOCX, HTML, MHTML → XLSX, ODS, CSV", EngineVersions.BuiltIn);

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var document = await DocumentReaders.ReadAsync(step.InputPath, step.From, step.WorkDirectory, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0.7);
        var book = Build(document);
        if (book.Sheets.Count == 0)
            throw new ConversionRefusedException("error.document_no_tables");
        // As the spreadsheet engine writes: CSV gets one file per sheet.
        var outputs = SpreadsheetConverter.Write(book, step);
        progress?.Report(1);
        return outputs;
    }

    /// <summary>The document's tables as sheets, in reading order: one per table that has any text.</summary>
    internal static Workbook Build(HDocument document)
    {
        var book = new Workbook();
        foreach (var table in Tables(document.Sections.SelectMany(s => s.Blocks)))
        {
            if (!table.Rows.SelectMany(r => r.Cells).Any(c => TableSheets.Text(c).Length > 0))
                continue;
            var sheet = new Worksheet($"Table {book.Sheets.Count + 1}");
            TableSheets.SetColumnWidths(sheet, table);
            TableSheets.Place(sheet, table, 0, 0, drawn: false);
            // Header rows stay in view, unless the whole table is header (nothing would scroll under them).
            var headers = table.Rows.TakeWhile(r => r.Header).Count();
            if (headers < table.Rows.Count)
                sheet.FrozenRows = headers;
            book.Sheets.Add(sheet);
        }
        return book;
    }

    /// <summary>Every table with rows, depth first: a table, then the tables inside its cells.</summary>
    private static IEnumerable<HTable> Tables(IEnumerable<HBlock> blocks)
    {
        foreach (var table in blocks.OfType<HTable>())
        {
            if (table.Rows.Count > 0)
                yield return table;
            foreach (var nested in Tables(table.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Blocks)))
                yield return nested;
        }
    }
}
