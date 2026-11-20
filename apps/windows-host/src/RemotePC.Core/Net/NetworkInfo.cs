using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemotePC.Core.Net;

public static class NetworkInfo
{
    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>IPv4 addresses on interfaces that are up, not loopback/tunnel, with a gateway or private range.</summary>
    public static IReadOnlyList<IPAddress> LanAddresses()
    {
        var list = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(ua.Address) && !IPAddress.IsLoopback(ua.Address)
                    && !ua.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    list.Add(ua.Address);
            }
        }
        return list;
    }

    public static bool HasInternet() => NetworkInterface.GetIsNetworkAvailable();
}
