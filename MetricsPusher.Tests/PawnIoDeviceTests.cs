using MetricsPusher.Services;

namespace MetricsPusher.Tests
{
    /// <summary>
    /// Everything here except the last few cases runs with no PawnIO driver, no device and
    /// no elevation: the marshalling and the IOCTL codes are pure data, and they are the
    /// half of this layer a live device would never catch. A wrong function name or a
    /// byte-swapped input does not fail the IOCTL - it makes the driver run a different
    /// function, or the right one against a garbage argument, and the temperature that
    /// comes back looks plausible.
    /// <para>
    /// The device-touching cases at the end are deliberately tolerant. On the machines this
    /// suite runs on, <c>CreateFileW</c> is expected to fail with either
    /// ERROR_FILE_NOT_FOUND (no driver) or ERROR_ACCESS_DENIED (the test host is not
    /// elevated, and PawnIO's device DACL admits only SYSTEM and Administrators), so what
    /// they pin is that each of those is classified rather than thrown.
    /// </para>
    /// </summary>
    public class PawnIoDeviceTests
    {
        // The published IOCTL literals (pawnio_um.h, plan section 2.3). Spelled out rather
        // than recomputed so a typo in the arithmetic that produces them is visible here.
        private const uint PublishedLoadBinary = 0xA1B22084;
        private const uint PublishedExecuteFn = 0xA1B22104;
        private const uint PublishedVersion = 0xA1B22184;

        // CTL_CODE inputs, likewise from pawnio_um.h.
        private const uint PawnIoDeviceType = 41394; // 0xA1B2
        private const uint MethodBuffered = 0;
        private const uint FileAnyAccess = 0;

        [Fact]
        public void IoctlCodes_ShouldEqualThePublishedLiterals()
        {
            // Assert - the first two are the entire contract with the driver, and both are
            // verified against a live PawnIO 2.2.0. If one were wrong the device would
            // answer ERROR_INVALID_FUNCTION and the whole feature would be silently off,
            // which is exactly the failure that looks like "no PawnIO on this machine".
            Assert.Equal(PublishedLoadBinary, PawnIoDevice.IoctlLoadBinary);
            Assert.Equal(PublishedExecuteFn, PawnIoDevice.IoctlExecuteFn);

            // The third is pinned as documentation of a dead end: sent to the real driver
            // it answers ERROR_INVALID_PARAMETER (87), so it is not a liveness probe and
            // nothing calls it. A successful TryLoadModule is the liveness signal.
            Assert.Equal(PublishedVersion, PawnIoDevice.IoctlVersion);
        }

        [Fact]
        public void IoctlCodes_ShouldEqualTheCtlCodeArithmetic()
        {
            // Assert - the same three numbers derived the way the Windows CTL_CODE macro
            // derives them, so a mistyped function number (0x821 / 0x841 / 0x861) fails
            // here even if someone "fixed" the literals above to match it
            Assert.Equal(PawnIoDevice.IoctlLoadBinary, CtlCode(PawnIoDeviceType, 0x821, MethodBuffered, FileAnyAccess));
            Assert.Equal(PawnIoDevice.IoctlExecuteFn, CtlCode(PawnIoDeviceType, 0x841, MethodBuffered, FileAnyAccess));
            Assert.Equal(PawnIoDevice.IoctlVersion, CtlCode(PawnIoDeviceType, 0x861, MethodBuffered, FileAnyAccess));
        }

        [Fact]
        public void TryWriteFunctionName_ShouldNulPadAShortNameToThirtyTwoBytes()
        {
            // Arrange - prefilled with 0xFF so a byte the writer fails to clear is visible
            Span<byte> destination = stackalloc byte[PawnIoDevice.FunctionNameBytes];
            destination.Fill(0xFF);

            // Act
            bool written = PawnIoDevice.TryWriteFunctionName("ioctl_read_msr", destination);

            // Assert - the field is fixed width; the driver reads all 32 bytes
            Assert.True(written);
            Assert.Equal("ioctl_read_msr"u8.ToArray(), destination[..14].ToArray());
            Assert.All(destination[14..].ToArray(), b => Assert.Equal((byte)0, b));
        }

