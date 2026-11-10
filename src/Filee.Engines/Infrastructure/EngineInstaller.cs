// Downloads, verifies and unpacks optional engines into the per-user engines folder (EngineEnvironment.DownloadRoot).
//
//  * Every download is checked against the SHA-256 pinned in engines.json before anything is unpacked.
//  * Unpacking happens in a staging folder that replaces the engine folder only when complete, so a failed or
//    cancelled install never leaves a half-installed engine behind.
//  * MSIs (LibreOffice, calibre) are unpacked with an administrative install (msiexec /a): files only, no registry
//    entries, no admin rights. Parts headless LibreOffice never uses (help, gallery, most dictionaries) are removed.
//  * conda-forge packages (Ghostscript, the Microsoft C++ runtime) are zip files holding zstd-compressed tarballs;
//    only their Windows binaries (Library/bin) are unpacked, with a managed decompressor (no 7-Zip, no installer).
//  * NSIS installers (Tesseract) are unpacked with Filee's own 7-Zip, never run: files only, no registry entries.
//  * A marker file with the component's hash records what is installed, so an engines.json update is detected.
//  * Without internet the same files can come from a folder instead (#447): saved by Filee on another computer
//    (SaveFilesAsync) or downloaded there by hand. They are copied and checked against the same SHA-256 first.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Filee.Core.Conversion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpCompress.Compressors.Xz;
using SharpCompress.Compressors.ZStandard;

namespace Filee.Engines.Infrastructure;

public enum EngineInstallStage
{
    Downloading,
    /// <summary>Copying and checking a file the user brought (<see cref="EngineInstaller.InstallFromFilesAsync"/>).</summary>
    Copying,
    Unpacking,
}

/// <summary>Progress of an install: <paramref name="Fraction"/> covers the whole package (0..1).</summary>
/// <param name="BytesDone">Bytes of the package downloaded so far (all components).</param>
/// <param name="BytesTotal">Download size of the components being installed.</param>
public readonly record struct EngineInstallProgress(EngineInstallStage Stage, string ComponentId, double Fraction, long BytesDone = 0, long BytesTotal = 0);

/// <summary>Which copy of a package the engines use, see <see cref="EngineInstaller.StatusOf"/>.</summary>
public enum EngineCopyStatus
{
    NotInstalled,
    /// <summary>Filee's download.</summary>
    Installed,
    /// <summary>The user's own copy (Settings → Engines) or one found on the PC; nothing of Filee's is used.</summary>
    OwnCopy,
}

/// <summary>The copy of a package the engines use, as Settings → Engines and 'filee engines' show it.</summary>
/// <param name="Folder">Folder of the copy in use while the status is <see cref="EngineCopyStatus.OwnCopy"/>.</param>
/// <param name="FromSystem">That copy was found on the PC (PATH, Program Files), not chosen by the user.</param>
/// <param name="Outdated">Filee's download is older than the version engines.json pins.</param>
public sealed record EngineCopy(EngineCopyStatus Status, string? Folder, bool FromSystem, bool Outdated);

/// <summary>Another Filee (the app, the CLI or a second copy) is installing or removing the same package.</summary>
public sealed class EngineBusyException(string packageId)
    : IOException($"{packageId} is being installed by another Filee. Try again when it has finished.")
{
    public string PackageId { get; } = packageId;
}

/// <summary>Installs and removes <see cref="EnginePackage"/>s.</summary>
public sealed class EngineInstaller
{
    private const string MarkerName = ".filee-component";
    private static readonly TimeSpan MsiTimeout = TimeSpan.FromMinutes(15);

    /// <summary>Tries before a file held open counts as in use: about 2 seconds, for holds as brief as a virus scan's.</summary>
    private const int LockAttempts = 5;
    private static readonly TimeSpan LockWait = TimeSpan.FromMilliseconds(200);

    /// <summary>What <see cref="DeleteFolder"/> makes writable: hidden and system files too, but not through links.</summary>
    private static readonly EnumerationOptions EveryEntry = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    private readonly string _root;
    private readonly HttpClient _http;
    private readonly IReadOnlyDictionary<string, EngineComponent> _components;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<EnginePackage> _packages;

    /// <param name="root">Folder that receives one sub folder per engine (default: EngineEnvironment.DownloadRoot).</param>
    /// <param name="components">Download list; engines.json when null (tests pass their own).</param>
    /// <param name="packages">Known packages (tests pass their own); EngineDownloads.Packages when null.</param>
    public EngineInstaller(HttpClient http, string? root = null, IReadOnlyDictionary<string, EngineComponent>? components = null, ILogger? logger = null, IReadOnlyList<EnginePackage>? packages = null)
    {
        _http = http;
        _root = root ?? EngineEnvironment.DownloadRoot;
        _components = components ?? EngineDownloads.Components;
        _logger = logger ?? NullLogger.Instance;
        _packages = packages ?? EngineDownloads.Packages;
    }

