// Opt-in portable storage, selected before settings, logging or engines touch a user profile.

namespace Filee.Core.Settings;

/// <summary>Portable storage enabled by a filee.portable marker next to the application.</summary>
public static class PortableMode
{
    /// <summary>The marker shipped or created beside Filee.</summary>
    public const string MarkerName = "filee.portable";

    /// <summary>The selected portable data directory, or null in an ordinary installation.</summary>
    public static string? DataDirectory { get; private set; }

    /// <summary>Whether this process was started with a portable marker.</summary>
    public static bool IsEnabled => DataDirectory is not null;

    /// <summary>
    /// Selects and checks portable storage before any profile-dependent service initializes. FILEE_DATA_DIR,
    /// when explicitly set, retains precedence. Disposing the scope restores the previous data override.
    /// </summary>
    /// <param name="applicationDirectory">Package location, defaulting to the executable directory.</param>
    public static IDisposable? Initialize(string? applicationDirectory = null)
    {
        var data = Resolve(applicationDirectory ?? AppContext.BaseDirectory, UserDataStore.OverrideDirectory);
        if (data is null) return null;
        var previousData = Environment.GetEnvironmentVariable(UserDataStore.DirectoryVariable);
        // Set this before probing: even the failure path must never open the installed copy's log or data.
        DataDirectory = data;
        Environment.SetEnvironmentVariable(UserDataStore.DirectoryVariable, data);
        try
        {
            Validate(data);
            return new DataScope(previousData);
        }
        catch
        {
            DataDirectory = null;
            Environment.SetEnvironmentVariable(UserDataStore.DirectoryVariable, previousData);
            throw;
        }
    }

    /// <summary>Finds a marked package; macOS data stays outside the signed .app bundle.</summary>
    internal static string? Resolve(string baseDirectory, string? explicitDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        if (directory.Name == "MacOS" && directory.Parent?.Name == "Contents"
            && directory.Parent.Parent is { } bundle && bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            directory = bundle.Parent ?? directory;
        return File.Exists(Path.Combine(directory.FullName, MarkerName))
            ? UserDataStore.DirectoryFrom(explicitDirectory) ?? Path.Combine(directory.FullName, "data")
            : null;
    }

    /// <summary>Checks creation, writing and deletion needed for atomic settings saves.</summary>
    internal static void Validate(string directory)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            using var probe = new FileStream(Path.Combine(directory, ".filee-write-" + Guid.NewGuid().ToString("N")),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            probe.WriteByte(0);
            probe.Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PortableStorageException(directory, ex);
        }
    }

    /// <summary>Portable copies cannot take ownership of an installed copy's login or file-manager entries.</summary>
    internal static void DisableIntegration(AppSettings settings)
    {
        settings.StartWithSystem = false;
        settings.ContextMenuEnabled = false;
    }

    private sealed class DataScope(string? previousData) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DataDirectory = null;
            Environment.SetEnvironmentVariable(UserDataStore.DirectoryVariable, previousData);
        }
    }
}

/// <summary>A marked package cannot use its selected data directory; no profile fallback is allowed.</summary>
public sealed class PortableStorageException(string dataDirectory, Exception innerException)
    : IOException($"Portable data folder is not writable: {dataDirectory}", innerException)
{
    /// <summary>The data directory that must be made writable or moved.</summary>
    public string DataDirectory { get; } = dataDirectory;
}
