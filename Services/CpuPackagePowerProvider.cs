using System.Diagnostics;

#pragma warning disable SA1402, SA1649 // File may only contain a single type - the source discriminator and the energy window exist only to serve the provider below them, and the window is split out precisely so its wrap and priming behaviour is testable without a device.

namespace MetricsPusher.Services
{
    /// <summary>
    /// Which vendor's RAPL registers <see cref="CpuPackagePowerProvider"/> is reading.
    /// The two differ in more than register numbers: Intel exposes a package power
    /// <em>limit</em> and AMD structurally does not, so the discriminator is what tells an
    /// absent limit apart from a failed read.
    /// </summary>
    internal enum CpuPowerSource
    {
        /// <summary>No usable source. The default, which must never read as a working sensor.</summary>
        None,

        /// <summary>Intel RAPL through PawnIO's IntelMSR module.</summary>
        IntelRapl,

        /// <summary>AMD RAPL through PawnIO's AMDFamily17 module.</summary>
        AmdRapl,
    }

    /// <summary>
    /// Two consecutive samples of a free-running RAPL energy accumulator, turned into an
    /// average wattage over the interval between them.
    /// <para>
    /// Split out from the provider for one reason: the two things most likely to be wrong
    /// here - the 32-bit wrap and the fact that a first sample cannot produce a value -
    /// are impossible to exercise against real hardware on demand. The wrap happens
    /// roughly every four minutes under load and never at all at idle, so a test that
    /// waits for one is either slow or useless. Here it is two method calls.
    /// </para>
    /// <para>
    /// <b>The window always advances, even when the sample is rejected.</b> A machine that
    /// resumes from sleep produces one enormous interval, and if that tick left the
    /// baseline untouched the <em>next</em> interval would still span the sleep and be
    /// rejected too - forever, in the pathological case. Advancing through the rejection
    /// costs one skipped reading and recovers on the very next tick.
    /// </para>
    /// </summary>
    internal sealed class RaplEnergyWindow
    {
        private readonly int _energyStatusUnit;

        private uint _lastRawEnergy;
        private long _lastTimestamp;
        private bool _primed;

        /// <summary>
        /// Initializes a new instance of the <see cref="RaplEnergyWindow"/> class.
        /// </summary>
        /// <param name="energyStatusUnit">
        /// The ESU field from the vendor's RAPL units register: each accumulator count is
        /// <c>1 / 2^ESU</c> joules.
        /// </param>
        internal RaplEnergyWindow(int energyStatusUnit)
        {
            _energyStatusUnit = energyStatusUnit;
        }

        /// <summary>
        /// Feeds one accumulator sample and, from the second sample on, reports the
        /// average power since the previous one.
        /// </summary>
        /// <param name="rawEnergy">The low 32 bits of the energy accumulator.</param>
        /// <param name="timestamp">
        /// A <see cref="Stopwatch.GetTimestamp"/> value taken next to the read. Passed in
        /// rather than taken here so the interval is testable, and taken from Stopwatch
        /// rather than assumed to be the nominal 1 s tick because <c>PeriodicTimer</c>
        /// drifts and dividing by an assumed interval turns that drift straight into power
        /// error.
        /// </param>
        /// <param name="watts">The average power, or 0 when this returns false.</param>
        /// <returns>True when a value could be computed for this interval.</returns>
        internal bool TryAdvance(uint rawEnergy, long timestamp, out float watts)
        {
            watts = 0f;

            bool wasPrimed = _primed;
            uint previousEnergy = _lastRawEnergy;
            long previousTimestamp = _lastTimestamp;

            _lastRawEnergy = rawEnergy;
            _lastTimestamp = timestamp;
            _primed = true;

            if (!wasPrimed)
                return false;

            double elapsedSeconds = (timestamp - previousTimestamp) / (double)Stopwatch.Frequency;
            uint delta = CpuPackagePowerProvider.EnergyDelta(previousEnergy, rawEnergy);
            return CpuPackagePowerProvider.TryComputeWatts(delta, _energyStatusUnit, elapsedSeconds, out watts);
        }

        /// <summary>
        /// Drops the baseline, so the next sample primes instead of reporting. Used when
        /// the window is (re)started deliberately and the interval since the last sample
        /// means nothing.
        /// </summary>
        internal void Reset()
        {
            _primed = false;
            _lastRawEnergy = 0;
            _lastTimestamp = 0;
        }
    }

