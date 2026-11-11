// A group's ring follows the donut's size and the Group ring size and spread settings: at the defaults it is the
// ring of before, at radius 240 it is 1.6 times as deep, the size setting makes it deeper or shallower, the spread
// setting fans it wider, and the window keeps room for it. Clicks still land on its slices.

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class GroupRingSizeTests
{
    private static DonutItem Item(string label) => new() { Id = label, Label = label, IsEnabled = true };

    private static DonutMenu Donut(double radius = 150, double size = 1, double spread = 1)
    {
        TestServices.EnsureInitialized("en");
        var donut = new DonutMenu
        {
            Items = [Item("PNG"), Item("JPG"), new DonutItem { Id = "group", Label = "More", IsEnabled = true,
                Children = [Item("WEBP"), Item("PDF"), Item("TIFF")] }, Item("BMP")],
            OuterRadius = radius,
            GroupRingSize = size,
            GroupRingSpread = spread,
        };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return donut;
    }

    /// <summary>Opens the group's ring (slice 2), as resting on it does.</summary>
    private static DonutMenu Opened(DonutMenu donut)
    {
        var center = new Avalonia.Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2);
        var at = DonutGeometry.PointAt(center, donut.OuterRadius * 0.7, DonutGeometry.MidAngle(2, 4));
        donut.SetExternalHit(donut.HitTest(at), at);
        donut.ExpandPendingGroup();
        for (var i = 0; i < 60; i++)
            donut.Advance(1 / 60.0);
        return donut;
    }

    [AvaloniaFact]
    public void At_the_defaults_the_ring_and_the_window_are_as_before()
    {
        // Before: the ring 6 px out from a popped slice (7), 46 px deep, and 62 px of room for it round the donut.
        var donut = Donut();
        Assert.Equal((150 + 7 + 6.0, 150 + 7 + 52.0), donut.SubRadii);
        Assert.Equal(2 * (150 + 7 + 14 + 62.0), DonutMenu.SideFor(150, subRing: true));
        Assert.Equal(2 * (150 + 7 + 14 + 62.0), donut.Bounds.Width);
        Assert.Equal(DonutGeometry.SubSpan(DonutGeometry.Span(4), 3, 150 + 7 + 29),
            DonutGeometry.SubSpan(DonutGeometry.Span(4), 3, 150 + 7 + 29, spread: 1, minWidth: DonutGeometry.MinSubWidth));
    }

    [AvaloniaFact]
    public void The_ring_follows_the_donuts_size_and_the_size_setting()
    {
        var big = Donut(radius: 240);
        Assert.Equal(46 * 1.6, big.SubRadii.Outer - big.SubRadii.Inner, 6);
        Assert.Equal(6 * 1.6, big.SubRadii.Inner - 240 - 7, 6);
        Assert.InRange(big.Bounds.Width, 2 * (240 + 7 + 14 + 62 * 1.6), 2 * (240 + 7 + 14 + 62 * 1.6) + 1); // whole pixels

        var deep = Donut(size: 1.5);
        Assert.Equal(46 * 1.5, deep.SubRadii.Outer - deep.SubRadii.Inner, 6);
        var shallow = Donut(size: 0.75);
        Assert.Equal(46 * 0.75, shallow.SubRadii.Outer - shallow.SubRadii.Inner, 6);
        Assert.True(shallow.Bounds.Width < Donut().Bounds.Width);

        // Never smaller than the default ring of the smallest donut: a 10 px name must still fit.
        var smallest = Donut(radius: 100, size: 0.75);
        Assert.Equal(46 * 100 / 150.0, smallest.SubRadii.Outer - smallest.SubRadii.Inner, 6);
        Assert.Equal(DonutMenu.MinRingScale, DonutMenu.RingScaleFor(100, 0.75));
    }

    [AvaloniaFact]
    public void Spread_fans_the_ring_wider_but_never_round_more_than_once()
    {
        var span = DonutGeometry.Span(12);
        var plain = DonutGeometry.SubSpan(span, 3);
        Assert.Equal(plain * 2, DonutGeometry.SubSpan(span, 3, spread: 2), 9);
        Assert.Equal(2 * Math.PI / 3, DonutGeometry.SubSpan(DonutGeometry.Span(2), 3, spread: 3), 9);
        // The narrowest slice scales with the ring; spread widens even a ring held at its narrowest.
        Assert.Equal(88 / 200.0, DonutGeometry.SubSpan(0.01, 3, midRadius: 200, minWidth: 88), 9);
        Assert.Equal(1.5 * 88 / 200.0, DonutGeometry.SubSpan(0.01, 3, midRadius: 200, spread: 1.5, minWidth: 88), 9);

        // On the donut: its ring's slices are wider, and each is hit where it is drawn.
        var wide = Opened(Donut(spread: 2));
        var normal = Donut();
        double Angle(DonutMenu d, int sub) => DonutGeometry.AngleOf(new Avalonia.Point(d.Bounds.Width / 2, d.Bounds.Height / 2), d.SubSliceCenter(2, sub));
        Assert.True(Angle(wide, 1) - Angle(wide, 0) > Angle(normal, 1) - Angle(normal, 0));
        for (var sub = 0; sub < 3; sub++)
            Assert.Equal(new DonutHit(DonutHitKind.SubSegment, 2, sub), wide.HitTest(wide.SubSliceCenter(2, sub)));
    }

    [AvaloniaFact]
    public void A_big_ring_is_hit_where_it_is_drawn()
    {
        var donut = Opened(Donut(radius: 240, size: 1.5));
        for (var sub = 0; sub < 3; sub++)
            Assert.Equal(new DonutHit(DonutHitKind.SubSegment, 2, sub), donut.HitTest(donut.SubSliceCenter(2, sub)));
    }

    [AvaloniaFact]
    public void The_donut_window_takes_both_settings_and_keeps_room_for_the_ring()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var (size, spread) = (store.Settings.Donut.GroupRingSize, store.Settings.Donut.GroupRingSpread);
        try
        {
            store.Settings.Donut.GroupRingSize = 1.25;
            store.Settings.Donut.GroupRingSpread = 1.5;
            var vm = AppHost.Get<RadialViewModel>();
            vm.Load([@"C:\photos\a.jpg"], clickMode: true, backFace: true);
            vm.FitToThisDonut();
            Assert.Equal((1.25, 1.5), (vm.GroupRingSize, vm.GroupRingSpread));
            var window = new RadialWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((1.25, 1.5), (window.DonutControl.GroupRingSize, window.DonutControl.GroupRingSpread));
            Assert.Equal(DonutMenu.SideFor(vm.OuterRadius, subRing: true, 1.25), window.DonutControl.Bounds.Width, 6);
            window.Close();
        }
        finally
        {
            (store.Settings.Donut.GroupRingSize, store.Settings.Donut.GroupRingSpread) = (size, spread);
        }
    }

    [AvaloniaFact]
    public void The_toolbar_page_shows_and_saves_both_settings()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var (size, spread) = (store.Settings.Donut.GroupRingSize, store.Settings.Donut.GroupRingSpread);
        try
        {
            using var page = new Filee.App.ViewModels.Pages.ToolbarPageViewModel(store, AppHost.Get<Filee.Core.Localization.ILocalizer>(),
                AppHost.Get<WindowService>(), AppHost.Get<PresetAvailability>(), AppHost.Get<Filee.Core.Conversion.ConverterCatalog>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Filee.App.ViewModels.Pages.ToolbarPageViewModel>.Instance);
            Assert.Equal((100.0, 100.0), (page.GroupRingSize, page.GroupRingSpread));
            page.GroupRingSize = 125;
            page.GroupRingSpread = 150;
            Assert.Equal((1.25, 1.5), (store.Settings.Donut.GroupRingSize, store.Settings.Donut.GroupRingSpread));
            Assert.Equal((1.25).ToString("P0", System.Globalization.CultureInfo.CurrentCulture), page.GroupRingSizeText);
            Assert.Equal((1.5).ToString("P0", System.Globalization.CultureInfo.CurrentCulture), page.GroupRingSpreadText);
        }
        finally
        {
            (store.Settings.Donut.GroupRingSize, store.Settings.Donut.GroupRingSpread) = (size, spread);
            store.SaveSettings();
        }
    }
}
