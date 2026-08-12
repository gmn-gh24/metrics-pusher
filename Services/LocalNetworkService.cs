using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MetricsPusher.Services
{
    /// <summary>
    /// The one piece of network inspection this app needs: which adapter the PC currently
    /// pushes through, so <see cref="GpuDisplayPushService"/> can derive the display
    /// address from its IPv4 address and <see cref="NetworkThroughputService"/> can watch
    /// the same adapter's counters. Both questions are answered by one selection walk, so
    /// the two callers can never disagree about which adapter is "the" adapter.
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
        /// Selects the push's adapter: the first active interface that has both an IPv4
        /// gateway and an IPv4 unicast address, in OS enumeration order.
        /// <para>
        /// That ordering is part of the wire contract (push_metrics.md section 1.1),
        /// deployment hazard included: a VPN, Hyper-V, or WSL adapter that has a gateway
        /// can win and send metrics to the wrong network's display octet. The network
        /// metrics inherit the hazard deliberately - they describe the adapter the
        /// datagram is leaving by, whichever one that is.
        /// </para>
        /// </summary>
        /// <returns>The interface's IP properties and its first IPv4 address, or null.</returns>
        private static (IPInterfaceProperties Properties, IPAddress Address)? SelectPrimaryInterface()
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
                        return (props, ipAddress.Address);
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the local IPv4 address of the adapter <see cref="SelectPrimaryInterface"/>
        /// picks. Feeds display-address derivation.
        /// </summary>
        /// <returns>The address, or null when no such interface exists or enumeration failed.</returns>
        internal static IPAddress? GetLocalIPv4Address()
        {
            try
            {
                return SelectPrimaryInterface()?.Address;
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"Failed to get local IP: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets the IPv4 interface index of the same adapter
        /// <see cref="GetLocalIPv4Address"/> reports on - by construction, since both go
        /// through <see cref="SelectPrimaryInterface"/>. Feeds
        /// <see cref="NetworkThroughputService"/>'s GetIfEntry2 lookup, which is keyed by
        /// index. Called once at that service's initialization, never on the 1 Hz path -
        /// this method enumerates and allocates.
        /// </summary>
        /// <returns>The interface index, or null when no adapter qualifies or enumeration failed.</returns>
        internal static int? GetPrimaryInterfaceIndex()
        {
            try
            {
                return SelectPrimaryInterface() is { } selected
                    ? selected.Properties.GetIPv4Properties().Index
                    : null;
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"Failed to get primary interface index: {ex.Message}");
                return null;
            }
        }
    }
}
