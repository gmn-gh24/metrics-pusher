using MetricsPusher;
using MetricsPusher.Services;
using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;

namespace MetricsPusher.Tests
{
    [Collection(ProcessGlobalCollection.Name)]
    public class NvmlServiceTests
    {
        // Plausibility bounds for the integration tests. Deliberately wide: they exist to
        // catch a marshaling error (a byte-swapped struct field, bytes reported as MB,
        // watts reported as milliwatts), not to characterize a particular board.
        private const float MinTemperatureC = 10f;
        private const float MaxTemperatureC = 110f;
        private const int MinCoreClockMHz = 100;
        private const int MaxCoreClockMHz = 5000;
        private const int MinMemoryClockMHz = 100;
        private const int MaxMemoryClockMHz = 20000;
        private const uint MinPowerMilliwatts = 5000;
        private const uint MaxPowerMilliwatts = 600000;
        private const uint MinPowerLimitMilliwatts = 100000;
        private const uint MaxPowerLimitMilliwatts = 1000000;
        private const long MinVramTotalMB = 8000;

        // The two stacks read the same sensor microseconds apart, so only sampling noise
        // should separate them. The VRAM tolerance is headroom for hardware other than
        // this one rather than an observed gap: on the reference board both stacks report
        // 24564 MB exactly (push_metrics.md section 4), and the tolerance exists so a
        // board where the two APIs subtract different reserved regions does not fail a
        // test that is about unit agreement, not about the last megabyte.
        private const float MaxCrossTemperatureDeltaC = 3f;
        private const double MaxCrossVramTotalRatio = 0.05;

        // xUnit constructs the class per test, so this resets NvmlService's shared static
        // state before every test - exactly like GpuMonitorServiceTests. Without it,
        // whichever test initialized NVML first would leave it live and break every
        // "_BeforeInitialization" test that happened to run after it.
        public NvmlServiceTests()
        {
            NvmlService.Shutdown();
        }

        [Fact]
        public void IsAvailable_ShouldBeFalse_BeforeInitialization()
        {
            // Assert - the latch starts closed and reading it must not touch nvml.dll
            Assert.False(NvmlService.IsAvailable);
        }

        [Fact]
        public void AllGetters_ShouldReturnNull_BeforeInitialization()
        {
            // Assert - no getter may call into NVML (or throw) before Initialize ran
            AssertEveryGetterIsNull();
        }

        [Fact]
        public void AllGetters_ShouldReturnNull_AfterShutdown()
        {
            // Arrange
            _ = NvmlService.Initialize();

            // Act
            NvmlService.Shutdown();

            // Assert - Shutdown resets every piece of state, including the device handle;
            // reading through a handle whose library has been unloaded is undefined
            Assert.False(NvmlService.IsAvailable);
            AssertEveryGetterIsNull();
        }

        [Fact]
        public void Initialize_ShouldReturnTheSameResult_WhenCalledTwice()
        {
            // Act - the second call must hit the latch, not nvmlInit_v2 again
            bool first = NvmlService.Initialize();
            bool second = NvmlService.Initialize();

            // Assert
            Assert.Equal(first, second);
            Assert.Equal(first, NvmlService.IsAvailable);
        }

        [Fact]
        public void Initialize_ShouldWorkAgain_AfterShutdown()
        {
            // Arrange
            bool before = NvmlService.Initialize();

            // Act - Shutdown must clear the "already attempted" latch too, otherwise a
            // restart would be stuck reporting the pre-shutdown verdict
            NvmlService.Shutdown();
            bool after = NvmlService.Initialize();

            // Assert
            Assert.Equal(before, after);
            if (!after)
                return;

            Assert.True(NvmlService.IsAvailable);
            Assert.NotNull(NvmlService.GetName());
        }

        [Fact]
        public void Shutdown_ShouldNotThrow_WhenCalledWithoutInitialize()
        {
            // Act & Assert - nvmlShutdown must not be called against a library that was
            // never initialized
            NvmlService.Shutdown();
        }

        [Fact]
        public void Shutdown_ShouldNotThrow_WhenCalledTwice()
        {
            // Act & Assert
            _ = NvmlService.Initialize();
            NvmlService.Shutdown();
            NvmlService.Shutdown();
        }

