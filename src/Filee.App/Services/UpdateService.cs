// Update check against GitHub Releases. When a newer version is out the app says so (sidebar, notice, tray) and sends
// the user to the latest release page to download it; running the new installer updates Filee in place and keeps the
// settings. Works in every build, also when run from source.

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

public sealed partial class UpdateService : ObservableObject
{
    /// <summary>How often a running Filee (it lives in the tray for days) looks for a new version.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly ILogger<UpdateService> _log;
    private readonly HttpClient _http;
    private DispatcherTimer? _timer;

    public UpdateService(ILogger<UpdateService> log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Filee", CurrentVersion));
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>GitHub repository from Directory.Build.props (FileeRepositoryUrl).</summary>
    public static string RepositoryUrl =>
        typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value ?? "https://github.com/KnifeLemon/Filee";

    public static string CurrentVersion =>
        typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>GitHub's "new issue" page (bug report or feature request template).</summary>
    public static string NewIssueUrl => RepositoryUrl.TrimEnd('/') + "/issues/new/choose";

    /// <summary>Page with the newest installer.</summary>
    public static string LatestReleaseUrl => RepositoryUrl.TrimEnd('/') + "/releases/latest";

    /// <summary>The newer version found by the last check (e.g. "1.0.2"), or null when up to date / unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable))]
    private string? _latestVersion;

    public bool IsUpdateAvailable => LatestVersion is not null;

    /// <summary>Raised on the UI thread when a check finds a newer version than before.</summary>
    public event EventHandler<string>? UpdateFound;

    /// <summary>
    /// True for published builds (the installer and the portable zip are published with <c>-p:FileeRelease=true</c>),
    /// false when running from source. Keeps development builds out of the registry.
    /// </summary>
    public static bool IsReleaseBuild =>
        typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(a => a.Key == "FileeRelease" && a.Value == "true");

    /// <summary>Checks the latest release now. Returns the newer version, or null when up to date or offline.</summary>
    public async Task<string?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var api = RepositoryUrl.TrimEnd('/').Replace("https://github.com/", "https://api.github.com/repos/", StringComparison.OrdinalIgnoreCase)
                      + "/releases/latest";
            using var response = await _http.GetAsync(api, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogInformation("Update check: {Status}", response.StatusCode); // e.g. 404 before the first release
                return LatestVersion;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var tag = json.RootElement.TryGetProperty("tag_name", out var value) ? value.GetString() : null;
            var newer = IsNewer(tag, CurrentVersion) ? Normalize(tag!) : null;
            await Dispatcher.UIThread.InvokeAsync(() => Publish(newer));
            return newer;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _log.LogInformation("Update check failed: {Message}", ex.Message);
            return LatestVersion;
        }
    }

    /// <summary>Checks shortly after start and then every few hours while <paramref name="enabled"/> says so.</summary>
    public void StartPeriodicChecks(Func<bool> enabled, TimeSpan firstDelay)
    {
        if (_timer is not null)
            return;
        _timer = new DispatcherTimer { Interval = firstDelay };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = CheckInterval;
            if (enabled())
                await CheckAsync();
        };
        _timer.Start();
    }

    /// <summary>Opens the latest release page in the browser.</summary>
    public void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(LatestReleaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.LogWarning(ex, "Could not open {Url}", LatestReleaseUrl);
        }
    }

    private void Publish(string? newer)
    {
        var previous = LatestVersion;
        LatestVersion = newer;
        if (newer is not null && newer != previous)
            UpdateFound?.Invoke(this, newer);
    }

    /// <summary>True when release tag <paramref name="tag"/> ("v1.2.0") is a higher version than <paramref name="current"/>.</summary>
    internal static bool IsNewer(string? tag, string current) =>
        TryParse(tag, out var latest) && TryParse(current, out var installed) && latest > installed;

    /// <summary>"v1.2" / "1.2.0-beta+abc" → 1.2.0 (missing parts are 0, suffixes ignored).</summary>
    internal static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (!Version.TryParse(core.Contains('.') ? core : core + ".0", out var parsed))
            return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }

    private static string Normalize(string tag) => TryParse(tag, out var version) ? version.ToString(3) : tag;
}
