// ViewModel for the "Upscale Quality" tool window.

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.Core.Conversion;
using Filee.Core.Formats;
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
    private readonly Action<IReadOnlyList<string>, Preset> _start;
    private readonly ILogger<UpscaleViewModel>? _log;
    private CancellationTokenSource? _cts;

    public event Action? CloseRequested;

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
        Action<IReadOnlyList<string>, Preset> start,
        ILogger<UpscaleViewModel>? log = null)
    {
        Pictures = pictures;
        _tool = tool;
        _loc = loc;
        _store = store;
        _pictureSize = pictureSize;
        _start = start;
        _log = log;

        Heading = pictures.Count == 1
            ? Path.GetFileName(pictures[0])
            : loc.Format("home.files", pictures.Count);
        HeadingTip = pictures.Count == 1 ? pictures[0] : string.Join("\n", pictures);

        if (pictures.Count > 0)
        {
            _initialDimensions = _pictureSize(pictures[0]);
        }

        Scale = tool.Upscale.Scale >= 2 ? Math.Clamp(tool.Upscale.Scale, 2, 4) : 4;

        InitTargets();
        RefreshModels();
    }

    private void InitTargets()
    {
        Targets.Clear();
        var firstFormatId = Pictures.Count > 0 ? (FormatRegistry.Detect(Pictures[0])?.Id ?? "png") : "png";
        var effectiveTarget = UpscaleTool.TargetFor(firstFormatId, BuiltInData.SameAsSource);
        Targets.Add(new(BuiltInData.SameAsSource, _loc.Format("upscale.format_same", effectiveTarget)));
        Targets.Add(new("png", "PNG (.png)"));
        Targets.Add(new("jpg", "JPEG (.jpg)"));
        Targets.Add(new("webp", "WebP (.webp)"));
        Targets.Add(new("avif", "AVIF (.avif)"));
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
        HasError = false;
        StatusMessage = "";
        ProgressValue = 0;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var chosenTarget = Target.Value;
        var model = SelectedModel;
        var currentScale = Math.Clamp(Scale, 2, 4);

        try
        {
            if (!UpscaleModels.IsInstalled(model.Id))
            {
                ProgressText = _loc["upscale.downloading_model"];
                var progress = new Progress<Filee.Engines.Infrastructure.EngineInstallProgress>(p =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var pct = (int)Math.Clamp(p.Fraction * 100.0, 0, 100);
                        ProgressValue = pct;
                        ProgressText = $"{_loc["upscale.downloading_model"]} ({pct}%)";
                    });
                });
                await UpscaleModels.EnsureAsync(model.Id, progress, token);
                NotifyModelStatusChanged();
            }

            Remember(model.Id, currentScale);

            foreach (var group in Pictures.GroupBy(path => UpscaleTool.TargetFor(FormatRegistry.Detect(path)?.Id ?? "", chosenTarget)))
            {
                _start([.. group], UpscaleTool.ConversionOf(_tool, group.Key, model.Id, currentScale));
            }

            CloseRequested?.Invoke();
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
            IsProcessing = false;
        }
    }

    private void Remember(string modelId, int scale)
    {
        _tool.Upscale.Model = modelId;
        _tool.Upscale.Scale = scale;
        if (_store.FindPreset(_tool.Id) is { IsUpscaleTool: true } saved)
        {
            saved.Upscale.Model = modelId;
            saved.Upscale.Scale = scale;
            try
            {
                _store.SaveLibrary();
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Could not save upscale tool settings");
            }
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

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
