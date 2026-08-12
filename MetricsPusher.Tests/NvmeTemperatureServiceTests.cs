using System.Buffers.Binary;
using MetricsPusher.Services;

namespace MetricsPusher.Tests
{
    /// <summary>
    /// Every decode case runs against hand-built byte arrays, so the whole class passes on
    /// a machine with no NVMe drive, a SATA system disk, or inside a VM. The two live
    /// tests at the bottom assert only what is true everywhere: that the service never
    /// throws and that a value, when it appears, is in band.
    /// </summary>
    public class NvmeTemperatureServiceTests
    {
        // STORAGE_TEMPERATURE_DATA_DESCRIPTOR: Version, Size, CriticalTemperature,
        // WarningTemperature, InfoCount, Reserved0[2], Reserved1[2] - 24 bytes before the
        // first 16-byte STORAGE_TEMPERATURE_INFO.
        private const int DescriptorHeaderSize = 24;
        private const int TemperatureInfoSize = 16;

        // STORAGE_PROTOCOL_DATA_DESCRIPTOR: Version, Size, then the 40-byte
        // STORAGE_PROTOCOL_SPECIFIC_DATA at offset 8.
        private const int ProtocolDescriptorHeaderSize = 8;
        private const int ProtocolSpecificDataSize = 40;

        // The composite temperature the dev box's Micron MTFDKBA1T0QFM reported during the
        // spike: 323 K on the health log, 50 C on the descriptor. Kept as the canonical
        // fixture so the decoders are pinned against a real drive's bytes, not invented ones.
        private const ushort MeasuredKelvin = 323;

        [Fact]
        public void DecodeTemperatureDescriptor_ShouldReturnCelsius_ForASignedInPlaceReading()
        {
            // Arrange - Temperature is already degrees Celsius; no conversion is involved
            byte[] descriptor = BuildTemperatureDescriptor(50);

            // Act
            float? result = NvmeTemperatureService.DecodeTemperatureDescriptor(descriptor);

            // Assert
            Assert.Equal(50f, result);
        }

        [Fact]
        public void DecodeTemperatureDescriptor_ShouldTakeTheFirstValidSensor_WhenTheCompositeIsNotReported()
        {
            // Arrange - index 0 is the composite and may be absent on a multi-sensor drive
            byte[] descriptor = BuildTemperatureDescriptor(0, 44);

            // Act
            float? result = NvmeTemperatureService.DecodeTemperatureDescriptor(descriptor);

            // Assert
            Assert.Equal(44f, result);
        }

        [Theory]
        [InlineData((short)0)] // 0 K decoded elsewhere, and the controller's "not reported" value here
        [InlineData(short.MinValue)] // SHRT_MIN: measured on the dev box for CriticalTemperature/UnderThreshold
        public void DecodeTemperatureDescriptor_ShouldReturnNull_ForNeverReportedSentinels(short temperature)
        {
            // Arrange
            byte[] descriptor = BuildTemperatureDescriptor(temperature);

            // Act
            float? result = NvmeTemperatureService.DecodeTemperatureDescriptor(descriptor);

            // Assert
            Assert.Null(result);
        }

