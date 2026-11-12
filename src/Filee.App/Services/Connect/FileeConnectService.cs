using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using QRCoder;

namespace Filee.App.Services.Connect;

public sealed class FileeConnectService : IAsyncDisposable
{
    private readonly ILogger<FileeConnectService> _log;
    private readonly SharedFolderManager _folderManager;
    private readonly List<ConnectMessage> _history = [];
    private readonly List<ConnectClient> _clients = [];
    private readonly object _clientsLock = new();
    private readonly ConcurrentDictionary<string, PairedDevice> _pairedDevices = new(StringComparer.Ordinal);

    private LocalWebServer? _server;
    private string? _pairingSecret;
    private string _currentPin = "";
    private DateTime _pinExpiresAtUtc;
    private System.Threading.Timer? _pinTimer;
    private readonly object _pinLock = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _pinPairSemaphore = new(3, 3);
    private const int MaxPairedDevices = 5;

    private sealed class ClientPinAttemptState
    {
        public int FailedAttempts { get; set; }
        public DateTime? LockoutUntilUtc { get; set; }
    }
    private readonly ConcurrentDictionary<IPAddress, ClientPinAttemptState> _pinAttemptsByIp = new();

    private string? _mobileUrl;
    private IPAddress _lanIp = IPAddress.Loopback;
    private Avalonia.Media.Imaging.Bitmap? _qrBitmap;
    private int _port;
    private bool _isRunning;
    private Func<string, string>? _stringLocalizer;

    public void SetStringLocalizer(Func<string, string> localizer) => _stringLocalizer = localizer;
    public SharedFolderManager FolderManager => _folderManager;
    public string? PairingSecret => _pairingSecret;
    public string CurrentPin => _currentPin;
    public int PinSecondsRemaining => Math.Max(0, (int)(_pinExpiresAtUtc - DateTime.UtcNow).TotalSeconds);
    public string? MobileUrl => _mobileUrl;
    public string ManualUrl => $"http://{_lanIp}:{_port}";
    public Avalonia.Media.Imaging.Bitmap? QrBitmap => _qrBitmap;
    public bool IsMobileConnected
    {
        get
        {
            lock (_clientsLock)
            {
                return _clients.Any(c => c.Role == "mobile") || !_pairedDevices.IsEmpty;
            }
        }
    }
    public IReadOnlyList<PairedDevice> PairedDevices => _pairedDevices.Values.ToList();
    public string? ConnectedDeviceName
    {
        get
        {
            var first = _pairedDevices.Values.OrderByDescending(d => d.LastSeen).FirstOrDefault();
            return first?.Name;
        }
    }
    public bool IsRunning => _isRunning;

    public IReadOnlyList<ConnectMessage> MessageHistory
    {
        get
        {
            lock (_history) { return [.. _history]; }
        }
    }

    public event Action<string>? MobileConnected;
    public event Action? MobileDisconnected;
    public event Action<ConnectMessage>? MessageReceived;
    public event Action? FilesChanged;
    public event Action<string>? SessionEnded;
    public event Action? PinRefreshed;
    public event Action? PinTicked;
    public event Action? PairedDevicesChanged;

    public FileeConnectService(ILogger<FileeConnectService> log, SharedFolderManager folderManager)
    {
        _log = log;
        _folderManager = folderManager;
    }

    public async Task StartSessionAsync(bool allowLoopback = false)
    {
        await _startLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isRunning)
                return;

            var lanIp = GetLocalLanIp();
            if (IPAddress.IsLoopback(lanIp) && !allowLoopback)
            {
                throw new InvalidOperationException("No active private Wi-Fi or Ethernet network connection found.");
            }

            _folderManager.Initialize();
            lock (_history) { _history.Clear(); }
            lock (_clientsLock) { _clients.Clear(); }
            _pairedDevices.Clear();
            _pinAttemptsByIp.Clear();

            _pairingSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            lock (_pinLock)
            {
                RefreshPin();
            }

