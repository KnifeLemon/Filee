// Explorer's classic menu entry starts one Filee per selected file (issue #767): all of them must end up as one request.

using Filee.App.Services;

namespace Filee.App.Tests;

public class SelectionLaunchesTests
{
    private const string A = @"C:\photos\a.png", B = @"C:\photos\b.png", C = @"C:\photos\c.png";

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    [Fact]
    public void The_first_launch_brings_the_whole_selection_and_the_others_nothing()
    {
        var time = new FakeTime();
        var reads = 0;
        var launches = new SelectionLaunches(() =>
        {
            reads++;
            return [A, B, C];
        }, time);

        // They arrive in any order, and far enough apart to outlast any wait for "the last one".
        Assert.Equal([A, B, C], launches.Arrived([B]));
        time.Advance(5);
        Assert.Empty(launches.Arrived([C]));
        time.Advance(5);
        Assert.Empty(launches.Arrived([A]));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void The_same_selection_again_is_a_new_request()
    {
        var time = new FakeTime();
        var launches = new SelectionLaunches(() => [A, B], time);
        Assert.Equal([A, B], launches.Arrived([A]));
        Assert.Empty(launches.Arrived([B]));

        time.Advance(10);
        Assert.Equal([A, B], launches.Arrived([B]));
    }

    [Fact]
    public void Launches_keep_their_files_when_the_selection_says_nothing()
    {
        // No selection to read (another file manager), or the launch isn't part of what is selected there.
        foreach (IReadOnlyList<string> selection in new IReadOnlyList<string>[] { [], [A], [B, C] })
        {
            var time = new FakeTime();
            var launches = new SelectionLaunches(() => selection, time);
            Assert.Equal([A], launches.Arrived([A]));
            time.Advance(5);
            Assert.Equal([A], launches.Arrived([A]));
        }
    }

    [Fact]
    public void A_burst_that_found_no_selection_does_not_ask_again_for_every_file()
    {
        var time = new FakeTime();
        var reads = 0;
        var launches = new SelectionLaunches(() =>
        {
            reads++;
            return [];
        }, time);
        foreach (var file in new[] { A, B, C })
        {
            Assert.Equal([file], launches.Arrived([file]));
            time.Advance(0.2);
        }
        Assert.Equal(1, reads);
    }

    [Fact]
    public void Several_files_in_one_launch_are_a_selection_already()
    {
        // Send To and the Windows 11 entry: whatever is selected by now doesn't matter.
        var launches = new SelectionLaunches(() => throw new InvalidOperationException("not asked"), new FakeTime());
        Assert.Equal([A, B], launches.Arrived([A, B]));
    }

    [Fact]
    public void A_launch_that_never_came_is_not_waited_for_forever()
    {
        var time = new FakeTime();
        var launches = new SelectionLaunches(() => [A, B], time);
        Assert.Equal([A, B], launches.Arrived([A]));

        // B's launch could not reach Filee. Its file, right-clicked again later, must not be swallowed.
        time.Advance(20);
        Assert.Equal([A, B], launches.Arrived([B]));
    }
}
