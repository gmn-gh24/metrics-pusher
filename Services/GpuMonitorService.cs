using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.Exceptions;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native.Interfaces.GPU;

#pragma warning disable SA1402, SA1649 // File may only contain a single type - GpuMetrics is tightly coupled to GpuMonitorService

namespace MetricsPusher.Services
{
    /// <summary>
    /// Data structure for all GPU metrics. Init-only: an instance is published to every
    /// consumer lock-free via the snapshot, so immutability after construction is a
    /// correctness requirement, not a preference. Each sweep builds a fresh one.
    /// </summary>
    internal sealed class GpuMetrics
    {
        public string? Name { get; init; }
        public float? Temperature { get; init; }
        public int? UsagePercent { get; init; }
        public long? VramUsedMB { get; init; }
        public long? VramTotalMB { get; init; }
        public int? FanSpeedPercent { get; init; }
        public int? PowerPercent { get; init; }
        public int? PowerWatts { get; init; }
        public int? PowerLimitWatts { get; init; }
        public int? CoreClockMHz { get; init; }
        public int? MemoryClockMHz { get; init; }
    }

    /// <summary>
    /// NVIDIA GPU monitoring service. Runs in user context (no elevation required).
    /// <para>
    /// Two backends, one at a time and never mixed per field: NVML
    /// (<see cref="NvmlService"/>) is tried first and reads every metric on every sweep,
    /// because each call costs ~0.02-0.3 ms; NVAPI is the fallback for machines or
    /// drivers where NVML will not load, and keeps the v5.9.0 cadence tiers that exist
    /// to amortize its much more expensive reads. Both are covered by the same
    /// two-strike handle-loss rule and the same 5 s rate-limited re-acquire, which
    /// always re-tries NVML first.
    /// </para>
    /// </summary>
    internal static class GpuMonitorService
    {
        private const int InitializationTimeoutMs = 30000;

        // One NVAPI read serves every consumer: snapshots younger than this are reused.
        // The 1 Hz display push drives the sweep and its PeriodicTimer never fires early,
        // so its ticks are always >= 1000 ms apart and usually miss this cache and sweep
        // fresh. It is not a guarantee: should a second consumer ever write the snapshot at
        // an arbitrary phase, the push would reuse that sample instead, so a datagram can
        // carry GPU values up to 950 ms old - see push_metrics.md section 6.
        // Must stay below 1000 ms.
        private const int MetricsCacheTtlMs = 950;

        // Power readings above 100% of TDP are real (transient boost), but anything
        // past this is sensor garbage and is dropped like an invalid temperature.
        private const float MaxValidPowerPercent = 200f;

        // No single board draws 2 kW - the most extravagant consumer cards sit under
        // 700 W - so anything at or past this is a broken reading, not a hot GPU. Also
        // caps the wire width at 4 digits, which the datagram budget is pinned against.
        internal const int MaxValidPowerWatts = 2000;

        // No GPU core clocks anywhere near 10 GHz; also caps the wire width at 4 digits.
        internal const int MaxValidCoreClockMHz = 10000;

        // Memory clocks are reported on their own scale and legitimately exceed the core
        // cap - GDDR6X reads ~10501 MHz under load - so they get their own ceiling. Also
        // caps the wire width at 5 digits, which the datagram budget is pinned against.
        internal const int MaxValidMemoryClockMHz = 20000;

        // How long to wait before re-enumerating after the GPU handle is lost. Enumeration
        // is the most expensive call left in a degraded state, so retrying it every sweep
        // would burn more CPU while broken than while working.
        private const int HandleReacquireIntervalMs = 5000;

        // How many consecutive all-null sweeps it takes to declare the handle dead. Two,
        // not one: on a machine where the per-sweep sensors are legitimately null (the
        // probe validated a GPU other than gpus[0]; a vGPU whose utilization domain
        // reports IsPresent:false), every sweep in which only those sensors were due
        // would otherwise look like handle loss and drop a perfectly live handle. The
        // slower cadences answer on the very next sweep, which clears the count.
        //
        // LATENT INVARIANT (NVAPI fallback only - on the NVML backend every metric is
        // read on every sweep, so there is no cadence window to fall inside): that
        // rescue argument holds only while no two consecutive
        // sweeps can both fall inside one VramIntervalMs window. True today because the
        // push loop is the only consumer and it re-requests on a >= 1000 ms grid
        // (PeriodicTimer never fires early), which the 950 ms TTL floors. Lowering
        // MetricsCacheTtlMs, raising VramIntervalMs, or adding a second consumer that
        // sweeps faster than 2 s re-opens the false-drop window this guard exists to
        // close. Those numbers move together, never one at a time.
        private const int LostSweepsBeforeDrop = 2;

        // Per-metric NVAPI read cadences - the FALLBACK backend's; NVML reads everything
        // every sweep. These do NOT change the wire: every datagram
        // still carries every available field once per second, because a sweep re-serves
        // the cached value between reads. They only control how often the underlying
        // NVAPI call is paid for. Measured on an RTX 3090 Ti, the power-topology read
        // alone is 13.15 ms of a 16.4 ms sweep (80%), which is why it sits at the
        // slowest tier - the deliberate trade being that the reported power can lag a
        // workload change by up to one period. Fan level and VRAM totals move slowly
        // enough that their tiers cost nothing perceptible. Temperature, load and clock
        // are cheap and are what a glance actually reads, so they stay per-sweep.
        private const int VramIntervalMs = 2000;
        private const int FanIntervalMs = 3000;
        private const int PowerIntervalMs = 3000;

        /// <summary>
        /// Which driver stack the sweep currently reads through. Latched at acquire time
        /// and never mixed per field: a null NVML reading stays null for that tick and is
        /// retried on the next one rather than falling through to NVAPI, so one datagram
        /// never blends two stacks' views of the board.
        /// </summary>
        internal enum GpuBackend
        {
            /// <summary>No usable stack: nothing acquired yet, or the handle was dropped.</summary>
            None,