            _pinTimer = new System.Threading.Timer(_ =>
            {
                if (!_isRunning) return;
                if (DateTime.UtcNow >= _pinExpiresAtUtc)
                {
                    lock (_pinLock)
                    {
                        RefreshPin();
                    }
                    Dispatcher.UIThread.Post(() => PinRefreshed?.Invoke());
                }
                else
                {
                    Dispatcher.UIThread.Post(() => PinTicked?.Invoke());
                }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            _lanIp = lanIp;
            _port = FindFreePort(_lanIp, 3000);

            _mobileUrl = $"http://{_lanIp}:{_port}/mobile/{_pairingSecret}";

            GenerateQrCode(_mobileUrl);

            _server = new LocalWebServer(_lanIp, _port, _log);
            await _server.StartAsync(ConfigureRoutes);

            _isRunning = true;
            _log.LogInformation("FileeConnect session started at {Url}", _mobileUrl);
        }
        finally
        {
            _startLock.Release();
        }
    }

    public void GenerateNewPairingSecret()
    {
        _pairingSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _mobileUrl = $"http://{_lanIp}:{_port}/mobile/{_pairingSecret}";
        GenerateQrCode(_mobileUrl);
    }

    private void RefreshPin()
    {
        _currentPin = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        _pinExpiresAtUtc = DateTime.UtcNow.AddSeconds(30);
    }

    public async Task StopSessionAsync()
    {
        await _startLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_isRunning)
                return;

            _isRunning = false;

            _pinTimer?.Dispose();
            _pinTimer = null;

            if (_server is not null)
            {
                await _server.StopAsync();
                await _server.DisposeAsync();
                _server = null;
            }

            List<ConnectClient> clientsToDispose;
            lock (_clientsLock)
            {
                clientsToDispose = [.. _clients];
                _clients.Clear();
            }

            foreach (var client in clientsToDispose)
            {
                await client.DisposeAsync();
            }

            _pairedDevices.Clear();
            _folderManager.Clear();

            if (_qrBitmap is not null)
            {
                _qrBitmap.Dispose();
                _qrBitmap = null;
            }

            SessionEnded?.Invoke("host_stopped");
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task DisconnectDeviceAsync(string deviceId)
    {
        var target = _pairedDevices.Values.FirstOrDefault(d => d.Id == deviceId);
        if (target is null) return;

        _pairedDevices.TryRemove(target.Token, out _);

        List<ConnectClient> matchingClients;
        lock (_clientsLock)
        {
            matchingClients = _clients.Where(c => c.DeviceId == deviceId).ToList();
            _clients.RemoveAll(c => c.DeviceId == deviceId);
        }

        foreach (var c in matchingClients)
        {
            try
            {
                await c.SendAsync("{\"type\":\"device-disconnected\"}");
            }
            catch { }

            try
            {
                if (c.Socket.State == WebSocketState.Open)
                {
                    await c.Socket.CloseAsync((WebSocketCloseStatus)4001, "Device disconnected by host", CancellationToken.None);
                }
            }
            catch { }

            await c.DisposeAsync();
        }

        Dispatcher.UIThread.Post(() =>
        {
            PairedDevicesChanged?.Invoke();
            if (!IsMobileConnected)
            {
                MobileDisconnected?.Invoke();
            }
        });
    }

