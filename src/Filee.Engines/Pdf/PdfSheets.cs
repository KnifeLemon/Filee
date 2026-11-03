// "Pages per sheet" (#737): several PDF pages on each sheet (N-up). Each page goes in as a vector form (PDFsharp's
// XPdfForm), cut to its crop box and turned by its /Rotate as a viewer shows it, scaled to fit its cell and centred.
// Nothing is rasterised, so text stays text and the file stays small.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Pdf;

/// <summary>
/// A page as a viewer shows it: the whole page upright (<paramref name="Width"/> × <paramref name="Height"/>) and its
/// visible part, the crop box, in points from the top left of it.
/// </summary>
internal readonly record struct ShownPage(double Width, double Height, XRect Visible);

internal static class PdfSheets
{
    private const double PointsPerMm = 72 / 25.4;

    /// <summary>Columns and rows for <paramref name="pages"/> per sheet: the long side of the sheet takes more.</summary>
    internal static (int Columns, int Rows) Grid(int pages, bool landscape)
    {
        var few = (int)Math.Sqrt(pages);
        while (pages % few != 0)
            few--;
        var many = pages / few;
        return landscape ? (many, few) : (few, many);
    }

    /// <summary>
    /// Where the crop box of a page with <paramref name="media"/>, <paramref name="crop"/> and <paramref name="rotate"/>
    /// (clockwise, as /Rotate) is in the page a viewer shows.
    /// </summary>
    internal static ShownPage Shown(PdfRectangle media, PdfRectangle crop, int rotate)
    {
        var (mw, mh) = (media.Width, media.Height);
        // The crop box inside the media box, from its bottom left.
        var u1 = Math.Clamp(crop.X1 - media.X1, 0, mw);
        var u2 = Math.Clamp(crop.X2 - media.X1, 0, mw);
        var v1 = Math.Clamp(crop.Y1 - media.Y1, 0, mh);
        var v2 = Math.Clamp(crop.Y2 - media.Y1, 0, mh);
        if (u2 <= u1 || v2 <= v1)
            (u1, u2, v1, v2) = (0, mw, 0, mh);
        return (((rotate % 360) + 360) % 360) switch
        {
            90 => new ShownPage(mh, mw, new XRect(v1, u1, v2 - v1, u2 - u1)),
            180 => new ShownPage(mw, mh, new XRect(mw - u2, v1, u2 - u1, v2 - v1)),
            270 => new ShownPage(mh, mw, new XRect(mh - v2, mw - u2, v2 - v1, u2 - u1)),
            _ => new ShownPage(mw, mh, new XRect(u1, mh - v2, u2 - u1, v2 - v1)),
        };
    }

    private static ShownPage Shown(PdfPage page) => Shown(page.MediaBox, page.EffectiveCropBoxReadOnly, page.Rotate);

    /// <summary>The sheet: the paper, standing the way the options say, or (auto) the way that fits the first page larger.</summary>
    internal static XSize SheetSize(PdfOptions options, int pages, ShownPage first)
    {
        var (w, h) = options.SheetPaper switch
        {
            PdfSheetPaper.Letter => (612.0, 792.0),
            PdfSheetPaper.A3 => (841.89, 1190.55),
            PdfSheetPaper.SameAsFirstPage => (first.Visible.Width, first.Visible.Height),
            _ => (595.28, 841.89),
        };
        var portrait = new XSize(Math.Min(w, h), Math.Max(w, h));
        var landscape = new XSize(portrait.Height, portrait.Width);
        return options.SheetOrientation switch
        {
            PdfSheetOrientation.Portrait => portrait,
            PdfSheetOrientation.Landscape => landscape,
            _ => Fit(first.Visible.Size, Cells(landscape, options, pages)[0].Size) > Fit(first.Visible.Size, Cells(portrait, options, pages)[0].Size)
                ? landscape : portrait,
        };
    }

    private static double Fit(XSize page, XSize cell) => Math.Min(cell.Width / page.Width, cell.Height / page.Height);

