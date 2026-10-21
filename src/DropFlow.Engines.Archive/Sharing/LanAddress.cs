using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DropFlow.Engines.Archive.Sharing;

/// <summary>A network adapter reduced to what choosing the address a phone should connect to needs. Plain data so the choice is testable.</summary>
public sealed record NetworkAdapter(
    string Name,
    string Description,
    NetworkInterfaceType Type,
    bool IsUp,
    IReadOnlyList<IPAddress> Ipv4Addresses,
    bool HasGateway);

/// <summary>Finds the local IPv4 address other devices on the same Wi-Fi/Ethernet network can reach.</summary>
public static class LanAddress
{
    private static readonly string[] VirtualMarkers =
        ["virtual", "vmware", "vbox", "hyper-v", "vethernet", "docker", "wsl", "loopback", "tap-", "tunnel", "bluetooth", "vpn"];

    /// <summary>The best LAN address on this machine, or null when it is not on any network.</summary>
    public static IPAddress? Find() => Pick(Enumerate());

    /// <summary>
    /// Picks the address a phone can reach: an adapter that is up, is Ethernet or Wi-Fi, is not a virtual switch or tunnel, has a
    /// default gateway, and has a real (not 169.254.x.x) IPv4 address. Without a gateway, falls back to a private-range address.
    /// </summary>
    public static IPAddress? Pick(IEnumerable<NetworkAdapter> adapters)
    {
        var usable = adapters
            .Where(a => a.IsUp
                        && a.Type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
                            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
                        && !IsVirtual(a))
            .Select(a => (Adapter: a, Address: a.Ipv4Addresses.FirstOrDefault(IsUsable)))
            .Where(x => x.Address is not null)
            .ToList();

        var withGateway = usable.FirstOrDefault(x => x.Adapter.HasGateway);
        if (withGateway.Address is not null) return withGateway.Address;

        return usable.FirstOrDefault(x => IsPrivate(x.Address!)).Address;
    }

    public static IReadOnlyList<NetworkAdapter> Enumerate()
    {
        var result = new List<NetworkAdapter>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var props = nic.GetIPProperties();
                var addresses = props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address)
                    .ToList();
                var gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                result.Add(new NetworkAdapter(nic.Name, nic.Description, nic.NetworkInterfaceType, nic.OperationalStatus == OperationalStatus.Up, addresses, gateway));
            }
            catch (NetworkInformationException)
            {
                // an adapter that vanished while enumerating
            }
        }
        return result;
    }

    private static bool IsVirtual(NetworkAdapter adapter) =>
        VirtualMarkers.Any(m => adapter.Name.Contains(m, StringComparison.OrdinalIgnoreCase) || adapter.Description.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static bool IsUsable(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)) return false;
        var b = address.GetAddressBytes();
        return !(b[0] == 169 && b[1] == 254); // APIPA: no DHCP answer, nobody can reach it
    }

    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
