// Time advances deterministically: waiting, paused and dismissed cards must not lose their visible lifetime.

using Filee.App.Services;

namespace Filee.App.Tests;

public sealed class ToastLifetimeTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    public void A_finished_waiting_card_gets_its_full_lifetime_when_first_presented(int seconds)
    {
        var clock = new Clock();
        var timers = new List<Timer>();
        var dismissed = new List<object>();
        var lifetime = new ToastLifetime<object>(dismissed.Add, (action, delay) => Add(timers, action, delay), clock);
        var job = new object();
        lifetime.Finished(job, TimeSpan.FromSeconds(seconds), visible: false);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(timers);
        Assert.Empty(dismissed);
        lifetime.SetVisible(job, true);
        Assert.Equal(TimeSpan.FromSeconds(seconds), Assert.Single(timers).Delay);
        timers[0].Fire();
        Assert.Equal([job], dismissed);
    }

    [Fact]
    public void Hiding_pauses_remaining_time_and_old_callbacks_cannot_dismiss_a_reshown_card()
    {
        var clock = new Clock();
        var timers = new List<Timer>();
        var dismissed = new List<object>();
        var lifetime = new ToastLifetime<object>(dismissed.Add, (action, delay) => Add(timers, action, delay), clock);
        var job = new object();
        lifetime.Finished(job, TimeSpan.FromSeconds(5), visible: true);
        clock.Advance(TimeSpan.FromSeconds(2));
        lifetime.SetVisible(job, false);
        Assert.True(timers[0].Disposed);
        clock.Advance(TimeSpan.FromHours(1));
        lifetime.SetVisible(job, true);
        Assert.Equal(TimeSpan.FromSeconds(3), timers[1].Delay);
        timers[0].Fire();
        Assert.Empty(dismissed);
        timers[1].Fire();
        Assert.Equal([job], dismissed);
    }

    [Fact]
    public void Repeated_layout_notifications_do_not_restart_timers_and_manual_dismissal_cancels_them()
    {
        var timers = new List<Timer>();
        var dismissed = new List<object>();
        var lifetime = new ToastLifetime<object>(dismissed.Add, (action, delay) => Add(timers, action, delay));
        var job = new object();
        lifetime.Finished(job, TimeSpan.FromSeconds(15), visible: true);
        lifetime.SetVisible(job, true);
        lifetime.Finished(job, TimeSpan.FromSeconds(15), visible: true);
        Assert.Single(timers);
        lifetime.Forget(job);
        Assert.True(timers[0].Disposed);
        timers[0].Fire();
        Assert.Empty(dismissed);
    }

    private static Timer Add(List<Timer> timers, Action action, TimeSpan delay)
    {
        var timer = new Timer(action, delay);
        timers.Add(timer);
        return timer;
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan time) => _ticks += time.Ticks;
    }

    private sealed class Timer(Action action, TimeSpan delay) : IDisposable
    {
        internal TimeSpan Delay => delay;
        internal bool Disposed { get; private set; }
        internal void Fire() => action();
        public void Dispose() => Disposed = true;
    }
}
