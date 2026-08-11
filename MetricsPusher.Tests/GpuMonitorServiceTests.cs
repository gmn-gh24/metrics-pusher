using System.Reflection;
using MetricsPusher.Services;
using NvAPIWrapper.Native.Exceptions;
using NvAPIWrapper.Native.General;

namespace MetricsPusher.Tests
{
    [Collection(ProcessGlobalCollection.Name)]
    public class GpuMonitorServiceTests
    {
        // xUnit constructs the class per test, so this resets GpuMonitorService's
        // shared static state before every test: on a machine with an NVIDIA GPU,
        // whichever test runs Initialize first would otherwise leave the service
        // live and break every "_BeforeInitialization"/"_WhenNotInitialized" test
        // that happens to run after it.
        public GpuMonitorServiceTests()
        {
            GpuMonitorService.Shutdown();
        }

        [Fact]
        public void IsGpuAvailable_ShouldNotThrow_BeforeInitialization()
        {
            // Act & Assert - should not throw even before Initialize is called
            _ = GpuMonitorService.IsGpuAvailable;
        }

        [Fact]
        public void GetGpuMetrics_ShouldReturnNonNull_BeforeInitialization()
        {
            // Act
            var metrics = GpuMonitorService.GetGpuMetrics();

            // Assert
            Assert.NotNull(metrics);
        }

        [Fact]
        public void GetGpuMetrics_ShouldReturnEmptyMetrics_WhenNotInitialized()
        {
            // Act
            var metrics = GpuMonitorService.GetGpuMetrics();

            // Assert - all fields should be null when not initialized
            Assert.Null(metrics.Name);
            Assert.Null(metrics.Temperature);
            Assert.Null(metrics.UsagePercent);
            Assert.Null(metrics.VramUsedMB);
            Assert.Null(metrics.VramTotalMB);
            Assert.Null(metrics.FanSpeedPercent);
            Assert.Null(metrics.PowerPercent);
            Assert.Null(metrics.PowerWatts);
            Assert.Null(metrics.PowerLimitWatts);
            Assert.Null(metrics.CoreClockMHz);
            Assert.Null(metrics.MemoryClockMHz);
        }

        [Fact]
        public void GetGpuMetrics_ShouldReturnPlausibleValues_WhenNvidiaGpuPresent()
        {
            // Arrange - integration-style: on machines without an NVIDIA GPU this
            // degenerates to a no-op rather than failing
            GpuMonitorService.Initialize();
            if (!GpuMonitorService.IsGpuAvailable)
                return;

            // Act
            var metrics = GpuMonitorService.GetGpuMetrics();

            // Assert - a responsive GPU must yield at least one field; an all-null sweep
            // is what the service reads as a lost handle
            Assert.True(HasAnyValue(metrics));

            // Individual nulls are legal (a driver may refuse an individual query), but
            // present values must have survived the validation guards
            if (metrics.PowerPercent != null)
                Assert.InRange(metrics.PowerPercent.Value, 0, 200);
            if (metrics.PowerWatts != null)
                Assert.InRange(metrics.PowerWatts.Value, 0, 1999);
            if (metrics.CoreClockMHz != null)
                Assert.InRange(metrics.CoreClockMHz.Value, 1, 9999);
            if (metrics.MemoryClockMHz != null)
                Assert.InRange(metrics.MemoryClockMHz.Value, 1, 19999);
            if (metrics.UsagePercent != null)
                Assert.InRange(metrics.UsagePercent.Value, 0, 100);
            if (metrics.FanSpeedPercent != null)
                Assert.InRange(metrics.FanSpeedPercent.Value, 0, 100);

            // Both VRAM figures come from one atomic native read, so used can never
            // exceed total (with two separate reads it could, briefly)
            if (metrics.VramUsedMB != null && metrics.VramTotalMB != null)
                Assert.True(metrics.VramUsedMB.Value <= metrics.VramTotalMB.Value, $"VramUsedMB {metrics.VramUsedMB} exceeds VramTotalMB {metrics.VramTotalMB}");
        }

        [Fact]
        public void GetGpuMetrics_ShouldReturnTheSameSnapshot_WhenCalledTwiceWithinCacheTtl()
        {
            // Arrange
            GpuMonitorService.Initialize();
            if (!GpuMonitorService.IsGpuAvailable)
                return;

            // Act - two back-to-back calls are milliseconds apart, far inside the TTL
            var first = GpuMonitorService.GetGpuMetrics();
            var second = GpuMonitorService.GetGpuMetrics();

            // Assert - the second call must reuse the published snapshot rather than
            // sweeping NVAPI again
            Assert.Same(first, second);
        }

        [Fact]
        public void GetGpuMetrics_ShouldResample_WhenShutdownAndReinitialized()
        {
            // Arrange
            GpuMonitorService.Initialize();
            if (!GpuMonitorService.IsGpuAvailable)
                return;

            var before = GpuMonitorService.GetGpuMetrics();

            // Act - a restart well inside the cache TTL and inside the handle
            // re-acquisition back-off: Shutdown must reset both
            GpuMonitorService.Shutdown();
            GpuMonitorService.Initialize();
            if (!GpuMonitorService.IsGpuAvailable)
                return;

            var after = GpuMonitorService.GetGpuMetrics();

            // Assert - a fresh sweep, not the pre-shutdown snapshot, and not the empty
            // metrics a still-backed-off handle acquisition would produce
            Assert.NotSame(before, after);
            Assert.True(HasAnyValue(after));
        }