        [Fact]
        public void TryWriteFunctionName_ShouldKeepATerminator_ForAThirtyOneCharName()
        {
            // Arrange - the longest name that survives intact
            string name = new string('a', PawnIoDevice.MaxFunctionNameChars);
            Span<byte> destination = stackalloc byte[PawnIoDevice.FunctionNameBytes];
            destination.Fill(0xFF);

            // Act
            bool written = PawnIoDevice.TryWriteFunctionName(name, destination);

            // Assert - byte 31 is the terminator, which is why the limit is 31 and not 32
            Assert.True(written);
            Assert.All(destination[..31].ToArray(), b => Assert.Equal((byte)'a', b));
            Assert.Equal((byte)0, destination[31]);
        }

        [Fact]
        public void TryWriteFunctionName_ShouldTruncateALongName_LeavingATerminator()
        {
            // Arrange - 40 chars, well past the field
            string name = new string('b', 40);
            Span<byte> destination = stackalloc byte[PawnIoDevice.FunctionNameBytes];
            destination.Fill(0xFF);

            // Act
            bool written = PawnIoDevice.TryWriteFunctionName(name, destination);

            // Assert - truncation is not a failure (it is what the upstream wrapper does),
            // but it must never consume the terminator: a 32-byte name with no NUL would
            // let the driver read past the field
            Assert.True(written);
            Assert.All(destination[..31].ToArray(), b => Assert.Equal((byte)'b', b));
            Assert.Equal((byte)0, destination[31]);
        }

        [Theory]
        [InlineData("temperaturé")]  // Latin-1 supplement
        [InlineData("ioctl_ read")]  // non-breaking space
        [InlineData("你好")]      // outside Latin entirely
        public void TryWriteFunctionName_ShouldRejectANonAsciiName(string name)
        {
            // Arrange
            Span<byte> destination = stackalloc byte[PawnIoDevice.FunctionNameBytes];
            destination.Fill(0xFF);

            // Act
            bool written = PawnIoDevice.TryWriteFunctionName(name, destination);

            // Assert - rejected, not transliterated. Encoding.ASCII would map each of these
            // to '?', which is a DIFFERENT function name the driver would then fail to find
            // - or, worse, find. PawnIO function names are compile-time constants in this
            // codebase, so a non-ASCII one is a bug, and the destination is left zeroed so
            // no half-written name can reach the device.
            Assert.False(written);
            Assert.All(destination.ToArray(), b => Assert.Equal((byte)0, b));
        }

        [Fact]
        public void TryWriteFunctionName_ShouldRejectAnEmbeddedNul()
        {
            // Arrange
            Span<byte> destination = stackalloc byte[PawnIoDevice.FunctionNameBytes];

            // Act - the driver stops at the first NUL, so this would silently address
            // "ioctl" while the caller believes it asked for "ioctl\0read"
            bool written = PawnIoDevice.TryWriteFunctionName("ioctl\0read", destination);

            // Assert
            Assert.False(written);
        }

        [Fact]
        public void TryWriteFunctionName_ShouldRejectAnEmptyName()
        {
            // Arrange
            Span<byte> destination = stackalloc byte[PawnIoDevice.FunctionNameBytes];

            // Act & Assert - an all-NUL name field is not a function
            Assert.False(PawnIoDevice.TryWriteFunctionName(string.Empty, destination));
        }

        [Fact]
        public void TryWriteFunctionName_ShouldRejectATooSmallDestination()
        {
            // Arrange - one byte short of the fixed field
            Span<byte> destination = stackalloc byte[PawnIoDevice.FunctionNameBytes - 1];

            // Act & Assert - refuse rather than write a short field, which would leave the
            // driver reading whatever follows the buffer
            Assert.False(PawnIoDevice.TryWriteFunctionName("ioctl_read_msr", destination));
        }

        [Fact]
        public void TryWriteExecuteInput_ShouldMatchAHandBuiltBuffer()
        {
            // Arrange - the shape of a real MSR read plus a second value, so both the
            // padding and the little-endian layout are pinned by literal bytes rather than
            // by the same BitConverter the production code could have used
            long[] input = new long[] { 0x1B1L, 0x123456789ABCDEF0L };
            byte[] expected = new byte[]
            {
                (byte)'r', (byte)'e', (byte)'a', (byte)'d', (byte)'_', (byte)'m', (byte)'s', (byte)'r',
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0,
                0xB1, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0xF0, 0xDE, 0xBC, 0x9A, 0x78, 0x56, 0x34, 0x12,
            };

            Span<byte> destination = stackalloc byte[PawnIoDevice.MaxExecuteInputBytes];
            destination.Fill(0xFF);

            // Act
            bool written = PawnIoDevice.TryWriteExecuteInput("read_msr", input, destination, out int bytesWritten);

            // Assert - 32-byte name field followed by the int64s in machine order, which on
            // this x64-only app is little-endian and is what the driver expects
            Assert.True(written);
            Assert.Equal(expected.Length, bytesWritten);
            Assert.Equal(expected, destination[..bytesWritten].ToArray());
        }

