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
        var extension = preset.TargetFormat switch
        {
            BuiltInData.SameAsSource => FormatRegistry.ExtensionOf(source),
            FormatRegistry.Folder => "",
            var id => FormatRegistry.FindById(id)?.PrimaryExtension ?? id,
        };
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
        foreach (var step in rule.Renames)
            RenameSteps.Add(new RenameStepViewModel(step, this));
        Recipes = RenameRecipes.All.Select(r => new RenameRecipeViewModel(r, page.Text(r.TitleKey), page.Text("watch.recipe_removes"), this)).ToList();
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

    /// <summary>Replacements applied in order to the name.</summary>
    public ObservableCollection<RenameStepViewModel> RenameSteps { get; } = [];

    /// <summary>Ready-made replacements, each with what it does to an example name.</summary>
    public IReadOnlyList<RenameRecipeViewModel> Recipes { get; }

    [ObservableProperty] private bool _showRecipes;

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

    [RelayCommand]
    private void ToggleRecipes() => ShowRecipes = !ShowRecipes;

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

    [RelayCommand]
    private void AddStep() => AddStep(new RenameStep());

    internal void AddStep(RenameStep step)
    {
        Rule.Renames.Add(step);
        RenameSteps.Add(new RenameStepViewModel(step, this));
        StepsChanged();
    }

    internal void RemoveStep(RenameStepViewModel step)
    {
        Rule.Renames.Remove(step.Step);
        RenameSteps.Remove(step);
        StepsChanged();
    }

    internal void StepsChanged() => Change(_ => { });

    internal string InvalidRegex(string error) => _page.Format("watch.invalid_regex", error);

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
        FileNamePlaceholder = _page.Format("watch.name_placeholder", _page.PresetNamePattern(Rule));
        var preview = _page.PreviewOf(Rule);
        PreviewExample = preview?.Example;
        PreviewDetails = preview?.Details;
        OnPropertyChanged(nameof(HasPreview));
    }
}

/// <summary>One replacement in a watch folder's names: a regular expression and what goes in its place.</summary>
public sealed partial class RenameStepViewModel : ObservableObject
{
    private readonly WatchRuleViewModel _rule;

    public RenameStepViewModel(RenameStep step, WatchRuleViewModel rule)
    {
        Step = step;
        _rule = rule;
        _find = step.Find;
        _replace = step.Replace;
        Validate();
    }

    public RenameStep Step { get; }

    [ObservableProperty] private string _find;
    [ObservableProperty] private string _replace;

    /// <summary>Why <see cref="Find"/> is not a valid regular expression, or null.</summary>
    [ObservableProperty] private string? _error;

    partial void OnFindChanged(string value)
    {
        Step.Find = value;
        Validate();
        _rule.StepsChanged();
    }

    partial void OnReplaceChanged(string value)
    {
        Step.Replace = value;
        _rule.StepsChanged();
    }

    [RelayCommand]
    private void Remove() => _rule.RemoveStep(this);

    private void Validate() => Error = OutputPathResolver.RenameError(Find) is { } error ? _rule.InvalidRegex(error) : null;
}

/// <summary>A ready-made replacement in the list: what it does, its expression and an example.</summary>
public sealed partial class RenameRecipeViewModel(RenameRecipe recipe, string title, string removes, WatchRuleViewModel rule) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>"IMG_(\d+) → Photo_$1"; an empty replacement shows as "(removed)".</summary>
    public string Expression { get; } = $"{recipe.Find}  →  {(recipe.Replace.Length == 0 ? removes : recipe.Replace)}";

    /// <summary>"IMG_0412 → Photo_0412".</summary>
    public string Example { get; } = $"{recipe.Example}  →  {OutputPathResolver.Rename(recipe.Example, recipe.Find, recipe.Replace)}";

    /// <summary>Adds the replacement as the last step (double-click, or the + button).</summary>
    [RelayCommand]
    private void Add() => rule.AddStep(new RenameStep { Find = recipe.Find, Replace = recipe.Replace });
}
