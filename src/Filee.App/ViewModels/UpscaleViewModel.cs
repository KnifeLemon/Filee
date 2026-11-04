// ViewModel for the "Upscale Quality" tool window.

using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Upscale;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels;

public sealed partial class UpscaleViewModel : ObservableObject, IDisposable
{
    private readonly Preset _tool;
    private readonly ILocalizer _loc;
    private readonly UserDataStore _store;
    private readonly Func<string, (int Width, int Height)?> _pictureSize;
    private readonly ILogger<UpscaleViewModel>? _log;
    private readonly UpscalerEngine _engine;
    private CancellationTokenSource? _cts;

    public IReadOnlyList<string> Pictures { get; }

    public string Heading { get; }
    public string? HeadingTip { get; }

    public ObservableCollection<UpscaleModel> Models { get; } = [];
    public ObservableCollection<Choice<string>> Targets { get; } = [];

    private (int Width, int Height)? _initialDimensions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResolutionPreview))]
    private int _scale = 4;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectedModelInstalled), nameof(ModelStatusText))]
    private UpscaleModel? _selectedModel;

    [ObservableProperty]
    private Choice<string> _target = null!;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private bool _isCompleted;

    [ObservableProperty]
    private string? _lastOutputFile;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _hasError;

    public bool IsSelectedModelInstalled => SelectedModel is not null && UpscaleModels.IsInstalled(SelectedModel.Id);

    public string ModelStatusText
    {
        get
        {
            if (SelectedModel is null)
                return "";
            if (UpscaleModels.IsInstalled(SelectedModel.Id))
                return _loc["upscale.model_ready"];
            var sizeMb = (SelectedModel.TotalSize / 1_000_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            return _loc.Format("upscale.model_download_size", sizeMb);
        }
    }

    public string ResolutionPreview
    {
        get
        {
            if (_initialDimensions is { } dim)
            {
                var targetW = dim.Width * Scale;
                var targetH = dim.Height * Scale;
                return _loc.Format("upscale.resolution_preview", dim.Width, dim.Height, targetW, targetH, Scale);
            }
            return $"{Scale}×";
        }
    }

    public UpscaleViewModel(
        IReadOnlyList<string> pictures,
        Preset tool,
        ILocalizer loc,
        UserDataStore store,
        Func<string, (int Width, int Height)?> pictureSize,
        ILogger<UpscaleViewModel>? log = null)
    {
        Pictures = pictures;
        _tool = tool;
        _loc = loc;
        _store = store;
        _pictureSize = pictureSize;
        _log = log;
        _engine = new UpscalerEngine(log as ILogger<UpscalerEngine>);

        Heading = pictures.Count == 1
            ? Path.GetFileName(pictures[0])
            : loc.Format("common.files_count", pictures.Count);
        HeadingTip = pictures.Count == 1 ? pictures[0] : string.Join("\n", pictures);

        if (pictures.Count > 0)
        {
            _initialDimensions = _pictureSize(pictures[0]);
        }

        Scale = tool.Upscale.Scale > 0 ? Math.Clamp(tool.Upscale.Scale, 1, 4) : 4;

        InitTargets();
        RefreshModels();
    }

    private void InitTargets()
    {
        Targets.Clear();
        var firstExt = Pictures.Count > 0 ? Path.GetExtension(Pictures[0]).TrimStart('.').ToLowerInvariant() : "png";
        Targets.Add(new("*", _loc.Format("upscale.format_same", firstExt)));
        Targets.Add(new("png", "PNG (.png)"));
        Targets.Add(new("jpg", "JPEG (.jpg)"));
        Targets.Add(new("webp", "WebP (.webp)"));
        Targets.Add(new("avif", "AVIF (.avif)"));
        Targets.Add(new("heic", "HEIC (.heic)"));
        Targets.Add(new("bmp", "BMP (.bmp)"));
        Targets.Add(new("tiff", "TIFF (.tiff)"));
        Targets.Add(new("ico", "ICO (.ico)"));
        Target = Targets[0];
    }

    public void RefreshModels()
    {
        Models.Clear();
        foreach (var m in UpscaleModels.All)
        {
            Models.Add(m);
        }

        var initialId = !string.IsNullOrWhiteSpace(_tool.Upscale.Model) ? _tool.Upscale.Model : UpscaleModels.Default;
        SelectedModel = Models.FirstOrDefault(m => string.Equals(m.Id, initialId, StringComparison.OrdinalIgnoreCase))
            ?? Models.FirstOrDefault();

        OnPropertyChanged(nameof(IsSelectedModelInstalled));
        OnPropertyChanged(nameof(ModelStatusText));
    }

    public void NotifyModelStatusChanged()
    {
        OnPropertyChanged(nameof(IsSelectedModelInstalled));
        OnPropertyChanged(nameof(ModelStatusText));
    }

    [RelayCommand]
    public async Task StartUpscaleAsync()
    {
        if (IsProcessing || SelectedModel is null || Pictures.Count == 0)
            return;

        if (UpscalerConverter.LocateExecutable() is null)
        {
            HasError = true;
            StatusMessage = _loc["error.upscale_needs_engine"];
            return;
        }

        IsProcessing = true;
        IsCompleted = false;
        HasError = false;
        StatusMessage = "";
        ProgressValue = 0;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var chosenTarget = Target.Value;
        var model = SelectedModel;
        var currentScale = Scale;
        var total = Pictures.Count;

        var workFolder = Path.Combine(JobQueue.DefaultWorkRoot, "upscale-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            if (!UpscaleModels.IsInstalled(model.Id))
            {
                ProgressText = _loc["upscale.downloading_model"];
                await UpscaleModels.EnsureAsync(model.Id, progress: null, token);
                NotifyModelStatusChanged();
            }

            Directory.CreateDirectory(workFolder);

            for (var i = 0; i < total; i++)
            {
                token.ThrowIfCancellationRequested();
                var itemIndex = i;
                var sourcePath = Pictures[i];
                var sourceExt = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
                var targetExt = chosenTarget == "*" ? sourceExt : chosenTarget;

                var tokens = new OutputPathResolver.Tokens(sourcePath, _tool.NameKey ?? "upscale", itemIndex + 1, DateTime.Now);
                var resolvedOutput = OutputPathResolver.Resolve(_tool.Output, tokens, targetExt, File.Exists);
                if (resolvedOutput is null)
                    continue;

                var progress = new Progress<double>(p =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var overall = (itemIndex + p) / total;
                        ProgressValue = Math.Clamp(overall * 100.0, 0, 100);
                        var currentPct = (int)Math.Clamp(p * 100.0, 0, 100);
                        ProgressText = total > 1
                            ? $"({itemIndex + 1}/{total}) {_loc.Format("upscale.processing", currentPct)}"
                            : _loc.Format("upscale.processing", currentPct);
                    });
                });

                await _engine.UpscaleAsync(
                    sourcePath,
                    resolvedOutput,
                    sourceExt,
                    targetExt,
                    model.Id,
                    currentScale,
                    workFolder,
                    _tool,
                    progress,
                    token);

                LastOutputFile = resolvedOutput;
            }

            ProgressValue = 100;
            ProgressText = _loc["upscale.success"];
            IsCompleted = true;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "";
            ProgressText = "";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = _loc.Format("error.upscale_failed", ex.Message);
            _log?.LogError(ex, "Upscaling failed");
        }
        finally
        {
            try
            {
                if (Directory.Exists(workFolder))
                    Directory.Delete(workFolder, recursive: true);
            }
            catch
            {
                // Swept by JobQueue.DeleteStaleWorkFolders
            }

            IsProcessing = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    public void Cancel()
    {
        if (IsProcessing && _cts is not null)
        {
            _cts.Cancel();
        }
    }

    [RelayCommand]
    public void OpenOutputFolder()
    {
        if (string.IsNullOrEmpty(LastOutputFile) || !File.Exists(LastOutputFile))
            return;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start("explorer.exe", $"/select,\"{LastOutputFile}\"");
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", $"-R \"{LastOutputFile}\"");
            }
            else
            {
                var dir = Path.GetDirectoryName(LastOutputFile);
                if (!string.IsNullOrEmpty(dir))
                    Process.Start("xdg-open", $"\"{dir}\"");
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to reveal output file");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
