using MetricsPusher.Services;

namespace MetricsPusher.Tests
{
    /// <summary>
    /// Every case here runs with no PawnIO driver, no elevation and no AMD hardware,
    /// because every case here is a <em>decode</em>: a register value in, a temperature
    /// out. That split is deliberate rather than convenient. A wrong decode does not throw
    /// and does not fail an IOCTL - it produces a number in the right neighbourhood, and
    /// the only place a factor-of-two or an off-by-49 is visible is against a value someone
    /// measured on real silicon.
    /// <para>
    /// Where a case carries a measured value from the dev box (Intel Core Ultra 7 155H,
    /// 2026-08-11, PawnIO 2.2.0) it says so, and those are the cases that matter most: the
    /// synthetic ones prove the arithmetic is self-consistent, the measured ones prove it
    /// agrees with the hardware. The AMD cases have no measured counterpart - no AMD part
    /// was available - so they pin the decode against the published register layout only.
    /// </para>
    /// </summary>
    public class CpuTemperatureProvidersTests
    {
        // IA32_PACKAGE_THERM_STATUS bit 31: "reading valid". Spelled out here so the tests
        // below read as register layout rather than as magic.
        private const long ReadingValidBit = 0x80000000L;

        // AMD THM_TCON_CUR_TMP flags (SMN 0x00059800).
        private const uint RangeSel = 0x80000;
        private const uint TjSel = 0x30000;

        [Fact]
        public void CpuTemperatureSource_None_ShouldBeTheDefault()
        {
            // Assert - a default-initialized source must not claim to be a real sensor;
            // the value is carried onto a future wire field, where "die" and "board
            // thermal zone" are not interchangeable
            Assert.Equal(CpuTemperatureSource.None, default(CpuTemperatureSource));
            Assert.Equal(0, (int)CpuTemperatureSource.None);
        }

        [Fact]
        public void EveryProvider_ShouldImplementTheProviderInterface()
        {
            // Assert - the service selects between these three by interface, so a provider
            // that quietly stopped implementing it would fail at the wiring, not here
            Assert.True(typeof(ICpuTemperatureProvider).IsAssignableFrom(typeof(IntelMsrTemperatureProvider)));
            Assert.True(typeof(ICpuTemperatureProvider).IsAssignableFrom(typeof(AmdSmnTemperatureProvider)));
            Assert.True(typeof(ICpuTemperatureProvider).IsAssignableFrom(typeof(ThermalZonePdhProvider)));
        }

        [Fact]
        public void TryDecodeTjMax_ShouldReadBits23To16()
        {
            // Arrange - the textbook value: IA32_TEMPERATURE_TARGET carrying 100 C
            long raw = 0x00640000L;

            // Act
            bool decoded = IntelMsrTemperatureProvider.TryDecodeTjMax(raw, out int tjMax);

            // Assert
            Assert.True(decoded);
            Assert.Equal(100, tjMax);
        }

        [Fact]
        public void TryDecodeTjMax_ShouldReturn110_ForTheMeasuredMeteorLakeValue()
        {
            // Arrange - MEASURED: MSR 0x1A2 on the dev box. 100 is the documented
            // fallback, not a typical reading, and a test written around 100 alone would
            // pass against a decode that ignored the register entirely
            long raw = 0x086E0000L;

            // Act
            bool decoded = IntelMsrTemperatureProvider.TryDecodeTjMax(raw, out int tjMax);

            // Assert
            Assert.True(decoded);
            Assert.Equal(110, tjMax);
        }

        [Theory]
        [InlineData(0x003C0000L, 60)]  // lower edge of the sanity band, accepted
        [InlineData(0x00820000L, 130)] // upper edge, accepted
        public void TryDecodeTjMax_ShouldAcceptTheEdgesOfTheSanityBand(long raw, int expected)
        {
            // Act
            bool decoded = IntelMsrTemperatureProvider.TryDecodeTjMax(raw, out int tjMax);

            // Assert - both edges are pinned; a band tested from one side only would pass
            // for a clamp that never rejected anything
            Assert.True(decoded);
            Assert.Equal(expected, tjMax);
        }

