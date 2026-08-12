using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using MetricsPusher.Services;

namespace MetricsPusher.Tests
{
    /// <summary>
    /// Every decode and rate case runs against hand-built byte rows, so the whole class
    /// passes on a machine with no network, an unusual adapter, or inside a VM. The layout
    /// test pins the service's offset table against the CLR marshaler's own computation of
    /// MIB_IF_ROW2 - the one check hand-built fixtures cannot provide, because a fixture
    /// encodes the same offsets the decoder reads. The live tests at the bottom assert
    /// only what is true everywhere: the service never throws, its verdicts are stable,
    /// and a value, when it appears, is in band.
    /// </summary>
    public class NetworkThroughputServiceTests
    {
        /// <summary>
        /// WCHAR[257] Alias precedes Description; the service never decodes it, so the
        /// offset lives here, for the decoy test only.
        /// </summary>
        private const int AliasOffset = NetworkThroughputService.DescriptionOffset - (257 * 2);

        [Fact]
        public void RowLayout_ShouldMatchTheMarshalersComputation_ForEveryDecodedField()
        {
            // Assert - the service decodes at hand-derived offsets from a raw buffer for
            // an allocation-free tick. This pins every one of them (and the row size) to
            // the layout the CLR marshaler computes for the same struct declaration, so a
            // wrong constant fails here instead of decoding plausible-looking garbage.
            Assert.Equal(NetworkThroughputService.RowSize, Marshal.SizeOf<MibIfRow2>());
            Assert.Equal(NetworkThroughputService.InterfaceIndexOffset, (int)Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.InterfaceIndex)));
            Assert.Equal(NetworkThroughputService.DescriptionOffset, (int)Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.Description)));
            Assert.Equal(NetworkThroughputService.TypeOffset, (int)Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.Type)));
            Assert.Equal(NetworkThroughputService.MediaConnectStateOffset, (int)Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.MediaConnectState)));
            Assert.Equal(NetworkThroughputService.ReceiveLinkSpeedOffset, (int)Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.ReceiveLinkSpeed)));
            Assert.Equal(NetworkThroughputService.InOctetsOffset, (int)Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.InOctets)));
            Assert.Equal(NetworkThroughputService.OutOctetsOffset, (int)Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.OutOctets)));
            Assert.Equal(AliasOffset, (int)Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.Alias)));
        }

        [Fact]
        public void DecodeFields_ShouldRoundTripValues_WrittenAtTheDocumentedOffsets()
        {
            // Arrange
            byte[] row = BuildRow(
                interfaceIndex: 11,
                interfaceType: 71,
                mediaConnectState: 2,
                receiveLinkSpeed: 5_000_000_000,
                inOctets: 48_030_488_317,
                outOctets: 7_950_769_887);

            // Act & Assert
            Assert.Equal(11u, NetworkThroughputService.DecodeInterfaceIndex(row));
            Assert.Equal(71u, NetworkThroughputService.DecodeInterfaceType(row));
            Assert.Equal(2u, NetworkThroughputService.DecodeMediaConnectState(row));
            Assert.Equal(5_000_000_000ul, NetworkThroughputService.DecodeReceiveLinkSpeed(row));
            Assert.Equal(48_030_488_317ul, NetworkThroughputService.DecodeInOctets(row));
            Assert.Equal(7_950_769_887ul, NetworkThroughputService.DecodeOutOctets(row));
        }

        [Fact]
        public void DecodeDescription_ShouldStopAtTheFirstNul_WhenTerminated()
        {
            // Arrange
            byte[] row = BuildRow(description: "Intel(R) Ethernet Controller I225-V");

            // Act
            string? result = NetworkThroughputService.DecodeDescription(row);

            // Assert - raw field value; trademark stripping happens in the service's
            // probe, not in the decoder
            Assert.Equal("Intel(R) Ethernet Controller I225-V", result);
        }

        [Fact]
        public void DecodeDescription_ShouldReadTheDescriptionField_NotTheAliasBeforeIt()
        {
            // Arrange - a decoy: the user-renameable Alias sits immediately before
            // Description and is also valid text, so a decoder reading 514 bytes too
            // early returns a plausible string instead of crashing. Both fields are
            // planted so that mistake stays visible.
            byte[] row = BuildRow(alias: "Ethernet 2", description: "Realtek PCIe 5GbE Family Controller");

            // Act
            string? result = NetworkThroughputService.DecodeDescription(row);

            // Assert
            Assert.Equal("Realtek PCIe 5GbE Family Controller", result);
        }

        [Fact]
        public void DecodeDescription_ShouldUseEveryCharacter_WhenTheFieldHasNoNul()
        {
            // Arrange - a description that fills all 257 WCHARs leaves no terminator;
            // the decoder must bound itself by the field width, not scan past it
            string full = new string('D', NetworkThroughputService.DescriptionChars);
            byte[] row = BuildRow(description: full);

            // Act
            string? result = NetworkThroughputService.DecodeDescription(row);

            // Assert
            Assert.Equal(full, result);
        }

        [Fact]
        public void DecodeDescription_ShouldReturnNull_WhenTheFieldIsEmpty()
        {
            // Arrange - an absent name must become a missing wire key, not ""
            byte[] row = BuildRow(description: null);

            // Act & Assert
            Assert.Null(NetworkThroughputService.DecodeDescription(row));
        }

        [Theory]
        [InlineData(0ul, null)]                     // No answer (disconnected and virtual adapters report 0)
        [InlineData(ulong.MaxValue, null)]          // NET_IF_LINK_SPEED_UNKNOWN
        [InlineData(999_999ul, null)]               // Sub-1-Mbps rounds to 0, which the cap excludes
        [InlineData(1_000_000ul, 1)]
        [InlineData(100_000_000ul, 100)]
        [InlineData(1_000_000_000ul, 1000)]
        [InlineData(2_500_000_000ul, 2500)]
        [InlineData(5_000_000_000ul, 5000)]
        [InlineData(400_000_000_000ul, 400_000)]    // The inclusive plausibility cap
        [InlineData(400_001_000_000ul, null)]       // Above it: dropped, not clamped
        public void MapLinkSpeedMbps_ShouldMapBitsPerSecondToWholeMbps_AndDropTheUnusable(ulong raw, int? expected)
        {
            // Act & Assert
            Assert.Equal(expected, NetworkThroughputService.MapLinkSpeedMbps(raw));
        }

        [Theory]
        [InlineData(6u, NetworkThroughputService.MediaTypeEthernet)]   // IF_TYPE_ETHERNET_CSMACD
        [InlineData(71u, NetworkThroughputService.MediaTypeWifi)]      // IF_TYPE_IEEE80211
        [InlineData(1u, NetworkThroughputService.MediaTypeOther)]      // IF_TYPE_OTHER
        [InlineData(24u, NetworkThroughputService.MediaTypeOther)]     // Software loopback
        [InlineData(131u, NetworkThroughputService.MediaTypeOther)]    // Tunnel
        [InlineData(0u, NetworkThroughputService.MediaTypeOther)]
        public void MapMediaType_ShouldMapTheTwoRenderableTypes_AndFoldTheRest(uint ifType, int expected)
        {
            // Act & Assert
            Assert.Equal(expected, NetworkThroughputService.MapMediaType(ifType));
        }

        [Theory]
        [InlineData(0ul, 125_000ul, 1.0, true, 1_000L)]              // 125 000 B over 1 s = 1000 kbit/s
        [InlineData(1_000ul, 1_000ul, 1.0, true, 0L)]                // Idle: zero is a real reading
        [InlineData(0ul, 130ul, 1.0, true, 1L)]                      // 1.04 kbit/s rounds to 1
        [InlineData(0ul, 1_250_000_000ul, 1.0, true, 10_000_000L)]   // Saturated 10GbE
        [InlineData(0ul, 12_500_000_000ul, 1.0, true, 100_000_000L)] // Exactly the inclusive 100 Gbit/s cap
        [InlineData(0ul, 13_000_000_000ul, 1.0, false, 0L)]          // Above the cap: a glitch, not traffic
        [InlineData(0ul, 62_500ul, 0.5, true, 1_000L)]               // The interval band is inclusive on both ends
        [InlineData(0ul, 250_000ul, 2.0, true, 1_000L)]
        [InlineData(0ul, 125_000ul, 0.4, false, 0L)]                 // Two ticks on top of each other
        [InlineData(0ul, 125_000ul, 2.5, false, 0L)]                 // Descheduled or asleep: no rate to report
        [InlineData(0ul, 125_000ul, double.NaN, false, 0L)]
        [InlineData(125_000ul, 124_999ul, 1.0, false, 0L)]           // Counter went backwards: adapter reset
        public void TryComputeRateKbps_ShouldComputeWholeKbps_AndRejectUnusableSamples(
            ulong previous, ulong current, double elapsedSeconds, bool expectedOk, long expectedKbps)
        {
            // Act
            bool ok = NetworkThroughputService.TryComputeRateKbps(previous, current, elapsedSeconds, out long kbps);

            // Assert
            Assert.Equal(expectedOk, ok);
            Assert.Equal(expectedKbps, kbps);
        }

        [Fact]
        public void Initialize_ShouldReturnTheSameVerdictTwice_WhenCalledRepeatedly()
        {
            // Arrange - the probe verdict is latched either way: a machine that answered
            // once keeps answering, one that latched unavailable stays unavailable
            var service = new NetworkThroughputService();

            // Act
            bool first = service.Initialize();
            bool second = service.Initialize();

            // Assert
            Assert.Equal(first, second);
        }

        [Fact]
        public void Initialize_ShouldNotChangeWhatTryReadReports_WhetherOrNotItWasCalled()
        {
            // Arrange - eager (Initialize then TryRead) and lazy (TryRead alone) paths
            // funnel through the same probe, so their verdicts must agree on any machine
            var eager = new NetworkThroughputService();
            var lazy = new NetworkThroughputService();

            // Act
            _ = eager.Initialize();
            bool eagerRead = eager.TryRead(out _);
            bool lazyRead = lazy.TryRead(out _);

            // Assert
            Assert.Equal(eagerRead, lazyRead);
        }

        [Fact]
        public void TryRead_ShouldReturnTheSameVerdictTwice_WhenCalledRepeatedly()
        {
            // Arrange
            var service = new NetworkThroughputService();

            // Act
            bool first = service.TryRead(out _);
            bool second = service.TryRead(out _);

            // Assert - back-to-back reads either both see the adapter or both do not
            Assert.Equal(first, second);
        }

        [Fact]
        public void AdapterName_ShouldBeNullOrSubstantive_NeverEmpty()
        {
            // Arrange - the empty string is reserved for the truncation edge case in
            // BuildPayload; the service itself must produce a real name or nothing
            var service = new NetworkThroughputService();
            _ = service.Initialize();

            // Act
            string? name = service.AdapterName;

            // Assert
            if (name != null)
                Assert.False(string.IsNullOrWhiteSpace(name));
        }

        [Fact]
        public void TryRead_ShouldReportInBandValues_WhenItReportsAtAll()
        {
            // Arrange - the one live test: on any machine, with or without a network,
            // the service never throws and never reports an out-of-band value. The
            // read immediately after Initialize sits inside the probe's baseline
            // interval (< 0.5 s), so the rates are expected to be null here - the
            // assertions accept null-or-in-band rather than demanding traffic.
            var service = new NetworkThroughputService();
            _ = service.Initialize();

            // Act
            bool read = service.TryRead(out NetworkThroughputService.Sample sample);

            // Assert
            if (read)
            {
                if (sample.LinkMbps != null)
                    Assert.InRange(sample.LinkMbps.Value, 1, 400_000);
                if (sample.RxKbps != null)
                    Assert.InRange(sample.RxKbps.Value, 0, 100_000_000);
                if (sample.TxKbps != null)
                    Assert.InRange(sample.TxKbps.Value, 0, 100_000_000);

                int? mediaType = service.MediaType;
                Assert.NotNull(mediaType);
                Assert.InRange(mediaType.Value, 0, 2);
            }
        }

        /// <summary>
        /// Builds a MIB_IF_ROW2-shaped buffer with the given fields planted at the
        /// service's documented offsets. Unset regions stay zero, like the real API's
        /// output for fields a driver does not fill.
        /// </summary>
        private static byte[] BuildRow(
            uint interfaceIndex = 7,
            string? alias = "Ethernet",
            string? description = "Intel(R) Ethernet Controller I225-V",
            uint interfaceType = 6,
            uint mediaConnectState = 1,
            ulong receiveLinkSpeed = 2_500_000_000,
            ulong inOctets = 0,
            ulong outOctets = 0)
        {
            byte[] row = new byte[NetworkThroughputService.RowSize];
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(NetworkThroughputService.InterfaceIndexOffset), interfaceIndex);
            WriteWideString(row, AliasOffset, alias);
            WriteWideString(row, NetworkThroughputService.DescriptionOffset, description);
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(NetworkThroughputService.TypeOffset), interfaceType);
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(NetworkThroughputService.MediaConnectStateOffset), mediaConnectState);
            BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(NetworkThroughputService.ReceiveLinkSpeedOffset), receiveLinkSpeed);
            BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(NetworkThroughputService.InOctetsOffset), inOctets);
            BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(NetworkThroughputService.OutOctetsOffset), outOctets);
            return row;
        }

        private static void WriteWideString(byte[] row, int offset, string? value)
        {
            if (value == null)
                return;

            Encoding.Unicode.GetBytes(value).CopyTo(row.AsSpan(offset));
        }

