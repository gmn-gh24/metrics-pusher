using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

#pragma warning disable SA1011 // Closing square bracket should be followed by a space - StyleCop 1.1.118 predates nullable reference types and reads "byte[]?" as a spacing error

namespace MetricsPusher.Services
{
    /// <summary>
    /// Temperature of the SSD holding the system volume, read straight from the storage
    /// stack with <c>IOCTL_STORAGE_QUERY_PROPERTY</c>. Unlike the CPU sensors this needs no
    /// driver and no elevation, so it works on a stock machine the moment the app runs.
    /// <para>
    /// The whole trick is the access mask. <c>CreateFileW</c>'s "Physical Disks and Volumes"
    /// rules demand administrative privilege only for direct-access (DASD) read/write
    /// handles; <c>dwDesiredAccess = 0</c> buys a handle that can query device attributes
    /// and nothing else, and <c>IOCTL_STORAGE_QUERY_PROPERTY</c> is declared
    /// <c>FILE_ANY_ACCESS</c>, so the I/O manager demands no read/write right on it.
    /// Measured on the dev box: both tiers below answer identically from a non-elevated
    /// process, while the convenient WMI equivalent (<c>Get-StorageReliabilityCounter</c>)
    /// is access-denied. Asking for any access right at all is what would break this.
    /// </para>
    /// <para>
    /// Two tiers, chosen once at init on the same handle because some drivers implement one
    /// and not the other: <c>StorageDeviceTemperatureProperty</c> first, whose
    /// <c>Temperature</c> is already signed degrees Celsius and answers for SATA devices
    /// too, then the NVMe SMART / Health Information log page, whose composite temperature
    /// is Kelvin. Neither answering is an ordinary negative - RAID and HBA controllers,
    /// USB bridges and VMs all report nothing - so it latches and falls silent after one
    /// line rather than being treated as an error.
    /// </para>
    /// <para>
    /// Thread-safety: a private lock, unlike <see cref="NvmlService"/>'s "the caller
    /// serializes" contract. Not because <see cref="TryRead"/> is contended - it is called
    /// from the single 1 Hz push tick - but because <see cref="Dispose"/> arrives from the
    /// tray teardown path, on a different thread, and would otherwise be free to close the
    /// handle and swap the shared query buffer out from under an in-flight
    /// <c>DeviceIoControl</c>. The lock makes teardown wait for the tick in progress; the
    /// <see cref="SafeFileHandle"/> is the second half of the same guarantee.
    /// </para>
    /// </summary>
    internal sealed class NvmeTemperatureService : IDisposable
    {
        #region Native storage IOCTLs

        // CreateFileW with dwDesiredAccess 0: see the class remarks. FILE_SHARE_READ |
        // FILE_SHARE_WRITE because the volume is mounted and in use - anything narrower
        // fails with a sharing violation on the running system disk.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        // METHOD_BUFFERED on both control codes, so the kernel copies in and out and the
        // byte[] parameters only have to survive the call. Blittable arrays are pinned
        // rather than copied, which is what keeps the poll allocation-free.
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            byte[]? lpInBuffer,
            uint nInBufferSize,
            byte[]? lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        // CTL_CODE(IOCTL_STORAGE_BASE = FILE_DEVICE_MASS_STORAGE = 0x2D, 0x0500,
        // METHOD_BUFFERED = 0, FILE_ANY_ACCESS = 0). FILE_ANY_ACCESS is the half of the
        // access-mask story the class remarks describe.
        private const uint IoctlStorageQueryProperty = 0x002D1400;

        // CTL_CODE(IOCTL_VOLUME_BASE = 'V' = 0x56, 0x0000, METHOD_BUFFERED, FILE_ANY_ACCESS)
        private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

        private const uint DesiredAccessNone = 0;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;

