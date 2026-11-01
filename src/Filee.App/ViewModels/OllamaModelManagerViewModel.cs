using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Infrastructure;
using Filee.Engines.Translation;

namespace Filee.App.ViewModels;

public sealed partial class OllamaModelItem : ObservableObject
{
    public string Name { get; init; } = "";
    public string SizeFormatted { get; init; } = "";
    public bool IsCloud { get; init; }

    [ObservableProperty]
    private bool _isUsing;

    [ObservableProperty]
    private string _actionText = "";

    [ObservableProperty]
    private bool _isActionEnabled = true;
}

public sealed partial class OllamaModelManagerViewModel : ObservableObject
{
    private static readonly Uri OllamaBase = new("http://127.0.0.1:11434");
    private static Process? _fileeStartedOllamaProcess;
    private readonly ILocalizer _loc;
    private readonly UserDataStore _store;

    public OllamaModelManagerViewModel()
        : this(AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>())
    {
    }

    public OllamaModelManagerViewModel(ILocalizer loc, UserDataStore store)
    {
        _loc = loc;
        _store = store;
        _activeModelName = store.Settings.PreferredOllamaModel ?? "";
        UpdateCanStopServer();
        _ = RefreshStatusAsync();
    }

    public static void StopFileeStartedServer()
    {
        if (_fileeStartedOllamaProcess is not null && !_fileeStartedOllamaProcess.HasExited)
        {
            try
            {
                _fileeStartedOllamaProcess.Kill(entireProcessTree: true);
                _fileeStartedOllamaProcess.WaitForExit(1000);
            }
            catch { }
            finally
            {
                _fileeStartedOllamaProcess.Dispose();
                _fileeStartedOllamaProcess = null;
            }
        }
    }

    [ObservableProperty]
    private bool _canStopServer;

    [ObservableProperty]
    private bool _isOllamaInstalled;

    [ObservableProperty]
    private bool _isServerRunning;

    [ObservableProperty]
    private string _serverStatusText = "";

    [ObservableProperty]
    private ObservableCollection<OllamaModelItem> _models = [];

    [ObservableProperty]
    private string _newModelName = "";

    [ObservableProperty]
    private bool _isCloudWarningVisible;

    [ObservableProperty]
    private bool _canPull;

    [ObservableProperty]
    private bool _isPulling;

    [ObservableProperty]
    private double _pullProgress;

    [ObservableProperty]
    private string _pullStatusText = "";

    [ObservableProperty]
    private string _activeModelName = "";

    [ObservableProperty]
    private string _testResultText = "";

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private bool _testHasError;

    [RelayCommand]
    public async Task TestConnectionAsync()
    {
        if (IsTesting) return;
        IsTesting = true;
        TestHasError = false;
        TestResultText = _loc["engines.ollama.testing"];

        try
        {
            var model = !string.IsNullOrWhiteSpace(ActiveModelName)
                ? ActiveModelName
                : await TranslationService.GetPreferredLocalOllamaModelAsync();
            var service = new TranslationService(new TranslateOptions { Model = model });
            var targetLang = "es";
            var result = await service.TranslateTextAsync("Hello, world!", targetLang);
            TestResultText = $"\"Hello, world!\" → \"{result}\"  ({model ?? "local model"})";
            TestHasError = false;
        }
        catch (Exception ex)
        {
            TestResultText = $"{_loc["engines.ollama.failed"]} {ex.Message}";
            TestHasError = true;
        }
        finally
        {
            IsTesting = false;
        }
    }

    partial void OnNewModelNameChanged(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            IsCloudWarningVisible = false;
            CanPull = false;
            return;
        }