            /// <summary>NVML (nvml.dll) - the primary. Cheap enough that every metric is read every sweep.</summary>
            Nvml,

            /// <summary>NVAPI - the fallback, with the v5.9.0 cadence tiers intact.</summary>
            Nvapi,
        }

        // Guards every NVML and NVAPI call (GetGpuMetrics/Shutdown), and the backend
        // latch with them. The initialization probe runs outside this lock so a slow
        // NVAPI probe never blocks UI-thread state reads.
        private static readonly object _lock = new object();
        private static int _initStarted; // Interlocked gate: 0 = not started, 1 = started
        private static volatile bool _initialized;
        private static volatile bool _nvidiaGpuAvailable;

        // The published sample. Written under _lock, read from anywhere via Volatile so a
        // reader can never pair one sweep's metrics with another sweep's timestamp.
        private static GpuSnapshot? _snapshot;

        // All guarded by _lock. The PhysicalGPU object and its handle never leave the lock:
        // NvAPIWrapper's internal delegate cache is a plain Dictionary and concurrent
        // first-calls corrupt it. NvmlService is not thread-safe by design and rides the
        // same lock, which is why every NVML call below happens inside a sweep.
        private static GpuBackend _backend; // Latched by AcquireBackend, cleared by DropBackend/Shutdown
        private static PhysicalGPU? _gpu;

        // The denominator of the NVML power PERCENTAGE, read once per acquire because it
        // only changes when the user moves the board's power slider. Null suppresses the
        // percentage rather than inventing one - it does NOT suppress watts, which is
        // read from the same call and needs no denominator (see ReadPower).
        private static uint? _powerLimitMilliwatts;
        private static long _nextReacquireTicks; // Environment.TickCount64 before which no re-enumeration is attempted
        private static bool _handleUnavailableLogged; // Edge trigger, so a dead GPU logs once instead of once per sweep
        private static int _consecutiveLostSweeps; // Strikes against the current handle; see LostSweepsBeforeDrop
        private static bool _usageLegacyFallback; // Latched when GetDynamicPerformanceStatesInfoEx is unsupported
        private static bool _coolerLegacyFallback; // Latched when GetClientFanCoolersStatus is unsupported

        // Reference count, not a flag: set from the UI thread when a window that wants
        // live values opens, read inside _lock by the sweep.
        private static int _highFidelityHolders;

        // The metric registry: what a sweep reads, and how often each read is worth
        // paying for. Guarded by _lock exactly like the handles they read through. The
        // delegates are static method groups, so a sweep allocates nothing here.
        //
        // The cadences below are the NVAPI fallback's: on the NVML backend every metric
        // is read every sweep (see ReadEveryMetric), because an NVML read costs
        // ~0.02-0.3 ms and there is nothing left worth amortizing. One registry serves
        // both stacks - each read delegate routes on _backend - so a metric can never
        // exist on one backend and be forgotten on the other.
        private static readonly SampledMetric<string?> _nameMetric = new SampledMetric<string?>(SampledMetric.Session, ReadName);
        private static readonly SampledMetric<float?> _temperatureMetric = new SampledMetric<float?>(SampledMetric.Live, ReadTemperature);
        private static readonly SampledMetric<int?> _usageMetric = new SampledMetric<int?>(SampledMetric.Live, ReadUsagePercent);
        private static readonly SampledMetric<(long UsedMB, long TotalMB)?> _vramMetric = new SampledMetric<(long UsedMB, long TotalMB)?>(VramIntervalMs, ReadVram);
        private static readonly SampledMetric<int?> _fanMetric = new SampledMetric<int?>(FanIntervalMs, ReadFanSpeedPercent);
        private static readonly SampledMetric<(int? Percent, int? Watts)?> _powerMetric = new SampledMetric<(int? Percent, int? Watts)?>(PowerIntervalMs, ReadPower);
        private static readonly SampledMetric<(int? CoreMHz, int? MemoryMHz)?> _clockMetric = new SampledMetric<(int? CoreMHz, int? MemoryMHz)?>(SampledMetric.Live, ReadClocks);

        // Declared after the metrics themselves: static field initializers run in
        // textual order. Everything that invalidates the cached handle resets this
        // whole array, so no metric can be forgotten.
        private static readonly SampledMetric[] _allMetrics =
        {
            _nameMetric, _temperatureMetric, _usageMetric, _vramMetric, _fanMetric, _powerMetric, _clockMetric
        };

        /// <summary>
        /// Gets a value indicating whether an NVIDIA GPU was detected and is available.
        /// </summary>
        public static bool IsGpuAvailable => _nvidiaGpuAvailable;

        /// <summary>
        /// Gets a value indicating whether any consumer currently needs every metric
        /// read live, cadences ignored.
        /// </summary>
        internal static bool HighFidelityEnabled => Volatile.Read(ref _highFidelityHolders) > 0;

        /// <summary>
        /// Gets the driver stack the sweep is currently reading through.
        /// <see cref="GpuBackend.None"/> until the first sweep acquires one, and again
        /// after a handle loss or <see cref="Shutdown"/>.
        /// </summary>
        internal static GpuBackend ActiveBackend
        {
            get
            {
                lock (_lock)
                {
                    return _backend;
                }
            }
        }

        /// <summary>
        /// Suspends the per-metric cadences while a consumer needs every value live
        /// (the GPU Monitor window). Reference-counted, so overlapping holders cannot
        /// release each other's hold; callers must pair every true with one false.
        /// The snapshot TTL is unaffected - it still dedupes concurrent consumers into
        /// one sweep.
        /// </summary>
        internal static void SetHighFidelity(bool enabled)
        {
            if (enabled)
            {
                Interlocked.Increment(ref _highFidelityHolders);
                return;
            }

            // Clamped at zero: an unbalanced release must not drive the count negative,
            // which would silently swallow the next hold.
            int current;
            do
            {
                current = Volatile.Read(ref _highFidelityHolders);
                if (current == 0)
                    return;
            }
            while (Interlocked.CompareExchange(ref _highFidelityHolders, current - 1, current) != current);
        }

