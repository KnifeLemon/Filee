using System.Reflection;
using Avalonia.Headless.XUnit;
using Filee.App.Controls;
using Filee.App.Services;

namespace Filee.App.Tests;

[Collection("Motion")]
public class DonutAnimationTests
{
    private static double[] Open(DonutMenu donut) => (double[])typeof(DonutMenu)
        .GetField("_open", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(donut)!;
    private static DonutMenu Menu() => new()
    {
        Items = Enumerable.Range(0, 20)
        .Select(i => new DonutItem { Id = i.ToString(), Label = "PNG" }).ToList()
    };

    [AvaloniaTheory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(144)]
    public void Opening_finishes_within_400ms_even_with_many_slices(int hz)
    {
        TestServices.EnsureInitialized("en");
        var previous = Motion.Enabled;
        try
        {
            Motion.Set(true);
            var donut = Menu(); donut.PlayOpenAnimation();
            for (var i = 0; i < Math.Ceiling(hz * 0.4); i++) donut.Advance(1.0 / hz);
            Assert.All(Open(donut), value => Assert.Equal(1, value, 6));
        }
        finally { Motion.Set(previous); }
    }

    [AvaloniaFact]
    public void Closing_mid_open_preserves_the_current_pose_and_completes_once()
    {
        TestServices.EnsureInitialized("en");
        var previous = Motion.Enabled;
        try
        {
            Motion.Set(true);
            var donut = Menu(); donut.PlayOpenAnimation(); donut.Advance(0.08);
            var before = Open(donut).ToArray(); var calls = 0;
            donut.PlayCloseAnimation(() => calls++); donut.Advance(0);
            Assert.Equal(before, Open(donut));
            donut.Advance(0.15); donut.Advance(0.15);
            Assert.All(Open(donut), value => Assert.Equal(0, value));
            Assert.Equal(1, calls);
            donut.PlayOpenAnimation(); donut.Advance(0.05, 0.4);
            Assert.All(Open(donut), value => Assert.Equal(1, value));
        }
        finally { Motion.Set(previous); }
    }
    [AvaloniaFact]
    public void Text_layout_cache_reuses_identical_input_and_tracks_paint_and_size()
    {
        TestServices.EnsureInitialized("en");
        var donut = Menu();
        var method = typeof(DonutMenu).GetMethod("CachedText", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object Text(string text, Avalonia.Media.IBrush brush, double width = 80) => method.Invoke(donut,
            [text, new Avalonia.Media.Typeface("Arial"), 13.0, brush, width, 2])!;
        var white = Avalonia.Media.Brushes.White;
        Assert.Same(Text("PNG", white), Text("PNG", white));
        Assert.NotSame(Text("PNG", white), Text("PNG", Avalonia.Media.Brushes.Black));
        Assert.NotSame(Text("PNG", white), Text("PNG", white, 90));
        for (var i = 0; i < 600; i++) Text("Label " + i, white);
        var cache = (System.Collections.IDictionary)typeof(DonutMenu)
            .GetField("_textLayouts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(donut)!;
        Assert.InRange(cache.Count, 1, 512);
    }

    [AvaloniaFact]
    public void Flip_during_opening_does_not_restart_opening_on_the_new_face()
    {
        TestServices.EnsureInitialized("en"); var previous = Motion.Enabled;
        try
        {
            Motion.Set(true); var donut = Menu(); donut.CanFlip = true;
            donut.FaceSwapping += (_, _) => donut.Items = Menu().Items;
            donut.PlayOpenAnimation(); donut.Advance(0.04); donut.Flip(); donut.Advance(0.01);
            Assert.All(Open(donut), value => Assert.Equal(1, value));
        }
        finally { Motion.Set(previous); }
    }

    [AvaloniaFact]
    public void Late_frame_completes_both_queued_turns_without_stretching_time()
    {
        TestServices.EnsureInitialized("en"); var previous = Motion.Enabled;
        try
        {
            Motion.Set(true); var donut = Menu(); donut.CanFlip = true; var turns = 0;
            donut.FaceSwapping += (_, _) => { turns++; donut.Items = Menu().Items; };
            donut.Flip(); donut.Flip(); donut.Advance(0.05, DonutMenu.FlipDuration * 2 + 0.001);
            Assert.False(donut.IsFlipping); Assert.Equal(2, turns);
        }
        finally { Motion.Set(previous); }
    }

}