        // STORAGE_PROPERTY_ID. The enum is NOT sequential - it jumps to 48 at
        // StorageDeviceIoCapabilityProperty - so these come from the header by value, never
        // by counting entries.
        private const uint StorageDeviceProtocolSpecificProperty = 50;
        private const uint StorageDeviceTemperatureProperty = 52;
        private const uint PropertyStandardQuery = 0; // STORAGE_QUERY_TYPE

        private const uint ProtocolTypeNvme = 3; // STORAGE_PROTOCOL_TYPE
        private const uint NvmeDataTypeLogPage = 2; // STORAGE_PROTOCOL_NVME_DATA_TYPE
        private const uint NvmeLogPageHealthInfo = 0x02; // SMART / Health Information

        // sizeof(STORAGE_PROPERTY_QUERY): PropertyId and QueryType are 4 bytes each and
        // AdditionalParameters[1] rounds the struct up to 12. Sending the 8 bytes the two
        // enums actually occupy is rejected with ERROR_BAD_LENGTH - measured, and a silent
        // trap because the request otherwise looks complete.
        private const int StoragePropertyQuerySize = 12;
        private const int AdditionalParametersOffset = 8;

        // STORAGE_TEMPERATURE_DATA_DESCRIPTOR: Version, Size, CriticalTemperature,
        // WarningTemperature, InfoCount, Reserved0[2], Reserved1[2] = 24 bytes, then
        // 16-byte STORAGE_TEMPERATURE_INFO entries whose Temperature is the second field.
        private const int TemperatureDescriptorHeaderSize = 24;
        private const int TemperatureInfoCountOffset = 12;
        private const int TemperatureInfoSize = 16;
        private const int TemperatureInfoTemperatureOffset = 2;

        // STORAGE_PROTOCOL_DATA_DESCRIPTOR: Version, Size, then the 40-byte
        // STORAGE_PROTOCOL_SPECIFIC_DATA (ten DWORDs) at offset 8.
        private const int ProtocolDataDescriptorHeaderSize = 8;
        private const int ProtocolSpecificDataSize = 40;
        private const int ProtocolDataOffsetOffset = 16;
        private const int ProtocolDataLengthOffset = 20;

        // The log-page request must ask for at least 512 bytes; the docs state it twice, and
        // a shorter request is refused rather than truncated.
        private const int NvmeLogPageLength = 512;

        // Composite temperature: Kelvin, little-endian, at log bytes 1 and 2. Byte 0 is
        // Critical Warning, which is why this starts at 1 and not 0.
        private const int NvmeHealthLogTemperatureOffset = 1;
        private const int NvmeHealthLogTemperatureEnd = 3;
        private const float AbsoluteZeroCelsius = 273.15f;

        // VOLUME_DISK_EXTENTS: NumberOfDiskExtents, then Extents[] at offset 8 - DISK_EXTENT
        // carries two LARGE_INTEGERs, so its 8-byte alignment pads the array start past 4.
        private const int DiskExtentsCountOffset = 0;
        private const int DiskExtentsFirstDiskNumberOffset = 8;
        private const int DiskExtentSize = 24;
        private const int MaxProbedDiskExtents = 8;

        #endregion

        // One buffer serves both tiers: the log-page query is the larger of the two and is
        // its own output buffer, as the Win32 sample recommends.
        private const int QueryBufferSize = ProtocolDataDescriptorHeaderSize + ProtocolSpecificDataSize + NvmeLogPageLength;

        // The tier-1 request never varies, and DeviceIoControl only reads its input buffer,
        // so one shared immutable copy replaces a per-instance array and a per-poll rewrite.
        private static readonly byte[] TemperatureQueryInput = BuildTemperatureQueryInput();

        private enum ProbeState
        {
            NotInitialized,        // Disk not resolved and neither tier tried yet
            TemperatureDescriptor, // Tier 1 answered: signed Celsius, nothing to convert
            NvmeHealthLog,         // Tier 2 answered: Kelvin in the SMART / Health log page
            Unavailable,           // Neither tier answered, or disposed - never retried
        }

