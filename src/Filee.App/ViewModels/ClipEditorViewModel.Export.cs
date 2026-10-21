// The editor's Export window: what the clip is for (ready-made choices for YouTube, Shorts / TikTok, Discord and
// Telegram), its size, format, frame rate, quality and size limit, the graphics chip, each part as its own file, and
// an estimate of the result. Export starts the conversion (its progress shows in the toast) and remembers the choices.

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.Core.Presets;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels;

public sealed partial class ClipEditorViewModel
{
    /// <summary>The ready-made choices: (id, shape, shorter side, format, fps, quality, size limit).</summary>
    private static readonly (string Id, string? Aspect, int Resolution, string Format, double Fps, MediaQuality Quality, double Limit)[] ExportPresetValues =
    [
        ("source", null, 0, "mp4", 0, MediaQuality.High, 0),
        ("youtube", null, 1080, "mp4", 0, MediaQuality.High, 0),
        ("shorts", "9:16", 1080, "mp4", 0, MediaQuality.High, 0),
        ("discord", null, 720, "mp4", 30, MediaQuality.Balanced, 10),
        ("telegram", null, 1080, "mp4", 0, MediaQuality.Balanced, 50),
    ];

    /// <summary>Size limits offered, in megabytes (0 none, -1 one of the user's own).</summary>
    private static readonly double[] Limits = [0, 10, 25, 50, 100, -1];