    /// <summary>
    /// CPU package power from the RAPL energy accumulator, on both vendors, at a cost of
    /// one extra IOCTL per tick on the device the temperature provider already opened.
    /// <para>
    /// Neither vendor reports a wattage. Both expose a free-running energy counter, and
    /// power is the delta over the elapsed time: <c>watts = Δcounts × energyUnit / Δs</c>.
    /// Three things about that will produce wrong numbers if missed, and all three are
    /// handled in <see cref="RaplEnergyWindow"/>: the counter is 32 bits and wraps
    /// routinely under load, the first tick cannot produce a value at all, and Δt has to
    /// be measured rather than assumed.
    /// </para>
    /// <para>
    /// <b>The power limit is Intel-only, and its absence on AMD is structural.</b>
    /// AMDFamily17's allow-list has no package power-limit register - on AMD that limit
    /// lives behind the SMU, which is out of scope - so an AMD machine reports watts with
    /// no limit beside them. This mirrors what the wire contract already documents for the
    /// GPU's NVAPI fallback, where <c>watts</c> and <c>limitW</c> are structurally absent:
    /// never render it as a failure.
    /// </para>
    /// <para>
    /// <b>It does not own the <see cref="PawnIoDevice"/> handed to it.</b> It shares one
    /// handle and one loaded module with the temperature provider, because the driver
    /// binds a module to the handle it was loaded through and both sets of registers are
    /// in the same module's allow-list. It owns nothing at all, which is why it is not
    /// <see cref="IDisposable"/>: there is no resource here for a Dispose to release, and
    /// having one would invite it to close a handle it does not own.
    /// </para>
    /// <para>
    /// <b>Not thread-safe</b> - it inherits <see cref="PawnIoDevice"/>'s contract, and the
    /// service above it serializes the whole CPU sensor sweep.
    /// </para>
    /// </summary>
    internal sealed class CpuPackagePowerProvider
    {
        // Both modules expose their MSR read under the same name, with the same arity:
        // exactly one int64 in, one out, checked before the allow-list is consulted.
        private const string ReadMsrFunction = "ioctl_read_msr";

        private const long IntelPowerUnitMsr = 0x606;    // MSR_RAPL_POWER_UNIT
        private const long IntelEnergyStatusMsr = 0x611; // MSR_PKG_ENERGY_STATUS
        private const long IntelPowerLimitMsr = 0x610;   // MSR_PKG_POWER_LIMIT, PL1 in bits 14:0
        private const long IntelPowerInfoMsr = 0x614;    // MSR_PKG_POWER_INFO, TDP in bits 14:0

        private const long AmdPowerUnitMsr = 0xC0010299;    // MSR_PWR_UNIT
        private const long AmdEnergyStatusMsr = 0xC001029B; // MSR_PKG_ENERGY_STAT

        // MSR_RAPL_POWER_UNIT packs three separate units. ESU (bits 12:8) scales the
        // ENERGY registers; PSU (bits 3:0) scales the POWER registers. They are different
        // fields with different values on the same machine - measured on the dev box, ESU
        // is 14 and PSU is 3 - and using one for the other is the defect this provider was
        // written to avoid: the raw 0x614 and 0x610 fields there read 224 and 512, which
        // taken as watts are wrong by 8x on a 28 W part and still pass every plausibility
        // guard below.
        private const int EnergyStatusUnitShift = 8;
        private const uint EnergyStatusUnitMask = 0x1F;
        private const uint PowerStatusUnitMask = 0xF;

        /// <summary>Bits 14:0 of both limit registers hold the power field.</summary>
        private const long PowerLimitFieldMask = 0x7FFF;

        // The elapsed-time window a sample must fall in. Below it, two ticks landed on top
        // of each other and jitter would show up as a power spike; above it, the process
        // was descheduled or the machine slept, and the accumulator delta no longer
        // describes a rate anyone wants. Mirrors the guard LibreHardwareMonitor puts on
        // its own TSC window.
        private const double MinElapsedSeconds = 0.5;
        private const double MaxElapsedSeconds = 2.0;

        /// <summary>Above this, the number came from a misread register, not a CPU.</summary>
        private const double MaxPlausibleWatts = 1000.0;

        private readonly PawnIoDevice _device;
        private readonly long _energyStatusMsr;
        private readonly long _powerUnitMsr;

        private RaplEnergyWindow? _window;
        private bool _readFailureLogged;