        [Theory]
        [InlineData(3, true)]    // NVML_ERROR_NOT_SUPPORTED
        [InlineData(0, false)]   // NVML_SUCCESS (never routed here, pinned for completeness)
        [InlineData(4, false)]   // NVML_ERROR_NO_PERMISSION
        [InlineData(9, false)]   // NVML_ERROR_DRIVER_NOT_LOADED
        [InlineData(15, false)]  // NVML_ERROR_GPU_IS_LOST
        [InlineData(999, false)] // NVML_ERROR_UNKNOWN
        public void IsUnsupportedStatus_ShouldMatchOnlyNotSupported(int status, bool expected)
        {
            // Assert - NOT_SUPPORTED is a permanent property of the board (a laptop GPU
            // whose fan the driver does not expose answers it on every tick), so it must
            // be a silent null: if it consumed the session's one edge-triggered log line
            // at startup, a later genuinely diagnostic failure - GPU_IS_LOST during the
            // handle loss GpuMonitorService's two-strike rule reacts to - would go
            // unlogged.
            Assert.Equal(expected, NvmlService.IsUnsupportedStatus(status));
        }

        [Fact]
        public void ShouldLogFailure_ShouldReportOnlyTheFirstFailureOfAStreak()
        {
            // Arrange - a fresh streak flag
            bool logged = false;

            // Act & Assert - the first failure is the diagnostic; the repeats are noise.
            // Every failure this layer reports is one its caller retries forever (a sensor
            // at 1 Hz, an initialization every 5 s), so "log each one" would be an
            // unbounded file append on a machine that stays broken.
            Assert.True(NvmlService.ShouldLogFailure(ref logged));
            Assert.False(NvmlService.ShouldLogFailure(ref logged));
            Assert.False(NvmlService.ShouldLogFailure(ref logged));
            Assert.True(logged);
        }

        [Fact]
        public void ShouldLogFailure_ShouldReportAgain_AfterTheStreakEnds()
        {
            // Arrange - mid-streak
            bool logged = true;
            Assert.False(NvmlService.ShouldLogFailure(ref logged));

            // Act - what a successful Initialize (or, for reads, a Shutdown) does to the
            // flag: the streak is over, so the NEXT failure is genuinely new information
            logged = false;

            // Assert
            Assert.True(NvmlService.ShouldLogFailure(ref logged));
        }

        [Fact]
        public void Initialize_ShouldSurviveRepeatedFailedProbes_WithoutThrowing()
        {
            // The both-stacks-dead loop GpuMonitorService.AcquireBackend drives: it calls
            // Shutdown after every failed Initialize (to clear the "already attempted"
            // latch so a recovered driver can be found), which means Initialize is
            // re-entered every ~5 s for as long as the machine stays broken. On a machine
            // WITH NVML this just re-initializes; the point pinned here is that the cycle
            // itself is safe and its verdict stable - the log-once-per-streak guard that
            // keeps it from appending a Debug line every cycle is ShouldLogFailure above,
            // whose flag is deliberately NOT reset by Shutdown.
            bool first = NvmlService.Initialize();

            for (int cycle = 0; cycle < 3; cycle++)
            {
                NvmlService.Shutdown();
                Assert.Equal(first, NvmlService.Initialize());
            }
        }

        [Fact]
        public void GetName_ShouldReturnANonEmptyName_WhenNvmlAvailable()
        {
            if (!NvmlService.Initialize())
                return;

            // Act
            string? name = NvmlService.GetName();

            // Assert - a truncated or mis-marshaled buffer shows up here first
            Assert.NotNull(name);
            Assert.NotEmpty(name);
            Assert.DoesNotContain('\0', name);
        }

        [Fact]
        public void GetTemperature_ShouldReturnPlausibleCelsius_WhenNvmlAvailable()
        {
            if (!NvmlService.Initialize())
                return;

            // Act
            float? temperature = NvmlService.GetTemperature();

            // Assert
            Assert.NotNull(temperature);
            Assert.InRange(temperature.Value, MinTemperatureC, MaxTemperatureC);
        }

        [Fact]
        public void GetUtilizationPercent_ShouldReturnAPercentage_WhenNvmlAvailable()
        {
            if (!NvmlService.Initialize())
                return;

            // Act
            int? utilization = NvmlService.GetUtilizationPercent();

            // Assert - the gpu field, not the memory field: a swapped struct order would
            // still land in 0-100, so the cross-check for this one is the field order
            // being pinned by the struct definition itself
            Assert.NotNull(utilization);
            Assert.InRange(utilization.Value, 0, 100);
        }

