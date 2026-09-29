// Factory defaults: presets and toolbar profiles created on first run or "Reset to defaults".

using Filee.Core.Profiles;

namespace Filee.Core.Presets;

/// <summary>Built-in presets and profiles.</summary>
public static class BuiltInData
{
    /// <summary>Special target format meaning "same format as the source file" (e.g. resize only).</summary>
    public const string SameAsSource = "*";

    /// <summary>Creates the default preset list.</summary>
    public static List<Preset> CreatePresets() =>
    [
        new() { Id = "to-png", Name = "PNG", TargetFormat = "png" },
        new() { Id = "to-jpg", Name = "JPG", TargetFormat = "jpg", Image = { Quality = 90 } },
        new()
        {
            Id = "jpg-70", Name = "JPG 70%", TargetFormat = "jpg",
            Image = { Quality = 70 }, Output = { FileNamePattern = "{name}_70" },
        },
        new() { Id = "to-webp", Name = "WEBP", TargetFormat = "webp", Image = { Quality = 85 } },
        new() { Id = "to-tiff", Name = "TIFF", TargetFormat = "tiff" },
        new() { Id = "to-bmp", Name = "BMP", TargetFormat = "bmp" },
        new() { Id = "to-ico", Name = "ICO", TargetFormat = "ico" },
        new() { Id = "to-gif", Name = "GIF", TargetFormat = "gif" },
        new() { Id = "to-avif", Name = "AVIF", TargetFormat = "avif", Image = { Quality = 60 } },
        new()
        {
            Id = "half-size", NameKey = "preset.half_size", TargetFormat = SameAsSource,
            Image = { Resize = ResizeMode.Percent, ResizePercent = 50 }, Output = { FileNamePattern = "{name}_50" },
        },
        new()
        {
            Id = "grayscale", NameKey = "preset.grayscale", TargetFormat = SameAsSource,
            Image = { Grayscale = true }, Output = { FileNamePattern = "{name}_gray" },
        },
        new()
        {
            Id = "tiff-300", Name = "TIFF 300dpi", TargetFormat = "tiff",
            Image = { Dpi = 300 }, Pdf = { RenderDpi = 300 }, Output = { FileNamePattern = "{name}_300dpi" },
        },
        new() { Id = "to-pdf", Name = "PDF", TargetFormat = "pdf" },
        new()
        {
            Id = "merge-pdf", NameKey = "preset.merge_pdf", TargetFormat = "pdf",
            Pdf = { MergeIntoSingle = true }, Output = { FileNamePattern = "{name}_merged" },
        },
        new()
        {
            Id = "pdf-split", NameKey = "preset.split_pdf", TargetFormat = "pdf",
            Pdf = { SplitPages = true }, Output = { Location = OutputLocation.Subfolder, SubfolderName = "{name}" },
        },
        new() { Id = "to-docx", Name = "DOCX", TargetFormat = "docx" },
        new() { Id = "to-hwpx", Name = "HWPX", TargetFormat = "hwpx" },
        new() { Id = "to-odt", Name = "ODT", TargetFormat = "odt" },
        new() { Id = "to-rtf", Name = "RTF", TargetFormat = "rtf" },
        new() { Id = "to-txt", Name = "TXT", TargetFormat = "txt" },
        new() { Id = "to-md", Name = "Markdown", TargetFormat = "md" },
        new() { Id = "to-html", Name = "HTML", TargetFormat = "html" },
    ];

    /// <summary>Creates the default toolbar profiles.</summary>
    public static List<ToolbarProfile> CreateProfiles() =>
    [
        new()
        {
            Id = "images", NameKey = "profile.images",
            Extensions = ["png", "jpg", "jpeg", "jpe", "jfif", "webp", "tiff", "tif", "bmp", "dib", "gif", "ico", "avif", "heic", "heif"],
            PresetIds = ["to-png", "to-jpg", "to-webp", "to-pdf", "to-tiff", "to-bmp", "to-ico", "half-size"],
        },
        new()
        {
            Id = "pdf", NameKey = "profile.pdf",
            Extensions = ["pdf"],
            PresetIds = ["to-png", "to-jpg", "to-docx", "to-hwpx", "to-txt", "pdf-split", "merge-pdf"],
        },
        new()
        {
            Id = "office", NameKey = "profile.office",
            Extensions = ["docx", "doc", "odt", "rtf", "xlsx", "xls", "ods", "csv", "pptx", "ppt", "odp"],
            PresetIds = ["to-pdf", "to-docx", "to-hwpx", "to-odt", "to-txt", "to-html", "merge-pdf"],
        },
        new()
        {
            Id = "hwp", NameKey = "profile.hwp",
            Extensions = ["hwp", "hwpx"],
            PresetIds = ["to-pdf", "to-docx", "to-hwpx", "to-png", "to-txt", "to-md", "merge-pdf"],
        },
        TextProfile(),
        new()
        {
            Id = "mixed", NameKey = "profile.mixed", IsFallback = true,
            PresetIds = ["to-pdf", "merge-pdf", "to-png", "to-txt"],
        },
    ];

    /// <summary>Markdown, plain text and HTML (added in settings schema 4, see SettingsMigrations).</summary>
    public static ToolbarProfile TextProfile() => new()
    {
        Id = "text",
        NameKey = "profile.text",
        Extensions = ["md", "markdown", "txt", "html", "htm"],
        PresetIds = ["to-pdf", "to-hwpx", "to-docx", "to-html", "to-txt"],
    };
}
