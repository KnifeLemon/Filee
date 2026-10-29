// SharpHook's EventLoopGlobalHook, except that the native callback never throws (#528). Its Dispose closes the event
// queue before it stops the native hook, and its queue thread disposes the queue once drained; an input event in
// between (or after a failed stop) threw on the OS hook thread and ended Filee. Here the hook stops first, and an
// event that finds the queue closed is dropped: the hook is going away anyway.

using System.Collections.Concurrent;
using SharpHook;
using SharpHook.Data;
using SharpHook.Providers;

namespace Filee.App.Services.Triggers;

internal sealed class QueuedGlobalHook(IGlobalHookProvider? provider = null) : GlobalHookBase(provider)
{
    private readonly BlockingCollection<UioHookEvent> _events = [];
    private int _loopStarted;

    protected override void BeforeRun()
    {
        if (Interlocked.Exchange(ref _loopStarted, 1) == 0)
            new Thread(RunEventLoop) { IsBackground = true, Name = "Filee input events" }.Start();
    }

    // The OS hook thread: Windows holds the cursor until this returns, so it only queues.
    protected override void HandleHookEvent(ref UioHookEvent e)
    {
        if (!ShouldDispatchEvent(ref e))
            return;
        if (e.Type == EventType.HookDisabled)
        {
            DispatchEvent(ref e);
            return;
        }
        try
        {
            _events.TryAdd(e);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Closed: the hook is being disposed.
        }
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing); // stops the native hook first
        }
        finally
        {
            _events.CompleteAdding();
        }
    }

    private void RunEventLoop()
    {
        foreach (var queued in _events.GetConsumingEnumerable())
        {
            var e = queued;
            DispatchEvent(ref e);
        }
    }
}