    private void ConfigureRoutes(WebApplication app)
    {
        // Static assets
        app.MapGet("/app.css", () => ServeEmbeddedResource("Filee.App.Resources.Connect.app.css", "text/css; charset=utf-8"));
        app.MapGet("/mobile.js", () => ServeEmbeddedResource("Filee.App.Resources.Connect.mobile.js", "application/javascript; charset=utf-8"));
        app.MapGet("/shared-files.js", () => ServeEmbeddedResource("Filee.App.Resources.Connect.shared-files.js", "application/javascript; charset=utf-8"));

        // QR Pairing Route: /mobile/{secret}
        app.MapGet("/mobile/{secret}", (string secret, HttpContext context) =>
        {
            var activeSecret = _pairingSecret;
            if (string.IsNullOrEmpty(activeSecret))
                return Results.NotFound();

            var secretBytes = Encoding.UTF8.GetBytes(activeSecret);
            var inputBytes = Encoding.UTF8.GetBytes(secret ?? "");

            if (!CryptographicOperations.FixedTimeEquals(inputBytes, secretBytes))
            {
                return Results.NotFound();
            }

            // Atomically consume the secret: only the single request that succeeds the CompareExchange proceeds to pair
            var consumed = Interlocked.CompareExchange(ref _pairingSecret, null, activeSecret);
            if (!string.Equals(consumed, activeSecret, StringComparison.Ordinal))
            {
                return Results.NotFound();
            }

            var device = PairNewDevice(context.Request.Headers.UserAgent.ToString());
            SetDeviceTokenCookie(context, device.Token);

            return Results.Redirect("/mobile");
        });

        // Direct Mobile Page (either already paired via cookie or shows PIN prompt)
        app.MapGet("/mobile", (HttpContext context) =>
        {
            return ServeMobileHtml();
        });
        app.MapGet("/", (HttpContext context) => Results.Redirect("/mobile"));
        app.MapGet("/connect", (HttpContext context) => Results.Redirect("/mobile"));

        // Localized UI strings for mobile client: GET /api/strings
        app.MapGet("/api/strings", (HttpContext context) =>
        {
            var keys = new[]
            {
                "connect.tab_text", "connect.tab_shared_folder", "connect.send",
                "connect.paste_clipboard", "connect.copy", "connect.copied",
                "connect.no_messages", "connect.no_files", "connect.upload_files",
                "connect.upload_folder", "connect.end_session", "connect.waiting_device",
                "connect.sender_pc", "connect.sender_phone", "connect.view",
                "connect.stored_locally", "connect.caption", "connect.input_placeholder",
                "connect.manual_instruction_pin", "connect.disconnect_device", "connect.title"
            };
            var dict = new Dictionary<string, string>();
            foreach (var k in keys)
            {
                dict[k] = _stringLocalizer?.Invoke(k) ?? k;
            }
            return Results.Json(dict);
        });

        // PIN Pairing API: POST /api/pair/pin
        app.MapPost("/api/pair/pin", async (HttpContext context) =>
        {
            if (!await _pinPairSemaphore.WaitAsync(TimeSpan.FromSeconds(2)))
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(cts.Token);
                if (body.Length > 2048)
                {
                    return Results.BadRequest(new { error = "payload_too_large" });
                }

                PinPairRequest? req;
                try
                {
                    req = JsonSerializer.Deserialize<PinPairRequest>(body);
                }
                catch
                {
                    return Results.BadRequest(new { error = "invalid_json" });
                }

                if (req is null || string.IsNullOrWhiteSpace(req.Pin))
                {
                    return Results.BadRequest(new { error = "missing_pin" });
                }

                var clientIp = context.Connection.RemoteIpAddress ?? IPAddress.Loopback;
                var attemptState = _pinAttemptsByIp.GetOrAdd(clientIp, _ => new ClientPinAttemptState());

                lock (_pinLock)
                {
                    if (attemptState.LockoutUntilUtc is { } lockout && DateTime.UtcNow < lockout)
                    {
                        var remainingSec = (int)(lockout - DateTime.UtcNow).TotalSeconds;
                        return Results.Json(new { error = "locked_out", remainingSeconds = remainingSec }, statusCode: StatusCodes.Status429TooManyRequests);
                    }

                    var pinBytes = Encoding.UTF8.GetBytes(req.Pin.Trim());
                    var currentBytes = Encoding.UTF8.GetBytes(_currentPin);

                    if (DateTime.UtcNow < _pinExpiresAtUtc && CryptographicOperations.FixedTimeEquals(pinBytes, currentBytes))
                    {
                        // Consumed OTP immediately!
                        attemptState.FailedAttempts = 0;
                        attemptState.LockoutUntilUtc = null;
                        RefreshPin();
                        _pairingSecret = null; // Also invalidate the QR secret when paired via PIN
                        Dispatcher.UIThread.Post(() => PinRefreshed?.Invoke());

                        var device = PairNewDevice(req.DeviceName ?? context.Request.Headers.UserAgent.ToString());
                        SetDeviceTokenCookie(context, device.Token);

                        return Results.Ok(new { token = device.Token, deviceName = device.Name, deviceId = device.Id });
                    }

                    attemptState.FailedAttempts++;
                    if (attemptState.FailedAttempts >= 5)
                    {
                        attemptState.LockoutUntilUtc = DateTime.UtcNow.AddMinutes(2);
                        return Results.Json(new { error = "locked_out", remainingSeconds = 120 }, statusCode: StatusCodes.Status429TooManyRequests);
                    }

                    return Results.Json(new { error = "invalid_pin", attemptsRemaining = 5 - attemptState.FailedAttempts }, statusCode: StatusCodes.Status401Unauthorized);
                }
            }
            finally
            {
                _pinPairSemaphore.Release();
            }
        });