        /// <summary>
        /// Initialize the GPU monitor service. Detects whether an NVIDIA GPU is present
        /// and at least one driver stack can read it: NVAPI is probed first and NVML is
        /// asked only if NVAPI declines, so a board that only one of them can see still
        /// enables the feature.
        /// </summary>
        public static void Initialize()
        {
            // Only the first caller runs the probe; later calls are no-ops.
            if (Interlocked.Exchange(ref _initStarted, 1) == 1)
                return;

            var initTask = Task.Run(ProbeNvidiaGpu);

            // Wait without holding _lock: IsGpuAvailable and GetGpuMetrics stay responsive
            // (returning "no GPU yet") even if the NVAPI probe hangs for the full timeout.
            if (!initTask.Wait(InitializationTimeoutMs))
            {
                LoggingService.Warn($"GpuMonitorService: Initialization timed out after {InitializationTimeoutMs}ms");
            }

            _initialized = true;

            if (_nvidiaGpuAvailable)
            {
                LoggingService.Info("GpuMonitorService: NVIDIA GPU detected");
            }
            else
            {
                LoggingService.Info("GpuMonitorService: No NVIDIA GPU found");
            }
        }

        /// <summary>
        /// Returns true when a metrics snapshot taken at <paramref name="lastReadTicks"/>
        /// is still fresh at <paramref name="nowTicks"/> (both from Environment.TickCount64).
        /// </summary>
        internal static bool IsCacheFresh(long nowTicks, long lastReadTicks)
        {
            return nowTicks - lastReadTicks < MetricsCacheTtlMs;
        }

        /// <summary>
        /// Whether this sweep must read every metric instead of honoring the per-metric
        /// cadences. True while a consumer holds high fidelity (the GPU Monitor window),
        /// and always on the NVML backend: its reads cost ~0.02-0.3 ms each, so the
        /// tiers have nothing left to amortize and the wire gets genuinely live values.
        /// The NVAPI fallback keeps its cadences - there the power-topology read alone
        /// was 13.15 ms of a 16.4 ms sweep.
        /// </summary>
        internal static bool ReadEveryMetric(bool highFidelity, GpuBackend backend)
        {
            return highFidelity || backend == GpuBackend.Nvml;
        }

        /// <summary>
        /// GPU power draw as a percentage of the board's enforced power limit, from the
        /// milliwatt pair NVML reports. Null when either reading is missing or the limit
        /// is zero (an unusable denominator, not an infinite percentage), and null again
        /// when the result fails the same <see cref="MaxValidPowerPercent"/> plausibility
        /// check the NVAPI path applies to its own percentage - the raw ratio is checked
        /// before rounding, so 200.4 % is rejected rather than rounded back under the cap.
        /// </summary>
        internal static int? DerivePowerPercent(uint? milliwatts, uint? enforcedLimitMilliwatts)
        {
            if (milliwatts is not uint draw || enforcedLimitMilliwatts is not uint limit || limit == 0)
                return null;

            double percent = draw * 100.0 / limit;
            return percent <= MaxValidPowerPercent ? (int)Math.Round(percent) : null;
        }

        /// <summary>
        /// The same NVML reading as whole watts, or null when it is missing or past
        /// <see cref="MaxValidPowerWatts"/> (dropped, not clamped - absent means
        /// "unknown", and a 2 kW board reading is a broken sensor, not a hot one).
        /// Zero is a value, exactly as it is for the percentage: both fields are
        /// derived from one reading and must not disagree about what it meant.
        /// </summary>
        internal static int? DerivePowerWatts(uint? milliwatts)
        {
            if (milliwatts is not uint draw)
                return null;

            double watts = Math.Round(draw / 1000.0);
            return watts < MaxValidPowerWatts ? (int)watts : null;
        }

        /// <summary>
        /// The enforced power limit as whole watts for the wire, or null when the
        /// acquire-time read failed or the value is implausible. Shares
        /// <see cref="MaxValidPowerWatts"/> with <see cref="DerivePowerWatts"/> but,
        /// unlike the draw, EXCLUDES zero: a zero limit is a broken read, not a real
        /// board state - the same verdict <see cref="DerivePowerPercent"/> passes on
        /// a zero denominator, so the two views of the limit can never disagree.
        /// Validated independently of the draw pair: a garbage limit drops this
        /// field alone, never power or watts.
        /// </summary>
        internal static int? DerivePowerLimitWatts(uint? limitMilliwatts)
        {
            if (limitMilliwatts is not uint limit)
                return null;

            double watts = Math.Round(limit / 1000.0);
            return watts > 0 && watts < MaxValidPowerWatts ? (int)watts : null;
        }

        /// <summary>
        /// Both power fields from ONE reading: the percentage against the enforced limit
        /// and the raw draw in watts. They are paired for the same reason the two clock
        /// domains are - so a sweep pays for the underlying read once, whatever the
        /// cadences do - and because publishing two views of one sample from two reads
        /// would let them describe different instants.
        /// <para>
        /// Null when the READING is missing - which is what the handle-loss rule counts
        /// as evidence - or when neither derivation survives validation, since nothing
        /// usable came out of the read either way. A present reading whose percentage
        /// cannot be computed (no enforced limit) still yields watts: the denominator is
        /// the percentage's problem alone.
        /// </para>
        /// </summary>
        internal static (int? Percent, int? Watts)? DerivePower(uint? milliwatts, uint? enforcedLimitMilliwatts)
        {
            if (milliwatts == null)
                return null;

            int? percent = DerivePowerPercent(milliwatts, enforcedLimitMilliwatts);
            int? watts = DerivePowerWatts(milliwatts);

            return percent == null && watts == null ? null : (percent, watts);
        }