        [Theory]
        [InlineData(0x003B0000L)] // 59 - one below the band
        [InlineData(0x00830000L)] // 131 - one above
        [InlineData(0x00000000L)] // a register that read back as zero
        public void TryDecodeTjMax_ShouldReject_WhenOutsideTheSanityBand(long raw)
        {
            // Act
            bool decoded = IntelMsrTemperatureProvider.TryDecodeTjMax(raw, out int tjMax);

            // Assert - a wrong TjMax shifts every later reading by a constant, which is
            // exactly the error nobody notices, so the caller falls back to 100 instead
            Assert.False(decoded);
            Assert.Equal(0, tjMax);
        }

        [Fact]
        public void DefaultTjMax_ShouldBe100()
        {
            // Assert - the documented fallback when 0x1A2 cannot be read at all
            Assert.Equal(100, IntelMsrTemperatureProvider.DefaultTjMax);
        }

        [Fact]
        public void TryDecodePackageTemperature_ShouldSubtractDeltaFromTjMax()
        {
            // Arrange - bit 31 set, deltaT 0x1E, TjMax 100
            long raw = ReadingValidBit | (0x1EL << 16);

            // Act
            bool decoded = IntelMsrTemperatureProvider.TryDecodePackageTemperature(raw, 100, out float celsius);

            // Assert
            Assert.True(decoded);
            Assert.Equal(70f, celsius, 0.01f);
        }

        [Fact]
        public void TryDecodePackageTemperature_ShouldMatchTheMeasuredHardwareSample()
        {
            // Arrange - MEASURED: MSR 0x1B1 = 0x000000008822080A alongside TjMax 110 on
            // the dev box, decoded there as 76.0 C. This is the one case that proves the
            // mask and the shift against silicon rather than against themselves
            long raw = 0x000000008822080AL;

            // Act
            bool decoded = IntelMsrTemperatureProvider.TryDecodePackageTemperature(raw, 110, out float celsius);

            // Assert
            Assert.True(decoded);
            Assert.Equal(76f, celsius, 0.01f);
        }

        [Fact]
        public void TryDecodePackageTemperature_ShouldReject_WhenTheValidBitIsClear()
        {
            // Arrange - the same deltaT as the measured sample, but bit 31 clear
            long raw = 0x0000000000220000L;

            // Act
            bool decoded = IntelMsrTemperatureProvider.TryDecodePackageTemperature(raw, 110, out float celsius);

            // Assert - without bit 31 the delta field is not a reading; taken anyway it
            // would decode to a plausible 76 C, which is why this case exists
            Assert.False(decoded);
            Assert.Equal(0f, celsius);
        }

        [Fact]
        public void TryDecodePackageTemperature_ShouldIgnoreTheUpperHalfOfTheRegister()
        {
            // Arrange - the driver hands back an int64; only EAX carries the reading, and
            // stray upper bits must not be mistaken for the valid bit
            long raw = unchecked((long)0xFFFFFFFF00220000UL);

            // Act
            bool decoded = IntelMsrTemperatureProvider.TryDecodePackageTemperature(raw, 110, out float celsius);

            // Assert
            Assert.False(decoded);
            Assert.Equal(0f, celsius);
        }

        [Fact]
        public void TryDecodePackageTemperature_ShouldReject_WhenTheResultFallsBelowZero()
        {
            // Arrange - the largest deltaT the 7-bit field holds, against a low TjMax
            long raw = ReadingValidBit | (0x7FL << 16);

            // Act
            bool decoded = IntelMsrTemperatureProvider.TryDecodePackageTemperature(raw, 100, out float celsius);

            // Assert - -27 C is rejected by Constants.IsValidTemperature, the single
            // validator this feature shares with the GPU temperature on the wire
            Assert.False(Constants.IsValidTemperature(-27f));
            Assert.False(decoded);
            Assert.Equal(0f, celsius);
        }

