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
    private int _failedPinAttempts;
    private DateTime? _pinLockoutUntilUtc;
    private System.Threading.Timer? _pinTimer;

    private string? _mobileUrl;
    private string? _localhostUrl;
    private IPAddress _lanIp = IPAddress.Loopback;
    private Avalonia.Media.Imaging.Bitmap? _qrBitmap;
    private int _port;
    private bool _isRunning;

    public SharedFolderManager FolderManager => _folderManager;
    public string? PairingSecret => _pairingSecret;
    public string CurrentPin => _currentPin;
    public int PinSecondsRemaining => Math.Max(0, (int)(_pinExpiresAtUtc - DateTime.UtcNow).TotalSeconds);
    public string? MobileUrl => _mobileUrl;
    public string? LocalhostUrl => _localhostUrl;
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
    public event Action? PairedDevicesChanged;

    public FileeConnectService(ILogger<FileeConnectService> log, SharedFolderManager folderManager)
    {
        _log = log;
        _folderManager = folderManager;
    }

    public async Task StartSessionAsync()
    {
        if (_isRunning)
            return;

        _folderManager.Initialize();
        lock (_history) { _history.Clear(); }
        lock (_clientsLock) { _clients.Clear(); }
        _pairedDevices.Clear();
        _failedPinAttempts = 0;
        _pinLockoutUntilUtc = null;

        _pairingSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        RefreshPin();

        _pinTimer = new System.Threading.Timer(_ =>
        {
            if (!_isRunning) return;
            if (DateTime.UtcNow >= _pinExpiresAtUtc)
            {
                RefreshPin();
                Dispatcher.UIThread.Post(() => PinRefreshed?.Invoke());
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        _lanIp = GetLocalLanIp();
        _port = FindFreePort(_lanIp, 3000);

        _mobileUrl = $"http://{_lanIp}:{_port}/mobile/{_pairingSecret}";
        _localhostUrl = $"http://localhost:{_port}/mobile/{_pairingSecret}";

        GenerateQrCode(_mobileUrl);

        _server = new LocalWebServer(_lanIp, _port, _log);
        await _server.StartAsync(ConfigureRoutes);

        _isRunning = true;
        _log.LogInformation("FileeConnect session started at {Url}", _mobileUrl);
    }

    private void RefreshPin()
    {
        _currentPin = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        _pinExpiresAtUtc = DateTime.UtcNow.AddSeconds(30);
    }

    public async Task StopSessionAsync()
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
            var secretBytes = Encoding.UTF8.GetBytes(_pairingSecret ?? "");
            var inputBytes = Encoding.UTF8.GetBytes(secret ?? "");

            if (!CryptographicOperations.FixedTimeEquals(inputBytes, secretBytes))
            {
                return Results.NotFound();
            }

            var device = PairNewDevice(context.Request.Headers.UserAgent.ToString());
            SetDeviceTokenCookie(context, device.Token);

            return ServeMobileHtml();
        });

        // Direct Mobile Page (either already paired via cookie or shows PIN prompt)
        app.MapGet("/mobile", (HttpContext context) =>
        {
            return ServeMobileHtml();
        });
        app.MapGet("/", (HttpContext context) => Results.Redirect("/mobile"));
        app.MapGet("/connect", (HttpContext context) => Results.Redirect("/mobile"));

        // PIN Pairing API: POST /api/pair/pin
        app.MapPost("/api/pair/pin", async (HttpContext context) =>
        {
            if (_pinLockoutUntilUtc is { } lockout && DateTime.UtcNow < lockout)
            {
                var remainingSec = (int)(lockout - DateTime.UtcNow).TotalSeconds;
                return Results.Json(new { error = "locked_out", remainingSeconds = remainingSec }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            PinPairRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<PinPairRequest>(context.Request.Body);
            }
            catch
            {
                return Results.BadRequest(new { error = "invalid_json" });
            }

            if (req is null || string.IsNullOrWhiteSpace(req.Pin))
            {
                return Results.BadRequest(new { error = "missing_pin" });
            }

            var pinBytes = Encoding.UTF8.GetBytes(req.Pin.Trim());
            var currentBytes = Encoding.UTF8.GetBytes(_currentPin);

            if (DateTime.UtcNow < _pinExpiresAtUtc && CryptographicOperations.FixedTimeEquals(pinBytes, currentBytes))
            {
                // Consumed OTP immediately!
                _failedPinAttempts = 0;
                RefreshPin();
                Dispatcher.UIThread.Post(() => PinRefreshed?.Invoke());

                var device = PairNewDevice(req.DeviceName ?? context.Request.Headers.UserAgent.ToString());
                SetDeviceTokenCookie(context, device.Token);

                return Results.Ok(new { token = device.Token, deviceName = device.Name, deviceId = device.Id });
            }

            _failedPinAttempts++;
            if (_failedPinAttempts >= 5)
            {
                _pinLockoutUntilUtc = DateTime.UtcNow.AddMinutes(2);
                return Results.Json(new { error = "locked_out", remainingSeconds = 120 }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            return Results.Json(new { error = "invalid_pin", attemptsRemaining = 5 - _failedPinAttempts }, statusCode: StatusCodes.Status401Unauthorized);
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

            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var json = JsonDocument.Parse(body);
            var text = json.RootElement.GetProperty("text").GetString();

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
                return Results.Unauthorized();

            context.Response.ContentType = "application/zip";
            context.Response.Headers.ContentDisposition = "attachment; filename=\"shared-folder.zip\"";

            var files = _folderManager.GetFiles();
            using var zip = new ZipArchive(context.Response.Body, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var meta in files)
            {
                ct.ThrowIfCancellationRequested();
                var diskPath = _folderManager.GetFullPath(meta.Id);
                if (File.Exists(diskPath))
                {
                    var entry = zip.CreateEntry(meta.Name, CompressionLevel.Fastest);
                    using var entryStream = entry.Open();
                    using var fileStream = new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    await fileStream.CopyToAsync(entryStream, ct).ConfigureAwait(false);
                }
            }
            return Results.Empty;
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
    }

    private PairedDevice PairNewDevice(string userAgent)
    {
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
            SameSite = SameSiteMode.Strict,
            HttpOnly = false,
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

        if (string.IsNullOrEmpty(token) && context.Request.Query.TryGetValue("token", out var queryToken))
        {
            token = queryToken.ToString();
        }

        if (string.IsNullOrEmpty(token))
            return false;

        var tokenBytes = Encoding.UTF8.GetBytes(token);
        foreach (var pair in _pairedDevices)
        {
            var candidateBytes = Encoding.UTF8.GetBytes(pair.Key);
            if (CryptographicOperations.FixedTimeEquals(tokenBytes, candidateBytes))
            {
                device = pair.Value with { LastSeen = DateTime.UtcNow };
                _pairedDevices[pair.Key] = device;
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

    private async Task BroadcastAsync(string payload)
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
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            var props = nic.GetIPProperties();
            foreach (var addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    LocalWebServer.IsPrivateIp(addr.Address))
                {
                    return addr.Address;
                }
            }
        }
        return IPAddress.Loopback;
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
