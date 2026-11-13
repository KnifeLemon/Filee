using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services.Capture;

public sealed class AirPlayMirrorEngine
{
    private readonly ILogger? _logger;
    private readonly MobileMirrorService _mirrorService;
    private TcpListener? _rtspListener;
    private CancellationTokenSource? _cts;

    // 1PhoneMirror external process
    private Process? _onePhoneProcess;

    // Bonjour handles via dnssd.dll
    private IntPtr _airplaySdRef = IntPtr.Zero;
    private IntPtr _raopSdRef = IntPtr.Zero;

    // CLI fallback processes (dns-sd.exe)
    private Process? _dnsSdProcessAirplay;
    private Process? _dnsSdProcessRaop;

    // UDP fallback client
    private UdpClient? _udpFallback;

    public const int AirPlayPort = 7000;
    public string ServerName { get; } = $"Filee-{Environment.MachineName}";
    public bool IsListening => (_cts != null && !_cts.IsCancellationRequested) || (_onePhoneProcess != null && !_onePhoneProcess.HasExited);
    public string StatusMessage { get; private set; } = "Initializing...";
    public bool Is1PhoneMirrorActive => _onePhoneProcess != null && !_onePhoneProcess.HasExited;

    public AirPlayMirrorEngine(MobileMirrorService mirrorService, ILogger? logger = null)
    {
        _mirrorService = mirrorService;
        _logger = logger;
    }

    #region Native Bonjour (dnssd.dll) P/Invoke

    [DllImport("dnssd.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int DNSServiceRegister(
        out IntPtr sdRef,
        uint flags,
        uint interfaceIndex,
        string name,
        string regtype,
        string? domain,
        string? host,
        ushort port,
        ushort txtLen,
        byte[]? txtRecord,
        IntPtr callBack,
        IntPtr context
    );

    [DllImport("dnssd.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void DNSServiceRefDeallocate(IntPtr sdRef);

    #endregion

    public static string? Find1PhoneMirrorExe()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "1PhoneMirror", "1PhoneMirror.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "1PhoneMirror", "1PhoneMirror.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "1PhoneMirror", "1PhoneMirror.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "1PhoneMirror", "1PhoneMirror.exe"),
            @"C:\Program Files\1PhoneMirror\1PhoneMirror.exe"
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, "1PhoneMirror.exe");
            if (File.Exists(p)) return p;
        }

        return null;
    }