        [Fact]
        public void TryWriteExecuteInput_ShouldWriteOnlyTheNameField_WhenThereAreNoInputs()
        {
            // Arrange
            Span<byte> destination = stackalloc byte[PawnIoDevice.MaxExecuteInputBytes];
            destination.Fill(0xFF);

            // Act - a function that takes no arguments still sends its 32-byte name
            bool written = PawnIoDevice.TryWriteExecuteInput("ioctl_version", default, destination, out int bytesWritten);

            // Assert
            Assert.True(written);
            Assert.Equal(PawnIoDevice.FunctionNameBytes, bytesWritten);
            Assert.Equal("ioctl_version"u8.ToArray(), destination[..13].ToArray());
            Assert.All(destination[13..PawnIoDevice.FunctionNameBytes].ToArray(), b => Assert.Equal((byte)0, b));
        }

        [Fact]
        public void TryWriteExecuteInput_ShouldRejectMoreInputsThanTheBufferHolds()
        {
            // Arrange - one past the preallocated capacity
            long[] input = new long[PawnIoDevice.MaxExecuteValues + 1];
            Span<byte> destination = stackalloc byte[PawnIoDevice.MaxExecuteInputBytes];

            // Act & Assert - the buffer is fixed and reused precisely so the 1 Hz tick
            // allocates nothing; overflowing it must be a refusal, never a resize
            Assert.False(PawnIoDevice.TryWriteExecuteInput("read_msr", input, destination, out int bytesWritten));
            Assert.Equal(0, bytesWritten);
        }

        [Fact]
        public void TryWriteExecuteInput_ShouldRejectATooSmallDestination()
        {
            // Arrange - room for the name field but not for the one input value
            Span<byte> destination = stackalloc byte[PawnIoDevice.FunctionNameBytes];

            // Act & Assert
            Assert.False(PawnIoDevice.TryWriteExecuteInput("read_msr", new long[] { 0x1B1L }, destination, out int bytesWritten));
            Assert.Equal(0, bytesWritten);
        }

        [Fact]
        public void TryWriteExecuteInput_ShouldLeaveNoResidueOfAPreviousName()
        {
            // The buffer is a reused field, so this is the realistic corruption: a long
            // name followed by a short one, with the tail of the first still in place.
            // Arrange
            Span<byte> destination = stackalloc byte[PawnIoDevice.MaxExecuteInputBytes];
            Assert.True(PawnIoDevice.TryWriteExecuteInput("a_very_long_pawnio_function", new long[] { 1L }, destination, out _));

            // Act
            bool written = PawnIoDevice.TryWriteExecuteInput("short", new long[] { 2L }, destination, out int bytesWritten);

            // Assert - "short" and nothing else; a stale tail would name a function that
            // exists in neither module
            Assert.True(written);
            Assert.Equal(PawnIoDevice.FunctionNameBytes + sizeof(long), bytesWritten);
            Assert.Equal("short"u8.ToArray(), destination[..5].ToArray());
            Assert.All(destination[5..PawnIoDevice.FunctionNameBytes].ToArray(), b => Assert.Equal((byte)0, b));
        }

        [Fact]
        public void ExecuteBufferSizes_ShouldBeExactForAOneInOneOutCall()
        {
            // The single most consequential number in this file. IntelMSR 0.2.10's
            // ioctl_read_msr requires exactly one int64 in and exactly one out, and it
            // checks that BEFORE consulting its MSR allow-list - so a call that declared
            // the reusable buffer's capacity instead of the exact length would fail every
            // read, and fail it looking precisely like "this module does not support this
            // CPU". Measured against the live 2.2.0 driver; pinned here so a future
            // "simplification" to pass buffer.Length cannot pass the suite.
            // Arrange
            Span<byte> destination = stackalloc byte[PawnIoDevice.MaxExecuteInputBytes];

            // Act
            bool written = PawnIoDevice.TryWriteExecuteInput("ioctl_read_msr", new long[] { 0x1B1L }, destination, out int inputBytes);

            // Assert - 32-byte name field plus one int64 in, one int64 out
            Assert.True(written);
            Assert.Equal(40, inputBytes);
            Assert.Equal(8, PawnIoDevice.ExecuteOutputBytes(1));

            // And the buffer they are sliced out of is deliberately much larger, which is
            // exactly why the lengths above must not come from it
            Assert.True(destination.Length > inputBytes);
        }

