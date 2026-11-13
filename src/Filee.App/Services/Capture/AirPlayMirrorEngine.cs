using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Filee.App.Services.Capture.AirPlayCrypto;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Filee.App.Services.Capture;

/// <summary>
/// Native, standalone AirPlay receiver engine for Filee.
/// Implements RTSP protocol on port 7000, FairPlay handshake, binary property lists,
/// and mirrors H.264 video streams over TCP on port 7100 piped into FFmpeg.
/// </summary>
public sealed class AirPlayMirrorEngine
{
    private readonly ILogger? _logger;
    private readonly MobileMirrorService _mirrorService;

    private TcpListener? _rtspListener;
    private TcpListener? _mirrorListener;
    private CancellationTokenSource? _cts;

    // Bonjour handles via dnssd.dll
    private IntPtr _airplaySdRef = IntPtr.Zero;
    private IntPtr _raopSdRef = IntPtr.Zero;

    // CLI fallback processes (dns-sd.exe)
    private Process? _dnsSdProcessAirplay;
    private Process? _dnsSdProcessRaop;

    // UDP fallback client
    private UdpClient? _udpFallback;

    // Cryptographic keys and state
    private readonly Ed25519PrivateKeyParameters _edPrivateKey;
    private readonly Ed25519PublicKeyParameters _edPublicKey;
    private readonly byte[] _edPublicKeyBytes;

    private byte[]? _ecdhSharedSecret;
    private byte[]? _fairplayKeyMsg;
    private byte[]? _aesKey;
    private byte[]? _aesIv;
    private byte[]? _decryptedAesKey;
    private ulong _streamConnectionId;

    // Mirror stream decryption
    private IBufferedCipher? _mirrorCipher;

    // Event and Timing listeners for AirPlay synchronization
    private TcpListener? _eventListener;
    private UdpClient? _timingClient;
    private int _eventPort = EventPort;
    private int _timingPort = TimingPort;

    // Cached SPS/PPS NAL units (Annex B format)
    private byte[]? _cachedSpsPps;

    public const int AirPlayPort = 7000;
    public const int MirrorPort = 7100;
    public const int TimingPort = 7003;
    public const int EventPort = 7002;

    public string ServerName { get; } = $"Filee-{Environment.MachineName}";
    public bool IsListening => _cts != null && !_cts.IsCancellationRequested;
    public string StatusMessage { get; private set; } = "Initializing...";

