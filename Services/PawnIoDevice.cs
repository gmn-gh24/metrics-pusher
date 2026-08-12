using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MetricsPusher.Services
{
    /// <summary>
    /// Thin P/Invoke layer over the PawnIO kernel driver: open the device, load one signed
    /// Pawn module into it, then call the functions that module exposes. It is to the CPU
    /// sensors what <see cref="NvmlService"/> is to the GPU ones - the raw transport, with
    /// every decision about cadence, caching, latching and vendor selection left to the
    /// service above it.
    /// <para>
    /// PawnIO is not a general ring-0 primitive: userspace never gets <c>rdmsr</c>, only
    /// whatever IOCTLs the loaded module chose to expose, and the module allow-lists the
    /// registers it will read. That is the whole reason this app can read die temperature
    /// at all after WinRing0 became undeployable, and it is why the module bytes - not this
    /// file - decide what is reachable.
    /// </para>
    /// <para>
    /// <b>One instance per loaded module.</b> The driver associates a module with the handle
    /// it was loaded through, so a second module means a second <see cref="TryOpen"/>. A
    /// <em>rejected</em> load leaves the instance reusable, which is what lets the caller
    /// try <c>IntelMSR</c> and then <c>AMDFamily17</c> on one handle: only a successful load
    /// closes the instance to further modules.
    /// </para>
    /// <para>
    /// <b>Nothing here throws and nothing here latches.</b> Every member answers false on
    /// failure, because the caller polls it on the 1 Hz push tick and a throw there would
    /// take down a datagram that has nine other fields in it. The one distinction this
    /// layer <em>does</em> insist on is <em>why</em> the open failed - see
    /// <see cref="PawnIoOpenStatus"/>. LibreHardwareMonitor's wrapper hands back a live
    /// object with a null handle and then zero-fills every read, so "not elevated", "driver
    /// absent" and "this CPU is unsupported" all arrive as 0 degrees; distinguishing them is
    /// the point of this type existing rather than that package being referenced.
    /// </para>
    /// <para>
    /// <b>Zero steady-state allocations.</b> The input and output buffers are fields sized
    /// once in the constructor and reused, and <see cref="TryExecute"/> takes and fills
    /// spans, so a tick costs one kernel round trip and nothing on the heap. The csproj
    /// disables both concurrent and server GC, so an allocation here buys a foreground GC
    /// pause in a process whose entire design point is invisibility.
    /// </para>
    /// <para>
    /// <b>NOT thread-safe, by design</b> - the same contract <see cref="NvmlService"/>
    /// carries. The reused buffers and the failure-streak flag are plain instance fields,
    /// so callers must serialize every member; in this app that means calling it only under
    /// the lock that already serializes the CPU sensor sweep. A second lock here would
    /// guard the same call path twice.
    /// </para>
    /// </summary>
    internal sealed class PawnIoDevice : IDisposable
    {
        #region Native kernel32 entry points

        // kernel32 is a KnownDLL: the loader resolves it from the copy already mapped into
        // every process, so there is no search to hijack and nothing for
        // SystemLibraryResolver.GuardedLibraries to pin. CA5392 - an error in this repo -
        // is satisfied by the assembly-level DefaultDllImportSearchPaths(System32) in
        // Program.cs.
        //
        // PawnIOLib.dll is deliberately NOT imported. Its entire job is the three calls
        // below, and importing it would add a native library that does not live in System32
        // - precisely the searched load the rule above exists to prevent. It would also
        // pull PawnIO's GPL-2.0 into this process; talking to the device through IOCTLs
        // alone is the case its licence exception carves out.
        //
        // CloseHandle is likewise absent: SafeFileHandle's ReleaseHandle is CloseHandle,
        // and letting the framework own that call is what removes the double-close and the
        // leak-on-exception from this file entirely.
        private const string Kernel32 = "kernel32.dll";

        [DllImport(Kernel32, EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        // Blittable byte[] parameters are pinned by the marshaler rather than copied, which
        // is what lets the reused buffers below cost nothing per call. [In] / [Out] record
        // which side fills which, exactly as NvmlService does for nvmlDeviceGetName.
        [DllImport(Kernel32, ExactSpelling = true, SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            [In] byte[] lpInBuffer,
            uint nInBufferSize,
            [Out] byte[] lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        #endregion

        /// <summary>
        /// The PawnIO device, reached through the object-manager root because the driver
        /// creates a plain <c>\Device\</c> object and no DOS symbolic link.
        /// </summary>
        internal const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";

        // The Windows CTL_CODE macro, spelled out:
        //     (DeviceType << 16) | (Access << 14) | (Function << 2) | Method
        // with PawnIO's device type 41394 (0xA1B2), METHOD_BUFFERED and FILE_ANY_ACCESS,
        // from pawnio_um.h. Written as the arithmetic rather than as 0xA1B22084 / 0xA1B22104
        // / 0xA1B22184 so a reader can check the function numbers against the header without
        // a calculator; PawnIoDeviceTests pins the arithmetic AND the published literals, so
        // neither can drift to match a mistake in the other.
        private const uint DeviceTypePawnIo = 41394;
        private const uint MethodBuffered = 0;
        private const uint FileAnyAccess = 0;
        private const uint FunctionLoadBinary = 0x821;
        private const uint FunctionExecuteFn = 0x841;
        private const uint FunctionVersion = 0x861;

        /// <summary>IOCTL_PIO_LOAD_BINARY (0xA1B22084): hands the driver a signed module.</summary>
        internal const uint IoctlLoadBinary = (DeviceTypePawnIo << 16) | (FileAnyAccess << 14) | (FunctionLoadBinary << 2) | MethodBuffered;

        /// <summary>IOCTL_PIO_EXECUTE_FN (0xA1B22104): calls one function in the loaded module.</summary>
        internal const uint IoctlExecuteFn = (DeviceTypePawnIo << 16) | (FileAnyAccess << 14) | (FunctionExecuteFn << 2) | MethodBuffered;

        /// <summary>
        /// IOCTL_PIO_VERSION (0xA1B22184) as the plan documents it - and <b>it does not
        /// work</b>. Sent to the real 2.2.0 driver it answers ERROR_INVALID_PARAMETER (87),
        /// measured on the dev box against an elevated handle, so either the function number
        /// or the buffer contract differs from what pawnio_um.h suggested. Nothing calls it
        /// and nothing should: the liveness signal is a successful
        /// <see cref="TryLoadModule"/>, which proves the driver, the handle and the module
        /// all at once. It is kept, and pinned by a test, only so the next reader who finds
        /// this code in the header does not rediscover it the same expensive way.
        /// </summary>
        internal const uint IoctlVersion = (DeviceTypePawnIo << 16) | (FileAnyAccess << 14) | (FunctionVersion << 2) | MethodBuffered;

        /// <summary>
        /// Width of the function-name field that opens every execute buffer: 32 bytes of
        /// NUL-padded ASCII.
        /// </summary>
        internal const int FunctionNameBytes = 32;

        /// <summary>
        /// Longest function name that fits: 31, not 32, so byte 31 is always a terminator
        /// even for a name that filled the field.
        /// </summary>
        internal const int MaxFunctionNameChars = FunctionNameBytes - 1;

        /// <summary>
        /// How many int64s <see cref="TryExecute"/> will carry in either direction. Far more
        /// than the one-in, one-out shape of every read this app makes (an MSR, an SMN
        /// offset, a RAPL energy accumulator); the headroom is free because the buffers are
        /// allocated once, and the cap is what keeps them fixed-size and reusable.
        /// </summary>
        internal const int MaxExecuteValues = 16;

        /// <summary>
        /// Size of the execute input buffer: the name field plus every input value.
        /// </summary>
        internal const int MaxExecuteInputBytes = FunctionNameBytes + (MaxExecuteValues * sizeof(long));

        private const int MaxExecuteOutputBytes = MaxExecuteValues * sizeof(long);

        // CreateFileW arguments. GENERIC_READ | GENERIC_WRITE because an IOCTL that both
        // sends and receives needs both; the device's DACL grants GENERIC_ALL to SYSTEM and
        // Administrators, so an elevated process gets them and nobody else gets anything.
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x80;

        // SHARED, and deliberately so - do NOT "harden" this to 0. PawnIO is a machine-wide
        // service with several well-known clients: LibreHardwareMonitor and FanControl both
        // talk to this exact device, and a user running this app is a user likely to have
        // one of them open. An exclusive open would either fail for us with
        // ERROR_SHARING_VIOLATION or lock them out, and the visible symptom would be CPU
        // temperature silently falling back to the ACPI thermal zone whenever another
        // monitoring tool happens to be running - a field-only failure no test would catch.
        //
        // Coexistence is a stated goal of this design rather than a courtesy: the AMD path
        // takes Global\Access_PCI with a World-FullControl DACL precisely so unelevated
        // third-party tools keep working on the shared PCI index/data pair, and the
        // acceptance criterion for the whole feature is that the reported temperature
        // tracks HWiNFO64 within ~2 C - which requires HWiNFO64 to be reading at the same
        // time we are. Sharing costs nothing on our side: we hold no exclusive state in the
        // driver, the module is bound to our own handle, and another client's handle cannot
        // disturb it. This also matches the configuration proven against the live 2.2.0
        // driver.
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;

        private const int ErrorFileNotFound = 2; // ERROR_FILE_NOT_FOUND
        private const int ErrorAccessDenied = 5; // ERROR_ACCESS_DENIED

        // SafeFileHandle rather than a raw IntPtr: it releases the handle deterministically
        // on Dispose, is idempotent so a double Dispose cannot close a handle number the OS
        // has already handed to someone else, and its critical finalizer closes the device
        // even on a path that forgot to dispose at all. A raw IntPtr would put all three of
        // those on this file's correctness.
        private readonly SafeFileHandle _handle;

        // Allocated once, reused forever: this is the difference between the 1 Hz tick
        // costing nothing and costing four arrays per call, which is what the wrapper this
        // layer replaces does.
        private readonly byte[] _inputBuffer = new byte[MaxExecuteInputBytes];
        private readonly byte[] _outputBuffer = new byte[MaxExecuteOutputBytes];

        // One module per handle; set only by a SUCCESSFUL load, so a rejected module leaves
        // the instance free for the next candidate.
        private bool _moduleLoaded;

        // Edge trigger: an execute that fails once at 1 Hz fails every second thereafter,
        // and one line per failure streak is the rule the rest of this codebase follows.
        // Cleared by the next success, so a recovered sensor's later failure is logged again.
        private bool _executeFailureLogged;

        /// <summary>
        /// Initializes a new instance of the <see cref="PawnIoDevice"/> class around an
        /// already-open device handle. Private because <see cref="TryOpen"/> is the only
        /// thing that can produce one, and it is also the only thing that can classify why
        /// there is not one.
        /// </summary>
        /// <param name="handle">An open, valid handle to the PawnIO device.</param>
        private PawnIoDevice(SafeFileHandle handle)
        {
            _handle = handle;
        }

        /// <summary>
        /// Opens the PawnIO device. The outcome is a classification rather than a bool
        /// because the three ways this fails call for three different reactions:
        /// <see cref="PawnIoOpenStatus.DriverNotPresent"/> is an ordinary negative that
        /// drives the install prompt, <see cref="PawnIoOpenStatus.AccessDenied"/> is a
        /// manifest regression that should be impossible, and
        /// <see cref="PawnIoOpenStatus.Failed"/> is anything else. Never throws.
        /// <para>
        /// Diagnostics are written here, at the level each outcome deserves: Debug for a
        /// missing driver, which is the expected state of a stock machine, and Error for
        /// access denied, which after the manifest change can only mean the app is running
        /// without the elevation it now requests.
        /// </para>
        /// </summary>
        /// <param name="device">
        /// The opened device, or null on every outcome other than
        /// <see cref="PawnIoOpenStatus.Opened"/>. The caller owns it and must dispose it.
        /// </param>
        /// <returns>Which of the outcomes occurred.</returns>
        internal static PawnIoOpenStatus TryOpen(out PawnIoDevice? device)
        {
            device = null;

            SafeFileHandle handle;
            int error;

            try
            {
                handle = CreateFileW(
                    DevicePath,
                    GenericRead | GenericWrite,
                    FileShareRead | FileShareWrite,
                    IntPtr.Zero,
                    OpenExisting,
                    FileAttributeNormal,
                    IntPtr.Zero);

                // Read the error before anything else on this thread can overwrite it. The
                // runtime captures it the instant the native call returns because
                // SetLastError is set, so constructing the SafeFileHandle in between is not
                // a hazard - but a log call or another P/Invoke would be.
                error = Marshal.GetLastWin32Error();
            }
            catch (Exception ex)
            {
                // Nothing above should be able to throw. The catch is here because this
                // layer's contract with its caller is that it never does, and the caller is
                // a startup path that must degrade to the ACPI fallback rather than die.
                LoggingService.Warn($"PawnIoDevice: CreateFileW on {DevicePath} failed: {ex.Message}");
                return PawnIoOpenStatus.Failed;
            }

            if (!handle.IsInvalid)
            {
                device = new PawnIoDevice(handle);
                LoggingService.Debug($"PawnIoDevice: opened {DevicePath}");
                return PawnIoOpenStatus.Opened;
            }

            handle.Dispose();

            switch (error)
            {
                case ErrorFileNotFound:
                    // The device object does not exist: PawnIO is not installed, or its
                    // service is demand-start and has not been started. An ordinary
                    // negative on a stock machine, so Debug and no alarm.
                    LoggingService.Debug($"PawnIoDevice: {DevicePath} does not exist (Win32 {error}); the PawnIO driver is not installed or not running");
                    return PawnIoOpenStatus.DriverNotPresent;

                case ErrorAccessDenied:
                    // PawnIO's INF sets a protected DACL of D:P(A;;GA;;;SY)(A;;GA;;;BA) -
                    // SYSTEM and Builtin Administrators, nothing else - so this means the
                    // process is not elevated. app.manifest requests requireAdministrator,
                    // which makes that impossible; if it happens anyway the manifest is the
                    // thing that broke, not the driver, and no amount of falling back to
                    // the thermal zone will fix it. Hence Error, and hence saying so.
                    LoggingService.Error($"PawnIoDevice: access denied opening {DevicePath} (Win32 {error}) - this process is not elevated, which app.manifest's requireAdministrator is supposed to guarantee. Treat this as a manifest regression, not as a missing driver");
                    return PawnIoOpenStatus.AccessDenied;

                default:
                    // Everything else, including ERROR_SHARING_VIOLATION (32), which gets
                    // no arm of its own: the open above is shared, so a sharing violation
                    // would mean some other client took the device exclusively, not that
                    // anything here is wrong. The message therefore reports the code and
                    // draws no conclusion - "failed with Win32 32" sends a reader to the
                    // error, whereas anything phrased as a driver fault would send them
                    // hunting a driver that is working fine.
                    LoggingService.Warn($"PawnIoDevice: CreateFileW on {DevicePath} failed with Win32 {error}");
                    return PawnIoOpenStatus.Failed;
            }
        }

        /// <summary>
        /// Loads one signed Pawn module into the driver through
        /// <see cref="IoctlLoadBinary"/>. False is the <em>expected</em> answer on most
        /// machines for at least one of the modules this app carries: a module's
        /// <c>main()</c> returns <c>STATUS_NOT_SUPPORTED</c> for the wrong CPU vendor, a
        /// family outside its range, or a 32-bit host, and the driver reports that as a
        /// failed IOCTL. It is therefore logged at Debug and never as an error - the module
        /// is the authoritative gate on which CPUs this feature works for, and asking it is
        /// how the caller finds out.
        /// <para>
        /// Only a successful load closes the instance to further modules; a rejected one
        /// leaves it reusable, which is what lets the caller try the Intel module and then
        /// the AMD one on a single handle.
        /// </para>
        /// </summary>
        /// <param name="moduleBytes">The module's signed bytecode, verbatim.</param>
        /// <returns>True when the driver accepted the module.</returns>
        internal bool TryLoadModule(ReadOnlySpan<byte> moduleBytes)
        {
            if (_handle.IsInvalid || _handle.IsClosed)
                return false;

            if (_moduleLoaded)
            {
                LoggingService.Debug("PawnIoDevice: a module is already loaded on this handle; a second module needs a second TryOpen");
                return false;
            }

            if (moduleBytes.IsEmpty)
                return false;

            // The one allocation in this file outside the constructor, and it happens once
            // per session at startup rather than on the tick: the marshaler needs an array
            // to pin, and the caller holds the module as a span over an embedded resource.
            byte[] module = moduleBytes.ToArray();

            try
            {
                // bytesReturned is discarded deliberately: a successful load reports 0
                // (measured against the live 2.2.0 driver), so the boolean is the whole
                // verdict and a zero count here means nothing.
                if (!DeviceIoControl(_handle, IoctlLoadBinary, module, (uint)module.Length, _outputBuffer, 0, out _, IntPtr.Zero))
                {
                    LoggingService.Debug($"PawnIoDevice: the driver declined the module (Win32 {Marshal.GetLastWin32Error()}); this is the normal answer when the module does not support this CPU");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"PawnIoDevice: IOCTL_PIO_LOAD_BINARY failed: {ex.Message}");
                return false;
            }

            _moduleLoaded = true;
            return true;
        }

        /// <summary>
        /// Calls one function in the loaded module through <see cref="IoctlExecuteFn"/> and
        /// fills <paramref name="output"/> with what it returned. This is the 1 Hz path, so
        /// it allocates nothing: the input buffer is a reused field and the results are
        /// copied straight into the caller's span.
        /// <para>
        /// Never throws, and its failure diagnostics are edge-triggered - a function that
        /// fails once at this cadence fails every second thereafter, so one line per streak
        /// is the difference between a diagnostic and a log that grows forever.
        /// </para>
        /// </summary>
        /// <param name="functionName">
        /// The module's exported name, ASCII and at most <see cref="MaxFunctionNameChars"/>
        /// characters (longer names are truncated, not rejected).
        /// </param>
        /// <param name="input">The int64 arguments, at most <see cref="MaxExecuteValues"/>.</param>
        /// <param name="output">
        /// Sized by the caller to the number of int64s the function returns, and written
        /// only when this returns true.
        /// </param>
        /// <returns>True when the driver ran the function and returned exactly that many values.</returns>
        internal bool TryExecute(string functionName, ReadOnlySpan<long> input, Span<long> output)
        {
            if (_handle.IsInvalid || _handle.IsClosed)
                return false;

            if (output.Length > MaxExecuteValues)
                return false;

            if (!TryWriteExecuteInput(functionName, input, _inputBuffer, out int inputBytes))
                return false;

            int outputBytes = ExecuteOutputBytes(output.Length);

            try
            {
                // The two sizes below are the EXACT byte counts for this call, never the
                // capacity of the buffers backing them - and that distinction is load
                // bearing, not tidiness. IntelMSR 0.2.10's ioctl_read_msr checks that the
                // in and out arrays hold exactly one int64 each and rejects the request
                // before it ever consults its MSR allow-list (measured against the live
                // driver). Passing the buffers' full length would fail every read on that
                // module, and fail it looking exactly like "this module does not support
                // this CPU" - a wrong diagnosis of a caller-side bug. Reusing the
                // allocation and sending an exact length are independent; do both.
                if (!DeviceIoControl(_handle, IoctlExecuteFn, _inputBuffer, (uint)inputBytes, _outputBuffer, (uint)outputBytes, out uint bytesReturned, IntPtr.Zero))
                {
                    NoteExecuteFailure($"PawnIoDevice: {functionName} returned Win32 {Marshal.GetLastWin32Error()}");
                    return false;
                }

                // METHOD_BUFFERED, so the driver writes into a system buffer and the I/O
                // manager copies exactly bytesReturned of it back into _outputBuffer. That
                // is why the check is an equality and not a courtesy: a short count has not
                // merely under-reported, it has left the tail of this REUSED buffer holding
                // the previous tick's answer, and a stale MSR value decodes to a perfectly
                // plausible temperature. The equality is safe rather than brittle, and that
                // is measured, not inferred: a successful ioctl_read_msr against the live
                // 2.2.0 driver reports exactly 8 bytes for a one-int64 result.
                if (bytesReturned != (uint)outputBytes)
                {
                    NoteExecuteFailure($"PawnIoDevice: {functionName} returned {bytesReturned} bytes, expected {outputBytes}");
                    return false;
                }

                _outputBuffer.AsSpan(0, outputBytes).CopyTo(MemoryMarshal.AsBytes(output));
            }
            catch (Exception ex)
            {
                NoteExecuteFailure($"PawnIoDevice: {functionName} failed: {ex.Message}");
                return false;
            }

            // Ends the failure streak: a later genuine failure gets its own line.
            _executeFailureLogged = false;
            return true;
        }

        /// <summary>
        /// Closes the device handle. Safe to call more than once, and safe to call on an
        /// instance whose handle never opened.
        /// </summary>
        public void Dispose()
        {
            // Idempotent by SafeHandle's own contract, and its ReleaseHandle is CloseHandle
            // - which is why kernel32's CloseHandle is not imported anywhere in this file.
            _handle.Dispose();
        }

        /// <summary>
        /// Writes the 32-byte, NUL-padded ASCII function-name field that opens every execute
        /// buffer. Pure, and internal so the tests exercise the real marshalling rather than
        /// a copy of it - this is the half of the wire format a live device cannot check,
        /// because a wrong name does not fail the IOCTL, it names a different function.
        /// <para>
        /// A name longer than <see cref="MaxFunctionNameChars"/> is truncated, matching the
        /// upstream wrapper, and the limit is 31 rather than 32 so the terminator survives
        /// truncation. A name that is empty, carries an embedded NUL, or contains any
        /// non-ASCII character is <em>rejected</em>: <c>Encoding.ASCII</c> would map the last
        /// to '?', and a NUL would make the driver read a shorter name than the caller
        /// wrote - both of which silently address a different function. Every PawnIO
        /// function name in this app is a compile-time constant, so any of the three is a
        /// bug rather than an input, and rejection is what makes it visible.
        /// </para>
        /// </summary>
        /// <param name="functionName">The module's exported function name.</param>
        /// <param name="destination">
        /// At least <see cref="FunctionNameBytes"/> bytes; that prefix is fully rewritten,
        /// and zeroed on every rejection so no partial name can reach the device.
        /// </param>
        /// <returns>True when the name was written.</returns>
        internal static bool TryWriteFunctionName(string functionName, Span<byte> destination)
        {
            if (destination.Length < FunctionNameBytes)
                return false;

            // Cleared before anything is written, not merely padded afterwards: the buffer
            // is a reused field, so a name shorter than its predecessor would otherwise keep
            // that predecessor's tail and name a function nobody asked for.
            destination[..FunctionNameBytes].Clear();

            if (string.IsNullOrEmpty(functionName))
                return false;

            // The whole name is validated, including any part truncation would discard: a
            // non-ASCII character anywhere is a bug in the caller, and whether it happens to
            // sit past byte 31 is not a reason to accept it.
            foreach (char c in functionName)
            {
                if (c == '\0' || c > '\u007F')
                    return false;
            }

            // Validated pure ASCII, so a byte cast IS the encoding. Done by hand rather than
            // through Encoding.ASCII so this never depends on that encoder's replacement
            // fallback for the exact case rejected above.
            int charCount = Math.Min(functionName.Length, MaxFunctionNameChars);
            for (int i = 0; i < charCount; i++)
                destination[i] = (byte)functionName[i];

            return true;
        }

        /// <summary>
        /// Assembles a complete execute input buffer: the 32-byte name field followed by the
        /// int64 arguments. Pure, and internal for the same reason as
        /// <see cref="TryWriteFunctionName"/> - the tests compare its output against
        /// hand-built byte arrays, which is the only way a byte-order or padding mistake
        /// gets caught before it becomes a plausible-looking temperature.
        /// </summary>
        /// <param name="functionName">The module's exported function name.</param>
        /// <param name="input">The int64 arguments, at most <see cref="MaxExecuteValues"/>.</param>
        /// <param name="destination">The buffer to fill; must hold name field plus arguments.</param>
        /// <param name="bytesWritten">How much of <paramref name="destination"/> to send, or 0 on failure.</param>
        /// <returns>True when the buffer was assembled.</returns>
        internal static bool TryWriteExecuteInput(string functionName, ReadOnlySpan<long> input, Span<byte> destination, out int bytesWritten)
        {
            bytesWritten = 0;

            // Refuse rather than resize: the buffer this normally writes into is a field
            // sized once, and growing it per call is the allocation this layer exists to
            // avoid.
            if (input.Length > MaxExecuteValues)
                return false;

            int required = FunctionNameBytes + (input.Length * sizeof(long));
            if (destination.Length < required)
                return false;

            if (!TryWriteFunctionName(functionName, destination))
                return false;

            // Machine byte order, which is what the driver reads and what this x64-only app
            // fixes as little-endian (PlatformTarget is pinned in the csproj).
            MemoryMarshal.AsBytes(input).CopyTo(destination[FunctionNameBytes..]);

            bytesWritten = required;
            return true;
        }

        /// <summary>
        /// Bytes an execute call must declare for <paramref name="valueCount"/> returned
        /// int64s. A one-line multiplication with its own name and its own test because the
        /// number it produces is checked by the module, not merely by the driver: a size
        /// that does not match the function's declared arity is rejected outright, so this
        /// must track the caller's span exactly and can never be rounded up to the reusable
        /// buffer's capacity. See the comment in <see cref="TryExecute"/>.
        /// </summary>
        /// <param name="valueCount">How many int64s the function returns.</param>
        /// <returns>The exact <c>nOutBufferSize</c> for the call.</returns>
        internal static int ExecuteOutputBytes(int valueCount)
        {
            return valueCount * sizeof(long);
        }

        /// <summary>
        /// Edge-triggered diagnostics for a failed execute: one line per failure streak,
        /// because the caller is a 1 Hz tick and a broken sensor stays broken. Deliberately
        /// local rather than shared with <see cref="NvmlService"/>'s equivalent - the two
        /// layers have nothing else in common, and coupling them would put a GPU service in
        /// the CPU path's dependency list for four lines of code.
        /// </summary>
        /// <param name="message">The already-formatted diagnostic.</param>
        private void NoteExecuteFailure(string message)
        {
            if (_executeFailureLogged)
                return;

            _executeFailureLogged = true;
            LoggingService.Debug($"{message}; further execute failures are not logged until one succeeds");
        }
    }

    /// <summary>
    /// Why <see cref="PawnIoDevice.TryOpen"/> did or did not produce a device. A bool would
    /// collapse three outcomes that need three different reactions, which is exactly the
    /// flaw in the wrapper this layer replaces: it returns a live object with a null handle
    /// and zero-fills every read afterwards, so a machine without the driver, a process
    /// without elevation and a CPU the module does not support are indistinguishable at the
    /// call site.
    /// <para>
    /// <see cref="Failed"/> is the zero value on purpose: a default-initialized status must
    /// not read as success.
    /// </para>
    /// </summary>
    internal enum PawnIoOpenStatus
    {
        /// <summary>An unexpected Win32 error. Also the default, which must never be success.</summary>
        Failed,

        /// <summary>The device opened and the caller owns the returned instance.</summary>
        Opened,

        /// <summary>
        /// ERROR_FILE_NOT_FOUND: the device object does not exist, so PawnIO is not
        /// installed or its demand-start service is not running. An ordinary negative -
        /// this is the state of a stock machine, and what the install prompt reacts to.
        /// </summary>
        DriverNotPresent,

        /// <summary>
        /// ERROR_ACCESS_DENIED: the device exists but this process is not elevated. PawnIO's
        /// DACL admits only SYSTEM and Builtin Administrators, so with app.manifest asking
        /// for requireAdministrator this cannot happen - it means the manifest regressed,
        /// and it is logged as an error rather than absorbed as a missing driver.
        /// </summary>
        AccessDenied,
    }
}
