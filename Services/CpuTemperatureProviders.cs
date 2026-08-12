using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

#pragma warning disable SA1402, SA1649 // File may only contain a single type - the three providers and the interface they satisfy are one unit: they are selected between, never used apart, and splitting them into four files would hide that.

namespace MetricsPusher.Services
{
    /// <summary>
    /// Where a CPU temperature came from. Carried from day one even though nothing
    /// consumes it yet, because the three sources are not the same physical quantity: an
    /// Intel package MSR and an AMD Tdie are die temperatures, while an ACPI thermal zone
    /// is whatever board sensor the firmware chose to expose - typically lower, laggier
    /// and on some machines a constant. A future <c>cpuTemp</c> wire field has to be able
    /// to say which of those it is shipping, and adding the discriminator afterwards would
    /// mean a consumer had already assumed.
    /// </summary>
    internal enum CpuTemperatureSource
    {
        /// <summary>No usable source. The default, which must never read as a real sensor.</summary>
        None,

        /// <summary>IA32_PACKAGE_THERM_STATUS through PawnIO's IntelMSR module.</summary>
        IntelPackageMsr,

        /// <summary>THM_TCON_CUR_TMP over SMN through PawnIO's AMDFamily17 module, reported as Tdie.</summary>
        AmdTctlSmn,

        /// <summary>An ACPI thermal zone read through PDH. A board sensor, not the die.</summary>
        AcpiThermalZone,
    }

    /// <summary>
    /// One way of getting a CPU temperature. Implementations are chosen between at startup
    /// by <c>CpuTemperatureService</c> and then polled on the existing 1 Hz push tick, so
    /// the contract is deliberately narrow: never throw, never block, never allocate in
    /// the steady state, and answer false rather than guessing.
    /// </summary>
    internal interface ICpuTemperatureProvider : IDisposable
    {
        /// <summary>Source of the reading, for logging and a future wire field.</summary>
        CpuTemperatureSource Source { get; }

        /// <summary>
        /// Reads once. False means "no value this call" - a transient IOCTL failure, an
        /// invalid-reading bit, or a decode the shared validator rejected - and the caller
        /// simply tries again on the next tick. Never throws.
        /// </summary>
        /// <param name="celsius">The reading, or 0 when this returns false.</param>
        /// <returns>True when <paramref name="celsius"/> holds a real reading.</returns>
        bool TryRead(out float celsius);
    }

    /// <summary>
    /// Intel die temperature: TjMax from IA32_TEMPERATURE_TARGET once at startup, then one
    /// IA32_PACKAGE_THERM_STATUS read per tick, decoded as <c>TjMax - deltaT</c>.
    /// <para>
    /// <b>It does not own the <see cref="PawnIoDevice"/> handed to it.</b>
    /// <c>CpuTemperatureService</c> opens the device, loads the module and disposes both;
    /// this provider and <see cref="CpuPackagePowerProvider"/> share that one handle and
    /// that one loaded module, because the driver binds a module to the handle it was
    /// loaded through and both registers live in the same module's allow-list.
    /// <see cref="Dispose"/> is therefore deliberately a no-op - closing a handle a
    /// sibling is still reading from would turn CPU power off as a side effect of
    /// disposing a temperature provider.
    /// </para>
    /// <para>
    /// <b>Per-core temperatures are deliberately not read.</b> The package register is one
    /// MSR read with no thread-affinity juggling, and this app reports one number; a
    /// per-core sweep would cost an IOCTL and an affinity change per core for a breakdown
    /// nothing consumes. That is where most of the saving over LibreHardwareMonitor comes
    /// from.
    /// </para>
    /// <para>
    /// <b>Not thread-safe</b> - it inherits <see cref="PawnIoDevice"/>'s contract, and the
    /// service above it already serializes the whole CPU sensor sweep.
    /// </para>
    /// </summary>
    internal sealed class IntelMsrTemperatureProvider : ICpuTemperatureProvider
    {
        /// <summary>
        /// TjMax when IA32_TEMPERATURE_TARGET cannot be read at all, matching what
        /// LibreHardwareMonitor falls back to. It is a <em>fallback</em>, not a typical
        /// value: the dev box's Meteor Lake part reports 110, and a wrong TjMax shifts
        /// every later reading by a constant that no downstream check can see.
        /// </summary>
        internal const int DefaultTjMax = 100;

        // IntelMSR 0.2.10's only read entry point. Its declared arity is exactly one int64
        // in and one out, checked by the module before its MSR allow-list is consulted, so
        // the one-element spans below are a hard requirement rather than a convention.
        private const string ReadMsrFunction = "ioctl_read_msr";

        private const long TemperatureTargetMsr = 0x1A2;   // IA32_TEMPERATURE_TARGET
        private const long PackageThermStatusMsr = 0x1B1;  // IA32_PACKAGE_THERM_STATUS
        private const long ThermStatusMsr = 0x19C;         // IA32_THERM_STATUS

        // Bit 31 of the status register: "reading valid". Without it the delta field is
        // not a temperature, and taken anyway it decodes to a perfectly plausible one.
        private const uint ReadingValidBit = 0x80000000;

        // Bits 22:16, the distance below TjMax in whole degrees.
        private const uint DeltaTemperatureMask = 0x007F0000;
        private const int DeltaTemperatureShift = 16;

        // Bits 23:16 of IA32_TEMPERATURE_TARGET.
        private const int TjMaxShift = 16;
        private const uint TjMaxMask = 0xFF;

        // Sanity band for TjMax. Real parts sit between roughly 85 and 110; the band is
        // wide enough to accept anything Intel has shipped and narrow enough to catch a
        // register that read back as 0, as all-ones, or as some other field entirely.
        private const int MinPlausibleTjMax = 60;
        private const int MaxPlausibleTjMax = 130;

        private readonly PawnIoDevice _device;

        private int _tjMax = DefaultTjMax;

        // Which status register this part answers on, decided once in Initialize.
        private long _statusMsr = PackageThermStatusMsr;