        [Fact]
        public void ExecuteOutputBytes_ShouldBeEightBytesPerValue()
        {
            // Assert - the arity the module checks is a count of int64s, not of bytes
            Assert.Equal(0, PawnIoDevice.ExecuteOutputBytes(0));
            Assert.Equal(16, PawnIoDevice.ExecuteOutputBytes(2));
            Assert.Equal(PawnIoDevice.MaxExecuteValues * sizeof(long), PawnIoDevice.ExecuteOutputBytes(PawnIoDevice.MaxExecuteValues));
        }

        [Fact]
        public void MaxExecuteInputBytes_ShouldHoldTheNameFieldAndEveryInputValue()
        {
            // Assert - callers size their own spans against these constants, so the two
            // must stay consistent or TryWriteExecuteInput would reject its own maximum
            Assert.Equal(PawnIoDevice.FunctionNameBytes + (PawnIoDevice.MaxExecuteValues * sizeof(long)), PawnIoDevice.MaxExecuteInputBytes);
        }

        [Fact]
        public void TryOpen_ShouldClassifyTheOutcome_WithoutThrowing()
        {
            // Act - on a machine with no PawnIO this is ERROR_FILE_NOT_FOUND; on one with
            // PawnIO but an unelevated test host it is ERROR_ACCESS_DENIED. Both are
            // classifications, and neither may escape as an exception - the whole point of
            // the multi-state result is that the caller can tell "no driver" (an ordinary
            // negative) from "not elevated" (a manifest regression).
            PawnIoOpenStatus status = PawnIoDevice.TryOpen(out PawnIoDevice? device);

            using (device)
            {
                // Assert
                Assert.True(Enum.IsDefined(status), $"TryOpen returned an undefined status {(int)status}");

                if (status == PawnIoOpenStatus.Opened)
                    Assert.NotNull(device);
                else
                    Assert.Null(device);
            }
        }

        [Fact]
        public void TryOpen_ShouldReturnTheSameOutcome_WhenCalledTwice()
        {
            // Act - nothing latches in this layer, so a second open must reach the same
            // verdict rather than inheriting the first call's state
            PawnIoOpenStatus first = PawnIoDevice.TryOpen(out PawnIoDevice? firstDevice);
            using (firstDevice)
            {
                PawnIoOpenStatus second = PawnIoDevice.TryOpen(out PawnIoDevice? secondDevice);
                using (secondDevice)
                {
                    // Assert
                    Assert.Equal(first, second);
                }
            }
        }

        [Fact]
        public void Dispose_ShouldBeSafe_WhenCalledTwice()
        {
            if (PawnIoDevice.TryOpen(out PawnIoDevice? device) != PawnIoOpenStatus.Opened || device == null)
                return; // No device on this machine (or no elevation): nothing to close twice

            // Act & Assert - a double close of a Win32 handle is the bug SafeFileHandle
            // exists to make impossible; this pins that Dispose is idempotent
            device.Dispose();
            device.Dispose();
        }

        [Fact]
        public void TryLoadModuleAndTryExecute_ShouldReturnFalse_AfterDispose()
        {
            if (PawnIoDevice.TryOpen(out PawnIoDevice? device) != PawnIoOpenStatus.Opened || device == null)
                return;

            // Arrange
            device.Dispose();

            // Act & Assert - a P/Invoke through a closed SafeFileHandle throws
            // ObjectDisposedException, and this layer promises never to throw at a caller
            // that polls it once a second
            Span<long> output = stackalloc long[1];
            Assert.False(device.TryExecute("ioctl_read_msr", new long[] { 0x1B1L }, output));
            Assert.False(device.TryLoadModule(new byte[] { 0x01, 0x02, 0x03 }));
        }

        /// <summary>
        /// The Windows CTL_CODE macro, spelled out: device type, access, function and
        /// transfer method packed into one 32-bit control code.
        /// </summary>
        /// <param name="deviceType">The driver's device type.</param>
        /// <param name="function">The function number within that device type.</param>
        /// <param name="method">The buffering method (METHOD_BUFFERED here).</param>
        /// <param name="access">The access the I/O manager demands on the handle.</param>
        /// <returns>The packed control code.</returns>
        private static uint CtlCode(uint deviceType, uint function, uint method, uint access)
        {
            return (deviceType << 16) | (access << 14) | (function << 2) | method;
        }
    }
}
