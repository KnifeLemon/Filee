// Optional engine downloads for the UI: one observable state per package, one install at a time, and a catalog
// refresh when an engine appears or disappears so donut slices and routes update immediately.

using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

public enum EnginePackageStatus
{
    NotInstalled,
    Queued,
    Downloading,
    Unpacking,
    Installed,
    Failed,
}

/// <summary>Install state of one optional engine package, bound by the Engines page and the first-run setup.</summary>
public sealed partial class EnginePackageState : ObservableObject
{
    private readonly ILocalizer _loc;

    public EnginePackageState(EnginePackage package, ILocalizer loc)
    {
        Package = package;
        _loc = loc;
    }

    public EnginePackage Package { get; }
    public string Name => _loc[$"engines.package.{Package.Id}.name"];
    public string Description => _loc[$"engines.package.{Package.Id}.description"];

    public string Sizes => _loc.Format("engines.package.size",
        FormatBytes(EngineDownloads.DownloadSize(Package)), FormatBytes(Package.InstalledSize));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsInstalled), nameof(IsBusy), nameof(CanInstall))]
    private EnginePackageStatus _status;

    /// <summary>0..100 while downloading or unpacking.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _error;

    /// <summary>"120 MB / 420 MB · 8.4 MB/s · 36 s left" while downloading, empty otherwise.</summary>
    [ObservableProperty]
    private string _progressDetail = "";

    /// <summary>Ticked in the first-run setup.</summary>
    [ObservableProperty]
    private bool _selected;

    public bool IsInstalled => Status == EnginePackageStatus.Installed;
    public bool IsBusy => Status is EnginePackageStatus.Queued or EnginePackageStatus.Downloading or EnginePackageStatus.Unpacking;
    public bool CanInstall => Status is EnginePackageStatus.NotInstalled or EnginePackageStatus.Failed;

    public string StatusText => Status switch
    {
        EnginePackageStatus.Queued => _loc["engines.state.queued"],
        EnginePackageStatus.Downloading => _loc.Format("engines.state.downloading", (int)Progress),
        EnginePackageStatus.Unpacking => _loc["engines.state.unpacking"],
        EnginePackageStatus.Installed => _loc["engines.state.installed"],
        EnginePackageStatus.Failed => _loc.Format("engines.state.failed", Error ?? ""),
        _ => _loc["engines.state.not_installed"],
    };

    internal CancellationTokenSource? Cancellation { get; set; }

    /// <summary>Re-reads localized texts after a language change.</summary>
    internal void Relocalize() => OnPropertyChanged(string.Empty);

    /// <summary>"420 MB", "1.2 GB".</summary>
    public static string FormatBytes(long bytes) => bytes >= 1_000_000_000
        ? $"{bytes / 1_000_000_000.0:0.0} GB"
        : $"{Math.Max(1, bytes / 1_000_000)} MB";

    /// <summary>Download amounts and speeds with one decimal where it matters: "850 KB", "12.3 MB", "1.23 GB".</summary>
    public static string FormatAmount(double bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000:0.00} GB",
        >= 100_000_000 => $"{bytes / 1_000_000:0} MB",
        >= 1_000_000 => $"{bytes / 1_000_000:0.0} MB",
        _ => $"{Math.Max(0, bytes / 1_000):0} KB",
    };

    /// <summary>Updates <see cref="ProgressDetail"/> from a download progress report.</summary>
    internal void ShowTransfer(long done, long total, TransferRate rate)
    {
        var remaining = rate.Remaining(done, total);
        var eta = remaining switch
        {
            null => _loc["engines.eta.estimating"],
            { TotalSeconds: < 60 } time => _loc.Format("engines.eta.seconds", Math.Max(1, (int)Math.Ceiling(time.TotalSeconds))),
            var time => _loc.Format("engines.eta.minutes", (int)Math.Ceiling(time.Value.TotalMinutes)),
        };
        ProgressDetail = _loc.Format("engines.progress_detail",
            FormatAmount(done), FormatAmount(total), FormatAmount(rate.BytesPerSecond), eta);
    }
}

public sealed class EngineDownloadService
{
    private readonly EngineInstaller _installer;
    private readonly ConverterCatalog _catalog;
    private readonly ILogger<EngineDownloadService> _logger;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private int _warmingUp;

    public EngineDownloadService(ConverterCatalog catalog, ILocalizer loc, ILogger<EngineDownloadService> logger)
    {
        _catalog = catalog;
        _logger = logger;
        // Redirects are followed by the installer (mirrors may use plain HTTP; files are verified by SHA-256).
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Filee/{typeof(EngineDownloadService).Assembly.GetName().Version}");
        _installer = new EngineInstaller(http, logger: logger);
        Packages = EngineDownloads.Packages.Select(p => new EnginePackageState(p, loc)).ToList();
        if (loc is LocalizationService localization)
            localization.LanguageChanged += (_, _) => Packages.ToList().ForEach(p => p.Relocalize());
        RefreshStatus();
    }

