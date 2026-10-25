// Settings → Security and limits: documents with macros or other active content (AppSettings.Security), and the limits
// for pictures, files converted at once and the time a built-in engine may spend on one step (AppSettings.Limits).

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.ViewModels.Pages;

public sealed partial class SecurityPageViewModel : ObservableObject, IDisposable
{
    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;

    public SecurityPageViewModel(UserDataStore store, ILocalizer loc)
    {
        _store = store;
        _loc = loc;
        Policies = Enum.GetValues<ActiveContentPolicy>()
            .Select(p => new Choice<ActiveContentPolicy>(p, loc[$"security.policy.{p}"], loc[$"security.policy.{p}_hint"]))
            .ToList();
        _policy = Policies.First(p => p.Value == store.Settings.Security.ActiveContent);
        Load(store.Settings.Limits);
        store.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>Ask / convert / never, each with what it means.</summary>
    public IReadOnlyList<Choice<ActiveContentPolicy>> Policies { get; }

    [ObservableProperty] private Choice<ActiveContentPolicy> _policy;

    partial void OnPolicyChanged(Choice<ActiveContentPolicy>? oldValue, Choice<ActiveContentPolicy> newValue)
    {
        if (!Choices.Restore(oldValue, newValue, v => Policy = v) && _store.Settings.Security.ActiveContent != newValue.Value)
            Save(s => s.Security.ActiveContent = newValue.Value);
    }

    /// <summary>Largest picture in megapixels; 0 for no limit.</summary>
    [ObservableProperty] private int _maxImageMegapixels;

    /// <summary>Longest side of a picture in pixels; 0 for ImageMagick's default.</summary>
    [ObservableProperty] private int _maxImageSide;

    /// <summary>Memory for pictures in MB; 0 for ImageMagick's default.</summary>
    [ObservableProperty] private int _maxImageMemoryMb;

    /// <summary>Disk space for pictures in MB; 0 for ImageMagick's default.</summary>
    [ObservableProperty] private int _maxImageDiskMb;

    /// <summary>Files of a job converted at once; 0 for one per processor core.</summary>
    [ObservableProperty] private int _maxParallelFiles;

    /// <summary>Minutes per step for built-in engines; 0 for no limit.</summary>
    [ObservableProperty] private int _stepTimeoutMinutes;

    public int MaxMegapixels => LimitSettings.MaxMegapixels;
    public int MaxSide => LimitSettings.MaxSide;
    public int MaxMegabytes => LimitSettings.MaxMegabytes;
    public int MaxFiles => LimitSettings.MaxFiles;
    public int MaxTimeoutMinutes => LimitSettings.MaxTimeoutMinutes;

    /// <summary>"0 = one per processor core (8 on this computer)".</summary>
    public string ParallelHint => string.Format(_loc["security.parallel_hint"], Environment.ProcessorCount);

    partial void OnMaxImageMegapixelsChanged(int value) => Save(s => s.Limits.MaxImageMegapixels = Math.Clamp(value, 0, LimitSettings.MaxMegapixels));
    partial void OnMaxImageSideChanged(int value) => Save(s => s.Limits.MaxImageSide = Math.Clamp(value, 0, LimitSettings.MaxSide));
    partial void OnMaxImageMemoryMbChanged(int value) => Save(s => s.Limits.MaxImageMemoryMb = Math.Clamp(value, 0, LimitSettings.MaxMegabytes));
    partial void OnMaxImageDiskMbChanged(int value) => Save(s => s.Limits.MaxImageDiskMb = Math.Clamp(value, 0, LimitSettings.MaxMegabytes));
    partial void OnMaxParallelFilesChanged(int value) => Save(s => s.Limits.MaxParallelFiles = Math.Clamp(value, 0, LimitSettings.MaxFiles));
    partial void OnStepTimeoutMinutesChanged(int value) => Save(s => s.Limits.StepTimeoutMinutes = Math.Clamp(value, 0, LimitSettings.MaxTimeoutMinutes));

    /// <summary>Puts every limit back to Filee's default.</summary>
    [RelayCommand]
    private void ResetLimits()
    {
        Save(s => s.Limits = new LimitSettings());
        Load(_store.Settings.Limits);
    }

    private void Load(LimitSettings limits)
    {
        // Through the properties: the change handlers save the same values again, which is harmless.
        MaxImageMegapixels = limits.EffectiveMaxImageMegapixels;
        MaxImageSide = limits.EffectiveMaxImageSide;
        MaxImageMemoryMb = limits.EffectiveMaxImageMemoryMb;
        MaxImageDiskMb = limits.EffectiveMaxImageDiskMb;
        MaxParallelFiles = Math.Clamp(limits.MaxParallelFiles, 0, LimitSettings.MaxFiles);
        StepTimeoutMinutes = Math.Clamp(limits.StepTimeoutMinutes, 0, LimitSettings.MaxTimeoutMinutes);
    }

    /// <summary>"Don't ask again" in the window about active content sets the policy too.</summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (Policy.Value != _store.Settings.Security.ActiveContent)
            Policy = Policies.First(p => p.Value == _store.Settings.Security.ActiveContent);
    }

    public void Dispose() => _store.SettingsChanged -= OnSettingsChanged;

    private void Save(Action<AppSettings> change)
    {
        var before = Snapshot();
        change(_store.Settings);
        if (Snapshot() != before)
            _store.SaveSettings();
    }

    private string Snapshot() =>
        $"{_store.Settings.Security.ActiveContent}|{_store.Settings.Limits.MaxImageMegapixels}|{_store.Settings.Limits.MaxImageSide}|" +
        $"{_store.Settings.Limits.MaxImageMemoryMb}|{_store.Settings.Limits.MaxImageDiskMb}|{_store.Settings.Limits.MaxParallelFiles}|" +
        $"{_store.Settings.Limits.StepTimeoutMinutes}";
}
