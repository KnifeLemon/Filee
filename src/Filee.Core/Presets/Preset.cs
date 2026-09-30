// A preset is one slice of the donut toolbar: "convert to <TargetFormat> with these options".

namespace Filee.Core.Presets;

/// <summary>
/// A named conversion recipe. The donut toolbar shows presets; dropping files on one runs it.
/// </summary>
/// <remarks>
/// Presets are plain mutable objects so they serialize to JSON directly and the editor can bind to them.
/// Use <see cref="Clone"/> before editing so that "Cancel" can discard changes.
/// </remarks>
public sealed class Preset
{
    /// <summary>Stable identifier referenced by toolbar profiles. Never shown to users.</summary>
    public string Id { get; set; } = NewId();

    /// <summary>User-defined name. When empty, <see cref="NameKey"/> is localized and shown instead.</summary>
    public string Name { get; set; } = "";

    /// <summary>Localization key for built-in presets (e.g. <c>preset.merge_pdf</c>). Ignored when <see cref="Name"/> is set.</summary>
    public string? NameKey { get; set; }

    /// <summary>Id of the output format (see <see cref="Formats.FormatRegistry"/>).</summary>
    public string TargetFormat { get; set; } = "png";

    /// <summary>Options used when the output is a raster image.</summary>
    public ImageOptions Image { get; set; } = new();

    /// <summary>Options used when the input or output is a PDF.</summary>
    public PdfOptions Pdf { get; set; } = new();

    /// <summary>Options used for office / HWP documents.</summary>
    public DocumentOptions Document { get; set; } = new();

    /// <summary>Options used for video and audio.</summary>
    public MediaOptions Media { get; set; } = new();

    /// <summary>Options used for archives (ZIP, 7Z, TAR ...).</summary>
    public ArchiveOptions Archive { get; set; } = new();

    /// <summary>Where and how output files are written.</summary>
    public OutputRule Output { get; set; } = new();

    /// <summary>Creates a new random preset id.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Deep copy, used by editors so changes can be cancelled.</summary>
    public Preset Clone() => new()
    {
        Id = Id,
        Name = Name,
        NameKey = NameKey,
        TargetFormat = TargetFormat,
        Image = Image.Clone(),
        Pdf = Pdf.Clone(),
        Document = Document.Clone(),
        Media = Media.Clone(),
        Archive = Archive.Clone(),
        Output = Output.Clone(),
    };
}

/// <summary>How an image is resized before it is encoded.</summary>
public enum ResizeMode
{
    /// <summary>Keep the original pixel size.</summary>
    None,
    /// <summary>Scale by <see cref="ImageOptions.ResizePercent"/>.</summary>
    Percent,
    /// <summary>Scale so the longer edge equals <see cref="ImageOptions.LongEdge"/> (never upscales).</summary>
    LongEdge,
}

/// <summary>TIFF compression schemes.</summary>
public enum TiffCompression
{
    None,
    Lzw,
    Zip,
}

/// <summary>Raster image encoding options.</summary>
public sealed class ImageOptions
{
    /// <summary>Lossy quality 1-100 (JPG, WEBP, AVIF).</summary>
    public int Quality { get; set; } = 90;

    public ResizeMode Resize { get; set; } = ResizeMode.None;

    /// <summary>Scale in percent when <see cref="Resize"/> is <see cref="ResizeMode.Percent"/>.</summary>
    public int ResizePercent { get; set; } = 50;

    /// <summary>Target long-edge length in pixels when <see cref="Resize"/> is <see cref="ResizeMode.LongEdge"/>.</summary>
    public int LongEdge { get; set; } = 1920;

    /// <summary>Output DPI written into the file. <c>null</c> keeps the source value.</summary>
    public int? Dpi { get; set; }

    /// <summary>Keep EXIF / XMP / ICC metadata.</summary>
    public bool KeepMetadata { get; set; } = true;

    /// <summary>Convert to grayscale.</summary>
    public bool Grayscale { get; set; }

    /// <summary>Fill colour for transparent pixels when the target has no alpha channel (e.g. JPG).</summary>
    public string Background { get; set; } = "#FFFFFF";

    public TiffCompression TiffCompression { get; set; } = TiffCompression.Lzw;

    /// <summary>Use lossless WEBP encoding.</summary>
    public bool WebpLossless { get; set; }

    /// <summary>Icon sizes embedded in an ICO file.</summary>
    public List<int> IcoSizes { get; set; } = [256, 128, 64, 48, 32, 16];

    public ImageOptions Clone()
    {
        var copy = (ImageOptions)MemberwiseClone();
        copy.IcoSizes = [.. IcoSizes];
        return copy;
    }
}

/// <summary>Page size used when images are placed into a PDF.</summary>
public enum PdfPageSize
{
    /// <summary>Each page has the size of its image.</summary>
    FitImage,
    A4,
    Letter,
}

/// <summary>PDF related options.</summary>
public sealed class PdfOptions
{
    /// <summary>Combine all dropped files into one PDF instead of one PDF per file.</summary>
    public bool MergeIntoSingle { get; set; }

    public PdfPageSize PageSize { get; set; } = PdfPageSize.FitImage;

    /// <summary>Page margin in millimetres (ignored for <see cref="PdfPageSize.FitImage"/>).</summary>
    public double MarginMm { get; set; }

    /// <summary>Resolution used when PDF pages are rendered to images.</summary>
    public int RenderDpi { get; set; } = 150;

    /// <summary>Pages to process, e.g. <c>"1-3,5"</c>. Empty means all pages.</summary>
    public string PageRange { get; set; } = "";

    /// <summary>For PDF → PDF: write every page as its own file.</summary>
    public bool SplitPages { get; set; }

    public PdfOptions Clone() => (PdfOptions)MemberwiseClone();
}

/// <summary>Options for office and HWP documents.</summary>
public sealed class DocumentOptions
{
    /// <summary>Export PDF/A (archival) when producing PDF from documents.</summary>
    public bool PdfA { get; set; }

    public DocumentOptions Clone() => (DocumentOptions)MemberwiseClone();
}

/// <summary>Trade-off between quality and file size for video and lossy audio.</summary>
public enum MediaQuality
{
    /// <summary>Visually lossless, large files.</summary>
    High,
    /// <summary>Good quality at a reasonable size (default).</summary>
    Balanced,
    /// <summary>Small files for sharing, visible compression.</summary>
    Small,
}

/// <summary>Video and audio options.</summary>
public sealed class MediaOptions
{
    public MediaQuality Quality { get; set; } = MediaQuality.Balanced;

    /// <summary>Largest output height in pixels (e.g. 720); 0 keeps the source size. Never upscales.</summary>
    public int MaxHeight { get; set; }

    /// <summary>Audio bitrate in kbit/s for lossy audio; 0 uses a good default for the format.</summary>
    public int AudioBitrateKbps { get; set; }

    /// <summary>Drop the audio track of a video.</summary>
    public bool RemoveAudio { get; set; }

    public MediaOptions Clone() => (MediaOptions)MemberwiseClone();
}

/// <summary>How hard archives are compressed.</summary>
public enum ArchiveLevel
{
    /// <summary>No compression (fast, for already compressed files).</summary>
    Store,
    Fast,
    Normal,
    Maximum,
}

/// <summary>Archive options.</summary>
public sealed class ArchiveOptions
{
    /// <summary>Put all dropped files into one archive instead of one archive per file.</summary>
    public bool CombineIntoOne { get; set; }

    public ArchiveLevel Level { get; set; } = ArchiveLevel.Normal;

    public ArchiveOptions Clone() => (ArchiveOptions)MemberwiseClone();
}
