#pragma warning disable SA1011 // Closing square bracket should be followed by a space - StyleCop 1.1.118 predates nullable reference types and reads "byte[]?" as a spacing error

namespace MetricsPusher.Services
{
    /// <summary>
    /// The only CPU-sensor type the rest of the app talks to. It picks one temperature
    /// provider at startup, owns the <see cref="PawnIoDevice"/> that provider shares with
    /// <see cref="CpuPackagePowerProvider"/>, serializes every call into both, and turns a
    /// per-tick read into a nullable value with one log line per failure streak.
    /// <para>
    /// <b>Ownership, which is the thing to get right here.</b> This service opens the
    /// device, loads the module, and hands the SAME instance to the temperature provider
    /// and the power provider - the driver binds a module to the handle it was loaded
    /// through, and both register sets live in that one module's allow-list. The providers
    /// hold non-owning references (<c>IntelMsrTemperatureProvider.Dispose</c> is
    /// deliberately a no-op, and the AMD one releases only its PCI mutex), so
    /// <see cref="Dispose"/> here tears the providers down FIRST and the device LAST.
    /// Closing the handle earlier would pull it out from under a sibling that is still
    /// reading through it.
    /// </para>
    /// <para>
    /// <b>The state machine mirrors <c>SystemMetricsService.PdhState</c>:</b> probe once,
    /// then either serve a provider forever or latch to failed forever. Structural failures
    /// - no PawnIO device, neither module accepted, no ACPI thermal zone - latch, so the
    /// poll on a machine without any CPU sensor becomes a single field read. Transient
    /// failures do not: a failed IOCTL or an invalid-reading bit costs that one tick and is
    /// retried on the next.
    /// </para>
    /// <para>
    /// <b>Vendor detection is a hint, not a gate.</b> The CPU name
    /// <c>SystemMetricsService</c> already caches only decides which module to TRY FIRST;
    /// the module's own <c>main()</c> is the authoritative check for vendor, family and
    /// architecture, so a name that says nothing useful simply costs one extra rejected
    /// load. That is why an unrecognised name still tries both.
    /// </para>
    /// <para>
    /// <b>No new thread and no new timer.</b> Everything runs on the existing 1 Hz push
    /// tick, next to <c>SystemMetricsService.GetSystemMetrics</c>. A consequence worth
    /// knowing: the push loop only starts once an NVIDIA GPU is found, so on a machine
    /// without one none of this is ever constructed - see the comment beside the
    /// construction in <c>GpuDisplayPushService.RunAsync</c>.
    /// </para>
    /// <para>
    /// Thread-safety: a private lock, like <see cref="NvmeTemperatureService"/> and unlike
    /// <see cref="NvmlService"/>'s "the caller serializes" contract. Every type below this
    /// one - the device, the providers, the RAPL window - is explicitly not thread-safe,
    /// and this is the single place that serializes them. The lock also makes
    /// <see cref="Dispose"/>, which arrives from the tray teardown on another thread, wait
    /// for the tick in progress instead of closing the handle underneath it.
    /// </para>
    /// </summary>
    internal sealed class CpuTemperatureService : IDisposable
    {
        /// <summary>
        /// Manifest name of the embedded IntelMSR 0.2.10 module. Read out of the built
        /// assembly rather than guessed: the directory is <c>PawnIo</c> with a
        /// <b>lowercase o</b> while the module keeps its uppercase <c>MSR</c>, and both
        /// spellings survive verbatim into the manifest. A test pins both names to real
        /// resources, because a typo here is indistinguishable at runtime from "this
        /// machine has no PawnIO".
        /// </summary>
        internal const string IntelModuleResourceName = "MetricsPusher.Resources.PawnIo.IntelMSR.bin";

        /// <summary>Manifest name of the embedded AMDFamily17 0.2.10 module.</summary>
        internal const string AmdModuleResourceName = "MetricsPusher.Resources.PawnIo.AMDFamily17.bin";

        private readonly object _lock = new object(); // Guards every field below and every call into the providers

        // Resolved once at construction: the name cannot change while the app runs, and the
        // AMD provider needs it for its Tdie offset.
        private readonly string? _cpuName;

