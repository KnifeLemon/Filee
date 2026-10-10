// Watch folders page: folders whose new files are converted automatically (WatchFolderService). Every change is
// saved at once; the service applies it after a short pause.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
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

    /// <summary>
    /// A converted file as this rule would save it, e.g. <c>scan.jpg → converted\scan.pdf</c> (a file already in the
    /// folder when there is one), and below it what happens when the name is taken and to the original. Null when the
    /// rule has no preset.
    /// </summary>
    internal (string Example, string Details)? PreviewOf(WatchRule rule)
    {
        if (_store.FindPreset(rule.PresetId) is not { } preset)
            return null;
        var folder = rule.Folder.Trim();
        var source = SampleFile(rule) ?? Path.Combine(folder, _loc["watch.sample_file"]);
        var extension = TargetExtension(preset, source);
        var applied = rule.Apply(preset);
        var output = OutputPathResolver.Resolve(applied.Output,
            new OutputPathResolver.Tokens(source, _loc.DisplayName(preset), 1, DateTime.Now), extension, _ => false) ?? "";
        var shown = folder.Length > 0 && output.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(folder, output)
            : output;
        var details = $"{_loc["presets.conflict"]}: {_loc[$"presets.conflict.{preset.Output.Conflict}"]} · "
            + _loc[rule.Originals == AfterConversion.MoveToOriginals ? "watch.preview_move" : "watch.preview_keep"];
        return ($"{Path.GetFileName(source)} → {shown}", details);
    }

    /// <summary>The pattern a rule without its own file name rule uses: the preset's.</summary>
    internal string PresetNamePattern(WatchRule rule) =>
        _store.FindPreset(rule.PresetId) is { } preset && !string.IsNullOrWhiteSpace(preset.Output.FileNamePattern)
            ? preset.Output.FileNamePattern
            : "{name}";

    internal string Format(string key, params object[] args) => _loc.Format(key, args);

    internal string Text(string key) => _loc[key];

    internal ILocalizer Localizer => _loc;

    /// <summary>The file name a source gets in this folder with <paramref name="steps"/> in place of the rule's.</summary>
    internal string OutputNameOf(WatchRule rule, string source, IReadOnlyList<RenameStep> steps)
    {
        if (_store.FindPreset(rule.PresetId) is not { } preset)
            return source;
        var trial = rule.Clone();
        trial.Renames = [.. steps];
        var applied = trial.Apply(preset);
        var output = OutputPathResolver.Resolve(applied.Output,
            new OutputPathResolver.Tokens(source, _loc.DisplayName(preset), 1, DateTime.Now), TargetExtension(preset, source), _ => false);
        return output is null ? source : Path.GetFileName(output);
    }

    /// <summary>Names of a few files in the folder the rule would convert, for the rename preview.</summary>
    internal static IReadOnlyList<string> SampleNames(WatchRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Folder) || !Directory.Exists(rule.Folder))
            return [];
        try
        {
            return Directory.EnumerateFiles(rule.Folder).Take(500).Where(path => !FolderWatcher.ShouldIgnore(path, rule))
                .Select(Path.GetFileName).OfType<string>().Take(8).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string TargetExtension(Preset preset, string source) => preset.TargetFormat switch
    {
        BuiltInData.SameAsSource => FormatRegistry.ExtensionOf(source),
        FormatRegistry.Folder => "",
        var id => FormatRegistry.FindById(id)?.PrimaryExtension ?? id,
    };

    /// <summary>Extensions starting with what is typed ("he" → *.heic, *.heif), minus the patterns already there.</summary>
    internal static IReadOnlyList<TagSuggestion> ExtensionSuggestions(string typed, IReadOnlyCollection<string> existing)
    {
        var start = typed.Trim().TrimStart('*').TrimStart('.');
        if (start.Length == 0 || start.IndexOfAny(['*', '?', '/']) >= 0)
            return [];
        return FormatRegistry.Known
            .SelectMany(format => format.Extensions.Select(extension => (Extension: extension, Format: format)))
            .Where(e => e.Extension.StartsWith(start, StringComparison.OrdinalIgnoreCase) && !e.Extension.Contains('.'))
            .Select(e => new TagSuggestion($"*.{e.Extension}", $"*.{e.Extension}", e.Format.DisplayName))
            .Where(s => !existing.Contains(s.Value, StringComparer.OrdinalIgnoreCase))
            .DistinctBy(s => s.Value, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    /// <summary>How many files in the folder the rule's patterns let through now; null without patterns or folder.</summary>
    internal static int? MatchCount(WatchRule rule)
    {
        if (rule.Include.Count == 0 || string.IsNullOrWhiteSpace(rule.Folder) || !Directory.Exists(rule.Folder))
            return null;
        try
        {
            var option = rule.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return Directory.EnumerateFiles(rule.Folder, "*", option).Take(5000).Count(path => !FolderWatcher.ShouldIgnore(path, rule));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A file in the folder this rule would convert, for the preview; null when there is none (yet).</summary>
    private static string? SampleFile(WatchRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Folder) || !Directory.Exists(rule.Folder))
            return null;
        try
        {
            return Directory.EnumerateFiles(rule.Folder).Take(200).FirstOrDefault(path => !FolderWatcher.ShouldIgnore(path, rule));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

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
        _fileNamePattern = rule.FileNamePattern;
        // Options in use stay in view; a new rule starts with them folded away.
        _showOptions = rule.Include.Count > 0 || rule.FileNamePattern.Length > 0 || rule.Renames.Count > 0;
        RefreshIncludeTags();
        RefreshPreview();
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

    // Optional settings ("More options"): empty means every file, named as the preset says.

    [ObservableProperty] private bool _showOptions;

    /// <summary>The "Files to convert" patterns as chips.</summary>
    [ObservableProperty] private IReadOnlyList<TagChip> _includeTags = [];
    [ObservableProperty] private IReadOnlyList<TagSuggestion> _includeSuggestions = [];
    [ObservableProperty] private string _includeText = "";

    /// <summary>Why the typed pattern wasn't added, or null.</summary>
    [ObservableProperty] private string? _includeError;

    /// <summary>"3 files in the folder match now", or null.</summary>
    [ObservableProperty] private string? _includeMatches;

    [ObservableProperty] private string _fileNamePattern;

    /// <summary>"Same as the preset: {name}" in the empty file name box.</summary>
    [ObservableProperty] private string _fileNamePlaceholder = "";

    /// <summary>The rename steps, one "find → replace" line each, for the card.</summary>
    [ObservableProperty] private IReadOnlyList<string> _renameSummary = [];

    public bool HasRenames => RenameSummary.Count > 0;

    /// <summary>"scan.jpg → converted\scan.pdf": a converted file as it would be saved.</summary>
    [ObservableProperty] private string? _previewExample;

    /// <summary>What happens when the name is taken, and to the original.</summary>
    [ObservableProperty] private string? _previewDetails;

    public bool HasPreview => PreviewExample is not null;

    partial void OnEnabledChanged(bool value) => Change(r => r.Enabled = value);
    partial void OnFolderChanged(string value) => Change(r => r.Folder = value.Trim());
    partial void OnPresetChanged(Choice<string>? value) => Change(r => r.PresetId = value?.Value ?? "");
    partial void OnIncludeSubfoldersChanged(bool value) => Change(r => r.IncludeSubfolders = value);
    partial void OnOutputFolderChanged(string value) => Change(r => r.OutputFolder = value.Trim());
    partial void OnOriginalsChanged(Choice<AfterConversion> value) => Change(r => r.Originals = value.Value);
    partial void OnFileNamePatternChanged(string value) => Change(r => r.FileNamePattern = value.Trim());

    partial void OnIncludeTextChanged(string value)
    {
        IncludeError = null;
        IncludeSuggestions = WatchFoldersPageViewModel.ExtensionSuggestions(value, Rule.Include);
    }

    [RelayCommand]
    private void ToggleOptions() => ShowOptions = !ShowOptions;

    /// <summary>Adds a typed pattern: "heic" becomes *.heic, plain text matches names containing it.</summary>
    [RelayCommand]
    private void AddInclude(string? typed)
    {
        var pattern = FileNameFilter.Normalize(typed);
        if (pattern is null)
            return;
        if (FileNameFilter.Validate(pattern) is { } error)
        {
            IncludeText = typed!.Trim(); // kept, so the mistake can be fixed
            IncludeError = _page.Format("watch.invalid_regex", error);
            return;
        }
        IncludeText = "";
        if (!Rule.Include.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            Change(r => r.Include.Add(pattern));
    }

    [RelayCommand]
    private void RemoveInclude(TagChip? chip)
    {
        if (chip is not null)
            Change(r => r.Include.RemoveAll(p => p == chip.Value));
    }

    /// <summary>The dialog that edits the rename steps, on copies of them.</summary>
    public RenameToolViewModel CreateRenameTool() =>
        new(Rule.Renames, WatchFoldersPageViewModel.SampleNames(Rule), (steps, source) => _page.OutputNameOf(Rule, source, steps), _page.Localizer);

    /// <summary>Takes the steps the dialog applied.</summary>
    public void SetRenames(List<RenameStep> steps) => Change(r => r.Renames = steps);

    [RelayCommand]
    private void Remove() => _page.Remove(this);

    internal void RefreshStatus() => Status = _page.StatusOf(Rule);

    private void Change(Action<WatchRule> change)
    {
        change(Rule);
        _page.Save();
        RefreshStatus();
        RefreshIncludeTags();
        RefreshPreview();
    }

    private void RefreshIncludeTags()
    {
        IncludeTags = Rule.Include.Select(p => new TagChip(p, p)).ToList();
        IncludeMatches = WatchFoldersPageViewModel.MatchCount(Rule) is { } count ? _page.Format("watch.include_matches", count) : null;
    }

    private void RefreshPreview()
    {
        RenameSummary = Rule.Renames.Select(r => RenameRecipes.Describe(_page.Localizer, r)).ToList();
        OnPropertyChanged(nameof(HasRenames));
        FileNamePlaceholder = _page.Format("watch.name_placeholder", _page.PresetNamePattern(Rule));
        var preview = _page.PreviewOf(Rule);
        PreviewExample = preview?.Example;
        PreviewDetails = preview?.Details;
        OnPropertyChanged(nameof(HasPreview));
    }
}

