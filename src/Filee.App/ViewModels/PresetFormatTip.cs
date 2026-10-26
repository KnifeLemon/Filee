using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.ViewModels;

public sealed record PresetFormatTipRow(string Kind, string Names);

/// <summary>Content of an available preset's palette tooltip, built only when first opened.</summary>
public sealed partial class PresetFormatTip(
    Preset preset, PresetAvailability availability, Func<RoutePlanner> planner, ILocalizer loc) : ObservableObject
{
    private static readonly HashSet<string> DefaultTargets = BuiltInData.CreatePresets()
        .Where(p => !p.IsGroup && p.Kind == PresetKind.Convert
            && p.TargetFormat is not BuiltInData.SameAsSource and not FormatRegistry.Folder)
        .Select(p => p.TargetFormat).ToHashSet();

    [ObservableProperty] private string? _title;
    [ObservableProperty] private string? _makesBefore;
    [ObservableProperty] private string? _makesName;
    [ObservableProperty] private string? _makesAfter;
    [ObservableProperty] private string? _worksWith;
    [ObservableProperty] private string? _anyFile;
    [ObservableProperty] private string? _reason;
    [ObservableProperty] private IReadOnlyList<PresetFormatTipRow> _summaryRows = [];
    [ObservableProperty] private IReadOnlyList<PresetFormatTipRow> _fullRows = [];
    [ObservableProperty] private string? _footer;

    private bool _isExpanded;
    public bool CanExpand => Footer is not null;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value && CanExpand))
            {
                OnPropertyChanged(nameof(Rows));
                OnPropertyChanged(nameof(ShowFooter));
            }
        }
    }

    public IReadOnlyList<PresetFormatTipRow> Rows => IsExpanded ? FullRows : SummaryRows;
    public bool ShowFooter => Footer is not null && !IsExpanded;
    internal int BuildCount { get; private set; }

    partial void OnSummaryRowsChanged(IReadOnlyList<PresetFormatTipRow> value) => OnPropertyChanged(nameof(Rows));
    partial void OnFullRowsChanged(IReadOnlyList<PresetFormatTipRow> value) => OnPropertyChanged(nameof(Rows));
    partial void OnFooterChanged(string? value)
    {
        OnPropertyChanged(nameof(CanExpand));
        OnPropertyChanged(nameof(ShowFooter));
    }

    public static PresetFormatTip Build(Preset preset, PresetAvailability availability, RoutePlanner planner, ILocalizer loc)
    {
        var tip = new PresetFormatTip(preset, availability, () => planner, loc);
        tip.EnsureBuilt();
        return tip;
    }

    public void EnsureBuilt()
    {
        if (BuildCount != 0)
            return;
        BuildCount++;
        if (preset.IsGroup)
            return;

        if (!preset.PacksIntoOneArchive)
        {
            var usable = availability.UsableFormats(preset, FormatRegistry.Known);
            if (usable.Count == 0)
            {
                Reason = availability.Check(preset, FormatRegistry.Known);
                return;
            }

            WorksWith = loc.Format("toolbar.chip_tip.works_with", usable.Count);
            var routes = planner();
            var categories = usable.GroupBy(f => f.Category)
                .Select(group => new
                {
                    Category = group.Key,
                    Formats = group.OrderByDescending(f => DefaultTargets.Contains(f.Id)).ToList(),
                    MinSteps = group.Min(f => preset.IsTool ? 1
                        : routes.Plan(f.Id, preset.TargetFormat == BuiltInData.SameAsSource ? f.Id : preset.TargetFormat)
                            is { Steps.Count: > 0 } route ? route.Steps.Count : 1),
                })
                .OrderBy(g => g.MinSteps == 1 ? 0 : 1)
                .ThenByDescending(g => g.Formats.Count)
                .ThenBy(g => g.Category)
                .ToList();

            string Names(IReadOnlyList<FileFormat> formats, int count)
            {
                var names = LocalizationService.LeftToRight(string.Join(loc["toolbar.chip_tip.name_separator"],
                    formats.Take(count).Select(f => FormatLabels.Name(loc, f))));
                return count < formats.Count
                    ? loc.Format("toolbar.chip_tip.names_more", names, formats.Count - count)
                    : names;
            }

            PresetFormatTipRow Row(FormatCategory category, IReadOnlyList<FileFormat> formats, int count) =>
                new(loc.Format("toolbar.chip_tip.kind", FormatLabels.Category(loc, category), formats.Count),
                    Names(formats, count));

            FullRows = categories.Select(g => Row(g.Category, g.Formats, g.Formats.Count)).ToList();
            var shown = categories.Where(g => g.MinSteps < 3).Take(6).ToList();
            SummaryRows = shown.Select(g => Row(g.Category, g.Formats, Math.Min(3, g.Formats.Count))).ToList();
            var hidden = usable.Count - shown.Sum(g => g.Formats.Count);
            Footer = hidden > 0 ? loc.Format("toolbar.chip_tip.more_shift", hidden)
                : shown.Any(g => g.Formats.Count > 3) ? loc["toolbar.chip_tip.shift_all"] : null;
        }
        else
            AnyFile = loc["toolbar.chip_tip.any_file"];

        Title = loc.DisplayName(preset);
        if (preset.TargetFormat == BuiltInData.SameAsSource)
        {
            MakesBefore = loc["toolbar.chip_tip.makes_same"];
            return;
        }

        var output = FormatRegistry.Get(preset.TargetFormat);
        var template = loc[preset.TargetFormat == FormatRegistry.Folder
            ? "toolbar.chip_tip.makes_name" : "toolbar.chip_tip.makes"];
        var position = template.IndexOf("{0}", StringComparison.Ordinal);
        MakesBefore = template[..position];
        MakesName = LocalizationService.LeftToRight(FormatLabels.Name(loc, output));
        MakesAfter = template[(position + 3)..].Replace("{1}", FormatLabels.Category(loc, output.Category),
            StringComparison.Ordinal);
    }
}