        [Fact]
        public void ActiveBackend_ShouldBeNone_BeforeAnySweepHasAcquiredAHandle()
        {
            // Assert - the latch starts closed; the ctor's Shutdown is what guarantees it
            Assert.Equal(GpuMonitorService.GpuBackend.None, GpuMonitorService.ActiveBackend);
        }

        [Fact]
        public void GetGpuMetrics_ShouldPreferTheNvmlBackend_WhenNvmlIsAvailable()
        {
            // Arrange - integration-style: no NVIDIA GPU means no-op, not a failure
            GpuMonitorService.Initialize();
            if (!GpuMonitorService.IsGpuAvailable)
                return;

            // Act - the backend is latched by the acquire the first sweep performs
            _ = GpuMonitorService.GetGpuMetrics();

            // Assert - a sweep that produced metrics must have latched a stack, and NVML
            // is tried first: the fallback may only be in play when NVML is unavailable
            Assert.NotEqual(GpuMonitorService.GpuBackend.None, GpuMonitorService.ActiveBackend);
            Assert.Equal(
                NvmlService.IsAvailable ? GpuMonitorService.GpuBackend.Nvml : GpuMonitorService.GpuBackend.Nvapi,
                GpuMonitorService.ActiveBackend);
        }

        [Fact]
        public void GetGpuMetrics_ShouldPopulateEveryNvmlBackedField_WhenTheNvmlBackendIsActive()
        {
            // Arrange
            GpuMonitorService.Initialize();
            if (!GpuMonitorService.IsGpuAvailable)
                return;

            // Act
            var metrics = GpuMonitorService.GetGpuMetrics();
            if (GpuMonitorService.ActiveBackend != GpuMonitorService.GpuBackend.Nvml)
                return; // The NVAPI fallback is covered by the plausibility test above

            // Assert - every metric NVML supports on every board must arrive on the very
            // first sweep: on this backend there are no cadence tiers left to wait for.
            // (fan and the power PERCENTAGE are deliberately absent from this list -
            // fan_v2 answers NVML_ERROR_NOT_SUPPORTED on boards whose fan the driver
            // does not expose, and the percentage needs an enforced limit to divide by;
            // both are range-checked instead, since a null there is legal hardware
            // behavior, not a bug. Watts is asserted below - it needs no denominator.)
            Assert.NotNull(metrics.Name);
            Assert.NotNull(metrics.Temperature);
            Assert.NotNull(metrics.UsagePercent);
            Assert.NotNull(metrics.VramUsedMB);
            Assert.NotNull(metrics.VramTotalMB);
            Assert.NotNull(metrics.CoreClockMHz);
            Assert.NotNull(metrics.MemoryClockMHz);

            Assert.InRange(metrics.Temperature.Value, 1f, 150f);
            Assert.InRange(metrics.UsagePercent.Value, 0, 100);
            Assert.InRange(metrics.CoreClockMHz.Value, 1, 9999);
            Assert.InRange(metrics.MemoryClockMHz.Value, 1, 19999);
            Assert.True(metrics.VramUsedMB.Value <= metrics.VramTotalMB.Value);

            // Watts needs no denominator, so unlike the percentage it is expected on
            // every NVML board - and when both are present they must describe the same
            // reading, which is the single-read contract observed end to end
            Assert.NotNull(metrics.PowerWatts);
            Assert.InRange(metrics.PowerWatts.Value, 0, 1999);

            // v5.12.0: the enforced limit is acquire-time state - cached when the NVML
            // handle was acquired, as the percentage's denominator - so a healthy
            // acquire must carry it, inside a cap that excludes zero
            Assert.NotNull(metrics.PowerLimitWatts);
            Assert.InRange(metrics.PowerLimitWatts.Value, 1, 1999);

            // End-to-end pin that all three fields came from one milliwatt pair
            if (metrics.PowerPercent != null)
            {
                int reconstructed = (int)Math.Round(metrics.PowerWatts.Value * 100.0 / metrics.PowerLimitWatts.Value);
                Assert.InRange(reconstructed - metrics.PowerPercent.Value, -1, 1);
            }

            if (metrics.FanSpeedPercent != null)
                Assert.InRange(metrics.FanSpeedPercent.Value, 0, 100);
            if (metrics.PowerPercent != null)
                Assert.InRange(metrics.PowerPercent.Value, 0, 200);
        }

        [Fact]
        public void Shutdown_ShouldResetTheBackendLatchAndReleaseNvml()
        {
            // Arrange - drive a real acquire so there is state to reset
            GpuMonitorService.Initialize();
            if (GpuMonitorService.IsGpuAvailable)
                _ = GpuMonitorService.GetGpuMetrics();

            // Act
            GpuMonitorService.Shutdown();

            // Assert - the per-test isolation contract: a restart must re-probe both
            // stacks from scratch rather than inherit a latched backend, a stale cached
            // power limit, or an NVML device handle from the previous session
            Assert.Equal(GpuMonitorService.GpuBackend.None, GpuMonitorService.ActiveBackend);
            Assert.False(NvmlService.IsAvailable);
        }

