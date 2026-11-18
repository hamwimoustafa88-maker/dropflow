using System.Net;
using System.Net.Sockets;

namespace DropFlow.Core.Sources;

/// <summary>
/// Which network addresses a link download may connect to: only the public internet. A link dropped from a web page must never make
/// DropFlow talk to the user's own computer, router, company network or a cloud metadata service (169.254.169.254). The downloader checks
/// the address it actually connects to, after its own DNS lookup and on every redirect hop, so a name that resolves to a private address
/// (DNS rebinding) is refused too.
/// </summary>
public static class AddressPolicy
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0                                   // "this network"
                || b[0] == 10                                    // private
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)    // carrier-grade NAT
                || b[0] == 127                                   // loopback
                || (b[0] == 169 && b[1] == 254)                  // link-local, cloud metadata
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)     // private
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)       // IETF protocol assignments
                || (b[0] == 192 && b[1] == 0 && b[2] == 2)       // documentation
                || (b[0] == 192 && b[1] == 168)                  // private
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))   // benchmarking
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)    // documentation
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)     // documentation
                || b[0] >= 224);                                 // multicast, reserved, broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;                               // unique local fc00::/7
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false; // documentation 2001:db8::/32
            // IPv4 inside IPv6: NAT64 (64:ff9b::/96) and 6to4 (2002::/16) carry an IPv4 address that must be public too
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B) return IsPublic(new IPAddress(b[12..16]));
            if (b[0] == 0x20 && b[1] == 0x02) return IsPublic(new IPAddress(b[2..6]));
            return true;
        }

        return false;
    }
}
