// The Metadata window: every field grouped (Location first, File last), searchable, lists one entry per line; with
// ExifTool, for a kind of file it writes, a value is edited, removed and put back, fields are added, Remove location /
// Remove everything clear many at once, and Save starts a conversion carrying exactly what changed; without it, or for
// a kind of file it doesn't write, every field is read-only and the window says why. The keys, the names a screen
// reader says, the summary of files that failed, the texts in every language. ExifTool is a stand-in here.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Metadata;

namespace Filee.App.Tests;

public sealed class MetadataWindowTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-metadata-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static readonly MetadataField[] Photo =
    [
        new("File", "File", "FileType", "JPEG", false),
        new("File", "System", "FileName", "photo.jpg", false),
        new("EXIF", "IFD0", "Make", "Apple", true),
        new("EXIF", "IFD0", "Model", "iPhone 15 Pro", true),
        new("EXIF", "IFD0", "Orientation", "Rotate 90 CW", true),
        new("EXIF", "IFD0", "Software", "iOS 26.6", true),
        new("EXIF", "ExifIFD", "DateTimeOriginal", "2026:08:12 18:42:10", true),
        new("EXIF", "ExifIFD", "ExifImageWidth", "4032", false),
        new("EXIF", "GPS", "GPSLatitude", "45 deg 30' 6.12\" N", true),
        new("EXIF", "GPS", "GPSLongitude", "73 deg 34' 2.28\" W", true),
        new("XMP", "XMP-dc", "Title", "Old Port at sunset", true),
        new("XMP", "XMP-dc", "Creator", ["Smith, John", "Kim"], true, true),
        new("XMP", "XMP-photoshop", "City", "Montréal", true),
        new("XMP", "XMP-tiff", "Orientation", "Rotate 90 CW", true),
        new("QuickTime", "UserData", "LocationInformation", "Lat=45.5", true),
        new("IPTC", "IPTC", "Keywords", ["harbour", "summer"], true, true),
        new("Composite", "Composite", "GPSPosition", "45 deg …", false),
    ];

    private sealed class FakeExifTool
    {
        public bool Installed { get; set; } = true;

        /// <summary>ExifTool fails on every file; MetadataExtractor may still read it.</summary>
        public bool Fails { get; set; }

        /// <summary>MetadataExtractor doesn't read this kind of file (a PDF, MKV, FLAC).</summary>
        public bool NotForTheReader { get; set; }

        /// <summary>Files neither reader can read.</summary>
        public HashSet<string> Unreadable { get; } = [];

        public MetadataEngines Engines => new()
        {
            Locate = () => Installed ? "/fake/exiftool" : null,
            Read = (_, path) => Fails || Unreadable.Contains(Path.GetFileName(path))
                ? Task.FromException<IReadOnlyList<MetadataField>>(new IOException("bad file"))
                : Task.FromResult<IReadOnlyList<MetadataField>>(Photo),
            ReadWithout = path => NotForTheReader || Unreadable.Contains(Path.GetFileName(path))
                ? throw new IOException("bad file") : [.. Photo.Select(f => f with { Editable = false })],
        };
    }

    private static Preset Tool()
    {
        TestServices.EnsureInitialized("en");
        var tool = AppHost.Get<UserDataStore>().FindPreset(BuiltInData.MetadataId) ?? BuiltInData.MetadataPresets().Single();
        tool.Output.ReplaceOriginal = false;
        return tool;
    }

    private List<string> Files(params string[] names) => [.. names.Select(name =>
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "x");
        return path;
    })];

    private (MetadataViewModel Vm, FakeExifTool ExifTool, List<(string Path, Preset Conversion)> Exported) Create(int files = 1, bool installed = true,
        string extension = "jpg", Action<string, Preset>? export = null)
    {
        var tool = Tool();
        var exifTool = new FakeExifTool { Installed = installed };
        var exported = new List<(string, Preset)>();
        var vm = new MetadataViewModel(Files([.. Enumerable.Range(1, files).Select(i => $"photo{i}.{extension}")]), tool, AppHost.Get<ILocalizer>(),
            AppHost.Get<UserDataStore>(), exifTool.Engines, export ?? ((path, conversion) => exported.Add((path, conversion))));
        Wait(() => !vm.IsBusy && vm.Groups.Count > 0);
        return (vm, exifTool, exported);
    }

    private (MetadataWindow Window, MetadataViewModel Vm, List<(string Path, Preset Conversion)> Exported) Open(int files = 1, bool installed = true,
        string extension = "jpg", Avalonia.Media.FlowDirection direction = Avalonia.Media.FlowDirection.LeftToRight)
    {
        var (vm, _, exported) = Create(files, installed, extension);
        var window = new MetadataWindow { DataContext = vm, Width = 760, Height = 820, FlowDirection = direction };
        window.Show();
        RenderTests.Pump();
        return (window, vm, exported);
    }

    private static void Wait(Func<bool> done, int seconds = 5)
    {
        for (var i = 0; i < seconds * 200 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(done());
    }

    /// <summary>A key pressed and let go, as on a keyboard.</summary>
    private static void Key(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        if (window.IsVisible)
            window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static MetadataRowViewModel Row(MetadataViewModel vm, string key) =>
        vm.Groups.SelectMany(g => g.Rows).Single(r => r.Field.Key == key);

    private static Button ValueButton(Window window, string key) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("value") && b.DataContext is MetadataRowViewModel row
                                                                   && row.Field.Key == key && b.IsEffectivelyVisible);

    private static object? Focused(Window window) => window.FocusManager?.GetFocusedElement();

    [AvaloniaFact]
    public void Fields_are_grouped_with_location_first_and_file_last_and_worked_out_ones_left_out()
    {
        var (vm, _, _) = Create();
        var loc = AppHost.Get<ILocalizer>();

        Assert.Equal(loc["metadata.group.location"], vm.Groups[0].Title);
        Assert.Equal(loc["metadata.location_warning"], vm.Groups[0].Warning);
        Assert.Equal(loc["metadata.group.file"], vm.Groups[^1].Title);
        Assert.DoesNotContain(vm.Groups.SelectMany(g => g.Rows), r => r.Field.Group is "Composite" or "System");
        // IPTC's own fields come as a group of their own, a list one entry per line.
        var keywords = Row(vm, "IPTC:Keywords");
        Assert.Equal((true, "harbour\nsummer"), (keywords.IsList, keywords.Value));
        Assert.Contains(vm.Groups, g => g.Title == loc["metadata.group.keywords"] && g.Origin == "IPTC");
        Assert.Equal("Date Time Original", MetadataViewModel.LabelOf("DateTimeOriginal"));
        Assert.Equal("GPS Latitude", MetadataViewModel.LabelOf("GPSLatitude"));
        // What the file decides shows a lock while the rest can be edited.
        Assert.True(Row(vm, "ExifIFD:ExifImageWidth").ShowsLock);
        Assert.False(Row(vm, "ExifIFD:ExifImageWidth").CanEdit);

        vm.SearchText = "iphone";
        Assert.Equal(["IFD0:Model"], vm.Groups.SelectMany(g => g.Rows).Select(r => r.Field.Key));
        vm.SearchText = "nothing like it";
        Assert.True(vm.IsEmpty);
    }

    [AvaloniaFact]
    public async Task Save_starts_a_conversion_with_exactly_what_changed_and_opens_the_next_file()
    {
        var (vm, _, exported) = Create(files: 2);
        var tool = Tool();
        Assert.Equal("Save & next", vm.SaveText);
        Assert.False(vm.SaveCommand.CanExecute(null));

        var model = Row(vm, "IFD0:Model");
        model.EditCommand.Execute(null);
        model.Value = "iPhone 16";
        model.CommitCommand.Execute(null);
        Row(vm, "IFD0:Software").RemoveCommand.Execute(null);
        // A list keeps its entries whole: "Lee, Ann" is one creator.
        var creator = Row(vm, "XMP-dc:Creator");
        creator.EditCommand.Execute(null);
        creator.Value = "Smith, John\nKim\n\nLee, Ann";
        creator.CommitCommand.Execute(null);
        vm.Groups.Single(g => g.Raw == "XMP-dc").AddCommand.Execute("Rights");
        var rights = vm.Groups.SelectMany(g => g.Rows).Single(r => r.Field.Key == "XMP-dc:Rights");
        rights.Value = "© 2026 Alex Kim";
        rights.CommitCommand.Execute(null);
        Assert.Equal(4, vm.ChangeCount);
        Assert.Equal("4 changes", vm.ChangesText);

        await vm.SaveCommand.ExecuteAsync(null);

        var (path, conversion) = Assert.Single(exported);
        Assert.Equal("photo1.jpg", Path.GetFileName(path));
        Assert.Equal((PresetKind.Convert, BuiltInData.SameAsSource, "{name}_metadata", false),
            (conversion.Kind, conversion.TargetFormat, conversion.Output.FileNamePattern, conversion.Output.ReplaceOriginal));
        var edits = conversion.Metadata!;
        Assert.Equal(["iPhone 16"], edits.Set["IFD0:Model"]);
        Assert.Equal(["Smith, John", "Kim", "Lee, Ann"], edits.Set["XMP-dc:Creator"]);
        Assert.Equal(["© 2026 Alex Kim"], edits.Set["XMP-dc:Rights"]);
        Assert.Equal(["IFD0:Software"], edits.Remove);
        Assert.Equal(3, edits.Set.Count);
        Assert.False(edits.RemoveLocation || edits.RemoveAll);
        Assert.True(tool.IsMetadataTool);
        // The next file, as it is.
        Wait(() => vm.CurrentIndex == 1 && !vm.IsBusy);
        Assert.Equal(0, vm.ChangeCount);
        Assert.Equal("Save copy", vm.SaveText);
    }

    [AvaloniaFact]
    public void Putting_a_field_back_and_removing_it_again_is_Remove_everything_again_and_an_orientation_removed_goes()
    {
        var (vm, _, _) = Create();

        vm.RemoveAllCommand.Execute(null);
        Assert.True(vm.Changes().RemoveAll);
        var make = Row(vm, "IFD0:Make");
        make.UndoCommand.Execute(null);
        Assert.False(vm.Changes().RemoveAll);
        make.RemoveCommand.Execute(null);
        // The screen is as it was, and so is what happens: not field by field, which would leave the thumbnail.
        Assert.True(vm.Changes() is { RemoveAll: true, Remove.Count: 0 });

        Row(vm, "IFD0:Orientation").RemoveCommand.Execute(null);
        Assert.True(vm.Changes() is { RemoveAll: true, Remove: ["IFD0:Orientation"] });
    }

    [AvaloniaFact]
    public void A_file_holding_more_than_its_picture_says_so_and_doesnt_offer_Remove_everything()
    {
        var engines = new MetadataEngines
        {
            Locate = () => "/fake/exiftool",
            Read = (_, _) => Task.FromResult<IReadOnlyList<MetadataField>>(
                [new("EXIF", "GPS", "GPSLatitude", "45", true), new("MPF", "MPF0", "NumberOfImages", "2", false)]),
        };
        var vm = new MetadataViewModel(Files("two.jpg"), Tool(), AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>(), engines, (_, _) => { });
        Wait(() => !vm.IsBusy && vm.Groups.Count > 0);
        var loc = AppHost.Get<ILocalizer>();

        Assert.False(vm.RemoveAllCommand.CanExecute(null));
        Assert.True(vm.RemoveLocationCommand.CanExecute(null));
        Assert.Equal(loc.Format("metadata.embedded", loc["preset.remove_metadata"]), vm.EmbeddedNotice);
        Assert.Contains(loc["preset.remove_metadata"], vm.EmbeddedNotice, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Removing_a_field_just_added_with_the_keyboard_leaves_the_focus_in_the_window()
    {
        var (window, vm, _) = Open();
        try
        {
            window.Activate();
            vm.Groups.Single(g => g.Raw == "XMP-dc").AddCommand.Execute("Rights");
            RenderTests.Pump();
            window.KeyTextInput("x");
            Key(window, Avalonia.Input.Key.Enter);
            RenderTests.Pump();
            var remove = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("remove") && b.DataContext is MetadataRowViewModel { Field.Key: "XMP-dc:Rights" });
            remove.Focus(NavigationMethod.Tab);
            Key(window, Avalonia.Input.Key.Space);
            RenderTests.Pump();

            Assert.Same(window.FindControl<TextBox>("Search"), Focused(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Opening_the_tool_on_other_files_replaces_the_window_that_was_open()
    {
        var tool = Tool();
        var windows = AppHost.Get<WindowService>();
        var first = Files("first.jpg");
        var second = Files("second.jpg");

        windows.ShowMetadataWindow(first, tool);
        var earlier = windows.MetadataWindow!;
        RenderTests.Pump();
        windows.ShowMetadataWindow(second, tool);
        RenderTests.Pump();
        var later = windows.MetadataWindow!;

        Assert.NotSame(earlier, later);
        Assert.False(earlier.IsVisible);
        Assert.True(later.IsVisible);
        later.Close();
        RenderTests.Pump();
        Assert.Null(windows.MetadataWindow);
    }

    [AvaloniaFact]
    public void Remove_everything_is_there_when_every_row_is_locked_and_counts_as_a_change()
    {
        var engines = new MetadataEngines
        {
            Locate = () => "/fake/exiftool",
            Read = (_, _) => Task.FromResult<IReadOnlyList<MetadataField>>([new("PNG", "PNG", "Parameters", "A prompt", false)]),
        };
        var vm = new MetadataViewModel(Files("art.png"), Tool(), AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>(), engines, (_, _) => { });
        Wait(() => !vm.IsBusy && vm.Groups.Count > 0);

        Assert.True(vm.RemoveAllCommand.CanExecute(null));
        vm.RemoveAllCommand.Execute(null);
        Assert.Equal(1, vm.ChangeCount);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.Changes().RemoveAll);
    }

    [AvaloniaFact]
    public void A_value_changed_shows_its_arrow_and_its_cross_side_by_side()
    {
        var (window, vm, _) = Open();
        try
        {
            var row = Row(vm, "IFD0:Make");
            row.Value = "Nikon";
            RenderTests.Pump();
            var buttons = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.IsEffectivelyVisible && b.DataContext == row && !b.Classes.Contains("value")).ToList();

            Assert.Equal(2, buttons.Count);
            Assert.False(buttons[0].Bounds.Intersects(buttons[1].Bounds));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_language_changing_while_a_new_fields_box_is_open_and_empty_leaves_the_window_whole()
    {
        var (window, vm, _) = Open();
        var languages = AppHost.Get<LocalizationService>();
        try
        {
            window.Activate();
            vm.Groups.Single(g => g.Raw == "XMP-dc").AddCommand.Execute("Rights");
            RenderTests.Pump();
            Assert.IsType<TextBox>(Focused(window));

            languages.SetLanguage("de");
            RenderTests.Pump();

            Assert.Equal("Ort", vm.Groups[0].Title);
            Assert.DoesNotContain(vm.Groups.SelectMany(g => g.Rows), r => r is { IsAdded: true, Value.Length: 0, IsEditing: false });
        }
        finally
        {
            languages.SetLanguage("en");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_value_emptied_is_the_field_removed_and_a_list_with_one_entry_is_still_a_list()
    {
        var (vm, _, _) = Create();
        var title = Row(vm, "XMP-dc:Title");
        title.EditCommand.Execute(null);
        title.Value = "";
        title.CommitCommand.Execute(null);
        var keywords = Row(vm, "IPTC:Keywords");
        keywords.EditCommand.Execute(null);
        keywords.Value = "\n\n";
        keywords.CommitCommand.Execute(null);

        var changes = vm.Changes();
        Assert.Equal(["IPTC:Keywords", "XMP-dc:Title"], changes.Remove.Order());
        Assert.Empty(changes.Set);
        Assert.Equal(2, vm.ChangeCount);

        var creator = Row(vm, "XMP-dc:Creator");
        creator.EditCommand.Execute(null);
        creator.Value = "Lee, Ann";
        creator.CommitCommand.Execute(null);
        Assert.Equal(["Lee, Ann"], vm.Changes().Set["XMP-dc:Creator"]);
    }

    [AvaloniaFact]
    public void A_field_added_while_searching_clears_the_search_so_its_box_shows_and_Remove_everything_takes_it()
    {
        var (vm, _, _) = Create();
        vm.SearchText = "old port";
        var filtered = vm.Groups.Single(g => g.Raw == "XMP-dc");
        filtered.AddCommand.Execute("Rights");

        Assert.Equal("", vm.SearchText);
        var rights = Row(vm, "XMP-dc:Rights");
        Assert.True(rights.IsEditing);
        rights.CommitCommand.Execute(null);
        Assert.DoesNotContain(vm.Groups.SelectMany(g => g.Rows), r => r.Field.Key == "XMP-dc:Rights");

        vm.Groups.Single(g => g.Raw == "XMP-dc").AddCommand.Execute("Rights");
        vm.RemoveAllCommand.Execute(null);
        Assert.DoesNotContain(vm.Groups.SelectMany(g => g.Rows), r => r.Field.Key == "XMP-dc:Rights");
    }

    [AvaloniaFact]
    public void A_field_added_and_left_empty_or_removed_again_is_no_field()
    {
        var (vm, _, _) = Create();
        var description = vm.Groups.Single(g => g.Raw == "XMP-dc");
        description.AddCommand.Execute("Description");
        var row = vm.Groups.SelectMany(g => g.Rows).Single(r => r.Field.Key == "XMP-dc:Description");
        Assert.True(row.IsEditing);
        row.CommitCommand.Execute(null);
        Assert.DoesNotContain(vm.Groups.SelectMany(g => g.Rows), r => r.Field.Key == "XMP-dc:Description");
        Assert.Equal(0, vm.ChangeCount);

        vm.Groups.Single(g => g.Raw == "XMP-dc").AddCommand.Execute("Description");
        row = vm.Groups.SelectMany(g => g.Rows).Single(r => r.Field.Key == "XMP-dc:Description");
        row.Value = "Kept";
        row.CommitCommand.Execute(null);
        Assert.Equal(1, vm.ChangeCount);
        Assert.False(row.CanUndo);
        row.RemoveCommand.Execute(null);
        Assert.DoesNotContain(vm.Groups.SelectMany(g => g.Rows), r => r.Field.Key == "XMP-dc:Description");
        Assert.Equal(0, vm.ChangeCount);

        // Taken by Remove everything too: counted once, not as a change of its own.
        vm.Groups.Single(g => g.Raw == "XMP-dc").AddCommand.Execute("Description");
        row = vm.Groups.SelectMany(g => g.Rows).Single(r => r.Field.Key == "XMP-dc:Description");
        row.Value = "Gone";
        row.CommitCommand.Execute(null);
        vm.RemoveAllCommand.Execute(null);
        Assert.DoesNotContain(vm.Groups.SelectMany(g => g.Rows), r => r.Field.Key == "XMP-dc:Description");
        Assert.False(vm.Changes().Set.ContainsKey("XMP-dc:Description"));
    }

    [AvaloniaFact]
    public void Remove_location_takes_the_place_fields_and_putting_one_back_removes_the_others_one_by_one()
    {
        var (vm, _, _) = Create();

        vm.RemoveLocationCommand.Execute(null);

        Assert.True(Row(vm, "GPS:GPSLatitude").IsRemoved);
        Assert.True(Row(vm, "XMP-photoshop:City").IsRemoved);
        Assert.False(Row(vm, "IFD0:Make").IsRemoved);
        // One rule with the writer: all the place fields go, and each counts, not one more for the button.
        Assert.True(Row(vm, "UserData:LocationInformation").IsRemoved);
        Assert.True(vm.Changes() is { RemoveLocation: true, Remove.Count: 0 });
        Assert.Equal(4, vm.ChangeCount);

        Row(vm, "GPS:GPSLatitude").UndoCommand.Execute(null);

        var changes = vm.Changes();
        Assert.False(changes.RemoveLocation);
        Assert.Equal(["GPS:GPSLongitude", "UserData:LocationInformation", "XMP-photoshop:City"], changes.Remove.Order());
    }

    [AvaloniaFact]
    public void Remove_everything_keeps_the_orientation_and_Replace_the_original_is_the_tools_rule_kept_on_disk()
    {
        var (vm, _, exported) = Create();
        var store = AppHost.Get<UserDataStore>();

        vm.RemoveAllCommand.Execute(null);
        Assert.False(Row(vm, "IFD0:Orientation").IsRemoved);
        Assert.True(Row(vm, "IFD0:Make").IsRemoved);
        // Wherever it is kept, it goes back there.
        Assert.False(Row(vm, "XMP-tiff:Orientation").IsRemoved);
        vm.ReplaceOriginal = true;
        Assert.Equal("Save", vm.SaveText);
        vm.SaveCommand.Execute(null);

        var conversion = Assert.Single(exported).Conversion;
        Assert.True(conversion.Metadata!.RemoveAll);
        Assert.Empty(conversion.Metadata.Remove);
        Assert.True(conversion.Output.ReplaceOriginal);
        // Kept in the tool, so the next window starts ticked; and on disk.
        Assert.True(store.FindPreset(BuiltInData.MetadataId)!.Output.ReplaceOriginal);
        var reread = new UserDataStore(store.Directory);
        reread.Load();
        Assert.True(reread.FindPreset(BuiltInData.MetadataId)!.Output.ReplaceOriginal);
        var next = new MetadataViewModel(Files("again.jpg"), store.FindPreset(BuiltInData.MetadataId)!, AppHost.Get<ILocalizer>(), store,
            new FakeExifTool().Engines, (_, _) => { });
        Assert.True(next.ReplaceOriginal);
        next.ReplaceOriginal = false;
        Assert.False(store.FindPreset(BuiltInData.MetadataId)!.Output.ReplaceOriginal);
    }

    [AvaloniaFact]
    public async Task Without_ExifTool_everything_is_read_only_until_it_is_installed()
    {
        var (vm, exifTool, _) = Create(installed: false);
        var installs = 0;
        vm.InstallRequested += () => installs++;

        Assert.False(vm.CanEdit);
        Assert.True(vm.CanInstall);
        Assert.Equal(AppHost.Get<ILocalizer>()["metadata.read_only_banner"], vm.ReadOnlyNotice);
        Assert.All(vm.Groups.SelectMany(g => g.Rows), r => Assert.False(r.CanEdit || r.ShowsLock));
        Assert.All(vm.Groups, g => Assert.False(g.CanAdd));
        Assert.False(vm.RemoveAllCommand.CanExecute(null));
        vm.InstallCommand.Execute(null);
        Assert.Equal(1, installs);

        exifTool.Installed = true;
        await vm.RecheckEngineAsync();

        Assert.True(vm.CanEdit);
        Assert.Equal("", vm.ReadOnlyNotice);
        Assert.True(Row(vm, "IFD0:Make").CanEdit);
    }

    [AvaloniaFact]
    public void A_kind_of_file_ExifTool_doesnt_write_is_read_only_and_says_so_without_offering_ExifTool()
    {
        var (vm, _, _) = Create(extension: "mp3");

        Assert.False(vm.CanEdit);
        Assert.True(vm.IsUnwritable);
        Assert.False(vm.CanInstall);
        Assert.Equal(AppHost.Get<ILocalizer>()["metadata.unwritable"], vm.ReadOnlyNotice);
        Assert.All(vm.Groups.SelectMany(g => g.Rows), r => Assert.False(r.CanEdit));
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [AvaloniaTheory]
    [InlineData("flv")]
    [InlineData("mkv")]
    public void A_file_ExifTool_doesnt_write_is_told_by_its_own_extension(string extension)
    {
        var (vm, _, _) = Create(extension: extension);

        Assert.True(vm.IsUnwritable);
        Assert.False(vm.CanEdit);
    }

    [AvaloniaFact]
    public void Camera_RAW_keeps_Remove_location_but_not_Remove_everything()
    {
        var (vm, _, _) = Create(extension: "cr2");

        Assert.True(vm.CanEdit);
        Assert.True(vm.RemoveLocationCommand.CanExecute(null));
        Assert.False(vm.RemoveAllCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task A_file_only_ExifTool_reads_says_so_and_offers_it_instead_of_failing()
    {
        var tool = Tool();
        var exifTool = new FakeExifTool { Installed = false, NotForTheReader = true };
        var vm = new MetadataViewModel(Files("report.pdf"), tool, AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>(), exifTool.Engines, (_, _) => { });
        Wait(() => !vm.IsBusy);

        Assert.True(vm.NeedsExifTool);
        Assert.True(vm.CanInstall);
        Assert.Equal(AppHost.Get<ILocalizer>()["metadata.needs_exiftool"], vm.ReadOnlyNotice);
        Assert.False(vm.IsFinished);
        Assert.Empty(vm.Problems);
        Assert.False(vm.IsEmpty);

        exifTool.Installed = true;
        await vm.RecheckEngineAsync();
        Assert.False(vm.NeedsExifTool);
        Assert.True(vm.CanEdit);
    }

    [AvaloniaFact]
    public async Task ExifTool_failing_on_a_file_shows_it_read_only_without_offering_ExifTool_or_reading_it_again()
    {
        var tool = Tool();
        var reads = 0;
        var exifTool = new FakeExifTool { Fails = true };
        var engines = exifTool.Engines;
        var vm = new MetadataViewModel(Files("bad.jpg"), tool, AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>(), new MetadataEngines
        {
            Locate = engines.Locate,
            Read = (path, file) =>
            {
                reads++;
                return engines.Read(path, file);
            },
            ReadWithout = engines.ReadWithout,
        }, (_, _) => { });
        Wait(() => !vm.IsBusy && vm.Groups.Count > 0);

        Assert.False(vm.CanEdit);
        Assert.False(vm.CanInstall);
        Assert.Equal(AppHost.Get<ILocalizer>()["metadata.unwritable"], vm.ReadOnlyNotice);
        await vm.RecheckEngineAsync();
        Assert.Equal(1, reads);
    }

    [AvaloniaFact]
    public void Remove_location_is_offered_only_where_there_is_a_place()
    {
        var tool = Tool();
        var engines = new MetadataEngines
        {
            Locate = () => "/fake/exiftool",
            Read = (_, _) => Task.FromResult<IReadOnlyList<MetadataField>>([new("EXIF", "IFD0", "Make", "Apple", true)]),
        };
        var vm = new MetadataViewModel(Files("plain.jpg"), tool, AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>(), engines, (_, _) => { });
        Wait(() => !vm.IsBusy && vm.Groups.Count > 0);

        Assert.False(vm.RemoveLocationCommand.CanExecute(null));
        Assert.True(vm.RemoveAllCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task The_thumbnail_is_the_caches_and_stays_whole_when_the_file_is_read_again_or_the_window_closes()
    {
        var tool = Tool();
        var path = Path.Combine(_folder, "shot.png");
        using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Coral, 32, 32))
            image.Write(path);
        var exifTool = new FakeExifTool { Installed = false };
        var vm = new MetadataViewModel([path], tool, AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>(), exifTool.Engines, (_, _) => { });
        Wait(() => !vm.IsBusy && vm.Thumbnail is not null);
        var shown = vm.Thumbnail!;

        exifTool.Installed = true;
        await vm.RecheckEngineAsync();
        Assert.Same(shown, vm.Thumbnail);
        Assert.True(shown.PixelSize.Width > 0);
        using var copy = new MemoryStream();
        shown.Save(copy);

        vm.Dispose();
        var again = new MetadataViewModel([path], tool, AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>(), exifTool.Engines, (_, _) => { });
        Wait(() => !again.IsBusy && again.Thumbnail is not null);
        using var saved = new MemoryStream();
        again.Thumbnail!.Save(saved);
        Assert.True(saved.Length > 0);
        again.Dispose();
    }

    [AvaloniaTheory]
    [InlineData("jpg", new[] { "XMP-dc", "IPTC" })]
    [InlineData("png", new[] { "XMP-dc" })]
    [InlineData("mp4", new[] { "XMP-dc" })]
    [InlineData("pdf", new[] { "PDF" })]
    public void Add_field_offers_the_places_the_kind_of_file_has(string extension, string[] groups)
    {
        var (vm, _, _) = Create(extension: extension);

        Assert.Equal(groups.Order(), vm.Groups.Where(g => g.CanAdd).Select(g => g.Raw).Order());
        // A list added is a list.
        if (groups.Contains("IPTC"))
            Assert.DoesNotContain("Keywords", vm.Groups.Single(g => g.Raw == "IPTC").Addable);
        if (groups.Contains("XMP-dc"))
        {
            vm.Groups.Single(g => g.Raw == "XMP-dc").AddCommand.Execute("Subject");
            Assert.True(vm.Groups.SelectMany(g => g.Rows).Single(r => r.Field.Key == "XMP-dc:Subject").IsList);
        }
    }

    [AvaloniaFact]
    public void A_file_that_cannot_be_read_is_said_and_the_run_ends_on_a_summary_that_Enter_closes()
    {
        var tool = Tool();
        var exifTool = new FakeExifTool();
        exifTool.Unreadable.Add("photo1.jpg");
        var exported = new List<(string, Preset)>();
        var vm = new MetadataViewModel(Files("photo1.jpg", "photo2.jpg"), tool, AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>(), exifTool.Engines,
            (path, conversion) => exported.Add((path, conversion)));
        var window = new MetadataWindow { DataContext = vm };
        window.Show();
        try
        {
            Wait(() => !vm.IsBusy && vm.CurrentIndex == 1);
            Assert.Equal("photo1.jpg: " + AppHost.Get<ILocalizer>()["metadata.unreadable"], vm.StatusNotice);

            Row(vm, "IFD0:Software").RemoveCommand.Execute(null);
            vm.SaveCommand.Execute(null);

            Wait(() => vm.IsFinished);
            Assert.Equal((1, 1, 0), (vm.FailedCount, vm.DoneCount, vm.SkippedCount));
            Assert.Single(vm.Problems);
            Assert.Single(exported);
            Key(window, Avalonia.Input.Key.Enter);
            Assert.False(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_save_that_cannot_start_keeps_the_file_and_one_that_works_the_second_time_leaves_no_failure()
    {
        var fail = true;
        var exported = new List<string>();
        var (vm, _, _) = Create(export: (path, _) =>
        {
            if (fail)
                throw new InvalidOperationException("queue gone");
            exported.Add(path);
        });
        Row(vm, "IFD0:Software").RemoveCommand.Execute(null);

        vm.SaveCommand.Execute(null);
        Assert.Equal(0, vm.CurrentIndex);
        Assert.False(vm.IsFinished);
        Assert.Single(vm.Problems);
        Assert.True(Row(vm, "IFD0:Software").IsRemoved);

        fail = false;
        vm.SaveCommand.Execute(null);
        Assert.Single(exported);
        Assert.Empty(vm.Problems);
    }

    [AvaloniaFact]
    public void The_values_the_buttons_and_the_search_say_their_names_to_a_screen_reader()
    {
        var (window, vm, _) = Open();
        try
        {
            Assert.Equal("Make: Apple", Avalonia.Automation.AutomationProperties.GetName(ValueButton(window, "IFD0:Make")));
            Assert.Equal("Creator: Smith, John, Kim", Avalonia.Automation.AutomationProperties.GetName(ValueButton(window, "XMP-dc:Creator")));
            var remove = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("remove") && b.DataContext is MetadataRowViewModel { Field.Key: "IFD0:Make" });
            Assert.Equal("Remove Make", Avalonia.Automation.AutomationProperties.GetName(remove));
            Row(vm, "IFD0:Make").RemoveCommand.Execute(null);
            RenderTests.Pump();
            Assert.Equal("Make: Apple, removed", Avalonia.Automation.AutomationProperties.GetName(ValueButton(window, "IFD0:Make")));
            var undo = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.DataContext is MetadataRowViewModel { Field.Key: "IFD0:Make" }
                                                                              && b.Command == Row(vm, "IFD0:Make").UndoCommand);
            Assert.Equal("Put Make back", Avalonia.Automation.AutomationProperties.GetName(undo));
            Assert.Equal(AppHost.Get<ILocalizer>()["metadata.search"], Avalonia.Automation.AutomationProperties.GetName(window.FindControl<TextBox>("Search")!));
            // Read-only values are texts that can be selected, named all the same.
            var width = window.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.DataContext is MetadataRowViewModel { Field.Key: "ExifIFD:ExifImageWidth" });
            Assert.Equal("Exif Image Width: 4032", Avalonia.Automation.AutomationProperties.GetName(width));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Tab_reaches_a_value_Enter_opens_it_Enter_keeps_it_and_the_focus_comes_back_to_it()
    {
        var (window, vm, _) = Open();
        try
        {
            window.Activate();
            var make = ValueButton(window, "IFD0:Make");
            make.Focus(NavigationMethod.Tab);
            Key(window, Avalonia.Input.Key.Enter);
            RenderTests.Pump();
            var row = Row(vm, "IFD0:Make");
            Assert.True(row.IsEditing);
            var editor = Assert.IsType<TextBox>(Focused(window));
            Assert.Same(row, editor.DataContext);

            editor.SelectAll();
            window.KeyTextInput("Nikon");
            Key(window, Avalonia.Input.Key.Enter);
            RenderTests.Pump();

            Assert.False(row.IsEditing);
            Assert.Equal("Nikon", row.Value);
            Assert.Same(ValueButton(window, "IFD0:Make"), Focused(window));
            // Enter on the value again opens it again, it doesn't save.
            Key(window, Avalonia.Input.Key.Enter);
            Assert.True(row.IsEditing);
            Assert.Equal(1, vm.ChangeCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_field_added_takes_the_keyboard_as_its_box_appears()
    {
        var (window, vm, _) = Open();
        try
        {
            window.Activate();
            vm.Groups.Single(g => g.Raw == "XMP-dc").AddCommand.Execute("Rights");
            RenderTests.Pump();

            var box = Assert.IsType<TextBox>(Focused(window));
            window.KeyTextInput("© Kim");
            Key(window, Avalonia.Input.Key.Enter);

            Assert.Equal("© Kim", Row(vm, "XMP-dc:Rights").Value);
            Assert.Equal(1, vm.ChangeCount);
            Assert.NotNull(box);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Save_is_there_for_the_first_change_while_it_is_typed_and_a_click_on_it_saves_it()
    {
        var (window, vm, exported) = Open();
        try
        {
            window.Activate();
            var row = Row(vm, "IFD0:Make");
            row.EditCommand.Execute(null);
            RenderTests.Pump();
            Assert.False(vm.SaveCommand.CanExecute(null));
            var editor = Assert.IsType<TextBox>(Focused(window));
            editor.SelectAll();
            window.KeyTextInput("Nikon");
            Assert.True(vm.SaveCommand.CanExecute(null));

            var save = window.FindControl<Button>("SaveButton")!;
            var at = save.TranslatePoint(new Point(save.Bounds.Width / 2, save.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["Nikon"], Assert.Single(exported).Conversion.Metadata!.Set["IFD0:Make"]);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Esc_puts_the_value_back_and_Shift_Enter_starts_a_new_line_in_a_list()
    {
        var (window, vm, _) = Open();
        try
        {
            window.Activate();
            var row = Row(vm, "XMP-dc:Creator");
            row.EditCommand.Execute(null);
            RenderTests.Pump();
            var editor = Assert.IsType<TextBox>(Focused(window));
            editor.CaretIndex = editor.Text!.Length;
            Key(window, Avalonia.Input.Key.Enter, RawInputModifiers.Shift);
            window.KeyTextInput("Lee, Ann");
            Assert.Equal("Smith, John\nKim\nLee, Ann", editor.Text!.Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.True(row.IsEditing);

            Key(window, Avalonia.Input.Key.Escape);
            RenderTests.Pump();

            Assert.False(row.IsEditing);
            Assert.Equal("Smith, John\nKim", row.Value);
            Assert.True(window.IsVisible);
            Assert.Same(ValueButton(window, "XMP-dc:Creator"), Focused(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Enter_saves_but_not_from_the_search_Esc_clears_the_search_then_closes_and_Ctrl_F_searches()
    {
        var (window, vm, exported) = Open(files: 2);
        try
        {
            window.Activate();
            Key(window, Avalonia.Input.Key.F, RawInputModifiers.Control);
            var search = window.FindControl<TextBox>("Search")!;
            Assert.Same(search, Focused(window));
            window.KeyTextInput("make");
            Assert.Equal("make", vm.SearchText);

            Row(vm, "IFD0:Make").RemoveCommand.Execute(null);
            search.Focus();
            Key(window, Avalonia.Input.Key.Enter);
            Assert.Empty(exported);
            Key(window, Avalonia.Input.Key.Escape);
            Assert.Equal("", vm.SearchText);
            Assert.True(window.IsVisible);

            window.FindControl<Button>("CloseButton")!.Focus(NavigationMethod.Pointer);
            Key(window, Avalonia.Input.Key.Enter);
            Assert.Single(exported);
            Wait(() => vm.CurrentIndex == 1 && !vm.IsBusy);

            Key(window, Avalonia.Input.Key.Escape);
            Assert.False(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Removing_and_putting_back_with_the_keyboard_keeps_the_focus_on_the_row()
    {
        var (window, vm, _) = Open();
        try
        {
            window.Activate();
            var remove = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("remove") && b.DataContext is MetadataRowViewModel { Field.Key: "IFD0:Make" });
            remove.Focus(NavigationMethod.Tab);
            Key(window, Avalonia.Input.Key.Space);
            RenderTests.Pump();
            var row = Row(vm, "IFD0:Make");
            Assert.True(row.IsRemoved);
            Assert.True(Focused(window) is Button undo && undo.Command == row.UndoCommand, $"focus on {Focused(window)}");

            Key(window, Avalonia.Input.Key.Space);
            RenderTests.Pump();
            Assert.False(row.IsRemoved);
            Assert.True(Focused(window) is Button again && again.Classes.Contains("remove") && again.DataContext == row, $"focus on {Focused(window)}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Its_texts_follow_a_language_change()
    {
        var (window, vm, _) = Open(files: 2);
        var languages = AppHost.Get<LocalizationService>();
        try
        {
            Row(vm, "IFD0:Make").RemoveCommand.Execute(null);
            languages.SetLanguage("de");
            RenderTests.Pump();
            var loc = AppHost.Get<ILocalizer>();
            Assert.Equal(loc["metadata.group.location"], vm.Groups[0].Title);
            Assert.Equal("Ort", vm.Groups[0].Title);
            Assert.Equal(loc["metadata.save_next"], vm.SaveText);
            Assert.Equal(loc["metadata.changes_one"], vm.ChangesText);
            Assert.Contains(loc.Format("metadata.fields", 15), vm.Details, StringComparison.Ordinal);
            // What a screen reader says, on the button itself.
            var undo = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.DataContext is MetadataRowViewModel { Field.Key: "IFD0:Make" }
                                                                              && b.Command == Row(vm, "IFD0:Make").UndoCommand);
            Assert.Equal(loc.Format("metadata.undo_named", "Make"), Avalonia.Automation.AutomationProperties.GetName(undo));
            Assert.Equal("Make wiederherstellen", Avalonia.Automation.AutomationProperties.GetName(undo));
            Assert.Equal(loc.Format("crop.page_counter", 1, 2), vm.CounterText);
            // An edit isn't lost with it.
            Assert.True(Row(vm, "IFD0:Make").IsRemoved);
        }
        finally
        {
            languages.SetLanguage("en");
            window.Close();
        }
    }

    public static TheoryData<string> Languages => new(LocalizationService.Languages.Select(l => l.Code));

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public void Every_text_of_the_window_shows_whole_in_every_language(string language)
    {
        var rightToLeft = language == "ar" ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
        foreach (var installed in new[] { true, false })
        {
            var (window, vm, _) = Open(files: 2, installed, direction: rightToLeft);
            var languages = AppHost.Get<LocalizationService>();
            try
            {
                // After opening: the tool's set-up starts from English.
                languages.SetLanguage(language);
                if (installed)
                    Row(vm, "IFD0:Make").RemoveCommand.Execute(null);
                RenderTests.Pump();
                if (language != "en")
                    Assert.NotEqual("Remove everything", AppHost.Get<ILocalizer>()["metadata.remove_all"]);
                var footer = window.GetVisualDescendants().OfType<Border>().First(b => b.IsEffectivelyVisible && DockPanel.GetDock(b) == Dock.Bottom);
                foreach (var text in footer.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
                {
                    var what = $"{language}, {(installed ? "edit" : "read-only")}: '{text.Text}'";
                    Assert.All(text.TextLayout.TextLines, line => Assert.False(line.HasCollapsed, what + " is cut off"));
                    Assert.True(text.TextLayout.Width <= text.Bounds.Width + 1, what + " is wider than its place");
                    var holder = text.GetVisualAncestors().OfType<Control>().FirstOrDefault(c => c is Button or CheckBox) ?? footer;
                    var bounds = new Rect(text.Bounds.Size).TransformToAABB(text.TransformToVisual(holder)!.Value);
                    Assert.True(bounds.Left >= -1 && bounds.Right <= holder.Bounds.Width + 1, what + " runs out of " + holder.GetType().Name);
                    if (holder is Button)
                        Assert.True(text.TextLayout.TextLines.Count == 1, what + " breaks onto another line");
                }
                // Every button and box in the window is inside it.
                foreach (var control in window.GetVisualDescendants().OfType<Control>().Where(c => c is Button or CheckBox or TextBox && c.IsEffectivelyVisible
                             && c.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault()?.Name != "Fields"))
                {
                    var at = new Rect(control.Bounds.Size).TransformToAABB(control.TransformToVisual(window)!.Value);
                    Assert.True(at.Left >= -1 && at.Right <= window.Bounds.Width + 1, $"{language}: {control} runs out of the window");
                }
                RenderTests.Save(window, $"metadata-{(installed ? "edit" : "readonly")}-{language}.png");
            }
            finally
            {
                languages.SetLanguage("en");
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void Values_keep_their_own_order_in_a_right_to_left_UI_and_are_plain_left_to_right()
    {
        var (vm, _, _) = Create();
        var latitude = Row(vm, "GPS:GPSLatitude");
        Assert.Equal(latitude.Value, latitude.ShownValue);

        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("ar");
            Assert.Equal("\u206845 deg 30' 6.12\" N\u2069", latitude.ShownValue);
            Assert.Equal("\u2068Smith, John\u2069\n\u2068Kim\u2069", Row(vm, "XMP-dc:Creator").ShownValue);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = culture;
        }
    }

    [AvaloniaTheory]
    [InlineData(ThemeMode.Light, true)]
    [InlineData(ThemeMode.Dark, false)]
    public void Screenshot(ThemeMode mode, bool installed)
    {
        TestServices.EnsureInitialized("en");
        var theme = AppHost.Get<ThemeService>();
        theme.Apply(new ThemeSettings { Mode = mode }, "en");
        try
        {
            var (vm, _, _) = Create(files: 3, installed);
            if (installed)
            {
                Row(vm, "IFD0:Software").RemoveCommand.Execute(null);
                var title = Row(vm, "XMP-dc:Title");
                title.EditCommand.Execute(null);
                title.Value = "Old Port at sunset, Montréal";
                title.CommitCommand.Execute(null);
            }
            var window = new MetadataWindow { DataContext = vm };
            theme.ApplyWindowClass(window);
            window.Show();
            RenderTests.Pump();
            var warning = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == AppHost.Get<ILocalizer>()["metadata.location_warning"]);
            // The privacy warning stands out from the captions around it.
            Assert.True(window.TryFindResource("WarningBrush", window.ActualThemeVariant, out var brush));
            Assert.Equal(brush, warning.Foreground);
            RenderTests.Save(window, $"metadata-{(installed ? "edit" : "readonly")}-{mode.ToString().ToLowerInvariant()}.png");
            window.Close();
        }
        finally
        {
            theme.Apply(new ThemeSettings { Mode = ThemeMode.Light }, "en");
        }
    }
}