    /// <summary>
    /// The client to create an installer with: automatic redirects off, because the installer follows them itself
    /// (mirrors may use plain HTTP; every file is checked against its SHA-256), and Filee's user agent.
    /// </summary>
    public static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Filee/{typeof(EngineInstaller).Assembly.GetName().Version}");
        return http;
    }

    /// <summary>Folder that receives one sub folder per engine.</summary>
    public string Root => _root;

    /// <summary>The downloads this installer knows: engines.json for this computer unless a test passed its own.</summary>
    public IReadOnlyDictionary<string, EngineComponent> Components => _components;

    /// <summary>
    /// The copy the engines use, in the order they look: the user's own copy, Filee's download, a copy on the PC
    /// (only looked for when neither of the others is there).
    /// </summary>
    public EngineCopy StatusOf(EnginePackage package)
    {
        var installed = IsInstalled(package);
        var own = EngineEnvironment.OwnCopyFolder(package.Id);
        var system = own is null && !installed ? EngineEnvironment.SystemCopyFolder(package.Id) : null;
        var folder = own ?? system;
        var status = folder is not null ? EngineCopyStatus.OwnCopy
            : installed ? EngineCopyStatus.Installed
            : EngineCopyStatus.NotInstalled;
        return new EngineCopy(status, folder, system is not null, folder is null && installed && !IsUpToDate(package));
    }

    /// <summary>True when every component is installed (any version).</summary>
    public bool IsInstalled(EnginePackage package) => package.Components.Count > 0 && package.Components.All(c => _components.ContainsKey(c) && Marker(c) is not null);

    /// <summary>True when every component is installed in the version engines.json pins.</summary>
    public bool IsUpToDate(EnginePackage package) =>
        package.Components.Count > 0 &&
        package.Components.All(c => _components.TryGetValue(c, out var component)
            && string.Equals(Marker(c), component.Sha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>Downloads and unpacks the components that are missing or outdated.</summary>
    public Task InstallAsync(EnginePackage package, IProgress<EngineInstallProgress>? progress, CancellationToken cancellationToken) =>
        InstallAsync(package, null, progress, cancellationToken);

    /// <summary>
    /// Installs the components that are missing or outdated from files the user brought instead of downloading them
    /// (#447): <paramref name="paths"/> are those files, or folders holding them (not their sub folders). Each
    /// component is the file of its exact download size, preferably with its download's name, and is checked against
    /// its SHA-256 like a download. Nothing is installed unless every component needed is there and matches.
    /// </summary>
    /// <exception cref="ConversionRefusedException">
    /// error.engine_files_missing (the detail names the files to bring) or error.engine_file_mismatch.
    /// </exception>
    public Task InstallFromFilesAsync(EnginePackage package, IReadOnlyList<string> paths, IProgress<EngineInstallProgress>? progress,
        CancellationToken cancellationToken) =>
        InstallAsync(package, paths, progress, cancellationToken);

    private async Task InstallAsync(EnginePackage package, IReadOnlyList<string>? localPaths, IProgress<EngineInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (package.Components.Any(c => !_components.ContainsKey(c)))
            throw new PlatformNotSupportedException(EngineDownloads.UnavailableReason(package)
                ?? $"The {package.Id} package is unavailable on this platform.");
        using var packageLock = LockPackage(package);
        ClearAside(package.Components);
        var pending = package.Components
            .Where(c => !string.Equals(Marker(c), _components[c].Sha256, StringComparison.OrdinalIgnoreCase))
            .Select(c => _components[c])
            .ToList();
        var local = localPaths is null ? null : FindFiles(pending, localPaths);
        var total = Math.Max(1, pending.Sum(c => c.Size));
        Directory.CreateDirectory(_root);

        // The user's files are all copied and checked before the first is unpacked, so a wrong one leaves the package
        // as it was. Downloads are unpacked one by one as they arrive.
        var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            long copied = 0;
            foreach (var component in local is null ? [] : pending)
            {
                var offset = copied;
                copies[component.Id] = await DownloadAsync(component, local![component.Id],
                    bytes => progress?.Report(new EngineInstallProgress(EngineInstallStage.Copying, component.Id,
                        Math.Min(1, (offset + bytes) / (double)total), offset + bytes, total)),
                    cancellationToken);
                copied += component.Size;
            }

            long done = 0;
            foreach (var component in pending)
            {
                var offset = done;
                var file = copies.GetValueOrDefault(component.Id) ?? await DownloadAsync(component, null,
                    bytes => progress?.Report(new EngineInstallProgress(EngineInstallStage.Downloading, component.Id,
                        Math.Min(1, (offset + bytes) / (double)total), offset + bytes, total)),
                    cancellationToken);
                try
                {
                    // After copying the user's files the bar is already full; it doesn't go back for unpacking.
                    progress?.Report(new EngineInstallProgress(EngineInstallStage.Unpacking, component.Id,
                        local is null ? Math.Min(1, (offset + component.Size) / (double)total) : 1, offset + component.Size, total));
                    await UnpackAsync(component, file, cancellationToken);
                }
                finally
                {
                    TryDelete(file);
                    copies.Remove(component.Id);
                }
                done += component.Size;
                _logger.LogInformation("Installed engine component {Component} {Version}", component.Id, component.Version);
            }
        }
        finally
        {
            foreach (var copy in copies.Values)
                TryDelete(copy);
        }
        if (pending.Count > 0)
            CopyRuntime(package);
    }

    /// <summary>
    /// Removes the package's folders. Fails if an engine is running (its files are locked), and then leaves the package
    /// as it was: each folder is first moved aside to a "~" name (on Windows that fails while a file in it is open), and
    /// only once all of them are aside are they deleted. A delete Windows cuts short leaves a "~" folder, which no engine
    /// reads and the next install or removal clears (#963).
    /// </summary>
    public void Uninstall(EnginePackage package)
    {
        using var packageLock = LockPackage(package);
        var shared = _packages
            .Where(p => p.Id != package.Id && IsInstalled(p))
            .SelectMany(p => p.Components)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var componentsToRemove = package.Components.Where(c => !shared.Contains(c)).ToList();

        ClearAside(componentsToRemove);
        var aside = new List<(string Folder, string Moved)>();
        try
        {
            foreach (var folder in componentsToRemove.Select(FolderOf).Distinct())
            {
                var folderName = Path.GetFileName(folder);
                if (!Directory.Exists(folder))
                    continue;
                var moved = AsideFolder(folderName);
                Directory.Move(folder, moved);
                aside.Add((folder, moved));
            }
        }
        catch
        {
            // One folder is in use: put back the ones already moved, so the package stays whole and installed.
            foreach (var (folder, moved) in aside)
                Directory.Move(moved, folder);
            throw;
        }
        foreach (var (_, moved) in aside)
            TryDeleteFolder(moved);
    }

    /// <summary>Start of the name of a folder an earlier removal or update moved aside but couldn't delete.</summary>
    private const string AsidePrefix = "~removed-";

    private string AsideFolder(string id) => Path.Combine(_root, $"{AsidePrefix}{id}-{Guid.NewGuid().ToString("N")[..8]}");

    /// <summary>Deletes what an earlier removal of components left aside (under the package's lock).</summary>
    private void ClearAside(IEnumerable<string> components)
    {
        if (!Directory.Exists(_root))
            return;
        foreach (var folder in components.Select(FolderOf).Distinct())
            foreach (var asideFolder in Directory.EnumerateDirectories(_root, $"{AsidePrefix}{Path.GetFileName(folder)}-*"))
                TryDeleteFolder(asideFolder);
    }

    /// <summary>Deletes a folder that is only in the way: a failure is logged and never replaces another error (#1092).</summary>
    private void TryDeleteFolder(string folder)
    {
        try
        {
            DeleteFolder(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder moved aside is left for the next install or removal (ClearAside).
            _logger.LogWarning(ex, "Could not delete {Folder}", folder);
        }
    }

    /// <summary>
    /// Deletes a folder tree, read-only files included: Directory.Delete stops at the first one, and the calibre MSI
    /// marks every file read-only (#1092). A file held briefly (an antivirus scanning what was just unpacked) is waited out.
    /// Links are never followed: of a link only the link goes, also when <paramref name="folder"/> itself is one, and what
    /// it leads to stays as it is.
    /// </summary>
    internal static void DeleteFolder(string folder) => WaitingOutLocks(() =>
    {
        var root = new DirectoryInfo(folder);
        if (!root.Exists)
            return;
        if (root.LinkTarget is not null)
        {
            root.Delete(recursive: false);
            return;
        }
        foreach (var entry in root.EnumerateFileSystemInfos("*", EveryEntry))
        {
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                entry.Attributes &= ~FileAttributes.ReadOnly;
        }
        Directory.Delete(folder, recursive: true);
    });

    /// <summary>Runs an action on files that may be held briefly, trying again until the last attempt's error stands.</summary>
    private static void WaitingOutLocks(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < LockAttempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(LockWait * attempt);
            }
        }
    }

    /// <summary>
    /// Holds the package's lock file (.downloads/locks/&lt;id&gt;.lock) while it is installed or removed, so two Filee
    /// processes never write the same download or replace the same folder at once. The file stays; the open handle
    /// is the lock (deleting it on close would let a third process lock a new file while a second still holds the old).
    /// </summary>
    /// <exception cref="EngineBusyException">Another process holds the lock.</exception>
    private IDisposable LockPackage(EnginePackage package)
    {
        var locks = Path.Combine(_root, ".downloads", "locks");
        Directory.CreateDirectory(locks);
        var streams = new List<FileStream>();
        try
        {
            var keys = new[] { package.Id }.Concat(package.Components).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.Ordinal);
            foreach (var key in keys)
            {
                try
                {
                    streams.Add(new FileStream(Path.Combine(locks, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
                catch (IOException ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException))
                {
                    throw new EngineBusyException(package.Id);
                }
            }
            return new CompositeLock(streams);
        }
        catch
        {
            foreach (var s in streams)
                s.Dispose();
            throw;
        }
    }

    private sealed class CompositeLock(List<FileStream> streams) : IDisposable
    {
        public void Dispose()
        {
            foreach (var s in streams)
                s.Dispose();
        }
    }

    // ───────────────────────── Download ─────────────────────────

    /// <summary>The download in .downloads, or a copy of <paramref name="localFile"/> there: checked either way.</summary>
    private async Task<string> DownloadAsync(EngineComponent component, string? localFile, Action<long> reportBytes, CancellationToken cancellationToken)
    {
        var downloads = Path.Combine(_root, ".downloads");
        Directory.CreateDirectory(downloads);
        var file = Path.Combine(downloads, $"{component.Id}-{component.Version}{Path.GetExtension(new Uri(component.Url).AbsolutePath)}");
        await FetchAsync(component, localFile, file, reportBytes, cancellationToken);
        return file;
    }

    /// <summary>
    /// Writes the component to <paramref name="file"/> (through a .part file) from its URL, or from
    /// <paramref name="localFile"/>, and keeps it only when its SHA-256 is the pinned one.
    /// </summary>
    private async Task FetchAsync(EngineComponent component, string? localFile, string file, Action<long> reportBytes, CancellationToken cancellationToken)
    {
        var partial = file + ".part";
        using var response = localFile is null ? await GetFollowingRedirectsAsync(new Uri(component.Url), cancellationToken) : null;
        response?.EnsureSuccessStatusCode();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await CopyAsync();
        }
        catch
        {
            // Cancelled or cut off: the partial download (up to ~420 MB for LibreOffice) isn't kept (#963).
            TryDelete(partial);
            throw;
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(actual, component.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(partial);
            if (localFile is not null)
                throw new ConversionRefusedException("error.engine_file_mismatch", Path.GetFileName(localFile));
            throw new InvalidDataException($"{component.Id}: checksum mismatch (expected {component.Sha256}, got {actual}). The download was discarded.");
        }
        File.Move(partial, file, overwrite: true);

        async Task CopyAsync()
        {
            await using var source = response is not null
                ? await response.Content.ReadAsStreamAsync(cancellationToken)
                : new FileStream(localFile!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            await using var target = File.Create(partial);
            var buffer = new byte[1 << 16];
            long received = 0;
            var lastReport = Stopwatch.StartNew();
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                // A file that has grown since it was picked by its size isn't the download: no need to read on.
                if (localFile is not null && received + read > component.Size)
                    break;
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                received += read;
                if (lastReport.ElapsedMilliseconds > 150)
                {
                    reportBytes(received);
                    lastReport.Restart();
                }
            }
            reportBytes(received);
        }
    }

    /// <summary>
    /// The file each component is taken from: one of its exact download size (engines.json pins it next to the hash),
    /// the one with its download's name when several have that size. Of a folder only the top level is looked at.
    /// </summary>
    /// <exception cref="ConversionRefusedException">error.engine_files_missing, naming the download of each missing component.</exception>
    internal static Dictionary<string, string> FindFiles(IReadOnlyList<EngineComponent> components, IReadOnlyList<string> paths)
    {
        var files = new List<FileInfo>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                files.AddRange(new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true }));
            else if (File.Exists(path))
                files.Add(new FileInfo(path));
        }
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var component in components)
        {
            var name = EngineDownloads.FileName(component);
            var match = files.Where(f => f.Length == component.Size)
                .OrderByDescending(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (match is null)
                missing.Add(name);
            else
                found[component.Id] = match.FullName;
        }
        if (missing.Count > 0)
            throw new ConversionRefusedException("error.engine_files_missing", string.Join(", ", missing));
        return found;
    }

    /// <summary>
    /// Saves every component of the package in <paramref name="folder"/> under its download's name, checked against
    /// its SHA-256, for <see cref="InstallFromFilesAsync"/> on a computer without internet (#447). What is installed
    /// here was unpacked and trimmed, so the downloads are fetched again; a file in the folder that matches already
    /// is kept. Returns the files.
    /// </summary>
    public async Task<IReadOnlyList<string>> SaveFilesAsync(EnginePackage package, string folder, IProgress<EngineInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (package.Components.Count == 0 || package.Components.Any(c => !_components.ContainsKey(c)))
            throw new PlatformNotSupportedException(EngineDownloads.UnavailableReason(package)
                ?? $"The {package.Id} package is unavailable on this platform.");
        Directory.CreateDirectory(folder);
        var components = package.Components.Select(c => _components[c]).ToList();
        var total = Math.Max(1, components.Sum(c => c.Size));
        long done = 0;
        var saved = new List<string>();
        foreach (var component in components)
        {
            var file = Path.Combine(folder, EngineDownloads.FileName(component));
            if (!await MatchesAsync(file, component, cancellationToken))
            {
                var offset = done;
                await FetchAsync(component, null, file,
                    bytes => progress?.Report(new EngineInstallProgress(EngineInstallStage.Downloading, component.Id,
                        Math.Min(1, (offset + bytes) / (double)total), offset + bytes, total)),
                    cancellationToken);
                _logger.LogInformation("Saved engine component {Component} {Version} to {File}", component.Id, component.Version, file);
            }
            done += component.Size;
            progress?.Report(new EngineInstallProgress(EngineInstallStage.Downloading, component.Id, Math.Min(1, done / (double)total), done, total));
            saved.Add(file);
        }
        return saved;
    }

    /// <summary>The file is there with the component's size and SHA-256.</summary>
    private static async Task<bool> MatchesAsync(string file, EngineComponent component, CancellationToken cancellationToken)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length != component.Size)
            return false;
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return string.Equals(Convert.ToHexString(hash), component.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Follows redirects itself: download sites send users to mirrors, some of them plain HTTP, which HttpClient
    /// never follows from HTTPS. That is safe here because every file is checked against its pinned SHA-256.
    /// The HttpClient should be created with automatic redirects off.
    /// </summary>
    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(Uri url, CancellationToken cancellationToken)
    {
        for (var hop = 0; ; hop++)
        {
            var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location && hop < 10)
            {
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                response.Dispose();
                continue;
            }
            return response;
        }
    }

    // ───────────────────────── Unpack ─────────────────────────

    /// <summary>
    /// Runs <paramref name="unpack"/> on a new staging folder and removes what is left of it. The error that made
    /// unpacking fail is the one the user sees: a cleanup that fails too doesn't replace it (#1092).
    /// </summary>
    internal async Task InStagingAsync(Func<string, Task> unpack)
    {
        // Short name: LibreOffice's own folder structure is deep, and msiexec fails beyond MAX_PATH (260 chars).
        var staging = Path.Combine(_root, "~" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            await unpack(staging);
        }
        finally
        {
            TryDeleteFolder(staging);
        }
    }

    private Task UnpackAsync(EngineComponent component, string file, CancellationToken cancellationToken) =>
        InStagingAsync(async staging =>
        {
            switch (component.Kind)
            {
                case "zip":
                    ZipFile.ExtractToDirectory(file, staging);
                    RenameExifTool(component.Id, SingleRoot(staging));
                    EnsureExecutablePrograms(component.Id, SingleRoot(staging));
                    Replace(component.Id, SingleRoot(staging));
                    break;
                case "tar.gz":
                case "tar.xz":
                    await UnpackTarAsync(file, staging, component.Kind, cancellationToken);
                    EnsureExecutablePrograms(component.Id, SingleRoot(staging));
                    Replace(component.Id, SingleRoot(staging));
                    break;
                case "dmg":
                    await UnpackDiskImageAsync(component.Id, file, staging, cancellationToken);
                    Replace(component.Id, staging);
                    break;
                case "msi":
                    await UnpackMsiAsync(component.Id, file, staging, Path.Combine(Path.GetTempPath(), "filee-msiexec.log"), cancellationToken);
                    Replace(component.Id, MsiProgramFolder(component.Id, staging));
                    break;
                case "conda":
                    UnpackConda(file, staging, cancellationToken);
                    Replace(component.Id, staging);
                    break;
                case "traineddata":
                    // One Tesseract language (OcrLanguages): the file as it is, in a folder of its own.
                    Directory.CreateDirectory(staging);
                    File.Copy(file, Path.Combine(staging, component.Id + ".traineddata"));
                    Replace(component.Id, staging);
                    break;
                case "ncnn-param":
                case "ncnn-bin":
                    var ncnnTarget = FolderOf(component.Id);
                    Directory.CreateDirectory(ncnnTarget);
                    var ncnnFile = Path.GetFileName(new Uri(component.Url).LocalPath);
                    File.Copy(file, Path.Combine(ncnnTarget, ncnnFile), overwrite: true);
                    File.WriteAllText(Path.Combine(ncnnTarget, "." + component.Id), component.Sha256);
                    return;
                case "model":
                case "bin":
                    // Standalone AI model file (e.g. Whisper ggml-base.bin): copied directly into its component folder.
                    Directory.CreateDirectory(staging);
                    var modelFileName = Path.GetFileName(new Uri(component.Url).LocalPath);
                    File.Copy(file, Path.Combine(staging, modelFileName));
                    Replace(component.Id, staging);
                    break;
                case "nsis":
                    var sevenZip = Archives.SevenZip.Locate()
                                   ?? throw new InvalidOperationException($"{component.Id} needs Filee's 7-Zip, which is missing.");
                    Directory.CreateDirectory(staging);
                    await sevenZip.ExtractAsync(file, staging, [], _root, cancellationToken);
                    TrimTesseract(staging);
                    Replace(component.Id, staging);
                    break;
                case "oxt":
                    // LibreOffice registers bundled extensions (plain folders under share/extensions) on start-up.
                    var extensions = Path.Combine(FolderOf("libreoffice"), "share", "extensions");
                    if (!Directory.Exists(extensions))
                        throw new InvalidOperationException($"{component.Id} needs LibreOffice, which is not installed.");
                    ZipFile.ExtractToDirectory(file, staging);
                    Replace(component.Id, staging);
                    break;
                default:
                    throw new NotSupportedException($"Unknown engine package kind '{component.Kind}'.");
            }
            File.WriteAllText(Path.Combine(FolderOf(component.Id), MarkerName), component.Sha256);
        });

    internal static async Task UnpackTarAsync(string file, string target, string kind, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);
        await using var input = File.OpenRead(file);
        await using Stream content = kind switch
        {
            "tar.gz" => new GZipStream(input, CompressionMode.Decompress),
            "tar.xz" => new XZStream(input),
            _ => throw new NotSupportedException(kind),
        };
        await TarFile.ExtractToDirectoryAsync(content, target, overwriteFiles: false, cancellationToken);
    }

    /// <summary>
    /// ExifTool's Windows build ships as "exiftool(-k).exe", which waits for a key before it exits (for
    /// double-clicking): renamed "exiftool.exe", it runs and exits as a command line program.
    /// </summary>
    internal static void RenameExifTool(string id, string root)
    {
        var waiting = Path.Combine(root, "exiftool(-k).exe");
        if (id == "exiftool" && File.Exists(waiting))
            File.Move(waiting, Path.Combine(root, "exiftool.exe"), overwrite: true);
    }

    internal static void EnsureExecutablePrograms(string id, string root)
    {
        var folder = RequiredProgramFolder(id, root, OperatingSystem.IsWindows());
        if (OperatingSystem.IsWindows() || folder is null)
            return;
        foreach (var program in EngineEnvironment.OwnCopyPrograms[id])
        {
            var path = Path.Combine(folder, program);
            File.SetUnixFileMode(path, File.GetUnixFileMode(path)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    internal static string? RequiredProgramFolder(string id, string root, bool windows)
    {
        if (!EngineEnvironment.OwnCopyPrograms.ContainsKey(id))
            return null;
        return EngineEnvironment.FolderWithPrograms(id, root, windows)
            ?? throw new InvalidDataException($"The {id} package is missing required executable programs in its expected folders.");
    }

    private async Task UnpackDiskImageAsync(string id, string file, string target, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Disk image packages can only be installed on macOS.");
        var appName = id switch
        {
            "libreoffice" => "LibreOffice.app",
            "calibre" => "calibre.app",
            _ => throw new NotSupportedException($"No application bundle is specified for {id}."),
        };
        // Keep the mount outside staging so failed unmounts cannot be removed by staging cleanup.
        var mount = Path.Combine(_root, ".mount-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mount);
        var attached = false;
        try
        {
            var attach = await ProcessRunner.RunAsync("/usr/bin/hdiutil",
                ["attach", "-readonly", "-nobrowse", "-mountpoint", mount, file], TimeSpan.FromMinutes(3), cancellationToken);
            if (attach.ExitCode != 0)
                throw new InvalidOperationException($"Could not open {id} disk image: {attach.StandardError.Trim()}");
            attached = true;
            var app = Path.Combine(mount, appName);
            if (!Directory.Exists(app) || new DirectoryInfo(app).LinkTarget is not null)
                throw new InvalidDataException($"{appName} is missing from the disk image.");
            Directory.CreateDirectory(target);
            var copy = await ProcessRunner.RunAsync("/usr/bin/ditto", [app, Path.Combine(target, appName)],
                TimeSpan.FromMinutes(10), cancellationToken);
            if (copy.ExitCode != 0)
                throw new InvalidOperationException($"Could not copy {appName}: {copy.StandardError.Trim()}");
        }
        finally
        {
            var detach = await ProcessRunner.RunAsync("/usr/bin/hdiutil", ["detach", mount],
                TimeSpan.FromSeconds(30), CancellationToken.None);
            if (detach.ExitCode != 0 && attached)
                throw new IOException($"Could not unmount the engine disk image at {mount}: {detach.StandardError.Trim()}");
            Directory.Delete(mount);
        }
    }

    /// <summary>
    /// The folder to keep from an administratively installed MSI, which lays files out as they would be installed
    /// (PFiles64\...): LibreOffice's folder with program/ and share/ (trimmed, with its C++ runtime copied into
    /// program/), calibre's folder with ebook-convert (it keeps its own C++ runtime in app/bin).
    /// </summary>
    private static string MsiProgramFolder(string id, string staging)
    {
        if (id == "calibre")
        {
            var convert = Directory.EnumerateFiles(staging, "ebook-convert.exe", SearchOption.AllDirectories).FirstOrDefault()
                          ?? throw new InvalidDataException("ebook-convert.exe not found in the calibre package.");
            return Path.GetDirectoryName(convert)!;
        }
        var soffice = Directory.EnumerateFiles(staging, "soffice.exe", SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new InvalidDataException("soffice.exe not found in the LibreOffice package.");
        var libreOffice = Path.GetDirectoryName(Path.GetDirectoryName(soffice)!)!; // folder with program/, share/
        TrimLibreOffice(libreOffice);
        AppLocalRuntime.CopyLibreOfficeRuntime(libreOffice);
        return libreOffice;
    }

    private async Task UnpackMsiAsync(string id, string msi, string target, string log, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException($"The {id} package is a Windows installer.");
        Directory.CreateDirectory(target);
        // The Installer service won't follow a junction a user made (#527); Filee keeps using its own paths.
        // The log too: through a link it can't be written (1622), as when %TEMP% has moved with AppData\Local.
        msi = RealPath.Of(msi);
        target = RealPath.Of(target);
        log = Path.Combine(RealPath.Of(Path.GetDirectoryName(log)!), Path.GetFileName(log));
        _logger.LogInformation("Unpacking {Component} with msiexec: {Msi} to {Target}, log {Log}", id, msi, target, log);
        // msiexec parses its own command line: the property value must be quoted as TARGETDIR="...", so the
        // argument list escaping of ProcessRunner cannot be used here.
        using var process = Process.Start(new ProcessStartInfo("msiexec.exe", $"/a \"{msi}\" /qn /l*v \"{log}\" TARGETDIR=\"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("msiexec could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MsiTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        if (process.ExitCode != 0)
            throw MsiFailure(id, process.ExitCode, log);
        // The verbose log is only worth keeping when unpacking failed (the error names it); else it would stay in %TEMP%.
        try
        {
            File.Delete(log);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>What the Engines card shows: the reasons a user can act on in their language (error.* keys).</summary>
    internal static Exception MsiFailure(string id, int exitCode, string log) => exitCode switch
    {
        1618 => new ConversionRefusedException("error.msi_busy"),
        1620 => new ConversionRefusedException("error.msi_link", $"msiexec 1620, log: {log}"),
        _ => new InvalidOperationException($"Unpacking {id} failed (msiexec exit code {exitCode}, log: {log})."),
    };

    /// <summary>
    /// Removes what headless conversion never uses (~500 MB): help, readmes, the MSI copy, the gallery and spelling
    /// dictionaries except English and Korean (their hyphenation patterns affect page layout in PDF export).
    /// </summary>
    internal static void TrimLibreOffice(string root)
    {
        foreach (var folder in new[] { "help", "readmes", Path.Combine("share", "gallery") })
        {
            var path = Path.Combine(root, folder);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        foreach (var msi in Directory.EnumerateFiles(root, "*.msi"))
            File.Delete(msi);
        var extensions = Path.Combine(root, "share", "extensions");
        if (Directory.Exists(extensions))
        {
            foreach (var dictionary in Directory.EnumerateDirectories(extensions, "dict-*"))
            {
                if (Path.GetFileName(dictionary) is not ("dict-en" or "dict-ko"))
                    Directory.Delete(dictionary, recursive: true);
            }
        }
    }

    /// <summary>
    /// Removes what OCR never uses (~80 MB): the training programs and their manuals, the installer's own files and
    /// the bundled language data (Filee downloads its own, see OcrLanguages). tesseract.exe, its DLLs and the
    /// licenses in doc/ stay.
    /// </summary>
    internal static void TrimTesseract(string root)
    {
        foreach (var folder in new[] { "$PLUGINSDIR", "tessdata" })
        {
            var path = Path.Combine(root, folder);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        foreach (var file in Directory.EnumerateFiles(root, "*.exe").Concat(Directory.EnumerateFiles(root, "*.html")).ToList())
        {
            if (!string.Equals(Path.GetFileName(file), "tesseract.exe", StringComparison.OrdinalIgnoreCase))
                File.Delete(file);
        }
    }

    /// <summary>
    /// Unpacks the Windows binaries of a conda package (.conda = zip with pkg-*.tar.zst): the files under Library/bin
    /// land directly in <paramref name="target"/>. Resources such as Ghostscript's fonts are compiled into its DLL.
    /// </summary>
    internal static void UnpackConda(string condaFile, string target, CancellationToken cancellationToken)
    {
        const string prefix = "Library/bin/";
        using var zip = ZipFile.OpenRead(condaFile);
        var payload = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("pkg-", StringComparison.Ordinal) && e.FullName.EndsWith(".tar.zst", StringComparison.Ordinal))
                      ?? throw new InvalidDataException("Not a conda package (pkg-*.tar.zst is missing).");
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        var count = 0;
        using (var zstd = new DecompressionStream(payload.Open(), leaveOpen: false))
        using (var tar = new TarReader(zstd))
        {
            while (tar.GetNextEntry() is { } entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = entry.Name.Replace('\\', '/');
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || !name.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                var path = Path.GetFullPath(Path.Combine(target, name[prefix.Length..]));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Unsafe path in conda package: {entry.Name}");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: true);
                count++;
            }
        }
        if (count == 0)
            throw new InvalidDataException("The conda package contains no Windows binaries (Library/bin).");
    }

    /// <summary>
    /// App-local Microsoft C++ runtime: when a package includes runtime components ("vcruntime", "vcomp14"), their DLLs
    /// are copied next to the programs of the package's other conda or zip components. Windows looks for DLLs in a
    /// program's folder before System32, so the runtime a program was built for is used even when the PC has none or an older one.
    /// </summary>
    private static readonly string[] RuntimeComponents = ["vcruntime", "vcomp14"];

    private void CopyRuntime(EnginePackage package)
    {
        var runtimeDirs = RuntimeComponents
            .Where(r => package.Components.Contains(r))
            .Select(FolderOf)
            .Where(Directory.Exists)
            .ToList();
        if (runtimeDirs.Count == 0)
            return;

        var dlls = runtimeDirs.SelectMany(dir => Directory.GetFiles(dir, "*.dll")).ToList();
        foreach (var id in package.Components.Where(c => !RuntimeComponents.Contains(c) && (_components[c].Kind is "conda" or "zip")))
        {
            var targetDir = FolderOf(id);
            if (!Directory.Exists(targetDir))
                continue;
            foreach (var dll in dlls)
                File.Copy(dll, Path.Combine(targetDir, Path.GetFileName(dll)), overwrite: true);
        }
    }

    /// <summary>An archive with a single top-level folder is unpacked without that extra level.</summary>
    private static string SingleRoot(string folder)
    {
        var entries = Directory.GetFileSystemEntries(folder);
        return entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : folder;
    }

    /// <summary>
    /// Makes <paramref name="source"/> the engine folder, replacing a previous version. Like <see cref="Uninstall"/>, it
    /// moves that version aside first: while an engine runs the move fails and the working copy keeps its marker, where
    /// a delete in place took the marker first and left a half-deleted engine that looked not installed (#1092).
    /// </summary>
    private void Replace(string id, string source)
    {
        RequiredProgramFolder(id, source, _components[id].Kind is "msi" or "conda" or "nsis" || OperatingSystem.IsWindows());
        var target = FolderOf(id);
        var previous = Directory.Exists(target) ? AsideFolder(id) : null;
        if (previous is not null)
            WaitingOutLocks(() => Directory.Move(target, previous));
        try
        {
            WaitingOutLocks(() => Directory.Move(source, target));
        }
        catch
        {
            if (previous is not null)
                Directory.Move(previous, target);
            throw;
        }
        if (previous is not null)
            TryDeleteFolder(previous);
    }

    // ───────────────────────── Layout ─────────────────────────

    /// <summary>Folder of a component; the LibreOffice extension lives inside LibreOffice.</summary>
    private string FolderOf(string id) => _components.TryGetValue(id, out var component) && component.Kind == "oxt"
        ? Path.Combine(_root, "libreoffice", "share", "extensions", ExtensionFolder(id))
        : _components.TryGetValue(id, out var c) && c.Kind is "ncnn-param" or "ncnn-bin"
            ? Path.Combine(_root, NcnnModelFolder(id))
            : Path.Combine(_root, id);

    private static string NcnnModelFolder(string id) =>
        id.EndsWith("-param", StringComparison.Ordinal) ? id[..^6]
        : id.EndsWith("-bin", StringComparison.Ordinal) ? id[..^4]
        : id;

    private static string ExtensionFolder(string id) => id switch
    {
        "h2orestart" => "H2Orestart",
        _ => id,
    };

    /// <summary>Hash of the installed version, or null when not installed.</summary>
    private string? Marker(string id)
    {
        if (_components.TryGetValue(id, out var component) && component.Kind is "ncnn-param" or "ncnn-bin")
        {
            var componentMarker = Path.Combine(FolderOf(id), "." + id);
            return File.Exists(componentMarker) ? File.ReadAllText(componentMarker).Trim() : null;
        }
        var marker = Path.Combine(FolderOf(id), MarkerName);
        return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
