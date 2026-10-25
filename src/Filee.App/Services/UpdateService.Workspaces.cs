// UpdateService: private work folders for an update (one per process) and the sweep of ones left behind.

using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Settings;
using Filee.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

public sealed partial class UpdateService
{
    /// <summary>Gets the default root directory where secure workspaces are allocated.</summary>
    internal static string GetDefaultWorkspaceBaseDirectory()
    {
        if (OperatingSystem.IsLinux())
        {
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var root = !string.IsNullOrWhiteSpace(dataHome)
                ? dataHome
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "filee");
        }
        return Path.GetTempPath();
    }

    /// <summary>
    /// Creates a private, dedicated temporary workspace for this update instance.
    /// Mode 0700 on Unix, inaccessible to other local users, non-predictable, and never reusing pre-existing paths.
    /// </summary>
    internal static string CreateSecureWorkspace(string? baseDir = null)
    {
        baseDir = string.IsNullOrWhiteSpace(baseDir) ? GetDefaultWorkspaceBaseDirectory() : baseDir;

        Directory.CreateDirectory(baseDir);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var candidate = Path.Combine(baseDir, $".update-{Environment.ProcessId}-{token}");
            if (Path.Exists(candidate))
                continue;

            if (OperatingSystem.IsLinux())
            {
                Directory.CreateDirectory(candidate, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(candidate, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else
            {
                Directory.CreateDirectory(candidate);
            }

            var dirInfo = new DirectoryInfo(candidate);
            if (dirInfo.LinkTarget is not null || dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException($"Secure workspace candidate '{candidate}' is a symbolic link.");
            }

            return candidate;
        }

        throw new IOException("Failed to create a unique secure workspace directory after multiple attempts.");
    }

    /// <summary>Safely deletes a secure workspace directory created by this update, refusing to follow symlinks.</summary>
    internal static void DeleteSecureWorkspace(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Path.Exists(workspacePath))
            return;

        var dirInfo = new DirectoryInfo(workspacePath);
        if (dirInfo.LinkTarget is not null || dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return;

        try
        {
            Directory.Delete(workspacePath, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }
    }

    /// <summary>
    /// Checks whether a process with the given <paramref name="pid"/> is currently running.
    /// Used to protect active update workspaces from being swept.
    /// </summary>
    internal static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
            return false;

        try
        {
            if (OperatingSystem.IsLinux())
                return Directory.Exists($"/proc/{pid}");

            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Exception)
        {
            // If in doubt (e.g. access denied), assume alive to safeguard against deleting active workspaces
            return true;
        }
    }

    /// <summary>
    /// Checks whether <paramref name="name"/> matches the Filee-owned workspace pattern
    /// <c>.update-&lt;pid&gt;-&lt;32-hex-characters&gt;</c> and extracts the valid positive process ID.
    /// </summary>
    internal static bool TryParseWorkspaceName(string name, out int pid)
    {
        pid = 0;
        var parts = name.Split('-');
        if (parts.Length != 3 || parts[0] != ".update")
            return false;

        // The final component must contain exactly 32 hexadecimal characters (0-9, a-f, or A-F)
        if (parts[2].Length != 32 || !parts[2].All(char.IsAsciiHexDigit))
            return false;

        // <pid> must be a valid positive process ID in decimal (digits only, no signs)
        if (parts[1].Length == 0 || !parts[1].All(char.IsAsciiDigit) || !int.TryParse(parts[1], out var parsedPid) || parsedPid <= 0)
            return false;

        pid = parsedPid;
        return true;
    }

    /// <summary>
    /// Deletes stale update workspaces left behind by crashes, forced kills, or aborted updates.
    /// Protects active workspaces by checking both an age threshold (default 1 day) and process liveness
    /// (never deleting a workspace belonging to a running PID). Restricts cleanup strictly to Filee-owned
    /// workspace directories matching <c>.update-&lt;pid&gt;-&lt;32-hex-characters&gt;</c>.
    /// </summary>
    public static void DeleteStaleWorkspaces(string? root = null, TimeSpan? age = null, ILogger? log = null)
    {
        // Workspaces are only made on Linux, under Filee's own data folder. Elsewhere the default root is the shared
        // temp folder, where other programs' folders live.
        if (root is null && !OperatingSystem.IsLinux())
            return;
        var threshold = age ?? TimeSpan.FromDays(1);
        try
        {
            var baseDir = root ?? GetDefaultWorkspaceBaseDirectory();
            var dirInfo = new DirectoryInfo(baseDir);
            if (!dirInfo.Exists || dirInfo.LinkTarget is not null || dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return;

            foreach (var folder in dirInfo.EnumerateDirectories(".update-*"))
            {
                if (folder.LinkTarget is not null || folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;

                // Restrict cleanup strictly to Filee-owned workspace names: .update-<pid>-<32-hex-chars>
                // Foreign directories (e.g. .update-something or .update-cache) are rejected immediately.
                if (!TryParseWorkspaceName(folder.Name, out var pid))
                    continue;

                try
                {
                    if (IsProcessAlive(pid))
                        continue;

                    if (DateTime.UtcNow - folder.LastWriteTimeUtc > threshold)
                    {
                        DeleteSecureWorkspace(folder.FullName);
                        log?.LogInformation("Deleted stale update workspace {Workspace}", folder.FullName);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log?.LogWarning(ex, "Could not delete stale update workspace {Workspace}", folder.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.LogWarning(ex, "Failed to enumerate update workspaces in {Root}", root);
        }
    }
}
