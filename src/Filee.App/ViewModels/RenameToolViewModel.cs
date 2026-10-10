// The name tool of a watch folder (Watch folders → More options → Replace part of the name → Name tool…): regular
// expression steps applied in order, ready-made replacements to add with a double-click, and a preview of files from
// the folder before and after. Works on copies; the folder takes the steps only when the dialog is applied.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.ViewModels;

/// <summary>A file name before and after the steps.</summary>
public sealed record RenamePreview(string Before, string After);

public sealed partial class RenameToolViewModel : ObservableObject
{
    private readonly ILocalizer _loc;
    private readonly Func<IReadOnlyList<RenameStep>, string, string> _rename;
    private readonly IReadOnlyList<string> _samples;

    /// <param name="steps">The folder's steps (copied; changes stay here until <see cref="Result"/> is taken).</param>
    /// <param name="samples">Names of files in the folder (with extension), for the preview.</param>
    /// <param name="rename">The name a file gets with some steps: source name → converted file name.</param>
    public RenameToolViewModel(IEnumerable<RenameStep> steps, IReadOnlyList<string> samples,
        Func<IReadOnlyList<RenameStep>, string, string> rename, ILocalizer loc)
    {
        _loc = loc;
        _rename = rename;
        _samples = samples;
        Kinds = Enum.GetValues<RenameKind>().Select(k => new Choice<RenameKind>(k, loc[$"watch.kind.{k}"])).ToList();
        Recipes = RenameRecipes.All.Select(r => new RenameRecipeViewModel(r, loc[r.TitleKey], this)).ToList();
        foreach (var step in steps)
            Steps.Add(new RenameStepViewModel(step.Clone(), this));
        _customName = samples.Count == 0 ? loc["watch.sample_file"] : "";
        Refresh();
    }

    /// <summary>Replacements applied in order.</summary>
    public ObservableCollection<RenameStepViewModel> Steps { get; } = [];

    /// <summary>What a rule can do, for its drop-down.</summary>
    public IReadOnlyList<Choice<RenameKind>> Kinds { get; }

    /// <summary>Ready-made rules, each with what it does to an example name.</summary>
    public IReadOnlyList<RenameRecipeViewModel> Recipes { get; }

    /// <summary>Files from the folder, before and after.</summary>
    public ObservableCollection<RenamePreview> Previews { get; } = [];

    public bool HasSamples => _samples.Count > 0;

    /// <summary>A name to try the steps on.</summary>
    [ObservableProperty] private string _customName;

    /// <summary>What <see cref="CustomName"/> becomes.</summary>
    [ObservableProperty] private string _customResult = "";

    public bool HasNoSteps => Steps.Count == 0;

    /// <summary>True when every step's expression is valid.</summary>
    public bool IsValid => Steps.All(s => s.Error is null);

    /// <summary>The rules to keep: the ones that change something.</summary>
    public List<RenameStep> Result => Steps.Where(s => !s.Step.IsBlank).Select(s => s.Step.Clone()).ToList();

    partial void OnCustomNameChanged(string value) => Refresh();

    [RelayCommand]
    private void AddStep() => AddStep(new RenameStep());

    internal void AddStep(RenameStep step)
    {
        Steps.Add(new RenameStepViewModel(step, this));
        StepsChanged();
    }

    internal void RemoveStep(RenameStepViewModel step)
    {
        Steps.Remove(step);
        StepsChanged();
    }

    /// <summary>Moves a step one place up (it runs earlier).</summary>
    internal void MoveUp(RenameStepViewModel step)
    {
        var index = Steps.IndexOf(step);
        if (index > 0)
        {
            Steps.Move(index, index - 1);
            StepsChanged();
        }
    }

    internal void StepsChanged() => Refresh();

    internal string InvalidRegex(string error) => _loc.Format("watch.invalid_regex", error);

    internal string Text(string key) => _loc[key];

    private void Refresh()
    {
        var steps = Steps.Select(s => s.Step).Where(s => !s.IsBlank).ToList();
        Previews.Clear();
        foreach (var sample in _samples)
            Previews.Add(new RenamePreview(sample, _rename(steps, sample)));
        CustomResult = CustomName.Trim().Length == 0 ? "" : _rename(steps, CustomName.Trim());
        OnPropertyChanged(nameof(HasNoSteps));
        OnPropertyChanged(nameof(IsValid));
    }
}

