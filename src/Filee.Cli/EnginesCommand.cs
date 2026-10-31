// "filee engines": the optional engines of Settings → Engines, listed, installed and removed with the app's own
// installer (EngineInstaller), so the CLI downloads, verifies and unpacks exactly as the app does. Results go to
// stdout (one JSON document with --json, errors included); progress goes to stderr (one JSON object per line with
// --json, so stderr stays readable line by line).

using System.Text;
using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Cli;

internal static class EnginesCommand
{
    public static async Task<int> RunAsync(CliOptions options, CliHost host, TextWriter output, TextWriter errors, CancellationToken cancellationToken)
    {
        if (options.Inputs.Count == 0)
            return List(options, host.Engines, output);
        var id = options.Inputs[1];
        if (EngineDownloads.Packages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) is not { } package)
        {
            errors.WriteLine($"filee: unknown engine '{id}'. Run 'filee engines' for the list.");
            return Cli.UsageError;
        }
        var result = options.Inputs[0].Equals("install", StringComparison.OrdinalIgnoreCase)
            ? await InstallAsync(package, options, host, errors, cancellationToken)
            : Remove(package, host.Engines);
        Report(result, options, output, errors);
        return result.State is "failed" or "cancelled" ? Cli.SomeFailed : Cli.Success;
    }

    /// <summary>What install or remove did: <paramref name="State"/> is installed, removed, skipped, failed or cancelled.</summary>
    /// <param name="Message">The line for people: what was done, why nothing was, or the error.</param>
    /// <param name="Version">The version of Filee's download, once it is installed.</param>
    private sealed record Result(string Id, string State, string Message, string? Version = null, string? ErrorKey = null);

    private static void Report(Result result, CliOptions options, TextWriter output, TextWriter errors)
    {
        var failed = result.State is "failed" or "cancelled";
        if (options.Json)
        {
            Cli.WriteJsonDocument(output, json =>
            {
                json.WriteStartObject();
                json.WriteString("id", result.Id);
                json.WriteString("state", result.State);
                if (result.Version is not null)
                    json.WriteString("version", result.Version);
                if (result.State == "skipped")
                    json.WriteString("reason", result.Message);
                if (failed)
                    json.WriteString("error", result.Message);
                if (failed && result.ErrorKey is not null)
                    json.WriteString("errorKey", result.ErrorKey);
                json.WriteEndObject();
            });
        }
        if (options.Json)
            return;
        if (failed)
            errors.WriteLine($"filee: {result.Message}");
        else if (!options.Quiet)
            output.WriteLine(result.Message);
    }

    // ───────────────────────── List ─────────────────────────

    private static int List(CliOptions options, EngineInstaller engines, TextWriter output)
    {
        var rows = EngineDownloads.Packages.Select(p => (Package: p, Copy: engines.StatusOf(p))).ToList();
        if (options.Json)
        {
            Cli.WriteJsonDocument(output, json =>
            {
                json.WriteStartObject();
                json.WriteString("platform", EngineDownloads.RuntimeIdentifier);
                json.WriteStartArray("engines");
                foreach (var (package, copy) in rows)
                    WriteEngine(json, engines, package, copy);
                json.WriteEndArray();
                json.WriteEndObject();
            });
            return Cli.Success;
        }

        output.WriteLine($"{"ID",-12} {"STATUS",-16} {"VERSION",-8} SIZE");
        foreach (var (package, copy) in rows)
        {
            var downloadable = CanDownload(engines, package);
            var status = copy.Status switch
            {
                EngineCopyStatus.OwnCopy => copy.FromSystem ? "system copy" : "own copy",
                EngineCopyStatus.Installed => copy.Outdated ? "update available" : "installed",
                _ => "not installed",
            };
            var size = copy.Status switch
            {
                EngineCopyStatus.OwnCopy => copy.Folder!,
                EngineCopyStatus.Installed => $"{Bytes(package.InstalledSize)} on disk",
                _ when downloadable => $"{Bytes(DownloadSize(engines, package))} download, {Bytes(package.InstalledSize)} on disk",
                _ => UnavailableReason(package),
            };
            // The version Filee downloads; next to a copy Filee didn't download it would read as that copy's version.
            var version = downloadable && copy.Status != EngineCopyStatus.OwnCopy ? Version(engines, package) : "";
            output.WriteLine($"{package.Id,-12} {status,-16} {version,-8} {size}");
        }
        output.WriteLine();
        output.WriteLine("Install one with: filee engines install <id>");
        return Cli.Success;
    }

    /// <summary>One package in the JSON of 'engines'; fields that don't apply are left out, as in 'convert --json'.</summary>
    private static void WriteEngine(Utf8JsonWriter json, EngineInstaller engines, EnginePackage package, EngineCopy copy)
    {
        var downloadable = CanDownload(engines, package);
        json.WriteStartObject();
        json.WriteString("id", package.Id);
        json.WriteString("status", copy.Status.ToString().ToLowerInvariant());
        if (copy.Status == EngineCopyStatus.Installed)
            json.WriteBoolean("outdated", copy.Outdated);
        if (copy.Status == EngineCopyStatus.OwnCopy)
        {
            json.WriteBoolean("fromSystem", copy.FromSystem);
            json.WriteString("folder", copy.Folder);
        }
        if (downloadable)
            json.WriteString("version", Version(engines, package));
        json.WriteBoolean("downloadable", downloadable);
        if (downloadable)
        {
            json.WriteNumber("downloadSize", DownloadSize(engines, package));
            json.WriteNumber("installedSize", package.InstalledSize);
        }
        else
        {
            json.WriteString("unavailableReason", UnavailableReason(package));
        }
        Cli.WriteStrings(json, "converters", package.ConverterIds);
        json.WriteEndObject();
    }

    // ───────────────────────── Install and remove ─────────────────────────

    /// <summary>
    /// As the Engines page offers it: a package not installed, Filee's download of an outdated one, and Filee's download
    /// over a copy found on the PC (it is the tested version). The user's own copy is left in use.
    /// </summary>
    private static async Task<Result> InstallAsync(EnginePackage package, CliOptions options, CliHost host, TextWriter errors,
        CancellationToken cancellationToken)
    {
        var engines = host.Engines;
        var copy = engines.StatusOf(package);
        var downloadable = CanDownload(engines, package);
        var version = downloadable ? Version(engines, package) : null;
        if (copy.Status == EngineCopyStatus.Installed && !copy.Outdated)
            return new Result(package.Id, "skipped", $"{package.Id} {version} is already installed.", version);
        if (copy.Status == EngineCopyStatus.OwnCopy && !copy.FromSystem)
            return new Result(package.Id, "skipped", $"{package.Id} uses your own copy at {copy.Folder} (Settings → Engines).");
        if (copy.Status == EngineCopyStatus.OwnCopy && !downloadable)
            return new Result(package.Id, "skipped", $"{package.Id} uses the copy found at {copy.Folder}.");
        if (!downloadable)
            return new Result(package.Id, "failed", UnavailableReason(package));

        var progress = new InstallProgress(package.Id, version!, options.Quiet ? null : errors, options.Json,
            live: ReferenceEquals(errors, Console.Error) && !Console.IsErrorRedirected);
        try
        {
            await engines.InstallAsync(package, progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new Result(package.Id, "cancelled", $"Installing {package.Id} was cancelled.");
        }
        catch (Exception ex)
        {
            var refused = ex.GetBaseException() as ConversionRefusedException;
            return new Result(package.Id, "failed", ErrorText(ex, host), ErrorKey: refused?.ErrorKey);
        }
        finally
        {
            progress.Finish();
        }
        return new Result(package.Id, "installed",
            copy.Outdated ? $"Updated {package.Id} to {version}." : $"Installed {package.Id} {version}.", version);
    }

    /// <summary>Removes Filee's download, as the Engines page allows it; a copy Filee didn't download is never touched.</summary>
    private static Result Remove(EnginePackage package, EngineInstaller engines)
    {
        var copy = engines.StatusOf(package);
        if (copy.Status == EngineCopyStatus.OwnCopy)
            return new Result(package.Id, "skipped", copy.FromSystem
                ? $"{package.Id} uses the copy found at {copy.Folder}; Filee didn't download it."
                : $"{package.Id} uses your own copy at {copy.Folder}; Filee didn't download it.");
        if (copy.Status == EngineCopyStatus.NotInstalled)
            return new Result(package.Id, "skipped", $"{package.Id} is not installed.");
        try
        {
            engines.Uninstall(package);
            return new Result(package.Id, "removed", $"Removed {package.Id}.");
        }
        catch (EngineBusyException busy)
        {
            return new Result(package.Id, "failed", busy.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Result(package.Id, "failed", $"{package.Id} is in use; close Filee or wait for the conversion to finish.");
        }
    }

    /// <summary>The reason as the Engines card gives it (EngineDownloadService.ErrorText), in English.</summary>
    private static string ErrorText(Exception ex, CliHost host) =>
        ex is HttpRequestException ? ex.Message
        : ex.GetBaseException() is ConversionRefusedException refused
            ? host.Texts[refused.ErrorKey] + (string.IsNullOrWhiteSpace(refused.Detail) ? "" : $" ({refused.Detail})")
        : ex.GetBaseException().Message;

    // ───────────────────────── Package details ─────────────────────────

    private static bool CanDownload(EngineInstaller engines, EnginePackage package) =>
        package.Components.Count > 0 && package.Components.All(engines.Components.ContainsKey);

    /// <summary>The version Filee downloads: its main component's (the first).</summary>
    private static string Version(EngineInstaller engines, EnginePackage package) => engines.Components[package.Components[0]].Version;

    private static long DownloadSize(EngineInstaller engines, EnginePackage package) =>
        package.Components.Sum(c => engines.Components[c].Size);

    private static string UnavailableReason(EnginePackage package) =>
        EngineDownloads.UnavailableReason(package) ?? $"No Filee download of {package.Id} is available for {EngineDownloads.RuntimeIdentifier}.";

    /// <summary>"297 MB", "1.3 GB", as Settings → Engines shows sizes.</summary>
    private static string Bytes(long bytes) => bytes >= 1_000_000_000
        ? FormattableString.Invariant($"{bytes / 1_000_000_000.0:0.0} GB")
        : $"{Math.Max(1, bytes / 1_000_000)} MB";

    /// <summary>"850 KB", "12.3 MB", "1.23 GB" for download progress, as the Engines card shows it.</summary>
    private static string Amount(double bytes) => bytes switch
    {
        >= 1_000_000_000 => FormattableString.Invariant($"{bytes / 1_000_000_000:0.00} GB"),
        >= 100_000_000 => FormattableString.Invariant($"{bytes / 1_000_000:0} MB"),
        >= 1_000_000 => FormattableString.Invariant($"{bytes / 1_000_000:0.0} MB"),
        _ => FormattableString.Invariant($"{Math.Max(0, bytes / 1_000):0} KB"),
    };

    /// <summary>
    /// Writes the installer's progress reports to stderr as they come (the installer reports about every 150 ms):
    /// one JSON object per line with --json; else a line that updates in place on a terminal, and one line per
    /// step and per 10% when stderr is redirected. Nothing with --quiet.
    /// </summary>
    private sealed class InstallProgress(string id, string version, TextWriter? errors, bool json, bool live) : IProgress<EngineInstallProgress>
    {
        private readonly Lock _gate = new();
        private (EngineInstallStage, string)? _step;
        private int _tenth = -1;
        private int _width;

        public void Report(EngineInstallProgress value)
        {
            if (errors is null)
                return;
            lock (_gate)
            {
                if (json)
                {
                    errors.WriteLine(JsonLine(value));
                    return;
                }
                var line = value.Stage == EngineInstallStage.Downloading
                    ? $"Downloading {id} {version}: {Amount(value.BytesDone)} / {Amount(value.BytesTotal)} ({(int)(value.Fraction * 100)}%)"
                    : $"Unpacking {value.ComponentId}…";
                var step = (value.Stage, value.ComponentId);
                var newStep = _step != step;
                var tenth = (int)(value.Fraction * 10);
                if (newStep)
                    _tenth = -1;
                if (live)
                {
                    if (newStep)
                        EndLine();
                    errors.Write("\r" + line.PadRight(_width));
                    _width = line.Length;
                }
                else if (newStep || tenth > _tenth)
                {
                    errors.WriteLine(line);
                }
                _step = step;
                _tenth = Math.Max(_tenth, tenth);
            }
        }

        /// <summary>Ends a line that was updating in place.</summary>
        public void Finish()
        {
            lock (_gate)
                EndLine();
        }

        private void EndLine()
        {
            if (errors is null || _width == 0)
                return;
            errors.WriteLine();
            _width = 0;
        }

        private string JsonLine(EngineInstallProgress value)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("id", id);
                writer.WriteString("stage", value.Stage.ToString().ToLowerInvariant());
                writer.WriteString("component", value.ComponentId);
                writer.WriteNumber("fraction", Math.Round(value.Fraction, 4));
                writer.WriteNumber("bytesDone", value.BytesDone);
                writer.WriteNumber("bytesTotal", value.BytesTotal);
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