        [Fact]
        public void GetGpuMetrics_ShouldReacquireTheBackend_WhenShutdownAndReinitialized()
        {
            // Arrange
            GpuMonitorService.Initialize();
            if (!GpuMonitorService.IsGpuAvailable)
                return;

            _ = GpuMonitorService.GetGpuMetrics();
            var backendBefore = GpuMonitorService.ActiveBackend;

            // Act - a restart well inside the 5 s re-acquire back-off
            GpuMonitorService.Shutdown();
            GpuMonitorService.Initialize();
            if (!GpuMonitorService.IsGpuAvailable)
                return;

            var after = GpuMonitorService.GetGpuMetrics();

            // Assert - the same stack is chosen again, from a clean probe
            Assert.Equal(backendBefore, GpuMonitorService.ActiveBackend);
            Assert.True(HasAnyValue(after));
        }

        [Fact]
        public void CombineProbes_ShouldSkipTheNvmlProbe_WhenNvapiAlreadyAnswered()
        {
            // Arrange - NVAPI stays first so every machine that worked before behaves
            // exactly as it did, and pays nothing new
            int nvmlProbes = 0;

            bool ProbeNvml()
            {
                nvmlProbes++;
                return false;
            }

            // Act
            bool available = GpuMonitorService.CombineProbes(true, ProbeNvml);

            // Assert
            Assert.True(available);
            Assert.Equal(0, nvmlProbes);
        }

        [Fact]
        public void CombineProbes_ShouldEnableTheFeature_WhenOnlyNvmlAnswers()
        {
            // Arrange - the machine this ruling exists for: NVML works, NVAPI does not
            // (TCC-mode compute board, some vGPU adapters). Before the second opinion the
            // whole GPU feature - window, tray temperature, the UDP push - was silently
            // off there, even though the sweep's own primary backend could read the board.
            int nvmlProbes = 0;

            bool ProbeNvml()
            {
                nvmlProbes++;
                return true;
            }

            // Act
            bool available = GpuMonitorService.CombineProbes(false, ProbeNvml);

            // Assert
            Assert.True(available);
            Assert.Equal(1, nvmlProbes);
        }

        [Fact]
        public void CombineProbes_ShouldStayDisabled_WhenNeitherStackAnswers()
        {
            // Arrange - a machine with no NVIDIA GPU: exactly one NVML probe for the whole
            // session (NvmlService latches the failed load), then the feature stays off
            int nvmlProbes = 0;

            bool ProbeNvml()
            {
                nvmlProbes++;
                return false;
            }

            // Act
            bool available = GpuMonitorService.CombineProbes(false, ProbeNvml);

            // Assert
            Assert.False(available);
            Assert.Equal(1, nvmlProbes);
        }

        [Fact]
        public void ProbeNvmlAvailability_ShouldReportTheBoard_AndHandTheLayerToTheSweep()
        {
            // Integration-style: on a machine without NVML this asserts the "nothing left
            // behind" half instead of failing.
            try
            {
                // Act - the probe must do its own initialization; nothing has run yet
                bool available = GpuMonitorService.ProbeNvmlAvailability();

                if (!available)
                {
                    // Assert - a probe that says no must leave the layer closed, or the
                    // next AcquireBackend would latch a backend the probe just rejected
                    Assert.False(NvmlService.IsAvailable);
                    return;
                }

                // Assert - saying yes means a real sensor answered, not merely that the
                // library loaded; and the layer is left INITIALIZED on purpose, so the
                // first sweep's AcquireBackend latches it without a second load
                Assert.True(NvmlService.IsAvailable);
                Assert.NotNull(NvmlService.GetTemperature());
            }
            finally
            {
                // The hand-off is process-global state: no test may inherit it
                NvmlService.Shutdown();
            }
        }

        [Fact]
        public void Shutdown_ShouldReleaseNvml_WhenOnlyTheProbeEverInitializedIt()
        {
            // Arrange - the leak the unconditional NvmlService.Shutdown in Shutdown()
            // closes: the probe hands an initialized layer to the first sweep, but a
            // session where no consumer ever asked for metrics has no sweep, so no backend
            // was ever latched and DropBackend alone would not release it.
            if (!GpuMonitorService.ProbeNvmlAvailability())
                return; // No NVML on this machine - nothing to leak

            Assert.True(NvmlService.IsAvailable);

            // Act
            GpuMonitorService.Shutdown();

            // Assert
            Assert.False(NvmlService.IsAvailable);
            Assert.Equal(GpuMonitorService.GpuBackend.None, GpuMonitorService.ActiveBackend);
        }