        private readonly object _lock = new object(); // Guards every field below; see the class remarks
        private readonly byte[] _queryBuffer = new byte[QueryBufferSize];
        private SafeFileHandle? _drive;
        private ProbeState _state;
        private bool _readFailing; // Edge-triggered logging: one line per failure streak, not one per tick

        /// <summary>
        /// Does the one-time work - resolve the system volume's physical drive, open it,
        /// pick a tier - so the 1 Hz tick never pays for it. Call this once at startup,
        /// beside <c>SystemMetricsService.PrimeCpuCounter</c>, which exists for exactly the
        /// same reason: two <c>CreateFile</c>s and up to three IOCTLs do not belong on the
        /// push loop.
        /// <para>
        /// Idempotent and never throws, matching <see cref="NvmlService.Initialize"/>: the
        /// verdict is latched, so later calls return it without touching the device again,
        /// and a call after <see cref="Dispose"/> reports false rather than re-probing.
        /// Calling it is an optimization, not a precondition - <see cref="TryRead"/> still
        /// initializes on demand if this was never called.
        /// </para>
        /// </summary>
        /// <returns>True when a tier answered and reads can produce values.</returns>
        public bool Initialize()
        {
            lock (_lock)
            {
                return EnsureInitialized();
            }
        }

        /// <summary>
        /// Reads the system disk's temperature. Never throws: a transient failure returns
        /// false and is retried on the next tick, while a structural one (no drive, neither
        /// tier supported, a missing entry point) latches for the session and every later
        /// call becomes a single field read.
        /// <para>
        /// After <see cref="Initialize"/> this is exactly one <c>DeviceIoControl</c> into the
        /// preallocated buffer, with no allocation. Without it, the first call absorbs the
        /// one-time work instead - correct, just charged to the first tick.
        /// </para>
        /// </summary>
        /// <param name="celsius">The reading in degrees Celsius; meaningful only when this returns true.</param>
        /// <returns>True when a valid, in-band temperature was read this call.</returns>
        public bool TryRead(out float celsius)
        {
            celsius = 0f;

            lock (_lock)
            {
                // Lazy fallback. Initialize() is the intended startup path, but a caller
                // that forgets it must still get readings rather than silence.
                if (!EnsureInitialized())
                    return false;

                try
                {
                    float? value = _state == ProbeState.TemperatureDescriptor
                        ? ReadTemperatureDescriptor()
                        : ReadNvmeHealthLog();

                    if (value is null)
                        return false;

                    if (_readFailing)
                    {
                        _readFailing = false;
                        LoggingService.Debug("NvmeTemperatureService: NVMe temperature read recovered");
                    }

                    celsius = value.Value;
                    return true;
                }
                catch (Exception ex)
                {
                    // DllNotFoundException / EntryPointNotFoundException on a SKU without
                    // these entry points, ObjectDisposedException if the handle went away.
                    // All of them mean the same thing: this machine will not answer.
                    LatchUnavailable($"NVMe temperature disabled: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Runs the probe once, turning any exception into the same latched verdict a failed
        /// probe produces. Shared by <see cref="Initialize"/> and <see cref="TryRead"/> so
        /// the eager and lazy paths cannot drift apart. Must be called under
        /// <see cref="_lock"/>.
        /// </summary>
        /// <returns>True when the service is usable, i.e. a tier answered.</returns>
        private bool EnsureInitialized()
        {
            try
            {
                if (_state == ProbeState.NotInitialized)
                    Probe();
            }
            catch (Exception ex)
            {
                LatchUnavailable($"NVMe temperature disabled: {ex.Message}");
            }

            return _state != ProbeState.Unavailable;
        }

        /// <summary>
        /// Closes the drive handle and stops the service permanently. Safe to call twice,
        /// and safe to call while a read is in flight - it waits for the tick to finish
        /// rather than pulling the handle out from under it.
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                // Unavailable doubles as "disposed": both mean no later call will ever
                // produce a value, and neither may re-probe.
                _state = ProbeState.Unavailable;
                _drive?.Dispose();
                _drive = null;
            }
        }

        /// <summary>
        /// Resolves the system disk, opens it and decides which tier answers - once per
        /// session. A tier that does not answer here is how the choice is made, not a
        /// failure, so nothing inside the probe logs; this method logs the verdict instead,
        /// exactly one line either way. Must be called under <see cref="_lock"/>.
        /// </summary>
        private void Probe()
        {
            string? drivePath = ResolveSystemPhysicalDrivePath();
            if (drivePath is null)
            {
                LatchUnavailable("the system disk could not be resolved to a physical drive");
                return;
            }

            SafeFileHandle? drive = OpenDeviceForQuery(drivePath, out int error);
            if (drive is null)
            {
                LatchUnavailable($"{drivePath} could not be opened for querying (error {error})");
                return;
            }

            _drive = drive;

            // Tier 1 first: protocol-agnostic, already in Celsius, and a SATA system disk
            // can answer it. A tier counts as working only when it also decodes to a valid
            // reading - a driver that answers the IOCTL with SHRT_MIN forever would
            // otherwise latch this to a path that never yields a number.
            if (ReadTemperatureDescriptor() is not null)
            {
                _state = ProbeState.TemperatureDescriptor;
                LoggingService.Info($"NvmeTemperatureService: {drivePath} answers StorageDeviceTemperatureProperty");
                return;
            }

            if (ReadNvmeHealthLog() is not null)
            {
                _state = ProbeState.NvmeHealthLog;
                LoggingService.Info($"NvmeTemperatureService: {drivePath} answers the NVMe health log page");
                return;
            }

            drive.Dispose();
            _drive = null;
            LatchUnavailable($"{drivePath} reports no temperature through either query");
        }

        /// <summary>
        /// The system volume's physical drive, as <c>\\.\PhysicalDriveN</c>. Derived from
        /// <see cref="Environment.SystemDirectory"/>'s root, the same starting point
        /// <c>SystemMetricsService.OpenSystemDrive</c> uses, so this reports the temperature
        /// of the disk whose free space and capacity are already on the wire rather than
        /// whichever disk happens to be number 0.
        /// </summary>
        private static string? ResolveSystemPhysicalDrivePath()
        {
            // SystemDirectory is always rooted; a root that is not "X:\" means a UNC or
            // otherwise unaddressable system volume, which has no PhysicalDriveN name.
            string? root = Path.GetPathRoot(Environment.SystemDirectory);
            if (root is null || root.Length != 3 || root[1] != ':')
                return null;

            using SafeFileHandle? volume = OpenDeviceForQuery(@"\\.\" + root[..2], out _);
            if (volume is null)
                return null;

            // Room for eight extents so an ordinary spanned volume answers in one call. A
            // volume spanning more than that fails with ERROR_MORE_DATA and is left alone:
            // one disk's temperature says nothing useful about such a set anyway.
            byte[] extents = new byte[DiskExtentsFirstDiskNumberOffset + (DiskExtentSize * MaxProbedDiskExtents)];
            if (!DeviceIoControl(volume, IoctlVolumeGetVolumeDiskExtents, null, 0, extents, (uint)extents.Length, out uint returned, IntPtr.Zero))
                return null;

            int? diskNumber = ReadDiskNumber(extents.AsSpan(0, (int)Math.Min(returned, (uint)extents.Length)));
            return diskNumber is null
                ? null
                : @"\\.\PhysicalDrive" + diskNumber.Value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Opens a volume or physical drive for attribute queries only. Returns null rather
        /// than an invalid handle, so a failed open cannot be mistaken for a usable one, and
        /// hands the Win32 error back explicitly: disposing the invalid handle runs
        /// <c>CloseHandle</c>, and relying on the ambient last-error value surviving that is
        /// relying on an implementation detail.
        /// </summary>
        /// <param name="path">The device path, e.g. <c>\\.\C:</c> or <c>\\.\PhysicalDrive0</c>.</param>
        /// <param name="error">The Win32 error when the open failed, otherwise 0.</param>
        /// <returns>An owned handle, or null.</returns>
        private static SafeFileHandle? OpenDeviceForQuery(string path, out int error)
        {
            SafeFileHandle handle = CreateFileW(
                path, DesiredAccessNone, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

            if (!handle.IsInvalid)
            {
                error = 0;
                return handle;
            }

            // The invalid handle is still a SafeHandle instance holding a finalizer slot
            error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return null;
        }

        /// <summary>
        /// Tier 1: <c>StorageDeviceTemperatureProperty</c>. Null when the query fails or
        /// carries no usable sensor.
        /// </summary>
        private float? ReadTemperatureDescriptor()
        {
            if (!SendQuery(TemperatureQueryInput, StoragePropertyQuerySize, "StorageDeviceTemperatureProperty", out int returned))
                return null;

            float? celsius = DecodeTemperatureDescriptor(_queryBuffer.AsSpan(0, returned));
            if (celsius is null)
                NoteFailure("StorageDeviceTemperatureProperty reported no usable sensor", 0);

            return celsius;
        }

        /// <summary>
        /// Tier 2: the NVMe SMART / Health Information log page. The request has to be
        /// rewritten before every call because the driver returns the descriptor into the
        /// same buffer.
        /// </summary>
        private float? ReadNvmeHealthLog()
        {
            WriteHealthLogQuery(_queryBuffer);

            if (!SendQuery(_queryBuffer, _queryBuffer.Length, "NVMe health log page query", out int returned))
                return null;

            float? celsius = DecodeProtocolDataDescriptor(_queryBuffer.AsSpan(0, returned));
            if (celsius is null)
                NoteFailure("the NVMe health log page carried no usable temperature", 0);

            return celsius;
        }

        /// <summary>
        /// One <c>IOCTL_STORAGE_QUERY_PROPERTY</c> into <see cref="_queryBuffer"/>. The
        /// input may be that same buffer, which is what the Win32 sample recommends for the
        /// log-page query and is safe because the control code is <c>METHOD_BUFFERED</c>.
        /// </summary>
        /// <param name="input">The STORAGE_PROPERTY_QUERY, already filled in.</param>
        /// <param name="inputLength">Bytes of <paramref name="input"/> the driver should read.</param>
        /// <param name="operation">Name of the query, for the one diagnostic line per streak.</param>
        /// <param name="returned">Bytes the driver wrote, clamped to the buffer.</param>
        /// <returns>True when the driver answered.</returns>
        private bool SendQuery(byte[] input, int inputLength, string operation, out int returned)
        {
            returned = 0;

            SafeFileHandle? drive = _drive;
            if (drive is null)
                return false;

            if (!DeviceIoControl(drive, IoctlStorageQueryProperty, input, (uint)inputLength, _queryBuffer, (uint)_queryBuffer.Length, out uint bytes, IntPtr.Zero))
            {
                NoteFailure(operation, Marshal.GetLastWin32Error());
                return false;
            }

            returned = (int)Math.Min(bytes, (uint)_queryBuffer.Length);
            return true;
        }

        /// <summary>
        /// The constant tier-1 request: a STORAGE_PROPERTY_QUERY naming the temperature
        /// property, padded to the struct's real size.
        /// </summary>
        private static byte[] BuildTemperatureQueryInput()
        {
            byte[] input = new byte[StoragePropertyQuerySize];
            BinaryPrimitives.WriteUInt32LittleEndian(input, StorageDeviceTemperatureProperty);
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), PropertyStandardQuery);
            return input;
        }

        /// <summary>
        /// Writes the tier-2 request in place: a STORAGE_PROPERTY_QUERY whose
        /// AdditionalParameters are a STORAGE_PROTOCOL_SPECIFIC_DATA asking for NVMe log
        /// page 0x02. Everything past the request is zeroed so a driver that returns less
        /// than it promises cannot leave the previous tick's bytes to be decoded.
        /// </summary>
        private static void WriteHealthLogQuery(byte[] buffer)
        {
            Array.Clear(buffer);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, StorageDeviceProtocolSpecificProperty);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), PropertyStandardQuery);

            Span<byte> specific = buffer.AsSpan(AdditionalParametersOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(specific, ProtocolTypeNvme);
            BinaryPrimitives.WriteUInt32LittleEndian(specific[4..], NvmeDataTypeLogPage);
            BinaryPrimitives.WriteUInt32LittleEndian(specific[8..], NvmeLogPageHealthInfo);
            BinaryPrimitives.WriteUInt32LittleEndian(specific[ProtocolDataOffsetOffset..], ProtocolSpecificDataSize);
            BinaryPrimitives.WriteUInt32LittleEndian(specific[ProtocolDataLengthOffset..], NvmeLogPageLength);
        }

        /// <summary>
        /// Decodes a STORAGE_TEMPERATURE_DATA_DESCRIPTOR. <c>Temperature</c> is a signed
        /// value already in degrees Celsius, so there is nothing to convert - only sensors
        /// to reject. <c>0</c> and <c>SHRT_MIN</c> are the two "not reported" values
        /// controllers use (this box's drive returns SHRT_MIN for the critical, warning and
        /// under-threshold fields), and everything else goes through the shared 0-150 band.
        /// The first sensor that survives wins: index 0 is the composite where one exists,
        /// and a drive that does not report a composite lists a real sensor next.
        /// </summary>
        /// <param name="descriptor">The bytes the driver returned, trimmed to that length.</param>
        /// <returns>The temperature in degrees Celsius, or null when no sensor is usable.</returns>
        internal static float? DecodeTemperatureDescriptor(ReadOnlySpan<byte> descriptor)
        {
            if (descriptor.Length < TemperatureDescriptorHeaderSize)
                return null;

            int infoCount = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[TemperatureInfoCountOffset..]);

            for (int i = 0; i < infoCount; i++)
            {
                int info = TemperatureDescriptorHeaderSize + (TemperatureInfoSize * i);

                // InfoCount is the driver's claim; the returned length is the fact. Trusting
                // the count is how this reads past the end of what was actually written.
                if (info + TemperatureInfoSize > descriptor.Length)
                    break;

                short raw = BinaryPrimitives.ReadInt16LittleEndian(descriptor[(info + TemperatureInfoTemperatureOffset)..]);
                if (raw == 0 || raw == short.MinValue)
                    continue;

                float celsius = raw;
                if (Constants.IsValidTemperature(celsius))
                    return celsius;
            }

            return null;
        }