        // Edge trigger: a sensor that fails once at 1 Hz fails every second thereafter.
        private bool _readFailureLogged;

        /// <summary>
        /// Initializes a new instance of the <see cref="IntelMsrTemperatureProvider"/>
        /// class over a device that already has <c>IntelMSR.bin</c> loaded.
        /// </summary>
        /// <param name="device">
        /// The shared, <b>non-owned</b> PawnIO device. The caller keeps ownership and is
        /// responsible for disposing it; see the type remarks.
        /// </param>
        internal IntelMsrTemperatureProvider(PawnIoDevice device)
        {
            _device = device;
        }

        /// <inheritdoc/>
        public CpuTemperatureSource Source => CpuTemperatureSource.IntelPackageMsr;

        /// <summary>
        /// Reads TjMax and picks the status register, once. False means neither status
        /// register answered, so this provider cannot produce a reading on this machine
        /// and the caller should move on down the chain rather than keep a provider that
        /// will return false forever.
        /// <para>
        /// A failed TjMax read is <em>not</em> a reason to fail: 100 °C is the documented
        /// fallback and a reading against it is still worth having. A failed status read
        /// is, because there is nothing left to fall back to.
        /// </para>
        /// </summary>
        /// <returns>True when a usable status register was found.</returns>
        internal bool Initialize()
        {
            if (TryReadMsr(TemperatureTargetMsr, out long target) && TryDecodeTjMax(target, out int tjMax))
            {
                _tjMax = tjMax;
            }
            else
            {
                _tjMax = DefaultTjMax;
                LoggingService.Debug($"IntelMsrTemperatureProvider: IA32_TEMPERATURE_TARGET (0x{TemperatureTargetMsr:X}) unreadable or implausible; falling back to TjMax {DefaultTjMax} C");
            }

            // The probe is on the IOCTL succeeding, not on bit 31 being set. Bit 31 can be
            // clear for one sample on a working part, and latching onto the legacy
            // register because of a single odd tick would cost the package reading for the
            // whole session.
            if (TryReadMsr(PackageThermStatusMsr, out _))
            {
                _statusMsr = PackageThermStatusMsr;
                LoggingService.Debug($"IntelMsrTemperatureProvider: TjMax {_tjMax} C, reading IA32_PACKAGE_THERM_STATUS (0x{PackageThermStatusMsr:X})");
                return true;
            }

            // Pre-Nehalem parts have no package register. This branch is effectively
            // unreachable - PawnIO's INF floors at Windows 10 1809, which those CPUs
            // cannot run - and it is kept only so the failure is a documented fallback
            // rather than an unexplained dead end.
            //
            // It deliberately does NOT pin thread affinity, which is what a per-core
            // register would otherwise want. The caller is the shared 1 Hz push tick
            // running on a thread-pool thread, and setting affinity there leaks onto
            // whatever unrelated work the pool hands that thread next. An unpinned core
            // reading is still a CPU temperature; a permanently affinitized pool thread is
            // a bug in code that has nothing to do with temperature.
            if (TryReadMsr(ThermStatusMsr, out _))
            {
                _statusMsr = ThermStatusMsr;
                LoggingService.Debug($"IntelMsrTemperatureProvider: TjMax {_tjMax} C, IA32_PACKAGE_THERM_STATUS unsupported; falling back to IA32_THERM_STATUS (0x{ThermStatusMsr:X}) unpinned");
                return true;
            }

            LoggingService.Debug("IntelMsrTemperatureProvider: neither IA32_PACKAGE_THERM_STATUS nor IA32_THERM_STATUS answered; no Intel die temperature on this machine");
            return false;
        }

        /// <inheritdoc/>
        public bool TryRead(out float celsius)
        {
            celsius = 0f;

            if (!TryReadMsr(_statusMsr, out long raw))
                return NoteReadFailure(readFailed: true, raw: 0);

            if (!TryDecodePackageTemperature(raw, _tjMax, out celsius))
            {
                celsius = 0f;
                return NoteReadFailure(readFailed: false, raw: raw);
            }

            // Ends the failure streak, so a later genuine failure gets its own line.
            _readFailureLogged = false;
            return true;
        }

        /// <summary>
        /// Deliberately a no-op. This provider owns nothing: the device handle, the loaded
        /// module and their lifetimes belong to <c>CpuTemperatureService</c>, and the same
        /// handle is being read by <see cref="CpuPackagePowerProvider"/>. Do not "fix"
        /// this by disposing <c>_device</c>.
        /// </summary>
        public void Dispose()
        {
            // Intentionally empty - see the summary.
        }

        /// <summary>
        /// Decodes TjMax from IA32_TEMPERATURE_TARGET: bits 23:16, rejected outside a
        /// plausible band. Pure, so the band and the shift are testable without a device.
        /// </summary>
        /// <param name="rawTemperatureTarget">The register value as the driver returned it.</param>
        /// <param name="tjMax">The decoded TjMax in °C, or 0 when this returns false.</param>
        /// <returns>True when the register held a plausible TjMax.</returns>
        internal static bool TryDecodeTjMax(long rawTemperatureTarget, out int tjMax)
        {
            tjMax = 0;

            int candidate = (int)((rawTemperatureTarget >> TjMaxShift) & TjMaxMask);
            if (candidate < MinPlausibleTjMax || candidate > MaxPlausibleTjMax)
                return false;

            tjMax = candidate;
            return true;
        }