    [ObservableProperty] private bool _isExportOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportSummary))]
    private Choice<string>? _exportPreset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportSummary))]
    [NotifyPropertyChangedFor(nameof(IsGif))]
    private Choice<string>? _exportFormat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportSummary))]
    private Choice<int>? _exportResolution;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportSummary))]
    private Choice<double>? _exportFps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportSummary))]
    private Choice<MediaQuality>? _exportQuality;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportSummary))]
    [NotifyPropertyChangedFor(nameof(HasCustomLimit))]
    private Choice<double>? _exportLimit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportSummary))]
    private double _customLimit = 25;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuText))]
    private bool _useGpu = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportSummary))]
    private bool _exportSeparate;

    /// <summary>The graphics chip's encoder that works here ("h264_nvenc"), null when none; "" while it is looked for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuText))]
    [NotifyPropertyChangedFor(nameof(HasGpu))]
    private string? _gpuEncoder = "";

    public IReadOnlyList<Choice<string>> ExportPresets { get; private set; } = [];
    public IReadOnlyList<Choice<string>> ExportFormats { get; private set; } = [];
    public IReadOnlyList<Choice<int>> ExportResolutions { get; private set; } = [];
    public IReadOnlyList<Choice<double>> ExportFpsChoices { get; private set; } = [];
    public IReadOnlyList<Choice<MediaQuality>> ExportQualities { get; private set; } = [];
    public IReadOnlyList<Choice<double>> ExportLimits { get; private set; } = [];

    public bool HasCustomLimit => ExportLimit?.Value < 0;
    public bool IsGif => ExportFormat?.Value == "gif";
    public bool HasGpu => !string.IsNullOrEmpty(GpuEncoder);

    /// <summary>"How many files": one, or one per part.</summary>
    public string SeparateText => _loc.Format("editor.export_separate", _project.Parts.Count(p => !p.Blank));

    public string GpuText => GpuEncoder switch
    {
        "" => _loc["editor.gpu_checking"],
        null => _loc["editor.gpu_none"],
        var name => _loc.Format(UseGpu ? "editor.gpu_on" : "editor.gpu_off", GpuName(name)),
    };

    /// <summary>"NVIDIA (NVENC)" for "h264_nvenc".</summary>
    internal static string GpuName(string encoder) => encoder[(encoder.IndexOf('_') + 1)..] switch
    {
        "nvenc" => "NVIDIA (NVENC)",
        "qsv" => "Intel (Quick Sync)",
        "amf" => "AMD (AMF)",
        "videotoolbox" => "Apple (VideoToolbox)",
        "vaapi" => "VA-API",
        var other => other,
    };

    /// <summary>"About 0:42 · about 38 MB · 1080×1920 · 60 fps".</summary>
    public string ExportSummary
    {
        get
        {
            var (width, height, fps) = ExportFrame();
            var length = _project.Length;
            var megabytes = EstimateMegabytes(width, height, fps, length);
            var size = Limit() > 0 && megabytes > Limit()
                ? _loc.Format("editor.export_size_limit", Limit().ToString("0.#", CultureInfo.CurrentCulture))
                : _loc.Format("editor.export_size", megabytes < 10 ? megabytes.ToString("0.#", CultureInfo.CurrentCulture) : Math.Round(megabytes).ToString(CultureInfo.CurrentCulture));
            return $"{ClipRange.Display(length)} · {size} · {width}×{height} · {Math.Round(fps).ToString(CultureInfo.CurrentCulture)} {_loc["editor.fps"]}";
        }
    }

    private double Limit() => ExportLimit?.Value is < 0 ? Math.Max(1, CustomLimit) : ExportLimit?.Value ?? 0;

    /// <summary>The written frame: the clip's shape at the chosen size (a GIF small), and its frame rate.</summary>
    private (int Width, int Height, double Fps) ExportFrame()
    {
        var first = _project.Parts.FirstOrDefault(p => !p.Blank) is { } part ? MediaOf(part.Source) : MediaOf(null);
        var (width, height) = ClipCanvas.Size(_project.Edit.Aspect, ExportResolution?.Value ?? 0, Math.Max(16, first?.Width ?? 1920),
            Math.Max(16, first?.Height ?? 1080));
        var fps = ExportFps?.Value is > 0 and var chosen ? chosen : first?.FrameRate is > 0 and < 241 and var own ? own : 30;
        if (IsGif)
        {
            if ((ExportResolution?.Value ?? 0) == 0 && Math.Max(width, height) > 480)
            {
                var scale = 480.0 / Math.Max(width, height);
                (width, height) = ((int)Math.Round(width * scale / 2) * 2, (int)Math.Round(height * scale / 2) * 2);
            }
            if ((ExportFps?.Value ?? 0) == 0)
                fps = Math.Min(fps, 15);
        }
        return (width, height, fps);
    }

    /// <summary>A rough size: bits per pixel for the format and quality, plus the sound.</summary>
    private double EstimateMegabytes(int width, int height, double fps, double seconds)
    {
        var quality = ExportQuality?.Value ?? MediaQuality.High;
        var bitsPerPixel = quality switch
        {
            MediaQuality.High => 0.11,
            MediaQuality.Small => 0.04,
            _ => 0.07,
        } * (ExportFormat?.Value switch
        {
            "mp4-hevc" => 0.6,
            "webm" => 0.7,
            "gif" => 1.6,
            _ => 1,
        });
        var bits = width * (double)height * fps * seconds * bitsPerPixel + (IsGif ? 0 : 160_000 * seconds);
        return bits / 8 / 1024 / 1024;
    }

    private void UpdateExportSummary()
    {
        OnPropertyChanged(nameof(ExportSummary));
        OnPropertyChanged(nameof(SeparateText));
    }

    partial void OnExportPresetChanged(Choice<string>? value)
    {
        if (_syncing || value is null || ExportPresetValues.FirstOrDefault(p => p.Id == value.Value) is not { Id: not null } preset)
            return;
        _syncing = true;
        try
        {
            ExportResolution = ExportResolutions.FirstOrDefault(r => r.Value == preset.Resolution) ?? ExportResolutions[0];
            ExportFormat = ExportFormats.FirstOrDefault(f => f.Value == preset.Format) ?? ExportFormats[0];
            ExportFps = ExportFpsChoices.FirstOrDefault(f => f.Value == preset.Fps) ?? ExportFpsChoices[0];
            ExportQuality = ExportQualities.FirstOrDefault(q => q.Value == preset.Quality) ?? ExportQualities[0];
            ExportLimit = ExportLimits.FirstOrDefault(l => l.Value == preset.Limit) ?? ExportLimits[0];
        }
        finally
        {
            _syncing = false;
        }
        // Shorts and TikTok are upright: the frame changes with them (and back with Undo).
        if (preset.Aspect is { } aspect && _project.Edit.Aspect != aspect)
            SetAspect(aspect);
        UpdateExportSummary();
    }

    partial void OnUseGpuChanged(bool value) => OnPropertyChanged(nameof(GpuText));

    /// <summary>The lists of the Export window, in the current language (the choices kept).</summary>
    private void BuildExportChoices()
    {
        var (preset, format, resolution, fps, quality, limit) = (ExportPreset?.Value, ExportFormat?.Value, ExportResolution?.Value, ExportFps?.Value,
            ExportQuality?.Value, ExportLimit?.Value);
        var main = MediaOf(null);
        ExportPresets =
        [
            new("source", _loc["editor.preset_source"]),
            new("youtube", _loc["editor.preset_youtube"]),
            new("shorts", _loc["editor.preset_shorts"]),
            new("discord", _loc["editor.preset_discord"]),
            new("telegram", _loc["editor.preset_telegram"]),
        ];
        ExportFormats =
        [
            new("mp4", "MP4 · H.264", _loc["editor.format_h264"]),
            new("mp4-hevc", "MP4 · H.265", _loc["editor.format_hevc"]),
            new("webm", "WebM · VP9", _loc["editor.format_webm"]),
            new("gif", "GIF", _loc["editor.format_gif"]),
        ];
        ExportResolutions =
        [
            new(0, _loc["editor.resolution_source"]),
            .. ClipCanvas.Resolutions.Where(r => r > 0).Select(r => new Choice<int>(r, r switch
            {
                2160 => "4K (2160p)",
                1440 => "2K (1440p)",
                _ => $"{r}p",
            })),
        ];
        ExportFpsChoices =
        [
            new(0, main is { FrameRate: > 0 } ? _loc.Format("editor.fps_source_value", Math.Round(main.FrameRate).ToString(CultureInfo.CurrentCulture)) : _loc["editor.fps_source"]),
            new(60, "60"),
            new(30, "30"),
            new(24, "24"),
        ];
        ExportQualities =
        [
            new(MediaQuality.High, _loc["editor.quality_high"]),
            new(MediaQuality.Balanced, _loc["editor.quality_balanced"]),
            new(MediaQuality.Small, _loc["editor.quality_small"]),
        ];
        ExportLimits = Limits.Select(l => new Choice<double>(l, l switch
        {
            0 => _loc["editor.limit_none"],
            < 0 => _loc["editor.limit_custom"],
            10 => _loc.Format("editor.limit_mb_for", 10, "Discord"),
            50 => _loc.Format("editor.limit_mb_for", 50, "Telegram"),
            _ => _loc.Format("editor.limit_mb", l),
        })).ToList();
        _syncing = true;
        try
        {
            ExportPreset = ExportPresets.FirstOrDefault(p => p.Value == preset);
            ExportFormat = ExportFormats.FirstOrDefault(f => f.Value == format) ?? ExportFormats[0];
            ExportResolution = ExportResolutions.FirstOrDefault(r => r.Value == resolution) ?? ExportResolutions[0];
            ExportFps = ExportFpsChoices.FirstOrDefault(f => f.Value == fps) ?? ExportFpsChoices[0];
            ExportQuality = ExportQualities.FirstOrDefault(q => q.Value == quality) ?? ExportQualities[0];
            ExportLimit = ExportLimits.FirstOrDefault(l => l.Value == limit) ?? ExportLimits[0];
        }
        finally
        {
            _syncing = false;
        }
        foreach (var name in new[] { nameof(ExportPresets), nameof(ExportFormats), nameof(ExportResolutions), nameof(ExportFpsChoices),
                     nameof(ExportQualities), nameof(ExportLimits), nameof(ExportSummary), nameof(SeparateText) })
            OnPropertyChanged(name);
    }

    /// <summary>The choices of the last export.</summary>
    private void LoadExportSettings()
    {
        BuildChoices();
        var saved = _store.Settings.EditorExport;
        _syncing = true;
        try
        {
            ExportFormat = ExportFormats.FirstOrDefault(f => f.Value == saved.Format) ?? ExportFormats[0];
            ExportResolution = ExportResolutions.FirstOrDefault(r => r.Value == saved.Resolution) ?? ExportResolutions[0];
            ExportFps = ExportFpsChoices.FirstOrDefault(f => f.Value == saved.Fps) ?? ExportFpsChoices[0];
            ExportQuality = ExportQualities.FirstOrDefault(q => q.Value == saved.Quality) ?? ExportQualities[0];
            var custom = saved.LimitMegabytes > 0 && !Limits.Contains(saved.LimitMegabytes);
            ExportLimit = ExportLimits.FirstOrDefault(l => l.Value == (custom ? -1 : saved.LimitMegabytes)) ?? ExportLimits[0];
            if (custom)
                CustomLimit = saved.LimitMegabytes;
            UseGpu = saved.Gpu;
        }
        finally
        {
            _syncing = false;
        }
    }

    private async Task FindGpuAsync()
    {
        try
        {
            GpuEncoder = await _frames.FindHardwareEncoderAsync("h264", _life.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogDebug(ex, "No graphics chip encoder found");
            GpuEncoder = null;
        }
    }

    /// <summary>E / Ctrl+E: the Export window.</summary>
    [RelayCommand]
    public void OpenExport()
    {
        StopPlaying();
        UpdateExportSummary();
        IsExportOpen = true;
    }

    [RelayCommand]
    public void CloseExport() => IsExportOpen = false;

    /// <summary>
    /// Starts the conversion that writes the clip with the Export window's choices (its progress shows in the toast)
    /// and remembers them.
    /// </summary>
    [RelayCommand]
    public void Export()
    {
        if (_closed)
            return;
        StopPlaying();
        // Texts and pictures drawn as the preview draws them, at the written frame's size.
        var (width, height, _) = ExportFrame();
        var edit = Services.EditorLayers.Render(_project.ToEdit(), width, height, ImageOf);
        edit.Resolution = ExportResolution?.Value ?? 0;
        edit.Fps = ExportFps?.Value ?? 0;
        edit.MaxMegabytes = IsGif ? 0 : Limit();
        edit.Separate = ExportSeparate && edit.Parts.Count(p => !p.Blank) > 1;
        var format = ExportFormat?.Value ?? "mp4";
        var target = format switch
        {
            "webm" => "webm",
            "gif" => "gif",
            _ => "mp4",
        };
        var tool = _tool.Clone();
        tool.Media.VideoCodec = format == "mp4-hevc" ? VideoCodec.Hevc : VideoCodec.Default;
        tool.Media.Quality = ExportQuality?.Value ?? MediaQuality.High;
        tool.Media.HardwareEncoding = UseGpu && HasGpu;
        try
        {
            _export(_main, ClipTool.ConversionOf(tool, target, edit));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogError(ex, "Could not start writing the editor's clip of {File}", _main);
            ShowMessage(_loc["error.conversion_failed"]);
            return;
        }
        var saved = _store.Settings.EditorExport;
        saved.Format = format;
        saved.Resolution = edit.Resolution;
        saved.Fps = edit.Fps;
        saved.Quality = tool.Media.Quality;
        saved.LimitMegabytes = Limit();
        saved.Gpu = UseGpu;
        try
        {
            _store.SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Could not remember the export choices");
        }
        IsExportOpen = false;
        Exported?.Invoke();
    }
}
