using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services.Capture;

public sealed class AirPlayMirrorEngine
{
    private readonly ILogger? _logger;
    private readonly MobileMirrorService _mirrorService;
    private TcpListener? _rtspListener;
    private UdpClient? _mdnsClient;
    private CancellationTokenSource? _cts;

    public const int AirPlayPort = 7000;
    public string ServerName { get; } = $"Filee-{Environment.MachineName}";
    public bool IsListening => _cts != null && !_cts.IsCancellationRequested;

    public AirPlayMirrorEngine(MobileMirrorService mirrorService, ILogger? logger = null)
    {
        _mirrorService = mirrorService;
        _logger = logger;
    }

    public static string GetMacAddressString()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
            if (nic != null)
            {
                var bytes = nic.GetPhysicalAddress().GetAddressBytes();
                if (bytes.Length == 6)
                {
                    return string.Join(":", bytes.Select(b => b.ToString("X2")));
                }
            }
        }
        catch { }

        return "48:2A:E3:55:66:77";
    }

    public void Start()
    {
        Stop();

        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            // 1. Start RTSP TCP Listener on port 7000
            _rtspListener = new TcpListener(IPAddress.Any, AirPlayPort);
            _rtspListener.Start();

            // 2. Start mDNS Multicast Listener on UDP 5353
            StartMdnsResponder(cts.Token);

            // 3. Listen for incoming AirPlay connections
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var client = await _rtspListener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                        _ = HandleAirPlayClientAsync(client, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "AirPlay accept error");
                    }
                }
            }, cts.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to start AirPlay engine on port {Port}", AirPlayPort);
        }
    }

    private void StartMdnsResponder(CancellationToken ct)
    {
        try
        {
            var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.ExclusiveAddressUse = false;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
            udp.JoinMulticastGroup(IPAddress.Parse("224.0.0.251"));
            _mdnsClient = udp;

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var res = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                        var data = res.Buffer;

                        // Check if query is looking for airplay or raop
                        var text = Encoding.ASCII.GetString(data);
                        if (text.Contains("_airplay") || text.Contains("_raop"))
                        {
                            await SendMdnsAnnouncementAsync(udp, res.RemoteEndPoint, ct).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch { }
                }
            }, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "mDNS responder socket bind info");
        }
    }

    private async Task SendMdnsAnnouncementAsync(UdpClient udp, IPEndPoint remote, CancellationToken ct)
    {
        try
        {
            // Build minimal mDNS response packet
            var mac = GetMacAddressString();
            var responsePayload = Encoding.UTF8.GetBytes(
                $"model=AppleTV3,2\ndeviceid={mac}\nfeatures=0x5A7FFFF7,0x1E\nsrcvers=220.68\nflags=0x4\nvv=2\nname={ServerName}");

            await udp.SendAsync(responsePayload, responsePayload.Length, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353)).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task HandleAirPlayClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            _mirrorService.OnConnected();
            var buffer = new byte[8192];

            try
            {
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    int bytesRead = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (bytesRead <= 0) break;

                    var request = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    // Parse CSeq
                    var cseq = "1";
                    var cseqIndex = request.IndexOf("CSeq:", StringComparison.OrdinalIgnoreCase);
                    if (cseqIndex >= 0)
                    {
                        var end = request.IndexOfAny(['\r', '\n'], cseqIndex);
                        if (end > cseqIndex + 5)
                        {
                            cseq = request.Substring(cseqIndex + 5, end - (cseqIndex + 5)).Trim();
                        }
                    }

                    // Handle RTSP / HTTP queries
                    if (request.StartsWith("GET /server-info", StringComparison.OrdinalIgnoreCase) ||
                        request.StartsWith("GET /info", StringComparison.OrdinalIgnoreCase))
                    {
                        var mac = GetMacAddressString();
                        var plist = $"""
                            <?xml version="1.0" encoding="UTF-8"?>
                            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                            <plist version="1.0">
                            <dict>
                            <key>deviceID</key>
                            <string>{mac}</string>
                            <key>model</key>
                            <string>AppleTV3,2</string>
                            <key>name</key>
                            <string>{ServerName}</string>
                            <key>features</key>
                            <integer>1484824567</integer>
                            <key>statusFlags</key>
                            <integer>4</integer>
                            <key>sourceVersion</key>
                            <string>220.68</string>
                            <key>vv</key>
                            <integer>2</integer>
                            </dict>
                            </plist>
                            """;

                        var plistBytes = Encoding.UTF8.GetBytes(plist);
                        var response = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nContent-Type: text/x-apple-plist+xml\r\nContent-Length: {plistBytes.Length}\r\n\r\n";
                        var headerBytes = Encoding.UTF8.GetBytes(response);

                        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
                        await stream.WriteAsync(plistBytes, ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // Generic 200 OK for RTSP handshakes (SETUP, RECORD, OPTIONS, POST /feedback, etc.)
                        var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\n\r\n";
                        var respBytes = Encoding.UTF8.GetBytes(resp);
                        await stream.WriteAsync(respBytes, ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "AirPlay client stream closed");
            }
            finally
            {
                _mirrorService.OnDisconnected();
            }
        }
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _cts = null;

            _rtspListener?.Stop();
            _rtspListener = null;

            _mdnsClient?.Close();
            _mdnsClient?.Dispose();
            _mdnsClient = null;
        }
        catch { }
    }
}