        /// <summary>
        /// Decodes a package (or legacy core) thermal-status register against a known
        /// TjMax. Pure, and the half of this provider that a live device cannot check: a
        /// wrong mask does not fail the IOCTL, it returns a temperature in the right
        /// neighbourhood.
        /// </summary>
        /// <param name="rawThermStatus">The register value as the driver returned it.</param>
        /// <param name="tjMax">TjMax in °C.</param>
        /// <param name="celsius">The reading, or 0 when this returns false.</param>
        /// <returns>True when the register carried a valid, in-range reading.</returns>
        internal static bool TryDecodePackageTemperature(long rawThermStatus, int tjMax, out float celsius)
        {
            celsius = 0f;

            // Narrowed to 32 bits first: the driver returns an int64 and only EAX carries
            // the reading, so stray upper bits must not be mistaken for the valid bit.
            uint eax = (uint)rawThermStatus;
            if ((eax & ReadingValidBit) == 0)
                return false;

            int deltaT = (int)((eax & DeltaTemperatureMask) >> DeltaTemperatureShift);
            float candidate = tjMax - deltaT;

            // Constants.IsValidTemperature is the single validator this feature shares
            // with the GPU temperature already on the wire. A second band here would be a
            // second thing to keep in step with the wire contract.
            if (!Constants.IsValidTemperature(candidate))
                return false;

            celsius = candidate;
            return true;
        }

        /// <summary>
        /// One MSR read: one int64 in, one int64 out, which is exactly the arity
        /// IntelMSR 0.2.10 declares and checks. The spans are stack-allocated, so a tick
        /// costs one kernel round trip and nothing on the heap.
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

        /// <summary>
        /// Edge-triggered diagnostics: one line per failure streak. The message is built
        /// <em>inside</em> the guard so a permanently broken sensor allocates nothing
        /// after its first line - at 1 Hz, formatting outside the guard would be an
        /// allocation per second forever in a process with concurrent GC disabled.
        /// </summary>
        /// <param name="readFailed">True when the IOCTL failed, false when the decode rejected it.</param>
        /// <param name="raw">The register value, for the decode-rejected case.</param>
        /// <returns>Always false, so call sites can <c>return</c> this directly.</returns>
        private bool NoteReadFailure(bool readFailed, long raw)
        {
            if (!_readFailureLogged)
            {
                _readFailureLogged = true;
                LoggingService.Debug(readFailed
                    ? $"IntelMsrTemperatureProvider: MSR 0x{_statusMsr:X} could not be read; further failures are not logged until one succeeds"
                    : $"IntelMsrTemperatureProvider: MSR 0x{_statusMsr:X} returned 0x{raw:X16}, which is not a valid reading against TjMax {_tjMax}; further failures are not logged until one succeeds");
            }

            return false;
        }
    }

    /// <summary>
    /// AMD die temperature: one SMN read of <c>THM_TCON_CUR_TMP</c> per tick, decoded to
    /// Tctl and then corrected to Tdie by a per-SKU offset. Covers families 0x17-0x1A
    /// (Zen 1 through Zen 5); the module's own <c>main()</c> rejects anything else, so
    /// there is no family table on this side.
    /// <para>
    /// <b>UNVERIFIED AGAINST HARDWARE.</b> No AMD machine was available while this was
    /// written. The decode below follows the published register layout and
    /// LibreHardwareMonitor's <c>Amd17Cpu</c>, and every part of it that can be is a pure
    /// function with tests - but the IOCTL path, the module load and the mutex have never
    /// run against real silicon. Treat a first AMD run as a bring-up, not a regression.
    /// </para>
    /// <para>
    /// <b>It does not own the <see cref="PawnIoDevice"/> handed to it</b> - see
    /// <see cref="IntelMsrTemperatureProvider"/>'s remarks. It <em>does</em> own the
    /// <c>Global\Access_PCI</c> mutex, which is what <see cref="Dispose"/> releases.
    /// </para>
    /// <para>
    /// <b>Per-CCD temperatures are deliberately not read.</b> They live at SMN 0x59954 on
    /// most parts and 0x59B08 on models 0x61 (Raphael) and 0x44 (Granite Ridge), stride 4,
    /// up to eight CCDs - eight extra IOCTLs per poll for a per-die breakdown this app has
    /// no field for. The addresses are recorded here so a future change knows where to
    /// look; the reads stay out.
    /// </para>
    /// </summary>
    internal sealed class AmdSmnTemperatureProvider : ICpuTemperatureProvider
    {
        // AMDFamily17 0.2.10's SMN read entry point, confirmed from the module source at
        // tag 0.2.10: one int64 in (the offset), one int64 out, both sizes checked.
        private const string ReadSmnFunction = "ioctl_read_smn";

        /// <summary>THM_TCON_CUR_TMP on the data fabric.</summary>
        private const long CurrentTemperatureOffset = 0x00059800;

        // The module's own doc comment says to hold \BaseNamedObjects\Access_PCI before
        // calling it, because the read writes an index to PCI config 0x60 and reads data
        // back from 0x64 on device 0:0.0 - a shared index/data pair that every other
        // monitoring tool on the machine uses too. Global\ maps to \BaseNamedObjects\.
        private const string PciMutexName = @"Global\Access_PCI";

        // Short on purpose: this runs on the 1 Hz push tick, and a missed temperature
        // sample is worth far less than a stalled datagram. On timeout the tick is skipped.
        private const int PciMutexTimeoutMs = 10;

        private const uint RangeSelectBit = 0x80000;   // RANGE_SEL
        private const uint TjSelectMask = 0x30000;     // TJ_SEL, both bits
        private const int TemperatureCodeShift = 21;   // bits 31:21
        private const uint MilliDegreesPerCode = 125;  // each code step is 0.125 C
        private const float RangeCorrectionCelsius = 49f;

        // Tdie offsets for the first-generation parts whose Tctl is deliberately inflated
        // for fan control. Zen 2 and later carry no offset, so Tctl is Tdie.
        private const float FirstGenerationTdieOffset = -20f;
        private const float ThreadripperTdieOffset = -27f;
        private const float SecondGenerationTdieOffset = -10f;

        private readonly PawnIoDevice _device;
        private readonly float _tdieOffset;

        private Mutex? _pciMutex;
        private bool _readFailureLogged;
        private bool _mutexTimeoutLogged;