        [Fact]
        public void BuildPayloadJson_ShouldCarryEveryGpuWireKey_OnEveryTickAcrossAllCadences()
        {
            // Integration-style: on machines without an NVIDIA GPU this degenerates to a
            // no-op rather than failing.
            GpuMonitorService.Initialize();
            if (!GpuMonitorService.IsGpuAvailable)
                return;

            // The GPU keys of the wire contract (push_metrics.md section 4), matched
            // with their quotes and colon so "load" cannot match "cpuLoad".
            var requiredKeys = new List<string>
            {
                "\"gpu\":", "\"temp\":", "\"load\":", "\"vramUsed\":",
                "\"vramTotal\":", "\"fan\":", "\"power\":", "\"clock\":", "\"vramClock\":"
            };

            // watts and limitW exist only on the NVML backend - NVAPI cannot report
            // either at all, and their absence there is contractual (section 5), not
            // a failure.
            _ = GpuMonitorService.GetGpuMetrics(); // Latch the backend before asking
            if (GpuMonitorService.ActiveBackend == GpuMonitorService.GpuBackend.Nvml)
            {
                requiredKeys.Add("\"watts\":");
                requiredKeys.Add("\"limitW\":");
            }

            // Four ~1 s ticks, like the push loop. On the NVML backend every field is
            // read on every sweep; on the NVAPI fallback this also spans every cadence
            // tier (per-sweep, 2 s VRAM, 3 s fan/power) so each both reads and re-serves
            // from cache at least once. This is the only guard that a native read path
            // has not silently latched null on real hardware - unit tests see neither
            // driver stack.
            for (int tick = 0; tick < 4; tick++)
            {
                // Outlive the 950 ms snapshot TTL so each tick is a genuine sweep
                if (tick > 0)
                    Thread.Sleep(1100);

                var json = GpuDisplayPushService.BuildPayloadJson(
                    GpuMonitorService.GetGpuMetrics(),
                    SystemMetricsService.GetSystemMetrics(),
                    "WIRE-KEYS");

                Assert.NotNull(json);
                var missing = requiredKeys.Where(key => !json.Contains(key, StringComparison.Ordinal)).ToList();
                Assert.True(missing.Count == 0, $"Tick {tick}: datagram is missing {string.Join(", ", missing)} - {json}");
            }
        }

        [Fact]
        public void Initialize_ShouldNotThrow_WhenCalledMultipleTimes()
        {
            // Act & Assert - should be safe to call multiple times
            GpuMonitorService.Initialize();
            GpuMonitorService.Initialize();
        }

        [Fact]
        public void Shutdown_ShouldNotThrow_WhenCalledWithoutInitialize()
        {
            // Act & Assert
            GpuMonitorService.Shutdown();
        }

        [Fact]
        public void IsHandleLost_ShouldReturnFalse_WhenNoReadExecutedThisSweep()
        {
            // Arrange - a registry nobody has swept yet (or one just Reset): no read
            // ran, so there is no evidence either way
            var registry = new SampledMetric[]
            {
                new SampledMetric<int?>(SampledMetric.Live, () => null),
                new SampledMetric<int?>(3000, () => null),
            };

            // Act & Assert
            Assert.False(GpuMonitorService.IsHandleLost(registry));
        }

        [Fact]
        public void IsHandleLost_ShouldReturnTrue_WhenEveryExecutedReadReturnedNull()
        {
            // Arrange
            var usage = new SampledMetric<int?>(SampledMetric.Live, () => null);
            var temperature = new SampledMetric<float?>(SampledMetric.Live, () => null);
            var registry = new SampledMetric[] { usage, temperature };

            // Act - one sweep
            _ = usage.Get(0, highFidelity: false);
            _ = temperature.Get(0, highFidelity: false);

            // Assert - every sensor that answered answered "nothing": the handle died,
            // not the sensors
            Assert.True(GpuMonitorService.IsHandleLost(registry));
        }

        [Fact]
        public void IsHandleLost_ShouldReturnFalse_WhenAnyExecutedReadReturnedAValue()
        {
            // Arrange - one surviving sensor must keep the handle alive, otherwise
            // per-field failures would be read as handle loss. Zero is a value, not
            // an absence (an idle GPU legitimately reports 0 % fan and 0 % load).
            var usage = new SampledMetric<int?>(SampledMetric.Live, () => null);
            var fan = new SampledMetric<int?>(SampledMetric.Live, () => 0);
            var temperature = new SampledMetric<float?>(SampledMetric.Live, () => null);
            var registry = new SampledMetric[] { usage, fan, temperature };

            // Act
            _ = usage.Get(0, highFidelity: false);
            _ = fan.Get(0, highFidelity: false);
            _ = temperature.Get(0, highFidelity: false);

            // Assert
            Assert.False(GpuMonitorService.IsHandleLost(registry));
        }

        [Fact]
        public void IsHandleLost_ShouldIgnoreCachedValues_WhenEveryExecutedReadReturnedNull()
        {
            // Arrange - the regression this rule exists for: the GPU name is read once
            // per session, so a rule that looked at the assembled fields (Task 2's
            // "all fields null") could never fire again once the name was cached
            var sessionName = new SampledMetric<string?>(SampledMetric.Session, () => "NVIDIA GeForce RTX 4090");
            var liveSensor = new SampledMetric<int?>(SampledMetric.Live, () => null);
            var registry = new SampledMetric[] { sessionName, liveSensor };

            // Act & Assert - sweep 1: the name read runs and succeeds, so the handle
            // demonstrably answers
            _ = sessionName.Get(0, highFidelity: false);
            _ = liveSensor.Get(0, highFidelity: false);
            Assert.False(GpuMonitorService.IsHandleLost(registry));

            // Act & Assert - sweep 2: the name is served from cache and every read that
            // actually ran failed
            _ = sessionName.Get(1000, highFidelity: false);
            _ = liveSensor.Get(1000, highFidelity: false);
            Assert.True(GpuMonitorService.IsHandleLost(registry));
        }

        [Fact]
        public void ShouldDropHandle_ShouldHoldTheHandle_OnASingleLostSweep()
        {
            // Arrange
            int consecutive = 0;

            // Act & Assert - one all-null sweep is a suspicion, not a verdict
            Assert.False(GpuMonitorService.ShouldDropHandle(true, ref consecutive));
            Assert.Equal(1, consecutive);
        }

