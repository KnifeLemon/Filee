// The tick dial of the progress ring (Settings → Theme → Progress ring): how many of its 60 ticks are lit. The
// drawing itself needs a render target, so what is tested here is the count. They run on the headless UI thread:
// DonutMenu's static brushes and icons are Avalonia objects, and making them on another thread before the headless
// session starts leaves every UI test without a UI thread.

using Avalonia.Headless.XUnit;
using Filee.App.Controls;

namespace Filee.App.Tests;

public class TickProgressRingTests
{
    [AvaloniaTheory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 30)]
    [InlineData(0.94, 56)]
    [InlineData(1.0, 60)]
    public void The_lit_ticks_follow_the_progress(double fraction, int lit) =>
        Assert.Equal(lit, DonutMenu.LitTicks(fraction));

    [AvaloniaTheory]
    [InlineData(-0.2, 0)]
    [InlineData(1.7, 60)]
    public void Progress_outside_the_range_is_kept_inside_it(double fraction, int lit) =>
        Assert.Equal(lit, DonutMenu.LitTicks(fraction));

    [AvaloniaFact]
    public void A_failed_conversion_still_shows_where_it_stopped()
    {
        Assert.Equal(1, DonutMenu.LitTicks(0, atLeastOne: true));
        Assert.Equal(30, DonutMenu.LitTicks(0.5, atLeastOne: true));
    }
}
