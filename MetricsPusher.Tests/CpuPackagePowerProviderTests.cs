using System.Diagnostics;
using MetricsPusher.Services;

namespace MetricsPusher.Tests
{
    /// <summary>
    /// RAPL decode cases, all of them pure arithmetic over supplied register values, so
    /// they run with no PawnIO driver, no elevation and on either vendor's silicon.
    /// <para>
    /// Two of these exist because of a specific defect the implementation plan carried
    /// into review. The plan's table described <c>MSR_PKG_POWER_INFO</c> bits 14:0 as
    /// "TDP" and <c>MSR_PKG_POWER_LIMIT</c> bits 14:0 as "PL1" without mentioning that
    /// both are in RAPL <em>power</em> units and must be divided by 2^PSU - a different
    /// field from the ESU the energy path uses. On the dev box PSU is 3, so the raw fields
    /// read 224 and 512 against a 28 W part. Taken as watts, both would have passed the
    /// plan's own "positive and under 1000 W" guard and shipped silently. The two PSU
    /// cases below are the regression test for that, and they are the reason this file
    /// pins raw register values rather than pre-scaled numbers.
    /// </para>
    /// <para>
    /// Values marked MEASURED came off the dev box (Intel Core Ultra 7 155H, 2026-08-11)
    /// through the live PawnIO 2.2.0 driver.
    /// </para>
    /// </summary>
    public class CpuPackagePowerProviderTests
    {
        // MEASURED: MSR_RAPL_POWER_UNIT (0x606) on the dev box - PSU 3 in bits 3:0, ESU 14
        // in bits 12:8.
        private const long MeasuredRaplPowerUnit = 0x00000000000A0E03L;

        // MEASURED: MSR_PKG_POWER_INFO (0x614), bits 14:0 = 224 -> 28.00 W with PSU 3.
        private const long MeasuredPackagePowerInfo = 0x00120000000000E0L;

        // MEASURED: MSR_PKG_POWER_LIMIT (0x610), bits 14:0 = 512 -> 64.00 W with PSU 3.
        private const long MeasuredPackagePowerLimit = 0x0042820000DD8200L;

        [Fact]
        public void CpuPowerSource_None_ShouldBeTheDefault()
        {
            // Assert - a default-initialized source must not read as a working sensor
            Assert.Equal(CpuPowerSource.None, default(CpuPowerSource));
            Assert.Equal(0, (int)CpuPowerSource.None);
        }

        [Fact]
        public void DecodeEnergyStatusUnit_ShouldReadBits12To8()
        {
            // Act
            int esu = CpuPackagePowerProvider.DecodeEnergyStatusUnit(MeasuredRaplPowerUnit);

            // Assert - MEASURED: ESU 14 on the dev box
            Assert.Equal(14, esu);
        }

        [Fact]
        public void DecodePowerStatusUnit_ShouldReadBits3To0()
        {
            // Act
            int psu = CpuPackagePowerProvider.DecodePowerStatusUnit(MeasuredRaplPowerUnit);

            // Assert - MEASURED: PSU 3. A decode that reused the ESU field would answer
            // 14 here and divide the limits by 16384 instead of 8
            Assert.Equal(3, psu);
        }

        [Fact]
        public void EnergyUnitJoules_ShouldBe61Microjoules_ForEsu14()
        {
            // Act
            double joules = CpuPackagePowerProvider.EnergyUnitJoules(14);

            // Assert - 1 / 2^14 J, the Intel unit on every recent part
            Assert.Equal(1.0 / 16384.0, joules, 12);
            Assert.Equal(61.035, joules * 1_000_000.0, 3);
        }

        [Fact]
        public void EnergyUnitJoules_ShouldBe15Microjoules_ForEsu16()
        {
            // Act - the AMD unit; the same decode has to serve both vendors
            double joules = CpuPackagePowerProvider.EnergyUnitJoules(16);

            // Assert
            Assert.Equal(15.2588, joules * 1_000_000.0, 3);
        }

        [Fact]
        public void TryDecodePowerLimitWatts_ShouldDivideTheTdpFieldByTwoToThePsu()
        {
            // Act - MEASURED: 0x614 raw field 224, PSU 3
            bool decoded = CpuPackagePowerProvider.TryDecodePowerLimitWatts(MeasuredPackagePowerInfo, 3, out float watts);

            // Assert - 28.00 W is the Core Ultra 7 155H's rated base power. Unscaled this
            // reads 224 W, which is not implausible enough for any guard to catch
            Assert.True(decoded);
            Assert.Equal(28f, watts, 0.01f);
        }

