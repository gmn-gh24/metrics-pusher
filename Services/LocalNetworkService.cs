using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MetricsPusher.Services
{
    /// <summary>
    /// The one piece of network inspection this app needs: which IPv4 address the PC
    /// currently holds, so <see cref="GpuDisplayPushService"/> can derive the display
    /// address from it.
    /// </summary>
    internal static class LocalNetworkService
    {
        /// <summary>
        /// Gets active network interfaces that are up and not loopback.
        /// </summary>
        private static IEnumerable<NetworkInterface> GetActiveNetworkInterfaces()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
        }

        /// <summary>
        /// Gets the local IPv4 address on the first active interface that has an IPv4
        /// gateway, in OS enumeration order.
        /// <para>
        /// That ordering is part of the wire contract (push_metrics.md section 1.1),
        /// deployment hazard included: a VPN, Hyper-V, or WSL adapter that has a gateway
        /// can win and send metrics to the wrong network's display octet.
        /// </para>
        /// </summary>
        /// <returns>The address, or null when no such interface exists or enumeration failed.</returns>
        internal static IPAddress? GetLocalIPv4Address()
        {
            try
            {
                foreach (var ni in GetActiveNetworkInterfaces())
                {
                    var props = ni.GetIPProperties();
                    var gw = props.GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);

                    if (gw != null)
                    {
                        var ipAddress = props.UnicastAddresses
                            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                        if (ipAddress != null)
                            return ipAddress.Address;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"Failed to get local IP: {ex.Message}");
            }
            return null;
        }
    }
}
