using MetricsPusher.Services;

namespace MetricsPusher.Tests
{
    /// <summary>
    /// SampledMetric is pure and clock-injected - every Get is handed the tick to
    /// compare against - so these tests need no GPU, no timers and no sleeping.
    /// </summary>
    public class SampledMetricTests
    {
        [Fact]
        public void Get_ShouldReadOnEveryCall_WhenCadenceIsLive()
        {
            // Arrange
            int reads = 0;
            var metric = new SampledMetric<int?>(SampledMetric.Live, () => ++reads);

            // Act & Assert - a live metric never serves a cached value, not even
            // twice within the same tick
            Assert.Equal(1, metric.Get(0, highFidelity: false));
            Assert.Equal(2, metric.Get(0, highFidelity: false));
            Assert.Equal(3, metric.Get(5, highFidelity: false));
            Assert.Equal(3, reads);
        }

        [Fact]
        public void Get_ShouldReadOnce_WhenCadenceIsSession()
        {
            // Arrange
            int reads = 0;
            var metric = new SampledMetric<string?>(SampledMetric.Session, () =>
            {
                reads++;
                return "NVIDIA GeForce RTX 4090";
            });

            // Act - a full minute of sweeps
            for (long t = 0; t <= 60_000; t += 1000)
                Assert.Equal("NVIDIA GeForce RTX 4090", metric.Get(t, highFidelity: false));

            // Assert
            Assert.Equal(1, reads);
        }

        [Fact]
        public void Get_ShouldRetryOnTheNextCall_WhenASessionReadReturnsNull()
        {
            // Arrange - the read fails until the third call
            int reads = 0;
            string? available = null;
            var metric = new SampledMetric<string?>(SampledMetric.Session, () =>
            {
                reads++;
                return available;
            });

            // Act & Assert - a failed session read must not latch null for the session
            Assert.Null(metric.Get(0, highFidelity: false));
            Assert.Null(metric.Get(1, highFidelity: false));
            Assert.Equal(2, reads);

            available = "NVIDIA GeForce RTX 4090";
            Assert.Equal("NVIDIA GeForce RTX 4090", metric.Get(2, highFidelity: false));
            Assert.Equal(3, reads);

            // ... and the first success is what latches it for good
            Assert.Equal("NVIDIA GeForce RTX 4090", metric.Get(3, highFidelity: false));
            Assert.Equal("NVIDIA GeForce RTX 4090", metric.Get(99_999, highFidelity: false));
            Assert.Equal(3, reads);
        }

        [Fact]
        public void Get_ShouldReadOnlyWhenTheIntervalHasElapsed_WhenCadenceIsAnInterval()
        {
            // Arrange
            int reads = 0;
            var metric = new SampledMetric<int?>(3000, () => ++reads);

            // Act & Assert
            Assert.Equal(1, metric.Get(0, highFidelity: false));       // first call always reads
            Assert.Equal(1, metric.Get(2999, highFidelity: false));    // 1 ms short of due - cached
            Assert.Equal(1, reads);
            Assert.Equal(2, metric.Get(3000, highFidelity: false));    // exactly due - reads
            Assert.Equal(2, reads);
        }

        [Fact]
        public void Get_ShouldMeasureTheIntervalFromTheLastRead_WhenCallsAreIrregular()
        {
            // Arrange
            int reads = 0;
            var metric = new SampledMetric<int?>(2000, () => ++reads);

            // Act & Assert - a Get that does not read must not restart the clock,
            // otherwise a fast consumer could starve the metric forever
            metric.Get(0, highFidelity: false);
            for (long t = 100; t < 2000; t += 100)
                metric.Get(t, highFidelity: false);

            Assert.Equal(1, reads);
            metric.Get(2000, highFidelity: false);
            Assert.Equal(2, reads);
        }

