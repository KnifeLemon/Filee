using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Filee.Engines.Translation;

/// <summary>
/// Where Filee reaches the local Ollama server. Ollama reads its own <c>OLLAMA_HOST</c> variable to listen on another
/// port or address, so Filee reads the same variable. It follows it only while it names this computer: the
/// translator sends the whole document text to Ollama, and a remote host would carry it off the machine.
/// </summary>
public static class OllamaEndpoint
{
    public const string Variable = "OLLAMA_HOST";
    public const int DefaultPort = 11434;

    public static readonly Uri DefaultBase = new($"http://127.0.0.1:{DefaultPort}");

    private static readonly Lazy<(Uri Base, string? Ignored)> Resolved = new(() => Resolve(Environment.GetEnvironmentVariable(Variable)));

    /// <summary>The server's base address, e.g. <c>http://127.0.0.1:11434</c>. Read once per run, like Ollama does.</summary>
    public static Uri Base => Resolved.Value.Base;

    /// <summary>The base address as shown to the user, without a trailing slash.</summary>
    public static string Address => Base.GetLeftPart(UriPartial.Authority);

    /// <summary>The <c>OLLAMA_HOST</c> value that was set but not followed (a remote host or not readable), or null.</summary>
    public static string? IgnoredValue => Resolved.Value.Ignored;

    /// <summary>The base address for an <c>OLLAMA_HOST</c> value, and the value itself when it was refused.</summary>
    public static (Uri Base, string? Ignored) Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (DefaultBase, null);
        return Parse(value) is { } parsed ? (parsed, null) : (DefaultBase, value.Trim());
    }

    /// <summary>
    /// Reads an <c>OLLAMA_HOST</c> value the way Ollama does: <c>host</c>, <c>host:port</c>, <c>:port</c> or
    /// <c>http(s)://host:port</c>, with port 11434 when neither a port nor a scheme is given. Returns null for a
    /// host that isn't this computer or a value that can't be read. An empty host, <c>0.0.0.0</c> or <c>::</c>
    /// means Ollama listens on every interface, which includes loopback, so Filee connects through loopback.
    /// </summary>
    public static Uri? Parse(string? value)
    {
        var text = value?.Trim().Trim('"', '\'').Trim();
        if (string.IsNullOrEmpty(text))
            return DefaultBase;

        var scheme = "http";
        var defaultPort = DefaultPort;
        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            scheme = text[..schemeEnd].ToLowerInvariant();
            if (scheme is not ("http" or "https"))
                return null;
            defaultPort = scheme == "https" ? 443 : 80;
            text = text[(schemeEnd + 3)..];
        }

        // Ollama allows a path after the host (for a reverse proxy); the API paths Filee calls replace it.
        var slash = text.IndexOf('/');
        if (slash >= 0)
            text = text[..slash];

        if (!SplitHostPort(text, out var host, out var portText))
            return null;

        // Like Ollama, a port that isn't a number in range falls back to the default one.
        var port = int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535
            ? p
            : defaultPort;

        IPAddress address;
        if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            address = IPAddress.Loopback;
        else if (!IPAddress.TryParse(host, out address!))
            return null;
        else if (address.Equals(IPAddress.Any))
            address = IPAddress.Loopback;
        else if (address.Equals(IPAddress.IPv6Any))
            address = IPAddress.IPv6Loopback;
        else if (!IPAddress.IsLoopback(address))
            return null;

        var hostText = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        return new Uri($"{scheme}://{hostText}:{port}");
    }

    /// <summary>Splits <c>host:port</c>, <c>[v6]:port</c>, a bare host or a bare IPv6 address; the port may be empty.</summary>
    private static bool SplitHostPort(string text, out string host, out string port)
    {
        host = text;
        port = "";
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0)
                return false;
            host = text[1..close];
            var rest = text[(close + 1)..];
            if (rest.Length == 0)
                return true;
            if (rest[0] != ':')
                return false;
            port = rest[1..];
            return true;
        }

        var colon = text.IndexOf(':');
        if (colon < 0)
            return true;
        // More than one colon without brackets is an IPv6 address with no port.
        if (text.IndexOf(':', colon + 1) >= 0)
            return true;
        host = text[..colon];
        port = text[(colon + 1)..];
        return true;
    }
}
