// The Engines page: dragging a card changes the engine priority, the LibreOffice switches open from its card.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Settings;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public class EnginesPageTests
{
    private static void Pump()
    {
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static Point Middle(Control card, Visual to) => card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height / 2), to)!.Value;

    [AvaloniaFact]
    public void A_package_Windows_Installer_cannot_open_says_why_in_the_users_language()
    {
        // #527: msiexec 1620, a junction on the way. The card says so in the user's language, with the log's path.
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<LocalizationService>();
        var failure = Filee.Engines.Infrastructure.EngineInstaller.MsiFailure("libreoffice", 1620, @"C:\Temp\filee-msiexec.log");
        try
        {
            loc.SetLanguage("ko");
            var text = EngineDownloadService.ErrorText(loc, failure);
            Assert.StartsWith(loc["error.msi_link"], text);
            Assert.NotEqual("error.msi_link", loc["error.msi_link"]);
            Assert.EndsWith(@"(msiexec 1620, log: C:\Temp\filee-msiexec.log)", text);
            Assert.Equal(loc["error.msi_busy"], EngineDownloadService.ErrorText(loc, Filee.Engines.Infrastructure.EngineInstaller.MsiFailure("calibre", 1618, "log")));
        }
        finally
        {
            loc.SetLanguage("en");
        }
        Assert.Contains("exit code 1603", EngineDownloadService.ErrorText(loc, new AggregateException(
            Filee.Engines.Infrastructure.EngineInstaller.MsiFailure("calibre", 1603, "log"))));
    }

    [AvaloniaFact]
    public void Dragging_a_card_below_the_next_one_moves_it_there_and_saves_the_order()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var saved = store.Settings.EnginePriority.ToList();
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
        vm.Navigate("engines");
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();
        try
        {
            var page = window.GetVisualDescendants().OfType<EnginesPage>().Single();
            var engines = ((EnginesPageViewModel)page.DataContext!).Engines;
            var (first, second, third) = (engines[0], engines[1], engines[2]);
            var list = page.FindControl<ItemsControl>("EngineList")!;
            // The first card a little below the top of the page, away from the edges that scroll the page while dragging.
            var scroller = page.FindAncestorOfType<ScrollViewer>()!;
            var top = list.ContainerFromIndex(0)!.TranslatePoint(default, scroller)!.Value.Y;
            scroller.Offset = scroller.Offset.WithY(scroller.Offset.Y + top - 150);
            Pump();

            // Grab the first card and move past the middle of the second one: it moves there while dragging.
            var from = Middle(list.ContainerFromIndex(0)!, window);
            var past = Middle(list.ContainerFromIndex(1)!, window) + new Point(0, 12);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + new Point(0, 8));
            Assert.True(first.IsDragging);
            window.MouseMove(past);
            Pump();
            Assert.Equal([second, first, third], engines.Take(3));
            Assert.Equal(saved, store.Settings.EnginePriority); // saved on release

            window.MouseUp(past, MouseButton.Left);
            Pump();
            Assert.False(first.IsDragging);
            Assert.Equal(engines.Select(e => e.Converter.Id), store.Settings.EnginePriority);

            // A click without moving changes nothing.
            var order = engines.ToList();
            var at = Middle(list.ContainerFromIndex(2)!, window);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Pump();
            Assert.Equal(order, engines);
        }
        finally
        {
            window.Close();
            store.Settings.EnginePriority = saved;
            store.SaveSettings();
        }
    }

    [AvaloniaFact]
    public void Font_card_describes_fon_input_only()
    {
        TestServices.EnsureInitialized("en");
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
        vm.Navigate("engines");
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();
        try
        {
            var page = window.GetVisualDescendants().OfType<EnginesPage>().Single();
            var engines = ((EnginesPageViewModel)page.DataContext!).Engines;
            var font = engines.Single(e => e.Converter.Id == "font");
            var card = page.FindControl<ItemsControl>("EngineList")!.ContainerFromIndex(engines.IndexOf(font))!;

            // The card's tooltip is the engine's own detail, and FON is only ever a source.
            var tip = card.GetVisualDescendants().OfType<StackPanel>().Select(ToolTip.GetTip).OfType<string>().Single();
            Assert.Equal(font.Converter.GetStatus().Detail, tip);
            Assert.Contains("FON", tip);
            Assert.Equal(["eot", "otf", "ttf", "woff", "woff2"], font.Converter.Edges.Where(e => e.From == "fon").Select(e => e.To).Order());
            Assert.DoesNotContain(font.Converter.Edges, e => e.To == "fon");

            var description = AppHost.Get<ILocalizer>()["engine.font.description"];
            Assert.Equal(description, font.Description);
            Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == description && t.IsVisible);
            Assert.Contains("FON", description);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Only_LibreOffice_has_settings_and_they_change_the_saved_switches()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var before = store.Settings.LibreOfficeForSheets;
        var vm = new EnginesPageViewModel(store, AppHost.Get<Filee.Core.Conversion.ConverterCatalog>(), AppHost.Get<ILocalizer>(),
            AppHost.Get<EngineDownloadService>());
        try
        {
            Assert.Equal(["libreoffice"], vm.Packages.Where(p => p.HasSettings).Select(p => p.Package.Id));

            var options = new LibreOfficeOptionsWindow { DataContext = vm };
            options.Show();
            Pump();
            var sheets = options.GetVisualDescendants().OfType<ToggleSwitch>().ElementAt(2);
            sheets.IsChecked = !before;
            Assert.Equal(!before, store.Settings.LibreOfficeForSheets);
            options.Close();
        }
        finally
        {
            vm.LibreOfficeForSheets = before;
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void A_second_engine_can_be_installed_while_one_downloads_and_waits_its_turn()
    {
        // #819: one install running must not turn off the Install button on the other cards.
        TestServices.EnsureInitialized("en");
        var downloads = new EngineDownloadService(AppHost.Get<ConverterCatalog>(), AppHost.Get<ILocalizer>(), NullLogger<EngineDownloadService>.Instance);
        var started = new List<string>();
        downloads.InstallPackage = (package, _, token) =>
        {
            Dispatcher.UIThread.Post(() => started.Add(package.Id));
            return Task.Delay(Timeout.Infinite, token);
        };
        var vm = new EnginesPageViewModel(AppHost.Get<UserDataStore>(), AppHost.Get<ConverterCatalog>(), AppHost.Get<ILocalizer>(), downloads);
        var (first, second) = (downloads.Packages.Where(p => p.CanInstall).ElementAt(0), downloads.Packages.Where(p => p.CanInstall).ElementAt(1));
        try
        {
            vm.InstallCommand.Execute(first);
            for (var i = 0; i < 200 && started.Count == 0; i++)
            {
                Thread.Sleep(10);
                Pump();
            }
            Assert.Equal([first.Package.Id], started);
            Assert.True(first.IsBusy);

            Assert.True(vm.InstallCommand.CanExecute(second));
            vm.InstallCommand.Execute(second);
            Pump();
            Assert.Equal(EnginePackageStatus.Queued, second.Status);
            Assert.Equal([first.Package.Id], started); // waits for the first one

            // Cancel on the first card stops only the first; the second then gets its turn.
            vm.CancelCommand.Execute(first);
            for (var i = 0; i < 200 && started.Count < 2; i++)
            {
                Thread.Sleep(10);
                Pump();
            }
            Assert.False(first.IsBusy);
            Assert.True(second.IsBusy);
            Assert.Equal([first.Package.Id, second.Package.Id], started);
        }
        finally
        {
            downloads.Cancel(first);
            downloads.Cancel(second);
            Pump();
            vm.Dispose();
        }
    }
}