        [Fact]
        public void ShouldDropHandle_ShouldDrop_OnTheSecondConsecutiveLostSweep()
        {
            // Arrange
            int consecutive = 0;

            // Act & Assert - genuine handle loss costs one extra sweep (~1 s) to confirm
            Assert.False(GpuMonitorService.ShouldDropHandle(true, ref consecutive));
            Assert.True(GpuMonitorService.ShouldDropHandle(true, ref consecutive));
        }

        [Fact]
        public void ShouldDropHandle_ShouldNeverDrop_WhenLostSweepsAlternateWithHealthyOnes()
        {
            // Arrange - the machine this guard exists for: temperature, usage and clock
            // are legitimately null on every sweep (the probe validated a different GPU
            // than gpus[0]; a vGPU reporting IsPresent:false), while the 2 s and 3 s
            // cadences answer whenever they come due. With a one-strike rule that
            // oscillation dropped a LIVE handle every 5 s forever.
            int consecutive = 0;

            // Act & Assert
            for (int sweep = 0; sweep < 20; sweep++)
                Assert.False(GpuMonitorService.ShouldDropHandle(sweep % 2 == 0, ref consecutive));
        }

        [Fact]
        public void ShouldDropHandle_ShouldClearItsCount_OnAHealthySweep()
        {
            // Arrange
            int consecutive = 0;

            // Act
            Assert.False(GpuMonitorService.ShouldDropHandle(true, ref consecutive));
            Assert.False(GpuMonitorService.ShouldDropHandle(false, ref consecutive));

            // Assert - strikes must be consecutive to count
            Assert.Equal(0, consecutive);
            Assert.False(GpuMonitorService.ShouldDropHandle(true, ref consecutive));
        }

        [Fact]
        public void ShouldDropHandle_ShouldClearItsCount_AfterDroppingTheHandle()
        {
            // Arrange
            int consecutive = 0;
            Assert.False(GpuMonitorService.ShouldDropHandle(true, ref consecutive));
            Assert.True(GpuMonitorService.ShouldDropHandle(true, ref consecutive));

            // Assert - the count belongs to the handle that was just dropped; whatever
            // is enumerated next gets its own two strikes
            Assert.Equal(0, consecutive);
            Assert.False(GpuMonitorService.ShouldDropHandle(true, ref consecutive));
        }

        [Fact]
        public void Shutdown_ShouldClearHighFidelity_SoAnUnreleasedHoldCannotLeak()
        {
            // Arrange - what a failed assertion between paired SetHighFidelity calls
            // leaves behind; without this reset it leaked into every later test
            GpuMonitorService.SetHighFidelity(true);

            // Act
            GpuMonitorService.Shutdown();

            // Assert
            Assert.False(GpuMonitorService.HighFidelityEnabled);
        }

        [Fact]
        public void SetHighFidelity_ShouldStayEnabled_UntilEveryEnableIsMatched()
        {
            // Assert - off by default
            Assert.False(GpuMonitorService.HighFidelityEnabled);

            // Act & Assert - nested holders (a second window, a re-entered Load)
            GpuMonitorService.SetHighFidelity(true);
            GpuMonitorService.SetHighFidelity(true);
            Assert.True(GpuMonitorService.HighFidelityEnabled);

            GpuMonitorService.SetHighFidelity(false);
            Assert.True(GpuMonitorService.HighFidelityEnabled);

            GpuMonitorService.SetHighFidelity(false);
            Assert.False(GpuMonitorService.HighFidelityEnabled);
        }

        [Fact]
        public void SetHighFidelity_ShouldClampAtZero_WhenDisabledMoreOftenThanEnabled()
        {
            // Act - stray disables (a close without a matching open)
            GpuMonitorService.SetHighFidelity(false);
            GpuMonitorService.SetHighFidelity(false);

            // Assert
            Assert.False(GpuMonitorService.HighFidelityEnabled);

            // Act & Assert - a negative counter would have swallowed this enable
            GpuMonitorService.SetHighFidelity(true);
            Assert.True(GpuMonitorService.HighFidelityEnabled);

            GpuMonitorService.SetHighFidelity(false);
            Assert.False(GpuMonitorService.HighFidelityEnabled);
        }

        [Fact]
        public void IsUnsupportedApiException_ShouldReturnTrue_ForBothNvapiUnsupportedSignals()
        {
            // Arrange - the two exceptions NVAPI raises when an entry point is not
            // supported. NVIDIANotSupportedException derives from
            // System.NotSupportedException, NOT from NVIDIAApiException, so a filter
            // naming only the latter would miss exactly the legacy-GPU / older-driver
            // case the usage and cooler fallback latches exist for.
            Assert.False(typeof(NVIDIAApiException).IsAssignableFrom(typeof(NVIDIANotSupportedException)));

            // Act & Assert
            Assert.True(GpuMonitorService.IsUnsupportedApiException(CreateApiException()));
            Assert.True(GpuMonitorService.IsUnsupportedApiException(CreateNotSupportedException()));
        }

        [Fact]
        public void IsUnsupportedApiException_ShouldReturnFalse_ForAnUnrelatedException()
        {
            // Assert - the filter must not latch a permanent fallback (and swallow the
            // exception) for failures that are not "this driver lacks the entry point"
            Assert.False(GpuMonitorService.IsUnsupportedApiException(new InvalidOperationException("boom")));
            Assert.False(GpuMonitorService.IsUnsupportedApiException(new NotSupportedException("plain")));
        }

