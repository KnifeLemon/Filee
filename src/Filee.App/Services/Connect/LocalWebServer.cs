using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services.Connect;

/// <summary>
/// A lightweight, strictly bound local HTTP/WebSocket server host.
/// Enforces private IP binding, Origin and Host validation, and secure HTTP response headers.
/// </summary>
public sealed class LocalWebServer : IAsyncDisposable
{
    private readonly ILogger? _log;
    private WebApplication? _app;
    private bool _isRunning;

    public IPAddress LanIp { get; }
    public int Port { get; }
    public string ExpectedHost => LanIp.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{LanIp}]:{Port}" : $"{LanIp}:{Port}";
    public bool IsRunning => _isRunning;

    public LocalWebServer(IPAddress lanIp, int port, ILogger? log = null)
    {
        if (!IsPrivateIp(lanIp))
            throw new InvalidOperationException($"Refusing to bind to non-private address: {lanIp}. Expected RFC 1918, ULA, link-local, or loopback.");

        LanIp = lanIp;
        Port = port;
        _log = log;
    }

    /// <summary>
    /// Checks if the IP address is an RFC 1918, ULA, link-local, or loopback address.
    /// </summary>
    public static bool IsPrivateIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 (link-local)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return true;
            var bytes = ip.GetAddressBytes();
            // Unique Local Address (ULA) fc00::/7 (0xfc or 0xfd)
            if ((bytes[0] & 0xfe) == 0xfc) return true;
            return false;
        }

        return false;
    }

    public async Task StartAsync(Action<WebApplication> configureRoutes, CancellationToken ct = default)
    {
        if (_isRunning)
            return;

        var options = new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Production"
        };

        var builder = WebApplication.CreateSlimBuilder(options);
        builder.Logging.ClearProviders();

        builder.WebHost.UseKestrel(kestrel =>
        {
            kestrel.Listen(LanIp, Port);
            kestrel.Limits.MaxRequestBodySize = 64L * 1024; // 64 KB default request body limit
        });

        builder.Services.Configure<FormOptions>(form =>
        {
            form.MultipartBodyLengthLimit = 500L * 1024 * 1024; // 500 MB per file on upload routes
        });

        var app = builder.Build();

        // 1. Enable WebSockets first so WebSocketMiddleware inspects headers and populates IHttpWebSocketFeature
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20)
        });

        // 2. Host and Origin verification middleware (defeats DNS rebinding and cross-origin attacks)
        app.Use(async (context, next) =>
        {
            var hostHeader = context.Request.Host.Value;
            if (string.IsNullOrEmpty(hostHeader) || !string.Equals(hostHeader, ExpectedHost, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // Enforce Origin check on WebSocket requests and state-changing HTTP methods
            var isStateChanging = HttpMethods.IsPost(context.Request.Method) ||
                                  HttpMethods.IsPut(context.Request.Method) ||
                                  HttpMethods.IsDelete(context.Request.Method) ||
                                  HttpMethods.IsPatch(context.Request.Method);

            var isWs = context.WebSockets.IsWebSocketRequest ||
                       string.Equals(context.Request.Headers.Upgrade, "websocket", StringComparison.OrdinalIgnoreCase);

            if (isStateChanging || isWs)
            {
                if (context.Request.Headers.TryGetValue("Origin", out var originValues) && originValues.Count > 0)
                {
                    var origin = originValues.ToString();
                    if (!string.IsNullOrEmpty(origin))
                    {
                        if (Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
                        {
                            if (!string.Equals(originUri.Authority, ExpectedHost, StringComparison.OrdinalIgnoreCase))
                            {
                                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                return;
                            }
                        }
                        else
                        {
                            context.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return;
                        }
                    }
                }
            }

            await next(context);
        });

        // 3. Base security response headers
        app.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await next(context);
        });

        // Map feature-specific routes
        configureRoutes(app);

        _app = app;
        await _app.StartAsync(ct);
        _isRunning = true;
        _log?.LogInformation("LocalWebServer started on {Ip}:{Port}", LanIp, Port);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning || _app is null)
            return;

        try
        {
            await _app.StopAsync(ct);
            await _app.DisposeAsync();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Error while stopping LocalWebServer");
        }
        finally
        {
            _app = null;
            _isRunning = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
