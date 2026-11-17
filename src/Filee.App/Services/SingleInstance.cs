// Keeps one Filee per Windows session, or per user across Unix launcher/terminal sessions.
// Later launches forward arguments through a named pipe.

using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using Filee.Core.Settings;

namespace Filee.App.Services;

/// <summary>Named mutex + named pipe single-instance helper.</summary>
public sealed class SingleInstance : IDisposable
{
    // string.GetHashCode() is randomized per process, so a stable hash is required for both processes to agree.
    internal static readonly string DefaultName = NameFor(Environment.UserName, UserDataStore.OverrideDirectory);
    private readonly Mutex _mutex;
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex, string name, bool isFirst)
    {
        _mutex = mutex;
        _name = name;
        IsFirst = isFirst;
    }

    public bool IsFirst { get; }

    /// <summary>Raised on a background thread with the arguments of a later launch.</summary>
    public event Action<string[]>? ArgumentsReceived;

    /// <summary>Raised on a background thread when listening failed and is about to try again, for the log.</summary>
    public event Action<Exception>? ListeningFailed;

    public static SingleInstance Acquire() => Acquire(DefaultName);

    /// <summary>
    /// The mutex and pipe name for a user. A copy started with FILEE_DATA_DIR (<paramref name="dataDirectoryOverride"/>)
    /// gets a name of its own, so it neither forwards to nor receives from the Filee the user runs.
    /// </summary>
    internal static string NameFor(string userName, string? dataDirectoryOverride)
    {
        var key = dataDirectoryOverride is null ? userName : userName + "|" + dataDirectoryOverride.ToUpperInvariant();
        return "Filee-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..12];
    }

    /// <summary>The instance under another name (tests, which must not reach a Filee that is running).</summary>
    internal static SingleInstance Acquire(string name)
    {
        bool createdNew;
        var mutex = OperatingSystem.IsWindows()
            ? new Mutex(initiallyOwned: true, @"Local\" + name, out createdNew)
            : new Mutex(initiallyOwned: true, name,
                new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false }, out createdNew);
        return new SingleInstance(mutex, name, createdNew);
    }

    /// <summary>Starts listening for later launches (first instance only).</summary>
    public void StartListening()
    {
        if (!IsFirst)
            return;
        _ = Task.Run(async () =>
        {
            NamedPipeServerStream? keeper = null;
            NamedPipeServerStream? next = null;
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var connected = false;
                    try
                    {
                        // On macOS and Linux all pipes of a name share one listening socket, which closes with the
                        // last: the launches queued on it, whose sends had already succeeded, would be lost without any
                        // of them knowing. This one is never waited on, so no error here can close the socket. On
                        // Windows a pipe nobody waits on could take a launch and never read it.
                        if (!OperatingSystem.IsWindows())
                            keeper ??= CreateServer();
                        var waiting = next ?? CreateServer();
                        next = null;
                        await using var server = waiting;
                        await WaitForLaunch(server, _cts.Token);
                        connected = true;
                        if (OperatingSystem.IsWindows())
                            next = TryCreateServer();
                        using var reading = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                        reading.CancelAfter(ReadTimeout);
                        var args = await JsonSerializer.DeserializeAsync(server, AppJsonContext.Default.StringArray, reading.Token);
                        if (args is not null)
                            ArgumentsReceived?.Invoke(args);
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex) when (connected && ex is IOException or JsonException)
                    {
                        // A client disconnected half-way; keep serving.
                    }
                    catch (Exception ex)
                    {
                        // The pipe name is still held by a Filee with other rights (one that just restarted this one
                        // without administrator rights and is closing), a peer of another user was refused, the process
                        // is out of file handles and can't accept the launches that wait, a launch never sent its
                        // arguments, or a handler threw. Listening has to outlive any of this, or every later launch
                        // fails until Filee restarts: try again in a moment.
                        try
                        {
                            // On macOS an accept without a free file handle drops the launch it took from the queue.
                            do
                            {
                                await Task.Delay(500, _cts.Token);
                            } while (ex is SocketException { SocketErrorCode: SocketError.TooManyOpenSockets } && !FileHandleIsFree());
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        ListeningFailed?.Invoke(ex); // only now: the log needs a file handle too
                    }
                }
            }
            finally
            {
                next?.Dispose();
                keeper?.Dispose();
            }
        });
    }

    private static bool FileHandleIsFree()
    {
        if (OperatingSystem.IsWindows())
            return true;
        try
        {
            File.OpenHandle("/dev/null").Dispose();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // says nothing about handles, and waiting for it could take forever
        }
    }

    /// <summary>How long a launch that connected may take to send its arguments: the ones behind it wait.</summary>
    internal TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Waits for the next launch to connect (replaced in tests, to fail the way the system can).</summary>
    internal Func<NamedPipeServerStream, CancellationToken, Task> WaitForLaunch { get; set; } =
        (server, token) => server.WaitForConnectionAsync(token);

    /// <summary>
    /// Two at a time: the one in use and the keeper, or on Windows the next one, see <see cref="TryCreateServer"/>.
    /// </summary>
    private NamedPipeServerStream CreateServer() => new(_name, PipeDirection.In, 2,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    /// <summary>
    /// The next pipe on Windows, made before the connected one is closed, so a later launch finds one at all times.
    /// Null when it can't be made: the launch that did connect still gets served.
    /// </summary>
    private NamedPipeServerStream? TryCreateServer()
    {
        try
        {
            return CreateServer();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends this process's arguments to the first instance. It only listens once its services are up, which can take
    /// over ten seconds on a slow or busy computer: a second launch (or the installer's --quit) right after the first
    /// lost its arguments after one 3-second try. Tries again until <paramref name="wait"/> has passed; false when the
    /// first instance never answered (it is shutting down, or hung) or refused this process.
    /// </summary>
    public bool ForwardToFirst(string[] args, TimeSpan wait) => Forward(args, wait) == ForwardResult.Delivered;

    /// <summary>
    /// Like <see cref="ForwardToFirst"/>, telling why it failed. A pipe that keeps refusing access belongs to a Filee
    /// running with other rights (as administrator, while this one isn't): that never changes, so it gives up after a
    /// few tries instead of waiting the whole time.
    /// </summary>
    public ForwardResult Forward(string[] args, TimeSpan wait)
    {
        // Made before connecting: the first use of this code can be slow, and the listener only waits so long.
        var message = JsonSerializer.SerializeToUtf8Bytes(args, AppJsonContext.Default.StringArray);
        var deadline = DateTime.UtcNow + wait;
        var refusals = 0;
        while (true)
        {
            var left = deadline - DateTime.UtcNow;
            try
            {
                using var client = new NamedPipeClientStream(".", _name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect((int)Math.Clamp(left.TotalMilliseconds, 100, 1000));
                Filee.Platform.Windows.Foreground.AllowOtherProcess(); // its donut takes the focus (Esc, click away)
                client.Write(message);
                return ForwardResult.Delivered;
            }
            catch (UnauthorizedAccessException)
            {
                // Also thrown for a moment while the pipe is being recreated between two launches: try a few times.
                if (++refusals >= RefusalsBeforeGivingUp)
                    return ForwardResult.AccessDenied;
                if (DateTime.UtcNow >= deadline)
                    return ForwardResult.NoAnswer;
                Thread.Sleep(100);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or SocketException)
            {
                if (DateTime.UtcNow >= deadline)
                    return ForwardResult.NoAnswer;
                Thread.Sleep(100); // the pipe is busy with another launch, or not there yet
            }
        }
    }

    /// <summary>Access-denied answers in a row after which <see cref="Forward"/> stops trying.</summary>
    private const int RefusalsBeforeGivingUp = 5;

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

    /// <summary>Stops listening and releases the mutex. Safe to call twice (a restart releases it before shutting down).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
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

    private bool _disposed;
}

/// <summary>Outcome of <see cref="SingleInstance.Forward"/>.</summary>
public enum ForwardResult
{
    /// <summary>
    /// The arguments are sent. On macOS and Linux they can still wait on the socket of the running Filee, which takes
    /// them even while that Filee hangs. They are lost if it closes before reading them, or on macOS when it has no
    /// file handle free to take them.
    /// </summary>
    Delivered,

    /// <summary>
    /// Nobody took them in time: the running Filee is starting, shutting down or hung, or this launch can't open a
    /// connection.
    /// </summary>
    NoAnswer,

    /// <summary>The running Filee's pipe refuses this process: it runs with other rights (as administrator).</summary>
    AccessDenied,
}
