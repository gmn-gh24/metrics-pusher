using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

#pragma warning disable SA1402, SA1649 // File may only contain a single type - SystemMetrics is tightly coupled to SystemMetricsService

namespace MetricsPusher.Services
{
    /// <summary>
    /// Data structure for system metrics (CPU, RAM, system disk, OS health).
    /// </summary>
    internal sealed class SystemMetrics
    {
        public string? CpuName { get; set; }
        public int? CpuUsagePercent { get; set; }
        public long? RamUsedMB { get; set; }
        public long? RamTotalMB { get; set; }
        public long? DiskFreeGB { get; set; }
        public long? DiskTotalGB { get; set; }
        public string? WindowsVersion { get; set; }
        public int? AntivirusHealth { get; set; }
        public int? RebootPending { get; set; }
        public int? FirewallEnabled { get; set; }
        public long? UptimeSeconds { get; set; }

        // ---------------------------------------------------------------------------
        // NOT ON THE WIRE, AND THAT IS DELIBERATE. Read this before "fixing" it.
        //
        // The four properties below - CpuTemperature, CpuPowerWatts, CpuPowerLimitWatts
        // and NvmeTemperature - are populated on every tick by GpuDisplayPushService's
        // push loop and are intentionally NOT mapped in BuildPayload. This commit adds
        // the providers only; putting any one of them on the wire is a separate change
        // that must, in the SAME commit:
        //
        //   1. raise GpuDisplayPushService.MaxDatagramBytes (the worst case EQUALS the
        //      522-byte ceiling today - there is no slack to spend),
        //   2. re-pin the worst-case datagram test in GpuDisplayPushServiceTests, and
        //   3. update push_metrics.md - sections 3.1, 3.3, 4, 5, 6, 8.3, 8.4 and 9.
        //
        // Adding a key does not break consumers, so the protocol version stays 1; the
        // budget and the document are what move. Note also that a CPU temperature needs
        // its provenance decided first: CpuTemperatureService.Source distinguishes a die
        // reading from an ACPI board sensor, and section 5 of the protocol document has
        // to say which absence semantics apply before either can be shipped.
        //
        // They are carried on this DTO rather than in a second structure because they are
        // per-tick system metrics like every other property here, and because the moment
        // they do go on the wire, BuildPayload is where they will be read from.
        // ---------------------------------------------------------------------------

        /// <summary>CPU temperature in °C - die on Intel/AMD via PawnIO, otherwise an ACPI zone.</summary>
        public float? CpuTemperature { get; set; }

        /// <summary>CPU package power in whole watts, from the RAPL energy accumulator.</summary>
        public int? CpuPowerWatts { get; set; }

        /// <summary>CPU package power limit in whole watts. Intel only - absent on AMD is structural.</summary>
        public int? CpuPowerLimitWatts { get; set; }

        /// <summary>System disk temperature in °C, from IOCTL_STORAGE_QUERY_PROPERTY.</summary>
        public float? NvmeTemperature { get; set; }
    }

    /// <summary>
    /// Collects CPU name/usage, RAM, system-disk and OS-health metrics without WMI:
    /// the CPU name comes from the registry (read once), CPU usage from PDH's
    /// "% Processor Utility" counter (the number Task Manager shows), RAM from
    /// GlobalMemoryStatusEx, disk from DriveInfo, Windows version from the registry
    /// (read once per session), antivirus health and firewall status from the
    /// Security Center API and pending reboot from registry key existence (all cached, re-read only by
    /// <see cref="RefreshOsHealth"/>). Single consumer assumed (the display push
    /// loop at 1 Hz) - no snapshot cache; add one before wiring in a second
    /// consumer, because collecting the PDH counter much faster than ~1 Hz degrades
    /// its accuracy.
    /// </summary>
    internal static class SystemMetricsService
    {
        #region Native APIs for CPU usage (PDH) and RAM (GlobalMemoryStatusEx)

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint PdhOpenQueryW(string? szDataSource, nuint dwUserData, out IntPtr phQuery);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, nuint dwUserData, out IntPtr phCounter);