        [Theory]
        [InlineData((short)-5)]  // Signed field, physically meaningful, still outside the wire band
        [InlineData((short)151)] // Just past Constants.MaxValidTemperature
        [InlineData((short)900)]
        public void DecodeTemperatureDescriptor_ShouldReturnNull_WhenOutsideTheSharedValidBand(short temperature)
        {
            // Arrange - the 0-150 band is Constants.IsValidTemperature's, shared with the
            // GPU temp already on the wire; this class deliberately owns no second validator
            byte[] descriptor = BuildTemperatureDescriptor(temperature);

            // Act
            float? result = NvmeTemperatureService.DecodeTemperatureDescriptor(descriptor);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void DecodeTemperatureDescriptor_ShouldReturnNull_WhenInfoCountIsZero()
        {
            // Arrange - a driver that answers the IOCTL but has no sensor to report
            byte[] descriptor = BuildTemperatureDescriptor();

            // Act
            float? result = NvmeTemperatureService.DecodeTemperatureDescriptor(descriptor);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void DecodeTemperatureDescriptor_ShouldReturnNull_WhenTheBufferIsShorterThanTheHeader()
        {
            // Arrange
            byte[] truncated = new byte[DescriptorHeaderSize - 1];

            // Act
            float? result = NvmeTemperatureService.DecodeTemperatureDescriptor(truncated);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void DecodeTemperatureDescriptor_ShouldReturnNull_WhenInfoCountOverrunsTheBuffer()
        {
            // Arrange - InfoCount claims a sensor whose 16 bytes are not there. Trusting the
            // count over the length is how this reads whatever follows the buffer.
            byte[] descriptor = BuildTemperatureDescriptor(50);
            byte[] truncated = descriptor[..(DescriptorHeaderSize + TemperatureInfoSize - 1)];

            // Act
            float? result = NvmeTemperatureService.DecodeTemperatureDescriptor(truncated);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void DecodeTemperatureDescriptor_ShouldReturnNull_ForAnEmptyBuffer()
        {
            // Act
            float? result = NvmeTemperatureService.DecodeTemperatureDescriptor(default);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void DecodeNvmeHealthLogTemperature_ShouldSubtractAbsoluteZero_FromTheMeasuredReading()
        {
            // Arrange - the bytes the dev box's drive actually returned
            byte[] log = BuildHealthLog(MeasuredKelvin);

            // Act
            float? result = NvmeTemperatureService.DecodeNvmeHealthLogTemperature(log);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(49.85f, result.Value, 0.01f);
        }

        [Fact]
        public void DecodeNvmeHealthLogTemperature_ShouldReadKelvinLittleEndian_AtLogBytesOneAndTwo()
        {
            // Arrange - 0x012C is 300 K little-endian and 11265 K read the other way round,
            // so the assertion fails loudly if the byte order is ever flipped
            byte[] log = BuildHealthLog(300);

            // Assert the fixture really is the byte pattern this test is about
            Assert.Equal(0x2C, log[1]);
            Assert.Equal(0x01, log[2]);

            // Act
            float? result = NvmeTemperatureService.DecodeNvmeHealthLogTemperature(log);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(26.85f, result.Value, 0.01f);
        }

        [Fact]
        public void DecodeNvmeHealthLogTemperature_ShouldIgnoreTheCriticalWarningByte()
        {
            // Arrange - log byte 0 is Critical Warning, not part of the temperature. Reading
            // the 16-bit value one byte early is the classic off-by-one on this log page.
            byte[] log = BuildHealthLog(MeasuredKelvin);
            log[0] = 0xFF;

            // Act
            float? result = NvmeTemperatureService.DecodeNvmeHealthLogTemperature(log);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(49.85f, result.Value, 0.01f);
        }

        [Theory]
        [InlineData((ushort)0)]   // 0 K: never reported, and -273 C if it were taken at face value
        [InlineData((ushort)273)] // -0.15 C: below Constants.MinValidTemperature
        [InlineData((ushort)500)] // 226.85 C: above Constants.MaxValidTemperature
        public void DecodeNvmeHealthLogTemperature_ShouldReturnNull_ForUnreportedOrOutOfBandKelvin(ushort kelvin)
        {
            // Arrange
            byte[] log = BuildHealthLog(kelvin);

            // Act
            float? result = NvmeTemperatureService.DecodeNvmeHealthLogTemperature(log);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void DecodeNvmeHealthLogTemperature_ShouldReturnNull_WhenTheLogIsShorterThanTheField()
        {
            // Arrange - the temperature occupies bytes 1 and 2, so three bytes is the minimum
            byte[] log = new byte[] { 0x00, 0x43 };

            // Act
            float? result = NvmeTemperatureService.DecodeNvmeHealthLogTemperature(log);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void DecodeProtocolDataDescriptor_ShouldDecode_WhenTheLogFollowsTheSpecificData()
        {
            // Arrange - the ordinary answer: ProtocolDataOffset == sizeof(STORAGE_PROTOCOL_SPECIFIC_DATA)
            byte[] descriptor = BuildProtocolDataDescriptor(ProtocolSpecificDataSize, BuildHealthLog(MeasuredKelvin));

            // Act
            float? result = NvmeTemperatureService.DecodeProtocolDataDescriptor(descriptor);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(49.85f, result.Value, 0.01f);
        }

        [Fact]
        public void DecodeProtocolDataDescriptor_ShouldApplyTheOffsetFromTheSpecificData_NotTheBufferStart()
        {
            // Arrange - ProtocolDataOffset is relative to the start of
            // STORAGE_PROTOCOL_SPECIFIC_DATA, which itself sits 8 bytes into the buffer. A
            // decoy log is planted where a buffer-relative reading would land, holding a
            // different in-band temperature, so getting this wrong yields a plausible number
            // rather than a crash.
            const uint offset = 48;
            byte[] descriptor = BuildProtocolDataDescriptor(offset, BuildHealthLog(MeasuredKelvin));
            BuildHealthLog(400).AsSpan(0, 8).CopyTo(descriptor.AsSpan((int)offset)); // 400 K = 126.85 C

            // Act
            float? result = NvmeTemperatureService.DecodeProtocolDataDescriptor(descriptor);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(49.85f, result.Value, 0.01f);
        }

        [Fact]
        public void DecodeProtocolDataDescriptor_ShouldReturnNull_WhenTheOffsetPointsInsideTheSpecificData()
        {
            // Arrange - an offset below sizeof(STORAGE_PROTOCOL_SPECIFIC_DATA) would have the
            // "log" overlap the request fields; the docs' own sample rejects exactly this
            byte[] descriptor = BuildProtocolDataDescriptor(ProtocolSpecificDataSize - 1, BuildHealthLog(MeasuredKelvin));

            // Act
            float? result = NvmeTemperatureService.DecodeProtocolDataDescriptor(descriptor);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void DecodeProtocolDataDescriptor_ShouldReturnNull_WhenTheReportedLengthOverrunsTheBuffer()
        {
            // Arrange - a driver reporting more data than it returned
            byte[] descriptor = BuildProtocolDataDescriptor(ProtocolSpecificDataSize, BuildHealthLog(MeasuredKelvin));
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(ProtocolDescriptorHeaderSize + 20), (uint)descriptor.Length);

            // Act
            float? result = NvmeTemperatureService.DecodeProtocolDataDescriptor(descriptor);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void DecodeProtocolDataDescriptor_ShouldReturnNull_WhenTheBufferIsShorterThanTheDescriptor()
        {
            // Arrange
            byte[] truncated = new byte[ProtocolDescriptorHeaderSize + ProtocolSpecificDataSize - 1];

            // Act
            float? result = NvmeTemperatureService.DecodeProtocolDataDescriptor(truncated);

            // Assert
            Assert.Null(result);
        }

        [Theory]
        [InlineData(0u)]
        [InlineData(3u)]
        public void ReadDiskNumber_ShouldReturnTheFirstExtentsDisk_ForAResolvableVolume(uint diskNumber)
        {
            // Arrange
            byte[] extents = BuildVolumeDiskExtents(1, diskNumber);

            // Act
            int? result = NvmeTemperatureService.ReadDiskNumber(extents);

            // Assert
            Assert.Equal((int)diskNumber, result);
        }

        [Fact]
        public void ReadDiskNumber_ShouldReturnNull_WhenTheVolumeHasNoExtents()
        {
            // Arrange
            byte[] extents = BuildVolumeDiskExtents(0, 0);

            // Act
            int? result = NvmeTemperatureService.ReadDiskNumber(extents);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void ReadDiskNumber_ShouldReturnNull_WhenTheBufferCannotHoldAnExtent()
        {
            // Arrange - DiskNumber is the first DWORD of Extents[0], which starts at offset 8
            byte[] truncated = BuildVolumeDiskExtents(1, 0)[..11];

            // Act
            int? result = NvmeTemperatureService.ReadDiskNumber(truncated);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void Initialize_ShouldReturnTheSameVerdictTwice_WhenCalledRepeatedly()
        {
            // Arrange - the probe runs once per session and latches its verdict; a second
            // call must be a field read, not a second CreateFile and IOCTL round
            using var service = new NvmeTemperatureService();

            // Act
            bool first = service.Initialize();
            bool second = service.Initialize();

            // Assert
            Assert.Equal(first, second);
        }

        [Fact]
        public void Initialize_ShouldNotChangeWhatTryReadReports_WhetherOrNotItWasCalled()
        {
            // Arrange - Initialize is where the one-time cost belongs, but it is an
            // optimization and not a precondition: a caller that skips it must still read
            using var eager = new NvmeTemperatureService();
            using var lazy = new NvmeTemperatureService();

            // Act
            bool initialized = eager.Initialize();
            bool eagerRead = eager.TryRead(out _);
            bool lazyRead = lazy.TryRead(out _);

            // Assert - true together on a drive that answers, false together on one that
            // does not, so this holds on a machine with no NVMe drive at all
            Assert.Equal(eagerRead, lazyRead);
            if (eagerRead)
                Assert.True(initialized);
        }

        [Fact]
        public void Initialize_ShouldReturnFalse_AfterDispose()
        {
            // Arrange - disposal is permanent; initializing afterwards must not re-probe
            var service = new NvmeTemperatureService();
            service.Dispose();

            // Act
            bool initialized = service.Initialize();

            // Assert
            Assert.False(initialized);
        }

        [Fact]
        public void TryRead_ShouldNotThrowAndReportOnlyInBandValues_OnAnyMachine()
        {
            // Arrange - the answer is hardware-dependent (RAID, USB bridges and VMs report
            // nothing at all), so the only portable assertion is that a reported value is real
            using var service = new NvmeTemperatureService();

            // Act
            bool read = service.TryRead(out float celsius);

            // Assert
            if (read)
                Assert.True(Constants.IsValidTemperature(celsius), $"reported {celsius} C");
        }

        [Fact]
        public void TryRead_ShouldReturnTheSameVerdictTwice_WhenCalledRepeatedly()
        {
            // Arrange - the tier is chosen once at init and latched; a second poll must not
            // re-probe or flip availability
            using var service = new NvmeTemperatureService();

            // Act
            bool first = service.TryRead(out _);
            bool second = service.TryRead(out _);

            // Assert
            Assert.Equal(first, second);
        }

        [Fact]
        public void Dispose_ShouldBeSafe_WhenCalledTwice()
        {
            // Arrange
            var service = new NvmeTemperatureService();

            // Act
            service.Dispose();
            service.Dispose();

            // Assert - a read after disposal is a no-value, not a crash
            Assert.False(service.TryRead(out _));
        }

        /// <summary>
        /// A STORAGE_TEMPERATURE_DATA_DESCRIPTOR carrying one STORAGE_TEMPERATURE_INFO per
        /// supplied reading, laid out exactly as winioctl.h declares it.
        /// </summary>
        private static byte[] BuildTemperatureDescriptor(params short[] temperatures)
        {
            byte[] buffer = new byte[DescriptorHeaderSize + (TemperatureInfoSize * temperatures.Length)];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), (uint)buffer.Length); // Version
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)buffer.Length); // Size
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(8), short.MinValue);       // CriticalTemperature
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(10), short.MinValue);      // WarningTemperature
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), (ushort)temperatures.Length);

            for (int i = 0; i < temperatures.Length; i++)
            {
                Span<byte> info = buffer.AsSpan(DescriptorHeaderSize + (TemperatureInfoSize * i));
                BinaryPrimitives.WriteUInt16LittleEndian(info, (ushort)i);              // Index
                BinaryPrimitives.WriteInt16LittleEndian(info[2..], temperatures[i]);    // Temperature
                BinaryPrimitives.WriteInt16LittleEndian(info[4..], 87);                 // OverThreshold
                BinaryPrimitives.WriteInt16LittleEndian(info[6..], short.MinValue);     // UnderThreshold
            }

            return buffer;
        }

        /// <summary>
        /// A STORAGE_PROTOCOL_DATA_DESCRIPTOR whose embedded STORAGE_PROTOCOL_SPECIFIC_DATA
        /// points at <paramref name="log"/> placed <paramref name="protocolDataOffset"/> bytes
        /// past the specific data - the layout the driver fills in on a successful log-page query.
        /// </summary>
        private static byte[] BuildProtocolDataDescriptor(uint protocolDataOffset, byte[] log)
        {
            byte[] buffer = new byte[ProtocolDescriptorHeaderSize + protocolDataOffset + log.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), ProtocolDescriptorHeaderSize + ProtocolSpecificDataSize); // Version
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), ProtocolDescriptorHeaderSize + ProtocolSpecificDataSize); // Size