        /// <summary>
        /// Decodes a STORAGE_PROTOCOL_DATA_DESCRIPTOR down to the log page it points at.
        /// <c>ProtocolDataOffset</c> is relative to the start of the embedded
        /// STORAGE_PROTOCOL_SPECIFIC_DATA, <b>not</b> to the start of the output buffer -
        /// getting that wrong reads eight bytes early and produces a plausible temperature
        /// rather than a crash, which is why it is a decode step of its own with its own test.
        /// </summary>
        /// <param name="descriptor">The bytes the driver returned, trimmed to that length.</param>
        /// <returns>The temperature in degrees Celsius, or null when the descriptor is unusable.</returns>
        internal static float? DecodeProtocolDataDescriptor(ReadOnlySpan<byte> descriptor)
        {
            if (descriptor.Length < ProtocolDataDescriptorHeaderSize + ProtocolSpecificDataSize)
                return null;

            ReadOnlySpan<byte> specific = descriptor[ProtocolDataDescriptorHeaderSize..];
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(specific[ProtocolDataOffsetOffset..]);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(specific[ProtocolDataLengthOffset..]);

            // An offset inside the specific data would have the "log" overlap the request
            // fields; a length short of three bytes cannot hold the temperature at all.
            if (offset < ProtocolSpecificDataSize || length < NvmeHealthLogTemperatureEnd)
                return null;

            if ((long)offset + length > specific.Length)
                return null;

            return DecodeNvmeHealthLogTemperature(specific.Slice((int)offset, (int)length));
        }