        var isCloud = TranslationService.IsCloudModel(trimmed);
        IsCloudWarningVisible = isCloud;
        CanPull = !isCloud && !IsPulling && IsServerRunning;
    }

    partial void OnIsServerRunningChanged(bool value)
    {
        OnNewModelNameChanged(NewModelName);
    }

    [RelayCommand]
    public async Task RefreshStatusAsync()
    {
        IsOllamaInstalled = CheckIsOllamaInstalled();
        await CheckServerAndLoadModelsAsync();
    }

    private static string? FindOllamaExe()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var standardPath = Path.Combine(localAppData, "Programs", "Ollama", "ollama.exe");
            if (File.Exists(standardPath))
                return standardPath;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (OperatingSystem.IsWindows())
        {
            pathEnv = string.Join(Path.PathSeparator,
                pathEnv,
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        }

        var exeName = OperatingSystem.IsWindows() ? "ollama.exe" : "ollama";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim('"')), exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { }
        }

        if (!OperatingSystem.IsWindows())
        {
            foreach (var dir in new[] { "/usr/local/bin", "/opt/homebrew/bin", "/usr/bin", "/bin" })
            {
                var candidate = Path.Combine(dir, "ollama");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static bool CheckIsOllamaInstalled() => FindOllamaExe() is not null;

    private void UpdateCanStopServer()
    {
        CanStopServer = _fileeStartedOllamaProcess is { HasExited: false };
    }

    private async Task CheckServerAndLoadModelsAsync()
    {
        UpdateCanStopServer();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
            using var response = await client.GetAsync(new Uri(OllamaBase, "/api/tags"));
            if (response.IsSuccessStatusCode)
            {
                IsServerRunning = true;
                ServerStatusText = $"{_loc["engines.ollama.server_running"]} (http://127.0.0.1:11434)";

                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);

                var list = new List<OllamaModelItem>();
                if (doc.RootElement.TryGetProperty("models", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in arr.EnumerateArray())
                    {
                        var name = m.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        long size = m.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                        var sizeStr = size > 0 ? $"{size / 1_000_000_000.0:0.1} GB" : "";
                        var isCloud = TranslationService.IsCloudModel(name);
                        var isUsing = !isCloud && string.Equals(name, ActiveModelName, StringComparison.OrdinalIgnoreCase);

                        list.Add(new OllamaModelItem
                        {
                            Name = name,
                            SizeFormatted = sizeStr,
                            IsCloud = isCloud,
                            IsUsing = isUsing,
                            ActionText = isCloud ? _loc["engines.ollama.cloud_blocked"] : isUsing ? _loc["engines.ollama.using_model"] : _loc["engines.ollama.use_model"],
                            IsActionEnabled = !isCloud && !isUsing
                        });
                    }
                }

                var preferred = !string.IsNullOrWhiteSpace(ActiveModelName)
                    ? ActiveModelName
                    : await TranslationService.GetPreferredLocalOllamaModelAsync();

                if (!string.IsNullOrWhiteSpace(preferred) && list.FirstOrDefault(m => string.Equals(m.Name, preferred, StringComparison.OrdinalIgnoreCase)) is { } matched)
                {
                    matched.IsUsing = true;
                    matched.ActionText = _loc["engines.ollama.using_model"];
                    matched.IsActionEnabled = false;
                    ActiveModelName = matched.Name;
                }
                else if (list.FirstOrDefault(m => !m.IsCloud) is { } fallbackLocal)
                {
                    fallbackLocal.IsUsing = true;
                    fallbackLocal.ActionText = _loc["engines.ollama.using_model"];
                    fallbackLocal.IsActionEnabled = false;
                    ActiveModelName = fallbackLocal.Name;
                }

                Models = new ObservableCollection<OllamaModelItem>(list);
                return;
            }
        }
        catch
        {
            // Server not running
        }

        IsServerRunning = false;
        ServerStatusText = _loc["engines.ollama.server_stopped"];
        Models = [];
    }

    [RelayCommand]
    public async Task UseModelAsync(OllamaModelItem item)
    {
        if (item.IsCloud || item.IsUsing)
            return;

        item.ActionText = _loc["engines.ollama.starting"];
        item.IsActionEnabled = false;

        foreach (var m in Models)
        {
            if (m != item && m.IsUsing)
            {
                m.IsUsing = false;
                m.ActionText = m.IsCloud ? _loc["engines.ollama.cloud_blocked"] : _loc["engines.ollama.use_model"];
                m.IsActionEnabled = !m.IsCloud;
            }
        }

        try
        {
            // Warm up model via Ollama local API
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var warmupBody = new { model = item.Name, prompt = "", stream = false };
            _ = await client.PostAsJsonAsync(new Uri(OllamaBase, "/api/generate"), warmupBody);
        }
        catch
        {
            // Ignore warmup failures
        }

        ActiveModelName = item.Name;
        _store.Settings.PreferredOllamaModel = item.Name;
        _store.SaveSettings();

        item.IsUsing = true;
        item.ActionText = _loc["engines.ollama.using_model"];
        item.IsActionEnabled = false;
    }

    [RelayCommand]
    public async Task StopServerAsync()
    {
        if (_fileeStartedOllamaProcess is not null && !_fileeStartedOllamaProcess.HasExited)
        {
            try
            {
                _fileeStartedOllamaProcess.Kill(entireProcessTree: true);
                await _fileeStartedOllamaProcess.WaitForExitAsync();
            }
            catch { }
            finally
            {
                _fileeStartedOllamaProcess.Dispose();
                _fileeStartedOllamaProcess = null;
            }
        }
        UpdateCanStopServer();
        await Task.Delay(300);
        await RefreshStatusAsync();
    }

    [RelayCommand]
    public async Task StartServerAsync()
    {
        var exe = FindOllamaExe();
        if (exe is null)
            return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "serve",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            _fileeStartedOllamaProcess = Process.Start(psi);
            UpdateCanStopServer();
        }
        catch { }

        // Poll for up to 5 seconds
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(500);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(300) };
                var res = await client.GetAsync(new Uri(OllamaBase, "/api/tags"));
                if (res.IsSuccessStatusCode)
                    break;
            }
            catch { }
        }

        await RefreshStatusAsync();
    }

    [RelayCommand]
    public async Task PullModelAsync()
    {
        var model = NewModelName.Trim();
        if (string.IsNullOrWhiteSpace(model) || TranslationService.IsCloudModel(model))
            return;

        IsPulling = true;
        CanPull = false;
        PullProgress = 0;
        PullStatusText = _loc["engines.ollama.connecting"];

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromHours(1) };
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(OllamaBase, "/api/pull"))
            {
                Content = JsonContent.Create(new { name = model, stream = true })
            };

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : "";
                    long total = doc.RootElement.TryGetProperty("total", out var t) && t.TryGetInt64(out var tot) ? tot : 0;
                    long completed = doc.RootElement.TryGetProperty("completed", out var c) && c.TryGetInt64(out var comp) ? comp : 0;

                    if (total > 0 && completed > 0)
                    {
                        var pct = (double)completed / total * 100.0;
                        PullProgress = Math.Min(100, Math.Max(0, pct));
                        PullStatusText = $"{status} ({PullProgress:0}%)";
                    }
                    else if (!string.IsNullOrWhiteSpace(status))
                    {
                        PullStatusText = status;
                    }
                }
                catch { }
            }

            PullProgress = 100;
            PullStatusText = _loc["engines.ollama.complete"];
            NewModelName = "";
            await Task.Delay(500);
            await CheckServerAndLoadModelsAsync();

            if (Models.FirstOrDefault(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase)) is { } newlyDownloaded)
            {
                await UseModelAsync(newlyDownloaded);
            }
        }
        catch (Exception ex)
        {
            PullStatusText = $"{_loc["engines.ollama.error"]} {ex.Message}";
        }
        finally
        {
            IsPulling = false;
            OnNewModelNameChanged(NewModelName);
        }
    }
}