        [Fact]
        public void DecodeTctl_ShouldScaleTheRawCodeBy125Millidegrees()
        {
            // Arrange - code 608 = 76.000 C, no range correction
            long raw = SmnTemperature(608, 0);

            // Act
            float tctl = AmdSmnTemperatureProvider.DecodeTctl(raw);

            // Assert
            Assert.Equal(76f, tctl, 0.01f);
        }

        [Fact]
        public void DecodeTctl_ShouldSubtract49_WhenRangeSelIsSet()
        {
            // Arrange - code 800 = 100.000 C with RANGE_SEL, i.e. the -49 C range
            long raw = SmnTemperature(800, RangeSel);

            // Act
            float tctl = AmdSmnTemperatureProvider.DecodeTctl(raw);

            // Assert
            Assert.Equal(51f, tctl, 0.01f);
        }

        [Fact]
        public void DecodeTctl_ShouldSubtract49_WhenTjSelIsBothBits()
        {
            // Arrange - the other signal for the same range: TJ_SEL == 0x30000
            long raw = SmnTemperature(800, TjSel);

            // Act
            float tctl = AmdSmnTemperatureProvider.DecodeTctl(raw);

            // Assert
            Assert.Equal(51f, tctl, 0.01f);
        }

        [Fact]
        public void DecodeTctl_ShouldNotSubtract49_WhenOnlyOneTjSelBitIsSet()
        {
            // Arrange - TJ_SEL is the PAIR of bits; either one alone is not the signal
            long raw = SmnTemperature(800, 0x10000);

            // Act
            float tctl = AmdSmnTemperatureProvider.DecodeTctl(raw);

            // Assert - a decode that tested "any TJ_SEL bit" would report 51 here
            Assert.Equal(100f, tctl, 0.01f);
        }

        [Fact]
        public void DecodeTctl_ShouldSubtract49_OnlyOnce_WhenBothSignalsAreSet()
        {
            // Arrange - RANGE_SEL and TJ_SEL together describe one range, not two
            long raw = SmnTemperature(800, RangeSel | TjSel);

            // Act
            float tctl = AmdSmnTemperatureProvider.DecodeTctl(raw);

            // Assert
            Assert.Equal(51f, tctl, 0.01f);
        }

        [Theory]
        [InlineData("AMD Ryzen 5 1600X", -20f)]
        [InlineData("AMD Ryzen 7 1700X", -20f)]
        [InlineData("AMD Ryzen 7 1800X", -20f)]
        [InlineData("AMD Ryzen Threadripper 1950X", -27f)]
        [InlineData("AMD Ryzen Threadripper 2990WX", -27f)]
        [InlineData("AMD Ryzen 7 2700X", -10f)]
        [InlineData("AMD Ryzen 9 9950X", 0f)]
        [InlineData("AMD Ryzen 9 5950X", 0f)]
        [InlineData("AMD Ryzen 5 1600", 0f)]
        [InlineData("AMD Ryzen 7 2700", 0f)]
        [InlineData("AMD Ryzen Threadripper 3990X", 0f)]
        [InlineData("AMD Ryzen Threadripper PRO 5995WX", 0f)]
        [InlineData("", 0f)]
        [InlineData(null, 0f)]
        public void TdieOffset_ShouldMatchTheFirstGenerationTable(string? cpuName, float expected)
        {
            // Act
            float offset = AmdSmnTemperatureProvider.TdieOffset(cpuName);

            // Assert - the non-X and later parts are in the table precisely because a
            // substring match that fired on them would inflate every Zen 2+ reading
            Assert.Equal(expected, offset, 0.01f);
        }

        [Fact]
        public void TryDecodeTdie_ShouldApplyTheOffsetOnTopOfTctl()
        {
            // Arrange - 1800X, code 800 = Tctl 100 C, so Tdie is 80 C
            long raw = SmnTemperature(800, 0);

            // Act
            bool decoded = AmdSmnTemperatureProvider.TryDecodeTdie(raw, "AMD Ryzen 7 1800X", out float celsius);

            // Assert
            Assert.True(decoded);
            Assert.Equal(80f, celsius, 0.01f);
        }