        // Auth Check API: GET /api/auth/check
        app.MapGet("/api/auth/check", (HttpContext context) =>
        {
            if (TryValidateDeviceToken(context, out var device))
            {
                return Results.Ok(new { authenticated = true, deviceId = device.Id, deviceName = device.Name });
            }
            return Results.Json(new { authenticated = false }, statusCode: StatusCodes.Status401Unauthorized);
        });

        // Message History: GET /api/history
        app.MapGet("/api/history", (HttpContext context) =>
        {
            if (!TryValidateDeviceToken(context, out _))
                return Results.Unauthorized();

            lock (_history)
            {
                return Results.Json(_history);
            }
        });

        // Send Text: POST /api/send
        app.MapPost("/api/send", async (HttpContext context) =>
        {
            if (!TryValidateDeviceToken(context, out var device))
                return Results.Unauthorized();

            string text;
            try
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                using var json = JsonDocument.Parse(body);
                text = json.RootElement.GetProperty("text").GetString() ?? "";
            }
            catch
            {
                return Results.BadRequest(new { error = "Invalid message JSON" });
            }

            if (string.IsNullOrWhiteSpace(text))
                return Results.BadRequest(new { error = "Text is empty" });

            var msg = new ConnectMessage(Guid.NewGuid().ToString("N"), text, "mobile", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            lock (_history) { _history.Add(msg); }

            Dispatcher.UIThread.Post(() => MessageReceived?.Invoke(msg));
            _ = BroadcastAsync(JsonSerializer.Serialize(new { type = "message", message = msg }));

            return Results.Ok(new { ok = true, message = msg });
        });

        // Shared Files List: GET /api/files
        app.MapGet("/api/files", (HttpContext context) =>
        {
            if (!TryValidateDeviceToken(context, out _))
                return Results.Unauthorized();

            var files = _folderManager.GetFiles();
            var usage = _folderManager.GetUsage();
            return Results.Json(new { files, usage });
        });

        // Upload Single File: POST /api/upload
        app.MapPost("/api/upload", async (HttpContext context) =>
        {
            if (!TryValidateDeviceToken(context, out var device))
                return Results.Unauthorized();

            var sizeFeature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is not null && !sizeFeature.IsReadOnly)
            {
                sizeFeature.MaxRequestBodySize = 510L * 1024 * 1024;
            }

            try
            {
                var form = await context.Request.ReadFormAsync();
                var file = form.Files.FirstOrDefault();
                if (file is null || file.Length == 0)
                    return Results.BadRequest(new { error = "No file uploaded" });

                var relPath = form["relativePath"].ToString();
                await using var stream = file.OpenReadStream();
                var item = await _folderManager.SaveStreamAsync(stream, file.FileName, relPath, "mobile", file.ContentType, file.Length);

                Dispatcher.UIThread.Post(() => FilesChanged?.Invoke());
                _ = BroadcastAsync(JsonSerializer.Serialize(new { type = "files-changed" }));

                return Results.Ok(new { ok = true, file = item.ToMetadata() });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to upload file from mobile");
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
        });

