using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class PaletteChipTipModelTests
{
    private static PresetFormatTip Tip(ToolbarPageViewModel vm, string id) =>
        Assert.IsType<PresetFormatTip>(vm.Palette.Single(c => c.PresetId == id).Tip);

    [AvaloniaFact]
    public void T1_usable_formats_match_single_format_checks_in_registry_order()
    {
        var extra = new PresetAvailabilityTests.FakeConverter("test-inputs",
            ("csv", "xlsx"), ("json", "xlsx"), ("png", "png"), ("jpg", "jpg"));
        using var f = new PresetAvailabilityTests.Fixture(extra);
        foreach (var id in new[] { "to-xlsx", "half-size", "tool-crop", "tool-pdf-crop", "test-pack", "test-mp4" })
        {
            var preset = f.Preset(id);
            var expected = FormatRegistry.Known.Where(format =>
                f.Availability.Check(preset, [format]) is null).ToList();
            Assert.Equal(expected, f.Availability.UsableFormats(preset, FormatRegistry.Known));
        }
    }

    [AvaloniaFact]
    public void T8_catalog_refresh_replaces_the_tip_and_recomputes_available_formats()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        using var vm = f.PageWithPalette();
        var before = Tip(vm, "test-jpg");
        before.EnsureBuilt();
        Assert.NotEmpty(before.FullRows);

        f.Built.Available = false;
        f.Catalog.Refresh();
        Dispatcher.UIThread.RunJobs();
        var after = Tip(vm, "test-jpg");
        Assert.NotSame(before, after);
        Assert.Equal(0, after.BuildCount);
        after.EnsureBuilt();
        var expected = PresetFormatTip.Build(f.Preset("test-jpg"), f.Availability, f.Catalog.CreatePlanner(), f.Loc);
        Assert.Equal(expected.WorksWith, after.WorksWith);
        Assert.Equal(expected.FullRows, after.FullRows);
        Assert.Empty(after.FullRows);
        Assert.DoesNotContain(FormatRegistry.Known, format =>
            f.Availability.Check(f.Preset("test-jpg"), [format]) is null);
        Assert.NotEqual(before.WorksWith, after.WorksWith);
    }

    [AvaloniaFact]
    public void T8_library_and_preset_edits_replace_only_the_affected_tips()
    {
        var extra = new PresetAvailabilityTests.FakeConverter("test-inputs",
            ("png", "xlsx"), ("json", "xlsx"));
        using var f = new PresetAvailabilityTests.Fixture(extra);
        using var vm = f.PageWithPalette();
        var original = Tip(vm, "test-jpg");
        var other = Tip(vm, "tool-crop");
        original.EnsureBuilt();
        var edited = f.Preset("test-jpg");

        edited.TargetFormat = "xlsx";
        f.Store.SaveLibrary();
        Dispatcher.UIThread.RunJobs();
        var external = Tip(vm, edited.Id);
        Assert.NotSame(original, external);
        Assert.NotSame(other, Tip(vm, "tool-crop"));
        external.EnsureBuilt();
        Assert.Equal("XLSX", external.MakesName);
        Assert.Equal(PresetFormatTip.Build(edited, f.Availability, f.Catalog.CreatePlanner(), f.Loc).FullRows,
            external.FullRows);

        var retained = Tip(vm, "tool-crop");
        edited.TargetFormat = "jpg";
        vm.PresetEdited(edited); // Shared by chip and donut-slice editor completion.
        Dispatcher.UIThread.RunJobs();
        var changed = Tip(vm, edited.Id);
        Assert.NotSame(external, changed);
        Assert.Same(retained, Tip(vm, "tool-crop"));
        changed.EnsureBuilt();
        Assert.Equal("JPG", changed.MakesName);
        Assert.Equal(PresetFormatTip.Build(edited, f.Availability, f.Catalog.CreatePlanner(), f.Loc).FullRows,
            changed.FullRows);
    }

    [AvaloniaFact]
    public void T8b_palette_rebuilds_are_lazy_and_profile_only_edits_keep_the_tip()
    {
        using var f = new PresetAvailabilityTests.Fixture();
        using var vm = f.PageWithPalette("png");
        Dispatcher.UIThread.RunJobs();
        var tip = Tip(vm, "test-jpg");
        Assert.Equal(0, tip.BuildCount);

        vm.SelectedTab = vm.Tabs.Single(t => t.Id == PaletteTab.Image);
        vm.SelectedTab = vm.Tabs.Single(t => t.Id == PaletteTab.All);
        Assert.Same(tip, Tip(vm, "test-jpg"));
        Assert.Equal(0, tip.BuildCount);

        tip.EnsureBuilt();
        Assert.Equal(1, tip.BuildCount);
        tip.IsExpanded = true;
        tip.IsExpanded = false;
        tip.EnsureBuilt();
        Assert.Equal(1, tip.BuildCount);

        vm.ProfileName = "Renamed";
        vm.ExtensionTags!.AddCommand.Execute("jpg");
        Dispatcher.UIThread.RunJobs();
        Assert.Same(tip, Tip(vm, "test-jpg"));
        Assert.Equal(1, tip.BuildCount);
    }

    [AvaloniaFact]
    public void T8c_default_target_name_order_ignores_library_edits()
    {
        var defaults = BuiltInData.CreatePresets().Where(p => p.Kind == PresetKind.Convert
            && !p.IsGroup && p.TargetFormat is not BuiltInData.SameAsSource and not FormatRegistry.Folder)
            .Select(p => p.TargetFormat).ToHashSet();
        var spreadsheet = FormatRegistry.Known.Where(format => format.Category == FormatCategory.Spreadsheet).ToList();
        var other = spreadsheet.First(format => !defaults.Contains(format.Id));
        var sources = spreadsheet.Where(format => format.Id == "csv" || format.Id == other.Id
            || format.Id == "ods" || format.Id == "xlsx").DistinctBy(format => format.Id).ToList();
        var extra = new PresetAvailabilityTests.FakeConverter("test-spreadsheet",
            sources.Select(source => new ConversionEdge(source.Id, "xlsx")).ToList());
        using var f = new PresetAvailabilityTests.Fixture(extra);
        using var vm = f.PageWithPalette();
        var original = Tip(vm, "to-xlsx");
        original.EnsureBuilt();

        f.Store.Presets.Remove(f.Preset("to-csv"));
        f.Store.Presets.Add(new Preset { Id = "test-new-spreadsheet", TargetFormat = other.Id });
        f.Store.SaveLibrary();
        Dispatcher.UIThread.RunJobs();

        var changed = Tip(vm, "to-xlsx");
        Assert.NotSame(original, changed);
        changed.EnsureBuilt();
        var accepted = FormatRegistry.Known.Where(format =>
            f.Availability.Check(f.Preset("to-xlsx"), [format]) is null).ToList();
        var expected = accepted.Where(format => format.Category == FormatCategory.Spreadsheet)
            .OrderByDescending(format => defaults.Contains(format.Id)).ToList();
        Assert.Contains(expected, format => format.Id == "csv");
        Assert.Contains(expected, format => format.Id == other.Id);
        Assert.Equal(LocalizationService.LeftToRight(string.Join(f.Loc["toolbar.chip_tip.name_separator"],
            expected.Select(format => FormatLabels.Name(f.Loc, format)))),
            changed.FullRows.Single(row => row.Kind == f.Loc.Format("toolbar.chip_tip.kind",
                FormatLabels.Category(f.Loc, FormatCategory.Spreadsheet), expected.Count)).Names);
        Assert.Equal(original.FullRows, changed.FullRows);
    }
}