        [Fact]
        public void TryDecodeTdie_ShouldEqualTctl_ForAPartWithNoOffset()
        {
            // Arrange - Zen 2 and later carry no Tctl inflation
            long raw = SmnTemperature(608, 0);

            // Act
            bool decoded = AmdSmnTemperatureProvider.TryDecodeTdie(raw, "AMD Ryzen 9 9950X", out float celsius);

            // Assert
            Assert.True(decoded);
            Assert.Equal(76f, celsius, 0.01f);
        }

        [Fact]
        public void TryDecodeTdie_ShouldReject_WhenTheDecodedValueIsAbsurd()
        {
            // Arrange - the largest code the 11-bit field holds: 255.875 C
            long raw = SmnTemperature(2047, 0);

            // Act
            bool decoded = AmdSmnTemperatureProvider.TryDecodeTdie(raw, "AMD Ryzen 9 9950X", out float celsius);

            // Assert - a garbage SMN read (the PCI index/data race this design takes a
            // mutex to avoid) lands here, not on the wire
            Assert.False(decoded);
            Assert.Equal(0f, celsius);
        }

        [Fact]
        public void TryDecodeDeciKelvin_ShouldMatchTheMeasuredThermalZoneSample()
        {
            // Act - MEASURED: 3342 deci-Kelvin from
            // \Thermal Zone Information(\_TZ.THRM)\High Precision Temperature
            bool decoded = ThermalZonePdhProvider.TryDecodeDeciKelvin(3342, out float celsius);

            // Assert
            Assert.True(decoded);
            Assert.Equal(61.05f, celsius, 0.01f);
        }

        [Fact]
        public void TryDecodeDeciKelvin_ShouldMatchTheSecondMeasuredThermalZoneSample()
        {
            // Act - MEASURED on the same counter under load, 3492 -> 76.05 C, close
            // enough to the package MSR's 76.0 C that a unit mistake would be invisible
            // without both
            bool decoded = ThermalZonePdhProvider.TryDecodeDeciKelvin(3492, out float celsius);

            // Assert
            Assert.True(decoded);
            Assert.Equal(76.05f, celsius, 0.01f);
        }

        [Fact]
        public void TryDecodeDeciKelvin_ShouldReject_WhenTheZoneReportsZero()
        {
            // Act - 0 K decodes to -273.15 C, which is what an unpopulated zone reports
            bool decoded = ThermalZonePdhProvider.TryDecodeDeciKelvin(0, out float celsius);

            // Assert
            Assert.False(decoded);
            Assert.Equal(0f, celsius);
        }

        [Fact]
        public void TryDecodeDeciKelvin_ShouldReject_WhenTheValueIsAbsurd()
        {
            // Act - 5000 deci-Kelvin is 226.85 C
            bool decoded = ThermalZonePdhProvider.TryDecodeDeciKelvin(5000, out float celsius);

            // Assert - the shared 0-150 C validator, not a second band invented here
            Assert.False(Constants.IsValidTemperature(226.85f));
            Assert.False(decoded);
            Assert.Equal(0f, celsius);
        }

        [Fact]
        public void TryDecodeDeciKelvin_ShouldReject_WhenTheCounterIsNotANumber()
        {
            // Act - PDH hands back a double; a formatted counter can carry NaN
            bool decoded = ThermalZonePdhProvider.TryDecodeDeciKelvin(double.NaN, out float celsius);

            // Assert
            Assert.False(decoded);
            Assert.Equal(0f, celsius);
        }

        /// <summary>
        /// Builds a THM_TCON_CUR_TMP register value: the 11-bit temperature code sits at
        /// bits 31:21, and the range flags live in the low half.
        /// </summary>
        /// <param name="code">The temperature code, in units of 0.125 C.</param>
        /// <param name="flags">RANGE_SEL / TJ_SEL bits to set.</param>
        /// <returns>The register value as the driver would return it.</returns>
        private static long SmnTemperature(uint code, uint flags)
        {
            return (long)((code << 21) | flags);
        }
    }
}
