using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

#pragma warning disable SA1402, SA1649 // File may only contain a single type - GpuDisplayPayload is tightly coupled to GpuDisplayPushService
#pragma warning disable SA1011 // Closing square bracket should be followed by a space - StyleCop 1.1.118 predates nullable reference types and misreads "byte[]?"

namespace MetricsPusher.Services
{
    /// <summary>
    /// Wire payload for the UDP metrics datagram. The authoritative protocol
    /// contract lives in push_metrics.md at the repo root; property declaration
    /// order here is the wire key order.
    /// </summary>
    internal sealed class GpuDisplayPayload
    {
        [JsonPropertyName("v")]
        public int Version { get; set; }

        [JsonPropertyName("gpu")]
        public string? Gpu { get; set; }

        [JsonPropertyName("host")]
        public string? Host { get; set; }

        [JsonPropertyName("temp")]
        public float? Temp { get; set; }

        [JsonPropertyName("load")]
        public int? Load { get; set; }

        [JsonPropertyName("vramUsed")]
        public long? VramUsed { get; set; }

        [JsonPropertyName("vramTotal")]
        public long? VramTotal { get; set; }

        [JsonPropertyName("fan")]
        public int? Fan { get; set; }

        /// <summary>GPU power draw as percent of TDP (transient boost can exceed 100).</summary>
        [JsonPropertyName("power")]
        public int? Power { get; set; }

        /// <summary>GPU board power draw in whole watts. NVML backend only.</summary>
        [JsonPropertyName("watts")]
        public int? Watts { get; set; }

        /// <summary>
        /// GPU enforced power limit in whole watts - the denominator of
        /// <see cref="Power"/>. NVML backend only, like <see cref="Watts"/>; read
        /// once per handle acquisition, so it has session-like lifetime rather than
        /// a per-tick cadence.
        /// </summary>
        [JsonPropertyName("limitW")]
        public int? LimitW { get; set; }

        /// <summary>GPU core clock in MHz.</summary>
        [JsonPropertyName("clock")]
        public int? Clock { get; set; }

        /// <summary>GPU memory (VRAM) clock in MHz.</summary>
        [JsonPropertyName("vramClock")]
        public int? VramClock { get; set; }

        [JsonPropertyName("cpu")]
        public string? Cpu { get; set; }

        [JsonPropertyName("cpuLoad")]
        public int? CpuLoad { get; set; }

        [JsonPropertyName("ramUsed")]
        public long? RamUsed { get; set; }

        [JsonPropertyName("ramTotal")]
        public long? RamTotal { get; set; }

        [JsonPropertyName("diskFree")]
        public long? DiskFree { get; set; }

        [JsonPropertyName("diskTotal")]
        public long? DiskTotal { get; set; }

        /// <summary>Windows version, e.g. "11 23H2" (static per session).</summary>
        [JsonPropertyName("win")]
        public string? Win { get; set; }

        /// <summary>Antivirus health: 0 = green, 1 = yellow, 2 = red.</summary>
        [JsonPropertyName("av")]
        public int? Av { get; set; }

        /// <summary>Pending reboot: always 0 or 1 when known (never omitted-for-false).</summary>
        [JsonPropertyName("reboot")]
        public int? Reboot { get; set; }

        /// <summary>Windows Firewall status: 1 = enabled/OK, 0 = disabled or at risk.</summary>
        [JsonPropertyName("fw")]
        public int? Fw { get; set; }

        /// <summary>System uptime in whole seconds (boot time = now - up, display-side).</summary>
        [JsonPropertyName("up")]
        public long? Up { get; set; }
    }

    /// <summary>
    /// Pushes GPU and system metrics to an ESP32 LCD display via UDP fire-and-forget.
    /// Discovery: pings the display address once per minute for up to
    /// <see cref="Constants.DisplayDiscoveryAttempts"/> attempts; first reply freezes
    /// the endpoint for the session, exhaustion disables the feature until restart.
    /// Push: one ~354-byte (≤522) JSON datagram per second. Never throws, never blocks.
    /// </summary>
    internal static class GpuDisplayPushService
    {
        /// <summary>
        /// Wire protocol version; bumped only on breaking schema changes.
        /// </summary>
        public const int ProtocolVersion = 1;

