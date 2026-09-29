// Downloads, verifies and unpacks optional engines into the per-user engines folder (EngineEnvironment.DownloadRoot).
//
//  * Every download is checked against the SHA-256 pinned in engines.json before anything is unpacked.
//  * Unpacking happens in a staging folder that replaces the engine folder only when complete, so a failed or
//    cancelled install never leaves a half-installed engine behind.
//  * The LibreOffice MSI is unpacked with an administrative install (msiexec /a): files only, no registry entries,
//    no admin rights. Parts headless conversion never uses (help, gallery, most dictionaries) are removed.
//  * A marker file with the component's hash records what is installed, so an engines.json update is detected.

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Engines.Infrastructure;

public enum EngineInstallStage
{
    Downloading,
    Unpacking,
}

/// <summary>Progress of an install: <paramref name="Fraction"/> covers the whole package (0..1).</summary>
public readonly record struct EngineInstallProgress(EngineInstallStage Stage, string ComponentId, double Fraction);

/// <summary>Installs and removes <see cref="EnginePackage"/>s.</summary>
public sealed class EngineInstaller
{
    private const string MarkerName = ".filee-component";
    private static readonly TimeSpan MsiTimeout = TimeSpan.FromMinutes(15);

    private readonly string _root;
    private readonly HttpClient _http;
    private readonly IReadOnlyDictionary<string, EngineComponent> _components;
    private readonly ILogger _logger;