        [Fact]
        public void TryDecodePowerLimitWatts_ShouldDivideThePl1FieldByTwoToThePsu()
        {
            // Act - MEASURED: 0x610 raw field 512, PSU 3
            bool decoded = CpuPackagePowerProvider.TryDecodePowerLimitWatts(MeasuredPackagePowerLimit, 3, out float watts);

            // Assert - 64.00 W is a plausible PL1 for this chassis; 512 W is not, and
            // would still have passed the 1000 W guard
            Assert.True(decoded);
            Assert.Equal(64f, watts, 0.01f);
        }

        [Fact]
        public void TryDecodePowerLimitWatts_ShouldReject_WhenTheFieldIsZero()
        {
            // Act - a register that read back as zero is an absent limit, not a 0 W part
            bool decoded = CpuPackagePowerProvider.TryDecodePowerLimitWatts(0L, 3, out float watts);

            // Assert
            Assert.False(decoded);
            Assert.Equal(0f, watts);
        }

        [Fact]
        public void TryDecodePowerLimitWatts_ShouldReject_WhenTheResultIsAbsurd()
        {
            // Arrange - the full 15-bit field with PSU 0, i.e. 32767 W
            long raw = 0x7FFFL;

            // Act
            bool decoded = CpuPackagePowerProvider.TryDecodePowerLimitWatts(raw, 0, out float watts);

            // Assert
            Assert.False(decoded);
            Assert.Equal(0f, watts);
        }

        [Fact]
        public void TryDecodePowerLimitWatts_ShouldIgnoreEverythingAboveBit14()
        {
            // Arrange - 0x610 carries enable bits, a time window and the PL2 field in the
            // bits above the one this reads; MEASURED, the real register has all of them
            // set. Masking to 15 bits is what keeps them out of the answer
            long raw = MeasuredPackagePowerLimit;

            // Act
            bool decoded = CpuPackagePowerProvider.TryDecodePowerLimitWatts(raw, 3, out float watts);

            // Assert
            Assert.True(decoded);
            Assert.True(watts < 100f);
        }

        [Fact]
        public void EnergyDelta_ShouldSubtract_WhenTheCounterDidNotWrap()
        {
            // Act - MEASURED: two consecutive 0x611 samples on the dev box
            uint delta = CpuPackagePowerProvider.EnergyDelta(0xB23D09D6u, 0xB23F946Au);

            // Assert
            Assert.Equal(166548u, delta);
        }

        [Fact]
        public void EnergyDelta_ShouldWrapCleanly_WhenTheCounterRolledOver()
        {
            // Act - the accumulator is 32-bit and wraps roughly every four minutes under
            // load, so this is routine rather than an edge case
            uint delta = CpuPackagePowerProvider.EnergyDelta(0xFFFFFF00u, 0x100u);

            // Assert - 0x200, the true modular distance. The formula the plan spells out,
            // (0xFFFFFFFF - last) + now, answers 0x1FF: correct to within one count, but
            // one count short of what the plan's own test table asks for
            Assert.Equal(0x200u, delta);
        }

        [Fact]
        public void EnergyDelta_ShouldBeZero_WhenTheCounterDidNotMove()
        {
            // Act
            uint delta = CpuPackagePowerProvider.EnergyDelta(0x1000u, 0x1000u);

            // Assert - zero, not a full 4-billion-count wrap; the CPU was simply idle
            // between two very close samples
            Assert.Equal(0u, delta);
        }

        [Fact]
        public void TryComputeWatts_ShouldReturnOneWatt_ForOneJoulePerSecond()
        {
            // Act - 16384 units of 1/16384 J is exactly 1 J, over exactly 1 s
            bool computed = CpuPackagePowerProvider.TryComputeWatts(16384u, 14, 1.0, out float watts);

            // Assert
            Assert.True(computed);
            Assert.Equal(1f, watts, 0.001f);
        }

        [Fact]
        public void TryComputeWatts_ShouldMatchTheMeasuredHardwareSample()
        {
            // Act - MEASURED end to end on the dev box: 0x611 moved 166548 counts in
            // 1.0116 s with ESU 14, which the spike decoded as 10.05 W at light idle
            bool computed = CpuPackagePowerProvider.TryComputeWatts(166548u, 14, 1.0116, out float watts);

            // Assert
            Assert.True(computed);
            Assert.Equal(10.05f, watts, 0.01f);
        }

        [Theory]
        [InlineData(0.5)] // lower edge of the accepted window
        [InlineData(2.0)] // upper edge
        public void TryComputeWatts_ShouldAcceptTheEdgesOfTheElapsedWindow(double elapsedSeconds)
        {
            // Act
            bool computed = CpuPackagePowerProvider.TryComputeWatts(16384u, 14, elapsedSeconds, out float watts);

            // Assert - both edges pinned, so a window that never rejected anything cannot
            // pass this pair
            Assert.True(computed);
            Assert.True(watts > 0f);
        }