        // Download Single File: GET /download/{id}
        app.MapGet("/download/{id}", (string id, HttpContext context) =>
        {
            if (!TryValidateDeviceToken(context, out _))
                return Results.Unauthorized();

            var file = _folderManager.GetFile(id);
            if (file is null || !File.Exists(file.DiskPath))
                return Results.NotFound();

            return Results.File(file.DiskPath, "application/octet-stream", file.Name, enableRangeProcessing: true);
        });

        // Preview Single File: GET /preview/{id}
        app.MapGet("/preview/{id}", (string id, HttpContext context) =>
        {
            if (!TryValidateDeviceToken(context, out _))
                return Results.Unauthorized();

            var file = _folderManager.GetFile(id);
            if (file is null || !File.Exists(file.DiskPath))
                return Results.NotFound();

            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentSecurityPolicy = "sandbox";

            var ext = Path.GetExtension(file.Name).ToLowerInvariant();
            var mime = SharedFolderManager.GuessMimeType(file.Name);

            // Images and PDFs render inline; all other files must be attachment
            var isInlineSafe = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".avif" or ".pdf";
            if (!isInlineSafe)
            {
                context.Response.Headers.ContentDisposition = $"attachment; filename=\"{Uri.EscapeDataString(file.Name)}\"";
            }

            return Results.File(file.DiskPath, mime, enableRangeProcessing: true);
        });

        // Download Entire Shared Folder as ZIP: GET /api/folder.zip
        app.MapGet("/api/folder.zip", async (HttpContext context, CancellationToken ct) =>
        {
            if (!TryValidateDeviceToken(context, out _))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var syncIo = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
            if (syncIo != null)
            {
                syncIo.AllowSynchronousIO = true;
            }

            context.Response.ContentType = "application/zip";
            context.Response.Headers.ContentDisposition = "attachment; filename=\"shared-folder.zip\"";

            if (context.Request.Query.TryGetValue("path", out var pathVal) && !string.IsNullOrWhiteSpace(pathVal))
            {
                await _folderManager.WriteFolderZipAsync(pathVal.ToString(), context.Response.Body, ct).ConfigureAwait(false);
            }
            else
            {
                await _folderManager.WriteAllZipAsync(context.Response.Body, ct).ConfigureAwait(false);
            }

            await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        });