    /// <param name="root">Folder that receives one sub folder per engine (default: EngineEnvironment.DownloadRoot).</param>
    /// <param name="components">Download list; engines.json when null (tests pass their own).</param>
    public EngineInstaller(HttpClient http, string? root = null, IReadOnlyDictionary<string, EngineComponent>? components = null, ILogger? logger = null)
    {
        _http = http;
        _root = root ?? EngineEnvironment.DownloadRoot;
        _components = components ?? EngineDownloads.Components;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>True when every component is installed (any version).</summary>
    public bool IsInstalled(EnginePackage package) => package.Components.All(c => Marker(c) is not null);

    /// <summary>True when every component is installed in the version engines.json pins.</summary>
    public bool IsUpToDate(EnginePackage package) =>
        package.Components.All(c => string.Equals(Marker(c), _components[c].Sha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>Downloads and unpacks the components that are missing or outdated.</summary>
    public async Task InstallAsync(EnginePackage package, IProgress<EngineInstallProgress>? progress, CancellationToken cancellationToken)
    {
        var pending = package.Components
            .Where(c => !string.Equals(Marker(c), _components[c].Sha256, StringComparison.OrdinalIgnoreCase))
            .Select(c => _components[c])
            .ToList();
        var total = Math.Max(1, pending.Sum(c => c.Size));
        long done = 0;
        Directory.CreateDirectory(_root);

        foreach (var component in pending)
        {
            var offset = done;
            var file = await DownloadAsync(component,
                bytes => progress?.Report(new EngineInstallProgress(EngineInstallStage.Downloading, component.Id, Math.Min(1, (offset + bytes) / (double)total))),
                cancellationToken);
            try
            {
                progress?.Report(new EngineInstallProgress(EngineInstallStage.Unpacking, component.Id, Math.Min(1, (offset + component.Size) / (double)total)));
                await UnpackAsync(component, file, cancellationToken);
            }
            finally
            {
                TryDelete(file);
            }
            done += component.Size;
            _logger.LogInformation("Installed engine component {Component} {Version}", component.Id, component.Version);
        }
    }

    /// <summary>Removes the package's folders. Fails if an engine is running (its files are locked).</summary>
    public void Uninstall(EnginePackage package)
    {
        foreach (var id in package.Components)
        {
            var folder = FolderOf(id);
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    // ───────────────────────── Download ─────────────────────────

    private async Task<string> DownloadAsync(EngineComponent component, Action<long> reportBytes, CancellationToken cancellationToken)
    {
        var downloads = Path.Combine(_root, ".downloads");
        Directory.CreateDirectory(downloads);
        var file = Path.Combine(downloads, $"{component.Id}-{component.Version}{Path.GetExtension(new Uri(component.Url).AbsolutePath)}");
        var partial = file + ".part";

        using var response = await GetFollowingRedirectsAsync(new Uri(component.Url), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = File.Create(partial))
        {
            var buffer = new byte[1 << 16];
            long received = 0;
            var lastReport = Stopwatch.StartNew();
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
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

        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(actual, component.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(partial);
            throw new InvalidDataException($"{component.Id}: checksum mismatch (expected {component.Sha256}, got {actual}). The download was discarded.");
        }
        File.Move(partial, file, overwrite: true);
        return file;
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

    private async Task UnpackAsync(EngineComponent component, string file, CancellationToken cancellationToken)
    {
        // Short name: LibreOffice's own folder structure is deep, and msiexec fails beyond MAX_PATH (260 chars).
        var staging = Path.Combine(_root, "~" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            switch (component.Kind)
            {
                case "zip":
                    ZipFile.ExtractToDirectory(file, staging);
                    Replace(component.Id, SingleRoot(staging));
                    break;
                case "msi":
                    await UnpackMsiAsync(file, staging, Path.Combine(Path.GetTempPath(), "filee-msiexec.log"), cancellationToken);
                    var soffice = Directory.EnumerateFiles(staging, "soffice.exe", SearchOption.AllDirectories).FirstOrDefault()
                                  ?? throw new InvalidDataException("soffice.exe not found in the LibreOffice package.");
                    var libreOffice = Path.GetDirectoryName(Path.GetDirectoryName(soffice)!)!; // folder with program/, share/
                    TrimLibreOffice(libreOffice);
                    Replace(component.Id, libreOffice);
                    break;
                case "oxt":
                    // LibreOffice registers bundled extensions (plain folders under share/extensions) on start-up.
                    var extensions = Path.Combine(FolderOf("libreoffice"), "share", "extensions");
                    if (!Directory.Exists(extensions))
                        throw new InvalidOperationException($"{component.Id} needs LibreOffice, which is not installed.");
                    ZipFile.ExtractToDirectory(file, staging);
                    var target = FolderOf(component.Id);
                    if (Directory.Exists(target))
                        Directory.Delete(target, recursive: true);
                    Directory.Move(staging, target);
                    break;
                default:
                    throw new NotSupportedException($"Unknown engine package kind '{component.Kind}'.");
            }
            File.WriteAllText(Path.Combine(FolderOf(component.Id), MarkerName), component.Sha256);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private static async Task UnpackMsiAsync(string msi, string target, string log, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The LibreOffice package is a Windows installer.");
        Directory.CreateDirectory(target);
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
        {
            throw new InvalidOperationException(process.ExitCode switch
            {
                1618 => "Another installation is running. Try again when it has finished.",
                _ => $"Unpacking LibreOffice failed (msiexec exit code {process.ExitCode}, log: {log}).",
            });
        }
    }

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

    /// <summary>An archive with a single top-level folder is unpacked without that extra level.</summary>
    private static string SingleRoot(string folder)
    {
        var entries = Directory.GetFileSystemEntries(folder);
        return entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : folder;
    }

    /// <summary>Makes <paramref name="source"/> the engine folder, replacing a previous version.</summary>
    private void Replace(string id, string source)
    {
        var target = FolderOf(id);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.Move(source, target);
    }

    // ───────────────────────── Layout ─────────────────────────

    /// <summary>Folder of a component; the LibreOffice extension lives inside LibreOffice.</summary>
    private string FolderOf(string id) => _components.TryGetValue(id, out var component) && component.Kind == "oxt"
        ? Path.Combine(_root, "libreoffice", "share", "extensions", ExtensionFolder(id))
        : Path.Combine(_root, id);

    private static string ExtensionFolder(string id) => id switch
    {
        "h2orestart" => "H2Orestart",
        _ => id,
    };

    /// <summary>Hash of the installed version, or null when not installed.</summary>
    private string? Marker(string id)
    {
        var marker = Path.Combine(FolderOf(id), MarkerName);
        return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
    }
}