        // The one environment seam. Production wires this to CreateProvider, which opens the
        // device, loads a module and starts the power provider; the tests wire it to a
        // factory of fakes so the whole state machine below runs with no PawnIO driver, no
        // elevation and on any CPU vendor.
        private readonly Func<CpuTemperatureSource, ICpuTemperatureProvider?> _tryCreateProvider;

        private State _state;
        private CpuTemperatureSource _source;
        private ICpuTemperatureProvider? _provider;
        private CpuPackagePowerProvider? _powerProvider;
        private PawnIoDevice? _device;

        // Set once either module has been loaded successfully or the device could not be
        // opened at all. Both mean no further PawnIO candidate can succeed: the driver binds
        // one module per handle, and a module that accepted this CPU has already named its
        // vendor, so the other module would be rejected by its own main().
        private bool _pawnIoExhausted;

        // Edge triggers: one line per failure streak, cleared by the next success. At 1 Hz a
        // sensor that fails once fails every second thereafter, and LoggingService's
        // duplicate collapsing is a safety net rather than a substitute for these.
        private bool _temperatureFailureLogged;
        private bool _powerFailureLogged;

        // Built once, when the source is known, and reused by every later tick. Formatting
        // it inside ReadTemperature would be a string allocation per second whether or not
        // anything was logged - the same trap the providers avoid by formatting inside their
        // edge guards, and the reason the csproj's disabled concurrent GC matters here.
        private string _temperatureLabel = "the CPU temperature provider";

