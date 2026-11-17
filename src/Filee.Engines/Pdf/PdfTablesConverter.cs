// PDF → XLSX (#267): the tables drawn with lines in a PDF, one sheet each. The tables are the ones PDF → DOCX finds
// (PdfDocumentReader's ruled tables, with merged cells, column widths and row heights); text outside them is left out.
//
// - A table that goes on at the top of the next page with the same columns joins the one before it on one sheet; a
//   header row repeated on the new page is left out.
// - Numbers and dates become real values (TableSheets, shared with DOCX / HTML → XLSX).
// - A PDF without such a table (or a scan, which has no text) fails with a message instead of an empty workbook.
//   Tables aligned by white space only aren't found, as in PDF → DOCX.

using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Pdf;

/// <summary>PDF → XLSX: each table drawn with lines on its own sheet.</summary>
public sealed class PdfTablesConverter : IConverter
{
    public string Id => "pdf-tables";
    public string DisplayName => "PDF tables (built-in)";
    public int MaxParallelism => 0;

    /// <summary>
    /// Only from a PDF, and never a step on a longer route: an image turned into a PDF has no ruled tables, so
    /// jpg → pdf → xlsx would always fail (and show spreadsheet presets on pictures).
    /// </summary>
    public IReadOnlyList<ConversionEdge> Edges { get; } =
        [new("pdf", "xlsx", DirectOnly: true), new("pdf", "ods", DirectOnly: true), new("pdf", "csv", DirectOnly: true)];

    public EngineStatus GetStatus() => EngineStatus.Available("PDF → XLSX, ODS, CSV", EngineVersions.BuiltIn);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var document = PdfDocumentReader.Read(step.InputPath, Path.Combine(step.WorkDirectory, $"media-{Guid.NewGuid():N}"));
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.7);
            var book = Build(document);
            if (book.Sheets.Count == 0)
                throw new ConversionRefusedException("error.pdf_no_tables");
            // As the spreadsheet engine writes: CSV gets one file per sheet.
            var outputs = SpreadsheetConverter.Write(book, step);
            progress?.Report(1);
            return outputs;
        }, cancellationToken);

    /// <summary>A table and the page it is on (0-based).</summary>
    private sealed record Placed(HTable Table, int Page)
    {
        /// <summary>Nothing (no table, no text) comes before it on its page.</summary>
        public bool FirstOnPage { get; set; }

        /// <summary>Nothing (no table, no text) comes after it on its page.</summary>
        public bool LastOnPage { get; set; }
    }

    /// <summary>The document's tables as sheets: one per table, a table going on over the next page joined.</summary>
    internal static Workbook Build(HDocument document)
    {
        // Everything on the pages in order: tables, and null for text (empty paragraphs, such as the one carrying a page
        // break, don't count), so a table only "goes on" when nothing at all sits between it and the next page's table.
        var items = new List<(Placed? Table, int Page)>();
        var page = -1;
        foreach (var section in document.Sections)
        {
            page++;
            foreach (var block in section.Blocks)
            {
                if (block is HParagraph paragraph)
                {
                    if (paragraph.PageBreakBefore)
                        page++;
                    if (paragraph.Inlines.OfType<HText>().Any(t => t.Text.Trim().Length > 0))
                        items.Add((null, page));
                }
                else if (block is HTable table && table.Rows.Count > 0)
                {
                    items.Add((new Placed(table, page), page));
                }
            }
        }
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Table is not { } placed)
                continue;
            placed.FirstOnPage = i == 0 || items[i - 1].Page != placed.Page;
            placed.LastOnPage = i == items.Count - 1 || items[i + 1].Page != placed.Page;
        }
        var tables = items.Where(i => i.Table is not null).Select(i => i.Table!).ToList();

        var book = new Workbook();
        Worksheet? sheet = null;
        Placed? previous = null;
        string[]? header = null;
        var row = 0;
        foreach (var placed in tables)
        {
            var goesOn = previous is not null && sheet is not null && previous.LastOnPage && placed.FirstOnPage
                         && placed.Page == previous.Page + 1 && SameColumns(previous.Table, placed.Table);
            var skip = 0;
            if (goesOn && header is not null && TableSheets.RowTexts(placed.Table.Rows[0]).SequenceEqual(header))
                skip = 1;
            if (!goesOn)
            {
                sheet = new Worksheet($"Table {book.Sheets.Count + 1}");
                book.Sheets.Add(sheet);
                row = 0;
                header = TableSheets.RowTexts(placed.Table.Rows[0]);
                TableSheets.SetColumnWidths(sheet, placed.Table);
            }
            row = TableSheets.Place(sheet!, placed.Table, row, skip, drawn: true);
            previous = placed;
        }
        return book;
    }

    /// <summary>Same number of columns, each within 5 % of the other table's width (or no widths to compare).</summary>
    private static bool SameColumns(HTable a, HTable b)
    {
        if (a.ColumnCount != b.ColumnCount)
            return false;
        if (a.ColumnWidths is not { } left || b.ColumnWidths is not { } right || left.Length != right.Length)
            return true;
        return left.Zip(right).All(w => Math.Abs(w.First - w.Second) <= 0.05 * Math.Max(w.First, w.Second) + 100);
    }
}
