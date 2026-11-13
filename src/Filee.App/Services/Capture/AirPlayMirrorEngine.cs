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
    private ulong _streamConnectionId;

    // Mirror stream decryption
    private IBufferedCipher? _mirrorCipher;
    private readonly byte[] _og = new byte[16];
    private int _nextDecryptCount;

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
                _rtspListener.Start();
            }
            catch
            {
                boundPort = 7001;
                _rtspListener = new TcpListener(IPAddress.Any, boundPort);
                _rtspListener.Start();
            }

            // Start mirror TCP listener on port 7100
            try
            {
                _mirrorListener = new TcpListener(IPAddress.Any, MirrorPort);
                _mirrorListener.Start();
                _ = Task.Run(() => AcceptMirrorClientsAsync(_mirrorListener, cts.Token), cts.Token);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to start mirror stream listener on port {Port}", MirrorPort);
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

    private async Task HandleAirPlayClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            _mirrorService.OnConnected();
            var readBuffer = new byte[65536];

            try
            {
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    // Read RTSP request header (until "\r\n\r\n")
                    var headerBytes = new List<byte>();
                    int b;
                    while ((b = stream.ReadByte()) != -1)
                    {
                        headerBytes.Add((byte)b);
                        int count = headerBytes.Count;
                        if (count >= 4 &&
                            headerBytes[count - 4] == '\r' && headerBytes[count - 3] == '\n' &&
                            headerBytes[count - 2] == '\r' && headerBytes[count - 1] == '\n')
                        {
                            break;
                        }
                    }

                    if (headerBytes.Count == 0) break;

                    var headerText = Encoding.UTF8.GetString(headerBytes.ToArray());
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
            finally
            {
                _mirrorService.OnDisconnected();
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
                        // Case 1: Initial key/timing setup
                        if (reader.TryGetData("ekey", out var ekey) && reader.TryGetData("eiv", out var eiv))
                        {
                            _aesKey = ekey;
                            _aesIv = eiv;

                            var w = new BPlistWriter();
                            var root = new List<KeyValuePair<int, int>>
                        {
                            new(w.AddString("eventPort"), w.AddUint(EventPort)),
                            new(w.AddString("timingPort"), w.AddUint(TimingPort))
                        };
                            var bplist = w.Build(w.AddDict(root));

                            var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nContent-Type: application/x-apple-binary-plist\r\nContent-Length: {bplist.Length}\r\n\r\n";
                            await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                            await stream.WriteAsync(bplist, ct).ConfigureAwait(false);
                            await stream.FlushAsync(ct).ConfigureAwait(false);
                        }
                        // Case 2: Stream setup (streams array)
                        else if (reader.HasKey("streams"))
                        {
                            if (reader.TryGetStreamConnectionId(out var connId))
                            {
                                _streamConnectionId = connId;
                            }

                            // Decrypt FairPlay AES key for video stream
                            InitializeMirrorCipher();

                            var w = new BPlistWriter();
                            var streamDict = w.AddDict([
                                new(w.AddString("dataPort"), w.AddUint(MirrorPort)),
                            new(w.AddString("type"), w.AddUint(110))
                            ]);
                            var streamsArr = w.AddArray([streamDict]);
                            var rootDict = w.AddDict([
                                new(w.AddString("streams"), streamsArr)
                            ]);
                            var bplist = w.Build(rootDict);

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
                    var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\nSession: 1\r\n\r\n";
                    await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
                    break;
                }

            case "TEARDOWN":
                {
                    var resp = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: AirTunes/220.68\r\n\r\n";
                    await WriteStringAsync(stream, resp, ct).ConfigureAwait(false);
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
            if (_fairplayKeyMsg != null && _aesKey != null && _aesKey.Length == 72)
            {
                var omgHax = new OmgHax();
                var decryptedAesKey = new byte[16];
                omgHax.DecryptAesKey(_fairplayKeyMsg, _aesKey, decryptedAesKey);

                // Hash decrypted key with ECDH shared secret (SHA-512, first 16 bytes)
                byte[] finalAesKey = decryptedAesKey;
                if (_ecdhSharedSecret != null && _ecdhSharedSecret.Length == 32)
                {
                    var sha = AirPlayCryptoUtils.Hash(decryptedAesKey, _ecdhSharedSecret);
                    finalAesKey = AirPlayCryptoUtils.CopyOfRange(sha, 0, 16);
                }

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
                _nextDecryptCount = 0;
                Array.Clear(_og, 0, _og.Length);

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
            new(w.AddString("statusFlags"), w.AddUint(4)),
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

            var ffmpegPsi = new ProcessStartInfo(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "h264", "-i", "-", "-f", "image2pipe", "-vcodec", "mjpeg", "-q:v", "5", "-"])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var ffmpegProc = Process.Start(ffmpegPsi);
            if (ffmpegProc == null) return;

            // Background task to read MJPEG frames from FFmpeg stdout and dispatch to MobileMirrorService
            using var ffmpegCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var frameReaderTask = Task.Run(async () =>
            {
                var frameBuffer = new byte[256 * 1024];
                using var ms = new MemoryStream();
                var ffmpegOut = ffmpegProc.StandardOutput.BaseStream;
                int bytesRead;

                while (!ffmpegCts.IsCancellationRequested && (bytesRead = await ffmpegOut.ReadAsync(frameBuffer, ffmpegCts.Token)) > 0)
                {
                    for (int i = 0; i < bytesRead; i++)
                    {
                        ms.WriteByte(frameBuffer[i]);

                        // Check for JPEG End-Of-Image marker (0xFF, 0xD9)
                        if (ms.Length >= 2)
                        {
                            ms.Position = ms.Length - 2;
                            int b1 = ms.ReadByte();
                            int b2 = ms.ReadByte();
                            if (b1 == 0xFF && b2 == 0xD9)
                            {
                                var jpeg = ms.ToArray();
                                _mirrorService.OnFrame(jpeg);
                                ms.SetLength(0);
                            }
                        }
                    }
                }
            }, ffmpegCts.Token);

            var header = new byte[128];
            byte[] nalStartCode = [0x00, 0x00, 0x00, 0x01];

            try
            {
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    // Read 128-byte packet header
                    int hdrRead = 0;
                    while (hdrRead < 128)
                    {
                        int r = await stream.ReadAsync(header.AsMemory(hdrRead, 128 - hdrRead), ct).ConfigureAwait(false);
                        if (r <= 0) goto stream_exit;
                        hdrRead += r;
                    }

                    // Check if HTTP probe
                    if ((header[0] == 'P' && header[1] == 'O' && header[2] == 'S' && header[3] == 'T') ||
                        (header[0] == 'G' && header[1] == 'E' && header[2] == 'T'))
                    {
                        continue;
                    }

                    // Parse header: payload size is Little-Endian
                    uint payloadSize = (uint)header[0] |
                                       ((uint)header[1] << 8) |
                                       ((uint)header[2] << 16) |
                                       ((uint)header[3] << 24);

                    byte packetType = header[4];

                    if (payloadSize > 10 * 1024 * 1024) break;

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
                                }
                            }
                        }
                    }
                    else if (packetType == 0x00 && payload.Length > 0)
                    {
                        // Encrypted video frame (VCL NAL)
                        byte[] decrypted = DecryptVideoPayload(payload);

                        // Convert AVCC lengths to Annex B 00 00 00 01 start codes
                        using var frameMs = new MemoryStream();

                        if (_cachedSpsPps != null)
                        {
                            frameMs.Write(_cachedSpsPps);
                            _cachedSpsPps = null;
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
            return videoData;

        try
        {
            if (_nextDecryptCount > 0)
            {
                for (int i = 0; i < _nextDecryptCount; i++)
                {
                    videoData[i] = (byte)(videoData[i] ^ _og[(16 - _nextDecryptCount) + i]);
                }
            }

            int encryptLen = ((videoData.Length - _nextDecryptCount) / 16) * 16;
            if (encryptLen > 0)
            {
                _mirrorCipher.ProcessBytes(videoData, _nextDecryptCount, encryptLen, videoData, _nextDecryptCount);
            }

            int restLen = (videoData.Length - _nextDecryptCount) % 16;
            int restStart = videoData.Length - restLen;
            _nextDecryptCount = 0;

            if (restLen > 0)
            {
                Array.Clear(_og, 0, _og.Length);
                Array.Copy(videoData, restStart, _og, 0, restLen);
                _mirrorCipher.ProcessBytes(_og, 0, 16, _og, 0);
                Array.Copy(_og, 0, videoData, restStart, restLen);
                _nextDecryptCount = 16 - restLen;
            }

            return videoData;
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