        /// <summary>
        /// Initializes a new instance of the <see cref="AmdSmnTemperatureProvider"/> class
        /// over a device that already has <c>AMDFamily17.bin</c> loaded.
        /// </summary>
        /// <param name="device">
        /// The shared, <b>non-owned</b> PawnIO device. The caller keeps ownership and
        /// disposes it.
        /// </param>
        /// <param name="cpuName">
        /// The CPU name <c>SystemMetricsService</c> already caches, used once to pick the
        /// Tdie offset. Passed in rather than re-read so this does not open the registry a
        /// second time for a string the app already has.
        /// </param>
        internal AmdSmnTemperatureProvider(PawnIoDevice device, string? cpuName)
        {
            _device = device;

            // Resolved once: the offset is a property of the installed CPU, and matching
            // a name per tick would allocate for an answer that cannot change.
            _tdieOffset = TdieOffset(cpuName);
        }

        /// <inheritdoc/>
        public CpuTemperatureSource Source => CpuTemperatureSource.AmdTctlSmn;

        /// <summary>
        /// Creates the PCI mutex and proves the SMN read answers, once. False means this
        /// machine does not produce a reading through this path.
        /// </summary>
        /// <returns>True when a first SMN read succeeded.</returns>
        internal bool Initialize()
        {
            _pciMutex = TryCreatePciMutex();

            if (!TryReadTemperatureRegister(out long raw))
            {
                LoggingService.Debug($"AmdSmnTemperatureProvider: SMN 0x{CurrentTemperatureOffset:X} did not answer; no AMD die temperature on this machine");
                return false;
            }

            LoggingService.Debug($"AmdSmnTemperatureProvider: SMN 0x{CurrentTemperatureOffset:X} = 0x{(uint)raw:X8}, Tdie offset {_tdieOffset} C");
            return true;
        }

        /// <inheritdoc/>
        public bool TryRead(out float celsius)
        {
            celsius = 0f;

            if (!TryReadTemperatureRegister(out long raw))
                return NoteReadFailure(readFailed: true, raw: 0);

            if (!TryDecodeTdieWithOffset(raw, _tdieOffset, out celsius))
            {
                celsius = 0f;
                return NoteReadFailure(readFailed: false, raw: raw);
            }

            _readFailureLogged = false;
            return true;
        }

        /// <summary>
        /// Releases the PCI mutex, which is the only thing this provider owns. The
        /// <see cref="PawnIoDevice"/> is <b>not</b> disposed here: it is shared with
        /// <see cref="CpuPackagePowerProvider"/> and owned by
        /// <c>CpuTemperatureService</c>.
        /// </summary>
        public void Dispose()
        {
            _pciMutex?.Dispose();
            _pciMutex = null;
        }

        /// <summary>
        /// Decodes Tctl from THM_TCON_CUR_TMP. Pure. The register carries an 11-bit code
        /// at bits 31:21 in steps of 0.125 °C, and two independent signals - RANGE_SEL, or
        /// both TJ_SEL bits - each mean the same 49 °C offset range.
        /// </summary>
        /// <param name="rawTemperature">The register value as the driver returned it.</param>
        /// <returns>Tctl in °C, uncorrected and unvalidated.</returns>
        internal static float DecodeTctl(long rawTemperature)
        {
            uint value = (uint)rawTemperature;
            float tctl = ((value >> TemperatureCodeShift) * MilliDegreesPerCode) / 1000f;

            // One correction, not two: the flags describe a single range, and a decode
            // that subtracted per signal would read 49 C low on any part that sets both.
            if ((value & RangeSelectBit) != 0 || (value & TjSelectMask) == TjSelectMask)
                tctl -= RangeCorrectionCelsius;

            return tctl;
        }

        /// <summary>
        /// The Tctl-to-Tdie offset for a CPU, matched against the name the app already
        /// caches. First-generation Ryzen and Threadripper parts report a deliberately
        /// inflated Tctl so fan curves ramp earlier; Tdie is the physical die temperature
        /// and is what this app reports. Zen 2 and later have no offset.
        /// <para>
        /// The non-X and later SKUs are in the tests precisely because a looser match -
        /// "1600" instead of "1600X" - would silently subtract 20 °C from parts that never
        /// needed it.
        /// </para>
        /// </summary>
        /// <param name="cpuName">The CPU name, or null.</param>
        /// <returns>The offset in °C, 0 when the part needs none.</returns>
        internal static float TdieOffset(string? cpuName)
        {
            if (string.IsNullOrEmpty(cpuName))
                return 0f;

            if (cpuName.Contains("Threadripper", StringComparison.OrdinalIgnoreCase))
                return IsFirstOrSecondGenerationModel(cpuName) ? ThreadripperTdieOffset : 0f;

            if (cpuName.Contains("1600X", StringComparison.OrdinalIgnoreCase) ||
                cpuName.Contains("1700X", StringComparison.OrdinalIgnoreCase) ||
                cpuName.Contains("1800X", StringComparison.OrdinalIgnoreCase))
            {
                return FirstGenerationTdieOffset;
            }

            if (cpuName.Contains("2700X", StringComparison.OrdinalIgnoreCase))
                return SecondGenerationTdieOffset;

            return 0f;
        }

        /// <summary>
        /// Decodes Tdie directly from a register value and a CPU name. Pure, and the entry
        /// point the tests use - the provider itself goes through
        /// <see cref="TryDecodeTdieWithOffset"/> with an offset resolved once at
        /// construction.
        /// </summary>
        /// <param name="rawTemperature">The register value as the driver returned it.</param>
        /// <param name="cpuName">The CPU name, or null.</param>
        /// <param name="celsius">Tdie, or 0 when this returns false.</param>
        /// <returns>True when the decoded value passed the shared validator.</returns>
        internal static bool TryDecodeTdie(long rawTemperature, string? cpuName, out float celsius)
        {
            return TryDecodeTdieWithOffset(rawTemperature, TdieOffset(cpuName), out celsius);
        }

