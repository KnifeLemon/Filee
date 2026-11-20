// Word, OpenDocument, RTF, 한글, web pages and UYAP documents → XLSX (#1016, #1588): the tables of a DOCX, DOC, ODT,
// RTF, HWP, HWPX, HTML or UDF file, one sheet each, so they can be calculated with. The readers that turn these files
// into HWPX and DOCX already know the rows, merged cells and column widths; text outside the tables is left out.
//
// - Every table is its own sheet ("Table 1", "Table 2" …), a table inside a cell, a text box or a shape with text (Word
//   text boxes, 한글 글상자 and 그리기 개체) too. Unlike PDF → XLSX nothing is joined: a Word table that runs over a
//   page break is one table already, and sections aren't pages.
// - A table without any text (a web page's layout table around another one) gets no sheet.
// - A table of one cell is a box around text (a 보기 box, a title, a source tag, a figure with its caption), not data:
//   when the document has a table of more cells, the boxes get no sheet; when it has only boxes, they share one sheet,
//   a row each in reading order, so their text still arrives.
// - Rows under a merged cell are made tall enough for its text, measured in the cell's font as Excel fits a row
//   (TableSheets.FitMergedRows).
// - Numbers and dates become real values as in PDF → XLSX (TableSheets). Cells keep their own borders, shading and
//   alignment; leading header rows (Word "repeat as header row", 한글 "제목 셀", HTML <thead> or all-<th> rows) stay in
//   view while scrolling.
// - A file without tables fails with a message instead of an empty workbook.
// - 한글 files are read as the renderer reads them (ReadHangul): an equation in a cell is its text ("40"), not its
//   script ("40~~~"), so a number typed as an equation is a number. Every equation in a cell is written as plain text
//   (EquationScript.Plain): "20 ^{이상}" reads "20 이상", "cm ^{3}" reads "cm³".
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
    /// and flat file), RTF, 한글 files, web pages, saved web pages and UYAP documents.
    /// </summary>
    private static readonly string[] Sources =
        ["docx", "docm", "dotx", "dotm", .. Hwp.Hwpx.Doc.DocReader.Formats, "odt", "ott", "fodt", "rtf", "hwp", "hwpx", "html", "mhtml", "udf"];

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
        EngineStatus.Available("DOCX, DOC, ODT, RTF, HWP, HWPX, HTML, MHTML, UDF → XLSX, ODS, CSV", EngineVersions.BuiltIn);

    /// <summary>A Word 6.0 / 95 or Works file with a .doc or .wps name, which the DOC reader doesn't read.</summary>
    public string? FirstUnsupported(string sourcePath, string sourceFormat) => DocumentReaders.Unsupported(sourcePath, sourceFormat);

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var document = step.From is "hwp" or "hwpx"
            ? await Task.Run(() => ReadHangul(step.InputPath, step.From, step.WorkDirectory), cancellationToken)
            : await DocumentReaders.ReadAsync(step.InputPath, step.From, step.WorkDirectory, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0.7);
        var book = Build(document, cancellationToken);
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

    /// <summary>
    /// The document's tables as sheets, in reading order: one per table that has any text, or one sheet of boxes when
    /// every table is a single cell (see the file header).
    /// </summary>
    /// <exception cref="ConversionRefusedException">
    /// The tables need more sheets, rows or cells than a workbook takes (<see cref="Documents.DocumentLimits"/>): counted
    /// before anything is written, so a span over thousands of rows and columns is refused at once.
    /// </exception>
    internal static Workbook Build(HDocument document, CancellationToken cancellationToken = default)
    {
        var tables = new List<HTable>();
        var boxes = new List<HTable>();
        long cells = 0;
        foreach (var table in Tables(document.Sections.SelectMany(s => s.Blocks)))
        {
            if (!table.Rows.SelectMany(r => r.Cells).Any(c => TableSheets.Text(c).Length > 0))
                continue;
            var box = IsBox(table);
            if (!box && tables.Count == MaxSheets)
                throw Documents.DocumentLimits.Exceeded($"The document has more than {Documents.DocumentLimits.Count(MaxSheets)} tables, one sheet each.");
            if (box ? boxes.Count == MaxSheetRows : table.Rows.Count > MaxSheetRows)
                throw Documents.DocumentLimits.Exceeded($"A table has more than {Documents.DocumentLimits.Count(MaxSheetRows)} rows.");
            // Every position a cell covers is written (a merged area keeps its borders); the whole workbook gets the
            // budget of one table.
            cells += TableSheets.Positions(table);
            if (cells > HwpxWriter.MaxTableCells)
                throw Documents.DocumentLimits.Exceeded(
                    $"The tables have more than {Documents.DocumentLimits.Count(HwpxWriter.MaxTableCells)} cells (rows × columns) together.");
            (box ? boxes : tables).Add(table);
        }

        var book = new Workbook();
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sheet = new Worksheet($"Table {book.Sheets.Count + 1}");
            TableSheets.SetColumnWidths(sheet, table);
            TableSheets.Place(sheet, table, 0, 0, drawn: false);
            // Header rows stay in view, unless the whole table is header (nothing would scroll under them).
            var headers = table.Rows.TakeWhile(r => r.Header).Count();
            if (headers < table.Rows.Count)
                sheet.FrozenRows = headers;
            book.Sheets.Add(sheet);
        }
        if (tables.Count == 0 && boxes.Count > 0)
        {
            // Only boxes: one column as wide as the widest of them, a box a row.
            var sheet = new Worksheet("Table 1");
            var row = 0;
            foreach (var box in boxes)
            {
                var sized = new Worksheet("widths");
                TableSheets.SetColumnWidths(sized, box);
                if (sized.ColumnWidths.TryGetValue(0, out var width) && width > sheet.ColumnWidths.GetValueOrDefault(0))
                    sheet.ColumnWidths[0] = width;
                row = TableSheets.Place(sheet, box, row, 0, drawn: false);
            }
            book.Sheets.Add(sheet);
        }
        TableSheets.FitMergedRows(book, cancellationToken);
        return book;
    }

    /// <summary>A table of one cell: a box drawn around text rather than rows of data.</summary>
    private static bool IsBox(HTable table) => table.Rows.Sum(r => r.Cells.Count) == 1;

    /// <summary>
    /// Every table with rows, depth first: a table, then the tables inside its cells, and the tables in a paragraph's
    /// text boxes where the paragraph is (the readers make a shape with text a text box). Walked with a stack of its
    /// own, so the depth the readers allow (<see cref="Documents.DocumentLimits.MaxNestingDepth"/>) costs no nested
    /// iterators.
    /// </summary>
    private static IEnumerable<HTable> Tables(IEnumerable<HBlock> blocks)
    {
        var pending = new Stack<IEnumerator<HBlock>>();
        pending.Push(blocks.GetEnumerator());
        while (pending.Count > 0)
        {
            var current = pending.Peek();
            if (!current.MoveNext())
            {
                current.Dispose();
                pending.Pop();
                continue;
            }
            switch (current.Current)
            {
                case HTable table:
                    if (table.Rows.Count > 0)
                        yield return table;
                    pending.Push(table.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Blocks).GetEnumerator());
                    break;
                case HParagraph { Inlines.Count: > 0 } paragraph:
                    // A link's content too, as the paragraph shows it.
                    pending.Push(HDocumentWalker.InlinesOf(paragraph.Inlines).OfType<HTextBox>().SelectMany(b => b.Blocks).GetEnumerator());
                    break;
            }
        }
    }
}
