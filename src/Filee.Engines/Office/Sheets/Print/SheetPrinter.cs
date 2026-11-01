// A workbook printed the way Excel prints the entire workbook to PDF: every visible sheet with something on it, each
// with its own page setup (SheetPagination), its pages drawn by SheetPageRenderer, numbered on from the sheet before
// unless a sheet sets its first page number, &N counting every page. The pages are written by PdfPageWriter (fonts
// subset by Filee); a workbook with several sheets gets a bookmark per sheet.

using Filee.Engines.Documents;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Office.Sheets;

/// <summary>Where the workbook came from, for the &amp;F and &amp;Z header fields.</summary>
internal sealed record SheetPrintSource(string FileName, string Folder)
{
    public static SheetPrintSource Of(string path) =>
        new(Path.GetFileName(path), (Path.GetDirectoryName(Path.GetFullPath(path)) ?? "") + Path.DirectorySeparatorChar);
}

internal static class SheetPrinter
{
    /// <summary>Bytes of one picture read at most (a sheet's or a header's).</summary>
    private const int MaxPictureBytes = 64 * 1024 * 1024;

    /// <summary>The workbook as a PDF.</summary>
    /// <param name="progress">Pages drawn and written, as a fraction.</param>
    public static byte[] ToPdf(Workbook book, SheetPrintSource source, CancellationToken cancellationToken = default,
        IProgress<double>? progress = null, DocumentLimits? limits = null, DateTime? now = null)
    {
        using var printed = Print(book, source, limits ?? DocumentLimits.Default, now ?? DateTime.Now,
            progress is null ? null : new Progress<double>(p => progress.Report(p * 0.5)), cancellationToken);
        using var output = new MemoryStream();
        PdfPageWriter.Write(output, printed.Pages, new PdfDocumentInfo { Outline = printed.Outline },
            progress is null ? null : new Progress<double>(p => progress.Report(0.5 + p * 0.5)), cancellationToken);
        return output.ToArray();
    }

    /// <summary>The printed pages, and the pictures they draw (disposed with the result).</summary>
    internal sealed class Printed : IDisposable
    {
        public List<PageContent> Pages { get; } = [];
        public List<OutlineEntry> Outline { get; } = [];
        public List<SKImage> Images { get; } = [];
        public SheetFonts? Fonts { get; set; }

        public void Dispose()
        {
            foreach (var image in Images)
                image.Dispose();
            Fonts?.Dispose();
        }
    }

    internal static Printed Print(Workbook book, SheetPrintSource source, DocumentLimits limits, DateTime now,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var printed = new Printed();
        try
        {
            var fonts = printed.Fonts = new SheetFonts();
            var guard = new LayoutGuard(limits, cancellationToken);
            var sheets = new List<(Worksheet Sheet, SheetIndex Index, SheetPrintPlan Plan)>();
            var count = 0;
            foreach (var sheet in book.Sheets)
            {
                guard.Paragraph();
                var index = new SheetIndex(sheet);
                var plan = SheetPagination.Plan(book, sheet, index, fonts, limits, count, cancellationToken);
                if (plan.Pages.Count == 0)
                    continue;
                count += plan.Pages.Count;
                sheets.Add((sheet, index, plan));
            }

            var number = 1;
            foreach (var (sheet, index, plan) in sheets)
            {
                var conditional = plan.Pages.Count > 0 ? ConditionalFormatting.Apply(sheet, Bounds(plan.Pages)) : [];
                var pictures = Pictures(sheet, printed.Images);
                var headerPictures = HeaderPictures(sheet, printed.Images);
                var renderer = new SheetPageRenderer(book, plan, index, fonts, conditional, pictures);
                if (sheet.PageSetup.FirstPageNumber is { } first)
                    number = first;
                renderer.FirstPageNumber = number;
                if (sheets.Count > 1)
                    printed.Outline.Add(new OutlineEntry(sheet.Name, printed.Pages.Count, 0, 0));
                foreach (var page in plan.Pages)
                {
                    guard.Paragraph();
                    printed.Pages.Add(renderer.Draw(page, new PageFields(number, count, sheet.Name, source.FileName, source.Folder, now, headerPictures)));
                    guard.PageFinished(empty: false);
                    number++;
                    progress?.Report((double)printed.Pages.Count / count);
                }
            }
            if (printed.Pages.Count == 0)
            {
                // Nothing to print (Excel refuses): one empty page of the first sheet's paper.
                var (width, height) = (book.Sheets.FirstOrDefault()?.PageSetup ?? new SheetPageSetup()).PageSize;
                printed.Pages.Add(new PageContent((float)width, (float)height));
            }
            return printed;
        }
        catch
        {
            printed.Dispose();
            throw;
        }
    }

    private static CellRange Bounds(IReadOnlyList<SheetPage> pages)
    {
        int top = int.MaxValue, left = int.MaxValue, bottom = -1, right = -1;
        foreach (var page in pages)
        {
            top = Math.Min(top, page.Top);
            left = Math.Min(left, page.Left);
            bottom = Math.Max(bottom, page.Bottom);
            right = Math.Max(right, page.Right);
        }
        return new CellRange(top, left, bottom, right);
    }

    private static List<(SheetPicture, SKImage)> Pictures(Worksheet sheet, List<SKImage> images)
    {
        var result = new List<(SheetPicture, SKImage)>();
        foreach (var picture in sheet.Pictures)
        {
            if (Decode(picture.Data) is { } image)
            {
                images.Add(image);
                result.Add((picture, image));
            }
        }
        return result;
    }

    private static Dictionary<string, (SKImage, double, double)> HeaderPictures(Worksheet sheet, List<SKImage> images)
    {
        var result = new Dictionary<string, (SKImage, double, double)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, (data, width, height)) in sheet.PageSetup.HeaderFooter.Pictures)
        {
            if (Decode(data) is { } image)
            {
                images.Add(image);
                result[key] = (image, width > 0 ? width : image.Width * 0.75, height > 0 ? height : image.Height * 0.75);
            }
        }
        return result;
    }

    /// <summary>A picture Skia can decode whose header claims no more pixels than the limit; null otherwise.</summary>
    internal static SKImage? Decode(byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxPictureBytes)
            return null;
        using (var stream = new MemoryStream(data, writable: false))
        {
            if (PictureSize.Read(stream) is not { } size || (long)size.Width * size.Height > DocumentLimits.Default.MaxPicturePixels)
                return null;
        }
        return SKImage.FromEncodedData(data);
    }
}