        [Theory]
        [InlineData(0.49)]  // too short: a double tick turns jitter into a power spike
        [InlineData(2.01)]  // too long: the first tick after a sleep/resume
        [InlineData(0.0)]   // no elapsed time at all
        [InlineData(-1.0)]  // a clock that went backwards
        public void TryComputeWatts_ShouldReject_WhenElapsedIsOutsideTheWindow(double elapsedSeconds)
        {
            // Act
            bool computed = CpuPackagePowerProvider.TryComputeWatts(16384u, 14, elapsedSeconds, out float watts);

            // Assert
            Assert.False(computed);
            Assert.Equal(0f, watts);
        }

        [Fact]
        public void TryComputeWatts_ShouldReject_WhenNoEnergyWasAccumulated()
        {
            // Act - a package that consumed nothing measurable did not report 0 W, it
            // failed to report
            bool computed = CpuPackagePowerProvider.TryComputeWatts(0u, 14, 1.0, out float watts);

            // Assert
            Assert.False(computed);
            Assert.Equal(0f, watts);
        }

        [Fact]
        public void TryComputeWatts_ShouldReject_WhenTheResultIsAbsurd()
        {
            // Arrange - a delta only a misread register produces
            uint delta = 100_000_000u;

            // Act
            bool computed = CpuPackagePowerProvider.TryComputeWatts(delta, 14, 1.0, out float watts);

            // Assert - over 1000 W, so it is a decode fault rather than a CPU
            Assert.False(computed);
            Assert.Equal(0f, watts);
        }

        [Fact]
        public void RaplEnergyWindow_ShouldReportNothingOnTheFirstSample()
        {
            // Arrange
            var window = new RaplEnergyWindow(14);

            // Act - one sample of a free-running accumulator is not a power reading
            bool computed = window.TryAdvance(0u, 0L, out float watts);

            // Assert
            Assert.False(computed);
            Assert.Equal(0f, watts);
        }

        [Fact]
        public void RaplEnergyWindow_ShouldReportOnTheSecondSample()
        {
            // Arrange
            var window = new RaplEnergyWindow(14);
            _ = window.TryAdvance(0u, 0L, out _);

            // Act - one second later, 16384 counts on
            bool computed = window.TryAdvance(16384u, Stopwatch.Frequency, out float watts);

            // Assert
            Assert.True(computed);
            Assert.Equal(1f, watts, 0.001f);
        }

        [Fact]
        public void RaplEnergyWindow_ShouldSurviveACounterWrap()
        {
            // Arrange - primed 0x2000 counts below the 32-bit roof
            var window = new RaplEnergyWindow(14);
            _ = window.TryAdvance(0xFFFFE000u, 0L, out _);

            // Act - 16384 counts later: 0x2000 of them reach the roof and 0x2000 land
            // past it, so the sample reads LOWER than the one before it
            bool computed = window.TryAdvance(0x2000u, Stopwatch.Frequency, out float watts);

            // Assert - 1 W, not a negative and not a four-billion-count spike. Under a
            // sustained load this happens about every four minutes
            Assert.True(computed);
            Assert.Equal(1f, watts, 0.001f);
        }

        [Fact]
        public void RaplEnergyWindow_ShouldRecoverAfterALongGap()
        {
            // Arrange - primed, then a ten-second gap standing in for a sleep/resume
            var window = new RaplEnergyWindow(14);
            _ = window.TryAdvance(0u, 0L, out _);
            bool acrossTheGap = window.TryAdvance(16384u, 10 * Stopwatch.Frequency, out float gapWatts);

            // Act - the very next tick, one second after the gap
            bool computed = window.TryAdvance(32768u, 11 * Stopwatch.Frequency, out float watts);

            // Assert - the rejected tick still advanced the window, so recovery is
            // immediate. A window that only advanced on success would divide the next
            // delta by eleven seconds and under-report for as long as anyone watched
            Assert.False(acrossTheGap);
            Assert.Equal(0f, gapWatts);
            Assert.True(computed);
            Assert.Equal(1f, watts, 0.001f);
        }

        [Fact]
        public void RaplEnergyWindow_ShouldReportNothingAgainAfterAReset()
        {
            // Arrange - a primed window
            var window = new RaplEnergyWindow(14);
            _ = window.TryAdvance(0u, 0L, out _);

            // Act - Reset drops the baseline, so the next sample primes rather than
            // reports; this is what a provider does when the sensor went away and came
            // back, where the accumulated delta spans an unknown interval
            window.Reset();
            bool computed = window.TryAdvance(16384u, Stopwatch.Frequency, out float watts);

            // Assert
            Assert.False(computed);
            Assert.Equal(0f, watts);
        }
    }
}