        /// <summary>
        /// Initializes a new instance of the <see cref="CpuTemperatureService"/> class over
        /// the real machine: the cached CPU name, the PawnIO device and the embedded
        /// modules.
        /// </summary>
        public CpuTemperatureService()
        {
            _cpuName = SystemMetricsService.CpuName;

            // Bound last, and only a delegate to an instance method - nothing runs until the
            // first probe.
            _tryCreateProvider = CreateProvider;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CpuTemperatureService"/> class with
        /// the environment injected, the same shape as
        /// <see cref="SystemMetricsService.DetectPendingReboot(Func{string, bool})"/> and
        /// <see cref="PawnIoInstaller.Decide"/>.
        /// <para>
        /// It exists because every interesting branch of this class - both modules rejected,
        /// the thermal-zone fallback, the latch, the self-heal - is unreachable on any one
        /// machine: a real Intel box with PawnIO installed takes the first branch and never
        /// the others. Injecting the provider factory is what makes the state machine
        /// testable without a driver, without elevation and on either vendor.
        /// </para>
        /// </summary>
        /// <param name="cpuName">The CPU name that decides which module is tried first.</param>
        /// <param name="tryCreateProvider">
        /// Attempts one source and returns an initialized provider, or null when that source
        /// is unavailable here. Called at most once per candidate, and only during the probe.
        /// </param>
        internal CpuTemperatureService(string? cpuName, Func<CpuTemperatureSource, ICpuTemperatureProvider?> tryCreateProvider)
        {
            _cpuName = cpuName;
            _tryCreateProvider = tryCreateProvider;
        }

        /// <summary>
        /// The three states this service can be in, mirroring
        /// <c>SystemMetricsService.PdhState</c>. There is no <c>Priming</c> here: a
        /// temperature is an instantaneous reading, not a rate, so the first read is already
        /// a real one. The energy counter <em>is</em> a rate, and <see cref="Prime"/> is
        /// where that is handled.
        /// </summary>
        private enum State
        {
            NotInitialized, // Nothing probed yet
            Ready,          // A provider was selected and is being polled
            Failed,         // No source on this machine, or disposed - never retried
        }

        /// <summary>
        /// Which sensor the readings come from, or <see cref="CpuTemperatureSource.None"/>
        /// before the probe and on a machine with no source. The wire mapping uses this to
        /// publish only die/package readings and omit the non-equivalent ACPI board zone.
        /// </summary>
        public CpuTemperatureSource Source
        {
            get { lock (_lock) { return _source; } }
        }

        /// <summary>
        /// Which vendor's RAPL registers the package power comes from, or
        /// <see cref="CpuPowerSource.None"/> when there is no power reading at all. Note
        /// that a temperature source and a power source are independent: the ACPI fallback
        /// has no power counterpart, and an Intel machine whose RAPL registers do not answer
        /// still reports temperature.
        /// </summary>
        public CpuPowerSource PowerSource
        {
            get { lock (_lock) { return _powerProvider?.Source ?? CpuPowerSource.None; } }
        }

        /// <summary>
        /// The package power limit in watts, or null when there is none.
        /// <para>
        /// <b>Null on AMD is structural, not a failure</b> - AMDFamily17's allow-list has no
        /// package power-limit register, so the limit lives behind the SMU and is out of
        /// scope. This mirrors what the wire contract already documents for the GPU's NVAPI
        /// fallback, where <c>watts</c> and <c>limitW</c> are structurally absent: never
        /// render it as an error.
        /// </para>
        /// </summary>
        public float? PackagePowerLimitWatts
        {
            get { lock (_lock) { return _powerProvider?.PackagePowerLimitWatts; } }
        }

        /// <summary>
        /// Runs the one-time probe - open the device, load a module, initialize a provider,
        /// or fall back to the ACPI thermal zone - and logs the selected source once. Call
        /// this at startup, beside <c>SystemMetricsService.PrimeCpuCounter</c>, so the tick
        /// never pays for a <c>CreateFile</c>, a module load and a handful of init reads.
        /// <para>
        /// Idempotent and never throws. Calling it is an optimization, not a precondition:
        /// <see cref="ReadTemperature"/> probes on demand if this was never called.
        /// </para>
        /// </summary>
        /// <returns>True when a source was selected and readings can be produced.</returns>
        public bool Initialize()
        {
            lock (_lock)
            {
                return EnsureProbed();
            }
        }

        /// <summary>
        /// Establishes the first RAPL energy sample so a call roughly a second later can
        /// already report watts. Exactly why <c>SystemMetricsService.PrimeCpuCounter</c>
        /// exists, and it belongs in the same place: an energy accumulator is a rate and a
        /// rate needs two samples.
        /// <para>
        /// Costs nothing on a machine with no power source, and nothing at all for
        /// temperature - that is an instantaneous reading with no baseline to establish.
        /// </para>
        /// </summary>
        public void Prime()
        {
            lock (_lock)
            {
                if (!EnsureProbed())
                    return;

                _powerProvider?.Prime();
            }
        }

        /// <summary>
        /// One temperature read. Null means no value this tick - a transient IOCTL failure,
        /// an invalid-reading bit, or no source on this machine at all - and the caller
        /// simply omits the field. Never throws.
        /// </summary>
        /// <returns>The CPU temperature in °C, or null.</returns>
        public float? ReadTemperature()
        {
            lock (_lock)
            {
                if (!EnsureProbed() || _provider == null)
                    return null;

                try
                {
                    bool read = _provider.TryRead(out float celsius);
                    _temperatureFailureLogged = NoteReadOutcome(read, _temperatureFailureLogged, _temperatureLabel);
                    return read ? celsius : null;
                }
                catch (Exception ex)
                {
                    // The providers promise not to throw, so one that does is structurally
                    // broken rather than having a bad tick: latch instead of retrying it
                    // once a second forever.
                    LatchFailed($"{_source} temperature provider threw: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// One RAPL energy read, reported as the average package power since the previous
        /// call. Null is ordinary: no power source, the first call after
        /// <see cref="Prime"/>, an interval outside the accepted window, or a failed IOCTL.
        /// Never throws.
        /// </summary>
        /// <returns>The CPU package power in watts, or null.</returns>
        public float? ReadPackagePower()
        {
            lock (_lock)
            {
                if (!EnsureProbed() || _powerProvider == null)
                    return null;

                try
                {
                    bool read = _powerProvider.TryRead(out float watts);
                    _powerFailureLogged = NoteReadOutcome(read, _powerFailureLogged, "the CPU package power provider");
                    return read ? watts : null;
                }
                catch (Exception ex)
                {
                    // Only the power half is dropped: the temperature provider is a separate
                    // object and a working temperature is worth keeping.
                    _powerProvider = null;
                    LoggingService.Debug($"CpuTemperatureService: package power provider threw ({ex.Message}); CPU power is disabled for this session");
                    return null;
                }
            }
        }

        /// <summary>
        /// Tears everything down in the one order that is safe: the providers first, then
        /// the device they were reading through. Safe to call more than once, and safe to
        /// call while a read is in flight - it waits for the tick to finish rather than
        /// closing the handle underneath it.
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                // Failed doubles as "disposed": both mean no later call may produce a value
                // and none may re-probe.
                _state = State.Failed;

                _provider?.Dispose();
                _provider = null;

                // Not IDisposable, and deliberately so - it owns nothing. Dropping the
                // reference is the whole teardown.
                _powerProvider = null;

                // LAST. Both providers held non-owning references to this handle, so closing
                // it before they were let go would be closing a handle still in use.
                CloseDevice();
            }
        }

        /// <summary>
        /// Which sources to try, in order. The CPU name only picks which module goes first -
        /// the module's <c>main()</c> is the authoritative gate - so an unrecognised name
        /// costs one rejected load and nothing else, and both modules are always tried
        /// before the fallback.
        /// <para>
        /// The ACPI thermal zone is always last and never first: it is a board sensor rather
        /// than the die, so it is what you settle for, not what you prefer.
        /// </para>
        /// </summary>
        /// <param name="cpuName">The cached CPU name, or null.</param>
        /// <returns>The candidate sources, most preferred first.</returns>
        internal static CpuTemperatureSource[] ProbeOrder(string? cpuName)
        {
            // One array per session, built during the probe - this is not a per-tick path.
            return LooksLikeAmd(cpuName)
                ? new CpuTemperatureSource[] { CpuTemperatureSource.AmdTctlSmn, CpuTemperatureSource.IntelPackageMsr, CpuTemperatureSource.AcpiThermalZone }
                : new CpuTemperatureSource[] { CpuTemperatureSource.IntelPackageMsr, CpuTemperatureSource.AmdTctlSmn, CpuTemperatureSource.AcpiThermalZone };
        }

        /// <summary>
        /// Walks <see cref="ProbeOrder"/> and returns the first source that answers, or null
        /// when none does. Pure apart from <paramref name="tryCreate"/>, which is the only
        /// thing here that touches the machine.
        /// </summary>
        /// <param name="cpuName">The cached CPU name, or null.</param>
        /// <param name="tryCreate">Attempts one source; null when it is unavailable.</param>
        /// <returns>An initialized provider, or null.</returns>
        internal static ICpuTemperatureProvider? SelectProvider(
            string? cpuName, Func<CpuTemperatureSource, ICpuTemperatureProvider?> tryCreate)
        {
            foreach (CpuTemperatureSource candidate in ProbeOrder(cpuName))
            {
                ICpuTemperatureProvider? provider = tryCreate(candidate);
                if (provider != null)
                    return provider;
            }

            return null;
        }

        /// <summary>
        /// The edge trigger, in the shape <c>GpuDisplayPushService.NoteOversizeDatagram</c>
        /// established: take the previous streak state, log at most one line, and return the
        /// state to carry to the next tick. Pure enough to test, which is the point - "one
        /// line per streak" is a claim about a sequence of calls, and asserting it on a flag
        /// beats asserting it on a log file.
        /// </summary>
        /// <param name="hadValue">Whether this read produced a value.</param>
        /// <param name="alreadyLogged">Whether the current failure streak was already logged.</param>
        /// <param name="what">What was read, for the one line.</param>
        /// <returns>The new "already logged" state.</returns>
        internal static bool NoteReadOutcome(bool hadValue, bool alreadyLogged, string what)
        {
            if (hadValue)
            {
                // Only speak on the edge back to working, so a healthy sensor is silent.
                if (alreadyLogged)
                    LoggingService.Debug($"CpuTemperatureService: {what} recovered");

                return false;
            }

            if (alreadyLogged)
                return true;

            // Formatted inside the guard: at 1 Hz, building this string outside it would be
            // an allocation per second forever in a process with concurrent GC disabled.
            LoggingService.Debug($"CpuTemperatureService: {what} produced no value this tick; further failures are not logged until one succeeds");
            return true;
        }

        /// <summary>
        /// Whether a CPU name names an AMD part. Deliberately generous - the answer only
        /// reorders two attempts - and deliberately not a CPUID path, since the module gate
        /// is authoritative and the app already has this string.
        /// </summary>
        /// <param name="cpuName">The cached CPU name, or null.</param>
        /// <returns>True when the AMD module should be tried first.</returns>
        private static bool LooksLikeAmd(string? cpuName)
        {
            if (string.IsNullOrEmpty(cpuName))
                return false;

            return cpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                   cpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) ||
                   cpuName.Contains("EPYC", StringComparison.OrdinalIgnoreCase) ||
                   cpuName.Contains("Threadripper", StringComparison.OrdinalIgnoreCase) ||
                   cpuName.Contains("Athlon", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reads one embedded module's bytes. One allocation per attempt, at startup: the
        /// blobs are a few KB and the driver copies them out of the buffer immediately.
        /// </summary>
        /// <param name="resourceName">The manifest resource name.</param>
        /// <returns>The module bytes, or null when the resource is missing or unreadable.</returns>
        private static byte[]? ReadEmbeddedModule(string resourceName)
        {
            try
            {
                using Stream? resource = typeof(CpuTemperatureService).Assembly.GetManifestResourceStream(resourceName);
                if (resource == null)
                {
                    // A build problem, not a machine problem - hence Warn, where a rejected
                    // module is only ever Debug.
                    LoggingService.Warn($"CpuTemperatureService: embedded module {resourceName} is missing from this build");
                    return null;
                }

                using var buffer = new MemoryStream();
                resource.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"CpuTemperatureService: could not read the embedded module {resourceName}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Probes once and latches the verdict. Must be called under <see cref="_lock"/>.
        /// </summary>
        /// <returns>True when a provider is selected and can be polled.</returns>
        private bool EnsureProbed()
        {
            if (_state == State.Ready)
                return true;

            if (_state == State.Failed)
                return false;

            // Pessimistic before anything is attempted, so a throw on the way through leaves
            // this latched rather than re-probing the same broken machine once a second.
            _state = State.Failed;

            try
            {
                ICpuTemperatureProvider? provider = SelectProvider(_cpuName, _tryCreateProvider);
                if (provider == null)
                {
                    // Expected in a VM, on a desktop with no \_TZ object, and on any machine
                    // where the user declined PawnIO and the firmware exposes no zone. One
                    // line, at Info because a reader looking for a missing temperature
                    // should find the answer without enabling Debug.
                    LoggingService.Info("CpuTemperatureService: no CPU temperature source on this machine - neither PawnIO module loaded and no ACPI thermal zone answered; CPU temperature is unavailable this session");
                    CloseDevice();
                    return false;
                }

                _provider = provider;
                _source = provider.Source;
                _state = State.Ready;
                _temperatureLabel = $"the {_source} temperature provider";

                // A device that survived two rejected modules has nothing left to do: the
                // fallback provider reads PDH, not PawnIO, and holding an open kernel handle
                // nobody will ever issue an IOCTL on is a leak with a friendly face.
                if (_source == CpuTemperatureSource.AcpiThermalZone)
                    CloseDevice();

                LoggingService.Info($"CpuTemperatureService: CPU temperature source {_source}, package power {_powerProvider?.Source ?? CpuPowerSource.None}, limit {DescribePackagePowerLimit()}");
                return true;
            }
            catch (Exception ex)
            {
                LatchFailed($"the probe threw: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// The production provider factory: open the device once, load the candidate's
        /// module, initialize the provider, and start the power provider on the same handle.
        /// Null means "this source is not available here", which is an ordinary answer for
        /// at least one of the three on every machine.
        /// </summary>
        /// <param name="source">The candidate source.</param>
        /// <returns>An initialized provider, or null.</returns>
        private ICpuTemperatureProvider? CreateProvider(CpuTemperatureSource source)
        {
            return source switch
            {
                CpuTemperatureSource.IntelPackageMsr => CreatePawnIoProvider(source, IntelModuleResourceName),
                CpuTemperatureSource.AmdTctlSmn => CreatePawnIoProvider(source, AmdModuleResourceName),
                CpuTemperatureSource.AcpiThermalZone => CreateThermalZoneProvider(),
                _ => null,
            };
        }

        /// <summary>
        /// Opens the device if it is not open yet, loads one module and starts the matching
        /// providers.
        /// <para>
        /// A rejected module leaves the device reusable, which is exactly what lets the
        /// second candidate be tried on the same handle. A module that <em>loads</em> closes
        /// the handle to further modules and has already named this CPU's vendor, so either
        /// way there is no third PawnIO attempt to make.
        /// </para>
        /// </summary>
        /// <param name="source">Which vendor's provider to build.</param>
        /// <param name="moduleResourceName">The module to load.</param>
        /// <returns>An initialized provider, or null.</returns>
        private ICpuTemperatureProvider? CreatePawnIoProvider(CpuTemperatureSource source, string moduleResourceName)
        {
            if (_pawnIoExhausted)
                return null;

            if (_device == null)
            {
                // TryOpen classifies its own failure and logs it at the level each outcome
                // deserves, so nothing is added here. A device that will not open means
                // neither module can be tried - hence exhausted, not just "this one failed".
                if (PawnIoDevice.TryOpen(out PawnIoDevice? device) != PawnIoOpenStatus.Opened || device == null)
                {
                    _pawnIoExhausted = true;
                    return null;
                }

                _device = device;
            }

            byte[]? module = ReadEmbeddedModule(moduleResourceName);
            if (module == null || !_device.TryLoadModule(module))
                return null;

            _pawnIoExhausted = true;

            if (source == CpuTemperatureSource.AmdTctlSmn)
            {
                var amd = new AmdSmnTemperatureProvider(_device, _cpuName);
                if (amd.Initialize())
                {
                    StartPowerProvider(CpuPowerSource.AmdRapl);
                    return amd;
                }

                amd.Dispose();
            }
            else
            {
                var intel = new IntelMsrTemperatureProvider(_device);
                if (intel.Initialize())
                {
                    StartPowerProvider(CpuPowerSource.IntelRapl);
                    return intel;
                }

                intel.Dispose();
            }

            // The module loaded but the registers behind it did not answer. Nothing else can
            // use this handle - one module per handle - so let it go now rather than holding
            // an open kernel device for the rest of the session.
            CloseDevice();
            return null;
        }

        /// <summary>
        /// Builds the ACPI fallback. It insists on an actual reading rather than a counter
        /// that merely got created, which is why a machine with no <c>\_TZ</c> object lands
        /// here and still answers null.
        /// </summary>
        /// <returns>An initialized provider, or null.</returns>
        private static ICpuTemperatureProvider? CreateThermalZoneProvider()
        {
            var zone = new ThermalZonePdhProvider();
            if (zone.Initialize())
                return zone;

            // Owns a PDH query even when it decided it is unusable; not disposing it here
            // would leak one per probe.
            zone.Dispose();
            return null;
        }

        /// <summary>
        /// Starts package power on the device the temperature provider is already using.
        /// Failure costs the power reading and nothing else - a machine whose RAPL registers
        /// do not answer still reports temperature.
        /// </summary>
        /// <param name="source">Which vendor's RAPL registers to read.</param>
        private void StartPowerProvider(CpuPowerSource source)
        {
            if (_device == null)
                return;

            var power = new CpuPackagePowerProvider(_device, source);
            if (power.Initialize())
                _powerProvider = power;
        }

        /// <summary>
        /// Closes the PawnIO device. Idempotent, and the only place the handle is released
        /// outside <see cref="Dispose"/>.
        /// </summary>
        private void CloseDevice()
        {
            _device?.Dispose();
            _device = null;
        }

        /// <summary>
        /// Latches the service off after one line. Used only for structural failures - the
        /// per-tick ones go through <see cref="NoteReadOutcome"/> and are retried.
        /// </summary>
        /// <param name="reason">What went wrong.</param>
        private void LatchFailed(string reason)
        {
            _state = State.Failed;
            _provider?.Dispose();
            _provider = null;
            _powerProvider = null;
            CloseDevice();
            LoggingService.Warn($"CpuTemperatureService: {reason}; CPU temperature is disabled for this session");
        }

        /// <summary>
        /// The power limit as a fragment of the one startup line. It says "structurally
        /// absent" rather than "unknown" on AMD because those are different facts; the
        /// wire expresses the same distinction by omitting the Intel-only limit on AMD.
        /// <para>
        /// Reads the fields directly rather than going through
        /// <see cref="PackagePowerLimitWatts"/>: it is only ever called from inside the
        /// lock, and while <c>lock</c> would re-enter happily, a property that takes a lock
        /// is not something to call from under one out of habit.
        /// </para>
        /// </summary>
        /// <returns>The limit, formatted for the startup line.</returns>
        private string DescribePackagePowerLimit()
        {
            if (_powerProvider == null)
                return "none";

            float? limit = _powerProvider.PackagePowerLimitWatts;
            if (limit != null)
                return $"{limit.Value:F2} W";

            return _powerProvider.Source == CpuPowerSource.AmdRapl ? "structurally absent (AMD)" : "unknown";
        }
    }
}
