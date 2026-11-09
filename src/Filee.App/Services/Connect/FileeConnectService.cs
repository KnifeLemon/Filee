using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using QRCoder;

namespace Filee.App.Services.Connect;

public sealed class FileeConnectService : IAsyncDisposable
{
    private readonly ILogger<FileeConnectService> _log;
    private readonly SharedFolderManager _folderManager;
    private readonly List<ConnectMessage> _history = [];
    private readonly List<(WebSocket Socket, string Role, string DeviceName)> _clients = [];
    private readonly object _clientsLock = new();

    private WebApplication? _app;
    private string? _token;
    private string? _mobileUrl;
    private string? _localhostUrl;
    private Avalonia.Media.Imaging.Bitmap? _qrBitmap;
    private string? _connectedDeviceName;
    private int _port;
    private bool _isRunning;
    private bool _httpMobileConnected;

    public SharedFolderManager FolderManager => _folderManager;
    public string? Token => _token;
    public string? MobileUrl => _mobileUrl;
    public string? LocalhostUrl => _localhostUrl;
    public Avalonia.Media.Imaging.Bitmap? QrBitmap => _qrBitmap;
    public string? ConnectedDeviceName => _connectedDeviceName;
    public bool IsMobileConnected => _httpMobileConnected || _clients.Any(c => c.Role == "mobile");
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

    public FileeConnectService(ILogger<FileeConnectService> log, SharedFolderManager folderManager)
    {
        _log = log;
        _folderManager = folderManager;
    }

