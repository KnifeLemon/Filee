// Long diagnostics stay readable, copyable and reachable within a screen-sized progress stack (#1240).

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

[CollectionDefinition("Toast notification environment", DisableParallelization = true)]
public sealed class ToastNotificationEnvironment;

[Collection("Toast notification environment")]
public sealed class ToastNotificationTests
{
    [AvaloniaTheory]
    [InlineData("en", true)]
    [InlineData("en", false)]
    [InlineData("ar", true)]
    [InlineData("ja", true)]
    public void Complete_errors_are_selectable_and_scroll_in_a_six_line_area(string language, bool dark)
    {
        using var fixture = new ToastFixture(language, dark);
        var job = fixture.AddFailure();
        var scroller = fixture.Window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "ErrorScroller");
        var text = Assert.IsType<SelectableTextBlock>(scroller.Content);
        Assert.Equal(job.ErrorDetails, text.Text);
        Assert.Contains("END OF ERROR", text.Text);
        Assert.Equal(0, text.MaxLines);
        foreach (var button in fixture.Window.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "CopyNotification" || b.Command == job.DismissCommand))
        {
            var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), fixture.Window)!.Value.X;
            Assert.True(right >= 0 && right <= fixture.Window.ClientSize.Width,
                $"button right {right}; " + string.Join("; ", button.GetVisualAncestors().OfType<Control>().Select(c => $"{c.GetType().Name} {c.Bounds} desired {c.DesiredSize} margin {c.Margin}")));
        }
        Assert.InRange(scroller.Bounds.Height, 1, 96);
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height);
        scroller.ScrollToEnd();
        Pump();
        Assert.True(scroller.Offset.Y > 0);
        Assert.InRange(Math.Abs(scroller.Offset.Y + scroller.Viewport.Height - scroller.Extent.Height), 0, 1);
        RenderTests.Save(fixture.Window, $"toast-error-{language}-{dark}.png");
    }

    [AvaloniaTheory]
    [InlineData(0, 0, 1366, 720, 1.5)]
    [InlineData(-1920, 40, 1920, 1000, 1)]
    [InlineData(1920, -1080, 1920, 1040, 2)]
    [InlineData(0, 0, 600, 800, 2)]
    public void Bursts_stay_inside_the_work_area_and_all_cards_remain_reachable(int x, int y, int width, int height, double scaling)
    {
        using var fixture = new ToastFixture("en");
        for (var i = 0; i < 8; i++) fixture.AddFailure();
        var area = new PixelRect(x, y, width, height);
        fixture.Window.FitTo(area, scaling);
        Pump();
        Assert.InRange(fixture.Window.ClientSize.Height * scaling, 1, area.Height);
        Assert.InRange(fixture.Window.ClientSize.Width * scaling, 1, area.Width);
        Assert.InRange(fixture.Window.Position.X, area.X, area.Right);
        Assert.InRange(fixture.Window.Position.Y, area.Y, area.Bottom);
        Assert.True(fixture.Window.Position.Y + fixture.Window.ClientSize.Height * scaling <= area.Bottom + 1);
        var stack = fixture.Window.FindControl<ScrollViewer>("StackScroller")!;
        Assert.True(stack.Extent.Height > stack.Viewport.Height);
        stack.ScrollToEnd();
        Pump();
        var lastCopy = fixture.Window.GetVisualDescendants().OfType<Button>().Last(b => b.Name == "CopyNotification");
        var point = lastCopy.TranslatePoint(new Point(0, lastCopy.Bounds.Height), fixture.Window)!.Value;
        Assert.InRange(point.Y, 0, fixture.Window.ClientSize.Height);
        Assert.Equal(8, fixture.Window.FindControl<ItemsControl>("Jobs")!.ItemCount);
        RenderTests.Save(fixture.Window, $"toast-stack-{x}-{y}-{scaling}.png");
    }

    [AvaloniaFact]
    public async Task Copy_button_copies_the_full_header_and_message_and_briefly_confirms_it()
    {
        using var fixture = new ToastFixture("en");
        var job = fixture.AddFailure();
        var button = fixture.Window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CopyNotification");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.NotNull(fixture.Window.Clipboard);
        Assert.Equal(job.NotificationText, await fixture.Window.Clipboard.TryGetTextAsync());
        Assert.Contains(job.Title, job.NotificationText);
        Assert.Contains("END OF ERROR", job.NotificationText);
        Assert.Equal("Copied", AutomationProperties.GetName(button));
        await Task.Delay(1700);
        Pump();
        Assert.Equal("Copy details", AutomationProperties.GetName(button));
        Assert.True(button.IsEnabled);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Keyboard_reaches_copy_close_and_job_actions_without_activating_the_toast_on_show(bool running)
    {
        using var fixture = new ToastFixture("en");
        fixture.AddFailure(running);
        Assert.False(fixture.Window.ShowActivated);
        fixture.Window.Activate();
        var reached = new HashSet<string>();
        var reachedAction = false;
        for (var i = 0; i < 12; i++)
        {
            fixture.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Pump();
            if (fixture.Window.FocusManager?.GetFocusedElement() is Button button)
            {
                reached.Add(AutomationProperties.GetName(button) ?? button.Content?.ToString() ?? "");
                reachedAction |= button.Command == (running ? fixture.Jobs[0].CancelCommand : fixture.Jobs[0].RetryCommand);
            }
        }
        Assert.Contains("Copy details", reached);
        Assert.Contains("Close", reached);
        Assert.True(reachedAction);
    }

    [AvaloniaFact]
    public void Copy_payload_includes_complete_notices_and_missing_font_warnings()
    {
        TestServices.EnsureInitialized("en");
        var conversion = new ConversionJob(["/tmp/document.pdf"], new Preset { TargetFormat = "pdf" }, "Copy / move");
        conversion.Files[0].State = FileState.Done;
        conversion.Files[0].NoticeKey = "notice.pdf_not_smaller";
        conversion.Files[0].MissingFonts = ["A missing font", "Another missing font"];
        var job = new JobViewModel(conversion, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>(), _ => { });
        Assert.Contains(job.Title, job.NotificationText);
        Assert.Contains(job.Status, job.NotificationText);
        Assert.Contains(job.Notices!, job.NotificationText);
        Assert.Contains(job.MissingFontsText!, job.NotificationText);
        Assert.DoesNotContain(Environment.NewLine + Environment.NewLine, job.NotificationText);
    }

    private sealed class ToastFixture : IDisposable
    {
        private readonly ConversionService _service;
        private readonly bool _motion;
        private readonly string _language;
        internal ToastWindow Window { get; }
        internal List<JobViewModel> Jobs { get; } = [];

        internal ToastFixture(string language, bool dark = true)
        {
            TestServices.EnsureInitialized(language);
            _language = language;
            _motion = Motion.Enabled;
            AppHost.Get<ThemeService>().Apply(new ThemeSettings { Mode = dark ? ThemeMode.Dark : ThemeMode.Light, ReduceMotion = true }, language);
            _service = AppHost.Get<ConversionService>();
            _service.ToastJobs.Clear();
            Window = new ToastWindow { DataContext = _service };
        }

        internal JobViewModel AddFailure(bool running = false)
        {
            var conversion = new ConversionJob(["/tmp/a-long-filename.txt"], new Preset { TargetFormat = "iso" }, "ISO image");
            typeof(ConversionJob).GetProperty(nameof(ConversionJob.State))!.SetValue(conversion, running ? JobState.Running : JobState.Failed);
            conversion.Files[0].State = FileState.Failed;
            conversion.Files[0].ErrorKey = "error.conversion_failed";
            conversion.Files[0].ErrorDetail = string.Concat(Enumerable.Repeat("Long diagnostic /folder/" + new string('a', 80) + ".txt\n", 20)) + "END OF ERROR";
            var job = new JobViewModel(conversion, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>(), _ => { }, _ => { });
            Jobs.Add(job);
            _service.Jobs.Insert(0, job);
            _service.ShowInToast(job);
            Pump();
            return job;
        }

        public void Dispose()
        {
            _service.ToastJobs.Clear();
            foreach (var job in Jobs) _service.Jobs.Remove(job);
            Window.Close();
            AppHost.Get<ThemeService>().Apply(new ThemeSettings(), _language);
            Motion.Set(_motion);
        }
    }

    private static void Pump()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
