// Addresses on the user's own computer and network. A web page can put any address in a link it lets you drag, and
// Filee downloads it without the browser, whose Private Network Access rules would stop a public page from reaching a
// router, a printer or a service on localhost. So a dragged link may only lead to the public internet.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Filee.Core.Web;

public static class LocalNetwork
{
    /// <summary>Names kept for local use: localhost (RFC 6761), mDNS .local (RFC 6762), home.arpa (RFC 8375), .internal.</summary>
    private static readonly string[] LocalSuffixes = [".localhost", ".local", ".home.arpa", ".internal"];

    /// <summary>
    /// True for a name that only means something on this computer or network. A name can still resolve to a local
    /// address; <see cref="IsLocal"/> checks what it resolves to.
    /// </summary>
    public static bool IsLocalName(string host)
    {
        host = host.TrimEnd('.');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || LocalSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True for an address that isn't on the public internet: this computer (loopback, unspecified), the local network
    /// (private, carrier-grade NAT, link-local, unique local), multicast and reserved ranges. IPv6 addresses that
    /// carry an IPv4 address (mapped, compatible, NAT64, 6to4) are judged by that address.
    /// </summary>
    public static bool IsLocal(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return IsLocalV4(BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes()));
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return true; // not an internet address at all

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        var high = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        var low = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        var embedded = (uint)low;

        if (high == 0 && low >> 32 is 0 or 0xFFFF)
            return IsLocalV4(embedded); // ::a.b.c.d and ::ffff:a.b.c.d; :: and ::1 fall in 0.0.0.0/8
        if (high == 0 && low >> 32 == 0xFFFF_0000)
            return IsLocalV4(embedded); // ::ffff:0:a.b.c.d (SIIT)
        if (high == 0x0064_FF9B_0000_0000 && low >> 32 == 0)
            return IsLocalV4(embedded); // 64:ff9b::/96, the NAT64 prefix
        if (high >> 16 == 0x0064_FF9B_0001)
            return true; // 64:ff9b:1::/48, NAT64 for local use
        if (high >> 48 == 0x2002)
            return IsLocalV4((uint)(high >> 16)); // 6to4

        var first = bytes[0];
        return first is 0xFC or 0xFD // fc00::/7 unique local
               || first == 0xFF // multicast
               || first == 0xFE && (bytes[1] & 0xC0) is 0x80 or 0xC0; // fe80::/10 link-local, fec0::/10 site-local
    }

    private static bool IsLocalV4(uint address) =>
        (address >> 24) is 0 or 10 or 127 // "this network" (0.0.0.0 reaches this computer), private, loopback
        || (address & 0xFFC0_0000) == 0x6440_0000 // 100.64.0.0/10 carrier-grade NAT
        || (address & 0xFFFF_0000) == 0xA9FE_0000 // 169.254.0.0/16 link-local
        || (address & 0xFFF0_0000) == 0xAC10_0000 // 172.16.0.0/12 private
        || (address & 0xFFFF_0000) == 0xC0A8_0000 // 192.168.0.0/16 private
        || address >= 0xE000_0000; // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved and the broadcast address
}