        /// <summary>
        /// A clock reading in MHz, or null when it is missing or implausible. One
        /// validator for both domains; only the (exclusive) ceiling differs, and each
        /// ceiling also bounds that field's width on the wire.
        /// </summary>
        internal static int? ValidateClockMHz(long? mhz, int maxExclusiveMHz)
        {
            return mhz is long value && value > 0 && value < maxExclusiveMHz ? (int)value : null;
        }

        /// <summary>
        /// Returns true when every NVAPI read that actually ran this sweep came back
        /// empty, which means the handle itself died (driver restart, GPU reset) rather
        /// than one sensor being unsupported.
        /// <para>
        /// Only executed reads count. Values served from a cadence cache say nothing
        /// about whether the handle still answers - and since the GPU name is latched
        /// for the session, a rule that looked at the assembled fields instead could
        /// never fire again. Temperature, usage and clock are read on every sweep, so
        /// detection stays as prompt as it was before cadences existed.
        /// </para>
        /// </summary>
        internal static bool IsHandleLost(SampledMetric[] metrics)
        {
            bool anyExecuted = false;

            foreach (var metric in metrics)
            {
                if (!metric.LastGetExecuted)
                    continue;

                if (!metric.LastGetReturnedNull)
                    return false;

                anyExecuted = true;
            }

            return anyExecuted;
        }

        /// <summary>
        /// Folds one sweep's <see cref="IsHandleLost"/> verdict into the consecutive-loss
        /// count and decides whether the handle should now be dropped. Two strikes are
        /// required (<see cref="LostSweepsBeforeDrop"/>): one all-null sweep is a
        /// suspicion, two in a row is a verdict. The count is cleared by any healthy
        /// sweep and by the drop itself, since the next handle deserves its own strikes.
        /// <para>
        /// The cost of the second strike is one extra sweep (~1 s) before a genuinely
        /// dead handle is released. The cost of NOT having it is a live handle being
        /// dropped and re-enumerated every 5 s forever on hardware whose per-sweep
        /// sensors legitimately report nothing.
        /// </para>
        /// </summary>
        internal static bool ShouldDropHandle(bool sweepLost, ref int consecutiveLostSweeps)
        {
            if (!sweepLost)
            {
                consecutiveLostSweeps = 0;
                return false;
            }

            consecutiveLostSweeps++;
            if (consecutiveLostSweeps < LostSweepsBeforeDrop)
                return false;

            consecutiveLostSweeps = 0;
            return true;
        }

        /// <summary>
        /// True for the two exceptions NVAPI raises when the running driver or GPU does
        /// not support an entry point, which is what the usage and cooler fallbacks below
        /// latch on. Both must be named: NVIDIANotSupportedException derives from
        /// System.NotSupportedException, NOT from NVIDIAApiException, and it is the one
        /// thrown for "driver does not export this entry point" and "no accepted struct
        /// version" - precisely the legacy-hardware case the fallbacks exist for.
        /// </summary>
        internal static bool IsUnsupportedApiException(Exception ex)
        {
            return ex is NVIDIAApiException or NVIDIANotSupportedException;
        }

        /// <summary>
        /// Get all GPU metrics. Returns empty metrics if no NVIDIA GPU available.
        /// </summary>
        public static GpuMetrics GetGpuMetrics()
        {
            if (!_initialized || !_nvidiaGpuAvailable)
                return new GpuMetrics();

            // Fast path: a fresh sample is served without touching the NVAPI lock at all,
            // so a slow sweep never stalls the consumers that are only reusing its result.
            var snapshot = Volatile.Read(ref _snapshot);
            if (snapshot != null && IsCacheFresh(Environment.TickCount64, snapshot.Ticks))
                return snapshot.Metrics;

            lock (_lock)
            {
                // Re-check the gate under the lock too: Shutdown() may have unloaded
                // NVAPI while this caller queued for it, and sweeping afterwards would
                // re-enumerate and cache a handle against an unloaded driver.
                if (!_initialized || !_nvidiaGpuAvailable)
                    return new GpuMetrics();

                long nowTicks = Environment.TickCount64;

                // Re-check: another consumer may have swept while this one waited for the lock.
                snapshot = Volatile.Read(ref _snapshot);
                if (snapshot != null && IsCacheFresh(nowTicks, snapshot.Ticks))
                    return snapshot.Metrics;

                var metrics = Sweep(nowTicks);
                Volatile.Write(ref _snapshot, new GpuSnapshot(metrics, nowTicks));
                return metrics;
            }
        }

