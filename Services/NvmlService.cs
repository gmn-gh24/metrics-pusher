using System.Runtime.InteropServices;
using System.Text;

namespace MetricsPusher.Services
{
    /// <summary>
    /// Thin P/Invoke layer over NVIDIA's NVML (<c>nvml.dll</c>, which every NVIDIA driver
    /// installs into System32 - a public, documented API, not a private one). It reads the
    /// same sensors <see cref="GpuMonitorService"/> reads through NVAPI, an order of
    /// magnitude cheaper per call, and exists to become that service's primary source.
    /// <para>
    /// Per-field independence is the contract: every getter returns null when the layer is
    /// not initialized and when NVML answers with a nonzero <c>nvmlReturn_t</c>, no getter
    /// throws, and no getter latches - a transient failure is retried on the next call.
    /// Only <see cref="Initialize"/> latches, so a machine without NVML pays exactly one
    /// failed load attempt per session instead of one per tick.
    /// </para>
    /// <para>
    /// Units are converted at this boundary and nowhere else: temperature in whole degrees
    /// Celsius as a float, VRAM in MB (NVML reports bytes), clocks in MHz. Power stays as
    /// NVML reports it - raw milliwatts, draw and enforced limit alike - because deriving a
    /// percentage from the pair is the caller's business logic, not this layer's.
    /// </para>
    /// <para>
    /// NOT thread-safe, by design: the latch and the device handle are plain static fields.
    /// Callers must serialize every member of this class; in the tray app that means
    /// calling it only under <c>GpuMonitorService._lock</c>, the lock that already
    /// serializes the NVAPI reads on the same path. A second lock here would guard the same
    /// call path twice.
    /// </para>
    /// </summary>
    internal static class NvmlService
    {
        #region Native NVML entry points

        // nvml.h declares no calling convention on Windows, which means cdecl. Irrelevant
        // on x64 (one convention exists), spelled out so an x86 build cannot silently
        // unbalance the stack. ExactSpelling stops the marshaler probing for "...A" names.
        private const string NvmlLibrary = "nvml.dll";
        private const CallingConvention NvmlCall = CallingConvention.Cdecl;

        [DllImport(NvmlLibrary, EntryPoint = "nvmlInit_v2", CallingConvention = NvmlCall, ExactSpelling = true)]
        private static extern int NvmlInit();

        [DllImport(NvmlLibrary, EntryPoint = "nvmlShutdown", CallingConvention = NvmlCall, ExactSpelling = true)]
        private static extern int NvmlShutdown();

        [DllImport(NvmlLibrary, EntryPoint = "nvmlDeviceGetHandleByIndex_v2", CallingConvention = NvmlCall, ExactSpelling = true)]
        private static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

        // char* buffer, ASCII, NUL-terminated. A blittable array is pinned rather than
        // copied, and [Out] documents that the callee is the one filling it.
        [DllImport(NvmlLibrary, EntryPoint = "nvmlDeviceGetName", CallingConvention = NvmlCall, ExactSpelling = true)]
        private static extern int NvmlDeviceGetName(IntPtr device, [Out] byte[] name, uint length);

        [DllImport(NvmlLibrary, EntryPoint = "nvmlDeviceGetTemperature", CallingConvention = NvmlCall, ExactSpelling = true)]
        private static extern int NvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temperatureC);

        [DllImport(NvmlLibrary, EntryPoint = "nvmlDeviceGetUtilizationRates", CallingConvention = NvmlCall, ExactSpelling = true)]
        private static extern int NvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

        [DllImport(NvmlLibrary, EntryPoint = "nvmlDeviceGetMemoryInfo", CallingConvention = NvmlCall, ExactSpelling = true)]
        private static extern int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

        [DllImport(NvmlLibrary, EntryPoint = "nvmlDeviceGetFanSpeed_v2", CallingConvention = NvmlCall, ExactSpelling = true)]
        private static extern int NvmlDeviceGetFanSpeed(IntPtr device, uint fan, out uint speedPercent);

        [DllImport(NvmlLibrary, EntryPoint = "nvmlDeviceGetClockInfo", CallingConvention = NvmlCall, ExactSpelling = true)]
        private static extern int NvmlDeviceGetClockInfo(IntPtr device, uint clockType, out uint clockMHz);