        /// <summary>
        /// Wire-contract cap for identity strings (GPU name, host name, CPU name),
        /// in JSON-encoded bytes (see <see cref="TruncateIdentity"/>). The firmware
        /// strlcpy's them into 64-byte buffers, so 63 plus the NUL fills a
        /// destination exactly (the decoded form is never longer than the encoded
        /// form, so this cap is safe for both the datagram and those buffers).
        /// </summary>
        internal const int MaxIdentityLength = 63;

        /// <summary>
        /// Wire-contract cap for the Windows version string, in JSON-encoded bytes
        /// ("11 23H2" is 7; 16 is defensive headroom that also bounds the datagram
        /// budget math).
        /// </summary>
        internal const int MaxOsVersionLength = 16;

        /// <summary>
        /// Wire-contract cap for one datagram - a whole-payload budget, where
        /// <see cref="MaxIdentityLength"/> and <see cref="MaxOsVersionLength"/> bound
        /// the strings on their own. Raised 384 -> 448 when three maxed-out names plus
        /// the widest numerics measured 386, then 448 -> 496 when power/clock/win/av/
        /// reboot/up added 84 worst-case bytes (measured 470); fw added 7 more
        /// (measured 477); vramClock added 18 more (measured 495); watts added 13 more
        /// (measured 508); limitW added 14 more (measured 522), which is this cap.
        /// <para>
        /// The cap EQUALS the measured worst case (no headroom between them at all),
        /// exactly as it did at 508/508 - but the second interval is no longer spent:
        /// the v5.12.0 raise rode a renegotiation of the receiver floor from >= 512
        /// to >= 1024 bytes (the reference consumer's buffers were raised 496 -> 1024
        /// in parallel), so 502 bytes now separate this cap from that floor. A further
        /// field therefore raises this constant and re-pins the worst-case test in
        /// the same change - and only a total approaching 1024 reopens the receiver
        /// contract in push_metrics.md. Pinned by the worst-case test, which
        /// asserts both the exact 522 and this constant.
        /// </para>
        /// </summary>
        internal const int MaxDatagramBytes = 522;

        /// <summary>
        /// The push loop re-reads the slow-changing OS-health values every this many
        /// ticks (~1 minute at 1 Hz) via <see cref="SystemMetricsService.RefreshOsHealth"/> -
        /// a counter comparison on the existing timer, not a new timer.
        /// </summary>
        private const int OsHealthRefreshTicks = 60;

        /// <summary>
        /// The push loop writes one Debug line with the CPU and disk sensor readings every
        /// this many ticks (~1 minute at 1 Hz) - the same counter idiom as
        /// <see cref="OsHealthRefreshTicks"/>, on the existing timer, not a second
        /// mechanism.
        /// <para>
        /// It exists because none of those readings is on the wire yet, so without this line
        /// there is no way to tell a working sensor from a silent one short of a debugger.
        /// One formatted string per minute is the whole cost.
        /// </para>
        /// </summary>
        private const int SensorLogTicks = 60;