        // WebSocket endpoint: GET /ws
        app.MapGet("/ws", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
                return Results.BadRequest();

            if (!TryValidateDeviceToken(context, out var device))
                return Results.Unauthorized();

            var socket = await context.WebSockets.AcceptWebSocketAsync();
            var client = new ConnectClient(Guid.NewGuid().ToString("N"), device.Id, "mobile", device.Name, socket);

            lock (_clientsLock)
            {
                _clients.Add(client);
            }

            await HandleWebSocketClientAsync(client);
            return Results.Empty;
        });

        // Mobile Screen Mirroring WebSocket endpoint: GET /ws/mirror
        app.MapGet("/ws/mirror", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
                return Results.BadRequest();

            if (!TryValidateDeviceToken(context, out _))
                return Results.Unauthorized();

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var mirrorService = AppHost.Get<Filee.App.Services.Capture.MobileMirrorService>();
            mirrorService?.OnConnected();

            var buffer = new byte[1024 * 512];
            try
            {
                while (socket.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    System.Net.WebSockets.WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                            break;
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                        break;

                    if (ms.Length > 0 && mirrorService != null)
                    {
                        mirrorService.OnFrame(ms.ToArray());
                    }
                }
            }
            catch
            {
            }
            finally
            {
                mirrorService?.OnDisconnected();
            }
            return Results.Empty;
        });
    }

    private PairedDevice PairNewDevice(string userAgent)
    {
        if (_pairedDevices.Count >= MaxPairedDevices)
        {
            var oldest = _pairedDevices.Values.OrderBy(d => d.LastSeen).FirstOrDefault();
            if (oldest is not null)
            {
                _ = DisconnectDeviceAsync(oldest.Id);
            }
        }

        var deviceId = Guid.NewGuid().ToString("N");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var friendlyName = ParseFriendlyDeviceName(userAgent);

        var device = new PairedDevice(deviceId, token, friendlyName, userAgent, DateTime.UtcNow, DateTime.UtcNow);
        _pairedDevices[token] = device;

        Dispatcher.UIThread.Post(() =>
        {
            PairedDevicesChanged?.Invoke();
            MobileConnected?.Invoke(device.Name);
        });

        return device;
    }

    private static string ParseFriendlyDeviceName(string ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return "Mobile Device";
        if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase)) return "iPhone";
        if (ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)) return "iPad";
        if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            if (ua.Contains("Samsung", StringComparison.OrdinalIgnoreCase)) return "Samsung Phone";
            if (ua.Contains("Pixel", StringComparison.OrdinalIgnoreCase)) return "Google Pixel";
            return "Android Device";
        }
        if (ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)) return "Mac";
        if (ua.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return "Windows PC";
        return "Connected Device";
    }

    private static void SetDeviceTokenCookie(HttpContext context, string token)
    {
        context.Response.Cookies.Append("filee_token", token, new CookieOptions
        {
            Path = "/",
            SameSite = SameSiteMode.Lax,
            HttpOnly = true,
            IsEssential = true
        });
    }

    public bool TryValidateDeviceToken(HttpContext context, [NotNullWhen(true)] out PairedDevice? device)
    {
        device = null;
        string? token = null;

        if (context.Request.Headers.TryGetValue("Authorization", out var authHeader) &&
            authHeader.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = authHeader.ToString()["Bearer ".Length..].Trim();
        }

        if (string.IsNullOrEmpty(token) && context.Request.Cookies.TryGetValue("filee_token", out var cookieToken))
        {
            token = cookieToken;
        }

        if (string.IsNullOrEmpty(token))
            return false;

        var tokenBytes = Encoding.UTF8.GetBytes(token);
        foreach (var pair in _pairedDevices)
        {
            var candidateBytes = Encoding.UTF8.GetBytes(pair.Key);
            if (CryptographicOperations.FixedTimeEquals(tokenBytes, candidateBytes))
            {
                var updated = pair.Value with { LastSeen = DateTime.UtcNow };
                _pairedDevices.TryUpdate(pair.Key, updated, pair.Value);
                device = updated;
                return true;
            }
        }

        return false;
    }

    private async Task HandleWebSocketClientAsync(ConnectClient client)
    {
        // Send initial state
        var initialHistory = MessageHistory;
        var files = _folderManager.GetFiles();
        var usage = _folderManager.GetUsage();

        var initPayload = JsonSerializer.Serialize(new
        {
            type = "init",
            messages = initialHistory,
            files,
            usage,
            deviceName = client.DeviceName
        });
        await client.SendAsync(initPayload);

        var buffer = new byte[4096];
        using var ms = new MemoryStream();

        try
        {
            while (client.Socket.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await client.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    ms.Write(buffer, 0, result.Count);
                    if (ms.Length > 65536) // 64 KB cap
                    {
                        await client.Socket.CloseAsync((WebSocketCloseStatus)1009, "Message too large", CancellationToken.None);
                        return;
                    }
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                var text = Encoding.UTF8.GetString(ms.ToArray());
                try
                {
                    var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("type", out var typeEl))
                    {
                        var type = typeEl.GetString();
                        if (type == "text" && doc.RootElement.TryGetProperty("text", out var textEl))
                        {
                            var messageContent = textEl.GetString();
                            if (!string.IsNullOrWhiteSpace(messageContent))
                            {
                                var msg = new ConnectMessage(Guid.NewGuid().ToString("N"), messageContent, "mobile", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                                lock (_history) { _history.Add(msg); }
                                Dispatcher.UIThread.Post(() => MessageReceived?.Invoke(msg));
                                _ = BroadcastAsync(JsonSerializer.Serialize(new { type = "message", message = msg }));
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            lock (_clientsLock)
            {
                _clients.Remove(client);
            }
            await client.DisposeAsync();

            Dispatcher.UIThread.Post(() =>
            {
                if (!IsMobileConnected)
                {
                    MobileDisconnected?.Invoke();
                }
            });
        }
    }

    public void SendTextMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var msg = new ConnectMessage(Guid.NewGuid().ToString("N"), text, "pc", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        lock (_history) { _history.Add(msg); }

        Dispatcher.UIThread.Post(() => MessageReceived?.Invoke(msg));
        _ = BroadcastAsync(JsonSerializer.Serialize(new { type = "message", message = msg }));
    }

    public async Task AddLocalFileAsync(string localPath, string relativePath)
    {
        await _folderManager.AddLocalFileAsync(localPath, relativePath);
        Dispatcher.UIThread.Post(() => FilesChanged?.Invoke());
        _ = BroadcastAsync(JsonSerializer.Serialize(new { type = "files-changed" }));
    }

    public async Task BroadcastAsync(string payload)
    {
        List<ConnectClient> snapshot;
        lock (_clientsLock)
        {
            snapshot = [.. _clients];
        }

        foreach (var client in snapshot)
        {
            _ = client.SendAsync(payload);
        }
    }

    private static IResult ServeMobileHtml()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("Filee.App.Resources.Connect.mobile.html");
        if (stream is null)
            return Results.NotFound();

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var html = reader.ReadToEnd();
        return Results.Text(html, "text/html; charset=utf-8");
    }

    private static IResult ServeEmbeddedResource(string resourceName, string contentType)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
            return Results.NotFound();

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Results.Text(reader.ReadToEnd(), contentType);
    }

    private void GenerateQrCode(string url)
    {
        try
        {
            if (_qrBitmap is not null)
            {
                _qrBitmap.Dispose();
                _qrBitmap = null;
            }

            using var generator = new QRCodeGenerator();
            var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
            using var qrCode = new PngByteQRCode(data);
            var bytes = qrCode.GetGraphic(20);
            using var ms = new MemoryStream(bytes);
            _qrBitmap = new Avalonia.Media.Imaging.Bitmap(ms);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not generate QR bitmap in current environment.");
        }
    }

    public static IPAddress GetLocalLanIp()
    {
        var candidates = new List<(IPAddress ip, int score)>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            var desc = (nic.Description + " " + nic.Name).ToLowerInvariant();
            var isVirtual = desc.Contains("vethernet") || desc.Contains("wsl") || desc.Contains("virtual") ||
                            desc.Contains("hyper-v") || desc.Contains("docker") || desc.Contains("vmware") ||
                            desc.Contains("virtualbox") || desc.Contains("tailscale") || desc.Contains("zerotier");

            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g => g.Address != null &&
                                                             !IPAddress.Any.Equals(g.Address) &&
                                                             !IPAddress.None.Equals(g.Address));

            foreach (var addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    LocalWebServer.IsPrivateIp(addr.Address))
                {
                    int score = 0;
                    if (hasGateway) score += 100;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 50;
                    else if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score += 40;
                    if (isVirtual) score -= 80;

                    candidates.Add((addr.Address, score));
                }
            }
        }

        var best = candidates.OrderByDescending(c => c.score).FirstOrDefault();
        return best.ip ?? IPAddress.Loopback;
    }

    private static int FindFreePort(IPAddress ip, int startPort)
    {
        for (var port = startPort; port < startPort + 500; port++)
        {
            try
            {
                var listener = new TcpListener(ip, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException) { }
        }
        return startPort;
    }

    public async ValueTask DisposeAsync()
    {
        await StopSessionAsync();
    }
}

public sealed class ConnectClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    public string Id { get; }
    public string DeviceId { get; }
    public string Role { get; }
    public string DeviceName { get; }
    public WebSocket Socket { get; }

    public ConnectClient(string id, string deviceId, string role, string deviceName, WebSocket socket)
    {
        Id = id;
        DeviceId = deviceId;
        Role = role;
        DeviceName = deviceName;
        Socket = socket;
    }

    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        if (Socket.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Socket.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Socket.State == WebSocketState.Open || Socket.State == WebSocketState.CloseReceived)
            {
                await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch { }
        finally
        {
            Socket.Dispose();
            _sendLock.Dispose();
        }
    }
}
