using System.Net;
using System.Net.Sockets;

namespace NekoChat.Core.Roster;

/// <summary>
/// The only place a socket address becomes a classroom source (CM-10): IPv4-mapped IPv6 is turned into IPv4, every other IPv6 address, 0.0.0.0, multicast and
/// broadcast give null. The Storage layer takes the resulting canonical string and does no network work itself.
/// </summary>
public static class ClassroomSource
{
    public static string? Canonical(IPAddress? address)
    {
        if (address is null) return null;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return null;
        byte[] b = address.GetAddressBytes();
        return b[0] == 0 || b[0] >= 224 ? null : address.ToString();
    }
}