        [Fact]
        public void Get_ShouldNotRetryBeforeTheNextDueTime_WhenAnIntervalReadReturnsNull()
        {
            // Arrange - a sensor that is failing right now
            int reads = 0;
            var metric = new SampledMetric<int?>(3000, () =>
            {
                reads++;
                return null;
            });

            // Act & Assert - null goes on the wire, but the failing read is retried at
            // the metric's own cadence, not on every sweep
            Assert.Null(metric.Get(0, highFidelity: false));
            Assert.Null(metric.Get(1000, highFidelity: false));
            Assert.Null(metric.Get(2999, highFidelity: false));
            Assert.Equal(1, reads);

            Assert.Null(metric.Get(3000, highFidelity: false));
            Assert.Equal(2, reads);
        }

        [Fact]
        public void Reset_ShouldForceTheNextGetToRead()
        {
            // Arrange
            int reads = 0;
            var metric = new SampledMetric<int?>(3000, () => ++reads);

            Assert.Equal(1, metric.Get(0, highFidelity: false));
            Assert.Equal(1, metric.Get(100, highFidelity: false));

            // Act - what handle loss and Shutdown do, so a new GPU cannot inherit
            // the old one's cached values
            metric.Reset();

            // Assert
            Assert.Equal(2, metric.Get(101, highFidelity: false));
            Assert.Equal(2, reads);
        }

        [Fact]
        public void Get_ShouldReadOnEveryCall_WhenHighFidelityIsEnabled()
        {
            // Arrange
            int reads = 0;
            var metric = new SampledMetric<int?>(3000, () => ++reads);

            // Act - the GPU Monitor window is open: every metric behaves as live
            metric.Get(0, highFidelity: true);
            metric.Get(1, highFidelity: true);
            metric.Get(2, highFidelity: true);

            // Assert
            Assert.Equal(3, reads);

            // ... and the metric returns to its own cadence when the window closes
            metric.Get(3, highFidelity: false);
            Assert.Equal(3, reads);
        }

        [Fact]
        public void Get_ShouldReadOnEveryCall_WhenHighFidelityIsEnabledOnASessionMetric()
        {
            // Arrange
            int reads = 0;
            var metric = new SampledMetric<string?>(SampledMetric.Session, () =>
            {
                reads++;
                return "GPU";
            });

            // Act
            metric.Get(0, highFidelity: true);
            metric.Get(1, highFidelity: true);

            // Assert - high fidelity treats every cadence as live, session included
            Assert.Equal(2, reads);
        }

        [Fact]
        public void Get_ShouldReportThatItExecuted_WhenTheReadRan()
        {
            // Arrange
            var metric = new SampledMetric<int?>(3000, () => 42);

            // Act
            metric.Get(0, highFidelity: false);

            // Assert - the handle-loss rule counts only reads that actually ran
            Assert.True(metric.LastGetExecuted);
            Assert.False(metric.LastGetReturnedNull);

            // Act - the same value, served from cache this time
            metric.Get(1, highFidelity: false);

            // Assert
            Assert.False(metric.LastGetExecuted);
        }

        [Fact]
        public void Get_ShouldReportANullResult_WhenTheReadFailed()
        {
            // Arrange
            var metric = new SampledMetric<int?>(SampledMetric.Live, () => null);

            // Act
            metric.Get(0, highFidelity: false);

            // Assert
            Assert.True(metric.LastGetExecuted);
            Assert.True(metric.LastGetReturnedNull);
        }

        [Fact]
        public void Reset_ShouldClearTheExecutionFlags_SoAResetMetricLooksUnread()
        {
            // Arrange
            var metric = new SampledMetric<int?>(SampledMetric.Live, () => null);
            metric.Get(0, highFidelity: false);

            // Act
            metric.Reset();

            // Assert - a reset registry must not be read as "every read failed"
            Assert.False(metric.LastGetExecuted);
            Assert.False(metric.LastGetReturnedNull);
        }
    }
}