        /// <summary>
        /// Decodes Tdie from a register value and an already-resolved offset. Pure.
        /// </summary>
        /// <param name="rawTemperature">The register value as the driver returned it.</param>
        /// <param name="tdieOffset">The Tctl-to-Tdie offset in °C.</param>
        /// <param name="celsius">Tdie, or 0 when this returns false.</param>
        /// <returns>True when the decoded value passed the shared validator.</returns>
        internal static bool TryDecodeTdieWithOffset(long rawTemperature, float tdieOffset, out float celsius)
        {
            celsius = 0f;

            float candidate = DecodeTctl(rawTemperature) + tdieOffset;

            // The same validator the Intel path and the GPU temperature use. It is also
            // the backstop for a garbage SMN read - the PCI index/data race the mutex
            // exists to avoid produces values well outside this band.
            if (!Constants.IsValidTemperature(candidate))
                return false;

            celsius = candidate;
            return true;
        }

        /// <summary>
        /// True when the first standalone four-digit model number in the name starts with
        /// 19 or 29, i.e. a first- or second-generation Threadripper (1900X/1920X/1950X,
        /// 2920X/2950X/2970WX/2990WX). Later parts - 3990X, 5995WX - fall through.
        /// </summary>
        /// <param name="cpuName">The CPU name.</param>
        /// <returns>True for a 19xx or 29xx model.</returns>
        private static bool IsFirstOrSecondGenerationModel(string cpuName)
        {
            for (int i = 0; i + 4 <= cpuName.Length; i++)
            {
                // Anchored on the left so a longer number cannot be entered mid-way and
                // read as a model.
                if (i > 0 && char.IsAsciiDigit(cpuName[i - 1]))
                    continue;

                if (!char.IsAsciiDigit(cpuName[i]) || !char.IsAsciiDigit(cpuName[i + 1]) ||
                    !char.IsAsciiDigit(cpuName[i + 2]) || !char.IsAsciiDigit(cpuName[i + 3]))
                {
                    continue;
                }

                return (cpuName[i] == '1' || cpuName[i] == '2') && cpuName[i + 1] == '9';
            }

            return false;
        }