    public async Task StartSessionAsync()
    {
        if (_isRunning)
            await StopSessionAsync();

        _httpMobileConnected = false;
        _folderManager.Initialize();
        lock (_history) { _history.Clear(); }
        lock (_clientsLock) { _clients.Clear(); }

        _token = Guid.NewGuid().ToString("N");
        _port = FindFreePort(3000);
        var lanIp = GetLocalLanIp();
        _mobileUrl = $"http://{lanIp}:{_port}/mobile/{_token}";
        _localhostUrl = $"http://localhost:{_port}/mobile/{_token}";

        GenerateQrCode(_mobileUrl);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.ListenAnyIP(_port);
            options.Limits.MaxRequestBodySize = SharedFolderManager.MaxFileSize + 10 * 1024 * 1024;
        });
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.UseDeveloperExceptionPage();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            context.Response.Headers["Access-Control-Allow-Headers"] = "*";
            context.Response.Headers["Access-Control-Allow-Methods"] = "*";
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            await next();
        });
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });

        ConfigureRoutes(app);

        await app.StartAsync();
        _app = app;
        _isRunning = true;
        _log.LogInformation("Filee Connect server started at {Url}", _mobileUrl);
    }

    private void GenerateQrCode(string url)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var pngQrCode = new PngByteQRCode(qrCodeData);
        var qrBytes = pngQrCode.GetGraphic(20);

        try
        {
            using var ms = new MemoryStream(qrBytes);
            _qrBitmap = new Avalonia.Media.Imaging.Bitmap(ms);
        }
        catch { }
    }

    private static int FindFreePort(int preferred = 3000)
    {
        for (var p = 3000; p <= 3100; p++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, p);
                listener.Start();
                listener.Stop();
                return p;
            }
            catch { }
        }
        var randomListener = new TcpListener(IPAddress.Any, 0);
        randomListener.Start();
        var port = ((IPEndPoint)randomListener.LocalEndpoint).Port;
        randomListener.Stop();
        return port;
    }

    public static string GetLocalLanIp()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                             && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                             && ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .OrderByDescending(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 2
                                       : ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 1 : 0)
                .ToList();

            foreach (var ni in interfaces)
            {
                var props = ni.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g => !IPAddress.IsLoopback(g.Address) && !g.Address.Equals(IPAddress.Any));
                var unicast = props.UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork
                                         && !IPAddress.IsLoopback(u.Address) && !u.Address.ToString().StartsWith("169.254."));
                if (unicast is not null && hasGateway)
                    return unicast.Address.ToString();
            }

            foreach (var ni in interfaces)
            {
                var unicast = ni.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork
                                         && !IPAddress.IsLoopback(u.Address) && !u.Address.ToString().StartsWith("169.254."));
                if (unicast is not null)
                    return unicast.Address.ToString();
            }
        }
        catch { }

        return "127.0.0.1";
    }

    private void ConfigureRoutes(WebApplication app)
    {
        IResult ServeMobileHtml(HttpContext ctx)
        {
            var ua = ctx.Request.Headers.UserAgent.ToString();
            string devName = "Mobile Device";
            if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase)) devName = "iPhone";
            else if (ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)) devName = "iPad";
            else if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase)) devName = "Android Device";

            if (!_httpMobileConnected)
            {
                _httpMobileConnected = true;
                _connectedDeviceName = devName;
                Dispatcher.UIThread.Post(() => MobileConnected?.Invoke(devName));
            }
            return ServeMobileHtmlInternal();
        }

        IResult ServeMobileHtmlInternal()
        {
            var html = ReadEmbeddedAsset("mobile.html");
            var script = $"<script>window.ACTIVE_TOKEN = '{_token}';</script>";
            var headIndex = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
            if (headIndex >= 0)
                html = html.Insert(headIndex + "<head>".Length, script);
            else
                html = script + html;
            return Results.Text(html, "text/html", Encoding.UTF8);
        }

        app.MapGet("/", (HttpContext ctx) => ServeMobileHtml(ctx));
        app.MapGet("/mobile", (HttpContext ctx) => ServeMobileHtml(ctx));
        app.MapGet("/mobile/{token}", (HttpContext ctx, string token) => ServeMobileHtml(ctx));
        app.MapGet("/connect", (HttpContext ctx) => ServeMobileHtml(ctx));

        // Static files (CSS / JS)
        app.MapGet("/css/{name}", (string name) =>
        {
            var content = ReadEmbeddedAsset(name);
            return Results.Text(content, "text/css", Encoding.UTF8);
        });

        app.MapGet("/js/{name}", (string name) =>
        {
            var content = ReadEmbeddedAsset(name);
            return Results.Text(content, "application/javascript", Encoding.UTF8);
        });

        // REST API: Session
        app.MapGet("/api/session", () => Results.Json(new
        {
            token = _token,
            mobileURL = _mobileUrl,
            localhostURL = _localhostUrl,
            expiresAt = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeMilliseconds(),
        }));

        // REST API: List Files
        app.MapGet("/api/files", (HttpContext ctx) =>
        {
            if (!ValidateAuth(ctx)) return Results.Unauthorized();
            return Results.Json(new
            {
                files = _folderManager.GetFiles(),
                storage = _folderManager.GetStorageUsage()
            });
        });

        // REST API: Upload Files
        app.MapPost("/api/files", async (HttpContext ctx) =>
        {
            if (!ValidateAuth(ctx)) return Results.Unauthorized();
            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart form content." });

            var form = await ctx.Request.ReadFormAsync();
            var files = form.Files;
            var paths = form["paths"].ToArray();

            if (files.Count == 0)
                return Results.BadRequest(new { error = "No files selected." });

            var role = ctx.Request.Headers["x-device-role"].ToString();
            if (string.IsNullOrEmpty(role)) role = "mobile";

            var saved = new List<SharedFileMetadata>();
            try
            {
                for (var i = 0; i < files.Count; i++)
                {
                    var file = files[i];
                    var relPath = paths != null && i < paths.Length && !string.IsNullOrWhiteSpace(paths[i]) ? paths[i]! : file.FileName;
                    await using var stream = file.OpenReadStream();
                    var item = await _folderManager.SaveStreamAsync(stream, file.FileName, relPath, role, file.ContentType);
                    saved.Add(item.ToMetadata());
                }

                Broadcast(new { type = "files-changed", action = "added", files = saved });
                Dispatcher.UIThread.Post(() => FilesChanged?.Invoke());

                return Results.Json(new
                {
                    success = true,
                    files = saved,
                    storage = _folderManager.GetStorageUsage()
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // REST API: File Preview
        app.MapGet("/api/files/{id}/preview", (string id, HttpContext ctx) =>
        {
            var file = _folderManager.GetFile(id);
            if (file is null || !File.Exists(file.DiskPath))
                return Results.NotFound(new { error = "File not found" });

            return Results.File(file.DiskPath, file.MimeType, enableRangeProcessing: true);
        });

        // REST API: File Download
        app.MapGet("/api/files/{id}/download", (string id, HttpContext ctx) =>
        {
            var file = _folderManager.GetFile(id);
            if (file is null || !File.Exists(file.DiskPath))
                return Results.NotFound(new { error = "File not found" });

            return Results.File(file.DiskPath, "application/octet-stream", file.Name, enableRangeProcessing: true);
        });

        // REST API: Folder ZIP
        app.MapGet("/api/folder.zip", (string? path, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "Folder path required." });

            var folderName = Path.GetFileName(path.TrimEnd('/'));
            var ms = new MemoryStream();
            _folderManager.WriteFolderZip(path, ms);
            ms.Position = 0;

            return Results.File(ms, "application/zip", $"{folderName}.zip");
        });

        // REST API: Delete File
        app.MapDelete("/api/files/{id}", (string id, HttpContext ctx) =>
        {
            if (!ValidateAuth(ctx)) return Results.Unauthorized();
            if (!_folderManager.DeleteFile(id))
                return Results.NotFound(new { error = "File not found" });

            Broadcast(new { type = "files-changed", action = "deleted", id });
            Dispatcher.UIThread.Post(() => FilesChanged?.Invoke());
            return Results.Json(new { success = true });
        });

        // REST API: Delete Folder
        app.MapDelete("/api/folder", (string? path, HttpContext ctx) =>
        {
            if (!ValidateAuth(ctx)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "Folder path required." });

            var deleted = _folderManager.DeleteFolder(path);
            Broadcast(new { type = "files-changed", action = "deleted-folder", path });
            Dispatcher.UIThread.Post(() => FilesChanged?.Invoke());
            return Results.Json(new { success = true, deleted });
        });

        // WebSocket
        app.Map("/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }

            var token = ctx.Request.Query["token"].ToString();
            if (!string.IsNullOrEmpty(token) && !string.Equals(token, _token, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 403;
                return;
            }

            var role = ctx.Request.Query["role"].ToString();
            if (string.IsNullOrEmpty(role)) role = "mobile";

            var devName = ctx.Request.Query["deviceName"].ToString();
            if (string.IsNullOrWhiteSpace(devName))
            {
                var ua = ctx.Request.Headers["User-Agent"].ToString();
                if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase)) devName = "iPhone";
                else if (ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)) devName = "iPad";
                else if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase)) devName = "Android Device";
                else devName = role == "pc" ? "PC" : "Mobile Device";
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await HandleWebSocketClientAsync(ws, role, devName);
        });
    }

    private bool ValidateAuth(HttpContext ctx)
    {
        var token = ctx.Request.Headers["x-pairing-token"].ToString();
        if (string.IsNullOrEmpty(token))
            token = ctx.Request.Query["token"].ToString();
        if (!string.IsNullOrEmpty(token))
            return string.Equals(token, _token, StringComparison.OrdinalIgnoreCase);
        return _isRunning;
    }

    private async Task HandleWebSocketClientAsync(WebSocket ws, string role, string deviceName)
    {
        lock (_clientsLock)
        {
            _clients.Add((ws, role, deviceName));
            if (role == "mobile")
            {
                _connectedDeviceName = deviceName;
            }
        }

        if (role == "mobile")
        {
            Dispatcher.UIThread.Post(() => MobileConnected?.Invoke(deviceName));
        }

        // Send initial state
        var initialState = new
        {
            type = "initial-state",
            history = MessageHistory,
            expiresAt = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeMilliseconds(),
            deviceName = _connectedDeviceName,
            pc = _clients.Count(c => c.Role == "pc"),
            mobile = _clients.Count(c => c.Role == "mobile")
        };
        await SendJsonAsync(ws, initialState);

        BroadcastPresence();

        var buffer = new byte[65536];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var jsonStr = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    HandleClientMessage(jsonStr, role);
                }
            }
        }
        catch { }
        finally
        {
            lock (_clientsLock)
            {
                _clients.RemoveAll(c => c.Socket == ws);
                if (!_clients.Any(c => c.Role == "mobile"))
                {
                    Dispatcher.UIThread.Post(() => MobileDisconnected?.Invoke());
                }
            }
            BroadcastPresence();
        }
    }

    private void HandleClientMessage(string rawJson, string role)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl))
                return;

            var type = typeEl.GetString();
            if (type == "text" && root.TryGetProperty("text", out var textEl))
            {
                var text = textEl.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var msg = new ConnectMessage(
                        Guid.NewGuid().ToString("N"),
                        text,
                        role,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                    lock (_history)
                    {
                        _history.Add(msg);
                        if (_history.Count > 200)
                            _history.RemoveAt(0);
                    }

                    Broadcast(new { type = "new-message", message = msg });
                    Dispatcher.UIThread.Post(() => MessageReceived?.Invoke(msg));
                }
            }
            else if (type == "end-session")
            {
                Dispatcher.UIThread.Post(() => _ = StopSessionAsync());
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not parse incoming WebSocket message");
        }
    }

    public void SendTextMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var msg = new ConnectMessage(
            Guid.NewGuid().ToString("N"),
            text,
            "pc",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        lock (_history)
        {
            _history.Add(msg);
            if (_history.Count > 200)
                _history.RemoveAt(0);
        }

        Broadcast(new { type = "new-message", message = msg });
        Dispatcher.UIThread.Post(() => MessageReceived?.Invoke(msg));
    }

    private void BroadcastPresence()
    {
        int pc, mobile;
        string? devName;
        lock (_clientsLock)
        {
            pc = _clients.Count(c => c.Role == "pc");
            mobile = _clients.Count(c => c.Role == "mobile");
            devName = _connectedDeviceName;
        }

        Broadcast(new
        {
            type = "presence",
            pc,
            mobile,
            deviceName = devName
        });
    }

    private void Broadcast(object data)
    {
        var json = JsonSerializer.Serialize(data);
        var bytes = Encoding.UTF8.GetBytes(json);

        List<WebSocket> targets;
        lock (_clientsLock)
        {
            targets = _clients.Where(c => c.Socket.State == WebSocketState.Open).Select(c => c.Socket).ToList();
        }

        foreach (var ws in targets)
        {
            _ = SendBytesAsync(ws, bytes);
        }
    }

    private static async Task SendJsonAsync(WebSocket ws, object data)
    {
        var json = JsonSerializer.Serialize(data);
        var bytes = Encoding.UTF8.GetBytes(json);
        await SendBytesAsync(ws, bytes);
    }

    private static async Task SendBytesAsync(WebSocket ws, byte[] bytes)
    {
        if (ws.State != WebSocketState.Open) return;
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        catch { }
    }

    public async Task StopSessionAsync()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _httpMobileConnected = false;
        Broadcast(new { type = "session-ended", reason = "Session ended by user." });

        lock (_clientsLock)
        {
            foreach (var client in _clients)
            {
                try
                {
                    client.Socket.Abort();
                }
                catch { }
            }
            _clients.Clear();
        }

        if (_app is not null)
        {
            try
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
            catch { }
            _app = null;
        }

        Dispatcher.UIThread.Post(() => SessionEnded?.Invoke("Session ended."));
        _log.LogInformation("Filee Connect server stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopSessionAsync();
    }

    private static string ReadEmbeddedAsset(string assetName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var fullResource = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(assetName, StringComparison.OrdinalIgnoreCase));

        if (fullResource is null)
            return $"/* Asset not found: {assetName} */";

        using var stream = asm.GetManifestResourceStream(fullResource);
        if (stream is null)
            return $"/* Resource stream null: {assetName} */";

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