        /// <summary>
        /// Initializes a new instance of the <see cref="CpuPackagePowerProvider"/> class
        /// over a device that already has the matching module loaded.
        /// </summary>
        /// <param name="device">
        /// The shared, <b>non-owned</b> PawnIO device. The caller keeps ownership and
        /// disposes it; see the type remarks.
        /// </param>
        /// <param name="source">
        /// Which module is loaded on that device. It selects the register set and decides
        /// whether a power limit exists at all.
        /// </param>
        internal CpuPackagePowerProvider(PawnIoDevice device, CpuPowerSource source)
        {
            _device = device;
            Source = source;

            _powerUnitMsr = source == CpuPowerSource.AmdRapl ? AmdPowerUnitMsr : IntelPowerUnitMsr;
            _energyStatusMsr = source == CpuPowerSource.AmdRapl ? AmdEnergyStatusMsr : IntelEnergyStatusMsr;
        }

        /// <summary>Which vendor's registers this is reading.</summary>
        internal CpuPowerSource Source { get; }

        /// <summary>
        /// The package power limit in watts, or null when there is none. Null on AMD is
        /// <em>structural</em> - the register does not exist behind this module - and must
        /// never be reported as a failure.
        /// </summary>
        internal float? PackagePowerLimitWatts { get; private set; }

        /// <summary>
        /// The part's rated base power (TDP) in watts, Intel-only, or null. Kept separate
        /// from <see cref="PackagePowerLimitWatts"/> because they are different numbers -
        /// 28 W and 64 W respectively on the dev box - and conflating them would misreport
        /// a laptop that is configured well above its rating.
        /// </summary>
        internal float? ThermalDesignPowerWatts { get; private set; }

        /// <summary>
        /// Reads the RAPL units and, on Intel, the limit registers, once. False means this
        /// machine cannot report package power and the caller should drop the provider.
        /// </summary>
        /// <returns>True when the units and the energy accumulator both answered.</returns>
        internal bool Initialize()
        {
            if (Source == CpuPowerSource.None)
                return false;

            if (!TryReadMsr(_powerUnitMsr, out long units))
            {
                LoggingService.Debug($"CpuPackagePowerProvider: RAPL units register 0x{_powerUnitMsr:X} did not answer; no CPU package power on this machine");
                return false;
            }

            int energyStatusUnit = DecodeEnergyStatusUnit(units);
            _window = new RaplEnergyWindow(energyStatusUnit);

            if (Source == CpuPowerSource.IntelRapl)
                ReadIntelLimits(DecodePowerStatusUnit(units));
            else
                LoggingService.Debug("CpuPackagePowerProvider: AMD exposes no package power-limit register through this module; the limit is structurally absent, not missing");

            // Proves the accumulator itself is readable before the caller commits to
            // polling it. The value is discarded: one sample is not a power reading.
            if (!TryReadMsr(_energyStatusMsr, out _))
            {
                LoggingService.Debug($"CpuPackagePowerProvider: energy accumulator 0x{_energyStatusMsr:X} did not answer; no CPU package power on this machine");
                return false;
            }

            LoggingService.Debug($"CpuPackagePowerProvider: {Source}, energy unit 1/2^{energyStatusUnit} J");
            return true;
        }

        /// <summary>
        /// Establishes the first energy sample, so a call roughly a second later can
        /// already report watts. The same reason - and the same place -
        /// <c>SystemMetricsService.PrimeCpuCounter</c> exists: a rate needs two samples.
        /// </summary>
        internal void Prime()
        {
            // Reset first so priming twice, or priming after a long idle, starts a clean
            // window rather than measuring across a gap nobody meant to measure.
            _window?.Reset();
            _ = TryRead(out _);
        }