        [Fact]
        public void GetVramMB_ShouldReturnUsedWithinTotal_WhenNvmlAvailable()
        {
            if (!NvmlService.Initialize())
                return;

            // Act
            var vram = NvmlService.GetVramMB();

            // Assert - one native read carries both figures, so used can never exceed
            // total; the MB conversion is what the magnitude check guards
            Assert.NotNull(vram);
            Assert.True(vram.Value.TotalMB > MinVramTotalMB, $"VRAM total {vram.Value.TotalMB} MB is implausibly small - bytes probably reached the caller unconverted or the struct fields are misaligned");
            Assert.InRange(vram.Value.UsedMB, 0L, vram.Value.TotalMB);
        }

        [Fact]
        public void GetFanSpeedPercent_ShouldReturnAPercentage_WhenNvmlAvailable()
        {
            if (!NvmlService.Initialize())
                return;

            // Act
            int? fan = NvmlService.GetFanSpeedPercent();

            // Assert - 0 is a value, not an absence: an idle board with zero-RPM fan
            // control legitimately reports 0 %
            Assert.NotNull(fan);
            Assert.InRange(fan.Value, 0, 100);
        }

        [Fact]
        public void GetCoreClockMHz_ShouldReturnPlausibleMHz_WhenNvmlAvailable()
        {
            if (!NvmlService.Initialize())
                return;

            // Act
            int? clock = NvmlService.GetCoreClockMHz();

            // Assert
            Assert.NotNull(clock);
            Assert.InRange(clock.Value, MinCoreClockMHz, MaxCoreClockMHz);
        }

        [Fact]
        public void GetMemoryClockMHz_ShouldReturnPlausibleMHz_WhenNvmlAvailable()
        {
            if (!NvmlService.Initialize())
                return;

            // Act
            int? clock = NvmlService.GetMemoryClockMHz();

            // Assert - a different clock domain than the core, so a clock-type constant
            // mix-up shows up as two identical readings rather than an out-of-range one
            Assert.NotNull(clock);
            Assert.InRange(clock.Value, MinMemoryClockMHz, MaxMemoryClockMHz);
        }

        [Fact]
        public void GetPowerMilliwatts_ShouldReturnPlausibleDraw_WhenNvmlAvailable()
        {
            if (!NvmlService.Initialize())
                return;

            // Act
            uint? power = NvmlService.GetPowerMilliwatts();

            // Assert - raw milliwatts: watts would fail the lower bound, microwatts the
            // upper one
            Assert.NotNull(power);
            Assert.InRange(power.Value, MinPowerMilliwatts, MaxPowerMilliwatts);
        }

        [Fact]
        public void GetEnforcedPowerLimitMilliwatts_ShouldReturnPlausibleLimit_WhenNvmlAvailable()
        {
            if (!NvmlService.Initialize())
                return;

            // Act
            uint? limit = NvmlService.GetEnforcedPowerLimitMilliwatts();

            // Assert
            Assert.NotNull(limit);
            Assert.InRange(limit.Value, MinPowerLimitMilliwatts, MaxPowerLimitMilliwatts);
        }

        [Fact]
        public void GetPowerMilliwatts_ShouldNotExceedTheEnforcedLimit_ByMoreThanTransientBoost()
        {
            if (!NvmlService.Initialize())
                return;

            // Act - read close together so the pair describes one operating point
            uint? power = NvmlService.GetPowerMilliwatts();
            uint? limit = NvmlService.GetEnforcedPowerLimitMilliwatts();

            // Assert - the two readings must share a unit for Task N2's percent
            // derivation to mean anything. Transient boost above the enforced limit is
            // real, so the bound is generous; a unit mismatch is off by 1000x.
            Assert.NotNull(power);
            Assert.NotNull(limit);
            Assert.True(power.Value < limit.Value * 2, $"Power {power.Value} mW against an enforced limit of {limit.Value} mW - the two readings are not in the same unit");
        }

