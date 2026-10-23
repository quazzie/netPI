using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetPI.ListenerKit;

/// <summary>
/// The addresses a plugin listener can bind on this PC, for its "Listen on" setting's choices: loopback, each LAN IPv4
/// address of an interface that is up, and every interface. Read when the plugin starts; another address can still be typed.
/// </summary>
internal static class BindAddresses
{
    public static IReadOnlyList<string> Choices()
    {
        var list = new List<string> { "127.0.0.1" };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)
                        && !list.Contains(a.Address.ToString()))
                        list.Add(a.Address.ToString());
            }
        }
        catch (NetworkInformationException) { /* loopback and every interface stay */ }
        list.Add("0.0.0.0");
        return list;
    }
}