        /// <summary>
        /// Reads the energy accumulator and reports the average power since the previous
        /// call. False is ordinary: the first call after priming, an interval outside the
        /// accepted window, or a failed IOCTL. Never throws.
        /// </summary>
        /// <param name="watts">The package power, or 0 when this returns false.</param>
        /// <returns>True when <paramref name="watts"/> holds a real reading.</returns>
        internal bool TryRead(out float watts)
        {
            watts = 0f;

            if (_window == null)
                return false;

            if (!TryReadMsr(_energyStatusMsr, out long raw))
            {
                if (!_readFailureLogged)
                {
                    _readFailureLogged = true;
                    LoggingService.Debug($"CpuPackagePowerProvider: energy accumulator 0x{_energyStatusMsr:X} could not be read; further failures are not logged until one succeeds");
                }

                // The window is deliberately NOT reset here. The accumulator is
                // free-running, so a missed tick costs nothing: the next successful pair
                // spans a longer interval, and the interval guard is what decides whether
                // that pair is still meaningful.
                return false;
            }

            _readFailureLogged = false;

            // Bits 31:0 are the accumulator; the timestamp is taken next to the read, not
            // at the top of the tick, so scheduling delay between the two cannot leak into
            // the interval.
            return _window.TryAdvance((uint)raw, Stopwatch.GetTimestamp(), out watts);
        }

        /// <summary>
        /// ESU: bits 12:8 of the RAPL units register. Each accumulator count is
        /// <c>1 / 2^ESU</c> joules - 14 on recent Intel parts, 16 on AMD.
        /// </summary>
        /// <param name="rawPowerUnit">MSR_RAPL_POWER_UNIT / MSR_PWR_UNIT as read.</param>
        /// <returns>The energy status unit exponent.</returns>
        internal static int DecodeEnergyStatusUnit(long rawPowerUnit)
        {
            return (int)((rawPowerUnit >> EnergyStatusUnitShift) & EnergyStatusUnitMask);
        }

        /// <summary>
        /// PSU: bits 3:0 of the RAPL units register, which scales the <em>power</em>
        /// registers. A different field from the ESU above, with a different value on the
        /// same machine, and the one the implementation plan omitted - see this type's
        /// remarks and the tests.
        /// </summary>
        /// <param name="rawPowerUnit">MSR_RAPL_POWER_UNIT as read.</param>
        /// <returns>The power status unit exponent.</returns>
        internal static int DecodePowerStatusUnit(long rawPowerUnit)
        {
            return (int)(rawPowerUnit & PowerStatusUnitMask);
        }

        /// <summary>
        /// Joules per accumulator count for an ESU exponent.
        /// </summary>
        /// <param name="energyStatusUnit">The ESU exponent, 0-31.</param>
        /// <returns>The energy unit in joules, or 0 for an impossible exponent.</returns>
        internal static double EnergyUnitJoules(int energyStatusUnit)
        {
            // ESU is a 5-bit field, so anything else means the register was not read.
            // Returning 0 makes every later wattage 0, which the guard then rejects -
            // preferable to a shift with undefined behaviour.
            if (energyStatusUnit < 0 || energyStatusUnit > 31)
                return 0.0;

            return 1.0 / (1u << energyStatusUnit);
        }

        /// <summary>
        /// Decodes a package power register - <c>MSR_PKG_POWER_INFO</c> (TDP) or
        /// <c>MSR_PKG_POWER_LIMIT</c> (PL1) - into watts.
        /// <para>
        /// The 15-bit field is in RAPL <b>power</b> units, not watts: it must be divided
        /// by <c>2^PSU</c>. Skipping that division is a silent factor of 8 on the dev box
        /// (raw 224 → 28.00 W, raw 512 → 64.00 W with PSU 3), and both raw values sail
        /// through the plausibility guard below, so nothing downstream would catch it.
        /// </para>
        /// </summary>
        /// <param name="rawPowerRegister">The register value as read.</param>
        /// <param name="powerStatusUnit">The PSU exponent from bits 3:0 of the units register.</param>
        /// <param name="watts">The decoded limit, or 0 when this returns false.</param>
        /// <returns>True when the register held a plausible limit.</returns>
        internal static bool TryDecodePowerLimitWatts(long rawPowerRegister, int powerStatusUnit, out float watts)
        {
            watts = 0f;

            // PSU is a 4-bit field; anything else means the units register was not read.
            if (powerStatusUnit < 0 || powerStatusUnit > 15)
                return false;

            // Masked to 15 bits: the rest of both registers carries enable bits, a time
            // window and, on 0x610, the PL2 field.
            uint field = (uint)(rawPowerRegister & PowerLimitFieldMask);
            if (field == 0)
                return false;

            float candidate = field / (float)(1u << powerStatusUnit);
            if (candidate <= 0f || candidate > MaxPlausibleWatts)
                return false;

            watts = candidate;
            return true;
        }

