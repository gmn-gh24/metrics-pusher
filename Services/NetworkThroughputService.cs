using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MetricsPusher.Services
{
    /// <summary>
    /// Link speed, media type, adapter identity and rx/tx throughput for the one network
    /// adapter the push already uses - the same interface
    /// <see cref="LocalNetworkService.GetLocalIPv4Address"/> selects to derive the display
    /// address, resolved once at <see cref="Initialize"/> and never re-picked mid-session
    /// (matching the frozen discovery endpoint it feeds).
    /// <para>
    /// The per-tick cost is one <c>GetIfEntry2</c> call into a preallocated, reused buffer:
    /// no enumeration, no managed <c>NetworkInterface</c> objects, no allocation. The managed
    /// <c>GetAllNetworkInterfaces()</c> route the discovery path uses would enumerate and
    /// allocate every adapter once per second, which is why it stays on the
    /// once-per-discovery-attempt path and this class exists at all.
    /// </para>
    /// <para>
    /// Throughput is a rate derived from the interface's free-running 64-bit octet
    /// counters, with the same accumulator discipline <see cref="RaplEnergyWindow"/>
    /// established for CPU package power: the baseline sample produces no value, the
    /// interval is measured with <see cref="Stopwatch"/> rather than assumed, intervals
    /// outside 0.5-2 s are rejected (the first tick after sleep/resume must drop rather
    /// than report a fabricated multi-hour average), and the window always advances so a
    /// rejected sample costs one reading and self-heals on the next tick. Unlike RAPL's
    /// 32-bit accumulator these counters are 64-bit and do not wrap in practice, so a
    /// decrease is an adapter reset (disable/re-enable, driver restart) and re-baselines
    /// instead of producing a rate.
    /// </para>
    /// <para>
    /// Not <see cref="IDisposable"/> on purpose: <c>GetIfEntry2</c> holds no handle, so
    /// there is no resource for a Dispose to release, and having one would only invite a
    /// call site to manage a lifetime that does not exist (the same reasoning as
    /// <see cref="CpuPackagePowerProvider"/>).
    /// </para>
    /// </summary>
    internal sealed class NetworkThroughputService
    {
        /// <summary>The wire value for an Ethernet adapter (IANA ifType 6).</summary>
        internal const int MediaTypeEthernet = 0;

        /// <summary>The wire value for a Wi-Fi adapter (IANA ifType 71).</summary>
        internal const int MediaTypeWifi = 1;

        /// <summary>The wire value for any other adapter type.</summary>
        internal const int MediaTypeOther = 2;

        // MIB_IF_ROW2 layout for x64, derived from netioapi.h's field order under MSVC
        // alignment rules and pinned by a test against Marshal.OffsetOf on an equivalent
        // [StructLayout(Sequential)] declaration - so a wrong constant here fails that
        // test rather than decoding plausible-looking garbage from the wrong offset.
        // The row is kept as a raw byte buffer instead of that struct so the per-tick
        // read is allocation- and marshaling-free.
        internal const int RowSize = 1352;
        internal const int InterfaceIndexOffset = 8;      // NET_IFINDEX, after the 8-byte NET_LUID
        internal const int DescriptionOffset = 542;       // WCHAR[257], after the GUID and the Alias
        internal const int TypeOffset = 1128;             // IFTYPE (IANA ifType)
        internal const int MediaConnectStateOffset = 1164; // NET_IF_MEDIA_CONNECT_STATE
        internal const int ReceiveLinkSpeedOffset = 1200; // ULONG64, bits per second
        internal const int InOctetsOffset = 1208;         // ULONG64, cumulative bytes received
        internal const int OutOctetsOffset = 1280;        // ULONG64, cumulative bytes sent

        /// <summary>WCHAR count of the Description field (IF_MAX_STRING_SIZE + 1).</summary>
        internal const int DescriptionChars = 257;

        private const uint NoError = 0;

        private const uint IfTypeEthernetCsmacd = 6; // IF_TYPE_ETHERNET_CSMACD
        private const uint IfTypeIeee80211 = 71;     // IF_TYPE_IEEE80211
        private const uint MediaConnectStateConnected = 1; // MediaConnectStateConnected

        /// <summary>Some virtual adapters report all-ones for a link speed they do not have.</summary>
        private const ulong LinkSpeedUnknown = ulong.MaxValue;

        /// <summary>
        /// Above this the value came from a misreported link, not a NIC this app will meet:
        /// 400 Gbps is the fastest Ethernet standard with real deployments.
        /// </summary>
        private const int MaxPlausibleLinkMbps = 400_000;

        /// <summary>
        /// Rate cap in kbit/s (100 Gbit/s). A delta that exceeds it is a counter glitch,
        /// not traffic - the plausible-link cap above already bounds what the adapter
        /// could carry.
        /// </summary>
        private const long MaxPlausibleRateKbps = 100_000_000;

        // The elapsed-time window a rate sample must fall in - the same band, for the
        // same reasons, as CpuPackagePowerProvider's RAPL window: below it two ticks
        // landed on top of each other and jitter becomes a rate spike; above it the
        // machine slept or the process was descheduled and the delta no longer
        // describes a rate anyone wants.
        private const double MinElapsedSeconds = 0.5;
        private const double MaxElapsedSeconds = 2.0;

        private readonly object _lock = new object(); // Guards every field below

        // Preallocated and reused every tick; capacity, not the length of any one call.
        private readonly byte[] _row = new byte[RowSize];

        private ProbeState _state;
        private uint _interfaceIndex;
        private string? _adapterName;
        private int? _mediaType;
        private bool _readFailing; // Edge-triggered logging: one line per failure streak, not one per tick

        // The throughput window: last octet counters and the Stopwatch timestamp taken
        // next to the read that produced them. Advances on every successful read, even
        // when the resulting sample is rejected - see the type remarks.
        private ulong _lastInOctets;
        private ulong _lastOutOctets;
        private long _lastTimestamp;
        private bool _primed;

        private enum ProbeState
        {
            NotInitialized, // Adapter not resolved yet
            Ready,          // Adapter resolved, identity cached, throughput baseline primed
            Unavailable,    // No adapter, or the probe failed - latched for the session
        }

        /// <summary>
        /// The adapter's driver description (make/model, e.g. "Intel Ethernet Controller
        /// I225-V"), with trademark marks already stripped; null until <see cref="Initialize"/>
        /// succeeds or when the description was empty. Deliberately the description and not
        /// the alias: the alias ("Ethernet", "Wi-Fi 2") is user-renameable and says nothing
        /// about the hardware. Read once at initialization - a session-static fact served
        /// from cache at zero per-tick cost, like the CPU name.
        /// </summary>
        internal string? AdapterName
        {
            get
            {
                lock (_lock)
                {
                    return _adapterName;
                }
            }
        }

        /// <summary>
        /// The adapter's media type mapped for the wire (<see cref="MediaTypeEthernet"/> /
        /// <see cref="MediaTypeWifi"/> / <see cref="MediaTypeOther"/>); null until
        /// <see cref="Initialize"/> succeeds. Session-static, like <see cref="AdapterName"/>.
        /// </summary>
        internal int? MediaType
        {
            get
            {
                lock (_lock)
                {
                    return _mediaType;
                }
            }
        }

        /// <summary>
        /// Resolves the adapter, caches its identity and primes the throughput baseline.
        /// Idempotent and never throws; a false verdict is latched for the session.
        /// Called off the tick, but <see cref="TryRead"/> self-initializes too, so the
        /// eager and lazy paths cannot disagree.
        /// </summary>
        /// <returns>True when network metrics are available on this machine.</returns>
        internal bool Initialize()
        {
            lock (_lock)
            {
                return EnsureInitialized();
            }
        }

        /// <summary>
        /// One <c>GetIfEntry2</c> read: link speed, connect state and the octet counters,
        /// turned into a sample. Never throws; false means the read itself failed (or the
        /// service is unavailable) and the caller should carry nulls this tick. True with
        /// null members is normal - a rejected interval, a counter reset or a disconnected
        /// medium each null the affected values while the read stays healthy.
        /// </summary>
        /// <param name="sample">The decoded sample; default when this returns false.</param>
        /// <returns>True when the interface row was read this tick.</returns>
        internal bool TryRead(out Sample sample)
        {
            lock (_lock)
            {
                sample = default;

                if (!EnsureInitialized())
                    return false;

                uint error = QueryRow();

                // Taken next to the read, not at the top of the tick, so scheduling delay
                // between the two cannot leak into the rate interval.
                long timestamp = Stopwatch.GetTimestamp();

                if (error != NoError)
                {
                    NoteFailure("GetIfEntry2", (int)error);
                    return false;
                }

                if (_readFailing)
                {
                    _readFailing = false;
                    LoggingService.Debug("NetworkThroughputService: interface read recovered");
                }

                bool connected = DecodeMediaConnectState(_row) == MediaConnectStateConnected;
                int? linkMbps = connected ? MapLinkSpeedMbps(DecodeReceiveLinkSpeed(_row)) : null;

                ulong inOctets = DecodeInOctets(_row);
                ulong outOctets = DecodeOutOctets(_row);

                bool wasPrimed = _primed;
                ulong previousIn = _lastInOctets;
                ulong previousOut = _lastOutOctets;
                long previousTimestamp = _lastTimestamp;

                // The window always advances, even when the sample below is rejected -
                // otherwise the tick after a resume would still span the sleep, forever
                // in the pathological case (see RaplEnergyWindow, which owns this rule).
                _lastInOctets = inOctets;
                _lastOutOctets = outOctets;
                _lastTimestamp = timestamp;
                _primed = true;

                long? rxKbps = null;
                long? txKbps = null;
                if (wasPrimed && connected)
                {
                    double elapsedSeconds = (timestamp - previousTimestamp) / (double)Stopwatch.Frequency;
                    if (TryComputeRateKbps(previousIn, inOctets, elapsedSeconds, out long rx))
                        rxKbps = rx;
                    if (TryComputeRateKbps(previousOut, outOctets, elapsedSeconds, out long tx))
                        txKbps = tx;
                }

                sample = new Sample(linkMbps, rxKbps, txKbps);
                return true;
            }
        }

        /// <summary>
        /// Turns two octet-counter samples and a measured interval into whole kbit/s,
        /// rejecting anything that is not a usable measurement. Pure, so the interval
        /// window, the counter-reset case and the plausibility cap are all testable
        /// without an adapter.
        /// </summary>
        /// <param name="previousOctets">The counter at the previous sample.</param>
        /// <param name="currentOctets">The counter at this sample.</param>
        /// <param name="elapsedSeconds">The measured interval between them.</param>
        /// <param name="kbps">The rate in kbit/s (0 is a real idle reading), or 0 when this returns false.</param>
        /// <returns>True when the interval and the result are both usable.</returns>
        internal static bool TryComputeRateKbps(ulong previousOctets, ulong currentOctets, double elapsedSeconds, out long kbps)
        {
            kbps = 0;

            if (double.IsNaN(elapsedSeconds) || elapsedSeconds < MinElapsedSeconds || elapsedSeconds > MaxElapsedSeconds)
                return false;

            // The counters are 64-bit and do not wrap in practice (58 million years of
            // 10GbE), so a decrease is always an adapter reset: drop the sample and let
            // the already-advanced window re-baseline.
            if (currentOctets < previousOctets)
                return false;

            double candidate = Math.Round((currentOctets - previousOctets) * 8.0 / (1000.0 * elapsedSeconds));
            if (double.IsNaN(candidate) || candidate < 0.0 || candidate > MaxPlausibleRateKbps)
                return false;

            kbps = (long)candidate;
            return true;
        }

        /// <summary>
        /// Maps a raw link speed (bits per second) to whole Mbps for the wire. Null for
        /// the two "no answer" encodings - zero and all-ones - and for anything outside
        /// (0, 400000] Mbps, the same drop-not-clamp rule every other validated field uses.
        /// </summary>
        /// <param name="receiveLinkSpeed">The ReceiveLinkSpeed field as read.</param>
        /// <returns>The link speed in Mbps, or null when there is no usable value.</returns>
        internal static int? MapLinkSpeedMbps(ulong receiveLinkSpeed)
        {
            if (receiveLinkSpeed == 0 || receiveLinkSpeed == LinkSpeedUnknown)
                return null;

            ulong mbps = receiveLinkSpeed / 1_000_000;
            if (mbps == 0 || mbps > MaxPlausibleLinkMbps)
                return null;

            return (int)mbps;
        }

        /// <summary>
        /// Maps an IANA ifType to the wire's media-type enum. Only the two types a consumer
        /// would render differently get their own value; everything else is
        /// <see cref="MediaTypeOther"/>.
        /// </summary>
        /// <param name="interfaceType">The Type field as read (IANA ifType).</param>
        /// <returns>The wire media type.</returns>
        internal static int MapMediaType(uint interfaceType)
        {
            return interfaceType switch
            {
                IfTypeEthernetCsmacd => MediaTypeEthernet,
                IfTypeIeee80211 => MediaTypeWifi,
                _ => MediaTypeOther,
            };
        }

        /// <summary>
        /// Decodes the InterfaceIndex field - also the offset self-check's witness: it
        /// sits after the LUID, so a layout error anywhere before it shows up as a
        /// mismatch against the requested index instead of as plausible traffic numbers.
        /// </summary>
        /// <param name="row">The MIB_IF_ROW2 buffer.</param>
        /// <returns>The interface index in the row.</returns>
        internal static uint DecodeInterfaceIndex(ReadOnlySpan<byte> row)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(InterfaceIndexOffset, sizeof(uint)));
        }

        /// <summary>
        /// Decodes the IANA ifType.
        /// </summary>
        /// <param name="row">The MIB_IF_ROW2 buffer.</param>
        /// <returns>The raw interface type.</returns>
        internal static uint DecodeInterfaceType(ReadOnlySpan<byte> row)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(TypeOffset, sizeof(uint)));
        }

        /// <summary>
        /// Decodes the media connect state (1 = connected).
        /// </summary>
        /// <param name="row">The MIB_IF_ROW2 buffer.</param>
        /// <returns>The raw connect state.</returns>
        internal static uint DecodeMediaConnectState(ReadOnlySpan<byte> row)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(MediaConnectStateOffset, sizeof(uint)));
        }

        /// <summary>
        /// Decodes the receive link speed in bits per second.
        /// </summary>
        /// <param name="row">The MIB_IF_ROW2 buffer.</param>
        /// <returns>The raw link speed.</returns>
        internal static ulong DecodeReceiveLinkSpeed(ReadOnlySpan<byte> row)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(ReceiveLinkSpeedOffset, sizeof(ulong)));
        }

        /// <summary>
        /// Decodes the cumulative received-octets counter.
        /// </summary>
        /// <param name="row">The MIB_IF_ROW2 buffer.</param>
        /// <returns>The raw counter.</returns>
        internal static ulong DecodeInOctets(ReadOnlySpan<byte> row)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(InOctetsOffset, sizeof(ulong)));
        }

        /// <summary>
        /// Decodes the cumulative sent-octets counter.
        /// </summary>
        /// <param name="row">The MIB_IF_ROW2 buffer.</param>
        /// <returns>The raw counter.</returns>
        internal static ulong DecodeOutOctets(ReadOnlySpan<byte> row)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(OutOctetsOffset, sizeof(ulong)));
        }

        /// <summary>
        /// Decodes the Description field (the driver's adapter name): the WCHAR[257]
        /// region up to its first NUL. Null when empty - an absent name must stay a
        /// missing key, not an empty string the truncation path happens to preserve.
        /// </summary>
        /// <param name="row">The MIB_IF_ROW2 buffer.</param>
        /// <returns>The description, or null when the field is empty.</returns>
        internal static string? DecodeDescription(ReadOnlySpan<byte> row)
        {
            ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(
                row.Slice(DescriptionOffset, DescriptionChars * sizeof(char)));

            int nul = chars.IndexOf('\0');
            if (nul >= 0)
                chars = chars[..nul];

            return chars.IsEmpty ? null : new string(chars);
        }

        // The row is in/out: the index written below selects the interface (the LUID
        // stays zero, which tells the API to key on the index instead), and on success
        // the whole row is filled. iphlpapi is pinned to System32 by SystemLibraryResolver.
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern uint GetIfEntry2(byte[] row);

        /// <summary>
        /// Runs the probe once, converting a throwing failure into the latched state.
        /// Both <see cref="Initialize"/> and <see cref="TryRead"/> funnel through here so
        /// the eager and lazy paths cannot drift. Caller must hold the lock.
        /// </summary>
        /// <returns>True when the service is ready to read.</returns>
        private bool EnsureInitialized()
        {
            try
            {
                if (_state == ProbeState.NotInitialized)
                    Probe();
            }
            catch (Exception ex)
            {
                LatchUnavailable($"network metrics disabled: {ex.Message}");
            }

            return _state == ProbeState.Ready;
        }

        /// <summary>
        /// Resolves the push's adapter, reads its row once, self-checks the layout,
        /// caches the session-static identity (name, media type) and primes the
        /// throughput baseline from that same probe read - so the first tick, roughly a
        /// second later, can already report a rate (the same reason
        /// <c>SystemMetricsService.PrimeCpuCounter</c> and the RAPL <c>Prime</c> exist).
        /// Caller must hold the lock.
        /// </summary>
        private void Probe()
        {
            int? interfaceIndex = LocalNetworkService.GetPrimaryInterfaceIndex();
            if (interfaceIndex == null)
            {
                LatchUnavailable("no active interface with an IPv4 gateway");
                return;
            }

            _interfaceIndex = (uint)interfaceIndex.Value;

            uint error = QueryRow();
            if (error != NoError)
            {
                LatchUnavailable($"GetIfEntry2 for interface {_interfaceIndex} failed with error {error}");
                return;
            }

            // Layout self-check: the requested index must read back from where this
            // class says the index lives. A wrong offset table would otherwise decode
            // plausible-looking numbers from the wrong fields - the one failure mode
            // hand-built test fixtures cannot catch, because they would encode the same
            // wrong offsets.
            uint echoedIndex = DecodeInterfaceIndex(_row);
            if (echoedIndex != _interfaceIndex)
            {
                LatchUnavailable($"MIB_IF_ROW2 offset self-check failed (asked for interface {_interfaceIndex}, row echoes {echoedIndex})");
                return;
            }

            _adapterName = SystemMetricsService.StripTrademarkMarks(DecodeDescription(_row));
            _mediaType = MapMediaType(DecodeInterfaceType(_row));

            // The probe read doubles as the rate baseline.
            _lastInOctets = DecodeInOctets(_row);
            _lastOutOctets = DecodeOutOctets(_row);
            _lastTimestamp = Stopwatch.GetTimestamp();
            _primed = true;

            _state = ProbeState.Ready;
            LoggingService.Debug(
                $"NetworkThroughputService: adapter '{_adapterName ?? "?"}' (interface {_interfaceIndex}, media type {_mediaType}); throughput baseline primed");
        }

        /// <summary>
        /// One interface-row read into the reused buffer. The buffer is cleared first so
        /// the zero LUID keys the lookup on the index alone and stale bytes from a prior
        /// read can never masquerade as fresh fields on a partial failure.
        /// Caller must hold the lock.
        /// </summary>
        /// <returns>The NETIO error code; 0 is success.</returns>
        private uint QueryRow()
        {
            Array.Clear(_row);
            BinaryPrimitives.WriteUInt32LittleEndian(_row.AsSpan(InterfaceIndexOffset, sizeof(uint)), _interfaceIndex);
            return GetIfEntry2(_row);
        }

        /// <summary>
        /// Transient-failure note, edge-triggered: one Debug line per failure streak,
        /// matching the codebase's "one line per streak" logging rule. Debug and not
        /// Error - an adapter that briefly cannot be read is expected behavior, not a fault.
        /// </summary>
        /// <param name="operation">What failed.</param>
        /// <param name="error">The error code, or 0 when there is none.</param>
        private void NoteFailure(string operation, int error)
        {
            if (_readFailing)
                return;

            _readFailing = true;
            LoggingService.Debug(
                $"NetworkThroughputService: {operation} failed with error {error}; further read failures are not logged until it recovers");
        }

        /// <summary>
        /// Structural failure: latches <see cref="ProbeState.Unavailable"/> for the
        /// session and says so once.
        /// </summary>
        /// <param name="reason">Why network metrics are off.</param>
        private void LatchUnavailable(string reason)
        {
            _state = ProbeState.Unavailable;
            LoggingService.Debug($"NetworkThroughputService: {reason}; network metrics unavailable this session");
        }

        /// <summary>
        /// One tick's decoded network sample. Any member can be null on its own: the link
        /// speed drops on a disconnected or unreported medium, the rates drop on the
        /// baseline tick, a rejected interval or a counter reset - each independently of
        /// the others.
        /// </summary>
        internal readonly struct Sample
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="Sample"/> struct.
            /// </summary>
            /// <param name="linkMbps">Negotiated link speed in Mbps, or null.</param>
            /// <param name="rxKbps">Receive rate in kbit/s, or null.</param>
            /// <param name="txKbps">Transmit rate in kbit/s, or null.</param>
            internal Sample(int? linkMbps, long? rxKbps, long? txKbps)
            {
                LinkMbps = linkMbps;
                RxKbps = rxKbps;
                TxKbps = txKbps;
            }

            /// <summary>Gets the negotiated link speed in whole Mbps, or null.</summary>
            internal int? LinkMbps { get; }

            /// <summary>Gets the receive throughput in whole kbit/s (0 = idle), or null.</summary>
            internal long? RxKbps { get; }

            /// <summary>Gets the transmit throughput in whole kbit/s (0 = idle), or null.</summary>
            internal long? TxKbps { get; }
        }
    }
}