        [Theory]
        [InlineData(1000, 900, true)]   // 100 ms old - fresh
        [InlineData(1000, 51, true)]    // 949 ms old - still fresh
        [InlineData(1000, 50, false)]   // exactly TTL (950 ms) - expired
        [InlineData(1000, 0, false)]    // 1000 ms old - expired
        [InlineData(1000, 1000, true)]  // same instant - fresh
        public void IsCacheFresh_ShouldReturnExpected_ForVariousTickPairs(long nowTicks, long lastReadTicks, bool expected)
        {
            // Act
            var result = GpuMonitorService.IsCacheFresh(nowTicks, lastReadTicks);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(150_000u, 450_000u, 33)]   // 33.3 % -> 33
        [InlineData(300_000u, 450_000u, 67)]   // 66.7 % -> 67
        [InlineData(450_000u, 450_000u, 100)]  // exactly at the enforced limit
        [InlineData(0u, 450_000u, 0)]          // an idle board draws little, not nothing: 0 % is a value
        [InlineData(472_500u, 450_000u, 105)]  // transient boost above the limit is real, not garbage
        [InlineData(900_000u, 450_000u, 200)]  // exactly at the validation cap - still legal
        public void DerivePowerPercent_ShouldRoundDrawAgainstTheEnforcedLimit_WhenBothReadingsExist(uint milliwatts, uint limitMilliwatts, int expected)
        {
            // Act - NVML reports watts, not a percentage; the wire carries a percentage,
            // so the pair is divided here (both readings are milliwatts, see NvmlService)
            var result = GpuMonitorService.DerivePowerPercent(milliwatts, limitMilliwatts);

            // Assert
            Assert.Equal(expected, result);
        }

        [Fact]
        public void DerivePowerPercent_ShouldReturnNull_WhenEitherReadingIsMissing()
        {
            // Assert - per-field independence: a missing reading is "unknown", and the
            // wire omits the key rather than sending a made-up 0 %
            Assert.Null(GpuMonitorService.DerivePowerPercent(null, 450_000u));
            Assert.Null(GpuMonitorService.DerivePowerPercent(150_000u, null));
            Assert.Null(GpuMonitorService.DerivePowerPercent(null, null));
        }

        [Fact]
        public void DerivePowerPercent_ShouldReturnNull_WhenTheEnforcedLimitIsZero()
        {
            // Assert - a zero denominator is a broken read, not an infinite percentage
            Assert.Null(GpuMonitorService.DerivePowerPercent(150_000u, 0u));
        }

        [Fact]
        public void DerivePowerPercent_ShouldReturnNull_WhenTheDerivedPercentExceedsTheValidationCap()
        {
            // Assert - same rule the NVAPI path applies to its own percentage: past
            // 200 % of TDP the reading is sensor garbage and is dropped, not clamped.
            // The raw ratio is validated before rounding, so 200.4 % is rejected even
            // though it would have rounded back to the cap.
            Assert.Null(GpuMonitorService.DerivePowerPercent(990_000u, 450_000u)); // 220 %
            Assert.Null(GpuMonitorService.DerivePowerPercent(901_800u, 450_000u)); // 200.4 %
        }

        [Theory]
        [InlineData(300_000u, 300)]      // 300.0 W
        [InlineData(300_400u, 300)]      // rounds down
        [InlineData(300_600u, 301)]      // rounds up
        [InlineData(0u, 0)]              // see the consistency test below: 0 is a value on both fields
        [InlineData(1_999_000u, 1999)]   // widest value under the cap - 4 digits, which the budget is pinned against
        public void DerivePowerWatts_ShouldConvertMilliwattsToWholeWatts(uint milliwatts, int expected)
        {
            // Act - NVML reports milliwatts; the wire carries whole watts
            var result = GpuMonitorService.DerivePowerWatts(milliwatts);

            // Assert
            Assert.Equal(expected, result);
        }

        [Fact]
        public void DerivePowerWatts_ShouldReturnNull_WhenTheReadFailedOrTheValueIsImplausible()
        {
            // Assert - no reading is "unknown"; past the cap it is sensor garbage and is
            // dropped rather than clamped, exactly like the percent and clock guards.
            // No board draws 2 kW, and the cap also bounds the field at 4 digits.
            Assert.Null(GpuMonitorService.DerivePowerWatts(null));
            Assert.Null(GpuMonitorService.DerivePowerWatts(2_000_000u)); // exactly at the exclusive cap
            Assert.Null(GpuMonitorService.DerivePowerWatts(9_999_000u));
        }

        [Fact]
        public void DerivePowerWatts_ShouldTreatZeroAsAValue_LikeDerivePowerPercentDoes()
        {
            // Assert - both fields come from ONE reading, so they must agree about what
            // that reading means. A 0 that the percent path publishes must not be a null
            // on the watts path, or one datagram would contradict itself.
            Assert.Equal(0, GpuMonitorService.DerivePowerPercent(0u, 450_000u));
            Assert.Equal(0, GpuMonitorService.DerivePowerWatts(0u));
        }