        /// <summary>
        /// The distance a 32-bit accumulator travelled between two samples, wrap included.
        /// <para>
        /// Plain unsigned subtraction <em>is</em> the modular distance, so there is no
        /// branch and no special case: 0xFFFFFF00 to 0x100 is 0x200. The formula usually
        /// written out for this, <c>(0xFFFFFFFF - last) + now</c>, answers 0x1FF - correct
        /// to within one count, which nobody would ever notice, and one count short of
        /// what the arithmetic actually says. A single wrap between two 1 Hz samples is
        /// impossible at any real power level, so there is no ambiguity to resolve either
        /// way.
        /// </para>
        /// </summary>
        /// <param name="last">The previous sample.</param>
        /// <param name="now">The current sample.</param>
        /// <returns>Counts accumulated between them.</returns>
        internal static uint EnergyDelta(uint last, uint now)
        {
            return unchecked(now - last);
        }

        /// <summary>
        /// Turns an accumulator delta and an interval into watts, rejecting anything that
        /// is not a usable measurement. Pure, so the wrap case, the interval window and
        /// the plausibility band are all testable without a device.
        /// </summary>
        /// <param name="energyDelta">Counts accumulated over the interval.</param>
        /// <param name="energyStatusUnit">The ESU exponent.</param>
        /// <param name="elapsedSeconds">The measured interval.</param>
        /// <param name="watts">The average power, or 0 when this returns false.</param>
        /// <returns>True when the interval and the result are both usable.</returns>
        internal static bool TryComputeWatts(uint energyDelta, int energyStatusUnit, double elapsedSeconds, out float watts)
        {
            watts = 0f;

            if (double.IsNaN(elapsedSeconds) || elapsedSeconds < MinElapsedSeconds || elapsedSeconds > MaxElapsedSeconds)
                return false;

            double candidate = (energyDelta * EnergyUnitJoules(energyStatusUnit)) / elapsedSeconds;

            // NaN is tested explicitly rather than relied on falling out of the
            // comparisons: a NaN fails every relational operator, so "candidate <= 0"
            // alone would let it through.
            if (double.IsNaN(candidate) || candidate <= 0.0 || candidate > MaxPlausibleWatts)
                return false;

            watts = (float)candidate;
            return true;
        }

        /// <summary>
        /// Reads the Intel-only limit registers, preferring PL1 - the limit the board
        /// actually enforces - and falling back to the rated TDP when PL1 is unreadable.
        /// Which one was used is logged, so a reader never has to guess whether a limit
        /// came from the board or from the part's rating.
        /// </summary>
        /// <param name="powerStatusUnit">The PSU exponent from the units register.</param>
        private void ReadIntelLimits(int powerStatusUnit)
        {
            if (TryReadMsr(IntelPowerInfoMsr, out long info) &&
                TryDecodePowerLimitWatts(info, powerStatusUnit, out float tdp))
            {
                ThermalDesignPowerWatts = tdp;
            }

            if (TryReadMsr(IntelPowerLimitMsr, out long limit) &&
                TryDecodePowerLimitWatts(limit, powerStatusUnit, out float pl1))
            {
                PackagePowerLimitWatts = pl1;
                LoggingService.Debug($"CpuPackagePowerProvider: PL1 {pl1:F2} W from MSR 0x{IntelPowerLimitMsr:X}, TDP {ThermalDesignPowerWatts?.ToString("F2") ?? "unknown"} W, PSU 1/2^{powerStatusUnit} W");
                return;
            }

            PackagePowerLimitWatts = ThermalDesignPowerWatts;
            LoggingService.Debug($"CpuPackagePowerProvider: MSR 0x{IntelPowerLimitMsr:X} (PL1) unreadable; using the rated TDP {ThermalDesignPowerWatts?.ToString("F2") ?? "unknown"} W as the limit");
        }

        /// <summary>
        /// One MSR read: one int64 in, one int64 out, the arity both modules declare and
        /// check. Stack-allocated spans, so a tick costs one kernel round trip and nothing
        /// on the heap.
        /// </summary>
        /// <param name="msr">The register address.</param>
        /// <param name="value">The value read, or 0 on failure.</param>
        /// <returns>True when the driver ran the read.</returns>
        private bool TryReadMsr(long msr, out long value)
        {
            Span<long> input = stackalloc long[1];
            Span<long> output = stackalloc long[1];
            input[0] = msr;

            if (!_device.TryExecute(ReadMsrFunction, input, output))
            {
                value = 0;
                return false;
            }

            value = output[0];
            return true;
        }
    }
}
