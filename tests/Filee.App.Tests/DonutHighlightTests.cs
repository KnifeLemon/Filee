// The slice under a dragged file lights up even after a frame of the donut's animation loop got lost.

using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Filee.App.Controls;

namespace Filee.App.Tests;

public class DonutHighlightTests
{
    [AvaloniaFact]
    public void A_lost_animation_frame_does_not_keep_the_slice_dark()
    {
        TestServices.EnsureInitialized("ko");
        var donut = new DonutMenu
        {
            Items = new[] { "PNG", "JPG", "WEBP", "PDF" }.Select(l => new DonutItem { Id = l, Label = l }).ToList(),
        };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // As after a frame request that never came back: the loop still counts as running, its last frame long ago.
        Set(donut, "_animating", true);
        Set(donut, "_lastTick", Environment.TickCount64 - DonutMenu.StalledLoopMs * 4);

        donut.SetExternalHighlight(2);
        for (var i = 0; i < 90 && donut.LitAmount(2) < 0.9; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(donut.LitAmount(2) > 0.9, $"lit {donut.LitAmount(2):0.00}");
        window.Close();
    }

    private static void Set(DonutMenu donut, string field, object value) =>
        typeof(DonutMenu).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(donut, value);
}