    public AirPlayMirrorEngine(MobileMirrorService mirrorService, ILogger? logger = null)
    {
        _mirrorService = mirrorService;
        _logger = logger;

        // Initialize Ed25519 signing keypair
        var keyGen = new Ed25519KeyPairGenerator();
        keyGen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var keyPair = keyGen.GenerateKeyPair();
        _edPrivateKey = (Ed25519PrivateKeyParameters)keyPair.Private;
        _edPublicKey = (Ed25519PublicKeyParameters)keyPair.Public;
        _edPublicKeyBytes = _edPublicKey.GetEncoded();
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

    public static string GetMacAddressString()
    {
        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

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

        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            int boundPort = AirPlayPort;
            try
            {
                _rtspListener = new TcpListener(IPAddress.Any, AirPlayPort);
                _rtspListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _rtspListener.Start();
            }
            catch
            {
                boundPort = 7001;
                _rtspListener = new TcpListener(IPAddress.Any, boundPort);
                _rtspListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _rtspListener.Start();
            }

            // Start mirror TCP listener on port 7100
            try
            {
                _mirrorListener = new TcpListener(IPAddress.Any, MirrorPort);
                _mirrorListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _mirrorListener.Start();
                _ = Task.Run(() => AcceptMirrorClientsAsync(_mirrorListener, cts.Token), cts.Token);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to start mirror stream listener on port {Port}", MirrorPort);
            }

            // Start event TCP listener (dynamic port)
            try
            {
                _eventListener = new TcpListener(IPAddress.Any, 0);
                _eventListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _eventListener.Start();
                _eventPort = ((IPEndPoint)_eventListener.LocalEndpoint).Port;
                _ = Task.Run(() => AcceptEventClientsAsync(_eventListener, cts.Token), cts.Token);
                _logger?.LogInformation("AirPlay event listener active on port {Port}", _eventPort);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to start AirPlay event listener");
            }

            // Start timing UDP listener (dynamic port)
            try
            {
                _timingClient = new UdpClient();
                _timingClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _timingClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                _timingPort = ((IPEndPoint)_timingClient.Client.LocalEndPoint!).Port;
                _ = Task.Run(() => RunTimingLoopAsync(_timingClient, cts.Token), cts.Token);
                _logger?.LogInformation("AirPlay timing (NTP) listener active on port {Port}", _timingPort);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to start AirPlay timing listener");
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
                        _logger?.LogDebug(ex, "AirPlay RTSP accept error");
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
            var pkHex = Convert.ToHexString(_edPublicKeyBytes).ToLowerInvariant();

            // 1. _airplay._tcp
            var airPlayTxt = BuildTxtRecord(
                "model=AppleTV3,2",
                $"deviceid={mac}",
                "features=0x5A7FFEE6",
                "srcvers=220.68",
                "flags=0x4",
                "vv=2",
                $"pk={pkHex}",
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

            // 2. _raop._tcp
            var raopName = $"{macNoColons}@{ServerName}";
            var raopTxt = BuildTxtRecord(
                "am=AppleTV3,2",
                "ch=2",
                "cn=0,1,2,3",
                "da=true",
                "et=0,3,5",
                "ft=0x5A7FFEE6",
                "md=0,1,2",
                $"pk={pkHex}",
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
            var pkHex = Convert.ToHexString(_edPublicKeyBytes).ToLowerInvariant();

            var psiAirplay = new ProcessStartInfo
            {
                FileName = "dns-sd.exe",
                Arguments = $"-R \"{ServerName}\" _airplay._tcp local. {port} model=AppleTV3,2 deviceid={mac} features=0x5A7FFEE6 srcvers=220.68 flags=0x4 vv=2 pk={pkHex}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _dnsSdProcessAirplay = Process.Start(psiAirplay);

            var psiRaop = new ProcessStartInfo
            {
                FileName = "dns-sd.exe",
                Arguments = $"-R \"{macNoColons}@{ServerName}\" _raop._tcp local. {port} ch=2 cn=0,1,2,3 et=0,3,5 ft=0x5A7FFEE6 md=0,1,2 pw=false sr=44100 ss=16 tp=UDP vn=65537 vs=220.68 am=AppleTV3,2 sf=0x4 pk={pkHex}",
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
                            var pkHex = Convert.ToHexString(_edPublicKeyBytes).ToLowerInvariant();
                            var announcement = BuildTxtRecord(
                                "model=AppleTV3,2",
                                $"deviceid={mac}",
                                "features=0x5A7FFEE6",
                                "srcvers=220.68",
                                "flags=0x4",
                                "vv=2",
                                $"pk={pkHex}"
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

    private async Task AcceptEventClientsAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _logger?.LogInformation("AirPlay event channel connected from {Endpoint}", client.Client.RemoteEndPoint);

                _ = Task.Run(async () =>
                {
                    using (client)
                    using (var s = client.GetStream())
                    {
                        var buf = new byte[1024];
                        while (!ct.IsCancellationRequested && client.Connected)
                        {
                            int r = await s.ReadAsync(buf, ct).ConfigureAwait(false);
                            if (r <= 0) break;
                        }
                    }
                }, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "AirPlay event accept error");
            }
        }
    }

    private async Task RunTimingLoopAsync(UdpClient timingClient, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await timingClient.ReceiveAsync(ct).ConfigureAwait(false);
                var buf = res.Buffer;
                if (buf.Length < 32) continue;

                var reply = new byte[32];
                reply[0] = 0x80;
                reply[1] = 0xd3; // timing response type
                reply[2] = buf[2];
                reply[3] = buf[3]; // echo sequence number

                var now = DateTimeOffset.UtcNow;
                ulong ntpSec = (ulong)now.ToUnixTimeSeconds() + 2208988800UL;
                ulong fracNs = (ulong)(now.Ticks % TimeSpan.TicksPerSecond) * 100;
                ulong ntpFrac = (fracNs * 0x100000000UL) / 1000000000UL;

                // Origin timestamp at offset 24 = echo client's transmit timestamp (bytes 24-31)
                Buffer.BlockCopy(buf, 24, reply, 24, 8);

                // Receive timestamp at offset 8
                WriteNtpTimestamp(reply, 8, ntpSec, ntpFrac);

                // Transmit timestamp at offset 16
                WriteNtpTimestamp(reply, 16, ntpSec, ntpFrac);

                await timingClient.SendAsync(reply, 32, res.RemoteEndPoint).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "AirPlay timing loop error");
            }
        }
    }

    private static void WriteNtpTimestamp(byte[] buf, int offset, ulong sec, ulong frac)
    {
        buf[offset + 0] = (byte)(sec >> 24);
        buf[offset + 1] = (byte)(sec >> 16);
        buf[offset + 2] = (byte)(sec >> 8);
        buf[offset + 3] = (byte)sec;
        buf[offset + 4] = (byte)(frac >> 24);
        buf[offset + 5] = (byte)(frac >> 16);
        buf[offset + 6] = (byte)(frac >> 8);
        buf[offset + 7] = (byte)frac;
    }

    private async Task HandleAirPlayClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            StatusMessage = "Connecting to iOS device...";

            try
            {
                var headerStream = new MemoryStream();
                var oneByte = new byte[1];

                while (!ct.IsCancellationRequested && client.Connected)
                {
                    headerStream.SetLength(0);
                    int matchState = 0;

                    // Asynchronously read RTSP header until "\r\n\r\n"
                    while (await stream.ReadAsync(oneByte.AsMemory(0, 1), ct).ConfigureAwait(false) > 0)
                    {
                        byte b = oneByte[0];
                        headerStream.WriteByte(b);

                        if (matchState == 0 && b == '\r') matchState = 1;
                        else if (matchState == 1 && b == '\n') matchState = 2;
                        else if (matchState == 2 && b == '\r') matchState = 3;
                        else if (matchState == 3 && b == '\n') { matchState = 4; break; }
                        else matchState = (b == '\r') ? 1 : 0;
                    }

                    if (headerStream.Length == 0 || matchState != 4) break;

                    var headerBytes = headerStream.ToArray();
                    var headerText = Encoding.UTF8.GetString(headerBytes);
                    var lines = headerText.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length == 0) break;

                    var reqLine = lines[0];
                    var parts = reqLine.Split(' ', 3);
                    var method = parts[0].ToUpperInvariant();
                    var uri = parts.Length > 1 ? parts[1] : "/";

                    // Parse CSeq and Content-Length
                    string cseq = "1";
                    int contentLength = 0;
                    foreach (var line in lines.Skip(1))
                    {
                        if (line.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase))
                        {
                            cseq = line.Substring(5).Trim();
                        }
                        else if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            _ = int.TryParse(line.Substring(15).Trim(), out contentLength);
                        }
                    }

                    // Read request body if present
                    byte[] body = [];
                    if (contentLength > 0)
                    {
                        body = new byte[contentLength];
                        int totalRead = 0;
                        while (totalRead < contentLength)
                        {
                            int r = await stream.ReadAsync(body.AsMemory(totalRead, contentLength - totalRead), ct).ConfigureAwait(false);
                            if (r <= 0) break;
                            totalRead += r;
                        }
                    }

                    // Dispatch request
                    await HandleRtspRequestAsync(stream, method, uri, cseq, body, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "AirPlay client stream closed");
            }
        }
    }

    private async Task HandleRtspRequestAsync(NetworkStream stream, string method, string uri, string cseq, byte[] body, CancellationToken ct)
    {
        switch (method)
        {
            case "OPTIONS":
                {
                    var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nPublic: ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER, POST, GET\r\n\r\n";
                    await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                    break;
                }

            case "GET":
                {
                    if (uri.Contains("/info", StringComparison.OrdinalIgnoreCase) ||
                        uri.Contains("/server-info", StringComparison.OrdinalIgnoreCase))
                    {
                        var bplist = BuildInfoPlist();
                        var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nContent-Type: application/x-apple-binary-plist\r\nContent-Length: {bplist.Length}\r\n\r\n";
                        await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                        await stream.WriteAsync(bplist, ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nContent-Length: 0\r\n\r\n";
                        await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                    }
                    break;
                }

            case "POST":
                {
                    if (uri.Contains("/pair-setup", StringComparison.OrdinalIgnoreCase))
                    {
                        // Return 32-byte Ed25519 public key
                        var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nContent-Type: application/octet-stream\r\nContent-Length: {_edPublicKeyBytes.Length}\r\n\r\n";
                        await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                        await stream.WriteAsync(_edPublicKeyBytes, ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                    }
                    else if (uri.Contains("/pair-verify", StringComparison.OrdinalIgnoreCase))
                    {
                        if (body.Length >= 4 && body[0] == 1 && body.Length == 68)
                        {
                            // Phase 1: body is [01 00 00 00] [32 bytes client X25519 pk] [32 bytes client Ed25519 pk]
                            var clientX25519Pk = AirPlayCryptoUtils.CopyOfRange(body, 4, 36);

                            // Generate our X25519 keypair
                            var x25519Gen = new X25519KeyPairGenerator();
                            x25519Gen.Init(new X25519KeyGenerationParameters(new SecureRandom()));
                            var x25519Pair = x25519Gen.GenerateKeyPair();
                            var ourX25519Priv = (X25519PrivateKeyParameters)x25519Pair.Private;
                            var ourX25519Pub = (X25519PublicKeyParameters)x25519Pair.Public;
                            var ourX25519Pk = ourX25519Pub.GetEncoded();

                            // Compute ECDH shared secret
                            var clientPubParam = new X25519PublicKeyParameters(clientX25519Pk, 0);
                            var agreement = new X25519Agreement();
                            agreement.Init(ourX25519Priv);
                            var sharedSecret = new byte[agreement.AgreementSize];
                            agreement.CalculateAgreement(clientPubParam, sharedSecret, 0);
                            _ecdhSharedSecret = sharedSecret;

                            // Sign msg: ourX25519Pk (32) + clientX25519Pk (32)
                            var msgToSign = new byte[64];
                            Buffer.BlockCopy(ourX25519Pk, 0, msgToSign, 0, 32);
                            Buffer.BlockCopy(clientX25519Pk, 0, msgToSign, 32, 32);

                            var signer = new Ed25519Signer();
                            signer.Init(true, _edPrivateKey);
                            signer.BlockUpdate(msgToSign, 0, 64);
                            var signature = signer.GenerateSignature();

                            // Encrypt signature with AES-128-CTR using keys derived from shared secret
                            var aesKey = AirPlayCryptoUtils.Hash(Encoding.UTF8.GetBytes(AirPlayCryptoUtils.PairVerifyAesKey), sharedSecret);
                            var aesIv = AirPlayCryptoUtils.Hash(Encoding.UTF8.GetBytes(AirPlayCryptoUtils.PairVerifyAesIv), sharedSecret);

                            var cipher = CipherUtilities.GetCipher("AES/CTR/NoPadding");
                            cipher.Init(true, new ParametersWithIV(new KeyParameter(aesKey, 0, 16), aesIv, 0, 16));
                            var encSignature = cipher.DoFinal(signature);

                            var respBody = new byte[ourX25519Pk.Length + encSignature.Length];
                            Buffer.BlockCopy(ourX25519Pk, 0, respBody, 0, ourX25519Pk.Length);
                            Buffer.BlockCopy(encSignature, 0, respBody, ourX25519Pk.Length, encSignature.Length);

                            var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nContent-Type: application/octet-stream\r\nContent-Length: {respBody.Length}\r\n\r\n";
                            await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                            await stream.WriteAsync(respBody, ct).ConfigureAwait(false);
                            await stream.FlushAsync(ct).ConfigureAwait(false);
                        }
                        else
                        {
                            // Phase 2: verify complete
                            var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nContent-Type: application/octet-stream\r\nContent-Length: 0\r\n\r\n";
                            await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                        }
                    }
                    else if (uri.Contains("/fp-setup", StringComparison.OrdinalIgnoreCase))
                    {
                        if (body.Length == 16)
                        {
                            // Phase 1: 142-byte response
                            int mode = body.Length > 14 ? body[14] : 0;
                            if (mode < 0 || mode > 3) mode = 0;
                            var reply = FairPlayConstants.ReplyMessages[mode];

                            var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nContent-Type: application/octet-stream\r\nContent-Length: {reply.Length}\r\n\r\n";
                            await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                            await stream.WriteAsync(reply, ct).ConfigureAwait(false);
                            await stream.FlushAsync(ct).ConfigureAwait(false);
                        }
                        else if (body.Length == 164)
                        {
                            // Phase 2: Store key message for later decrypt, respond with 32 bytes
                            _fairplayKeyMsg = (byte[])body.Clone();

                            var respBody = new byte[32];
                            Buffer.BlockCopy(FairPlayConstants.FpHeader, 0, respBody, 0, 12);
                            Buffer.BlockCopy(body, 144, respBody, 12, 20);

                            var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nContent-Type: application/octet-stream\r\nContent-Length: {respBody.Length}\r\n\r\n";
                            await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                            await stream.WriteAsync(respBody, ct).ConfigureAwait(false);
                            await stream.FlushAsync(ct).ConfigureAwait(false);
                        }
                        else
                        {
                            var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nContent-Length: 0\r\n\r\n";
                            await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nContent-Length: 0\r\n\r\n";
                        await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                    }
                    break;
                }

            case "SETUP":
                {
                    var reader = new BPlistReader();
                    if (body.Length > 0 && reader.Parse(body))
                    {
                        var w = new BPlistWriter();
                        var root = new List<KeyValuePair<int, int>>();

                        // Phase 1: Encryption setup (ekey + eiv present)
                        if (reader.TryGetData("ekey", out var ekey) && reader.TryGetData("eiv", out var eiv))
                        {
                            _aesKey = ekey;
                            _aesIv = eiv;

                            if (_fairplayKeyMsg != null && ekey.Length == 72)
                            {
                                var omgHax = new OmgHax();
                                var decryptedKey = new byte[16];
                                omgHax.DecryptAesKey(_fairplayKeyMsg, ekey, decryptedKey);

                                if (_ecdhSharedSecret != null && _ecdhSharedSecret.Length == 32)
                                {
                                    var sha = AirPlayCryptoUtils.Hash(decryptedKey, _ecdhSharedSecret);
                                    _decryptedAesKey = AirPlayCryptoUtils.CopyOfRange(sha, 0, 16);
                                }
                                else
                                {
                                    _decryptedAesKey = decryptedKey;
                                }
                                _logger?.LogInformation("AirPlay FairPlay AES key decrypted successfully");
                            }

                            root.Add(new(w.AddString("eventPort"), w.AddUint((ulong)_eventPort)));
                            root.Add(new(w.AddString("timingPort"), w.AddUint((ulong)_timingPort)));
                        }

                        // Phase 2: Stream setup (streams array present)
                        if (reader.HasKey("streams"))
                        {
                            if (reader.TryGetStreamConnectionId(out var connId))
                            {
                                _streamConnectionId = connId;
                                InitializeMirrorCipher();
                            }

                            var streamDict = w.AddDict([
                                new(w.AddString("dataPort"), w.AddUint(MirrorPort)),
                                new(w.AddString("type"), w.AddUint(110))
                            ]);
                            var streamsArr = w.AddArray([streamDict]);
                            root.Add(new(w.AddString("streams"), streamsArr));
                        }

                        var bplist = w.Build(w.AddDict(root));
                        var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nContent-Type: application/x-apple-binary-plist\r\nContent-Length: {bplist.Length}\r\n\r\n";
                        await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                        await stream.WriteAsync(bplist, ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nTransport: RTP/AVP/TCP;unicast;interleaved=0-1;mode=record;server_port={MirrorPort}\r\nSession: 1\r\n\r\n";
                        await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                    }
                    break;
                }

            case "RECORD":
                {
                    var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nAudio-Latency: 11025\r\nAudio-Jack-Status: connected; type=digital\r\n\r\n";
                    await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                    _mirrorService.OnConnected();
                    break;
                }

            case "GET_PARAMETER":
                {
                    string volResp = "";
                    string ctHeader = "";
                    if (body.Length > 0 && Encoding.UTF8.GetString(body).Contains("volume", StringComparison.OrdinalIgnoreCase))
                    {
                        volResp = "volume: -30.000000\r\n";
                        ctHeader = "Content-Type: text/parameters\r\n";
                    }
                    var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\n{ctHeader}Content-Length: {volResp.Length}\r\n\r\n{volResp}";
                    await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                    break;
                }

            case "SET_PARAMETER":
                {
                    var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nContent-Length: 0\r\n\r\n";
                    await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                    break;
                }

            case "TEARDOWN":
                {
                    var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\n\r\n";
                    await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                    _mirrorService.OnDisconnected();
                    break;
                }

            default:
                {
                    var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nContent-Length: 0\r\n\r\n";
                    await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                    break;
                }
        }
    }

    private void InitializeMirrorCipher()
    {
        try
        {
            byte[]? finalAesKey = _decryptedAesKey;
            if (finalAesKey == null && _fairplayKeyMsg != null && _aesKey != null && _aesKey.Length == 72)
            {
                var omgHax = new OmgHax();
                var decryptedKey = new byte[16];
                omgHax.DecryptAesKey(_fairplayKeyMsg, _aesKey, decryptedKey);

                if (_ecdhSharedSecret != null && _ecdhSharedSecret.Length == 32)
                {
                    var sha = AirPlayCryptoUtils.Hash(decryptedKey, _ecdhSharedSecret);
                    finalAesKey = AirPlayCryptoUtils.CopyOfRange(sha, 0, 16);
                }
                else
                {
                    finalAesKey = decryptedKey;
                }
                _decryptedAesKey = finalAesKey;
            }

            if (finalAesKey != null)
            {
                // Derive AES-CTR key and IV from streamConnectionId
                var keyStr = Encoding.UTF8.GetBytes($"AirPlayStreamKey{_streamConnectionId}");
                var ivStr = Encoding.UTF8.GetBytes($"AirPlayStreamIV{_streamConnectionId}");

                var keyHash = AirPlayCryptoUtils.Hash(keyStr, finalAesKey);
                var ivHash = AirPlayCryptoUtils.Hash(ivStr, finalAesKey);

                var decryptKey = AirPlayCryptoUtils.CopyOfRange(keyHash, 0, 16);
                var decryptIv = AirPlayCryptoUtils.CopyOfRange(ivHash, 0, 16);

                var cipher = CipherUtilities.GetCipher("AES/CTR/NoPadding");
                cipher.Init(false, new ParametersWithIV(new KeyParameter(decryptKey), decryptIv));

                _mirrorCipher = cipher;

                _logger?.LogInformation("AirPlay AES-CTR cipher initialized successfully for connection ID {Id}", _streamConnectionId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to initialize AirPlay AES-CTR cipher");
        }
    }

    private byte[] BuildInfoPlist()
    {
        var w = new BPlistWriter();
        var mac = GetMacAddressString();

        var root = new List<KeyValuePair<int, int>>
        {
            new(w.AddString("deviceID"), w.AddString(mac)),
            new(w.AddString("macAddress"), w.AddString(mac)),
            new(w.AddString("features"), w.AddUint(0x5A7FFEE6)),
            new(w.AddString("model"), w.AddString("AppleTV3,2")),
            new(w.AddString("name"), w.AddString(ServerName)),
            new(w.AddString("protovers"), w.AddString("1.1")),
            new(w.AddString("sourceVersion"), w.AddString("220.68")),
            new(w.AddString("pk"), w.AddData(_edPublicKeyBytes)),
            new(w.AddString("pi"), w.AddString("2e388006-13ba-4041-9a67-25dd4a43d536")),
            new(w.AddString("vv"), w.AddUint(2)),
            new(w.AddString("statusFlags"), w.AddUint(68)),
            new(w.AddString("keepAliveLowPower"), w.AddUint(1)),
            new(w.AddString("keepAliveSendStatsAsBody"), w.AddBool(true)),
            new(w.AddString("initialVolume"), w.AddReal(-30.0))
        };

        // Audio formats (100 and 101)
        var fmt0 = w.AddDict([
            new(w.AddString("type"), w.AddUint(100)),
            new(w.AddString("audioInputFormats"), w.AddUint(0x03FFFFFC)),
            new(w.AddString("audioOutputFormats"), w.AddUint(0x03FFFFFC))
        ]);
        var fmt1 = w.AddDict([
            new(w.AddString("type"), w.AddUint(101)),
            new(w.AddString("audioInputFormats"), w.AddUint(0x03FFFFFC)),
            new(w.AddString("audioOutputFormats"), w.AddUint(0x03FFFFFC))
        ]);
        root.Add(new(w.AddString("audioFormats"), w.AddArray([fmt0, fmt1])));

        // Audio latencies
        var lat0 = w.AddDict([
            new(w.AddString("type"), w.AddUint(100)),
            new(w.AddString("audioType"), w.AddString("default")),
            new(w.AddString("inputLatencyMicros"), w.AddUint(0)),
            new(w.AddString("outputLatencyMicros"), w.AddBool(false))
        ]);
        var lat1 = w.AddDict([
            new(w.AddString("type"), w.AddUint(101)),
            new(w.AddString("audioType"), w.AddString("default")),
            new(w.AddString("inputLatencyMicros"), w.AddUint(0)),
            new(w.AddString("outputLatencyMicros"), w.AddBool(false))
        ]);
        root.Add(new(w.AddString("audioLatencies"), w.AddArray([lat0, lat1])));

        // Displays
        var disp = w.AddDict([
            new(w.AddString("uuid"), w.AddString("e0ff8a27-6738-3d56-8a16-cc53aacee925")),
            new(w.AddString("width"), w.AddUint(1920)),
            new(w.AddString("height"), w.AddUint(1080)),
            new(w.AddString("widthPixels"), w.AddUint(1920)),
            new(w.AddString("heightPixels"), w.AddUint(1080)),
            new(w.AddString("widthPhysical"), w.AddUint(0)),
            new(w.AddString("heightPhysical"), w.AddUint(0)),
            new(w.AddString("rotation"), w.AddBool(false)),
            new(w.AddString("refreshRate"), w.AddReal(1.0 / 60.0)),
            new(w.AddString("maxFPS"), w.AddUint(30)),
            new(w.AddString("overscanned"), w.AddBool(false)),
            new(w.AddString("features"), w.AddUint(14))
        ]);
        root.Add(new(w.AddString("displays"), w.AddArray([disp])));

        return w.Build(w.AddDict(root));
    }

    private async Task AcceptMirrorClientsAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = HandleMirrorClientAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Mirror stream accept error");
            }
        }
    }

    private async Task HandleMirrorClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            _logger?.LogInformation("AirPlay mirror stream connected on port {Port}", MirrorPort);

            var ffmpeg = CaptureRecordingEngine.FindFfmpeg();
            if (string.IsNullOrWhiteSpace(ffmpeg))
            {
                _logger?.LogWarning("FFmpeg not found; cannot decode AirPlay video stream");
                return;
            }

            var ffmpegPsi = new ProcessStartInfo(ffmpeg, [
                "-hide_banner",
                "-loglevel", "error",
                "-f", "h264",
                "-i", "-",
                "-f", "image2pipe",
                "-vcodec", "mjpeg",
                "-q:v", "3",
                "-"
            ])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var ffmpegProc = Process.Start(ffmpegPsi);
            if (ffmpegProc == null) return;

            using var ffmpegCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Read stderr asynchronously to monitor FFmpeg decoder state
            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = ffmpegProc.StandardError;
                    while (!ffmpegCts.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;
                        _logger?.LogDebug("[FFmpeg AirPlay] {Line}", line);
                    }
                }
                catch { }
            }, ffmpegCts.Token);

            // Read MJPEG frames from FFmpeg stdout and dispatch to MobileMirrorService
            var frameReaderTask = Task.Run(async () =>
            {
                var frameBuffer = new byte[256 * 1024];
                using var ms = new MemoryStream();
                var ffmpegOut = ffmpegProc.StandardOutput.BaseStream;
                int bytesRead;
                byte prev = 0;

                try
                {
                    while (!ffmpegCts.IsCancellationRequested && (bytesRead = await ffmpegOut.ReadAsync(frameBuffer, ffmpegCts.Token).ConfigureAwait(false)) > 0)
                    {
                        for (int i = 0; i < bytesRead; i++)
                        {
                            byte b = frameBuffer[i];
                            ms.WriteByte(b);

                            if (prev == 0xFF && b == 0xD9)
                            {
                                var jpeg = ms.ToArray();
                                ms.SetLength(0);
                                _mirrorService.OnFrame(jpeg);
                            }
                            prev = b;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "AirPlay frame reader loop terminated");
                }
            }, ffmpegCts.Token);

            var header = new byte[128];
            byte[] nalStartCode = [0x00, 0x00, 0x00, 0x01];
            bool prependSpsPps = false;

            try
            {
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    int hdrRead = 0;
                    while (hdrRead < 128)
                    {
                        int r = await stream.ReadAsync(header.AsMemory(hdrRead, 128 - hdrRead), ct).ConfigureAwait(false);
                        if (r <= 0) goto stream_exit;
                        hdrRead += r;
                    }

                    // Check for HTTP probe
                    if ((header[0] == 'P' && header[1] == 'O' && header[2] == 'S' && header[3] == 'T') ||
                        (header[0] == 'G' && header[1] == 'E' && header[2] == 'T'))
                    {
                        continue;
                    }

                    // Payload size is Little-Endian
                    uint payloadSize = (uint)header[0] |
                                       ((uint)header[1] << 8) |
                                       ((uint)header[2] << 16) |
                                       ((uint)header[3] << 24);

                    byte packetType = header[4];

                    if (payloadSize > 10 * 1024 * 1024)
                    {
                        _logger?.LogWarning("Invalid AirPlay payload size: {Size}", payloadSize);
                        break;
                    }

                    byte[] payload = [];
                    if (payloadSize > 0)
                    {
                        payload = new byte[payloadSize];
                        int payRead = 0;
                        while (payRead < (int)payloadSize)
                        {
                            int r = await stream.ReadAsync(payload.AsMemory(payRead, (int)payloadSize - payRead), ct).ConfigureAwait(false);
                            if (r <= 0) goto stream_exit;
                            payRead += r;
                        }
                    }

                    if (packetType == 0x01)
                    {
                        // Unencrypted SPS + PPS NAL units
                        if (payload.Length >= 11)
                        {
                            int spsSize = (payload[6] << 8) | payload[7];
                            int ppsOffset = 8 + spsSize + 1;
                            if (ppsOffset + 2 <= payload.Length)
                            {
                                int ppsSize = (payload[ppsOffset] << 8) | payload[ppsOffset + 1];
                                if (ppsOffset + 2 + ppsSize <= payload.Length)
                                {
                                    using var spsPpsMs = new MemoryStream();
                                    spsPpsMs.Write(nalStartCode);
                                    spsPpsMs.Write(payload, 8, spsSize);
                                    spsPpsMs.Write(nalStartCode);
                                    spsPpsMs.Write(payload, ppsOffset + 2, ppsSize);
                                    _cachedSpsPps = spsPpsMs.ToArray();
                                    prependSpsPps = true;
                                    _logger?.LogInformation("AirPlay SPS ({SpsSize}b) and PPS ({PpsSize}b) received", spsSize, ppsSize);

                                    try
                                    {
                                        await ffmpegProc.StandardInput.BaseStream.WriteAsync(_cachedSpsPps, ct).ConfigureAwait(false);
                                        await ffmpegProc.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    else if (packetType == 0x00 && payload.Length > 0)
                    {
                        // Encrypted video frame (VCL NAL)
                        byte[] decrypted = DecryptVideoPayload(payload);

                        using var frameMs = new MemoryStream();

                        if (prependSpsPps && _cachedSpsPps != null)
                        {
                            frameMs.Write(_cachedSpsPps);
                            prependSpsPps = false;
                        }

                        int pos = 0;
                        int total = decrypted.Length;
                        while (pos + 4 <= total)
                        {
                            int naluLen = (decrypted[pos] << 24) |
                                          (decrypted[pos + 1] << 16) |
                                          (decrypted[pos + 2] << 8) |
                                          decrypted[pos + 3];

                            if (naluLen <= 0 || pos + 4 + naluLen > total) break;
                            if ((decrypted[pos + 4] & 0x80) != 0) break; // forbidden_zero_bit

                            frameMs.Write(nalStartCode);
                            frameMs.Write(decrypted, pos + 4, naluLen);
                            pos += 4 + naluLen;
                        }

                        var annexB = frameMs.ToArray();
                        if (annexB.Length > 0)
                        {
                            await ffmpegProc.StandardInput.BaseStream.WriteAsync(annexB, ct).ConfigureAwait(false);
                            await ffmpegProc.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Mirror stream reading closed");
            }

        stream_exit:
            ffmpegCts.Cancel();
            try { ffmpegProc.Kill(); } catch { }
            _mirrorService.OnDisconnected();
        }
    }

    private byte[] DecryptVideoPayload(byte[] videoData)
    {
        if (_mirrorCipher == null)
        {
            InitializeMirrorCipher();
        }

        if (_mirrorCipher == null || videoData.Length == 0)
            return videoData;

        try
        {
            var output = new byte[videoData.Length];
            _mirrorCipher.ProcessBytes(videoData, 0, videoData.Length, output, 0);
            return output;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Error decrypting video payload block");
            return videoData;
        }
    }

    private static async Task WriteStringAsync(NetworkStream stream, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _cts = null;

            _rtspListener?.Stop();
            _rtspListener = null;

            _mirrorListener?.Stop();
            _mirrorListener = null;

            _eventListener?.Stop();
            _eventListener = null;

            _timingClient?.Close();
            _timingClient?.Dispose();
            _timingClient = null;

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