        [DllImport("pdh.dll", ExactSpelling = true)]
        private static extern uint PdhCollectQueryData(IntPtr hQuery);

        [DllImport("pdh.dll", ExactSpelling = true)]
        private static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, IntPtr lpdwType, out PDH_FMT_COUNTERVALUE pValue);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        // Security Center: one lightweight RPC returning provider health; far cheaper
        // than the WMI Defender provider (no provider host spin-up).
        [DllImport("wscapi.dll", ExactSpelling = true)]
        private static extern int WscGetSecurityProviderHealth(uint providers, out int health);

        /// <summary>
        /// PDH_FMT_COUNTERVALUE: a DWORD status followed by an 8-byte-aligned union
        /// (double / LONGLONG / pointers), hence the explicit field offsets.
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct PDH_FMT_COUNTERVALUE
        {
            [FieldOffset(0)]
            public uint CStatus;
            [FieldOffset(8)]
            public double DoubleValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint Length; // dwLength: must hold sizeof(MEMORYSTATUSEX) before the call
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
        private const uint PDH_CSTATUS_NEW_DATA = 0x00000001;

        #endregion

        // English counter path (PdhAddEnglishCounterW): works regardless of OS display
        // language, and "% Processor Utility" is the value Task Manager's CPU % shows.
        private const string CpuCounterPath = @"\Processor Information(_Total)\% Processor Utility";
        private const string CpuNameRegistryKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
        private const string CpuNameRegistryValue = "ProcessorNameString";
        private const string WindowsVersionRegistryKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

        // Pending-reboot signals: key existence only. PendingFileRenameOperations is
        // deliberately NOT checked - installers and AV leave it set chronically.
        private const string CbsRebootPendingKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending";
        private const string WuRebootRequiredKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";

        private const uint WscSecurityProviderFirewall = 0x1; // WSC_SECURITY_PROVIDER_FIREWALL
        private const uint WscSecurityProviderAntivirus = 0x4; // WSC_SECURITY_PROVIDER_ANTIVIRUS
        private const int WscResultSFalse = 1; // S_FALSE: WSC service not running; health is deliberately set to POOR
        private const int Windows11MinimumBuild = 22000;
        private const ulong BytesPerMB = 1024UL * 1024;
        private const long BytesPerGB = 1024L * 1024 * 1024;

        private enum PdhState
        {
            NotInitialized, // Query/counter not created yet
            Priming,        // Handles ready, baseline sample not established yet (retried each tick)
            Ready,          // Baseline established; collects can be formatted
            Failed,         // Structurally unusable on this machine - never retried
        }

        // GlobalMemoryStatusEx wants dwLength on every call; the size is a compile-time
        // constant of the struct, so pay Marshal.SizeOf once instead of once per second.
        private static readonly uint MemoryStatusExSize = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();

        private static readonly object _lock = new object(); // Guards all PDH state below
        private static readonly Lazy<string?> _cpuName = new Lazy<string?>(ReadCpuName);
        private static readonly Lazy<string?> _windowsVersion = new Lazy<string?>(ReadWindowsVersion);
        private static readonly Lazy<DriveInfo?> _systemDrive = new Lazy<DriveInfo?>(OpenSystemDrive);
        private static readonly Lazy<long?> _diskTotalGB = new Lazy<long?>(ReadDiskTotalGB);
        private static PdhState _pdhState;
        private static IntPtr _pdhQuery;
        private static IntPtr _pdhCounter;
        private static bool _cpuReadFailing; // Edge-triggered logging: one line per failure streak, not one per tick

        // OS-health cache: written by RefreshOsHealth (run on a background task so a
        // hung Security Center RPC can never stall the push loop), read by
        // GetSystemMetrics on the push loop. Encoded as volatile ints (-1 = unknown)
        // because Nullable<int> writes are not guaranteed atomic across threads.
        private static volatile int _avHealthCode = -1;
        private static volatile int _rebootPendingCode = -1;
        private static volatile int _firewallStatusCode = -1;
        private static volatile bool _wscUnavailable; // Latched (no Security Center on this SKU) - never retried, like PdhState.Failed
        private static int _osHealthRefreshRunning; // Interlocked gate: skip a refresh while the previous one is still blocked

        /// <summary>
        /// The cached CPU name on its own, without collecting anything else.
        /// <para>
        /// It exists for <c>CpuTemperatureService</c>, which needs the name twice at
        /// startup - to pick which PawnIO module to try first, and to resolve
        /// <c>AmdSmnTemperatureProvider</c>'s Tdie offset - and for nothing else. The
        /// alternative was calling <see cref="GetSystemMetrics"/> for one string, which
        /// would drag a PDH collect, a GlobalMemoryStatusEx and two disk reads along with
        /// it, and would perturb the CPU-usage counter's sampling interval on the way past.
        /// </para>
        /// <para>
        /// Backed by the same <c>Lazy</c> the metrics sweep uses, so the registry is still
        /// read exactly once per process no matter which of the two asks first.
        /// </para>
        /// </summary>
        internal static string? CpuName => _cpuName.Value;

        /// <summary>
        /// Reads all system metrics. Each metric fails independently (null = unavailable
        /// this call). The first call only establishes the PDH baseline sample, so
        /// <see cref="SystemMetrics.CpuUsagePercent"/> is null until the second call
        /// at least ~1 second later.
        /// </summary>
        public static SystemMetrics GetSystemMetrics()
        {
            var metrics = new SystemMetrics();
            metrics.CpuName = _cpuName.Value;
            metrics.CpuUsagePercent = ReadCpuUsagePercent();
            ReadRam(metrics);
            ReadDisk(metrics);
            metrics.WindowsVersion = _windowsVersion.Value;
            int avHealthCode = _avHealthCode;
            metrics.AntivirusHealth = avHealthCode >= 0 ? avHealthCode : null;
            int rebootPendingCode = _rebootPendingCode;
            metrics.RebootPending = rebootPendingCode >= 0 ? rebootPendingCode : null;
            int firewallStatusCode = _firewallStatusCode;
            metrics.FirewallEnabled = firewallStatusCode >= 0 ? firewallStatusCode : null;
            metrics.UptimeSeconds = Environment.TickCount64 / 1000;
            return metrics;
        }

        /// <summary>
        /// Re-reads the slow-changing OS-health values (antivirus health, firewall
        /// status, pending reboot) into the cache served by <see cref="GetSystemMetrics"/>.
        /// Synchronous - the WSC call is a cross-process RPC that can block if the
        /// Security Center service is hung, so production goes through
        /// <see cref="RefreshOsHealthInBackground"/> instead.
        /// </summary>
        public static void RefreshOsHealth()
        {
            _avHealthCode = ReadAvHealth() ?? -1;
            _firewallStatusCode = ReadFirewallStatus() ?? -1;
            _rebootPendingCode = ReadPendingReboot() ?? -1;
        }

        /// <summary>
        /// Queues <see cref="RefreshOsHealth"/> on the thread pool so the display
        /// push loop is never blocked by a slow or hung Security Center RPC. Called
        /// once before the loop and then about once a minute. If a previous refresh
        /// is still running (hung RPC), the new request is skipped rather than piling
        /// up blocked thread-pool threads.
        /// </summary>
        public static void RefreshOsHealthInBackground()
        {
            if (Interlocked.Exchange(ref _osHealthRefreshRunning, 1) == 1)
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    RefreshOsHealth();
                }
                finally
                {
                    Interlocked.Exchange(ref _osHealthRefreshRunning, 0);
                }
            });
        }

        /// <summary>
        /// Strips marketing noise from a raw ProcessorNameString - "(R)"/"(TM)" marks,
        /// clock suffixes ("@ 3.20GHz"), "CPU", "N-Core Processor", "with Radeon
        /// Graphics" - and collapses whitespace. Returns null when nothing survives.
        /// </summary>
        internal static string? NormalizeCpuName(string? rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return null;

            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            string name = Regex.Replace(rawName, @"\((R|TM)\)", string.Empty, options);
            name = Regex.Replace(name, @"@\s*\d+(\.\d+)?\s*GHz", string.Empty, options);
            name = Regex.Replace(name, @"\b\d+-Core\s+Processor\b", string.Empty, options);
            name = Regex.Replace(name, @"\bwith\s+Radeon\s+Graphics\b", string.Empty, options);
            name = Regex.Replace(name, @"\bCPU\b", string.Empty, options);
            name = Regex.Replace(name, @"\s+", " ").Trim();
            return name.Length == 0 ? null : name;
        }

        private static string? ReadCpuName()
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(CpuNameRegistryKey);
                return NormalizeCpuName(key?.GetValue(CpuNameRegistryValue) as string);
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"SystemMetricsService: Failed to read CPU name: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Formats registry values into the compact wire form ("11 23H2").
        /// ProductName is used ONLY to detect Server SKUs (client builds share Server
        /// build numbers: Server 2022 is 20348, Server 2025 is 26100) - for the 10/11
        /// split it is deliberately ignored because it still reports "Windows 10" on
        /// Windows 11, so the client major version comes from the build number.
        /// Falls back to the raw build ("11 26100") when DisplayVersion is missing;
        /// null when the build is unparsable.
        /// </summary>
        internal static string? FormatWindowsVersion(string? currentBuild, string? displayVersion, string? productName)
        {
            if (!int.TryParse(currentBuild, out int build))
                return null;

            string major = productName?.Contains("Server", StringComparison.OrdinalIgnoreCase) == true
                ? "Srv"
                : build >= Windows11MinimumBuild ? "11" : "10";
            return string.IsNullOrWhiteSpace(displayVersion) ? $"{major} {build}" : $"{major} {displayVersion}";
        }

        /// <summary>
        /// Maps a WSC_SECURITY_PROVIDER_HEALTH value to the wire tri-state:
        /// 0 = green (GOOD), 1 = yellow (NOTMONITORED or SNOOZE), 2 = red (POOR);
        /// null for anything unrecognized.
        /// </summary>
        internal static int? MapAvHealth(int wscHealth)
        {
            return wscHealth switch
            {
                0 => 0, // WSC_SECURITY_PROVIDER_HEALTH_GOOD
                1 => 1, // WSC_SECURITY_PROVIDER_HEALTH_NOTMONITORED
                3 => 1, // WSC_SECURITY_PROVIDER_HEALTH_SNOOZE
                2 => 2, // WSC_SECURITY_PROVIDER_HEALTH_POOR
                _ => null,
            };
        }

        /// <summary>
        /// Maps a WSC_SECURITY_PROVIDER_HEALTH value for the firewall provider to the
        /// wire boolean: 1 = enabled/OK (GOOD), 0 = disabled or at risk (NOTMONITORED,
        /// SNOOZE or POOR); null for anything unrecognized.
        /// </summary>
        internal static int? MapFirewallStatus(int wscHealth)
        {
            return wscHealth switch
            {
                0 => 1, // WSC_SECURITY_PROVIDER_HEALTH_GOOD
                1 => 0, // WSC_SECURITY_PROVIDER_HEALTH_NOTMONITORED
                3 => 0, // WSC_SECURITY_PROVIDER_HEALTH_SNOOZE
                2 => 0, // WSC_SECURITY_PROVIDER_HEALTH_POOR
                _ => null,
            };
        }

        /// <summary>
        /// Pending-reboot detection over an abstract key-existence probe so tests can
        /// supply fakes: 1 when either signal key exists, else 0.
        /// </summary>
        internal static int DetectPendingReboot(Func<string, bool> subKeyExists)
        {
            return subKeyExists(CbsRebootPendingKey) || subKeyExists(WuRebootRequiredKey) ? 1 : 0;
        }

        private static string? ReadWindowsVersion()
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(WindowsVersionRegistryKey);
                return FormatWindowsVersion(
                    key?.GetValue("CurrentBuild") as string,
                    key?.GetValue("DisplayVersion") as string,
                    key?.GetValue("ProductName") as string);
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"SystemMetricsService: Failed to read Windows version: {ex.Message}");
                return null;
            }
        }

        private static int? ReadAvHealth()
        {
            if (_wscUnavailable)
                return null;

            try
            {
                int status = WscGetSecurityProviderHealth(WscSecurityProviderAntivirus, out int health);

                // S_OK: health holds the provider state. S_FALSE: the Security Center
                // service itself is not running and the API deliberately reports POOR -
                // that IS the red signal, not a failure to discard.
                return status == 0 || status == WscResultSFalse ? MapAvHealth(health) : null;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _wscUnavailable = true;
                LoggingService.Debug($"SystemMetricsService: antivirus health unavailable on this SKU: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"SystemMetricsService: Failed to read antivirus health: {ex.Message}");
                return null;
            }
        }

        private static int? ReadFirewallStatus()
        {
            if (_wscUnavailable)
                return null;

            try
            {
                int status = WscGetSecurityProviderHealth(WscSecurityProviderFirewall, out int health);

                // Same contract as ReadAvHealth: S_FALSE means the Security Center
                // service is down and health deliberately reads POOR - an at-risk
                // signal to report as 0, not a failure to discard.
                return status == 0 || status == WscResultSFalse ? MapFirewallStatus(health) : null;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _wscUnavailable = true;
                LoggingService.Debug($"SystemMetricsService: firewall status unavailable on this SKU: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"SystemMetricsService: Failed to read firewall status: {ex.Message}");
                return null;
            }
        }

        private static int? ReadPendingReboot()
        {
            try
            {
                return DetectPendingReboot(SubKeyExists);
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"SystemMetricsService: Failed to read pending-reboot state: {ex.Message}");
                return null;
            }
        }

        private static bool SubKeyExists(string subKey)
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(subKey);
            return key != null;
        }

        private static int? ReadCpuUsagePercent()
        {
            lock (_lock)
            {
                try
                {
                    if (_pdhState == PdhState.Failed)
                        return null;

                    if (_pdhState == PdhState.NotInitialized)
                        CreateCpuCounter();

                    if (_pdhState == PdhState.Priming)
                    {
                        // Baseline sample. This can legitimately fail right after logon while
                        // perflib is still coming up, so it is retried on later calls instead
                        // of disabling the counter for the session.
                        uint baselineStatus = PdhCollectQueryData(_pdhQuery);
                        if (baselineStatus != 0)
                            return NoCpuValueThisTick($"baseline PdhCollectQueryData returned 0x{baselineStatus:X8}");

                        _pdhState = PdhState.Ready;
                        return null; // A rate needs a second sample ~1 second from now
                    }

                    if (_pdhState != PdhState.Ready)
                        return null;

                    uint status = PdhCollectQueryData(_pdhQuery);
                    if (status != 0)
                        return NoCpuValueThisTick($"PdhCollectQueryData returned 0x{status:X8}");

                    status = PdhGetFormattedCounterValue(_pdhCounter, PDH_FMT_DOUBLE, IntPtr.Zero, out PDH_FMT_COUNTERVALUE value);
                    if (status != 0)
                        return NoCpuValueThisTick($"PdhGetFormattedCounterValue returned 0x{status:X8}");

                    // A successful return can still carry unusable data - CStatus is authoritative
                    if (value.CStatus != PDH_CSTATUS_VALID_DATA && value.CStatus != PDH_CSTATUS_NEW_DATA)
                        return NoCpuValueThisTick($"counter CStatus 0x{value.CStatus:X8}");

                    if (_cpuReadFailing)
                    {
                        _cpuReadFailing = false;
                        LoggingService.Debug("SystemMetricsService: CPU usage read recovered");
                    }

                    // PDH caps formatted values at 100 by default (no PDH_FMT_NOCAP100),
                    // matching Task Manager; the clamp only guards the int cast.
                    return Math.Clamp((int)Math.Round(value.DoubleValue), 0, 100);
                }
                catch (Exception ex)
                {
                    // DllNotFoundException and friends - PDH is unusable on this machine
                    _pdhState = PdhState.Failed;
                    LoggingService.Debug($"SystemMetricsService: CPU usage disabled: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Opens the PDH query and adds the CPU counter. Failures here are structural
        /// (no PDH, unknown counter path) rather than timing-related, so they disable
        /// the counter for the session. The query handle is deliberately never closed:
        /// it lives for the whole process and the OS reclaims it at exit - closing it
        /// from shutdown paths is riskier than not closing it. A handle opened before a
        /// failed add is likewise left to process teardown (one-time, on a machine where
        /// the counter never works anyway).
        /// </summary>
        private static void CreateCpuCounter()
        {
            uint status = PdhOpenQueryW(null, 0, out IntPtr query);
            if (status != 0)
            {
                _pdhState = PdhState.Failed;
                LoggingService.Debug($"SystemMetricsService: PdhOpenQuery failed with 0x{status:X8}; CPU usage disabled");
                return;
            }

            status = PdhAddEnglishCounterW(query, CpuCounterPath, 0, out IntPtr counter);
            if (status != 0)
            {
                _pdhState = PdhState.Failed;
                LoggingService.Debug($"SystemMetricsService: PdhAddEnglishCounter failed with 0x{status:X8}; CPU usage disabled");
                return;
            }

            _pdhQuery = query;
            _pdhCounter = counter;
            _pdhState = PdhState.Priming;
        }

        /// <summary>
        /// Establishes the PDH baseline sample so a call roughly a second later can
        /// already report CPU usage (a rate counter needs two samples ~1s apart).
        /// </summary>
        public static void PrimeCpuCounter()
        {
            _ = ReadCpuUsagePercent();
        }

        private static int? NoCpuValueThisTick(string reason)
        {
            if (!_cpuReadFailing)
            {
                _cpuReadFailing = true;
                LoggingService.Debug($"SystemMetricsService: CPU usage unavailable: {reason}");
            }

            return null;
        }

        private static void ReadRam(SystemMetrics metrics)
        {
            try
            {
                var status = default(MEMORYSTATUSEX);
                status.Length = MemoryStatusExSize;
                if (!GlobalMemoryStatusEx(ref status))
                {
                    LoggingService.Debug($"SystemMetricsService: GlobalMemoryStatusEx failed with error {Marshal.GetLastWin32Error()}");
                    return;
                }

                metrics.RamTotalMB = (long)(status.TotalPhys / BytesPerMB);
                metrics.RamUsedMB = (long)((status.TotalPhys - status.AvailPhys) / BytesPerMB);
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"SystemMetricsService: Failed to read RAM info: {ex.Message}");
            }
        }

        private static DriveInfo? OpenSystemDrive()
        {
            try
            {
                // SystemDirectory is always rooted, so GetPathRoot cannot return null here
                return new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"SystemMetricsService: Failed to open the system drive: {ex.Message}");
                return null;
            }
        }

        private static long? ReadDiskTotalGB()
        {
            try
            {
                return _systemDrive.Value?.TotalSize / BytesPerGB;
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"SystemMetricsService: Failed to read disk capacity: {ex.Message}");
                return null;
            }
        }

        private static void ReadDisk(SystemMetrics metrics)
        {
            // Drive root and capacity never change for the process lifetime; only the
            // free-space figure is re-read, keeping this to one syscall per call.
            metrics.DiskTotalGB = _diskTotalGB.Value;

            try
            {
                metrics.DiskFreeGB = _systemDrive.Value?.AvailableFreeSpace / BytesPerGB;
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"SystemMetricsService: Failed to read free disk space: {ex.Message}");
            }
        }
    }
}
