// "Choose pages" (#981): the real window opened from the donut for a preset whose pages are set to "Ask every time",
// the pages and one PDF / one file per page going into the conversion, Esc starting nothing; and the window itself in
// light and dark.

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
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class AskPagesTests
{
    private static string Pdf(string folder, string name, int pages)
    {
        var path = Path.Combine(folder, name);
        using var document = new PdfDocument();
        for (var i = 0; i < pages; i++)
        {
            var page = document.AddPage();
            // The width tells the pages apart in the result.
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(100 + i);
        }
        document.Save(path);
        return path;
    }

    private static async Task<AskPagesWindow> WaitForWindow(WindowService windows, AskPagesWindow? previous = null)
    {
        for (var i = 0; i < 300; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (windows.AskPages is { } open && open != previous && open.IsVisible)
            {
                RenderTests.Pump();
                return open;
            }
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("The pages window didn't open");
    }

    private static async Task<List<JobViewModel>> WaitForJobs(ConversionService conversions, HashSet<string> before, int count)
    {
        for (var i = 0; i < 300; i++)
        {
            Dispatcher.UIThread.RunJobs();
            var started = conversions.Jobs.Where(j => !before.Contains(j.Job.Id)).ToList();
            if (started.Count >= count && started.All(j => j.Job.State is JobState.Completed or JobState.Failed))
                return started;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("The conversions didn't finish");
    }

    private static void Type(Window window, string text)
    {
        var box = window.FindControl<TextBox>("RangeBox")!;
        box.Focus();
        box.SelectAll();
        window.KeyTextInput(text);
        RenderTests.Pump();
    }

    private static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        RenderTests.Pump();
    }

    private static void Click(Window window, string name)
    {
        var control = window.FindControl<Control>(name)!;
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        RenderTests.Pump();
    }

    /// <summary>
    /// Split PDF with "Ask every time": the window shows the page count, takes the range typed and one PDF, and Enter
    /// starts the split of those pages; Esc starts nothing. The choice of one PDF is kept for the next time.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dropping_a_pdf_asks_for_the_pages_then_splits_them_or_esc_starts_nothing(bool cancel)
    {
        TestServices.EnsureInitialized();
        var folder = Directory.CreateTempSubdirectory("filee-ask-pages-").FullName;
        var store = AppHost.Get<UserDataStore>();
        var profile = store.Profiles.Single(p => p.Id == "pdf");
        var previous = profile.PresetIds;
        var preset = store.FindPreset("pdf-split")!;
        var (previousOutput, previousPdf) = (preset.Output, preset.Pdf.Clone());
        var conversions = AppHost.Get<ConversionService>();
        var before = conversions.Jobs.Select(j => j.Job.Id).ToHashSet();
        var radial = AppHost.Get<RadialController>();
        var windows = AppHost.Get<WindowService>();
        try
        {
            var book = Pdf(folder, "book.pdf", 12);
            profile.PresetIds = ["pdf-split"];
            preset.Pdf.AskPageRange = true;
            preset.Pdf.SplitPages = true;
            preset.Output = new() { Location = OutputLocation.CustomFolder, CustomFolder = folder };
            radial.ShowForFiles(450, 400, [book]);
            var donut = radial.Window!.DonutControl;
            Assert.True(donut.HandleKey(Key.Right));
            Assert.True(donut.HandleKey(Key.Enter));

            var dialog = await WaitForWindow(windows);
            var vm = Assert.IsType<AskPagesViewModel>(dialog.DataContext);
            Assert.False(radial.IsVisible);
            Assert.DoesNotContain(conversions.Jobs, j => !before.Contains(j.Job.Id));
            Assert.Equal("book.pdf", dialog.FindControl<TextBlock>("FileName")!.Text);
            Assert.Equal("Pages in this PDF: 12", dialog.FindControl<TextBlock>("PageCount")!.Text);
            Assert.Equal("Split PDF", dialog.StartPages.Content);
            Assert.True(dialog.FindControl<RadioButton>("PerPage")!.IsChecked);
            Assert.False(dialog.FindControl<TextBlock>("FileOfText")!.IsVisible);

            // Past the end: said, and nothing starts.
            Type(dialog, "10-14");
            Assert.Equal("This PDF ends at page 12.", dialog.FindControl<TextBlock>("RangeStatus")!.Text);
            Assert.False(dialog.StartPages.IsEffectivelyEnabled);
            Press(dialog, Key.Enter);
            Assert.True(dialog.IsVisible);

            Type(dialog, "3-5, 9");
            Assert.Equal("Pages chosen: 4 of 12", dialog.FindControl<TextBlock>("RangeStatus")!.Text);
            Assert.True(dialog.StartPages.IsEffectivelyEnabled);
            Click(dialog, "OnePdf");
            Assert.False(vm.SplitPages);
            dialog.FindControl<TextBox>("RangeBox")!.Focus();
            Press(dialog, cancel ? Key.Escape : Key.Enter);
            Assert.False(dialog.IsVisible);

            if (cancel)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
                Assert.DoesNotContain(conversions.Jobs, j => !before.Contains(j.Job.Id));
                Assert.Equal([book], Directory.EnumerateFiles(folder, "*.pdf"));
                Assert.True(store.FindPreset("pdf-split")!.Pdf.SplitPages);
                return;
            }

            var job = Assert.Single(await WaitForJobs(conversions, before, 1));
            Assert.Equal(JobState.Completed, job.Job.State);
            using var extract = PdfReader.Open(Assert.Single(job.Job.Outputs), PdfDocumentOpenMode.Import);
            Assert.Equal([102d, 103d, 104d, 108d], extract.Pages.Cast<PdfPage>().Select(p => p.Width.Point));
            // The saved preset still asks, with its own range; one PDF is what the window starts with next time.
            Assert.Equal(("", true, false), (preset.Pdf.PageRange, preset.Pdf.AskPageRange, preset.Pdf.SplitPages));
        }
        finally
        {
            windows.AskPages?.Close();
            radial.Close(animate: false);
            profile.PresetIds = previous;
            preset.Output = previousOutput;
            preset.Pdf = previousPdf;
            store.SaveLibrary();
            Directory.Delete(folder, true);
        }
    }

    /// <summary>
    /// Two PDFs dropped together: a window for each, in turn, with its own page count, so each gives its own chapter.
    /// </summary>
    [AvaloniaFact]
    public async Task Each_pdf_dropped_gets_its_own_window_and_its_own_pages()
    {
        TestServices.EnsureInitialized();
        var folder = Directory.CreateTempSubdirectory("filee-ask-pages-two-").FullName;
        var store = AppHost.Get<UserDataStore>();
        var profile = store.Profiles.Single(p => p.Id == "pdf");
        var previous = profile.PresetIds;
        var preset = new Preset
        {
            Id = "test-ask-pages",
            Name = "Chapters",
            TargetFormat = "pdf",
            Pdf = { AskPageRange = true, PageRange = "1" },
            Output = { Location = OutputLocation.CustomFolder, CustomFolder = folder, FileNamePattern = "{name}_part" },
        };
        store.Presets.Add(preset);
        var conversions = AppHost.Get<ConversionService>();
        var before = conversions.Jobs.Select(j => j.Job.Id).ToHashSet();
        var radial = AppHost.Get<RadialController>();
        var windows = AppHost.Get<WindowService>();
        try
        {
            var book = Pdf(folder, "a book.pdf", 60);
            var framework = Pdf(folder, "b framework.pdf", 30);
            profile.PresetIds = [preset.Id];
            radial.ShowForFiles(450, 400, [book, framework]);
            var donut = radial.Window!.DonutControl;
            Assert.True(donut.HandleKey(Key.Right));
            Assert.True(donut.HandleKey(Key.Enter));

            var first = await WaitForWindow(windows);
            Assert.Equal("File 1 of 2", first.FindControl<TextBlock>("FileOfText")!.Text);
            Assert.Equal("Pages in this PDF: 60", first.FindControl<TextBlock>("PageCount")!.Text);
            // It starts with the preset's range, selected so typing replaces it.
            Assert.Equal("1", first.FindControl<TextBox>("RangeBox")!.Text);
            Type(first, "13-55");
            Assert.True(first.FindControl<RadioButton>("OnePdf")!.IsChecked);
            Press(first, Key.Enter);

            var second = await WaitForWindow(windows, first);
            Assert.Equal("File 2 of 2", second.FindControl<TextBlock>("FileOfText")!.Text);
            Assert.Equal("Pages in this PDF: 30", second.FindControl<TextBlock>("PageCount")!.Text);
            Type(second, "15-23");
            Click(second, "StartPages");

            var jobs = await WaitForJobs(conversions, before, 2);
            Assert.All(jobs, job => Assert.Equal(JobState.Completed, job.Job.State));
            int Pages(string name)
            {
                using var document = PdfReader.Open(Path.Combine(folder, name), PdfDocumentOpenMode.Import);
                return document.PageCount;
            }
            Assert.Equal(43, Pages("a book_part.pdf"));
            Assert.Equal(9, Pages("b framework_part.pdf"));
        }
        finally
        {
            windows.AskPages?.Close();
            radial.Close(animate: false);
            profile.PresetIds = previous;
            store.Presets.Remove(preset);
            Directory.Delete(folder, true);
        }
    }

    [AvaloniaFact]
    public void The_window_checks_the_range_as_it_is_typed()
    {
        TestServices.EnsureInitialized();
        var loc = AppHost.Get<ILocalizer>();
        var split = BuiltInData.CreatePresets().Single(p => p.Id == "pdf-split");
        var vm = new AskPagesViewModel(@"C:\Books\Book.pdf", 120, split, loc);
        Assert.Equal("Pages in this PDF: 120", vm.PageCountText);
        Assert.Equal("Pages chosen: 120 of 120", vm.Status);
        Assert.Equal(new PageChoice("", true), vm.Result());
        Assert.False(vm.ShowsPosition);

        vm.Range = "13\u201355"; // an en dash
        Assert.Equal("Pages chosen: 43 of 120", vm.Status);
        vm.OnePdf = true;
        Assert.Equal(new PageChoice("13-55", false), vm.Result());
        vm.Range = "13-x";
        Assert.False(vm.IsValid);
        Assert.Equal("Type page numbers from 1, like 13-55 or 1-3, 8, 12-15.", vm.Status);
        Assert.Null(vm.Result());

        // Pages that couldn't be counted: said, and the range is only checked for its form.
        var unknown = new AskPagesViewModel("locked.pdf", null, split, loc, 1, 3);
        Assert.Equal("Filee couldn't count the pages of this PDF.", unknown.PageCountText);
        Assert.Equal("File 2 of 3", unknown.Position);
        unknown.Range = "400-410";
        Assert.True(unknown.IsValid);
        Assert.Equal("", unknown.Status);

        // PDF → PNG: no one PDF / one file per page, whatever the preset has.
        var png = new AskPagesViewModel("a.pdf", 5, new Preset { TargetFormat = "png", Pdf = { SplitPages = true } }, loc);
        Assert.False(png.OffersSplit);
        Assert.Equal(new PageChoice("", false), png.Result());
    }

    /// <summary>A PDF that can't be read gives no count, not an error: the window opens anyway.</summary>
    [AvaloniaFact]
    public void A_pdf_that_cant_be_read_has_no_count()
    {
        TestServices.EnsureInitialized();
        var folder = Directory.CreateTempSubdirectory("filee-ask-pages-bad-").FullName;
        try
        {
            var bad = Path.Combine(folder, "broken.pdf");
            File.WriteAllText(bad, "not a pdf");
            var windows = AppHost.Get<WindowService>();
            Assert.Null(windows.CountPages(bad));
            Assert.Null(windows.CountPages(Path.Combine(folder, "gone.pdf")));
            Assert.Equal(7, windows.CountPages(Pdf(folder, "ok.pdf", 7)));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    public static TheoryData<string, bool> Looks => new()
    {
        { "en", false }, { "en", true }, { "ko", false }, { "ko", true },
        { "de", false }, { "ar", true }, { "ru", false }, { "hi", true }, { "vi", false }, { "ja", true },
    };

    /// <summary>
    /// The window in light and dark, long languages and right to left: the file, its pages, the range and its status,
    /// the switch and the buttons all inside it, the switch in one piece. Also with the range not valid.
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(Looks))]
    public void The_window_fits_in_light_and_dark(string language, bool dark)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        theme.Apply(new ThemeSettings { Mode = dark ? ThemeMode.Dark : ThemeMode.Light }, language);
        var loc = AppHost.Get<ILocalizer>();
        var split = BuiltInData.CreatePresets().Single(p => p.Id == "pdf-split");
        var vm = new AskPagesViewModel(@"C:\Users\me\Documents\Software Engineering, 10th edition (Sommerville) - full book.pdf",
            812, split, loc, 0, 2)
        { Range = "13-55", OnePdf = true };
        var window = new AskPagesWindow { DataContext = vm };
        window.StartPages.Content = loc.DisplayName(split);
        try
        {
            window.Show();
            RenderTests.Pump();
            Rect Box(string name)
            {
                var control = window.FindControl<Control>(name)!;
                return new Rect(control.Bounds.Size).TransformToAABB(control.TransformToVisual(window)!.Value);
            }
            var bounds = new Rect(window.Bounds.Size);
            foreach (var name in new[] { "FileName", "PageCount", "RangeBox", "RangeStatus", "SplitChoice", "OnePdf", "PerPage", "StartPages", "CancelPages" })
            {
                var box = Box(name);
                Assert.True(box.Width > 0 && bounds.Contains(box), $"{name} {box} outside {bounds}");
            }
            Assert.False(Box("OnePdf").Deflate(1).Intersects(Box("PerPage").Deflate(1)));
            Assert.False(Box("StartPages").Deflate(1).Intersects(Box("CancelPages").Deflate(1)));
            // Arabic mirrors the switch and the buttons.
            if (language == "ar")
                Assert.True(Box("OnePdf").Left > Box("PerPage").Left && Box("StartPages").Left < Box("CancelPages").Left);
            else
                Assert.True(Box("OnePdf").Right <= Box("PerPage").Left + 0.5 && Box("StartPages").Left >= Box("CancelPages").Right);
            // The range opens selected; as typed, the caret at its end.
            var range = window.FindControl<TextBox>("RangeBox")!;
            Assert.Equal((0, 5), (range.SelectionStart, range.SelectionEnd));
            void CaretAtEnd()
            {
                range.SelectionStart = range.SelectionEnd = range.Text!.Length;
                RenderTests.Pump();
            }
            CaretAtEnd();
            var look = dark ? "dark" : "light";
            RenderTests.Save(window, $"ask-pages-{language}-{look}.png");

            vm.Range = "800-830";
            CaretAtEnd();
            Assert.Contains("danger", window.FindControl<TextBlock>("RangeStatus")!.Classes);
            Assert.True(bounds.Contains(Box("StartPages")));
            RenderTests.Save(window, $"ask-pages-{language}-{look}-past-the-end.png");
        }
        finally
        {
            window.Close();
            theme.Apply(new ThemeSettings(), language);
        }
    }

    /// <summary>The preset editor: "Ask every time" under Pages, its hint when on, gone for a merge; saved into the preset.</summary>
    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("ko", true)]
    public void The_editor_turns_asking_on_under_pages(string language, bool dark)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        var loc = AppHost.Get<ILocalizer>();
        var preset = new Preset { Id = "test-ask-editor", Name = "Chapters", TargetFormat = "pdf" };
        var vm = new PresetEditorViewModel(preset, loc);
        Assert.True(vm.ShowAskPages);
        Assert.False(vm.ShowAskPagesHint);
        vm.AskPageRange = true;
        Assert.True(vm.ShowAskPagesHint);
        if (dark)
            theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
        try
        {
            var window = new PresetEditorWindow { DataContext = vm, Height = 1400 };
            window.Show();
            RenderTests.Pump();
            var ask = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "AskPages");
            Assert.True(ask.IsEffectivelyVisible);
            Assert.True(ask.IsChecked);
            RenderTests.Save(window, $"ask-pages-editor-{language}-{(dark ? "dark" : "light")}.png");
            window.Close();
        }
        finally
        {
            if (dark)
                theme.Apply(new ThemeSettings(), language);
        }
        Assert.True(vm.Apply());
        Assert.True(preset.Pdf.AskPageRange);

        // A merge takes one range for every file: no asking, and none kept behind it.
        vm = new PresetEditorViewModel(preset, loc);
        Assert.True(vm.AskPageRange);
        vm.Merge = true;
        Assert.False(vm.ShowAskPages);
        Assert.True(vm.Apply());
        Assert.False(preset.Pdf.AskPageRange);
    }
}