        /// <summary>
        /// SIO_UDP_CONNRESET: stops Windows from surfacing ICMP port-unreachable
        /// (display powered off) as SocketException on subsequent sends.
        /// Input is a 4-byte BOOL; 0 = disable the reset behavior.
        /// </summary>
        private const int SioUdpConnReset = -1744830452;

        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>
        /// Discovery + push loop; runs until cancelled. Never throws.
        /// </summary>
        public static async Task RunAsync(CancellationToken cancellationToken)
        {
            UdpClient? client = null;

            // Owned by this loop, and disposed in the finally beside the socket. They are
            // NOT held by SystemMetricsService, which is static: a static instance field
            // there would be process-global mutable state shared by every test that touches
            // GetSystemMetrics, which is the exact problem MetricsPusher.Tests'
            // ProcessGlobalCollection exists to work around. Here their lifetime is the push
            // loop's, which is also the only thing that reads them.
            CpuTemperatureService? cpuSensors = null;
            NvmeTemperatureService? diskSensor = null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                IPEndPoint? target = await DiscoverDisplayAsync(cancellationToken).ConfigureAwait(false);
                if (target == null)
                {
                    LoggingService.Info(
                        $"GpuDisplayPushService: no display answered after {Constants.DisplayDiscoveryAttempts} attempts; GPU display push disabled for this session");
                    return;
                }

                LoggingService.Info($"GpuDisplayPushService: display found at {target}");

                // So the first timer tick, ~1 second from now, already carries CPU usage
                SystemMetricsService.PrimeCpuCounter();

                // The CPU and disk sensors start here for the same reason, and pay the same
                // kind of one-time cost off the tick: a CreateFile, a PawnIO module load and
                // a handful of init reads for the CPU, two CreateFiles and a tier probe for
                // the disk. Prime() is the energy counter's PrimeCpuCounter - RAPL exposes a
                // free-running accumulator, not a wattage, so a first sample has to exist
                // before a second one can be a rate.
                //
                // Coupling worth knowing: this loop only runs once an NVIDIA GPU is found
                // and a display has answered discovery, so on a machine with neither, none
                // of these sensors is ever initialized. That is the design (plan section
                // 3.5) rather than an oversight - they exist to fill fields in this
                // datagram, and there is no datagram without a GPU.
                cpuSensors = new CpuTemperatureService();
                diskSensor = new NvmeTemperatureService();
                _ = cpuSensors.Initialize();
                _ = diskSensor.Initialize();
                cpuSensors.Prime();

                // And usually the OS-health values too (refreshed once a minute below;
                // queued off-loop so a hung Security Center RPC can never stall sends)
                SystemMetricsService.RefreshOsHealthInBackground();

                string hostName = Environment.MachineName;
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                bool sendFailing = false;
                bool oversizeWarned = false;
                int ticksSinceOsHealth = 0;
                int ticksSinceSensorLog = 0;

                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (++ticksSinceOsHealth >= OsHealthRefreshTicks)
                    {
                        ticksSinceOsHealth = 0;
                        SystemMetricsService.RefreshOsHealthInBackground();
                    }

                    if (!GpuMonitorService.IsGpuAvailable)
                        continue;

                    SystemMetrics systemMetrics = SystemMetricsService.GetSystemMetrics();

                    // The whole per-tick cost of the new sensors: one temperature read, one
                    // energy read, one disk read. The power limit is a cached field, read
                    // once at init. None of the four reaches the wire yet - see the comment
                    // on these properties in SystemMetricsService.
                    systemMetrics.CpuTemperature = cpuSensors.ReadTemperature();
                    systemMetrics.CpuPowerWatts = ToWholeWatts(cpuSensors.ReadPackagePower());
                    systemMetrics.CpuPowerLimitWatts = ToWholeWatts(cpuSensors.PackagePowerLimitWatts);
                    systemMetrics.NvmeTemperature = diskSensor.TryRead(out float diskCelsius) ? diskCelsius : null;

                    if (++ticksSinceSensorLog >= SensorLogTicks)
                    {
                        ticksSinceSensorLog = 0;
                        LogSensorReadings(systemMetrics, cpuSensors.Source);
                    }

                    byte[]? datagram = BuildPayloadUtf8(GpuMonitorService.GetGpuMetrics(), systemMetrics, hostName);
                    if (datagram == null)
                        continue;

                    oversizeWarned = NoteOversizeDatagram(datagram.Length, oversizeWarned);

                    try
                    {
                        client ??= CreateUdpClient();
                        await client.SendAsync(datagram, target, cancellationToken).ConfigureAwait(false);

                        if (sendFailing)
                        {
                            sendFailing = false;
                            LoggingService.Info("GpuDisplayPushService: send recovered");
                        }
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                    {
                        client?.Dispose();
                        client = null;

                        if (!sendFailing)
                        {
                            sendFailing = true;
                            LoggingService.Warn($"GpuDisplayPushService: send failing: {ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // App exit - silently stop
            }
            catch (Exception ex)
            {
                LoggingService.Error("GpuDisplayPushService: loop faulted", ex);
            }
            finally
            {
                client?.Dispose();

                // The CPU service disposes its providers before the PawnIO handle they share
                // - it owns that order, and this call site must not try to help.
                cpuSensors?.Dispose();
                diskSensor?.Dispose();
                LoggingService.Info("GpuDisplayPushService: stopped");
            }
        }

        /// <summary>
        /// Rounds a wattage to whole watts, matching how the GPU's <c>watts</c> and
        /// <c>limitW</c> are already carried. Null in, null out - an absent reading must
        /// stay absent rather than becoming a zero.
        /// </summary>
        /// <param name="watts">The reading, or null.</param>
        /// <returns>The reading in whole watts, or null.</returns>
        private static int? ToWholeWatts(float? watts)
        {
            return watts == null ? null : (int)MathF.Round(watts.Value);
        }

        /// <summary>
        /// One Debug line a minute carrying the sensors that are not on the wire yet. The
        /// source is included because an ACPI thermal-zone reading and a die reading are not
        /// the same measurement, and a number alone would not say which one this is.
        /// </summary>
        /// <param name="systemMetrics">The metrics just collected.</param>
        /// <param name="cpuTemperatureSource">Where the CPU temperature came from.</param>
        private static void LogSensorReadings(SystemMetrics systemMetrics, CpuTemperatureSource cpuTemperatureSource)
        {
            LoggingService.Debug(
                $"GpuDisplayPushService: CPU {systemMetrics.CpuTemperature?.ToString("F1") ?? "-"} C ({cpuTemperatureSource}), " +
                $"package {systemMetrics.CpuPowerWatts?.ToString() ?? "-"} W of {systemMetrics.CpuPowerLimitWatts?.ToString() ?? "-"} W, " +
                $"disk {systemMetrics.NvmeTemperature?.ToString("F1") ?? "-"} C");
        }

        /// <summary>
        /// Edge-triggered guard for the one contract this service cannot otherwise
        /// enforce at runtime: <see cref="MaxDatagramBytes"/> is pinned by a unit test,
        /// and the worst case EQUALS that ceiling (522 of 522 since v5.12.0, under a
        /// >= 1024-byte receiver floor). A field added without re-running that
        /// test would overrun a consumer's buffer silently, in the field.
        /// <para>
        /// The oversize datagram is still SENT. Truncating it would produce invalid JSON
        /// and dropping it would blank the display, while a receiver that honors the
        /// contractual buffer floor still parses anything up to 1024 bytes: the payload is
        /// the better of the available outcomes, and the log line is what makes the
        /// overrun findable. One integer comparison per second.
        /// </para>
        /// </summary>
        /// <param name="datagramBytes">Length of the datagram about to be sent.</param>
        /// <param name="alreadyWarned">Whether the current oversize streak was logged.</param>
        /// <returns>The new "already warned" state, to be carried to the next tick.</returns>
        internal static bool NoteOversizeDatagram(int datagramBytes, bool alreadyWarned)
        {
            if (datagramBytes <= MaxDatagramBytes)
                return false; // Under budget: re-arm, so a later streak is reported too

            if (alreadyWarned)
                return true; // Same streak - it repeats every second; one line is the diagnostic

            LoggingService.Warn(
                $"GpuDisplayPushService: datagram is {datagramBytes} bytes, over the {MaxDatagramBytes}-byte wire contract " +
                "(sending anyway); consumers buffering only the contractual 1024 bytes may truncate it");
            return true;
        }

        /// <summary>
        /// Replaces the last octet of an IPv4 address with <see cref="Constants.DisplayHostOctet"/>.
        /// Returns null for null/non-IPv4 input, when the address is not on a private
        /// network (see <see cref="IsPrivateIPv4"/>), or when the local address already
        /// holds the display octet (the app must never target the PC itself).
        /// </summary>
        internal static IPAddress? DeriveDisplayAddress(IPAddress? localIPv4)
        {
            if (localIPv4 == null || localIPv4.AddressFamily != AddressFamily.InterNetwork)
                return null;

            byte[] bytes = localIPv4.GetAddressBytes();
            if (!IsPrivateIPv4(bytes))
                return null;

            if (bytes[3] == Constants.DisplayHostOctet)
                return null;

            bytes[3] = (byte)Constants.DisplayHostOctet;
            return new IPAddress(bytes);
        }

        /// <summary>
        /// Whether an IPv4 address is on a network the "trusted local subnet" premise in
        /// push_metrics.md section 10 can actually be made about: RFC 1918 private space,
        /// RFC 6598 carrier-grade NAT, or RFC 3927 link-local.
        /// <para>
        /// The push is cleartext and unauthenticated by design, and the destination is
        /// DERIVED rather than configured - so without this check a PC holding a routable
        /// public IPv4 (a bridged modem, some hosting and VM setups) would push its host
        /// name, hardware, uptime and its antivirus/firewall/pending-reboot posture to a
        /// stranger's machine on the internet, once per second, all session. That is not an
        /// instance of the accepted LAN trade-off; it is the premise silently not holding.
        /// Refusing to derive an address is the same outcome as having no network yet, which
        /// the discovery loop already handles.
        /// </para>
        /// </summary>
        /// <param name="bytes">The four octets of an IPv4 address, in network order.</param>
        /// <returns>True when the address belongs to a private or link-local range.</returns>
        internal static bool IsPrivateIPv4(byte[] bytes)
        {
            return bytes switch
            {
                [10, _, _, _] => true,                       // 10.0.0.0/8
                [172, >= 16 and <= 31, _, _] => true,        // 172.16.0.0/12
                [192, 168, _, _] => true,                    // 192.168.0.0/16
                [100, >= 64 and <= 127, _, _] => true,       // 100.64.0.0/10 (CGNAT)
                [169, 254, _, _] => true,                    // 169.254.0.0/16 (link-local)
                _ => false,
            };
        }

        /// <summary>
        /// Serializes metrics to the wire JSON string, or null when
        /// <see cref="BuildPayload"/> suppresses the datagram. The string-returning
        /// reference path the wire-contract tests assert against; the push loop uses
        /// <see cref="BuildPayloadUtf8"/>, which a test pins byte-identical to this.
        /// </summary>
        internal static string? BuildPayloadJson(GpuMetrics metrics, SystemMetrics systemMetrics, string? hostName)
        {
            GpuDisplayPayload? payload = BuildPayload(metrics, systemMetrics, hostName);
            return payload == null ? null : JsonSerializer.Serialize(payload, SerializerOptions);
        }

        /// <summary>
        /// Same payload and suppression rules as <see cref="BuildPayloadJson"/>, but
        /// serialized directly to the UTF-8 bytes the datagram carries - the once-per-second
        /// send path never materializes a string only to transcode it.
        /// </summary>
        internal static byte[]? BuildPayloadUtf8(GpuMetrics metrics, SystemMetrics systemMetrics, string? hostName)
        {
            GpuDisplayPayload? payload = BuildPayload(metrics, systemMetrics, hostName);
            return payload == null ? null : JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
        }

        /// <summary>
        /// Builds the wire payload. Returns null when every per-tick live metric
        /// (GPU temperature/load/VRAM/fan/power/watts/clocks, CPU usage, RAM, disk) is null -
        /// identity/ambient fields (GPU name, host name, CPU name, Windows version,
        /// uptime, antivirus health, firewall status, pending reboot, enforced power
        /// limit) alone send no datagram.
        /// The ambient fields are excluded because they are practically always
        /// available (uptime always, av/fw/reboot from the slow cache), so counting any
        /// of them would make this guard dead code and blank the display's last-good
        /// screen with a names-only payload when every real sensor fails.
        /// </summary>
        private static GpuDisplayPayload? BuildPayload(GpuMetrics metrics, SystemMetrics systemMetrics, string? hostName)
        {
            if (metrics.Temperature == null && metrics.UsagePercent == null &&
                metrics.VramUsedMB == null && metrics.VramTotalMB == null &&
                metrics.FanSpeedPercent == null &&
                metrics.PowerPercent == null && metrics.PowerWatts == null &&
                metrics.CoreClockMHz == null && metrics.MemoryClockMHz == null &&
                systemMetrics.CpuUsagePercent == null &&
                systemMetrics.RamUsedMB == null && systemMetrics.RamTotalMB == null &&
                systemMetrics.DiskFreeGB == null && systemMetrics.DiskTotalGB == null)
            {
                return null;
            }

            return new GpuDisplayPayload
            {
                Version = ProtocolVersion,
                Gpu = TruncateIdentity(metrics.Name),
                Host = TruncateIdentity(hostName),
                Temp = metrics.Temperature,
                Load = metrics.UsagePercent,
                VramUsed = metrics.VramUsedMB,
                VramTotal = metrics.VramTotalMB,
                Fan = metrics.FanSpeedPercent,
                Power = metrics.PowerPercent,
                Watts = metrics.PowerWatts,
                LimitW = metrics.PowerLimitWatts,
                Clock = metrics.CoreClockMHz,
                VramClock = metrics.MemoryClockMHz,
                Cpu = TruncateIdentity(systemMetrics.CpuName),
                CpuLoad = systemMetrics.CpuUsagePercent,
                RamUsed = systemMetrics.RamUsedMB,
                RamTotal = systemMetrics.RamTotalMB,
                DiskFree = systemMetrics.DiskFreeGB,
                DiskTotal = systemMetrics.DiskTotalGB,
                Win = TruncateIdentity(systemMetrics.WindowsVersion, MaxOsVersionLength),
                Av = systemMetrics.AntivirusHealth,
                Reboot = systemMetrics.RebootPending,
                Fw = systemMetrics.FirewallEnabled,
                Up = systemMetrics.UptimeSeconds,
            };
        }

        /// <summary>
        /// Caps a wire string at <paramref name="maxLength"/> JSON-encoded bytes, not
        /// characters: the serializer's default encoder escapes non-ASCII and
        /// HTML-sensitive characters as \uXXXX (up to 6 bytes per character), and both
        /// the datagram budget and the firmware's receive buffers are byte-denominated.
        /// For plain ASCII this is the original character cap unchanged. Returns null
        /// for input the encoder rejects (invalid UTF-16) - omitting the field rather
        /// than faulting the push loop.
        /// </summary>
        internal static string? TruncateIdentity(string? value, int maxLength = MaxIdentityLength)
        {
            if (value == null)
                return null;

            // Fast path for the overwhelmingly common case (ASCII hardware and host
            // names, "11 23H2"): for characters the encoder passes through verbatim the
            // encoded byte count *is* the character count, so a short-enough string is
            // provably already compliant and needs no JsonEncodedText allocation.
            if (value.Length <= maxLength && IsUnescapedPrintableAscii(value))
                return value;

            try
            {
                if (JsonEncodedText.Encode(value).EncodedUtf8Bytes.Length <= maxLength)
                    return value;

                // Encoded length >= char count, so maxLength chars is a safe upper
                // bound; walk back until the encoded form fits, never splitting a
                // surrogate pair (a lone half would make Encode throw).
                int length = Math.Min(value.Length, maxLength);
                while (length > 0)
                {
                    if (char.IsHighSurrogate(value[length - 1]))
                    {
                        length--;
                        continue;
                    }

                    if (JsonEncodedText.Encode(value[..length]).EncodedUtf8Bytes.Length <= maxLength)
                        return value[..length];

                    length--;
                }

                return string.Empty;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// True when every character is printable ASCII that the serializer's default
        /// encoder emits verbatim - i.e. inside 0x20..0x7E and outside
        /// JavaScriptEncoder.Default's escape set (the HTML-sensitive characters plus
        /// the JSON backslash and the backtick). For such strings the JSON-encoded byte
        /// length equals the character count. A test sweeps the whole 0x20..0x7E range
        /// against the encoder so this predicate can never drift from it.
        /// </summary>
        private static bool IsUnescapedPrintableAscii(string value)
        {
            foreach (char c in value)
            {
                if (c < ' ' || c > '~')
                    return false;

                switch (c)
                {
                    case '"':
                    case '\\':
                    case '<':
                    case '>':
                    case '&':
                    case '\'':
                    case '+':
                    case '`':
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Pings the derived display address once per minute (first attempt immediate)
        /// until it answers or the attempt budget is exhausted. Every attempt consumes
        /// budget - a hard wall-clock window of roughly attempts × interval.
        /// </summary>
        private static async Task<IPEndPoint?> DiscoverDisplayAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Constants.DisplayDiscoveryIntervalSeconds));

            for (int attempt = 0; attempt < Constants.DisplayDiscoveryAttempts; attempt++)
            {
                if (attempt > 0 && !await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                    return null;

                IPAddress? displayAddress = DeriveDisplayAddress(LocalNetworkService.GetLocalIPv4Address());
                if (displayAddress == null)
                    continue; // No network yet, or the PC itself holds .99 - attempt still consumed

                try
                {
                    using var ping = new Ping();
                    PingReply reply = await ping.SendPingAsync(
                        displayAddress,
                        TimeSpan.FromMilliseconds(Constants.DisplayPingTimeoutMs),
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (reply.Status == IPStatus.Success)
                        return new IPEndPoint(displayAddress, Constants.DisplayUdpPort);
                }
                catch (Exception ex) when (ex is PingException or SocketException)
                {
                    // Attempt consumed; no per-attempt logging (a missing display is normal)
                }
            }

            return null;
        }

        /// <summary>
        /// Creates the send-only UDP client with ICMP-unreachable feedback suppressed,
        /// so a powered-off display costs nothing per tick.
        /// </summary>
        private static UdpClient CreateUdpClient()
        {
            var client = new UdpClient(AddressFamily.InterNetwork);
            try
            {
                client.Client.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (SocketException)
            {
                // Non-fatal: without it, ICMP resets surface as send failures, which are handled
            }

            return client;
        }
    }
}
