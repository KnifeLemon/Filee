// Real JobQueue completion must wait for toast presentation, including handoff from the donut ring.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Core.Web;

namespace Filee.App.Tests;

[Collection("Toast notification environment")]
public sealed class ToastServiceLifetimeTests
{
    [AvaloniaFact]
    public async Task The_window_arms_only_presented_cards_and_manual_close_promotes_the_next_one()
    {
        TestServices.EnsureInitialized("en");
        AppHost.Get<ThemeService>().Apply(new ThemeSettings { ReduceMotion = true }, "en");
        var root = Directory.CreateTempSubdirectory("filee-toast-window-lifetime-").FullName;
        ToastWindow? window = null;
        try
        {
            await using var queue = new JobQueue(new ConverterCatalog([]));
            var store = new UserDataStore(root);
            store.Load();
            var service = new ConversionService(queue, store, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>(), new WebDownloader());
            var timers = new List<Pending>();
            service.ScheduleToastDismissal = (action, delay) => { var timer = new Pending(action, delay); timers.Add(timer); return timer; };
            var count = 0;
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.JobFinished += (_, _) => { if (Interlocked.Increment(ref count) == 12) finished.TrySetResult(); };
            for (var i = 0; i < 12; i++) service.Start([Path.Combine(root, $"missing-{i}.unknown")], new Preset { TargetFormat = "png" });
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(timers);
            var first = service.ToastJobs.Last();
            window = new ToastWindow { DataContext = service };
            Pump();
            window.FitTo(new PixelRect(0, 0, 800, 300), 1);
            Pump();
            var panel = Assert.IsType<ToastStackPanel>(window.FindControl<ItemsControl>("Jobs")!.ItemsPanelRoot);
            Assert.True(panel.PendingCount > 0);
            Assert.Equal(panel.VisibleCount, timers.Count(t => !t.Disposed));
            first.DismissCommand.Execute(null);
            Pump();
            Assert.DoesNotContain(first, service.ToastJobs);
            Assert.Equal(panel.VisibleCount, timers.Count(t => !t.Disposed));
            window.Hide();
            Assert.All(timers, t => Assert.True(t.Disposed));
        }
        finally
        {
            window?.Close();
            AppHost.Get<ThemeService>().Apply(new ThemeSettings(), "en");
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Completed_jobs_wait_for_presentation_and_ring_handoff_pauses_an_existing_timer(bool showInToast)
    {
        TestServices.EnsureInitialized("en");
        var root = Directory.CreateTempSubdirectory("filee-toast-service-").FullName;
        try
        {
            await using var queue = new JobQueue(new ConverterCatalog([]));
            var store = new UserDataStore(root);
            store.Load();
            var service = new ConversionService(queue, store, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>(), new WebDownloader());
            var scheduled = new List<Pending>();
            service.ScheduleToastDismissal = (action, delay) =>
            {
                var timer = new Pending(action, delay);
                scheduled.Add(timer);
                return timer;
            };
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.JobFinished += (_, _) => finished.TrySetResult();
            var job = service.Start([Path.Combine(root, "missing.unknown")], new Preset { TargetFormat = "png" }, showInToast)!;
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.False(job.IsRunning);
            if (showInToast) Assert.Empty(scheduled);
            else
            {
                Assert.Single(scheduled);
                service.ShowInToast(job);
                Assert.True(scheduled[0].Disposed);
            }
            Assert.Contains(job, service.Jobs);
            service.SetToastVisible(job, true);
            Assert.Equal(showInToast ? 1 : 2, scheduled.Count);
            Assert.InRange(scheduled[^1].Delay.TotalSeconds, 14, 15);
            if (!showInToast)
            {
                scheduled[0].Action();
                Assert.Contains(job, service.Jobs);
            }
            scheduled[^1].Action();
            Assert.DoesNotContain(job, service.Jobs);
            Assert.DoesNotContain(job, service.ToastJobs);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Pending(Action action, TimeSpan delay) : IDisposable
    {
        internal Action Action => action;
        internal TimeSpan Delay => delay;
        internal bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
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
