// The proxy in Settings (#879): which addresses are taken, which requests go through it, and that its password stays
// out of the address the handler (and its error messages) get. No network: only what the proxy object answers.

using System.Net;
using Filee.Core.Web;

namespace Filee.Core.Tests;

public sealed class ProxySettingTests
{
    [Theory]
    [InlineData("http://127.0.0.1:7890", "http://127.0.0.1:7890/")]
    [InlineData("  http://127.0.0.1:7890/  ", "http://127.0.0.1:7890/")]
    [InlineData("127.0.0.1:7890", "http://127.0.0.1:7890/")]
    [InlineData("https://proxy.example:8443", "https://proxy.example:8443/")]
    [InlineData("http://proxy.example", "http://proxy.example/")]
    [InlineData("proxy.example", "http://proxy.example/")]
    [InlineData("socks5://127.0.0.1:1080", "socks5://127.0.0.1:1080/")]
    [InlineData("socks5h://127.0.0.1:1080", "socks5://127.0.0.1:1080/")]
    [InlineData("socks4a://[::1]:1080", "socks4a://[::1]:1080/")]
    [InlineData("http://user:pass@127.0.0.1:7890", "http://user:pass@127.0.0.1:7890/")]
    public void Takes_a_proxy_address(string text, string expected)
    {
        Assert.True(ProxySetting.TryParse(text, out var proxy));
        Assert.Equal(expected, proxy!.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_means_no_proxy_of_its_own(string? text)
    {
        Assert.True(ProxySetting.TryParse(text, out var proxy));
        Assert.Null(proxy);
    }

    [Theory]
    [InlineData("ftp://127.0.0.1:21")]
    [InlineData("socks5://127.0.0.1")] // no port, and socks has no default one
    [InlineData("http://127.0.0.1:7890/download/file.zip")]
    [InlineData("http://127.0.0.1:7890/?a=1")]
    [InlineData("http://127.0.0.1:99999")]
    [InlineData("http://")]
    [InlineData("not a proxy")]
    public void Refuses_what_isnt_a_proxy_address(string text)
    {
        Assert.False(ProxySetting.TryParse(text, out var proxy));
        Assert.Null(proxy);
    }

    [Fact]
    public void The_password_is_handed_on_apart_and_never_described()
    {
        Assert.True(ProxySetting.TryParse("http://us%40er:p%3Ass@proxy.example:7890", out var proxy));

        var credential = ProxySetting.CredentialsOf(proxy!);
        Assert.Equal("us@er", credential!.UserName);
        Assert.Equal("p:ss", credential.Password);
        Assert.Equal("http://proxy.example:7890/", ProxySetting.WithoutCredentials(proxy!).AbsoluteUri);
        Assert.Equal("http://proxy.example:7890", ProxySetting.Describe(proxy!));
        Assert.Null(ProxySetting.CredentialsOf(new Uri("http://proxy.example:7890")));
    }

    [Fact]
    public void A_set_proxy_takes_internet_addresses_and_the_password_stays_out_of_its_address()
    {
        var system = new FixedProxy(new Uri("http://system.example:8080"));
        var proxy = new SettingsWebProxy(() => "http://user:secret@127.0.0.1:7890", system);
        var github = new Uri("https://github.com/KnifeLemon/Filee/releases");

        Assert.False(proxy.IsBypassed(github));
        Assert.Equal(new Uri("http://127.0.0.1:7890/"), proxy.GetProxy(github));
        Assert.DoesNotContain("secret", proxy.GetProxy(github)!.AbsoluteUri, StringComparison.Ordinal);
        var credential = proxy.Credentials!.GetCredential(new Uri("http://127.0.0.1:7890/"), "Basic");
        Assert.Equal(("user", "secret"), (credential!.UserName, credential.Password));
        Assert.Equal(0, system.Asked);
    }

    [Theory]
    [InlineData("http://localhost:11434/api/tags")] // Ollama
    [InlineData("http://127.0.0.1:5000/")]
    [InlineData("http://[::1]:5000/")]
    [InlineData("http://192.168.1.20/share/photo.png")] // a NAS
    [InlineData("http://10.0.0.7/")]
    [InlineData("http://printer.local/")]
    [InlineData("http://nas/photo.png")] // an intranet name
    public void Addresses_on_this_computer_and_the_local_network_go_direct(string address)
    {
        var proxy = new SettingsWebProxy(() => "http://127.0.0.1:7890", new FixedProxy(new Uri("http://system.example:8080")));
        var uri = new Uri(address);

        Assert.True(proxy.IsBypassed(uri));
        Assert.Null(proxy.GetProxy(uri));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a proxy")]
    public void Without_a_usable_setting_the_system_proxy_decides(string? setting)
    {
        var system = new FixedProxy(new Uri("http://system.example:8080")) { Bypassed = false };
        system.Credentials = new NetworkCredential("system-user", "x");
        var proxy = new SettingsWebProxy(() => setting, system);
        var github = new Uri("https://github.com/");

        Assert.False(proxy.IsBypassed(github));
        Assert.Equal(new Uri("http://system.example:8080"), proxy.GetProxy(github));
        Assert.Equal("system-user", proxy.Credentials!.GetCredential(github, "Basic")!.UserName);
        Assert.Equal(2, system.Asked);
    }

    [Fact]
    public void Without_a_setting_or_a_system_proxy_everything_goes_direct()
    {
        var proxy = new SettingsWebProxy(() => null, null);

        Assert.True(proxy.IsBypassed(new Uri("https://github.com/")));
        Assert.Null(proxy.GetProxy(new Uri("https://github.com/")));
        Assert.Null(proxy.Credentials!.GetCredential(new Uri("https://github.com/"), "Basic"));
    }

    [Fact]
    public void A_change_in_Settings_applies_to_the_next_request()
    {
        string? setting = null;
        var proxy = new SettingsWebProxy(() => setting, null);
        var github = new Uri("https://github.com/");

        Assert.Null(proxy.GetProxy(github));
        setting = "socks5://127.0.0.1:1080";
        Assert.Equal(new Uri("socks5://127.0.0.1:1080/"), proxy.GetProxy(github));
        Assert.Equal(new Uri("socks5://127.0.0.1:1080/"), proxy.Configured);
        setting = "";
        Assert.Null(proxy.GetProxy(github));
        Assert.Null(proxy.Configured);
    }

    /// <summary>The system's proxy, counting how often it was asked.</summary>
    private sealed class FixedProxy(Uri address) : IWebProxy
    {
        public int Asked { get; private set; }

        public bool Bypassed { get; init; } = true;

        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination)
        {
            Asked++;
            return address;
        }

        public bool IsBypassed(Uri host)
        {
            Asked++;
            return Bypassed;
        }
    }
}

/// <summary>
/// Replaces HttpClient.DefaultProxy for a moment, so nothing else may run meanwhile: a handler created then would keep
/// the test's proxy.
/// </summary>
[CollectionDefinition("Default proxy", DisableParallelization = true)]
public sealed class DefaultProxyCollection;

[Collection("Default proxy")]
public sealed class ProxySettingUseTests
{
    [Fact]
    public void Use_wraps_the_default_proxy_once_and_keeps_the_latest_setting()
    {
        var original = HttpClient.DefaultProxy;
        try
        {
            ProxySetting.Use(() => "http://127.0.0.1:1");
            var installed = Assert.IsType<SettingsWebProxy>(HttpClient.DefaultProxy);
            ProxySetting.Use(() => "http://127.0.0.1:2");

            Assert.Same(installed, HttpClient.DefaultProxy);
            Assert.Equal(new Uri("http://127.0.0.1:2/"), installed.GetProxy(new Uri("https://github.com/")));
        }
        finally
        {
            HttpClient.DefaultProxy = original;
        }
    }
}