    /// <summary>The cells of a sheet in the order the pages fill them, inside the margin and with the gap between them.</summary>
    internal static IReadOnlyList<XRect> Cells(XSize sheet, PdfOptions options, int pages)
    {
        var (columns, rows) = Grid(pages, sheet.Width > sheet.Height);
        // A margin or gap too large for the sheet is cut down, so every cell keeps some room.
        var margin = Math.Clamp(options.SheetMarginMm * PointsPerMm, 0, Math.Min(sheet.Width, sheet.Height) / 4);
        var inner = new XSize(sheet.Width - 2 * margin, sheet.Height - 2 * margin);
        var gap = Math.Clamp(options.SheetGapMm * PointsPerMm, 0, Math.Min(inner.Width / columns, inner.Height / rows) / 2);
        var cell = new XSize((inner.Width - (columns - 1) * gap) / columns, (inner.Height - (rows - 1) * gap) / rows);
        return [.. Enumerable.Range(0, pages).Select(k =>
        {
            var (column, row) = options.SheetOrder == PdfSheetOrder.DownFirst ? (k / rows, k % rows) : (k % columns, k / columns);
            return new XRect(margin + column * (cell.Width + gap), margin + row * (cell.Height + gap), cell.Width, cell.Height);
        })];
    }

    /// <summary>
    /// Writes the pages of <paramref name="input"/> (those of <see cref="PdfOptions.PageRange"/>), <see cref="PdfOptions.PagesPerSheet"/>
    /// to a sheet, to <paramref name="output"/>.
    /// </summary>
    internal static void Write(string input, string output, PdfOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        List<(int Index, ShownPage Shown)> pages;
        using (var source = PdfSharpConverter.Open(input, PdfDocumentOpenMode.Import))
            pages = [.. PageRange.Parse(options.PageRange, source.PageCount).Select(i => (i, Shown(source.Pages[i])))];
        if (pages.Count == 0)
            throw new InvalidOperationException("The PDF has no pages in the chosen range.");

        var perSheet = Math.Clamp(options.PagesPerSheet, 2, 16);
        var sheet = SheetSize(options, perSheet, pages[0].Shown);
        var cells = Cells(sheet, options, perSheet);
        using var form = XPdfForm.FromFile(input);
        using var document = new PdfDocument();
        for (var first = 0; first < pages.Count; first += perSheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(sheet.Width);
            page.Height = XUnit.FromPoint(sheet.Height);
            using (var gfx = XGraphics.FromPdfPage(page))
            {
                for (var k = 0; k < perSheet && first + k < pages.Count; k++)
                {
                    form.PageIndex = pages[first + k].Index;
                    Draw(gfx, form, pages[first + k].Shown, cells[k], options.SheetBorders);
                }
            }
            progress?.Report(Math.Min(1, (first + perSheet) / (double)pages.Count));
        }
        document.Save(output);
    }

    /// <summary>
    /// Draws the visible part of a page as large as fits in <paramref name="cell"/>, centred; a page that fits larger
    /// turned (landscape in a portrait cell) is turned a quarter to the left.
    /// </summary>
    private static void Draw(XGraphics gfx, XPdfForm form, ShownPage page, XRect cell, bool border)
    {
        var visible = page.Visible;
        var turn = Fit(new XSize(visible.Height, visible.Width), cell.Size) > Fit(visible.Size, cell.Size) * 1.001;
        var state = gfx.Save();
        gfx.TranslateTransform(cell.X + cell.Width / 2, cell.Y + cell.Height / 2);
        if (turn)
            gfx.RotateTransform(-90);
        var room = turn ? new XSize(cell.Height, cell.Width) : cell.Size;
        var scale = Fit(visible.Size, room);
        var shown = new XRect(-visible.Width * scale / 2, -visible.Height * scale / 2, visible.Width * scale, visible.Height * scale);
        var clip = gfx.Save();
        gfx.IntersectClip(shown);
        // XPdfForm turns the page by its /Rotate (a /Matrix) but keeps the box of the unturned page, and the turned
        // content sits at that box's bottom: draw it at the unturned size, moved so the turned page lands on top.
        var (width, height) = (form.PointWidth * scale, form.PointHeight * scale);
        gfx.DrawImage(form, shown.X - visible.X * scale, shown.Y - visible.Y * scale + page.Height * scale - height, width, height);
        gfx.Restore(clip);
        if (border)
            gfx.DrawRectangle(new XPen(XColors.Gray, 0.5), shown);
        gfx.Restore(state);
    }
}
