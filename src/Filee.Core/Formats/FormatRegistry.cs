// Central list of supported formats and extension lookup.
// To add a new format: add one line to Known and give at least one converter an edge for it.

namespace Filee.Core.Formats;

/// <summary>Lookup table of every format Filee knows about.</summary>
public static class FormatRegistry
{
    /// <summary>All known formats. Order is the order shown in format pickers.</summary>
    public static IReadOnlyList<FileFormat> Known { get; } =
    [
        // Raster images
        new("png", "PNG", FormatCategory.Image, ["png"]),
        new("jpg", "JPG", FormatCategory.Image, ["jpg", "jpeg", "jpe", "jfif"]),
        new("webp", "WEBP", FormatCategory.Image, ["webp"]),
        new("tiff", "TIFF", FormatCategory.Image, ["tiff", "tif"]),
        new("bmp", "BMP", FormatCategory.Image, ["bmp", "dib"]),
        new("gif", "GIF", FormatCategory.Image, ["gif"]),
        new("ico", "ICO", FormatCategory.Image, ["ico"]),
        new("avif", "AVIF", FormatCategory.Image, ["avif"]),
        new("heic", "HEIC", FormatCategory.Image, ["heic", "heif"]),

        // PDF
        new("pdf", "PDF", FormatCategory.Pdf, ["pdf"]),

        // Word processing
        new("docx", "DOCX", FormatCategory.Document, ["docx"]),
        new("doc", "DOC", FormatCategory.Document, ["doc"]),
        new("odt", "ODT", FormatCategory.Document, ["odt"]),
        new("rtf", "RTF", FormatCategory.Document, ["rtf"]),

        // Korean word processor (Hancom)
        new("hwpx", "HWPX", FormatCategory.Hwp, ["hwpx"]),
        new("hwp", "HWP", FormatCategory.Hwp, ["hwp"]),

        // Spreadsheets
        new("xlsx", "XLSX", FormatCategory.Spreadsheet, ["xlsx"]),
        new("xls", "XLS", FormatCategory.Spreadsheet, ["xls"]),
        new("ods", "ODS", FormatCategory.Spreadsheet, ["ods"]),
        new("csv", "CSV", FormatCategory.Spreadsheet, ["csv"]),

        // Presentations
        new("pptx", "PPTX", FormatCategory.Presentation, ["pptx"]),
        new("ppt", "PPT", FormatCategory.Presentation, ["ppt"]),
        new("odp", "ODP", FormatCategory.Presentation, ["odp"]),

        // Plain text / markup
        new("txt", "TXT", FormatCategory.Text, ["txt"]),
        new("md", "Markdown", FormatCategory.Text, ["md", "markdown"]),
        new("html", "HTML", FormatCategory.Text, ["html", "htm"]),
    ];

    private static readonly Dictionary<string, FileFormat> ById =
        Known.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, FileFormat> ByExtension =
        Known.SelectMany(f => f.Extensions.Select(e => (e, f)))
             .ToDictionary(x => x.e, x => x.f, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the format with the given id, or <c>null</c>.</summary>
    public static FileFormat? FindById(string id) => ById.GetValueOrDefault(id);

    /// <summary>Returns the format with the given id or throws if it is unknown.</summary>
    public static FileFormat Get(string id) =>
        FindById(id) ?? throw new KeyNotFoundException($"Unknown format id '{id}'.");

    /// <summary>Returns the format for an extension (with or without the leading dot), or <c>null</c>.</summary>
    public static FileFormat? FindByExtension(string extension) =>
        ByExtension.GetValueOrDefault(extension.TrimStart('.'));

    /// <summary>Detects the format of a file from its extension, or <c>null</c> if it is not supported.</summary>
    public static FileFormat? Detect(string path) => FindByExtension(Path.GetExtension(path));
}
