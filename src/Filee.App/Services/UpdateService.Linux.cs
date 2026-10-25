// UpdateService: Linux packages. Safe extraction of the release archive and where the per-user install lives.

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
    /// <summary>
    /// Extracts a Linux release archive (.tar.gz) into <paramref name="destinationDirectory"/> with security validation
    /// (rejecting path traversal, absolute paths, unsafe entry types, directory symlinks, and escaping/component symlinks).
    /// Returns the path to the extracted payload folder containing <c>install.sh</c>.
    /// </summary>
    internal static string ExtractLinuxArchive(string archivePath, string destinationDirectory)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Archive file not found", archivePath);

        var fullDestRoot = Path.GetFullPath(destinationDirectory);
        if (!fullDestRoot.EndsWith(Path.DirectorySeparatorChar))
            fullDestRoot += Path.DirectorySeparatorChar;

        Directory.CreateDirectory(fullDestRoot);

        using (var fileStream = File.OpenRead(archivePath))
        using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
        using (var tarReader = new TarReader(gzipStream))
        {
            while (tarReader.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
                    continue;

                if (string.IsNullOrWhiteSpace(entry.Name))
                    throw new InvalidDataException("Archive contains an entry with an empty name.");

                if (Path.IsPathRooted(entry.Name) || entry.Name.StartsWith('/') || entry.Name.StartsWith('\\') || entry.Name.Contains('\\'))
                    throw new InvalidDataException($"Archive entry '{entry.Name}' has an invalid or rooted path.");

                var destinationPath = Path.GetFullPath(Path.Combine(fullDestRoot, entry.Name));
                if (!destinationPath.StartsWith(fullDestRoot, StringComparison.Ordinal))
                    throw new InvalidDataException($"Archive entry '{entry.Name}' attempts path traversal outside destination.");

                // Validate that no ancestor directory component between fullDestRoot and destinationPath is a symlink on disk
                ValidateNoSymlinkComponents(fullDestRoot, destinationPath);

                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        if (Path.Exists(destinationPath))
                        {
                            var info = new DirectoryInfo(destinationPath);
                            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                                throw new InvalidDataException($"Archive entry '{entry.Name}' conflicts with an existing symbolic link.");
                        }
                        Directory.CreateDirectory(destinationPath);
                        break;

                    case TarEntryType.RegularFile:
                    case TarEntryType.V7RegularFile:
                        if (Directory.Exists(destinationPath))
                            throw new InvalidDataException($"Archive entry '{entry.Name}' conflicts with an existing directory.");

                        var parentDir = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(parentDir))
                            Directory.CreateDirectory(parentDir);

                        // If destinationPath already exists and is a symlink, delete it rather than writing through it
                        if (Path.Exists(destinationPath))
                        {
                            var existingInfo = new FileInfo(destinationPath);
                            if (existingInfo.LinkTarget is not null || existingInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                                File.Delete(destinationPath);
                        }

                        entry.ExtractToFile(destinationPath, overwrite: true);

                        if (OperatingSystem.IsLinux())
                        {
                            var mode = (entry.Mode & RwxMask) | UnixFileMode.UserRead | UnixFileMode.UserWrite;
                            File.SetUnixFileMode(destinationPath, mode);
                        }
                        break;

                    case TarEntryType.SymbolicLink:
                        if (string.IsNullOrWhiteSpace(entry.LinkName)
                            || Path.IsPathRooted(entry.LinkName)
                            || entry.LinkName.StartsWith('/')
                            || entry.LinkName.StartsWith('\\')
                            || entry.LinkName.Contains('\\'))
                        {
                            throw new InvalidDataException($"Tar symlink '{entry.Name}' has an invalid target '{entry.LinkName}'.");
                        }

                        var linkParent = Path.GetDirectoryName(destinationPath)!;
                        var linkTarget = Path.GetFullPath(Path.Combine(linkParent, entry.LinkName));
                        if (!linkTarget.StartsWith(fullDestRoot, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException($"Tar symlink '{entry.Name}' points outside destination directory: '{entry.LinkName}'.");
                        }

                        // Directory symlinks are disallowed to prevent symlink-component traversal and link-order attacks
                        if (Directory.Exists(linkTarget))
                        {
                            throw new InvalidDataException($"Tar symlink '{entry.Name}' points to a directory, which is prohibited.");
                        }

                        if (Directory.Exists(destinationPath))
                            throw new InvalidDataException($"Archive symlink '{entry.Name}' conflicts with an existing directory.");

                        if (!string.IsNullOrEmpty(linkParent))
                            Directory.CreateDirectory(linkParent);

                        if (Path.Exists(destinationPath))
                            File.Delete(destinationPath);

                        File.CreateSymbolicLink(destinationPath, entry.LinkName);
                        break;

                    case TarEntryType.HardLink:
                        if (string.IsNullOrWhiteSpace(entry.LinkName)
                            || Path.IsPathRooted(entry.LinkName)
                            || entry.LinkName.StartsWith('/')
                            || entry.LinkName.StartsWith('\\')
                            || entry.LinkName.Contains('\\'))
                        {
                            throw new InvalidDataException($"Tar hard link '{entry.Name}' has an invalid target '{entry.LinkName}'.");
                        }

                        var hardTarget = Path.GetFullPath(Path.Combine(fullDestRoot, entry.LinkName));
                        if (!hardTarget.StartsWith(fullDestRoot, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException($"Tar hard link '{entry.Name}' points outside destination directory: '{entry.LinkName}'.");
                        }

                        if (Directory.Exists(hardTarget))
                        {
                            throw new InvalidDataException($"Tar hard link '{entry.Name}' points to a directory, which is prohibited.");
                        }

                        if (!File.Exists(hardTarget))
                        {
                            throw new InvalidDataException($"Tar hard link '{entry.Name}' target '{entry.LinkName}' does not exist.");
                        }

                        if (Directory.Exists(destinationPath))
                            throw new InvalidDataException($"Archive hard link '{entry.Name}' conflicts with an existing directory.");

                        var hardParent = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(hardParent))
                            Directory.CreateDirectory(hardParent);

                        if (Path.Exists(destinationPath))
                            File.Delete(destinationPath);

                        File.Copy(hardTarget, destinationPath, overwrite: true);
                        break;

                    default:
                        throw new InvalidDataException($"Unsupported or unsafe tar entry type '{entry.EntryType}' for entry '{entry.Name}'.");
                }
            }
        }

        var rootTrimmed = Path.TrimEndingDirectorySeparator(fullDestRoot);
        string payloadDir;
        if (File.Exists(Path.Combine(rootTrimmed, "install.sh")))
        {
            payloadDir = rootTrimmed;
        }
        else
        {
            var candidates = Directory.GetDirectories(rootTrimmed)
                .Where(d => File.Exists(Path.Combine(d, "install.sh")))
                .ToList();
            if (candidates.Count == 1)
            {
                payloadDir = candidates[0];
            }
            else
            {
                throw new InvalidDataException("Archive does not contain a single valid Filee payload with install.sh");
            }
        }

        var installSh = Path.Combine(payloadDir, "install.sh");
        var fileeExe = Path.Combine(payloadDir, "Filee");
        if (!File.Exists(installSh) || !File.Exists(fileeExe))
        {
            throw new InvalidDataException("Archive is missing install.sh or Filee executable in the payload directory.");
        }

        if (OperatingSystem.IsLinux())
        {
            EnsureExecutable(installSh);
            EnsureExecutable(fileeExe);
            var cli = Path.Combine(payloadDir, "filee-cli");
            if (File.Exists(cli))
                EnsureExecutable(cli);
            var uninstall = Path.Combine(payloadDir, "uninstall.sh");
            if (File.Exists(uninstall))
                EnsureExecutable(uninstall);
        }

        return payloadDir;
    }

    /// <summary>
    /// Verifies that no intermediate directory component between <paramref name="rootPath"/> and <paramref name="targetPath"/> is a symbolic link.
    /// </summary>
    private static void ValidateNoSymlinkComponents(string rootPath, string targetPath)
    {
        var current = rootPath.TrimEnd(Path.DirectorySeparatorChar);
        var relative = Path.GetRelativePath(current, targetPath);
        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = Path.Combine(current, segments[i]);
            if (Path.Exists(current))
            {
                var dirInfo = new DirectoryInfo(current);
                if (dirInfo.LinkTarget is not null || dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException($"Archive path component '{segments[i]}' in '{relative}' is a symbolic link.");
                }
            }
        }
    }

    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsLinux() && File.Exists(path))
        {
            var mode = (File.GetUnixFileMode(path) & RwxMask) | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(path, mode);
        }
    }

    /// <summary>Locates the installation directory on Linux.</summary>
    internal static string GetLinuxInstallDirectory()
    {
        if (IsLinuxInstalled(AppContext.BaseDirectory))
            return AppContext.BaseDirectory;
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var root = !string.IsNullOrWhiteSpace(dataHome)
            ? dataHome
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "filee", "application");
    }
}
