using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class EngineSetupReleaseTests
{
    private static readonly FieldInfo SetupWindow = typeof(WindowService)
        .GetField("_engineSetup", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PackageChanged = typeof(EnginePackageState).BaseType!
        .GetField("PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Closing_setup_releases_the_window_and_view_model(bool command)
    {
        TestServices.EnsureInitialized("en");
        var service = AppHost.Get<WindowService>();
        var states = SavePackages();
        var baseline = HandlerCounts();
        var closed = new List<(WeakReference Window, WeakReference Model)>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                closed.Add(OpenAndClose(service, command ? "command" : "direct", baseline));
                AssertCollected(closed);
            }
        }
        finally
        {
            (SetupWindow.GetValue(service) as EngineSetupWindow)?.Close();
            RestorePackages(states);
        }
    }

    [AvaloniaTheory]
    [InlineData("direct")]
    [InlineData("command")]
    [InlineData("escape")]
    public void Directly_created_setup_releases_on_close(string path)
    {
        TestServices.EnsureInitialized("en");
        var states = SavePackages();
        var baseline = HandlerCounts();
        try
        {
            var closed = OpenAndClose(null, path, baseline);
            AssertCollected([closed]);
        }
        finally
        {
            RestorePackages(states);
        }
    }

    [AvaloniaFact]
    public void Releasing_setup_twice_preserves_other_subscribers()
    {
        TestServices.EnsureInitialized("en");
        var states = SavePackages();
        var baseline = HandlerCounts();
        var other = NewModel();
        var target = NewModel();
        try
        {
            AssertHandlers(baseline, 2, target);
            target.Release();
            target.Release();
            AssertHandlers(baseline, 1, other);
            Assert.All(target.Packages, p => Assert.DoesNotContain(SetupHandlers(p), d => ReferenceEquals(d.Target, target)));
        }
        finally
        {
            target.Release();
            other.Release();
            RestorePackages(states);
        }
        AssertHandlers(baseline, 0);
    }

    [AvaloniaFact]
    public void Open_setup_keeps_selection_and_close_text_in_sync()
    {
        TestServices.EnsureInitialized("en");
        var states = SavePackages();
        var baseline = HandlerCounts();
        var vm = NewModel();
        var window = new EngineSetupWindow { DataContext = vm };
        try
        {
            window.Show();
            Pump();
            var package = vm.Packages.First(p => p.CanInstall);
            var check = window.GetVisualDescendants().OfType<CheckBox>()
                .Single(c => ReferenceEquals(c.DataContext, package));
            var install = window.FindControl<Button>("InstallButton")!;
            var close = window.FindControl<Button>("CloseButton")!;
            var selection = window.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Text == vm.SelectionText);
            var empty = vm.SelectionText;
            Assert.False(check.IsChecked);
            Assert.False(install.IsEffectivelyEnabled);
            Assert.False(vm.InstallCommand.CanExecute(null));

            check.IsChecked = true;
            Pump();
            Assert.True(package.Selected);
            Assert.True(install.IsEffectivelyEnabled);
            Assert.True(vm.InstallCommand.CanExecute(null));
            Assert.NotEqual(empty, vm.SelectionText);
            Assert.Equal(vm.SelectionText, selection.Text);

            package.Selected = false;
            Pump();
            Assert.False(check.IsChecked);
            Assert.False(install.IsEffectivelyEnabled);
            Assert.Equal(empty, vm.SelectionText);
            Assert.Equal(vm.SelectionText, selection.Text);

            var later = vm.CloseText;
            Assert.Equal(later, close.Content);
            vm.Started = true;
            package.Status = EnginePackageStatus.Queued;
            Pump();
            var background = vm.CloseText;
            Assert.NotEqual(later, background);
            Assert.Equal(background, close.Content);
            package.Status = EnginePackageStatus.Installed;
            Pump();
            Assert.NotEqual(background, vm.CloseText);
            Assert.Equal(vm.CloseText, close.Content);
            AssertHandlers(baseline, 1, vm);
        }
        finally
        {
            window.Close();
            vm.Release();
            RestorePackages(states);
        }
        AssertHandlers(baseline, 0);
    }

    // No strong references to the closed window or model survive this helper.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Window, WeakReference Model) OpenAndClose(
        WindowService? service, string path, Dictionary<string, int> baseline)
    {
        EngineSetupWindow window;
        if (service is not null)
        {
            service.ShowEngineSetup();
            window = Assert.IsType<EngineSetupWindow>(SetupWindow.GetValue(service));
        }
        else
        {
            window = new EngineSetupWindow { DataContext = NewModel() };
            window.Show();
        }
        var vm = Assert.IsType<EngineSetupViewModel>(window.DataContext);
        try
        {
            Pump();
            Assert.True(window.IsVisible);
            AssertHandlers(baseline, 1, vm);
            if (service is not null)
            {
                service.ShowEngineSetup();
                Assert.Same(window, SetupWindow.GetValue(service));
                Assert.Same(vm, window.DataContext);
                AssertHandlers(baseline, 1, vm);
            }

            var closed = false;
            window.Closed += (_, _) => closed = true;
            switch (path)
            {
                case "direct":
                    window.Close();
                    break;
                case "command":
                    vm.CloseCommand.Execute(null);
                    break;
                case "escape":
                    window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                    break;
            }
            Pump();
            Assert.True(closed, $"{path} must raise Closed");
            Assert.False(window.IsVisible);
            if (service is not null)
                Assert.Null(SetupWindow.GetValue(service));
            AssertHandlers(baseline, 0);

            // A command after Closed repeats release without touching another subscriber.
            vm.CloseCommand.Execute(null);
            AssertHandlers(baseline, 0);
            return (new(window), new(vm));
        }
        finally
        {
            window.Close();
            vm.Release();
        }
    }

    private static EngineSetupViewModel NewModel() =>
        new(AppHost.Get<EngineDownloadService>(), AppHost.Get<ILocalizer>());

    private static Delegate[] SetupHandlers(EnginePackageState package) =>
        ((PropertyChangedEventHandler?)PackageChanged.GetValue(package))?.GetInvocationList()
            .Where(d => d.Target is EngineSetupViewModel).ToArray() ?? [];

    private static Dictionary<string, int> HandlerCounts() =>
        AppHost.Get<EngineDownloadService>().Packages.ToDictionary(p => p.Package.Id, p => SetupHandlers(p).Length);

    private static void AssertHandlers(Dictionary<string, int> baseline, int added, EngineSetupViewModel? model = null)
    {
        foreach (var package in AppHost.Get<EngineDownloadService>().Packages)
        {
            var handlers = SetupHandlers(package);
            Assert.Equal(baseline[package.Package.Id] + added, handlers.Length);
            if (model is not null)
                Assert.Single(handlers, d => ReferenceEquals(d.Target, model));
        }
    }

    private static (EnginePackageState Package, EnginePackageStatus Status, bool Selected)[] SavePackages() =>
        AppHost.Get<EngineDownloadService>().Packages.Select(p => (p, p.Status, p.Selected)).ToArray();

    private static void RestorePackages((EnginePackageState Package, EnginePackageStatus Status, bool Selected)[] states)
    {
        foreach (var (package, status, selected) in states)
        {
            package.Status = status;
            package.Selected = selected;
        }
    }

    private static void Pump()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void AssertCollected(List<(WeakReference Window, WeakReference Model)> closed)
    {
        for (var round = 0; round < 3; round++)
        {
            Pump();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.All(closed, r =>
        {
            Assert.False(r.Window.IsAlive, "window");
            Assert.False(r.Model.IsAlive, "view model");
        });
    }
}
