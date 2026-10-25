// Settings → Security and limits: what Filee does with documents that carry macros or other active content, and the
// limits that keep one huge or stuck file from taking over the computer (image size and memory, files converted at
// once, time per conversion step).

using System.Text.Json.Serialization;
using Filee.Core.Presets;

namespace Filee.Core.Settings;

/// <summary>What Filee does with a document that has macros, ActiveX controls, external links or DDE fields.</summary>
public enum ActiveContentPolicy
{
    /// <summary>
    /// Show what was found and let the user decide. Watch folders and the command line can't ask: they convert, and
    /// the file's result says what it contained.
    /// </summary>
    Ask,

    /// <summary>Convert without asking.</summary>
    Convert,

    /// <summary>Never convert such documents.</summary>
    Refuse,
}

/// <summary>Settings → Security and limits: documents with active content.</summary>
/// <remarks>
/// Nothing in a document is ever run: LibreOffice's conversion mode doesn't execute document macros, and Filee also
/// starts it with macros disabled and links never updated (<c>LibreOfficeSecurity</c> in Filee.Engines).
/// </remarks>
public sealed class SecuritySettings
{
    /// <summary>What happens to a document with active content. A value from a newer Filee reads as <see cref="ActiveContentPolicy.Ask"/>.</summary>
    [JsonConverter(typeof(LenientEnumJsonConverter<ActiveContentPolicy>))]
    public ActiveContentPolicy ActiveContent { get; set; } = ActiveContentPolicy.Ask;

    public SecuritySettings Clone() => (SecuritySettings)MemberwiseClone();
}

/// <summary>
/// Settings → Security and limits: resource limits. 0 means "automatic" everywhere: no limit of Filee's own (the
/// engine's defaults, one file per processor core, no time limit). Values outside a range are taken as its nearest end.
/// </summary>
public sealed class LimitSettings
{
    /// <summary>Largest picture Filee opens, in megapixels (width × height); 0 for no limit. Guards against "image bombs".</summary>
    public int MaxImageMegapixels { get; set; } = 500;

    /// <summary>Longest side of a picture in pixels; 0 for ImageMagick's default.</summary>
    public int MaxImageSide { get; set; }

    /// <summary>Memory ImageMagick may use for pictures, in MB, before it pages to disk; 0 for its default.</summary>
    public int MaxImageMemoryMb { get; set; }

    /// <summary>Disk space ImageMagick may page pictures to, in MB; 0 for its default.</summary>
    public int MaxImageDiskMb { get; set; }

    /// <summary>Files of one job converted at the same time; 0 for one per processor core.</summary>
    public int MaxParallelFiles { get; set; }

    /// <summary>
    /// Minutes a built-in engine may spend on one conversion step before the file fails as stuck; 0 for no limit.
    /// Engines that run another program (LibreOffice, FFmpeg, Ghostscript, …) keep their own time limits.
    /// </summary>
    public int StepTimeoutMinutes { get; set; } = 15;

    public const int MaxMegapixels = 100_000;
    public const int MaxSide = 1_000_000;
    public const int MaxMegabytes = 1_048_576;
    public const int MaxFiles = 64;
    public const int MaxTimeoutMinutes = 1440;

    /// <summary><see cref="MaxImageMegapixels"/> within its range; 0 for no limit.</summary>
    [JsonIgnore]
    public int EffectiveMaxImageMegapixels => Math.Clamp(MaxImageMegapixels, 0, MaxMegapixels);

    /// <summary><see cref="MaxImageSide"/> within its range; 0 for the default.</summary>
    [JsonIgnore]
    public int EffectiveMaxImageSide => Math.Clamp(MaxImageSide, 0, MaxSide);

    /// <summary><see cref="MaxImageMemoryMb"/> within its range; 0 for the default.</summary>
    [JsonIgnore]
    public int EffectiveMaxImageMemoryMb => Math.Clamp(MaxImageMemoryMb, 0, MaxMegabytes);

    /// <summary><see cref="MaxImageDiskMb"/> within its range; 0 for the default.</summary>
    [JsonIgnore]
    public int EffectiveMaxImageDiskMb => Math.Clamp(MaxImageDiskMb, 0, MaxMegabytes);

    /// <summary>How many files of a job convert at once: <see cref="MaxParallelFiles"/>, or one per processor core.</summary>
    [JsonIgnore]
    public int EffectiveParallelFiles => MaxParallelFiles <= 0 ? Environment.ProcessorCount : Math.Min(MaxParallelFiles, MaxFiles);

    /// <summary>The step time limit, or null for none.</summary>
    [JsonIgnore]
    public TimeSpan? EffectiveStepTimeout =>
        StepTimeoutMinutes <= 0 ? null : TimeSpan.FromMinutes(Math.Min(StepTimeoutMinutes, MaxTimeoutMinutes));

    public LimitSettings Clone() => (LimitSettings)MemberwiseClone();
}
