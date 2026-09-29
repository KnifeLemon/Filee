// COM automation (MS Word) must run on a single-threaded apartment thread.

using System.Runtime.Versioning;

namespace Filee.Engines.Infrastructure;

/// <summary>Runs a delegate on a fresh STA thread and awaits the result.</summary>
public static class StaThread
{
    [SupportedOSPlatform("windows")]
    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                tcs.TrySetResult(work());
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Filee COM worker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