        /// <summary>
        /// Assembles a fresh metrics object from the registry: each metric either pays
        /// for its NVAPI read or re-serves its cached value, per its own cadence, so the
        /// sweep's cost no longer scales with the number of fields. Caller must hold
        /// <see cref="_lock"/>. Each sensor is independent: one failing read never nulls
        /// the others.
        /// </summary>
        private static GpuMetrics Sweep(long nowTicks)
        {
            // The handles stay in _backend/_gpu; the read delegates pick them up from
            // there rather than taking them as arguments, so neither escapes the lock.
            if (!AcquireBackend(nowTicks))
                return new GpuMetrics();

            bool readEveryMetric = ReadEveryMetric(HighFidelityEnabled, _backend);

            // One native call carries both VRAM figures, so they always describe the
            // same instant (the wrapper's MemoryInformation issues GetMemoryInfo twice).
            var vram = _vramMetric.Get(nowTicks, readEveryMetric);

            // Likewise both clock domains: on the NVAPI fallback they come out of one
            // GetAllClockFrequencies call, so vramClock costs no extra driver work.
            var clocks = _clockMetric.Get(nowTicks, readEveryMetric);

            // ... and both power fields, so the percentage and the watts on one datagram
            // are always two views of the same milliwatt reading, not two reads apart.
            var power = _powerMetric.Get(nowTicks, readEveryMetric);

            var metrics = new GpuMetrics
            {
                Name = _nameMetric.Get(nowTicks, readEveryMetric),
                Temperature = _temperatureMetric.Get(nowTicks, readEveryMetric),
                UsagePercent = _usageMetric.Get(nowTicks, readEveryMetric),
                VramUsedMB = vram?.UsedMB,
                VramTotalMB = vram?.TotalMB,
                FanSpeedPercent = _fanMetric.Get(nowTicks, readEveryMetric),
                PowerPercent = power?.Percent,
                PowerWatts = power?.Watts,

                // Acquire-time state, not a registry read: the limit was cached when
                // the handle was acquired (it is the power percentage's denominator)
                // and only moves when the user drags the board's power slider. Never
                // populated on the NVAPI fallback, so the wire absence there is
                // structural - see push_metrics.md sections 5 and 11.
                PowerLimitWatts = DerivePowerLimitWatts(_powerLimitMilliwatts),
                CoreClockMHz = clocks?.CoreMHz,
                MemoryClockMHz = clocks?.MemoryMHz
            };

            bool sweepLost = IsHandleLost(_allMetrics);

            if (ShouldDropHandle(sweepLost, ref _consecutiveLostSweeps))
            {
                // Two sweeps in a row where nothing that ran answered: the handle is
                // stale, not the sensors. Drop it so the next sweep re-acquires
                // (rate-limited by _nextReacquireTicks) and clear the cadence caches, so
                // a GPU that comes back - possibly a different one - cannot inherit the
                // dead handle's values. The assembled object is discarded with them:
                // every value left in it is cached from a handle that has just been
                // declared dead, and absent means "unknown".
                DropBackend("every executed GPU sensor read failed twice in a row; dropping the cached handle");
                return new GpuMetrics();
            }

            // On a first strike the handle is kept and so are its cached values: the
            // sweep may just have caught a transient. Only the sensors that were read
            // this sweep are missing from what goes out.
            if (!sweepLost && _handleUnavailableLogged)
            {
                _handleUnavailableLogged = false;
                LoggingService.Info("GpuMonitorService: GPU handle reacquired");
            }

            return metrics;
        }

        /// <summary>
        /// GPU marketing name. Session cadence on the NVAPI fallback: it cannot change
        /// while a handle lives.
        /// </summary>
        private static string? ReadName()
        {
            return _backend == GpuBackend.Nvml ? NvmlService.GetName() : ReadNameNvapi();
        }

