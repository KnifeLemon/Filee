// Watch folders page: folders whose new files are converted automatically (WatchFolderService). Every change is
// saved at once; the service applies it after a short pause.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Settings;
using Filee.Core.Watching;

namespace Filee.App.ViewModels.Pages;

public sealed partial class WatchFoldersPageViewModel : ObservableObject, IDisposable
{
    private readonly UserDataStore _store;
    private readonly WatchFolderService _service;
    private readonly ILocalizer _loc;

    public WatchFoldersPageViewModel(UserDataStore store, WatchFolderService service, ILocalizer loc)
    {
        _store = store;
        _service = service;
        _loc = loc;
        Presets = store.Presets.Select(p => new Choice<string>(p.Id, loc.DisplayName(p), FormatLabels.NameOf(loc, p.TargetFormat))).ToList();
        OriginalsChoices =
        [
            new Choice<AfterConversion>(AfterConversion.Keep, loc["watch.originals_keep"]),
            new Choice<AfterConversion>(AfterConversion.MoveToOriginals, loc["watch.originals_move"]),
        ];
        foreach (var rule in store.Settings.WatchFolders)
            Rules.Add(new WatchRuleViewModel(rule, this));
        service.StatusChanged += OnStatusChanged;
    }

    public ObservableCollection<WatchRuleViewModel> Rules { get; } = [];
    public IReadOnlyList<Choice<string>> Presets { get; }
    public IReadOnlyList<Choice<AfterConversion>> OriginalsChoices { get; }
    public bool IsEmpty => Rules.Count == 0;

    [RelayCommand]
    private void Add()
    {
        var rule = new WatchRule { PresetId = Presets.FirstOrDefault(p => p.Value == "to-pdf")?.Value ?? Presets.FirstOrDefault()?.Value ?? "" };
        _store.Settings.WatchFolders.Add(rule);
        Rules.Add(new WatchRuleViewModel(rule, this));
        OnPropertyChanged(nameof(IsEmpty));
        Save();
    }

    internal void Remove(WatchRuleViewModel item)
    {
        _store.Settings.WatchFolders.Remove(item.Rule);
        Rules.Remove(item);
        OnPropertyChanged(nameof(IsEmpty));
        Save();
    }

    internal void Save() => _store.SaveSettings();

    internal string StatusOf(WatchRule rule) =>
        !rule.Enabled ? _loc["watch.status_off"]
        : string.IsNullOrWhiteSpace(rule.Folder) ? _loc["watch.status_no_folder"]
        : _service.ErrorOf(rule.Id) is { } error ? _loc.Format("watch.status_error", error)
        : _service.IsWatching(rule.Id) ? _loc["watch.status_watching"]
        : _loc["watch.status_starting"];

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        foreach (var rule in Rules)
            rule.RefreshStatus();
    }

    public void Dispose() => _service.StatusChanged -= OnStatusChanged;
}

/// <summary>One watch folder on the page; edits go straight into the settings' <see cref="WatchRule"/>.</summary>
public sealed partial class WatchRuleViewModel : ObservableObject
{
    private readonly WatchFoldersPageViewModel _page;

    public WatchRuleViewModel(WatchRule rule, WatchFoldersPageViewModel page)
    {
        Rule = rule;
        _page = page;
        _enabled = rule.Enabled;
        _folder = rule.Folder;
        _preset = page.Presets.FirstOrDefault(p => p.Value == rule.PresetId);
        _includeSubfolders = rule.IncludeSubfolders;
        _outputFolder = rule.OutputFolder;
        _originals = page.OriginalsChoices.First(c => c.Value == rule.Originals);
        _status = page.StatusOf(rule);
    }

    public WatchRule Rule { get; }
    public IReadOnlyList<Choice<string>> Presets => _page.Presets;
    public IReadOnlyList<Choice<AfterConversion>> OriginalsChoices => _page.OriginalsChoices;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _folder;
    [ObservableProperty] private Choice<string>? _preset;
    [ObservableProperty] private bool _includeSubfolders;
    [ObservableProperty] private string _outputFolder;
    [ObservableProperty] private Choice<AfterConversion> _originals;
    [ObservableProperty] private string _status;

    partial void OnEnabledChanged(bool value) => Change(r => r.Enabled = value);
    partial void OnFolderChanged(string value) => Change(r => r.Folder = value.Trim());
    partial void OnPresetChanged(Choice<string>? value) => Change(r => r.PresetId = value?.Value ?? "");
    partial void OnIncludeSubfoldersChanged(bool value) => Change(r => r.IncludeSubfolders = value);
    partial void OnOutputFolderChanged(string value) => Change(r => r.OutputFolder = value.Trim());
    partial void OnOriginalsChanged(Choice<AfterConversion> value) => Change(r => r.Originals = value.Value);

    [RelayCommand]
    private void Remove() => _page.Remove(this);

    internal void RefreshStatus() => Status = _page.StatusOf(Rule);

    private void Change(Action<WatchRule> change)
    {
        change(Rule);
        _page.Save();
        RefreshStatus();
    }
}