            Span<byte> specific = buffer.AsSpan(ProtocolDescriptorHeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(specific, 3);            // ProtocolType = ProtocolTypeNvme
            BinaryPrimitives.WriteUInt32LittleEndian(specific[4..], 2);       // DataType = NVMeDataTypeLogPage
            BinaryPrimitives.WriteUInt32LittleEndian(specific[8..], 0x02);    // ProtocolDataRequestValue = health log
            BinaryPrimitives.WriteUInt32LittleEndian(specific[16..], protocolDataOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(specific[20..], (uint)log.Length);

            log.CopyTo(buffer.AsSpan(ProtocolDescriptorHeaderSize + (int)protocolDataOffset));
            return buffer;
        }

        /// <summary>
        /// An NVMe SMART / Health Information log page: Critical Warning at byte 0, composite
        /// temperature in Kelvin little-endian at bytes 1-2, and 509 bytes this app ignores.
        /// </summary>
        private static byte[] BuildHealthLog(ushort kelvin)
        {
            byte[] log = new byte[512];
            BinaryPrimitives.WriteUInt16LittleEndian(log.AsSpan(1), kelvin);
            return log;
        }

        /// <summary>
        /// A VOLUME_DISK_EXTENTS holding one DISK_EXTENT. DiskNumber is at offset 8, not 4:
        /// DISK_EXTENT's LARGE_INTEGERs give it 8-byte alignment, so the array starts padded.
        /// </summary>
        private static byte[] BuildVolumeDiskExtents(uint extentCount, uint diskNumber)
        {
            byte[] buffer = new byte[8 + 24];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), extentCount);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), diskNumber);
            return buffer;
        }
    }
}
