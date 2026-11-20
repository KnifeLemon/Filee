// Word, OpenDocument, RTF, 한글 and web pages → XLSX (#1016): the tables of a DOCX, DOC, ODT, RTF, HWP, HWPX or HTML
// file, one sheet each, so they can be calculated with. The readers that turn these files into HWPX and DOCX already
// know the rows, merged cells and column widths; text outside the tables is left out.
//
// - Every table is its own sheet ("Table 1", "Table 2" …), a table inside a cell too. Unlike PDF → XLSX nothing is
//   joined: a Word table that runs over a page break is one table already, and sections aren't pages.
// - A table without any text (a web page's layout table around another one) gets no sheet.
// - Numbers and dates become real values as in PDF → XLSX (TableSheets). Cells keep their own borders, shading and
//   alignment; leading header rows (Word "repeat as header row", 한글 "제목 셀", HTML <thead> or all-<th> rows) stay in
//   view while scrolling.
// - A file without tables fails with a message instead of an empty workbook.
// - 한글 files are read as the renderer reads them (ReadHangul): an equation in a cell is its text ("40"), not its
//   script ("40~~~"), so a number typed as an equation is a number.
// - DOC (and .dot, .wps) is read by the built-in DOC reader; a Word 6.0 / 95 or Works file it doesn't read is named by
//   FirstUnsupported, as on the other routes from DOC (the edges are Checked).

using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Office;

/// <summary>Documents and web pages → XLSX: each table of the document on its own sheet.</summary>
public sealed class DocumentTablesConverter : IConverter, ICapabilityCheck
{
    /// <summary>
    /// Word documents (and their macro and template variants, Word 97–2003 and WPS), OpenDocument text (and its template
    /// and flat file), RTF, 한글 files, web pages and saved web pages.
    /// </summary>
    private static readonly string[] Sources =
        ["docx", "docm", "dotx", "dotm", .. Hwp.Hwpx.Doc.DocReader.Formats, "odt", "ott", "fodt", "rtf", "hwp", "hwpx", "html", "mhtml"];

    public string Id => "document-tables";
    public string DisplayName => "Document tables (built-in)";
    public int MaxParallelism => 0;

    /// <summary>
    /// Only from the document itself, and never a step on a longer route: a PDF or a picture turned into a DOCX would
    /// reach spreadsheets this way (and show spreadsheet presets on every file that becomes a Word document), and a
    /// workbook of a document's tables must not go on to JSON or XML.
    /// </summary>
    public IReadOnlyList<ConversionEdge> Edges { get; } =
        [.. Sources.SelectMany(from => new[] { "xlsx", "ods", "csv" }
            .Select(to => new ConversionEdge(from, to, DirectOnly: true, Checked: DocumentReaders.Checked(from))))];

    public EngineStatus GetStatus() =>
        EngineStatus.Available("DOCX, DOC, ODT, RTF, HWP, HWPX, HTML, MHTML → XLSX, ODS, CSV", EngineVersions.BuiltIn);

    /// <summary>A Word 6.0 / 95 or Works file with a .doc or .wps name, which the DOC reader doesn't read.</summary>
    public string? FirstUnsupported(string sourcePath, string sourceFormat) => DocumentReaders.Unsupported(sourcePath, sourceFormat);

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var document = step.From is "hwp" or "hwpx"
            ? await Task.Run(() => ReadHangul(step.InputPath, step.From, step.WorkDirectory), cancellationToken)
            : await DocumentReaders.ReadAsync(step.InputPath, step.From, step.WorkDirectory, cancellationToken);
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

    /// <summary>
    /// A 한글 file read as the renderer reads it: its equations as equations, so a number typed as an equation (common
    /// in 한글 tables) reads as that number rather than as its script ("40~~~").
    /// </summary>
    private static HDocument ReadHangul(string path, string format, string workDirectory)
    {
        var media = Path.Combine(workDirectory, $"media-{Guid.NewGuid():N}");
        return format == "hwp" ? Hwp.Binary.HwpReader.ReadForLayout(path, media) : HwpxReader.ReadForLayout(path, media);
    }

    /// <summary>Rows of a worksheet (Excel's limit): a longer table can't be one sheet.</summary>
    internal const int MaxSheetRows = 1_048_576;

    /// <summary>
    /// Sheets of one workbook. A document may hold up to <see cref="Documents.DocumentLimits.MaxObjects"/> tables, far
    /// more than a workbook should get as tabs: Excel opens a few thousand sheets slowly.
    /// </summary>
    internal const int MaxSheets = 10_000;

    /// <summary>The document's tables as sheets, in reading order: one per table that has any text.</summary>
    /// <exception cref="ConversionRefusedException">
    /// The tables need more sheets, rows or cells than a workbook takes (<see cref="Documents.DocumentLimits"/>): counted
    /// before anything is written, so a span over thousands of rows and columns is refused at once.
    /// </exception>
    internal static Workbook Build(HDocument document)
    {
        var tables = new List<HTable>();
        long cells = 0;
        foreach (var table in Tables(document.Sections.SelectMany(s => s.Blocks)))
        {
            if (!table.Rows.SelectMany(r => r.Cells).Any(c => TableSheets.Text(c).Length > 0))
                continue;
            if (tables.Count == MaxSheets)
                throw Documents.DocumentLimits.Exceeded($"The document has more than {Documents.DocumentLimits.Count(MaxSheets)} tables, one sheet each.");
            if (table.Rows.Count > MaxSheetRows)
                throw Documents.DocumentLimits.Exceeded($"A table has more than {Documents.DocumentLimits.Count(MaxSheetRows)} rows.");
            // Every position a cell covers is written (a merged area keeps its borders); the whole workbook gets the
            // budget of one table.
            cells += TableSheets.Positions(table);
            if (cells > HwpxWriter.MaxTableCells)
                throw Documents.DocumentLimits.Exceeded(
                    $"The tables have more than {Documents.DocumentLimits.Count(HwpxWriter.MaxTableCells)} cells (rows × columns) together.");
            tables.Add(table);
        }

        var book = new Workbook();
        foreach (var table in tables)
        {
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

    /// <summary>
    /// Every table with rows, depth first: a table, then the tables inside its cells. Walked with a stack of its own,
    /// so the depth the readers allow (<see cref="Documents.DocumentLimits.MaxNestingDepth"/>) costs no nested iterators.
    /// </summary>
    private static IEnumerable<HTable> Tables(IEnumerable<HBlock> blocks)
    {
        var pending = new Stack<IEnumerator<HTable>>();
        pending.Push(blocks.OfType<HTable>().GetEnumerator());
        while (pending.Count > 0)
        {
            var current = pending.Peek();
            if (!current.MoveNext())
            {
                current.Dispose();
                pending.Pop();
                continue;
            }
            var table = current.Current;
            if (table.Rows.Count > 0)
                yield return table;
            pending.Push(table.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Blocks).OfType<HTable>().GetEnumerator());
        }
    }
}
