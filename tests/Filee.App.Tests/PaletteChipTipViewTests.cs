// The format tooltips of the palette chips on the real ToolbarPage (#548): what an open tip shows, Shift showing the
// whole list, keyboard focus opening a tip and a click closing it. The fixture's fake converters (and one chosen per
// test) decide which formats a preset takes, so the counts don't depend on the engines installed; every expected row
// is worked out here from Check, RoutePlanner.Plan, the built-in library and FormatRegistry.Known.

using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views.Pages;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using FakeConverter = Filee.App.Tests.PresetAvailabilityTests.FakeConverter;
using Fixture = Filee.App.Tests.PresetAvailabilityTests.Fixture;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class PaletteChipTipViewTests
{
    // The ToolTip control Avalonia shows for a control's tip (internal to Avalonia).
    private static readonly AttachedProperty<ToolTip?> ToolTipControl = (AttachedProperty<ToolTip?>)typeof(ToolTip)
        .GetField("ToolTipProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static readonly HashSet<string> DefaultTargets = BuiltInData.CreatePresets()
        .Where(p => !p.IsGroup && p.Kind == PresetKind.Convert
            && p.TargetFormat is not BuiltInData.SameAsSource and not FormatRegistry.Folder)
        .Select(p => p.TargetFormat).ToHashSet();

    /// <summary>
    /// Presets whose formats the "test-tips" converter sets: MP3 from audio in one step and from SVG only in three (a
    /// kind the summary leaves out), XLSX from four spreadsheets (a row with names left out), DOCX from two texts (nothing
    /// left out). No target is a source of another, so no route reaches further than these edges say.
    /// </summary>
    private static FakeConverter TipsConverter() => new("test-tips",
        ("wav", "mp3"), ("flac", "mp3"), ("svg", "test-step-a"), ("test-step-a", "test-step-b"), ("test-step-b", "mp3"),
        ("csv", "xlsx"), ("ods", "xlsx"), ("xlsm", "xlsx"), ("xltx", "xlsx"),
        ("md", "docx"), ("txt", "docx"));

    private static void AddTipPresets(Fixture f) => f.Store.Presets.AddRange(
    [
        new() { Id = "test-tip-mp3", TargetFormat = "mp3" },
        new() { Id = "test-tip-xlsx", TargetFormat = "xlsx" },
        new() { Id = "test-tip-docx", TargetFormat = "docx" },
    ]);

    /// <summary>A ToolbarPage for a fixture view model in a window, as the main window shows it.</summary>
    private sealed class View : IDisposable
    {
        public View(ToolbarPageViewModel vm)
        {
            Page = new ToolbarPage { DataContext = vm };
            Window = new Window { Width = 1080, Height = 820, Content = new ScrollViewer { Content = Page } };
            Window.Show();
            RenderTests.Pump();
            // The keyboard is in this window, as when the user hovers its chips: a key goes to the focused element.
            Assert.True(Page.FindControl<TextBox>("PaletteSearchBox")!.Focus());
            RenderTests.Pump();
        }

        public ToolbarPage Page { get; }
        public Window Window { get; }

        public List<Border> Chips => Page.FindControl<Border>("PaletteBox")!.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("chip") && b.DataContext is PaletteChip).ToList();

        public Border Chip(string id) => Chips.Single(b => ((PaletteChip)b.DataContext!).PresetId == id);

        public static ToolTip Open(Border chip)
        {
            ToolTip.SetIsOpen(chip, true);
            RenderTests.Pump();
            Assert.True(ToolTip.GetIsOpen(chip));
            return Assert.IsType<ToolTip>(chip.GetValue(ToolTipControl));
        }

        public static void Close(Control chip)
        {
            ToolTip.SetIsOpen(chip, false);
            RenderTests.Pump();
        }

        public void Key(Key key, bool down, Window? window = null)
        {
            var physical = key == Avalonia.Input.Key.RightShift ? PhysicalKey.ShiftRight : PhysicalKey.ShiftLeft;
            if (down)
                (window ?? Window).KeyPress(key, RawInputModifiers.Shift, physical, null);
            else
                (window ?? Window).KeyRelease(key, RawInputModifiers.None, physical, null);
            RenderTests.Pump();
        }

        public Point Center(Control control) =>
            control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)!.Value;

        public void Dispose() => Window.Close();
    }

    /// <summary>The visible text of an open tip, line by line as the template draws it.</summary>
    private sealed record Shown(string? Reason, string? Title, string? Makes, string? AnyFile, string? WorksWith,
        List<(string Kind, string Names)> Rows, string? Footer, List<string> Lines);

    private static string TextOf(TextBlock block) => block.Inlines is { Count: > 0 } inlines
        ? string.Concat(inlines.OfType<Run>().Select(r => r.Text))
        : block.Text ?? "";

    private static Shown Read(ToolTip tip)
    {
        var blocks = tip.GetVisualDescendants().OfType<TextBlock>().Where(b => b.IsEffectivelyVisible).ToList();
        string? Line(string name) => blocks.SingleOrDefault(b => b.Name == name) is { } b ? TextOf(b) : null;
        var rows = blocks.Where(b => b.Name == "TipKind")
            .Select(kind => (TextOf(kind), TextOf(((Grid)kind.Parent!).Children.OfType<TextBlock>().Single(b => b.Name == "TipNames"))))
            .ToList();
        return new(Line("TipReason"), Line("TipTitle"), Line("TipMakes"), Line("TipAnyFile"), Line("TipWorksWith"), rows,
            Line("TipFooter"), blocks.Select(TextOf).ToList());
    }

    private sealed record Kind(FormatCategory Category, List<FileFormat> Formats, int Steps);

    /// <summary>R1-R4 and R6: the kinds a preset takes, in order, each with its formats in order.</summary>
    private static List<Kind> ExpectedKinds(Fixture f, Preset preset)
    {
        var usable = FormatRegistry.Known.Where(format => f.Availability.Check(preset, [format]) is null).ToList();
        var planner = f.Catalog.CreatePlanner();
        int Steps(FileFormat format)
        {
            if (preset.IsTool)
                return 1;
            var target = preset.TargetFormat == BuiltInData.SameAsSource ? format.Id : preset.TargetFormat;
            return planner.Plan(format.Id, target) is { Steps.Count: > 0 } route ? route.Steps.Count : 1;
        }
        return usable.GroupBy(format => format.Category)
            .Select(g => new Kind(g.Key,
                [.. g.Where(x => DefaultTargets.Contains(x.Id)), .. g.Where(x => !DefaultTargets.Contains(x.Id))],
                g.Min(Steps)))
            .OrderBy(k => k.Steps == 1 ? 0 : 1).ThenByDescending(k => k.Formats.Count).ThenBy(k => (int)k.Category)
            .ToList();
    }

    private static (string Kind, string Names) Row(Fixture f, Kind kind, int shown)
    {
        var names = LocalizationService.LeftToRight(string.Join(f.Loc["toolbar.chip_tip.name_separator"],
            kind.Formats.Take(shown).Select(format => FormatLabels.Name(f.Loc, format))));
        return (f.Loc.Format("toolbar.chip_tip.kind", FormatLabels.Category(f.Loc, kind.Category), kind.Formats.Count),
            shown < kind.Formats.Count ? f.Loc.Format("toolbar.chip_tip.names_more", names, kind.Formats.Count - shown) : names);
    }

    private static List<Kind> SummaryKinds(List<Kind> kinds) => kinds.Where(k => k.Steps < 3).Take(6).ToList();

    private static List<(string, string)> SummaryRows(Fixture f, List<Kind> kinds) =>
        SummaryKinds(kinds).Select(k => Row(f, k, Math.Min(3, k.Formats.Count))).ToList();

    private static List<(string, string)> FullRows(Fixture f, List<Kind> kinds) =>
        kinds.Select(k => Row(f, k, k.Formats.Count)).ToList();

    /// <summary>R5 and R8: the formats no summary row holds, and the last line.</summary>
    private static (int Hidden, string? Footer) ExpectedFooter(Fixture f, List<Kind> kinds)
    {
        var shown = SummaryKinds(kinds);
        var hidden = kinds.Sum(k => k.Formats.Count) - shown.Sum(k => k.Formats.Count);
        return (hidden, hidden > 0 ? f.Loc.Format("toolbar.chip_tip.more_shift", hidden)
            : shown.Any(k => k.Formats.Count > 3) ? f.Loc["toolbar.chip_tip.shift_all"] : null);
    }

    /// <summary>R7: the second line.</summary>
    private static string ExpectedMakes(Fixture f, Preset preset)
    {
        if (preset.TargetFormat == BuiltInData.SameAsSource)
            return f.Loc["toolbar.chip_tip.makes_same"];
        var output = FormatRegistry.Get(preset.TargetFormat);
        var name = LocalizationService.LeftToRight(FormatLabels.Name(f.Loc, output));
        return preset.TargetFormat == FormatRegistry.Folder
            ? f.Loc["toolbar.chip_tip.makes_name"].Replace("{0}", name, StringComparison.Ordinal)
            : f.Loc["toolbar.chip_tip.makes"].Replace("{0}", name, StringComparison.Ordinal)
                .Replace("{1}", FormatLabels.Category(f.Loc, output.Category), StringComparison.Ordinal);
    }

    /// <summary>A whole summary tip and, with Shift held, the whole list, against what the rules give.</summary>
    private static void AssertTip(Fixture f, View view, string id)
    {
        var preset = f.Preset(id);
        var kinds = ExpectedKinds(f, preset);
        var count = FormatRegistry.Known.Count(format => f.Availability.Check(preset, [format]) is null);
        var (hidden, footer) = ExpectedFooter(f, kinds);
        var chip = view.Chip(id);
        var shown = Read(View.Open(chip));

        Assert.Equal(f.Loc.DisplayName(preset), shown.Title);
        Assert.Equal(ExpectedMakes(f, preset), shown.Makes);
        Assert.Equal(f.Loc.Format("toolbar.chip_tip.works_with", count), shown.WorksWith);
        Assert.Equal(SummaryRows(f, kinds), shown.Rows);
        Assert.Equal(count, SummaryKinds(kinds).Sum(k => k.Formats.Count) + hidden);
        Assert.Equal(footer, shown.Footer);
        Assert.Null(shown.Reason);
        Assert.Null(shown.AnyFile);

        view.Key(Key.LeftShift, down: true);
        var whole = Read(chip.GetValue(ToolTipControl)!);
        Assert.Equal(FullRows(f, kinds), whole.Rows);
        Assert.Null(whole.Footer);
        Assert.Equal(shown.Title, whole.Title);
        Assert.Equal(shown.Makes, whole.Makes);
        Assert.Equal(shown.WorksWith, whole.WorksWith);
        view.Key(Key.LeftShift, down: false);
        View.Close(chip);
    }

    [AvaloniaFact]
    public void T2_an_xlsx_chip_tip_shows_what_it_makes_and_the_kinds_it_takes()
    {
        var extra = new FakeConverter("test-sheets",
            ("csv", "xlsx"), ("ods", "xlsx"), ("xlsm", "xlsx"), ("xltx", "xlsx"), ("json", "xlsx"));
        using var f = new Fixture(extra);
        using var vm = f.PageWithPalette();
        using var view = new View(vm);
        var preset = f.Preset("to-xlsx");

        AssertTip(f, view, preset.Id);

        var shown = Read(View.Open(view.Chip(preset.Id)));
        Assert.Contains("XLSX", shown.Makes);
        var spreadsheet = shown.Rows.Single(r => r.Kind.StartsWith(FormatLabels.Category(f.Loc, FormatCategory.Spreadsheet), StringComparison.Ordinal));
        Assert.Contains("CSV", spreadsheet.Names);
        // Every name listed is a format the preset takes.
        var usable = FormatRegistry.Known.Where(format => f.Availability.Check(preset, [format]) is null)
            .Select(format => FormatLabels.Name(f.Loc, format)).ToHashSet();
        var listed = shown.Rows.SelectMany(r => r.Names.Split(f.Loc["toolbar.chip_tip.name_separator"]))
            .Select(name => name.Split(" +")[0]).ToList();
        Assert.NotEmpty(listed);
        Assert.All(listed, name => Assert.Contains(name, usable));
        View.Close(view.Chip(preset.Id));
    }

    [AvaloniaFact]
    public void T2_in_arabic_the_names_and_the_output_format_read_left_to_right()
    {
        var extra = new FakeConverter("test-sheets", ("csv", "xlsx"), ("ods", "xlsx"), ("xlsm", "xlsx"), ("xltx", "xlsx"));
        using var f = new Fixture(extra);
        using var vm = f.PageWithPalette();
        using var view = new View(vm);
        try
        {
            TestServices.EnsureInitialized("ar");
            var preset = f.Preset("to-xlsx");
            var kinds = ExpectedKinds(f, preset);
            var shown = Read(View.Open(view.Chip(preset.Id)));

            var output = "\u200E" + FormatLabels.Name(f.Loc, FormatRegistry.Get("xlsx")) + "\u200E";
            Assert.Contains(output, shown.Makes);
            Assert.Equal(ExpectedMakes(f, preset), shown.Makes);
            Assert.NotEmpty(shown.Rows);
            for (var i = 0; i < shown.Rows.Count; i++)
            {
                var names = string.Join(f.Loc["toolbar.chip_tip.name_separator"],
                    kinds[i].Formats.Take(3).Select(format => FormatLabels.Name(f.Loc, format)));
                Assert.Contains("\u200E" + names + "\u200E", shown.Rows[i].Names);
            }
            Assert.Equal(SummaryRows(f, kinds), shown.Rows);
            View.Close(view.Chip(preset.Id));
        }
        finally
        {
            TestServices.EnsureInitialized("en");
        }
    }

    [AvaloniaFact]
    public void T3_a_kind_reached_only_in_three_steps_is_counted_in_the_summary_and_listed_with_shift()
    {
        using var f = new Fixture(TipsConverter());
        AddTipPresets(f);
        using var vm = f.PageWithPalette();
        using var view = new View(vm);
        var preset = f.Preset("test-tip-mp3");
        Assert.Equal(3, f.Catalog.CreatePlanner().Plan("svg", "mp3")!.Steps.Count);
        Assert.Single(f.Catalog.CreatePlanner().Plan("wav", "mp3")!.Steps);
        var vector = f.Loc.Format("toolbar.chip_tip.kind", FormatLabels.Category(f.Loc, FormatCategory.Vector), 1);
        var kinds = ExpectedKinds(f, preset);
        var (hidden, footer) = ExpectedFooter(f, kinds);
        Assert.Equal(1, hidden);

        var chip = view.Chip(preset.Id);
        var shown = Read(View.Open(chip));
        Assert.DoesNotContain(shown.Rows, r => r.Kind == vector);
        Assert.Contains(shown.Rows, r => r.Kind.StartsWith(FormatLabels.Category(f.Loc, FormatCategory.Audio), StringComparison.Ordinal));
        Assert.Equal(f.Loc.Format("toolbar.chip_tip.more_shift", 1), shown.Footer);
        Assert.Equal(footer, shown.Footer);

        view.Key(Key.LeftShift, down: true);
        var whole = Read(chip.GetValue(ToolTipControl)!);
        Assert.Contains(whole.Rows, r => r.Kind == vector);
        Assert.Equal(FullRows(f, kinds), whole.Rows);
        view.Key(Key.LeftShift, down: false);
        View.Close(chip);
    }

    [AvaloniaFact]
    public void T3_kinds_with_a_one_step_route_come_first_and_the_summary_shows_six()
    {
        // Eight kinds reach ODT in one step with one format each; Text reaches it only in two steps, with two formats.
        var kindsConverter = new FakeConverter("test-kinds",
            ("wav", "odt"), ("mp4", "odt"), ("png", "odt"), ("epub", "odt"), ("zip", "odt"), ("svg", "odt"),
            ("ttf", "odt"), ("rtf", "odt"), ("txt", "test-mid-a"), ("md", "test-mid-a"), ("test-mid-a", "odt"));
        using var f = new Fixture(kindsConverter);
        f.Store.Presets.Add(new() { Id = "test-tip-odt", TargetFormat = "odt" });
        using var vm = f.PageWithPalette();
        using var view = new View(vm);
        var kinds = ExpectedKinds(f, f.Preset("test-tip-odt"));
        Assert.True(kinds.Count(k => k.Steps < 3) > 6);
        var text = Assert.Single(kinds, k => k.Category == FormatCategory.Text);
        Assert.Equal(2, text.Steps);
        Assert.Contains(SummaryKinds(kinds), k => k.Formats.Count < text.Formats.Count);

        AssertTip(f, view, "test-tip-odt");

        var shown = Read(View.Open(view.Chip("test-tip-odt")));
        Assert.Equal(6, shown.Rows.Count);
        Assert.DoesNotContain(shown.Rows, r => r.Kind.StartsWith(FormatLabels.Category(f.Loc, FormatCategory.Text), StringComparison.Ordinal));
        View.Close(view.Chip("test-tip-odt"));
    }

    [AvaloniaFact]
    public void T4_same_format_folder_archive_and_empty_chips_have_their_own_lines()
    {
        var extra = new FakeConverter("test-folders", ("zip", FormatRegistry.Folder), ("7z", FormatRegistry.Folder));
        using var f = new Fixture(extra);
        using var vm = f.PageWithPalette();
        using var view = new View(vm);

        // Same format: "→ Same format", the rest as usual.
        AssertTip(f, view, "half-size");
        Assert.Equal(f.Loc["toolbar.chip_tip.makes_same"], Read(View.Open(view.Chip("half-size"))).Makes);
        View.Close(view.Chip("half-size"));

        // A folder: its name, no kind.
        AssertTip(f, view, "extract");
        var folder = Read(View.Open(view.Chip("extract")));
        Assert.Equal(f.Loc["toolbar.chip_tip.makes_name"].Replace("{0}",
            FormatLabels.Name(f.Loc, FormatRegistry.Get(FormatRegistry.Folder)), StringComparison.Ordinal), folder.Makes);
        View.Close(view.Chip("extract"));

        // Packed into one archive: the two head lines and "Any file", nothing else.
        var pack = f.Preset("test-pack");
        var packed = Read(View.Open(view.Chip(pack.Id)));
        Assert.Equal(f.Loc.DisplayName(pack), packed.Title);
        Assert.Equal(ExpectedMakes(f, pack), packed.Makes);
        Assert.Equal(f.Loc["toolbar.chip_tip.any_file"], packed.AnyFile);
        Assert.Null(packed.WorksWith);
        Assert.Empty(packed.Rows);
        Assert.Null(packed.Footer);
        Assert.Equal([packed.Title!, packed.Makes!, packed.AnyFile!], packed.Lines);
        View.Close(view.Chip(pack.Id));

        // Takes no format at all (an extensionless profile doesn't judge it): the reason, alone.
        var empty = f.Preset("test-wav");
        Assert.Equal(PresetUse.Usable, ((PaletteChip)view.Chip(empty.Id).DataContext!).Use);
        Assert.DoesNotContain(FormatRegistry.Known, format => f.Availability.Check(empty, [format]) is null);
        var reason = Read(View.Open(view.Chip(empty.Id)));
        Assert.Equal([f.Availability.Check(empty, FormatRegistry.Known)!], reason.Lines);
        Assert.Equal(reason.Lines[0], reason.Reason);
        View.Close(view.Chip(empty.Id));
    }

    [AvaloniaFact]
    public void T4b_crop_and_pdf_tools_list_the_formats_they_take()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette();
        using var view = new View(vm);
        AssertTip(f, view, "tool-crop");
        AssertTip(f, view, f.Store.Presets.First(p => p.Id.StartsWith("tool-pdf-", StringComparison.Ordinal)).Id);
    }

    [AvaloniaFact]
    public void T4b_the_clip_tool_lists_the_videos_it_can_cut()
    {
        using var f = new Fixture(new FakeConverter("test-clipper", ("mp4", "mp4"), ("mkv", "mkv")));
        using var vm = f.PageWithPalette();
        using var view = new View(vm);
        var clip = f.Store.Presets.First(p => p.IsClipTool);
        Assert.Contains(FormatRegistry.Known, format => f.Availability.Check(clip, [format]) is null);
        AssertTip(f, view, clip.Id);
    }

    [AvaloniaFact]
    public void T4b_the_sheet_studio_lists_xlsx_with_libreoffice()
    {
        using var f = new Fixture(new FakeConverter("libreoffice", ("xlsx", "pdf")));
        using var vm = f.PageWithPalette();
        using var view = new View(vm);
        Assert.Null(f.Availability.Check(f.Preset("tool-sheet-studio"), [FormatRegistry.Get("xlsx")]));
        AssertTip(f, view, "tool-sheet-studio");
    }

    [AvaloniaFact]
    public void T5_shift_shows_the_whole_list_while_held_and_either_shift_keeps_it()
    {
        using var f = new Fixture(TipsConverter());
        AddTipPresets(f);
        using var vm = f.PageWithPalette();
        using var view = new View(vm);
        var chip = view.Chip("test-tip-mp3");
        var tip = View.Open(chip);
        var summary = Read(tip);
        Assert.NotNull(summary.Footer);

        view.Key(Key.LeftShift, down: true);
        var whole = Read(tip);
        Assert.True(whole.Rows.Count > summary.Rows.Count);
        Assert.Null(whole.Footer);
        view.Key(Key.LeftShift, down: false);
        Assert.Equal(summary.Lines, Read(tip).Lines);

        // Both Shift keys, one let go: still whole. Both let go: short again.
        view.Key(Key.LeftShift, down: true);
        view.Key(Key.RightShift, down: true);
        view.Key(Key.LeftShift, down: false);
        Assert.Equal(whole.Lines, Read(tip).Lines);
        view.Key(Key.RightShift, down: false);
        Assert.Equal(summary.Lines, Read(tip).Lines);
        view.Key(Key.RightShift, down: true);
        view.Key(Key.LeftShift, down: true);
        view.Key(Key.RightShift, down: false);
        Assert.Equal(whole.Lines, Read(tip).Lines);
        view.Key(Key.LeftShift, down: false);
        Assert.Equal(summary.Lines, Read(tip).Lines);
        View.Close(chip);
    }

    [AvaloniaFact]
    public void T5_names_left_out_of_a_row_show_with_shift()
    {
        using var f = new Fixture(TipsConverter());
        AddTipPresets(f);
        using var vm = f.PageWithPalette();
        using var view = new View(vm);
        var preset = f.Preset("test-tip-xlsx");
        var kinds = ExpectedKinds(f, preset);
        Assert.Equal(0, ExpectedFooter(f, kinds).Hidden);
        var chip = view.Chip(preset.Id);
        var tip = View.Open(chip);
        var summary = Read(tip);
        Assert.Equal(f.Loc["toolbar.chip_tip.shift_all"], summary.Footer);

        view.Key(Key.RightShift, down: true);
        var whole = Read(tip);
        Assert.Equal(summary.Rows.Count, whole.Rows.Count);
        Assert.Equal(FullRows(f, kinds), whole.Rows);
        Assert.NotEqual(summary.Rows, whole.Rows);
        Assert.Null(whole.Footer);
        view.Key(Key.RightShift, down: false);
        Assert.Equal(summary.Lines, Read(tip).Lines);
        View.Close(chip);
    }

    [AvaloniaFact]
    public void T5_a_tip_with_nothing_left_out_has_no_last_line_and_ignores_shift()
    {
        using var f = new Fixture(TipsConverter());
        AddTipPresets(f);
        using var vm = f.PageWithPalette();
        using var view = new View(vm);
        var chip = view.Chip("test-tip-docx");
        var tip = View.Open(chip);
        var before = Read(tip);
        Assert.Null(before.Footer);
        Assert.NotEmpty(before.Rows);

        view.Key(Key.LeftShift, down: true);
        Assert.Equal(before.Lines, Read(tip).Lines);
        view.Key(Key.LeftShift, down: false);
        Assert.Equal(before.Lines, Read(tip).Lines);
        View.Close(chip);
    }

    [AvaloniaFact]
    public void T5_a_tip_opens_short_unless_shift_is_held_when_it_opens()
    {
        using var f = new Fixture(TipsConverter());
        AddTipPresets(f);
        using var vm = f.PageWithPalette();
        using var view = new View(vm);
        var chip = view.Chip("test-tip-mp3");
        var summary = Read(View.Open(chip)).Lines;
        view.Key(Key.LeftShift, down: true);
        var whole = Read(chip.GetValue(ToolTipControl)!).Lines;
        Assert.NotEqual(summary, whole);

        // Closed whole, Shift let go, opened again: short.
        View.Close(chip);
        view.Key(Key.LeftShift, down: false);
        Assert.Equal(summary, Read(View.Open(chip)).Lines);
        View.Close(chip);

        // Opened while Shift is held: whole.
        view.Key(Key.LeftShift, down: true);
        Assert.Equal(whole, Read(View.Open(chip)).Lines);
        view.Key(Key.LeftShift, down: false);
        View.Close(chip);
    }

    [AvaloniaFact]
    public void T5_shift_let_go_in_another_window_does_not_stick()
    {
        using var f = new Fixture(TipsConverter());
        AddTipPresets(f);
        using var vm = f.PageWithPalette();
        using var view = new View(vm);
        var chip = view.Chip("test-tip-mp3");
        var summary = Read(View.Open(chip)).Lines;
        view.Key(Key.LeftShift, down: true);
        Assert.NotEqual(summary, Read(chip.GetValue(ToolTipControl)!).Lines);

        var other = new Window { Width = 300, Height = 200 };
        other.Show();
        try
        {
            other.Activate();
            RenderTests.Pump();
            view.Key(Key.LeftShift, down: false, other);
            // The page's window never heard Shift go up.
            Assert.True(((PresetFormatTip)ToolTip.GetTip(chip)!).IsExpanded);
            view.Window.Activate();
            RenderTests.Pump();
            View.Close(chip);
            Assert.Equal(summary, Read(View.Open(chip)).Lines);
            View.Close(chip);
        }
        finally
        {
            other.Close();
        }
    }

    [AvaloniaFact]
    public void T5_a_dimmed_chip_keeps_its_reason_with_shift()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png", "mp4");
        using var view = new View(vm);
        var dimmed = view.Chip("test-mp4");
        var reason = ((PaletteChip)dimmed.DataContext!).Reason;
        Assert.NotNull(reason);
        var tip = View.Open(dimmed);
        Assert.Equal(reason, tip.Content);

        view.Key(Key.LeftShift, down: true);
        view.Key(Key.RightShift, down: true);
        Assert.True(ToolTip.GetIsOpen(dimmed));
        Assert.Equal(reason, tip.Content);
        Assert.Equal(reason, ToolTip.GetTip(dimmed));
        view.Key(Key.LeftShift, down: false);
        view.Key(Key.RightShift, down: false);
        Assert.True(ToolTip.GetIsOpen(dimmed));
        Assert.Equal(reason, tip.Content);
        View.Close(dimmed);
    }

    /// <summary>Two chips next to each other in the palette, both with a tip.</summary>
    private static (Border First, Border Next) TippedPair(View view)
    {
        var chips = view.Chips;
        for (var i = 0; i + 1 < chips.Count; i++)
            if (ToolTip.GetTip(chips[i]) is PresetFormatTip && ToolTip.GetTip(chips[i + 1]) is PresetFormatTip)
                return (chips[i], chips[i + 1]);
        throw new InvalidOperationException("no two chips with a tip next to each other");
    }

    [AvaloniaFact]
    public void T7_pressing_dragging_or_double_clicking_a_chip_closes_its_tip()
    {
        using var f = new Fixture(TipsConverter());
        AddTipPresets(f);
        using var vm = f.PageWithPalette();
        using var view = new View(vm);
        var (first, next) = TippedPair(view);

        // Press on a chip whose tip is showing.
        var at = view.Center(first);
        view.Window.MouseMove(at);
        View.Open(first);
        view.Window.MouseDown(at, MouseButton.Left);
        Assert.False(ToolTip.GetIsOpen(first));
        view.Window.MouseUp(at, MouseButton.Left);
        RenderTests.Pump();
        Assert.False(ToolTip.GetIsOpen(first));

        // Press and drag past the threshold: closed while dragging and after letting go. The tip opens at the pointer, so
        // it shows below the chip, not over it.
        var from = view.Center(next);
        var away = new Point(from.X - 40, from.Y + 40);
        view.Window.MouseMove(from);
        View.Open(next);
        view.Window.MouseDown(from, MouseButton.Left);
        view.Window.MouseMove(away);
        RenderTests.Pump();
        Assert.True(view.Page.FindControl<Border>("Ghost")!.IsVisible);
        Assert.False(ToolTip.GetIsOpen(next));
        view.Window.MouseUp(away, MouseButton.Left);
        RenderTests.Pump();
        Assert.False(ToolTip.GetIsOpen(next));

        var chip = view.Chip("test-tip-docx");
        View.Open(chip);
        chip.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));
        Assert.False(ToolTip.GetIsOpen(chip));
    }

    [AvaloniaFact]
    public void T7_a_hovered_tip_closes_when_the_pointer_leaves_the_chip()
    {
        using var f = new Fixture(TipsConverter());
        AddTipPresets(f);
        using var vm = f.PageWithPalette();
        using var view = new View(vm);
        // A chip, and one on a row above it: the tip opens below the pointer, so it doesn't cover the one above.
        var tipped = view.Chips.Where(c => ToolTip.GetTip(c) is PresetFormatTip).ToList();
        var above = tipped[0];
        var below = tipped.First(c => view.Center(c).Y > view.Center(above).Y + above.Bounds.Height);
        Assert.True(view.Center(below).Y < view.Window.Height);

        // The pointer rests on the lower chip until its tip shows (the show delay is not waited for here).
        view.Window.MouseMove(view.Center(below));
        View.Open(below);
        // On to the chip above: the tooltip service moves the tip there at once, as between any two tips.
        view.Window.MouseMove(view.Center(above));
        RenderTests.Pump();
        Assert.False(ToolTip.GetIsOpen(below));
        Assert.True(ToolTip.GetIsOpen(above));
        Assert.Equal(1, ((PresetFormatTip)ToolTip.GetTip(above)!).BuildCount);

        // Off the chips, onto the page title: closed.
        var title = view.Page.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("h1"));
        view.Window.MouseMove(view.Center(title));
        RenderTests.Pump();
        Assert.False(ToolTip.GetIsOpen(above));
    }

    [AvaloniaFact]
    public void T7_a_tip_closes_when_the_pointer_moves_onto_it()
    {
        using var f = new Fixture(TipsConverter());
        AddTipPresets(f);
        using var vm = f.PageWithPalette();
        using var view = new View(vm);
        var chip = view.Chip("test-tip-xlsx");
        var at = chip.TranslatePoint(new Point(chip.Bounds.Width / 2, 5), view.Window)!.Value;
        view.Window.MouseMove(at);
        var tip = View.Open(chip);
        var origin = tip.TranslatePoint(default, view.Window)!.Value;

        // Down from the chip onto the tip, which Avalonia would otherwise keep open over the next rows.
        view.Window.MouseMove(new Point(origin.X + tip.Bounds.Width / 2, origin.Y + tip.Bounds.Height / 2));
        RenderTests.Pump();
        Assert.False(ToolTip.GetIsOpen(chip));
    }

    [AvaloniaFact]
    public void T9_a_group_chip_has_no_tip_by_hover_or_focus()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette();
        using var view = new View(vm);
        var group = view.Chip("test-group");
        Assert.Null(ToolTip.GetTip(group));
        Assert.True(group.Focus(NavigationMethod.Tab));
        RenderTests.Pump();
        Assert.False(ToolTip.GetIsOpen(group));
        view.Window.MouseMove(view.Center(group));
        RenderTests.Pump();
        Assert.False(ToolTip.GetIsOpen(group));
    }

    [AvaloniaFact]
    public void T10_nothing_in_an_open_tip_takes_focus_or_input()
    {
        using var f = new Fixture(TipsConverter());
        AddTipPresets(f);
        using var vm = f.PageWithPalette();
        using var view = new View(vm);
        var chip = view.Chip("test-tip-mp3");
        var tip = View.Open(chip);

        void AssertReadOnly()
        {
            var inside = tip.GetVisualDescendants().OfType<InputElement>().ToList();
            Assert.NotEmpty(inside.OfType<TextBlock>());
            Assert.DoesNotContain(inside, e => e.Focusable);
            Assert.DoesNotContain(inside, e => e is Button or TextBox);
        }

        AssertReadOnly();
        view.Key(Key.LeftShift, down: true);
        Assert.True(((PresetFormatTip)ToolTip.GetTip(chip)!).IsExpanded);
        AssertReadOnly();
        view.Key(Key.LeftShift, down: false);
        View.Close(chip);
    }

    [AvaloniaFact]
    public void T11_chips_keep_the_default_tooltip_delays()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png", "mp4");
        using var view = new View(vm);
        Assert.NotEmpty(view.Chips);
        Assert.All(view.Chips, chip =>
        {
            Assert.False(chip.IsSet(ToolTip.ShowDelayProperty));
            Assert.False(chip.IsSet(ToolTip.BetweenShowDelayProperty));
        });
    }

    [AvaloniaFact]
    public void T12_only_a_format_tip_is_a_card()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png", "mp4");
        using var view = new View(vm);
        Assert.True(view.Page.TryFindResource("SurfaceBrush", view.Page.ActualThemeVariant, out var surface));

        var formats = view.Chip("test-jpg");
        Assert.IsType<PresetFormatTip>(ToolTip.GetTip(formats));
        var card = View.Open(formats);
        Assert.Contains("formats", card.Classes);
        Assert.Same(surface, card.Background);
        View.Close(formats);
        card = View.Open(formats);
        Assert.Contains("formats", card.Classes);
        Assert.Same(surface, card.Background);
        View.Close(formats);

        var dimmed = view.Chip("test-mp4");
        var reason = View.Open(dimmed);
        Assert.IsType<string>(reason.Content);
        Assert.DoesNotContain("formats", reason.Classes);
        Assert.NotSame(surface, reason.Background);
        View.Close(dimmed);

        var badge = view.Page.GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("badge") && ToolTip.GetTip(b) is not null);
        var count = View.Open(badge);
        Assert.DoesNotContain("formats", count.Classes);
        Assert.NotSame(surface, count.Background);
        View.Close(badge);
    }
}
