// The keys of the Home page's queue: the arrows select a card (Up and Down by a row) only while the queue has the focus,
// so a button keeps its own focus and the selection stays where it is.

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
using Filee.Core.Localization;
using Filee.Core.Settings;
using PdfSharp.Pdf;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class HomeKeysTests
{
    private static void Pump()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public void Arrows_select_the_queued_cards_only_while_the_queue_has_the_focus()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-home-keys-").FullName;
        var paths = new List<string>();
        for (var i = 1; i <= 6; i++)
        {
            var path = Path.Combine(folder, $"card {i}.pdf");
            using var document = new PdfDocument();
            document.AddPage();
            document.Save(path);
            paths.Add(path);
        }
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<Filee.App.Services.UpdateService>(), AppHost.Get<UserDataStore>());
        vm.Navigate("home");
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();
        try
        {
            var page = window.GetVisualDescendants().OfType<HomePage>().Single();
            var model = Assert.IsType<HomePageViewModel>(vm.CurrentPage);
            model.AddToQueue(paths);
            Pump();
            var queue = page.FindControl<ItemsControl>("QueueList")!;
            int Selected() => model.Queue.ToList().FindIndex(card => card.IsSelected);

            queue.Focus();
            Pump();
            model.SelectQueued(0);
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Pump();
            var top = queue.ContainerFromIndex(0)!.TranslatePoint(new Point(), queue)!.Value.Y;
            var columns = Enumerable.Range(0, paths.Count)
                .Count(i => Math.Abs(queue.ContainerFromIndex(i)!.TranslatePoint(new Point(), queue)!.Value.Y - top) < 1);
            Assert.Equal(columns, Selected());

            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Pump();
            Assert.Equal(0, Selected());

            // A button has the focus: the arrows leave the queue's selection alone.
            var button = page.GetVisualDescendants().OfType<Button>().First(b => b.Focusable && b.IsEffectivelyVisible);
            button.Focus();
            Pump();
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            Pump();
            Assert.Equal(0, Selected());

            // Delete takes the selected card out, and the card that takes its place is selected.
            model.SelectQueued(1);
            queue.Focus();
            Pump();
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Pump();
            Assert.Equal(5, model.Queue.Count);
            Assert.Equal(1, Selected());

            // Alt+Down moves the selected card from anywhere on the page.
            window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
            Pump();
            // After the Delete the selected card is card 3 (index 1); Alt+Down moves it below card 4.
            Assert.Equal(paths[2], model.QueuedPaths[2]);
        }
        finally
        {
            window.Close();
        }
    }
}