        private static string? ReadNameNvapi()
        {
            var gpu = _gpu;
            if (gpu == null)
                return null;

            try
            {
                return gpu.FullName;
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"GpuMonitorService: Failed to get GPU name: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Core temperature in degrees Celsius. Both backends run the reading through
        /// the same plausibility guard, so the wire contract's 0-150 promise does not
        /// depend on which stack answered.
        /// </summary>
        private static float? ReadTemperature()
        {
            if (_backend != GpuBackend.Nvml)
                return ReadTemperatureNvapi();

            return NvmlService.GetTemperature() is float celsius && Constants.IsValidTemperature(celsius)
                ? celsius
                : null;
        }

        /// <summary>
        /// The first NVAPI thermal sensor with a plausible reading.
        /// </summary>
        private static float? ReadTemperatureNvapi()
        {
            var gpu = _gpu;
            if (gpu == null)
                return null;

            try
            {
                var thermalInfo = gpu.ThermalInformation;
                if (thermalInfo?.ThermalSensors != null)
                {
                    foreach (var sensor in thermalInfo.ThermalSensors)
                    {
                        float temp = sensor.CurrentTemperature;
                        if (Constants.IsValidTemperature(temp))
                            return temp;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"GpuMonitorService: Failed to get GPU temperature: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// GPU utilization in percent.
        /// </summary>
        private static int? ReadUsagePercent()
        {
            return _backend == GpuBackend.Nvml ? NvmlService.GetUtilizationPercent() : ReadUsagePercentNvapi();
        }

        /// <summary>
        /// GPU utilization in percent. Prefers the single native call; the wrapper's
        /// UsageInformation property issues this call plus GetUsages on every read.
        /// </summary>
        private static int? ReadUsagePercentNvapi()
        {
            var gpu = _gpu;
            if (gpu == null)
                return null;

            try
            {
                if (!_usageLegacyFallback)
                {
                    try
                    {
                        int? percent = ToPercent(GPUApi.GetDynamicPerformanceStatesInfoEx(gpu.Handle).GPU);
                        if (percent != null)
                            return percent;

                        // The call succeeded but the driver does not populate the GPU
                        // domain (vGPU, some virtualized adapters). That is not the same
                        // statement as "no utilization data", so fall through to the
                        // legacy call - symmetric with the cooler path below. Not latched:
                        // the entry point works, so a later driver state may populate it.
                    }
                    catch (Exception ex) when (IsUnsupportedApiException(ex))
                    {
                        // Latched: one failed attempt per session, not one per sweep.
                        _usageLegacyFallback = true;
                        LoggingService.Debug($"GpuMonitorService: GetDynamicPerformanceStatesInfoEx unsupported ({ex.Message}); using GetUsages from now on");
                    }
                }

                return ToPercent(GPUApi.GetUsages(gpu.Handle).GPU);
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"GpuMonitorService: Failed to get GPU usage: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// A utilization domain's percentage, or null when the driver does not populate
        /// that domain - its Percentage is then meaningless and must not be read as 0 %.
        /// </summary>
        private static int? ToPercent(IUtilizationDomainInfo? domain)
        {
            return domain is { IsPresent: true } ? (int)domain.Percentage : null;
        }

        /// <summary>
        /// Dedicated VRAM in MB from one native read, so used and total always describe
        /// the same instant.
        /// </summary>
        private static (long UsedMB, long TotalMB)? ReadVram()
        {
            return _backend == GpuBackend.Nvml ? NvmlService.GetVramMB() : ReadVramNvapi();
        }

        private static (long UsedMB, long TotalMB)? ReadVramNvapi()
        {
            var gpu = _gpu;
            if (gpu == null)
                return null;

            try
            {
                var memInfo = GPUApi.GetMemoryInfo(gpu.Handle);
                var totalKB = memInfo.DedicatedVideoMemoryInkB;
                var availableKB = memInfo.CurrentAvailableDedicatedVideoMemoryInkB;
                var usedKB = totalKB - availableKB;

                return ((long)(usedKB / 1024), (long)(totalKB / 1024));
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"GpuMonitorService: Failed to get VRAM info: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fan level in percent, or null when the GPU reports no fan. On NVML this is
        /// fan 0 of nvmlDeviceGetFanSpeed_v2, matching the "first cooler" the NVAPI path
        /// reports; boards whose fan the driver does not expose answer NOT_SUPPORTED and
        /// land here as a silent null, retried on the next sweep.
        /// </summary>
        private static int? ReadFanSpeedPercent()
        {
            return _backend == GpuBackend.Nvml ? NvmlService.GetFanSpeedPercent() : ReadFanSpeedPercentNvapi();
        }

        /// <summary>
        /// Prefers the native client-cooler call; the wrapper's CoolerInformation probes
        /// the legacy GetCoolerSettings first, which throws on modern GPUs before
        /// falling back.
        /// </summary>
        private static int? ReadFanSpeedPercentNvapi()
        {
            var gpu = _gpu;
            if (gpu == null)
                return null;

            try
            {
                if (!_coolerLegacyFallback)
                {
                    try
                    {
                        var entries = GPUApi.GetClientFanCoolersStatus(gpu.Handle).FanCoolersStatusEntries;
                        if (entries != null && entries.Length > 0)
                            return (int)entries[0].CurrentLevel;

                        // Call succeeded but listed no client fan cooler. That is not the
                        // same statement as "this GPU has no fan", so fall through to the
                        // wrapper, which also sees the legacy cooler table.
                    }
                    catch (Exception ex) when (IsUnsupportedApiException(ex))
                    {
                        // Latched: legacy GPUs pay one exception per session, then use the
                        // wrapper path, which handles them internally.
                        _coolerLegacyFallback = true;
                        LoggingService.Debug($"GpuMonitorService: GetClientFanCoolersStatus unsupported ({ex.Message}); using CoolerInformation from now on");
                    }
                }

                return gpu.CoolerInformation?.Coolers?.FirstOrDefault()?.CurrentLevel;
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"GpuMonitorService: Failed to get fan speed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Both power fields - percentage of TDP and raw watts - from one reading.
        /// <para>
        /// On NVML both come from a single <c>nvmlDeviceGetPowerUsage</c>: the percentage
        /// divides it by the enforced limit cached at acquire time, so it is BOARD power.
        /// The NVAPI path prefers the GPU (chip) domain when the driver exposes it, so
        /// the same field can read a few percent apart between backends; same shape, same
        /// units, slightly wider domain. Documented in push_metrics.md section 4.
        /// </para>
        /// <para>
        /// NVAPI reports a percentage and nothing else, so <c>watts</c> is simply absent
        /// on the fallback - a contractual, long-lived absence rather than a failure.
        /// </para>
        /// </summary>
        private static (int? Percent, int? Watts)? ReadPower()
        {
            if (_backend != GpuBackend.Nvml)
            {
                int? nvapiPercent = ReadPowerPercentNvapi();
                return nvapiPercent == null ? null : (nvapiPercent, (int?)null);
            }

            // NOTE: v5.10.0-N3 skipped this read entirely when the enforced limit was
            // null, because without a denominator the percentage was the read's only
            // consumer and it could not be computed. Watts changed that - it needs no
            // denominator - so the read is now always worth paying for, and a board whose
            // limit query failed still reports its draw. DerivePower decides what
            // survives.
            return DerivePower(NvmlService.GetPowerMilliwatts(), _powerLimitMilliwatts);
        }

        /// <summary>
        /// The NVAPI power topology read: the single most expensive read in the fallback
        /// sweep (13.15 ms measured), hence the slowest cadence there.
        /// </summary>
        private static int? ReadPowerPercentNvapi()
        {
            var gpu = _gpu;
            if (gpu == null)
                return null;

            try
            {
                var entries = gpu.PowerTopologyInformation?.PowerTopologyEntries;
                if (entries != null)
                {
                    float? pct = null;
                    foreach (var entry in entries)
                    {
                        pct ??= entry.PowerUsageInPercent; // First entry = fallback (Board)
                        if (entry.Domain == PowerTopologyDomain.GPU)
                        {
                            pct = entry.PowerUsageInPercent; // Prefer chip power
                            break;
                        }
                    }

                    if (pct is float p && !float.IsNaN(p) && !float.IsInfinity(p) && p >= 0f && p <= MaxValidPowerPercent)
                        return (int)Math.Round(p);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"GpuMonitorService: Failed to get GPU power: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Both clock domains in MHz - graphics (core) and memory (VRAM) - read
        /// together. They travel as one metric because on the NVAPI fallback a single
        /// GetAllClockFrequencies call carries both, so the memory clock rides a call
        /// the core clock already paid for.
        /// <para>
        /// Null only when NEITHER domain answered: that is what the handle-loss rule
        /// needs to see. A tuple with one null half means the other domain is simply
        /// unavailable, which is per-field independence, not a dead handle.
        /// </para>
        /// </summary>
        private static (int? CoreMHz, int? MemoryMHz)? ReadClocks()
        {
            return _backend == GpuBackend.Nvml ? ReadClocksNvml() : ReadClocksNvapi();
        }

        private static (int? CoreMHz, int? MemoryMHz)? ReadClocksNvml()
        {
            int? core = ValidateClockMHz(NvmlService.GetCoreClockMHz(), MaxValidCoreClockMHz);
            int? memory = ValidateClockMHz(NvmlService.GetMemoryClockMHz(), MaxValidMemoryClockMHz);

            return core == null && memory == null ? null : (core, memory);
        }

        private static (int? CoreMHz, int? MemoryMHz)? ReadClocksNvapi()
        {
            var gpu = _gpu;
            if (gpu == null)
                return null;

            try
            {
                // Read once into a local: the wrapper property issues the native call on
                // every access, so touching it twice would double the fallback's cost.
                var clocks = gpu.CurrentClockFrequencies;
                int? core = ToClockMHz(clocks.GraphicsClock, MaxValidCoreClockMHz);
                int? memory = ToClockMHz(clocks.MemoryClock, MaxValidMemoryClockMHz);

                return core == null && memory == null ? null : (core, memory);
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"GpuMonitorService: Failed to get GPU clocks: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// One NVAPI clock domain in MHz, or null when the driver does not populate it.
        /// </summary>
        private static int? ToClockMHz(ClockDomainInfo clock, int maxExclusiveMHz)
        {
            return clock.IsPresent ? ValidateClockMHz((long)clock.Frequency / 1000, maxExclusiveMHz) : null; // kHz -> MHz
        }

        /// <summary>
        /// Drops every cached metric value so the next sweep reads them all again.
        /// Caller must hold <see cref="_lock"/>.
        /// </summary>
        private static void ResetMetrics()
        {
            foreach (var metric in _allMetrics)
                metric.Reset();
        }

        /// <summary>
        /// Makes sure a backend is held, acquiring one at most once per
        /// <see cref="HandleReacquireIntervalMs"/> while none is. NVML is tried first
        /// and NVAPI enumeration only if it declines; the choice is latched in
        /// <see cref="_backend"/> until the handle is dropped. Caller must hold
        /// <see cref="_lock"/>; nothing acquired here may escape it.
        /// </summary>
        /// <returns>True when a backend is available for this sweep.</returns>
        private static bool AcquireBackend(long nowTicks)
        {
            if (_backend != GpuBackend.None)
                return true;

            if (nowTicks < _nextReacquireTicks)
                return false;

            // Back off on every attempt, successful or not, so a machine whose GPU has
            // gone away does not pay for an acquisition on every sweep.
            _nextReacquireTicks = nowTicks + HandleReacquireIntervalMs;

            if (NvmlService.Initialize())
            {
                // Cached here rather than per sweep: it only moves when the user drags
                // the board's power slider, and it is the percentage's denominator.
                _powerLimitMilliwatts = NvmlService.GetEnforcedPowerLimitMilliwatts();
                _backend = GpuBackend.Nvml;
                return true;
            }

            // NvmlService latches its verdict, and a failed Initialize leaves that latch
            // set to "unavailable". Clearing it here (a no-op when nothing was loaded)
            // is what makes the next rate-limited acquire a genuine re-probe rather than
            // a cached "no" - NVML that was merely mid-driver-restart must be able to
            // come back without restarting the tray app.
            NvmlService.Shutdown();

            try
            {
                var gpus = PhysicalGPU.GetPhysicalGPUs();
                if (gpus != null && gpus.Length > 0)
                {
                    _gpu = gpus[0]; // Primary GPU
                    _backend = GpuBackend.Nvapi;
                    return true;
                }

                NoteHandleUnavailable("GPU enumeration returned no NVIDIA GPU");
            }
            catch (Exception ex)
            {
                NoteHandleUnavailable($"GPU enumeration failed: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Releases whatever backend is held and every value cached through it, so the
        /// next acquire starts from scratch - NVML first again, since a driver restart
        /// invalidates both stacks and NVML re-init is the cheaper probe. Caller must
        /// hold <see cref="_lock"/>.
        /// </summary>
        /// <param name="reason">Edge-triggered log line, or null to reset silently.</param>
        private static void DropBackend(string? reason)
        {
            if (_backend == GpuBackend.Nvml)
                NvmlService.Shutdown();

            _backend = GpuBackend.None;
            _gpu = null;
            _powerLimitMilliwatts = null;
            ResetMetrics();

            if (reason != null)
                NoteHandleUnavailable(reason);
        }

        /// <summary>
        /// Edge-triggered warning: one line per working-to-broken transition, so a GPU
        /// that stays gone cannot flood the log at the sweep rate. Cleared by the next
        /// sweep that returns data, which logs the matching recovery line.
        /// </summary>
        private static void NoteHandleUnavailable(string reason)
        {
            if (_handleUnavailableLogged)
                return;

            _handleUnavailableLogged = true;
            LoggingService.Warn($"GpuMonitorService: {reason}");
        }

        /// <summary>
        /// Shutdown and cleanup: unloads both driver stacks and resets every piece of
        /// state a restart must not inherit - the backend latch, the cached handles and
        /// power limit, the cadence caches, the snapshot and the high-fidelity holders.
        /// </summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                if (_nvidiaGpuAvailable)
                {
                    try
                    {
                        NVIDIA.Unload();
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                }

                _nvidiaGpuAvailable = false;
                _initialized = false;

                // Releases NVML if it is the live backend, clears the backend latch, the
                // cached power limit and every cadence cache: a restart must never serve
                // values sampled before it, nor inherit the stack the previous session
                // happened to pick.
                DropBackend(reason: null);

                // ... and NVML unconditionally, because DropBackend only releases it when
                // it is the LATCHED backend, and it can be live without ever having been
                // latched: ProbeNvmlAvailability leaves the layer initialized on purpose,
                // to hand it to the first sweep's AcquireBackend, and a session that never
                // swept (no consumer opened) would otherwise leak it past shutdown. On the
                // NVAPI fallback path this is a no-op - AcquireBackend already cleared the
                // layer's own latch there.
                NvmlService.Shutdown();

                _nextReacquireTicks = 0; // A restart must be able to acquire immediately, not wait out the back-off
                _handleUnavailableLogged = false;
                _consecutiveLostSweeps = 0;
                _usageLegacyFallback = false;
                _coolerLegacyFallback = false;

                // Hygiene: a holder that never got to release (a form that failed to
                // close, an assertion that threw between the paired calls) must not
                // leave every later sweep in high fidelity.
                Volatile.Write(ref _highFidelityHolders, 0);
                Volatile.Write(ref _snapshot, null);
            }

            Interlocked.Exchange(ref _initStarted, 0);
        }

        /// <summary>
        /// Probe for NVIDIA GPU at startup: NVAPI first, NVML only if NVAPI declines.
        /// </summary>
        private static void ProbeNvidiaGpu()
        {
            _nvidiaGpuAvailable = CombineProbes(ProbeNvapiAvailability(), ProbeNvmlAvailability);
        }

        /// <summary>
        /// The startup availability verdict: the GPU feature is on when EITHER stack can
        /// see the board.
        /// <para>
        /// NVAPI is asked first and the NVML probe is only reached if it declines, which
        /// is what keeps every machine that worked before behaving exactly as it did -
        /// including a machine with no NVIDIA GPU at all, which reaches the NVML probe
        /// once and pays a single failed library load for the whole session (NvmlService
        /// latches that verdict).
        /// </para>
        /// </summary>
        /// <param name="nvapiAvailable">Verdict of the NVAPI probe.</param>
        /// <param name="probeNvml">The NVML probe, run only when NVAPI declined.</param>
        /// <returns>True when the GPU feature should be enabled for this session.</returns>
        internal static bool CombineProbes(bool nvapiAvailable, Func<bool> probeNvml)
        {
            return nvapiAvailable || probeNvml();
        }

        /// <summary>
        /// Startup probe through NVML, the second opinion. It exists because gating the
        /// whole feature on NVAPI silently loses it on machines where NVML works and NVAPI
        /// does not - a TCC-mode compute board, some vGPU/virtualized adapters - even
        /// though NVML is the backend the sweep would have used anyway.
        /// <para>
        /// On success the layer is left INITIALIZED, deliberately: the first sweep's
        /// <see cref="AcquireBackend"/> then latches it through the same idempotent
        /// <c>Initialize</c> without a second load. On failure nothing is left behind - a
        /// failed init has already released the library itself, and an initialized layer
        /// that cannot answer is shut down here.
        /// </para>
        /// </summary>
        /// <returns>True when NVML loaded and the board answered a real sensor read.</returns>
        internal static bool ProbeNvmlAvailability()
        {
            // Under _lock, unlike the NVAPI probe around it: NvmlService is not
            // thread-safe by design and its contract is that every member is called under
            // this lock, and it can afford to honor that here because the whole probe is
            // one library load plus one read (sub-millisecond). The NVAPI probe is the
            // slow one this task exists to keep off the lock in the first place.
            lock (_lock)
            {
                if (!NvmlService.Initialize())
                    return false;

                // The question is "does this board answer a sensor read", not "did the
                // library load" - a loaded library with a dead board is not a usable GPU.
                // Deliberately weaker than the NVAPI probe beside it, which additionally
                // requires the reading to pass IsValidTemperature: NVML returns whole
                // degrees through a typed status code, so a non-null answer already means
                // the sensor responded.
                if (NvmlService.GetTemperature() != null)
                    return true;

                NvmlService.Shutdown();
                return false;
            }
        }

        /// <summary>
        /// Startup probe through NVAPI: enumerate, then read a plausible temperature off
        /// any GPU. Unloads NVAPI again on every path that ends in "no".
        /// </summary>
        /// <returns>True when an NVIDIA GPU answered a plausible temperature.</returns>
        private static bool ProbeNvapiAvailability()
        {
            bool nvapiInitialized = false;

            try
            {
                NVIDIA.Initialize();
                nvapiInitialized = true;

                var gpus = PhysicalGPU.GetPhysicalGPUs();
                if (gpus == null || gpus.Length == 0)
                {
                    NVIDIA.Unload();
                    return false;
                }

                // Try to read temperature from at least one GPU
                foreach (var gpu in gpus)
                {
                    try
                    {
                        var thermalInfo = gpu.ThermalInformation;
                        if (thermalInfo?.ThermalSensors == null)
                            continue;

                        foreach (var sensor in thermalInfo.ThermalSensors)
                        {
                            float temp = sensor.CurrentTemperature;
                            if (Constants.IsValidTemperature(temp))
                                return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Debug($"GpuMonitorService: Failed to probe GPU: {ex.Message}");
                    }
                }

                // No valid temperature could be read - unload NVAPI
                NVIDIA.Unload();
                return false;
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"GpuMonitorService: NVAPI probe failed: {ex.Message}");

                if (nvapiInitialized)
                {
                    try
                    {
                        NVIDIA.Unload();
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// An immutable metrics sample paired with the tick it was taken at. Published as
        /// one reference so readers can never pair a sample with the wrong timestamp.
        /// </summary>
        private sealed class GpuSnapshot
        {
            public GpuSnapshot(GpuMetrics metrics, long ticks)
            {
                Metrics = metrics;
                Ticks = ticks;
            }

            public GpuMetrics Metrics { get; }

            public long Ticks { get; }
        }
    }
}