    public IReadOnlyList<EnginePackageState> Packages { get; }

    /// <summary>True while any package is downloading or waiting.</summary>
    public bool IsBusy => Packages.Any(p => p.IsBusy);

    /// <summary>Re-reads what is installed (e.g. after the user deleted a folder).</summary>
    public void RefreshStatus()
    {
        foreach (var state in Packages.Where(p => !p.IsBusy))
            state.Status = _installer.IsInstalled(state.Package) ? EnginePackageStatus.Installed : EnginePackageStatus.NotInstalled;
    }

    /// <summary>Downloads and installs a package; packages queue up and install one after another.</summary>
    public async Task InstallAsync(EnginePackageState state)
    {
        if (state.IsBusy || state.IsInstalled)
            return;
        using var cancellation = new CancellationTokenSource();
        state.Cancellation = cancellation;
        state.Error = null;
        state.Progress = 0;
        state.Status = EnginePackageStatus.Queued;
        try
        {
            await _oneAtATime.WaitAsync(cancellation.Token);
            try
            {
                // Progress<T> reports on the UI thread it was created on.
                var clock = Stopwatch.StartNew();
                var rate = new TransferRate();
                var progress = new Progress<EngineInstallProgress>(p =>
                {
                    if (!state.IsBusy)
                        return;
                    state.Status = p.Stage == EngineInstallStage.Downloading ? EnginePackageStatus.Downloading : EnginePackageStatus.Unpacking;
                    state.Progress = p.Fraction * 100;
                    if (p.Stage == EngineInstallStage.Downloading && p.BytesTotal > 0)
                    {
                        rate.Add(clock.Elapsed, p.BytesDone);
                        state.ShowTransfer(p.BytesDone, p.BytesTotal, rate);
                    }
                    else
                    {
                        state.ProgressDetail = "";
                    }
                });
                await Task.Run(() => _installer.InstallAsync(state.Package, progress, cancellation.Token), cancellation.Token);
            }
            finally
            {
                _oneAtATime.Release();
            }
            state.Status = EnginePackageStatus.Installed;
            _catalog.Refresh();
            if (state.Package.ConverterIds.Contains("libreoffice"))
                WarmUpInBackground(TimeSpan.Zero);
        }
        catch (OperationCanceledException)
        {
            state.Status = _installer.IsInstalled(state.Package) ? EnginePackageStatus.Installed : EnginePackageStatus.NotInstalled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Installing engine package {Package} failed", state.Package.Id);
            state.Error = ex is HttpRequestException ? ex.Message : ex.GetBaseException().Message;
            state.Status = EnginePackageStatus.Failed;
        }
        finally
        {
            state.Cancellation = null;
            state.ProgressDetail = "";
        }
    }

    public void Cancel(EnginePackageState state) => state.Cancellation?.Cancel();

    /// <summary>
    /// Prepares LibreOffice's worker profiles in the background, so the first document conversion is as quick as the
    /// following ones (LibreOffice's first start with a new profile takes 2-3× longer). One run at a time; does
    /// nothing when LibreOffice is missing or already prepared.
    /// </summary>
    public void WarmUpInBackground(TimeSpan delay)
    {
        if (Interlocked.Exchange(ref _warmingUp, 1) == 1)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
                foreach (var libreOffice in _catalog.All.OfType<LibreOfficeConverter>())
                {
                    var prepared = await libreOffice.WarmUpAsync();
                    if (prepared > 0)
                        _logger.LogInformation("Prepared {Count} LibreOffice profile(s)", prepared);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Preparing LibreOffice failed");
            }
            finally
            {
                Interlocked.Exchange(ref _warmingUp, 0);
            }
        });
    }

    /// <summary>Removes a package. Returns false when its files are in use (a conversion is running).</summary>
    public bool Uninstall(EnginePackageState state)
    {
        if (state.IsBusy)
            return false;
        try
        {
            _installer.Uninstall(state.Package);
            state.Status = EnginePackageStatus.NotInstalled;
            _catalog.Refresh();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Removing engine package {Package} failed", state.Package.Id);
            RefreshStatus();
            return false;
        }
    }

    /// <summary>The first not-installed package whose engines would make <paramref name="possible"/> true, if any.</summary>
    public EnginePackageState? PackageEnabling(Func<RoutePlanner, bool> possible) =>
        Packages.FirstOrDefault(p => !p.IsInstalled && possible(_catalog.CreatePlanner(p.Package.ConverterIds)));
}