#pragma warning disable CS0649 // Fields are never assigned - the struct exists only for Marshal.OffsetOf/SizeOf
        /// <summary>
        /// MIB_IF_ROW2 transcribed verbatim from netioapi.h so the CLR marshaler can
        /// compute the x64 layout independently of the service's hand-derived offsets.
        /// The eight BOOLEAN:1 bitfields of InterfaceAndOperStatusFlags pack into one
        /// byte; every enum is 4 bytes.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MibIfRow2
        {
            public ulong InterfaceLuid;
            public uint InterfaceIndex;
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
            public string Alias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
            public string Description;
            public uint PhysicalAddressLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] PhysicalAddress;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] PermanentPhysicalAddress;
            public uint Mtu;
            public uint Type;
            public uint TunnelType;
            public uint MediaType;
            public uint PhysicalMediumType;
            public uint AccessType;
            public uint DirectionType;
            public byte InterfaceAndOperStatusFlags;
            public uint OperStatus;
            public uint AdminStatus;
            public uint MediaConnectState;
            public Guid NetworkGuid;
            public uint ConnectionType;
            public ulong TransmitLinkSpeed;
            public ulong ReceiveLinkSpeed;
            public ulong InOctets;
            public ulong InUcastPkts;
            public ulong InNUcastPkts;
            public ulong InDiscards;
            public ulong InErrors;
            public ulong InUnknownProtos;
            public ulong InUcastOctets;
            public ulong InMulticastOctets;
            public ulong InBroadcastOctets;
            public ulong OutOctets;
            public ulong OutUcastPkts;
            public ulong OutNUcastPkts;
            public ulong OutDiscards;
            public ulong OutErrors;
            public ulong OutUcastOctets;
            public ulong OutMulticastOctets;
            public ulong OutBroadcastOctets;
            public ulong OutQLen;
        }
#pragma warning restore CS0649
    }
}