        /// <summary>
        /// Decodes the composite temperature of an NVMe SMART / Health Information log page:
        /// Kelvin, little-endian, at bytes 1 and 2 (byte 0 is Critical Warning). Zero Kelvin
        /// is the never-reported value and is dropped before it can become -273 C; anything
        /// else goes through the shared 0-150 band.
        /// </summary>
        /// <param name="log">The log page, or at least its first three bytes.</param>
        /// <returns>The temperature in degrees Celsius, or null when none was reported.</returns>
        internal static float? DecodeNvmeHealthLogTemperature(ReadOnlySpan<byte> log)
        {
            if (log.Length < NvmeHealthLogTemperatureEnd)
                return null;

            int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[NvmeHealthLogTemperatureOffset..]);
            if (kelvin == 0)
                return null;

            float celsius = kelvin - AbsoluteZeroCelsius;
            return Constants.IsValidTemperature(celsius) ? celsius : null;
        }

        /// <summary>
        /// The disk number of a volume's first extent, from a VOLUME_DISK_EXTENTS. A volume
        /// can span several disks; the first extent is the one this app reports, matching
        /// the single free/total pair it already publishes for that volume.
        /// </summary>
        /// <param name="extents">The bytes the driver returned, trimmed to that length.</param>
        /// <returns>The disk number, or null when the volume reported no usable extent.</returns>
        internal static int? ReadDiskNumber(ReadOnlySpan<byte> extents)
        {
            if (extents.Length < DiskExtentsFirstDiskNumberOffset + sizeof(uint))
                return null;

            if (BinaryPrimitives.ReadUInt32LittleEndian(extents[DiskExtentsCountOffset..]) == 0)
                return null;

            uint diskNumber = BinaryPrimitives.ReadUInt32LittleEndian(extents[DiskExtentsFirstDiskNumberOffset..]);
            return diskNumber > int.MaxValue ? null : (int)diskNumber;
        }