/// <summary>One rule: what it does (<see cref="RenameKind"/>) and the text it needs.</summary>
public sealed partial class RenameStepViewModel : ObservableObject
{
    private readonly RenameToolViewModel _tool;

    public RenameStepViewModel(RenameStep step, RenameToolViewModel tool)
    {
        Step = step;
        _tool = tool;
        _kind = tool.Kinds.First(k => k.Value == step.Kind);
        _find = step.Find;
        _replace = step.Replace;
        Validate();
    }

    public RenameStep Step { get; }

    public IReadOnlyList<Choice<RenameKind>> Kinds => _tool.Kinds;

    [ObservableProperty] private Choice<RenameKind> _kind;
    [ObservableProperty] private string _find;
    [ObservableProperty] private string _replace;

    /// <summary>Why <see cref="Find"/> is not a valid regular expression, or null.</summary>
    [ObservableProperty] private string? _error;

    /// <summary>The kind needs the text to look for.</summary>
    public bool ShowFind => Kind.Value is RenameKind.Replace or RenameKind.Remove or RenameKind.Regex;

    /// <summary>The kind needs the text that goes in.</summary>
    public bool ShowReplace => Kind.Value is RenameKind.Replace or RenameKind.Prefix or RenameKind.Suffix or RenameKind.Regex;

    public bool ShowArrow => ShowFind && ShowReplace;

    public bool ShowFindOnly => ShowFind && !ShowReplace;

    public bool ShowReplaceOnly => ShowReplace && !ShowFind;

    public string FindPlaceholder => _tool.Text(Kind.Value switch
    {
        RenameKind.Remove => "watch.ph.remove",
        RenameKind.Regex => "watch.rename_find",
        _ => "watch.ph.find",
    });

    public string ReplacePlaceholder => _tool.Text(Kind.Value switch
    {
        RenameKind.Prefix => "watch.ph.prefix",
        RenameKind.Suffix => "watch.ph.suffix",
        RenameKind.Regex => "watch.rename_replace",
        _ => "watch.ph.replace",
    });

    partial void OnKindChanged(Choice<RenameKind> value)
    {
        Step.Kind = value.Value;
        OnPropertyChanged(nameof(ShowFind));
        OnPropertyChanged(nameof(ShowReplace));
        OnPropertyChanged(nameof(ShowArrow));
        OnPropertyChanged(nameof(ShowFindOnly));
        OnPropertyChanged(nameof(ShowReplaceOnly));
        OnPropertyChanged(nameof(FindPlaceholder));
        OnPropertyChanged(nameof(ReplacePlaceholder));
        Validate();
        _tool.StepsChanged();
    }

    partial void OnFindChanged(string value)
    {
        Step.Find = value;
        Validate();
        _tool.StepsChanged();
    }

    partial void OnReplaceChanged(string value)
    {
        Step.Replace = value;
        _tool.StepsChanged();
    }

    [RelayCommand]
    private void Remove() => _tool.RemoveStep(this);

    [RelayCommand]
    private void MoveUp() => _tool.MoveUp(this);

    // Only a regular expression can be wrong; plain text is taken as it is.
    private void Validate() => Error = Step.Kind == RenameKind.Regex && OutputPathResolver.RenameError(Find) is { } error
        ? _tool.InvalidRegex(error)
        : null;
}

/// <summary>A ready-made rule in the list: what it does and an example.</summary>
public sealed partial class RenameRecipeViewModel(RenameRecipe recipe, string title, RenameToolViewModel tool) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>"IMG_0412 → Photo_0412".</summary>
    public string Example { get; } = $"{recipe.Example}  →  {recipe.Step.Apply(recipe.Example)}";

    /// <summary>Marks the one that needs a regular expression.</summary>
    public bool IsAdvanced { get; } = recipe.Step.Kind == RenameKind.Regex;

    /// <summary>Adds the rule as the last one (double-click, or the + button).</summary>
    [RelayCommand]
    private void Add() => tool.AddStep(recipe.Step.Clone());
}
