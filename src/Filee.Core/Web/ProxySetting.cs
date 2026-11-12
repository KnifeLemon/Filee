// The proxy Filee's own downloads go through (Settings → General → Network, AppSettings.Proxy): engines, update
// checks, OCR languages and links dragged from a browser. It is for users who reach GitHub only through a proxy but
// don't want every program to use it, as the system proxy would (#879). Empty: the system proxy or HTTPS_PROXY, as
// before. Addresses on this computer and the local network never go through it (Ollama, an intranet link).

using System.Net;

namespace Filee.Core.Web;

public static class ProxySetting
{
    /// <summary>
    /// What .NET can talk to a proxy with. curl's socks5h ("the proxy looks up names") is read as socks5, which in
    /// .NET already leaves the lookup to the proxy.
    /// </summary>
    private static readonly string[] Schemes = ["http", "https", "socks4", "socks4a", "socks5"];

    private static readonly Lock Gate = new();

    /// <summary>
    /// Reads a proxy address as typed in Settings: <c>http://127.0.0.1:7890</c>, <c>socks5://user:pass@host:1080</c>,
    /// or just <c>127.0.0.1:7890</c> (taken as http). True with a null <paramref name="proxy"/> for an empty value.
    /// </summary>
    public static bool TryParse(string? text, out Uri? proxy)
    {
        proxy = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        var value = text.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "http://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.HostNameType == UriHostNameType.Unknown)
            return false;
        if (uri.Scheme == "socks5h")
            uri = new UriBuilder(uri) { Scheme = "socks5" }.Uri;
        // A proxy is a host and a port; a path or query means something else was pasted (a download link).
        if (!Schemes.Contains(uri.Scheme) || uri.Port <= 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
            return false;
        proxy = uri;
        return true;
    }

    /// <summary>The address without its user name and password, which only <see cref="CredentialsOf"/> hands on.</summary>
    public static Uri WithoutCredentials(Uri proxy) =>
        proxy.UserInfo.Length == 0 ? proxy : new UriBuilder(proxy) { UserName = "", Password = "" }.Uri;

    /// <summary>The user name and password in the address; null without them.</summary>
    public static NetworkCredential? CredentialsOf(Uri proxy)
    {
        if (proxy.UserInfo.Length == 0)
            return null;
        var colon = proxy.UserInfo.IndexOf(':', StringComparison.Ordinal);
        return colon < 0
            ? new NetworkCredential(Uri.UnescapeDataString(proxy.UserInfo), "")
            : new NetworkCredential(Uri.UnescapeDataString(proxy.UserInfo[..colon]), Uri.UnescapeDataString(proxy.UserInfo[(colon + 1)..]));
    }

    /// <summary>The address for logs: scheme, host and port, never the password.</summary>
    public static string Describe(Uri proxy) => $"{proxy.Scheme}://{proxy.Authority}";

    /// <summary>
    /// Makes every <see cref="HttpClient"/> that uses the default proxy (all of Filee's downloads; the clients for
    /// Ollama turn proxies off) read <paramref name="setting"/> for each request, so a change in Settings applies at
    /// once. Called once the settings are loaded and before anything downloads; a second call replaces the setting.
    /// </summary>
    public static void Use(Func<string?> setting)
    {
        lock (Gate)
        {
            if (HttpClient.DefaultProxy is SettingsWebProxy proxy)
                proxy.Setting = setting;
            else
                HttpClient.DefaultProxy = new SettingsWebProxy(setting, HttpClient.DefaultProxy);
        }
    }
}

/// <summary>
/// The proxy in Settings when one is set, for every address but those on this computer and the local network;
/// otherwise the system's (Windows' proxy settings, HTTPS_PROXY and the other variables).
/// </summary>
public sealed class SettingsWebProxy : IWebProxy
{
    private readonly IWebProxy? _system;
    private Parsed _parsed = new(null, null, null);

    /// <param name="setting">Read for each request: the proxy address as typed in Settings.</param>
    /// <param name="system">What is used while the setting is empty or not an address.</param>
    public SettingsWebProxy(Func<string?> setting, IWebProxy? system)
    {
        Setting = setting;
        _system = system;
        Credentials = new CurrentCredentials(this);
    }

    internal Func<string?> Setting { get; set; }

    /// <summary>
    /// Asks for the current proxy's sign-in each time, since the handler may keep this object from its first request.
    /// </summary>
    public ICredentials? Credentials { get; set; }

    /// <summary>The proxy from Settings, without its password; null when the setting is empty or not an address.</summary>
    public Uri? Configured => Current().Address;

    public Uri? GetProxy(Uri destination)
    {
        var current = Current();
        if (current.Address is null)
            return _system?.GetProxy(destination);
        return IsLocal(destination) ? null : current.Address;
    }

    public bool IsBypassed(Uri host)
    {
        if (Current().Address is null)
            return _system?.IsBypassed(host) ?? true;
        return IsLocal(host);
    }

    /// <summary>
    /// This computer and the local network: loopback, private and link-local numbers, localhost, .local and other
    /// local names, and single-label names (an intranet server). A proxy elsewhere couldn't reach them.
    /// </summary>
    internal static bool IsLocal(Uri address)
    {
        if (address.IsLoopback)
            return true;
        var host = address.IdnHost.Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip))
            return LocalNetwork.IsLocal(ip);
        return LocalNetwork.IsLocalName(host) || !host.TrimEnd('.').Contains('.', StringComparison.Ordinal);
    }

    private Parsed Current()
    {
        var text = Setting();
        var parsed = _parsed;
        if (string.Equals(parsed.Text, text, StringComparison.Ordinal))
            return parsed;
        parsed = ProxySetting.TryParse(text, out var uri) && uri is not null
            ? new Parsed(text, ProxySetting.WithoutCredentials(uri), ProxySetting.CredentialsOf(uri))
            : new Parsed(text, null, null);
        _parsed = parsed;
        return parsed;
    }

    private sealed record Parsed(string? Text, Uri? Address, NetworkCredential? Credential);

    private sealed class CurrentCredentials(SettingsWebProxy owner) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            var current = owner.Current();
            return current.Address is null
                ? owner._system?.Credentials?.GetCredential(uri, authType)
                : current.Credential;
        }
    }
}