        [DllImport(NvmlLibrary, EntryPoint = "nvmlDeviceGetPowerUsage", CallingConvention = NvmlCall, ExactSpelling = true)]
        private static extern int NvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

        [DllImport(NvmlLibrary, EntryPoint = "nvmlDeviceGetEnforcedPowerLimit", CallingConvention = NvmlCall, ExactSpelling = true)]
        private static extern int NvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint limitMilliwatts);

        /// <summary>
        /// nvmlUtilization_t: two consecutive unsigned ints, gpu first. The percentages are
        /// sampled over the driver's own window, so both fields describe one instant.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlUtilization
        {
            public uint Gpu;
            public uint Memory;
        }

        /// <summary>
        /// nvmlMemory_t: three consecutive unsigned long longs in BYTES, in header order
        /// total, free, used. Deliberately the v1 struct that plain
        /// <c>nvmlDeviceGetMemoryInfo</c> fills - the v2 variant is a different, versioned
        /// struct behind a different entry point, and passing one to the other's function
        /// is how this call silently returns garbage.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlMemory
        {
            public ulong Total;
            public ulong Free;
            public ulong Used;
        }

        #endregion

        private const int NvmlSuccess = 0; // NVML_SUCCESS
        private const int NvmlErrorNotSupported = 3; // NVML_ERROR_NOT_SUPPORTED
        private const uint PrimaryDeviceIndex = 0;
        private const uint TemperatureSensorGpu = 0; // NVML_TEMPERATURE_GPU
        private const uint ClockGraphics = 0; // NVML_CLOCK_GRAPHICS (1 = SM, 3 = VIDEO)
        private const uint ClockMemory = 2; // NVML_CLOCK_MEM

        // Fan 0, matching the "first cooler" the NVAPI path reports, and half the cost of
        // the v1 entry point, which aggregates every fan on the board.
        private const uint FirstFanIndex = 0;

        private const uint NameBufferSize = 96; // NVML_DEVICE_NAME_V2_BUFFER_SIZE
        private const ulong BytesPerMB = 1024UL * 1024;

        private static bool _initAttempted; // Latch: the probe runs once per session
        private static bool _available; // nvmlInit_v2 and the device-0 handle both succeeded
        private static IntPtr _device; // nvmlDevice_t: opaque, owned by NVML, invalid after nvmlShutdown
        private static bool _readFailureLogged; // Edge trigger, so a dead sensor logs once instead of once per tick

        // Edge trigger for the initialization failures, and deliberately NOT reset by
        // Shutdown: GpuMonitorService.AcquireBackend calls Shutdown after every failed
        // Initialize precisely to clear the "already attempted" latch, so a machine where
        // both stacks are dead re-enters Initialize every ~5 s forever. Resetting this
        // flag there would turn that retry loop into an unbounded Debug-log append. Only a
        // successful Initialize clears it, which is exactly "one line per failure streak".
        private static bool _initFailureLogged;

        /// <summary>
        /// Gets a value indicating whether NVML loaded and handed out a device handle.
        /// False until <see cref="Initialize"/> has run, and again after
        /// <see cref="Shutdown"/>.
        /// </summary>
        internal static bool IsAvailable => _available;

        /// <summary>
        /// Loads NVML and takes a handle on device 0. Idempotent: the verdict is latched,
        /// so later calls return it without touching the library again. Never throws - a
        /// missing driver, an entry point an older driver does not export, or any nonzero
        /// <c>nvmlReturn_t</c> all latch "unavailable" and every getter then returns null.
        /// <para>
        /// Its failure diagnostics are edge-triggered per failure streak (see
        /// <see cref="_initFailureLogged"/>): the caller is allowed to retry this forever,
        /// so one line per streak is the difference between a diagnostic and a log that
        /// grows every five seconds until the disk fills.
        /// </para>
        /// </summary>
        /// <returns>True when NVML is usable.</returns>
        internal static bool Initialize()
        {
            if (_initAttempted)
                return _available;

            _initAttempted = true;
            bool libraryInitialized = false;

            try
            {
                int status = NvmlInit();
                if (status != NvmlSuccess)
                {
                    NoteInitFailure($"NvmlService: nvmlInit_v2 returned nvmlReturn {status}; NVML unavailable");
                    return false;
                }

                libraryInitialized = true;

                status = NvmlDeviceGetHandleByIndex(PrimaryDeviceIndex, out IntPtr device);
                if (status != NvmlSuccess)
                {
                    NoteInitFailure($"NvmlService: nvmlDeviceGetHandleByIndex_v2 returned nvmlReturn {status}; NVML unavailable");
                    ReleaseLibrary();
                    return false;
                }

                _device = device;
                _available = true;

                // Ends the failure streak: a later genuine failure gets its own line.
                _initFailureLogged = false;
                LoggingService.Info("NvmlService: NVML initialized on device 0");
                return true;
            }
            catch (Exception ex)
            {
                // DllNotFoundException (no NVIDIA driver on the box),
                // EntryPointNotFoundException (a driver too old to export one of these) and
                // BadImageFormatException (32-bit process against the 64-bit nvml.dll) all
                // mean the same thing here, and all of them arrive on the first call.
                NoteInitFailure($"NvmlService: NVML unavailable: {ex.Message}");

                if (libraryInitialized)
                    ReleaseLibrary();

                return false;
            }
        }

        /// <summary>
        /// Releases NVML and resets every field, including the latch, so a later
        /// <see cref="Initialize"/> probes again from scratch. Safe to call when
        /// initialization never ran or already failed.
        /// </summary>
        internal static void Shutdown()
        {
            // _available is exactly "an nvmlShutdown is still owed": Initialize releases
            // the library itself on every path that leaves the layer unavailable. It is
            // cleared BEFORE the unload rather than after: identical single-threaded,
            // but it narrows the window in which a caller that ignored the
            // serialize-every-member contract could read through a handle whose library
            // is already going away.
            bool unloadOwed = _available;
            _available = false;

            if (unloadOwed)
                ReleaseLibrary();

            _device = IntPtr.Zero; // The handle belongs to the unloaded library; reading through it is undefined
            _initAttempted = false;
            _readFailureLogged = false;
        }

        /// <summary>
        /// Marketing name of the device ("NVIDIA GeForce RTX 3090 Ti"), or null when
        /// unavailable.
        /// </summary>
        internal static string? GetName()
        {
            if (!_available)
                return null;

            try
            {
                byte[] buffer = new byte[NameBufferSize];
                int status = NvmlDeviceGetName(_device, buffer, NameBufferSize);
                if (status != NvmlSuccess)
                {
                    NoteReadFailure("nvmlDeviceGetName", status);
                    return null;
                }

                // NUL-terminated C string: everything past the terminator is uninitialized
                // buffer, and a driver that filled the buffer completely omits it.
                int end = Array.IndexOf(buffer, (byte)0);
                if (end < 0)
                    end = buffer.Length;

                string name = Encoding.ASCII.GetString(buffer, 0, end).Trim();
                return name.Length == 0 ? null : name;
            }
            catch (Exception ex)
            {
                NoteReadFailure("nvmlDeviceGetName", ex);
                return null;
            }
        }

        /// <summary>
        /// Core temperature in degrees Celsius (NVML_TEMPERATURE_GPU), or null when
        /// unavailable. NVML reports whole degrees; float is the unit temperatures travel
        /// in through this codebase.
        /// </summary>
        internal static float? GetTemperature()
        {
            if (!_available)
                return null;

            try
            {
                int status = NvmlDeviceGetTemperature(_device, TemperatureSensorGpu, out uint temperatureC);
                if (status != NvmlSuccess)
                {
                    NoteReadFailure("nvmlDeviceGetTemperature", status);
                    return null;
                }

                return temperatureC;
            }
            catch (Exception ex)
            {
                NoteReadFailure("nvmlDeviceGetTemperature", ex);
                return null;
            }
        }

        /// <summary>
        /// GPU utilization in percent (the gpu field of nvmlUtilization_t, not the memory
        /// one), or null when unavailable.
        /// </summary>
        internal static int? GetUtilizationPercent()
        {
            if (!_available)
                return null;

            try
            {
                int status = NvmlDeviceGetUtilizationRates(_device, out NvmlUtilization utilization);
                if (status != NvmlSuccess)
                {
                    NoteReadFailure("nvmlDeviceGetUtilizationRates", status);
                    return null;
                }

                return (int)utilization.Gpu;
            }
            catch (Exception ex)
            {
                NoteReadFailure("nvmlDeviceGetUtilizationRates", ex);
                return null;
            }
        }

        /// <summary>
        /// Used and total device memory in MB, or null when unavailable. One native read
        /// carries both figures, so they always describe the same instant and used can
        /// never exceed total.
        /// </summary>
        internal static (long UsedMB, long TotalMB)? GetVramMB()
        {
            if (!_available)
                return null;

            try
            {
                int status = NvmlDeviceGetMemoryInfo(_device, out NvmlMemory memory);
                if (status != NvmlSuccess)
                {
                    NoteReadFailure("nvmlDeviceGetMemoryInfo", status);
                    return null;
                }

                return ((long)(memory.Used / BytesPerMB), (long)(memory.Total / BytesPerMB));
            }
            catch (Exception ex)
            {
                NoteReadFailure("nvmlDeviceGetMemoryInfo", ex);
                return null;
            }
        }

        /// <summary>
        /// Level of the first fan in percent, or null when unavailable. Zero is a value,
        /// not an absence: an idle board under zero-RPM fan control reports 0 %.
        /// </summary>
        internal static int? GetFanSpeedPercent()
        {
            if (!_available)
                return null;

            try
            {
                int status = NvmlDeviceGetFanSpeed(_device, FirstFanIndex, out uint speedPercent);
                if (status != NvmlSuccess)
                {
                    NoteReadFailure("nvmlDeviceGetFanSpeed_v2", status);
                    return null;
                }

                return (int)speedPercent;
            }
            catch (Exception ex)
            {
                NoteReadFailure("nvmlDeviceGetFanSpeed_v2", ex);
                return null;
            }
        }

        /// <summary>
        /// Graphics (core) clock in MHz, or null when unavailable.
        /// </summary>
        internal static int? GetCoreClockMHz()
        {
            return GetClockMHz(ClockGraphics);
        }

        /// <summary>
        /// Memory clock in MHz, or null when unavailable.
        /// </summary>
        internal static int? GetMemoryClockMHz()
        {
            return GetClockMHz(ClockMemory);
        }

        /// <summary>
        /// Board power draw in milliwatts, exactly as NVML reports it, or null when
        /// unavailable.
        /// </summary>
        internal static uint? GetPowerMilliwatts()
        {
            if (!_available)
                return null;

            try
            {
                int status = NvmlDeviceGetPowerUsage(_device, out uint milliwatts);
                if (status != NvmlSuccess)
                {
                    NoteReadFailure("nvmlDeviceGetPowerUsage", status);
                    return null;
                }

                return milliwatts;
            }
            catch (Exception ex)
            {
                NoteReadFailure("nvmlDeviceGetPowerUsage", ex);
                return null;
            }
        }

        /// <summary>
        /// The power limit the board is currently enforcing, in milliwatts, or null when
        /// unavailable. Same unit as <see cref="GetPowerMilliwatts"/>, which is what makes
        /// the pair divisible.
        /// </summary>
        internal static uint? GetEnforcedPowerLimitMilliwatts()
        {
            if (!_available)
                return null;

            try
            {
                int status = NvmlDeviceGetEnforcedPowerLimit(_device, out uint limitMilliwatts);
                if (status != NvmlSuccess)
                {
                    NoteReadFailure("nvmlDeviceGetEnforcedPowerLimit", status);
                    return null;
                }

                return limitMilliwatts;
            }
            catch (Exception ex)
            {
                NoteReadFailure("nvmlDeviceGetEnforcedPowerLimit", ex);
                return null;
            }
        }

        /// <summary>
        /// One clock domain in MHz. The two public clock getters differ only in the
        /// nvmlClockType_t they pass, so they share this body rather than the constant
        /// being duplicated at two call sites.
        /// </summary>
        private static int? GetClockMHz(uint clockType)
        {
            if (!_available)
                return null;

            try
            {
                int status = NvmlDeviceGetClockInfo(_device, clockType, out uint clockMHz);
                if (status != NvmlSuccess)
                {
                    NoteReadFailure("nvmlDeviceGetClockInfo", status);
                    return null;
                }

                return (int)clockMHz;
            }
            catch (Exception ex)
            {
                NoteReadFailure("nvmlDeviceGetClockInfo", ex);
                return null;
            }
        }

        /// <summary>
        /// Unloads NVML, tolerating any failure: this runs on teardown paths where there
        /// is nothing useful left to do about one.
        /// </summary>
        private static void ReleaseLibrary()
        {
            try
            {
                int status = NvmlShutdown();
                if (status != NvmlSuccess)
                    LoggingService.Debug($"NvmlService: nvmlShutdown returned nvmlReturn {status}");
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"NvmlService: nvmlShutdown failed: {ex.Message}");
            }
        }

        /// <summary>
        /// True for the one status that says "this board/driver will never answer this
        /// query" rather than "this query failed". It is a permanent property of the
        /// hardware - a laptop GPU whose fan the driver does not expose answers
        /// NVML_ERROR_NOT_SUPPORTED on every single tick - so it must be a silent null:
        /// see <see cref="NoteReadFailure(string, int)"/> for what it would otherwise cost.
        /// </summary>
        internal static bool IsUnsupportedStatus(int status)
        {
            return status == NvmlErrorNotSupported;
        }

        /// <summary>
        /// The edge-trigger primitive both diagnostics below are built from: returns true
        /// for the first failure of a streak and false for every repeat, flipping the
        /// caller's flag as it goes. Every failure this layer can report is one the caller
        /// retries forever - a sensor at 1 Hz, an initialization every 5 s - so "log it
        /// every time" is never an option here; what differs between the two is only when
        /// the streak is considered over (a successful init; a
        /// <see cref="Shutdown"/> for reads).
        /// </summary>
        /// <param name="alreadyLogged">The streak flag; set to true by the first call.</param>
        /// <returns>True when this failure is the one worth a log line.</returns>
        internal static bool ShouldLogFailure(ref bool alreadyLogged)
        {
            if (alreadyLogged)
                return false;

            alreadyLogged = true;
            return true;
        }

        /// <summary>
        /// Edge-triggered diagnostics for a failed <see cref="Initialize"/>. One line per
        /// failure streak: the caller re-probes at its own re-acquire cadence and clears
        /// the "already attempted" latch every time it does, so an unrecovered driver loss
        /// would otherwise append this line forever.
        /// </summary>
        private static void NoteInitFailure(string message)
        {
            if (ShouldLogFailure(ref _initFailureLogged))
                LoggingService.Debug(message);
        }

        /// <summary>
        /// Edge-triggered diagnostics for a failed read: one line per session, because a
        /// sensor this driver does not support answers the same way on every tick and the
        /// caller polls at 1 Hz. Deliberately shared by every getter - the first failure is
        /// the one worth a log line; the layer's job afterwards is to keep returning null
        /// cheaply, and the policy for reacting to that belongs to the caller. Reset by
        /// <see cref="Shutdown"/>.
        /// <para>
        /// NVML_ERROR_NOT_SUPPORTED never consumes that one line. It arrives on the very
        /// first tick of a machine whose board lacks the sensor, and burning the session's
        /// only diagnostic on a permanent, expected condition would silence the failure
        /// that actually matters later - NVML_ERROR_GPU_IS_LOST during a driver restart,
        /// which is exactly what <c>GpuMonitorService</c>'s handle-loss rule reacts to.
        /// </para>
        /// </summary>
        private static void NoteReadFailure(string entryPoint, int status)
        {
            if (IsUnsupportedStatus(status))
                return;

            if (ShouldLogFailure(ref _readFailureLogged))
                LoggingService.Debug($"NvmlService: {entryPoint} returned nvmlReturn {status}; further NVML read failures are not logged this session");
        }

        /// <summary>
        /// Edge-triggered diagnostics for a read that threw rather than returning a status:
        /// an entry point missing from an older driver, found only when it is first called.
        /// </summary>
        private static void NoteReadFailure(string entryPoint, Exception ex)
        {
            if (ShouldLogFailure(ref _readFailureLogged))
                LoggingService.Debug($"NvmlService: {entryPoint} failed: {ex.Message}; further NVML read failures are not logged this session");
        }
    }
}