        /// <summary>
        /// Creates (or opens) <c>Global\Access_PCI</c> with a World-FullControl DACL.
        /// <para>
        /// The permissive DACL is the point, not an oversight: this process is elevated
        /// and the tools it shares the PCI index/data pair with - HWiNFO, an unelevated
        /// LibreHardwareMonitor - are typically not. A default DACL created by an elevated
        /// process would lock them out of the very mutex that exists to keep them from
        /// colliding with us, which is strictly worse than not taking one at all.
        /// </para>
        /// <para>
        /// A failure here is not fatal. The mutex is a courtesy against a race whose
        /// symptom - a wildly out-of-band reading - the validator already catches, so the
        /// provider carries on without it and says so once.
        /// </para>
        /// </summary>
        /// <returns>The mutex, or null when it could not be created or opened.</returns>
        private static Mutex? TryCreatePciMutex()
        {
            try
            {
                var security = new MutexSecurity();
                security.AddAccessRule(new MutexAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    MutexRights.FullControl,
                    AccessControlType.Allow));

                return MutexAcl.Create(false, PciMutexName, out _, security);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"AmdSmnTemperatureProvider: could not create {PciMutexName} ({ex.Message}); SMN reads continue unserialized, which risks a garbage reading if another monitoring tool touches the PCI index/data pair at the same moment");
                return null;
            }
        }

        /// <summary>
        /// One SMN read, serialized against other PCI clients when the mutex exists. The
        /// wait, the read and the release all happen on the caller's thread with no await
        /// between them, which is what makes the mutex's thread affinity safe here.
        /// </summary>
        /// <param name="value">The register value, or 0 on failure.</param>
        /// <returns>True when the read completed.</returns>
        private bool TryReadTemperatureRegister(out long value)
        {
            value = 0;

            bool holdsMutex = false;

            try
            {
                holdsMutex = TryAcquirePciMutex();

                // Skip the tick rather than read unserialized: if the mutex exists, some
                // other tool is holding it, which means it is mid-transaction on the
                // index/data pair right now.
                if (_pciMutex != null && !holdsMutex)
                {
                    if (!_mutexTimeoutLogged)
                    {
                        _mutexTimeoutLogged = true;
                        LoggingService.Debug($"AmdSmnTemperatureProvider: {PciMutexName} was busy for {PciMutexTimeoutMs} ms; skipping the tick. Further timeouts are not logged until one succeeds");
                    }

                    return false;
                }

                _mutexTimeoutLogged = false;
                return TryReadSmn(CurrentTemperatureOffset, out value);
            }
            finally
            {
                if (holdsMutex)
                    ReleasePciMutex();
            }
        }

        /// <summary>
        /// One SMN read: one int64 in, one int64 out, the arity AMDFamily17 0.2.10
        /// declares. Stack-allocated spans, so the tick costs one kernel round trip and
        /// nothing on the heap.
        /// </summary>
        /// <param name="offset">The SMN offset.</param>
        /// <param name="value">The value read, or 0 on failure.</param>
        /// <returns>True when the driver ran the read.</returns>
        private bool TryReadSmn(long offset, out long value)
        {
            Span<long> input = stackalloc long[1];
            Span<long> output = stackalloc long[1];
            input[0] = offset;

            if (!_device.TryExecute(ReadSmnFunction, input, output))
            {
                value = 0;
                return false;
            }

            value = output[0];
            return true;
        }

        /// <summary>
        /// Waits briefly for the PCI mutex.
        /// </summary>
        /// <returns>True when this thread now owns it and must release it.</returns>
        private bool TryAcquirePciMutex()
        {
            if (_pciMutex == null)
                return false;

            try
            {
                return _pciMutex.WaitOne(PciMutexTimeoutMs, false);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner died holding it. The wait SUCCEEDED - this thread now
                // owns the mutex and must release it - so this is true, not false. Nothing
                // is corrupted by an abandoned PCI mutex: it guards a hardware
                // index/data pair, not shared memory.
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"AmdSmnTemperatureProvider: waiting on {PciMutexName} failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Releases the PCI mutex, tolerating the case where this thread does not own it.
        /// </summary>
        private void ReleasePciMutex()
        {
            try
            {
                _pciMutex?.ReleaseMutex();
            }
            catch (ApplicationException ex)
            {
                // ApplicationException is what Mutex throws for "not owned by this
                // thread". It cannot happen on the path above, and swallowing it is still
                // right: a release that failed has nothing left to clean up.
                LoggingService.Debug($"AmdSmnTemperatureProvider: releasing {PciMutexName} failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Edge-triggered diagnostics, formatted inside the guard so a broken sensor
        /// allocates nothing after its first line.
        /// </summary>
        /// <param name="readFailed">True when the IOCTL failed, false when the decode rejected it.</param>
        /// <param name="raw">The register value, for the decode-rejected case.</param>
        /// <returns>Always false.</returns>
        private bool NoteReadFailure(bool readFailed, long raw)
        {
            if (!_readFailureLogged)
            {
                _readFailureLogged = true;
                LoggingService.Debug(readFailed
                    ? $"AmdSmnTemperatureProvider: SMN 0x{CurrentTemperatureOffset:X} could not be read; further failures are not logged until one succeeds"
                    : $"AmdSmnTemperatureProvider: SMN 0x{CurrentTemperatureOffset:X} returned 0x{(uint)raw:X8}, which decodes outside the valid band; further failures are not logged until one succeeds");
            }

            return false;
        }
    }

    /// <summary>
    /// The degraded fallback: an ACPI thermal zone read through PDH, for machines with no
    /// PawnIO driver or a CPU neither module supports.
    /// <para>
    /// <b>PDH, not WMI, and that is a design decision rather than a preference.</b> It
    /// reads the same counter set the <c>Win32_PerfFormattedData_Counters_ThermalZoneInformation</c>
    /// class projects, but through <c>pdh.dll</c> - which this app already P/Invokes for
    /// CPU usage and which is already pinned in <c>SystemLibraryResolver</c>. The WMI
    /// route would mean a NuGet package, a provider-host spin-up and a round trip for the
    /// same number, and <c>root\WMI</c>'s <c>MSAcpi_ThermalZoneTemperature</c> is
    /// access-denied unelevated anyway.
    /// </para>
    /// <para>
    /// <b>Know what this number is.</b> It is a board/platform sensor the firmware chose
    /// to expose, not the die: expect it to read low and lag under load. Many desktops
    /// expose no <c>\_TZ</c> object at all and VMs generally expose nothing, in which case
    /// this provider reports nothing - the expected outcome, not a fault. Some firmware
    /// reports a plausible constant that never moves, and there is no programmatic way to
    /// tell that from a genuinely stable idle temperature; <see cref="Source"/> exists so
    /// a consumer at least knows which kind of sensor it is looking at.
    /// </para>
    /// </summary>
    internal sealed class ThermalZonePdhProvider : ICpuTemperatureProvider
    {
        #region Native PDH entry points

        // pdh.dll is already pinned to absolute System32 by SystemLibraryResolver and
        // CA5392 is satisfied by the assembly-level DefaultDllImportSearchPaths in
        // Program.cs, so this adds no new native surface. The declarations are duplicated
        // from SystemMetricsService rather than shared because that type's are private and
        // a P/Invoke signature is cheaper to repeat than a service dependency is to add.
        private const string PdhDll = "pdh.dll";

        [DllImport(PdhDll, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint PdhOpenQueryW(string? szDataSource, nuint dwUserData, out IntPtr phQuery);

        [DllImport(PdhDll, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, nuint dwUserData, out IntPtr phCounter);

        [DllImport(PdhDll, ExactSpelling = true)]
        private static extern uint PdhCollectQueryData(IntPtr hQuery);

        [DllImport(PdhDll, ExactSpelling = true)]
        private static extern uint PdhCloseQuery(IntPtr hQuery);

        // The wildcard instance form: one call yields every thermal zone. lpdwBufferSize
        // is in/out - zero on input asks for the required size and answers PDH_MORE_DATA.
        //
        // It is declared twice, against the same export, because the documented protocol
        // needs the ItemBuffer pointer to be NULL on the sizing call and a pinned array on
        // the real one. Two signatures beat one nullable-array parameter: the array
        // marshaler pins a blittable byte[] instead of copying it, which is what keeps the
        // per-tick cost at zero, and a zero-length array would not marshal as NULL.
        [DllImport(PdhDll, EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint PdhGetFormattedCounterArraySizeW(
            IntPtr hCounter,
            uint dwFormat,
            ref uint lpdwBufferSize,
            out uint lpdwItemCount,
            IntPtr itemBuffer);

        [DllImport(PdhDll, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint PdhGetFormattedCounterArrayW(
            IntPtr hCounter,
            uint dwFormat,
            ref uint lpdwBufferSize,
            out uint lpdwItemCount,
            [Out] byte[] itemBuffer);

        /// <summary>
        /// PDH_FMT_COUNTERVALUE_ITEM_W as x64 lays it out: an <c>LPWSTR</c> instance name,
        /// then a PDH_FMT_COUNTERVALUE whose DWORD status is followed by an 8-byte-aligned
        /// union. Hence the explicit offsets, matching the idiom
        /// <c>SystemMetricsService</c> uses for the scalar form.
        /// <para>
        /// <c>SzName</c> is declared but never dereferenced, and that is deliberate. PDH
        /// appends the instance strings to the end of the same buffer and writes pointers
        /// into it, so those pointers are only valid while the buffer is pinned - which,
        /// for a managed array, is only for the duration of the call. Reading the field is
        /// safe; following it after the call returns is a use-after-move. Nothing needs
        /// the names anyway: this provider takes the maximum across zones rather than
        /// matching one by name, which also side-steps the fact that PDH reports the
        /// instance lower-cased (<c>\_tz.thrm</c>) while the counter path advertises it as
        /// <c>\_TZ.THRM</c>.
        /// </para>
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct PDH_FMT_COUNTERVALUE_ITEM
        {
            [FieldOffset(0)]
            public IntPtr SzName;
            [FieldOffset(8)]
            public uint CStatus;
            [FieldOffset(16)]
            public double DoubleValue;
        }

        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
        private const uint PDH_CSTATUS_NEW_DATA = 0x00000001;
        private const uint PDH_MORE_DATA = 0x800007D2;

        #endregion

        /// <summary>
        /// The English counter path, wildcarded across instances so a machine with several
        /// zones is handled by one call. <c>PdhAddEnglishCounterW</c> makes it independent
        /// of the OS display language, exactly as the CPU-usage counter does.
        /// </summary>
        internal const string ThermalZoneCounterPath = @"\Thermal Zone Information(*)\High Precision Temperature";

        private const double DeciKelvinPerKelvin = 10.0;
        private const double KelvinAtZeroCelsius = 273.15;

        // sizeof the item struct rather than a hand-copied 24: derived from the layout
        // above so the stride cannot drift away from the offsets it walks.
        private static readonly int ItemSizeBytes = Unsafe.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();

        private State _state;
        private IntPtr _query;
        private IntPtr _counter;

        // Sized once against the real instance count and reused forever after; an empty
        // array means "size it on the next read".
        private byte[] _itemBuffer = Array.Empty<byte>();

        private bool _readFailureLogged;

        /// <summary>
        /// The same four-state discipline <c>SystemMetricsService.PdhState</c> uses, for
        /// the same reason: creating the query fails structurally, collecting the first
        /// sample fails temporarily, and the two must not be treated alike.
        /// </summary>
        private enum State
        {
            NotInitialized, // Query and counter not created yet
            Priming,        // Handles ready, no sample collected yet
            Ready,          // Sampling; values can be formatted
            Failed,         // Structurally unusable on this machine - never retried
        }

        /// <inheritdoc/>
        public CpuTemperatureSource Source => CpuTemperatureSource.AcpiThermalZone;

        /// <summary>
        /// Creates the query and proves the counter yields a usable value, once.
        /// <para>
        /// It insists on an actual reading rather than settling for a counter that was
        /// added successfully, because a machine with no <c>\_TZ</c> object can still
        /// produce a live wildcard counter with zero instances. Selecting this provider on
        /// that basis would mean reporting nothing forever while claiming a source, which
        /// is worse than reporting no source at all.
        /// </para>
        /// </summary>
        /// <returns>True when a thermal zone answered with an in-range temperature.</returns>
        internal bool Initialize()
        {
            CreateCounter();
            if (_state == State.Failed)
                return false;

            // First call moves Priming to Ready; the second is the one that can format.
            // Back-to-back is fine here - this is an instantaneous counter, not a rate.
            _ = TryRead(out _);

            if (!TryRead(out float celsius))
            {
                _state = State.Failed;
                LoggingService.Debug($"ThermalZonePdhProvider: {ThermalZoneCounterPath} produced no usable value; this machine exposes no ACPI thermal zone");
                return false;
            }

            LoggingService.Debug($"ThermalZonePdhProvider: {ThermalZoneCounterPath} reports {celsius:F1} C - a board sensor, not the die");
            return true;
        }

        /// <inheritdoc/>
        public bool TryRead(out float celsius)
        {
            celsius = 0f;

            try
            {
                if (_state == State.Failed)
                    return false;

                if (_state == State.NotInitialized)
                    CreateCounter();

                if (_state == State.Priming)
                {
                    // The baseline collect can legitimately fail right after logon while
                    // perflib is still coming up, so it is retried rather than latched.
                    uint baselineStatus = PdhCollectQueryData(_query);
                    if (baselineStatus != 0)
                        return NoValueThisTick(baselineStatus, collecting: true);

                    _state = State.Ready;
                    return false;
                }

                if (_state != State.Ready)
                    return false;

                uint status = PdhCollectQueryData(_query);
                if (status != 0)
                    return NoValueThisTick(status, collecting: true);

                return TryReadHottestZone(out celsius);
            }
            catch (Exception ex)
            {
                // DllNotFoundException and friends: PDH is unusable on this machine.
                _state = State.Failed;
                LoggingService.Debug($"ThermalZonePdhProvider: thermal zone disabled: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Closes the PDH query, which is the only thing this provider owns.
        /// <para>
        /// Deliberately different from <c>SystemMetricsService</c>, which never closes its
        /// query: that one is static and lives for the process, so closing it could only
        /// happen on a shutdown path where not closing is the safer bet. This provider has
        /// an owner and a lifetime - it can be constructed, rejected by
        /// <see cref="Initialize"/> and thrown away - so leaking a query per probe would
        /// be a real leak rather than a deliberate non-cleanup.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (_query == IntPtr.Zero)
                return;

            try
            {
                // Closes every counter handle on the query too, so _counter needs no
                // separate call.
                _ = PdhCloseQuery(_query);
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"ThermalZonePdhProvider: PdhCloseQuery failed: {ex.Message}");
            }

            _query = IntPtr.Zero;
            _counter = IntPtr.Zero;
            _state = State.Failed;
        }

        /// <summary>
        /// Converts a thermal-zone counter value to °C. The counter's unit is
        /// <b>deci-Kelvin</b>, which is the one thing about it that is easy to get wrong:
        /// treated as Kelvin it reads about 61 °C low, and treated as deci-Celsius it
        /// reads about 273 °C high - the second is caught by the validator, the first is
        /// not.
        /// </summary>
        /// <param name="deciKelvin">The formatted counter value.</param>
        /// <param name="celsius">The temperature, or 0 when this returns false.</param>
        /// <returns>True when the value is a real, in-range reading.</returns>
        internal static bool TryDecodeDeciKelvin(double deciKelvin, out float celsius)
        {
            celsius = 0f;

            float candidate = (float)((deciKelvin / DeciKelvinPerKelvin) - KelvinAtZeroCelsius);

            // The shared validator does the NaN, infinity and range work in one place -
            // including the 0 K case an unpopulated zone reports, which decodes to
            // -273.15 C.
            if (!Constants.IsValidTemperature(candidate))
                return false;

            celsius = candidate;
            return true;
        }

        /// <summary>
        /// Opens the query and adds the wildcard counter. Failures here are structural (no
        /// PDH, no thermal-zone counter set on this machine) rather than timing-related,
        /// so they latch for the session.
        /// </summary>
        private void CreateCounter()
        {
            uint status = PdhOpenQueryW(null, 0, out IntPtr query);
            if (status != 0)
            {
                _state = State.Failed;
                LoggingService.Debug($"ThermalZonePdhProvider: PdhOpenQuery failed with 0x{status:X8}; thermal zone disabled");
                return;
            }

            status = PdhAddEnglishCounterW(query, ThermalZoneCounterPath, 0, out IntPtr counter);
            if (status != 0)
            {
                _state = State.Failed;

                // Expected on most desktops and in most VMs: no \_TZ object means no
                // counter to add. Debug, not an error.
                LoggingService.Debug($"ThermalZonePdhProvider: PdhAddEnglishCounter failed with 0x{status:X8}; this machine exposes no thermal zone counter");
                _ = PdhCloseQuery(query);
                return;
            }

            _query = query;
            _counter = counter;
            _state = State.Priming;
        }

        /// <summary>
        /// Formats every zone and returns the hottest.
        /// <para>
        /// The maximum, not the first or the average: a machine with several zones is
        /// reporting several different places on the board, and the hottest is both the
        /// most conservative answer and the one most likely to be near the package. It
        /// also makes an idle zone that never moves harmless as long as one live zone
        /// exists.
        /// </para>
        /// </summary>
        /// <param name="celsius">The hottest valid reading, or 0 when there is none.</param>
        /// <returns>True when at least one zone produced a valid reading.</returns>
        private bool TryReadHottestZone(out float celsius)
        {
            celsius = 0f;

            if (!TryFillItemBuffer(out uint itemCount))
                return false;

            bool any = false;
            float hottest = 0f;

            for (uint i = 0; i < itemCount; i++)
            {
                int offset = (int)i * ItemSizeBytes;
                if (offset + ItemSizeBytes > _itemBuffer.Length)
                    break;

                // A struct read out of the reused byte[]: no per-tick array, no
                // Marshal.PtrToStructure, nothing on the heap.
                PDH_FMT_COUNTERVALUE_ITEM item = MemoryMarshal.Read<PDH_FMT_COUNTERVALUE_ITEM>(_itemBuffer.AsSpan(offset));

                // A successful call can still carry unusable per-instance data; CStatus is
                // authoritative, exactly as it is for the scalar counter.
                if (item.CStatus != PDH_CSTATUS_VALID_DATA && item.CStatus != PDH_CSTATUS_NEW_DATA)
                    continue;

                if (!TryDecodeDeciKelvin(item.DoubleValue, out float zone))
                    continue;

                if (!any || zone > hottest)
                {
                    hottest = zone;
                    any = true;
                }
            }

            if (!any)
                return NoValueThisTick(0, collecting: false);

            _readFailureLogged = false;
            celsius = hottest;
            return true;
        }

        /// <summary>
        /// Fills the reused item buffer, sizing it on the first call and whenever the
        /// instance set grows.
        /// <para>
        /// PDH's documented protocol is a sizing call with a zero size and a null buffer,
        /// then the real one. The docs also say that when a non-zero size is too small the
        /// returned size cannot be relied on - so that case drops the buffer entirely and
        /// re-sizes from scratch on the next tick rather than trusting it.
        /// </para>
        /// </summary>
        /// <param name="itemCount">How many items were written.</param>
        /// <returns>True when the buffer holds <paramref name="itemCount"/> items.</returns>
        private bool TryFillItemBuffer(out uint itemCount)
        {
            itemCount = 0;

            if (_itemBuffer.Length == 0)
            {
                uint requiredBytes = 0;
                uint sizingStatus = PdhGetFormattedCounterArraySizeW(_counter, PDH_FMT_DOUBLE, ref requiredBytes, out _, IntPtr.Zero);

                if (sizingStatus != PDH_MORE_DATA || requiredBytes == 0)
                    return NoValueThisTick(sizingStatus, collecting: false);

                // The one allocation on this path, and it happens once: thermal zones do
                // not come and go on a running machine.
                _itemBuffer = new byte[requiredBytes];
            }

            uint bufferBytes = (uint)_itemBuffer.Length;
            uint status = PdhGetFormattedCounterArrayW(_counter, PDH_FMT_DOUBLE, ref bufferBytes, out itemCount, _itemBuffer);

            if (status == PDH_MORE_DATA)
            {
                _itemBuffer = Array.Empty<byte>();
                itemCount = 0;
                return NoValueThisTick(status, collecting: false);
            }

            if (status != 0)
            {
                itemCount = 0;
                return NoValueThisTick(status, collecting: false);
            }

            return true;
        }

        /// <summary>
        /// Edge-triggered diagnostics: one line per failure streak, formatted inside the
        /// guard so a machine whose zone never answers allocates nothing per tick.
        /// </summary>
        /// <param name="status">The PDH status, or 0 when there simply was no valid zone.</param>
        /// <param name="collecting">True when the failure was in the collect rather than the format.</param>
        /// <returns>Always false.</returns>
        private bool NoValueThisTick(uint status, bool collecting)
        {
            if (!_readFailureLogged)
            {
                _readFailureLogged = true;
                LoggingService.Debug(collecting
                    ? $"ThermalZonePdhProvider: PdhCollectQueryData returned 0x{status:X8}; further failures are not logged until one succeeds"
                    : $"ThermalZonePdhProvider: no thermal zone reported a usable temperature (PDH status 0x{status:X8}); further failures are not logged until one succeeds");
            }

            return false;
        }
    }
}
