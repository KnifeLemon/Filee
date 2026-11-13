// Which addresses a dragged link may not lead to: this computer and the local network, also when written as IPv6.

using System.Net;
using Filee.Core.Web;

namespace Filee.Core.Tests;

public class LocalNetworkTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("0.0.0.0")] // reaches this computer on Linux and macOS
    [InlineData("0.1.2.3")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")] // carrier-grade NAT
    [InlineData("100.127.255.255")]
    [InlineData("169.254.169.254")] // link-local, where cloud machines keep their metadata
    [InlineData("224.0.0.251")] // multicast
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")] // reserved
    [InlineData("255.255.255.255")]
    public void Local_ipv4_addresses_are_refused(string address) =>
        Assert.True(LocalNetwork.IsLocal(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("9.255.255.255")] // just below 10/8
    [InlineData("11.0.0.0")]
    [InlineData("172.15.255.255")] // around 172.16/12
    [InlineData("172.32.0.0")]
    [InlineData("192.167.255.255")] // around 192.168/16
    [InlineData("192.169.0.0")]
    [InlineData("100.63.255.255")] // around 100.64/10
    [InlineData("100.128.0.0")]
    [InlineData("169.253.255.255")]
    [InlineData("128.0.0.1")]
    [InlineData("198.18.0.1")] // benchmarking, but proxies with fake-IP DNS hand these out for every public name
    [InlineData("223.255.255.255")]
    public void Public_ipv4_addresses_are_allowed(string address) =>
        Assert.False(LocalNetwork.IsLocal(IPAddress.Parse(address)));

    [Theory]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("febf:ffff::1")] // the end of fe80::/10
    [InlineData("fec0::1")] // site-local (deprecated)
    [InlineData("fc00::1")] // unique local
    [InlineData("fd12:3456:789a::1")]
    [InlineData("ff02::1")] // multicast
    [InlineData("ff05::2")]
    [InlineData("::ffff:127.0.0.1")] // IPv4-mapped
    [InlineData("::ffff:192.168.0.10")]
    [InlineData("::ffff:10.1.2.3")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:0.0.0.0")]
    [InlineData("::127.0.0.1")] // IPv4-compatible (deprecated)
    [InlineData("::ffff:0:192.168.1.1")] // IPv4-translated
    [InlineData("64:ff9b::10.0.0.1")] // NAT64
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("64:ff9b:1::8.8.8.8")] // NAT64 for local use
    [InlineData("2002:c0a8:0101::1")] // 6to4 of 192.168.1.1
    [InlineData("2002:7f00:1::")]
    public void Local_ipv6_addresses_are_refused(string address) =>
        Assert.True(LocalNetwork.IsLocal(IPAddress.Parse(address)));

    [Theory]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")] // NAT64 of 8.8.8.8
    [InlineData("2002:0808:0808::1")] // 6to4 of 8.8.8.8
    [InlineData("fbff:ffff::1")] // just below fc00::/7
    [InlineData("fe00::1")] // below fe80::/10
    [InlineData("2a00:1450:4001:82a::200e")]
    public void Public_ipv6_addresses_are_allowed(string address) =>
        Assert.False(LocalNetwork.IsLocal(IPAddress.Parse(address)));

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("localhost.")]
    [InlineData("app.localhost")]
    [InlineData("printer.local")]
    [InlineData("NAS.Local.")]
    [InlineData("router.home.arpa")]
    [InlineData("db.corp.internal")]
    public void Local_names_are_refused(string host) => Assert.True(LocalNetwork.IsLocalName(host));

    [Theory]
    [InlineData("example.com")]
    [InlineData("localhost.example.com")]
    [InlineData("mylocal")]
    [InlineData("local.example.org")]
    [InlineData("files.locality.net")]
    [InlineData("internal.example.com")]
    public void Public_names_are_allowed(string host) => Assert.False(LocalNetwork.IsLocalName(host));
}
