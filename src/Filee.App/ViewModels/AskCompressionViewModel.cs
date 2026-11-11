// "Choose the compression" (#1353): the files dropped on a preset that asks for its compression (Preset.AskCompression),
// the setting of each kind among them (the preset editor's own controls) and the size they will roughly come to,
// estimated again a moment after each change.

using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.ViewModels;

public sealed partial class AskCompressionViewModel : ObservableObject, IDisposable
{
    /// <summary>How long the slider rests before the size is estimated again: one encode per stop, not per step.</summary>
    internal static readonly TimeSpan EstimateDelay = TimeSpan.FromMilliseconds(250);

    private readonly ILocalizer _loc;
    private readonly Func<CompressionChoice, CancellationToken, Task<long?>> _estimate;
    private readonly TimeSpan _delay;
    private CancellationTokenSource? _pending;

    /// <param name="files">The dropped files a level is chosen for (AskedCompression.KindOf), in the order dropped.</param>
    /// <param name="sourceBytes">Their size now.</param>
    /// <param name="estimate">About what they come to with a choice (CompressionEstimator.EstimateAsync); null when unknown.</param>
    /// <param name="delay">The rest before estimating again; <see cref="EstimateDelay"/> unless a test sets it.</param>
    public AskCompressionViewModel(Preset preset, IReadOnlyList<string> files, long sourceBytes,
        Func<CompressionChoice, CancellationToken, Task<long?>> estimate, ILocalizer loc, TimeSpan? delay = null)
    {
        _loc = loc;
        _estimate = estimate;
        _delay = delay ?? EstimateDelay;
        var kinds = AskedCompression.KindsOf(preset, files);
        ShowImage = kinds.Contains(CompressionKind.Image);
        ShowVideo = kinds.Contains(CompressionKind.Video);
        ShowAudio = kinds.Contains(CompressionKind.Audio);
        ShowPdf = kinds.Contains(CompressionKind.Pdf);
        OnlyIfSmaller = preset.Output.OnlyIfSmaller;
        SourceBytes = sourceBytes;
        FilesText = files.Count == 1 ? Path.GetFileName(files[0]) : loc.Format("compression.files", files.Count);
        SizeNowText = loc.Format("compression.size_now", AskedCompression.SizeText(sourceBytes));
        HintText = loc[ShowVideo ? "compression.estimate_hint_video" : "compression.estimate_hint"];

        var start = CompressionChoice.Of(preset);
        _quality = Math.Clamp(start.ImageQuality, 1, 100);
        VideoQualities = Enum.GetValues<MediaQuality>().Select(v => new Choice<MediaQuality>(v, loc[$"presets.media_quality.{v}"])).ToList();
        _videoQuality = VideoQualities.First(q => q.Value == start.VideoQuality);
        AudioBitrates = [.. PresetEditorViewModel.BitrateSteps.Append(start.AudioBitrateKbps).Distinct()
            .Select(k => new Choice<int>(k, k == 0 ? loc["presets.audio_bitrate.auto"] : $"{k} kbps"))];
        _audioBitrate = AudioBitrates.First(b => b.Value == start.AudioBitrateKbps);
        // "Don't compress" is no level for a compression: a preset saved with it starts at Balanced.
        PdfLevels = Enum.GetValues<PdfCompression>().Where(v => v != PdfCompression.None)
            .Select(v => new Choice<PdfCompression>(v, loc[$"presets.pdf_compression.{v}"])).ToList();
        _pdfLevel = PdfLevels.FirstOrDefault(l => l.Value == start.Pdf) ?? PdfLevels.First(l => l.Value == PdfCompression.Ebook);
        _ = EstimateAsync(TimeSpan.Zero);
    }

    /// <summary>The file's name, or "3 files".</summary>
    public string FilesText { get; }

    /// <summary>"Size now: 100 MB".</summary>
    public string SizeNowText { get; }

    public long SourceBytes { get; }

    /// <summary>The preset keeps the original when the result isn't smaller (OutputRule.OnlyIfSmaller).</summary>
    public bool OnlyIfSmaller { get; }

    public bool ShowImage { get; }
    public bool ShowVideo { get; }
    public bool ShowAudio { get; }
    public bool ShowPdf { get; }

    /// <summary>Under the estimate: what it rests on (a video's picture changes it the most).</summary>
    public string HintText { get; }

    [ObservableProperty]
    private int _quality;

    public IReadOnlyList<Choice<MediaQuality>> VideoQualities { get; }

    [ObservableProperty]
    private Choice<MediaQuality> _videoQuality;

    public IReadOnlyList<Choice<int>> AudioBitrates { get; }

    [ObservableProperty]
    private Choice<int> _audioBitrate;

    public IReadOnlyList<Choice<PdfCompression>> PdfLevels { get; }

    [ObservableProperty]
    private Choice<PdfCompression> _pdfLevel;

    /// <summary>"About 30.0 MB (70% smaller)", "Estimating the size…", or why there is none.</summary>
    [ObservableProperty]
    private string _estimateText = "";

    /// <summary>The estimate in bytes, null while it is worked out or when there is none.</summary>
    [ObservableProperty]
    private long? _estimatedBytes;

    [ObservableProperty]
    private bool _isEstimating;

    partial void OnQualityChanged(int value) => _ = EstimateAsync(_delay);

    partial void OnVideoQualityChanged(Choice<MediaQuality>? oldValue, Choice<MediaQuality> newValue)
    {
        if (!Choices.Restore(oldValue, newValue, v => VideoQuality = v))
            _ = EstimateAsync(TimeSpan.Zero);
    }

    partial void OnAudioBitrateChanged(Choice<int>? oldValue, Choice<int> newValue)
    {
        if (!Choices.Restore(oldValue, newValue, v => AudioBitrate = v))
            _ = EstimateAsync(TimeSpan.Zero);
    }

    partial void OnPdfLevelChanged(Choice<PdfCompression>? oldValue, Choice<PdfCompression> newValue)
    {
        if (!Choices.Restore(oldValue, newValue, v => PdfLevel = v))
            _ = EstimateAsync(TimeSpan.Zero);
    }

    /// <summary>The settings chosen.</summary>
    public CompressionChoice Result() => new(Quality, VideoQuality.Value, AudioBitrate.Value, PdfLevel.Value);

    /// <summary>The estimate for the settings as they are now; one started before it is dropped.</summary>
    internal async Task EstimateAsync(TimeSpan delay)
    {
        _pending?.Cancel();
        _pending?.Dispose();
        var pending = _pending = new CancellationTokenSource();
        var choice = Result();
        IsEstimating = true;
        EstimatedBytes = null;
        EstimateText = _loc["compression.estimating"];
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, pending.Token);
            var estimate = await _estimate(choice, pending.Token);
            if (pending.IsCancellationRequested)
                return;
            EstimatedBytes = estimate;
            EstimateText = Describe(estimate);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!pending.IsCancellationRequested)
                EstimateText = _loc["compression.unknown"];
        }
        finally
        {
            if (pending == _pending)
                IsEstimating = false;
        }
    }

    private string Describe(long? estimate)
    {
        if (estimate is not { } bytes || SourceBytes <= 0)
            return _loc["compression.unknown"];
        var smaller = AskedCompression.PercentSmaller(SourceBytes, bytes);
        if (smaller > 0)
            return _loc.Format("compression.estimate", AskedCompression.SizeText(bytes), smaller);
        return OnlyIfSmaller ? _loc["compression.not_smaller_kept"] : _loc.Format("compression.not_smaller", AskedCompression.SizeText(bytes));
    }

    public void Dispose()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
    }
}