        [Fact]
        public void Nvml_ShouldDescribeTheSameDevice_AsNvapi()
        {
            // The point of the whole layer: NVML must be able to REPLACE NVAPI, which is
            // only true if both stacks describe the same board in the same units.
            //
            // Each side is therefore read from its own stack directly. Taking the NVAPI
            // side from GpuMonitorService - as this test did until v5.10.0 - stopped
            // working the moment that service latched NVML as its primary backend: it
            // then returned NVML values under an "nvapi" name and the test compared NVML
            // against itself, passing by construction. The bug was invisible precisely
            // because a self-comparison is always green.
            if (!NvmlService.Initialize())
                return;

            bool nvapiInitialized = false;

            try
            {
                string? nvapiName;
                float? nvapiTemperature = null;
                long? nvapiVramTotalMB;

                try
                {
                    NVIDIA.Initialize();
                    nvapiInitialized = true;

                    var gpus = PhysicalGPU.GetPhysicalGPUs();
                    if (gpus == null || gpus.Length == 0)
                        return; // NVAPI declines on this machine: skip the comparison, not the suite

                    // Act - NVAPI first, then NVML immediately after, so the board has as
                    // little time as possible to move between the two stacks
                    var gpu = gpus[0];
                    nvapiName = gpu.FullName;

                    var thermalSensors = gpu.ThermalInformation?.ThermalSensors;
                    if (thermalSensors != null)
                    {
                        foreach (var sensor in thermalSensors)
                        {
                            float celsius = sensor.CurrentTemperature;
                            if (Constants.IsValidTemperature(celsius))
                            {
                                nvapiTemperature = celsius;
                                break;
                            }
                        }
                    }

                    nvapiVramTotalMB = (long)(GPUApi.GetMemoryInfo(gpu.Handle).DedicatedVideoMemoryInkB / 1024);
                }
                catch (Exception ex) when (GpuMonitorService.IsUnsupportedApiException(ex))
                {
                    return; // Same skip: this machine cannot answer through NVAPI at all
                }

                string? nvmlName = NvmlService.GetName();
                float? nvmlTemperature = NvmlService.GetTemperature();
                var nvmlVram = NvmlService.GetVramMB();

                // Assert - NVML must have produced everything; each cross-stack comparison
                // is then made only when NVAPI produced its side too, the same per-field
                // independence the services themselves promise
                Assert.NotNull(nvmlName);
                Assert.NotNull(nvmlTemperature);
                Assert.NotNull(nvmlVram);

                if (nvapiName != null)
                    Assert.Equal(nvapiName, nvmlName);

                if (nvapiTemperature != null)
                {
                    float delta = Math.Abs(nvmlTemperature.Value - nvapiTemperature.Value);
                    Assert.True(delta <= MaxCrossTemperatureDeltaC, $"NVML reports {nvmlTemperature.Value} C, NVAPI {nvapiTemperature.Value} C - {delta} C apart");
                }

                if (nvapiVramTotalMB > 0)
                {
                    double ratio = Math.Abs(nvmlVram.Value.TotalMB - nvapiVramTotalMB.Value) / (double)nvapiVramTotalMB.Value;
                    Assert.True(ratio <= MaxCrossVramTotalRatio, $"NVML reports {nvmlVram.Value.TotalMB} MB total VRAM, NVAPI {nvapiVramTotalMB} MB - {ratio:P1} apart");
                }
            }
            finally
            {
                // Nothing this test loaded may outlive it: both stacks are process-global,
                // and the Gpu collection's next test expects to start from a clean slate
                if (nvapiInitialized)
                {
                    try
                    {
                        NVIDIA.Unload();
                    }
                    catch
                    {
                        // Teardown only - a failed unload must not mask the test's verdict
                    }
                }

                NvmlService.Shutdown();
            }
        }

        private static void AssertEveryGetterIsNull()
        {
            Assert.Null(NvmlService.GetName());
            Assert.Null(NvmlService.GetTemperature());
            Assert.Null(NvmlService.GetUtilizationPercent());
            Assert.Null(NvmlService.GetVramMB());
            Assert.Null(NvmlService.GetFanSpeedPercent());
            Assert.Null(NvmlService.GetCoreClockMHz());
            Assert.Null(NvmlService.GetMemoryClockMHz());
            Assert.Null(NvmlService.GetPowerMilliwatts());
            Assert.Null(NvmlService.GetEnforcedPowerLimitMilliwatts());
        }
    }
}
