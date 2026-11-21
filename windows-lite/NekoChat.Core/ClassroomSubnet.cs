namespace NekoChat.Core;

/// The classroom moved from subnet 24 to 21; seat/host numbers stay the same.
public static class ClassroomSubnet
{
    public static bool IsCurrentNetwork(IReadOnlyCollection<string> localAddresses) =>
        localAddresses.Any(ip => ip.StartsWith("192.168.21.", StringComparison.Ordinal) && LanInvitation.PrivateIPv4(ip));

    public static string? UpgradeAddress(string? ip) =>
        ip is not null && ip.StartsWith("192.168.24.", StringComparison.Ordinal) && LanInvitation.PrivateIPv4(ip)
            ? "192.168.21." + ip[11..] : ip;
}