        /// <summary>
        /// Edge-triggered read diagnostics: one line per failure streak, cleared by the next
        /// successful read. The poll runs at 1 Hz and a drive that stops answering keeps
        /// doing so, so "log it every time" would grow the file forever.
        /// <para>
        /// Silent while the probe is still running: a tier that does not answer is how
        /// <see cref="Initialize"/> chooses the other one, and it logs the verdict itself.
        /// </para>
        /// </summary>
        /// <param name="operation">What was being attempted.</param>
        /// <param name="win32Error">The Win32 error, or 0 when the call succeeded but its data was unusable.</param>
        private void NoteFailure(string operation, int win32Error)
        {
            if (_state == ProbeState.NotInitialized || _readFailing)
                return;

            _readFailing = true;
            LoggingService.Debug(win32Error != 0
                ? $"NvmeTemperatureService: {operation} failed with error {win32Error}; further read failures are not logged until it recovers"
                : $"NvmeTemperatureService: {operation}; further read failures are not logged until it recovers");
        }

        /// <summary>
        /// Disables the service for the session after one Debug line. Deliberately not an
        /// error: a RAID or HBA controller, a USB bridge and a VM all legitimately answer
        /// nothing, and that is the expected outcome on those machines rather than a fault.
        /// </summary>
        private void LatchUnavailable(string reason)
        {
            _state = ProbeState.Unavailable;
            LoggingService.Debug($"NvmeTemperatureService: {reason}; disk temperature unavailable this session");
        }
    }
}
