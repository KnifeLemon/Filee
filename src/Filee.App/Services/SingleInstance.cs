// Makes sure only one Filee runs per user session. Later launches send their command line to the first
// instance through a named pipe (e.g. Explorer starts one process per selected file for the context menu).

using System.IO.Pipes;
using System.Text.Json;

namespace Filee.App.Services;

/// <summary>Named mutex + named pipe single-instance helper.</summary>
public sealed class SingleInstance : IDisposable
{
    // string.GetHashCode() is randomized per process, so a stable hash is required for both processes to agree.
    private static readonly string Name = "Filee-" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Environment.UserName)))[..12];
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex, bool isFirst)
    {
        _mutex = mutex;
        IsFirst = isFirst;
    }

    public bool IsFirst { get; }

    /// <summary>Raised on a background thread with the arguments of a later launch.</summary>
    public event Action<string[]>? ArgumentsReceived;

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + Name, out var createdNew);
        return new SingleInstance(mutex, createdNew);
    }

    /// <summary>Starts listening for later launches (first instance only).</summary>
    public void StartListening()
    {
        if (!IsFirst)
            return;
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token);
                    var args = await JsonSerializer.DeserializeAsync(server, AppJsonContext.Default.StringArray, _cts.Token);
                    if (args is not null)
                        ArgumentsReceived?.Invoke(args);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    // A client disconnected half-way; keep serving.
                }
            }
        });
    }

    /// <summary>Sends this process's arguments to the first instance.</summary>
    public void ForwardToFirst(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            JsonSerializer.Serialize(client, args, AppJsonContext.Default.StringArray);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // The first instance is starting or shutting down; nothing sensible to do.
        }
    }

    /// <summary>
    /// Waits until the first instance has exited (it releases the mutex then), e.g. after forwarding <c>--quit</c>.
    /// Returns false on timeout.
    /// </summary>
    public bool WaitForFirstToExit(TimeSpan timeout)
    {
        if (IsFirst)
            return true;
        try
        {
            if (!_mutex.WaitOne(timeout))
                return false;
        }
        catch (AbandonedMutexException)
        {
            // The first instance ended without releasing the mutex (killed): it is gone all the same.
        }
        _mutex.ReleaseMutex();
        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
