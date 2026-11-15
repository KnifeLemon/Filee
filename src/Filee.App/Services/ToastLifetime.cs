// Dismissal time belongs to actual presentation, not to the time a queued job finished.

namespace Filee.App.Services;

internal sealed class ToastLifetime<T>(Action<T> dismiss, Func<Action, TimeSpan, IDisposable> schedule, TimeProvider? clock = null) where T : class
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<T, Entry> _entries = [];

    internal void Finished(T job, TimeSpan keep, bool visible)
    {
        if (!_entries.TryGetValue(job, out var entry)) _entries[job] = entry = new Entry { Remaining = keep };
        SetVisible(job, visible);
    }

    internal void SetVisible(T job, bool visible)
    {
        if (!_entries.TryGetValue(job, out var entry) || entry.Visible == visible) return;
        entry.Visible = visible;
        entry.Generation++;
        if (!visible)
        {
            if (entry.Timer is not null)
            {
                entry.Remaining = TimeSpan.FromTicks(Math.Max(0, (entry.Remaining - _clock.GetElapsedTime(entry.Started)).Ticks));
                entry.Timer.Dispose();
                entry.Timer = null;
            }
            return;
        }
        entry.Started = _clock.GetTimestamp();
        var generation = entry.Generation;
        entry.Timer = schedule(() =>
        {
            if (!_entries.TryGetValue(job, out var current) || current != entry || current.Generation != generation) return;
            _entries.Remove(job);
            dismiss(job);
        }, entry.Remaining);
    }

    internal void Forget(T job)
    {
        if (_entries.Remove(job, out var entry)) entry.Timer?.Dispose();
    }

    private sealed class Entry
    {
        internal TimeSpan Remaining;
        internal long Started;
        internal int Generation;
        internal bool Visible;
        internal IDisposable? Timer;
    }
}