    public static string GetMacAddressString()
    {
        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            // Prefer Wi-Fi first, then Ethernet, ignoring filter and virtual adapters
            var preferred = nics.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                                                     && n.GetPhysicalAddress().GetAddressBytes().Length == 6
                                                     && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                                                     && !n.Description.Contains("Filter", StringComparison.OrdinalIgnoreCase))
                         ?? nics.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                                                     && n.GetPhysicalAddress().GetAddressBytes().Length == 6
                                                     && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                                                     && !n.Description.Contains("Filter", StringComparison.OrdinalIgnoreCase))
                         ?? nics.FirstOrDefault(n => n.GetPhysicalAddress().GetAddressBytes().Length == 6);

            if (preferred != null)
            {
                var bytes = preferred.GetPhysicalAddress().GetAddressBytes();
                if (bytes.Length == 6)
                {
                    return string.Join(":", bytes.Select(b => b.ToString("X2")));
                }
            }
        }
        catch { }

        return "38:7A:0E:02:40:B8";
    }

    public static string? GetLocalIpAddress()
    {
        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            var preferred = nics.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                                                     && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                                                     && !n.Description.Contains("Filter", StringComparison.OrdinalIgnoreCase))
                         ?? nics.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                                                     && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                                                     && !n.Description.Contains("Filter", StringComparison.OrdinalIgnoreCase));

            if (preferred != null)
            {
                var ip = preferred.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address))
                    ?.Address.ToString();
                if (!string.IsNullOrEmpty(ip)) return ip;
            }
        }
        catch { }

        return null;
    }

    public static byte[] BuildTxtRecord(params string[] entries)
    {
        using var ms = new MemoryStream();
        foreach (var entry in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(entry);
            if (bytes.Length > 255) continue;
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    public void Start()
    {
        Stop();

        // 1. Try launching 1PhoneMirror engine if present
        var onePhone = Find1PhoneMirrorExe();
        if (!string.IsNullOrEmpty(onePhone))
        {
            try
            {
                // Kill stale prior instance if any
                var existing = Process.GetProcessesByName("1PhoneMirror");
                foreach (var p in existing)
                {
                    try { p.Kill(); p.WaitForExit(1000); } catch { }
                }

                var psi = new ProcessStartInfo
                {
                    FileName = onePhone,
                    Arguments = $"--name \"{ServerName}\"",
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(onePhone) ?? ""
                };

                _onePhoneProcess = Process.Start(psi);
                if (_onePhoneProcess != null && !_onePhoneProcess.HasExited)
                {
                    StatusMessage = $"1PhoneMirror Engine Active ('{ServerName}')";
                    _logger?.LogInformation("Started 1PhoneMirror engine with name {Name}", ServerName);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to launch 1PhoneMirror engine, falling back to built-in listener");
            }
        }

        // 2. Fallback to built-in RTSP & Bonjour stack
        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            int boundPort = AirPlayPort;
            try
            {
                _rtspListener = new TcpListener(IPAddress.Any, AirPlayPort);
                _rtspListener.Start();
            }
            catch
            {
                // Port 7000 in use, pick dynamic port
                boundPort = 7001;
                _rtspListener = new TcpListener(IPAddress.Any, boundPort);
                _rtspListener.Start();
            }

            StartDiscoveryServices(boundPort, cts.Token);

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
            StatusMessage = $"Error starting on port {AirPlayPort}: {ex.Message}";
            _logger?.LogWarning(ex, "Failed to start AirPlay engine on port {Port}", AirPlayPort);
        }
    }

    private void StartDiscoveryServices(int port, CancellationToken ct)
    {
        if (TryRegisterNativeBonjour(port))
        {
            StatusMessage = $"Broadcasting as '{ServerName}' (Bonjour Active)";
            return;
        }

        if (TryRegisterDnsSdCli(port))
        {
            StatusMessage = $"Broadcasting as '{ServerName}' (CLI Active)";
            return;
        }

        StartUdpMdnsFallback(port, ct);
        StatusMessage = $"Broadcasting as '{ServerName}' (UDP mDNS)";
    }

    private bool TryRegisterNativeBonjour(int port)
    {
        try
        {
            var mac = GetMacAddressString();
            var macNoColons = mac.Replace(":", "").Replace("-", "").ToUpperInvariant();
            var networkPort = unchecked((ushort)IPAddress.HostToNetworkOrder((short)port));

            // 1. _airplay._tcp matching 1PhoneMirror features
            var airPlayTxt = BuildTxtRecord(
                "model=AppleTV3,2",
                $"deviceid={mac}",
                "features=0x5A7FFEE6",
                "srcvers=220.68",
                "flags=0x4",
                "vv=2",
                "pk=b07727d6f6cd6e08b58ede525ec3cdeaa252ad9f683feb212ef8a205246554e7",
                "pi=2e388006-13ba-4041-9a67-25dd4a43d536"
            );

            int err1 = DNSServiceRegister(
                out _airplaySdRef,
                0,
                0,
                ServerName,
                "_airplay._tcp",
                null,
                null,
                networkPort,
                (ushort)airPlayTxt.Length,
                airPlayTxt,
                IntPtr.Zero,
                IntPtr.Zero
            );

            // 2. _raop._tcp matching 1PhoneMirror audio metadata
            var raopName = $"{macNoColons}@{ServerName}";
            var raopTxt = BuildTxtRecord(
                "am=AppleTV3,2",
                "ch=2",
                "cn=0,1,2,3",
                "da=true",
                "et=0,3,5",
                "ft=0x5A7FFEE6",
                "md=0,1,2",
                "pk=b07727d6f6cd6e08b58ede525ec3cdeaa252ad9f683feb212ef8a205246554e7",
                "pw=false",
                "rhd=5.6.0.0",
                "sf=0x4",
                "sr=44100",
                "ss=16",
                "sv=false",
                "tp=UDP",
                "txtvers=1",
                "vn=65537",
                "vs=220.68",
                "vv=2"
            );

            int err2 = DNSServiceRegister(
                out _raopSdRef,
                0,
                0,
                raopName,
                "_raop._tcp",
                null,
                null,
                networkPort,
                (ushort)raopTxt.Length,
                raopTxt,
                IntPtr.Zero,
                IntPtr.Zero
            );

            if (err1 == 0 && err2 == 0)
            {
                _logger?.LogInformation("Successfully registered AirPlay and RAOP via dnssd.dll on port {Port}", port);
                return true;
            }
        }
        catch (DllNotFoundException)
        {
            _logger?.LogDebug("dnssd.dll not found in library path");
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "dnssd.dll registration attempt failed");
        }

        return false;
    }

    private bool TryRegisterDnsSdCli(int port)
    {
        try
        {
            var mac = GetMacAddressString();
            var macNoColons = mac.Replace(":", "").Replace("-", "").ToUpperInvariant();

            var psiAirplay = new ProcessStartInfo
            {
                FileName = "dns-sd.exe",
                Arguments = $"-R \"{ServerName}\" _airplay._tcp local. {port} model=AppleTV3,2 deviceid={mac} features=0x5A7FFEE6 srcvers=220.68 flags=0x4 vv=2",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _dnsSdProcessAirplay = Process.Start(psiAirplay);

            var psiRaop = new ProcessStartInfo
            {
                FileName = "dns-sd.exe",
                Arguments = $"-R \"{macNoColons}@{ServerName}\" _raop._tcp local. {port} ch=2 cn=0,1,2,3 et=0,3,5 ft=0x5A7FFEE6 md=0,1,2 pw=false sr=44100 ss=16 tp=UDP vn=65537 vs=220.68 am=AppleTV3,2 sf=0x4",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _dnsSdProcessRaop = Process.Start(psiRaop);

            return _dnsSdProcessAirplay != null && !_dnsSdProcessAirplay.HasExited;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "dns-sd.exe CLI registration not available");
        }

        return false;
    }

    private void StartUdpMdnsFallback(int port, CancellationToken ct)
    {
        try
        {
            var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.ExclusiveAddressUse = false;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
            udp.JoinMulticastGroup(IPAddress.Parse("224.0.0.251"));
            _udpFallback = udp;

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var res = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                        var data = res.Buffer;

                        var text = Encoding.ASCII.GetString(data);
                        if (text.Contains("_airplay") || text.Contains("_raop"))
                        {
                            var mac = GetMacAddressString();
                            var announcement = BuildTxtRecord(
                                "model=AppleTV3,2",
                                $"deviceid={mac}",
                                "features=0x5A7FFEE6",
                                "srcvers=220.68",
                                "flags=0x4",
                                "vv=2"
                            );
                            await udp.SendAsync(announcement, announcement.Length, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353)).ConfigureAwait(false);
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
            _logger?.LogDebug(ex, "UDP mDNS fallback listener bind failed");
        }
    }

    private async Task HandleAirPlayClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            _mirrorService.OnConnected();
            var buffer = new byte[16384];

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

                    // Handle RTSP OPTIONS
                    if (request.StartsWith("OPTIONS", StringComparison.OrdinalIgnoreCase))
                    {
                        var optionsResp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nPublic: ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER, POST, GET\r\n\r\n";
                        var bytes = Encoding.UTF8.GetBytes(optionsResp);
                        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                    }
                    // Handle GET /info or GET /server-info
                    else if (request.StartsWith("GET /server-info", StringComparison.OrdinalIgnoreCase) ||
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
                            <integer>1518337766</integer>
                            <key>statusFlags</key>
                            <integer>4</integer>
                            <key>sourceVersion</key>
                            <string>220.68</string>
                            <key>vv</key>
                            <integer>2</integer>
                            <key>pi</key>
                            <string>2e388006-13ba-4041-9a67-25dd4a43d536</string>
                            <key>pk</key>
                            <string>b07727d6f6cd6e08b58ede525ec3cdeaa252ad9f683feb212ef8a205246554e7</string>
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
                    // Handle SETUP
                    else if (request.StartsWith("SETUP", StringComparison.OrdinalIgnoreCase))
                    {
                        var setupResp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nTransport: RTP/AVP/TCP;unicast;interleaved=0-1;mode=record\r\nSession: 1\r\n\r\n";
                        var bytes = Encoding.UTF8.GetBytes(setupResp);
                        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                    }
                    // Handle RECORD
                    else if (request.StartsWith("RECORD", StringComparison.OrdinalIgnoreCase))
                    {
                        var recordResp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nSession: 1\r\n\r\n";
                        var bytes = Encoding.UTF8.GetBytes(recordResp);
                        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                    }
                    // Handle TEARDOWN
                    else if (request.StartsWith("TEARDOWN", StringComparison.OrdinalIgnoreCase))
                    {
                        var teardownResp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\n\r\n";
                        var bytes = Encoding.UTF8.GetBytes(teardownResp);
                        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                        break;
                    }
                    // Generic 200 OK for POST /pair-setup, /feedback, etc.
                    else
                    {
                        var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nContent-Length: 0\r\n\r\n";
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
            // Kill 1PhoneMirror external process if launched by us
            if (_onePhoneProcess != null)
            {
                try
                {
                    if (!_onePhoneProcess.HasExited)
                    {
                        _onePhoneProcess.Kill();
                        _onePhoneProcess.WaitForExit(1000);
                    }
                }
                catch { }
                _onePhoneProcess = null;
            }

            _cts?.Cancel();
            _cts = null;

            _rtspListener?.Stop();
            _rtspListener = null;

            // Deallocate native dnssd.dll handles
            if (_airplaySdRef != IntPtr.Zero)
            {
                try { DNSServiceRefDeallocate(_airplaySdRef); } catch { }
                _airplaySdRef = IntPtr.Zero;
            }
            if (_raopSdRef != IntPtr.Zero)
            {
                try { DNSServiceRefDeallocate(_raopSdRef); } catch { }
                _raopSdRef = IntPtr.Zero;
            }

            // Terminate CLI processes if any
            try
            {
                if (_dnsSdProcessAirplay != null && !_dnsSdProcessAirplay.HasExited)
                {
                    _dnsSdProcessAirplay.Kill();
                }
                _dnsSdProcessAirplay = null;
            }
            catch { }

            try
            {
                if (_dnsSdProcessRaop != null && !_dnsSdProcessRaop.HasExited)
                {
                    _dnsSdProcessRaop.Kill();
                }
                _dnsSdProcessRaop = null;
            }
            catch { }

            // Close UDP fallback
            _udpFallback?.Close();
            _udpFallback?.Dispose();
            _udpFallback = null;
        }
        catch { }
    }
}