        [Theory]
        [InlineData(480_000u, 480)]      // the RTX 3090 Ti's real enforced limit (~480 W)
        [InlineData(300_400u, 300)]      // rounds down
        [InlineData(300_600u, 301)]      // rounds up
        [InlineData(1_999_499u, 1999)]   // widest value under the cap - 4 digits, which the budget is pinned against
        public void DerivePowerLimitWatts_ShouldConvertMilliwattsToWholeWatts(uint limitMilliwatts, int expected)
        {
            var result = GpuMonitorService.DerivePowerLimitWatts(limitMilliwatts);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void DerivePowerLimitWatts_ShouldReturnNull_WhenTheReadFailedOrTheValueIsImplausible()
        {
            // No reading is "unknown"; past the cap it is sensor garbage, dropped not clamped
            Assert.Null(GpuMonitorService.DerivePowerLimitWatts(null));
            Assert.Null(GpuMonitorService.DerivePowerLimitWatts(2_000_000u)); // exactly at the exclusive cap
            Assert.Null(GpuMonitorService.DerivePowerLimitWatts(1_999_999u)); // 1999.999 rounds to 2000 - cap applied after rounding
            Assert.Null(GpuMonitorService.DerivePowerLimitWatts(9_999_000u));
        }

        [Fact]
        public void DerivePowerLimitWatts_ShouldExcludeZero_UnlikeDerivePowerWatts()
        {
            // A zero enforced limit is a broken read, not a real board state - the same
            // verdict DerivePowerPercent passes on a zero denominator. The draw treats 0
            // as a value; the limit must not, and the asymmetry is deliberate.
            Assert.Null(GpuMonitorService.DerivePowerLimitWatts(0u));
            Assert.Null(GpuMonitorService.DerivePowerLimitWatts(499u)); // 0.499 W rounds to 0, which the cap excludes
            Assert.Equal(0, GpuMonitorService.DerivePowerWatts(0u));    // contrast pin: the draw's cap admits 0
        }

        [Theory]
        [InlineData(65_000u, 480_000u, 14, 65, 480)]  // exact: round(65 * 100 / 480) = 14 = power
        [InlineData(64_600u, 480_000u, 13, 65, 480)]  // double rounding: off by exactly one count
        public void DerivedPowerTrio_ShouldReconstructThePercentWithinOneCount_FromOneMilliwattPair(
            uint drawMilliwatts, uint limitMilliwatts, int expectedPercent, int expectedWatts, int expectedLimitW)
        {
            var percent = GpuMonitorService.DerivePowerPercent(drawMilliwatts, limitMilliwatts);
            var watts = GpuMonitorService.DerivePowerWatts(drawMilliwatts);
            var limitW = GpuMonitorService.DerivePowerLimitWatts(limitMilliwatts);

            Assert.Equal(expectedPercent, percent);
            Assert.Equal(expectedWatts, watts);
            Assert.Equal(expectedLimitW, limitW);

            // watts and limitW are rounded before a consumer divides, while power divides
            // the raw milliwatts, so exact equality is NOT guaranteed - the second row is
            // the counterexample this tolerance exists for.
            int reconstructed = (int)Math.Round(watts!.Value * 100.0 / limitW!.Value);
            Assert.InRange(reconstructed - percent!.Value, -1, 1);
        }

        [Fact]
        public void DerivePower_ShouldProduceBothFields_FromOneReading()
        {
            // Act - the single-read contract: one milliwatt reading, two wire fields
            var result = GpuMonitorService.DerivePower(300_000u, 450_000u);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(67, result.Value.Percent);  // 300 W of a 450 W limit
            Assert.Equal(300, result.Value.Watts);
        }

        [Fact]
        public void DerivePower_ShouldStillProduceWatts_WhenTheEnforcedLimitIsUnknown()
        {
            // Assert - the regression this pairing exists to prevent. Watts needs no
            // denominator, so a board whose enforced-limit read failed still reports its
            // draw; only the percentage is unknown. (v5.10.0-N3 skipped the draw read
            // entirely when the limit was null - correct while the percent was the only
            // consumer of that read, wrong the moment watts joined it.)
            var result = GpuMonitorService.DerivePower(300_000u, null);

            Assert.NotNull(result);
            Assert.Null(result.Value.Percent);
            Assert.Equal(300, result.Value.Watts);
        }

        [Fact]
        public void DerivePower_ShouldReturnNull_WhenTheReadingItselfIsMissing()
        {
            // Assert - null means "the read failed", which is what the handle-loss rule
            // counts. A tuple with null halves would claim the handle answered.
            Assert.Null(GpuMonitorService.DerivePower(null, 450_000u));
            Assert.Null(GpuMonitorService.DerivePower(null, null));
        }

        [Fact]
        public void DerivePower_ShouldReturnNull_WhenNeitherDerivationSurvivesValidation()
        {
            // Assert - 2500 W against a 450 W limit: 555 % fails the percent cap and
            // 2500 fails the watts cap, so nothing usable came out of the read
            Assert.Null(GpuMonitorService.DerivePower(2_500_000u, 450_000u));
        }

        [Fact]
        public void PowerMetric_ShouldCostOneReadForBothFields_WhenSampled()
        {
            // Arrange - the shape that guarantees the single-read contract at runtime:
            // percent and watts ride ONE SampledMetric entry, so a sweep cannot pay for
            // the draw twice however the cadences fall
            int reads = 0;
            var metric = new SampledMetric<(int? Percent, int? Watts)?>(3000, () =>
            {
                reads++;
                return GpuMonitorService.DerivePower(300_000u, 450_000u);
            });

            // Act
            var first = metric.Get(0, highFidelity: false);
            var second = metric.Get(1, highFidelity: false); // inside the cadence - served from cache

            // Assert
            Assert.Equal(1, reads);
            Assert.Equal(67, first?.Percent);
            Assert.Equal(300, first?.Watts);
            Assert.Equal(first, second);
        }

        [Theory]
        [InlineData(2610L, 10000, 2610)]     // typical boost core clock
        [InlineData(9999L, 10000, 9999)]     // widest value under the core cap
        [InlineData(10000L, 10000, null)]    // the cap is exclusive
        [InlineData(0L, 10000, null)]        // a zero clock is a failed read, not a stopped GPU
        [InlineData(-1L, 10000, null)]
        [InlineData(null, 10000, null)]
        [InlineData(10501L, 20000, 10501)]   // GDDR6X under load - above the core cap, legal for VRAM
        [InlineData(19999L, 20000, 19999)]   // widest value under the memory cap
        [InlineData(20000L, 20000, null)]
        public void ValidateClockMHz_ShouldDropImplausibleClocks_AndPassPlausibleOnes(long? mhz, int maxExclusiveMHz, int? expected)
        {
            // Act - one validator for both clock domains; the cap is what differs
            var result = GpuMonitorService.ValidateClockMHz(mhz, maxExclusiveMHz);

            // Assert
            Assert.Equal(expected, result);
        }

        [Fact]
        public void ValidateClockMHz_ShouldUseCapsThatBoundTheWireWidth()
        {
            // Assert - the datagram budget is pinned against these caps: 4 digits for
            // the core clock, 5 for the VRAM clock (see the worst-case budget test)
            Assert.Equal(9999, GpuMonitorService.ValidateClockMHz(9999L, GpuMonitorService.MaxValidCoreClockMHz));
            Assert.Null(GpuMonitorService.ValidateClockMHz(10000L, GpuMonitorService.MaxValidCoreClockMHz));
            Assert.Equal(19999, GpuMonitorService.ValidateClockMHz(19999L, GpuMonitorService.MaxValidMemoryClockMHz));
            Assert.Null(GpuMonitorService.ValidateClockMHz(20000L, GpuMonitorService.MaxValidMemoryClockMHz));
        }

        [Fact]
        public void ReadEveryMetric_ShouldSuspendEveryCadence_WhenTheBackendIsNvml()
        {
            // Arrange - the NVML reads cost ~0.02-0.3 ms each, so there is nothing left
            // for a cadence tier to amortize: every metric is read on every sweep
            int reads = 0;
            var metric = new SampledMetric<int?>(3000, () => ++reads);

            // Act - three sweeps 1 ms apart, far inside the metric's own 3 s cadence
            for (long tick = 0; tick < 3; tick++)
                metric.Get(tick, GpuMonitorService.ReadEveryMetric(highFidelity: false, GpuMonitorService.GpuBackend.Nvml));

            // Assert
            Assert.Equal(3, reads);
        }

        [Fact]
        public void ReadEveryMetric_ShouldKeepTheCadences_WhenTheBackendIsTheNvapiFallback()
        {
            // Arrange - NVAPI reads are expensive (the power topology read alone was
            // 13.15 ms of a 16.4 ms sweep), so the fallback keeps its tiers
            int reads = 0;
            var metric = new SampledMetric<int?>(3000, () => ++reads);

            // Act
            for (long tick = 0; tick < 3; tick++)
                metric.Get(tick, GpuMonitorService.ReadEveryMetric(highFidelity: false, GpuMonitorService.GpuBackend.Nvapi));

            // Assert - the first call always reads; the other two are served from cache
            Assert.Equal(1, reads);
        }

        [Fact]
        public void ReadEveryMetric_ShouldStillHonorHighFidelity_OnTheNvapiFallback()
        {
            // Arrange - SetHighFidelity semantics are unchanged by the backend model
            int reads = 0;
            var metric = new SampledMetric<int?>(3000, () => ++reads);

            // Act
            for (long tick = 0; tick < 3; tick++)
                metric.Get(tick, GpuMonitorService.ReadEveryMetric(highFidelity: true, GpuMonitorService.GpuBackend.Nvapi));

            // Assert
            Assert.Equal(3, reads);
        }

        // "The GPU answered something." The service's own handle-loss rule works on
        // executed reads rather than assembled fields, so the integration tests carry
        // their own field-level check.
        private static bool HasAnyValue(GpuMetrics metrics)
        {
            return metrics.Name != null
                || metrics.Temperature != null
                || metrics.UsagePercent != null
                || metrics.VramUsedMB != null
                || metrics.VramTotalMB != null
                || metrics.FanSpeedPercent != null
                || metrics.PowerPercent != null
                || metrics.PowerWatts != null
                || metrics.CoreClockMHz != null
                || metrics.MemoryClockMHz != null;
        }

        // NvAPIWrapper's exception constructors are internal - the driver layer is their
        // only normal caller - so the real types are built by reflection rather than
        // stubbed, keeping the assertions on the exact instances NVAPI would throw.
        private static Exception CreateApiException()
        {
            return Instantiate(typeof(NVIDIAApiException), Status.NotSupported);
        }

        private static Exception CreateNotSupportedException()
        {
            return Instantiate(typeof(NVIDIANotSupportedException), "NVAPI_NOT_SUPPORTED");
        }

        private static Exception Instantiate(Type exceptionType, params object[] args)
        {
            var instance = Activator.CreateInstance(exceptionType, BindingFlags.Instance | BindingFlags.NonPublic, null, args, null);
            Assert.NotNull(instance);
            return (Exception)instance;
        }
    }
}
