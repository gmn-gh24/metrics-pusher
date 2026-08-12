using MetricsPusher.Services;

namespace MetricsPusher.Tests
{
    /// <summary>
    /// Every case here runs with no PawnIO driver, no elevation and on any CPU vendor,
    /// because every case here is about the <em>state machine</em> rather than about a
    /// sensor: which module is tried first, what happens when both are rejected, what
    /// latches, what self-heals, and how often a broken sensor is allowed to speak.
    /// <para>
    /// That is not a convenience. On any one machine most of these branches are
    /// unreachable - an Intel box with PawnIO installed takes the first branch of the probe
    /// every time and never sees the fallback, the latch or the recovery path - so the only
    /// way they get exercised at all is with the provider factory injected. The decode
    /// arithmetic those providers perform is covered separately in
    /// <c>CpuTemperatureProvidersTests</c> and <c>CpuPackagePowerProviderTests</c>.
    /// </para>
    /// </summary>
    public class CpuTemperatureServiceTests
    {
        private const string IntelCpuName = "Intel Core Ultra 7 155H";
        private const string AmdCpuName = "AMD Ryzen 9 5950X 16-Core Processor";

        [Theory]
        [InlineData(IntelCpuName)]
        [InlineData("Intel Core i7-8700K")]
        [InlineData("Genuine Intel 0000")]
        public void ProbeOrder_ShouldTryTheIntelModuleFirst_ForAnIntelName(string cpuName)
        {
            // Act
            CpuTemperatureSource[] order = CpuTemperatureService.ProbeOrder(cpuName);

            // Assert - the name only picks which module to try first; the module's own
            // main() is what actually decides, so this is a hint and never a gate
            Assert.Equal(CpuTemperatureSource.IntelPackageMsr, order[0]);
        }

        [Theory]
        [InlineData(AmdCpuName)]
        [InlineData("AMD Ryzen Threadripper 2990WX")]
        [InlineData("AMD EPYC 7763")]
        [InlineData("Ryzen 7 7800X3D")]
        public void ProbeOrder_ShouldTryTheAmdModuleFirst_ForAnAmdName(string cpuName)
        {
            // Act
            CpuTemperatureSource[] order = CpuTemperatureService.ProbeOrder(cpuName);

            // Assert
            Assert.Equal(CpuTemperatureSource.AmdTctlSmn, order[0]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Virtual CPU 00000000")]
        public void ProbeOrder_ShouldStillTryBothModules_WhenTheNameSaysNothing(string? cpuName)
        {
            // Act
            CpuTemperatureSource[] order = CpuTemperatureService.ProbeOrder(cpuName);

            // Assert - an unrecognised name costs one rejected load, not the whole feature.
            // A VM or a renamed part must not silently skip straight to the fallback
            Assert.Contains(CpuTemperatureSource.IntelPackageMsr, order);
            Assert.Contains(CpuTemperatureSource.AmdTctlSmn, order);
        }

        [Theory]
        [InlineData(IntelCpuName)]
        [InlineData(AmdCpuName)]
        [InlineData(null)]
        public void ProbeOrder_ShouldEndWithTheThermalZone_WhateverTheName(string? cpuName)
        {
            // Act
            CpuTemperatureSource[] order = CpuTemperatureService.ProbeOrder(cpuName);

            // Assert - the ACPI zone is a board sensor, not the die: it is what you settle
            // for, so it must never be reached before either module has been asked
            Assert.Equal(3, order.Length);
            Assert.Equal(CpuTemperatureSource.AcpiThermalZone, order[^1]);
        }

        [Fact]
        public void SelectProvider_ShouldStopAtTheFirstSourceThatAnswers()
        {
            // Arrange
            var factory = new RecordingFactory { Answers = { [CpuTemperatureSource.IntelPackageMsr] = true } };

            // Act
            ICpuTemperatureProvider? provider = CpuTemperatureService.SelectProvider(IntelCpuName, factory.TryCreate);

            // Assert - no wasted module load and, more importantly, no second CreateFile
            Assert.NotNull(provider);
            Assert.Equal(CpuTemperatureSource.IntelPackageMsr, provider.Source);
            Assert.Equal(new[] { CpuTemperatureSource.IntelPackageMsr }, factory.Attempts);
        }

        [Fact]
        public void SelectProvider_ShouldFallBackToTheThermalZone_WhenBothModulesAreRejected()
        {
            // Arrange - the normal outcome on a machine without PawnIO, and on an AMD part
            // outside families 0x17-0x1A where both modules' main() says NOT_SUPPORTED
            var factory = new RecordingFactory { Answers = { [CpuTemperatureSource.AcpiThermalZone] = true } };

            // Act
            ICpuTemperatureProvider? provider = CpuTemperatureService.SelectProvider(AmdCpuName, factory.TryCreate);

            // Assert - both modules asked, in AMD-first order, before settling
            Assert.NotNull(provider);
            Assert.Equal(CpuTemperatureSource.AcpiThermalZone, provider.Source);
            Assert.Equal(
                new[] { CpuTemperatureSource.AmdTctlSmn, CpuTemperatureSource.IntelPackageMsr, CpuTemperatureSource.AcpiThermalZone },
                factory.Attempts);
        }

        [Fact]
        public void SelectProvider_ShouldReturnNull_WhenNothingAnswers()
        {
            // Arrange - a VM: no PawnIO device and no \_TZ object either
            var factory = new RecordingFactory();

            // Act
            ICpuTemperatureProvider? provider = CpuTemperatureService.SelectProvider(IntelCpuName, factory.TryCreate);

            // Assert
            Assert.Null(provider);
            Assert.Equal(3, factory.Attempts.Count);
        }

        [Fact]
        public void Initialize_ShouldSelectASource_AndProbeOnlyOnce()
        {
            // Arrange
            var factory = new RecordingFactory { Answers = { [CpuTemperatureSource.AcpiThermalZone] = true } };
            using var service = new CpuTemperatureService(IntelCpuName, factory.TryCreate);

            // Act
            bool initialized = service.Initialize();
            bool again = service.Initialize();
            float? reading = service.ReadTemperature();

            // Assert - three attempts belong to the single probe; a second Initialize and a
            // read must both be served from the latched result
            Assert.True(initialized);
            Assert.True(again);
            Assert.Equal(CpuTemperatureSource.AcpiThermalZone, service.Source);
            Assert.Equal(FakeProvider.DefaultReading, reading);
            Assert.Equal(3, factory.Attempts.Count);
        }

        [Fact]
        public void Source_ShouldBeNone_BeforeTheProbe()
        {
            // Arrange
            var factory = new RecordingFactory { Answers = { [CpuTemperatureSource.IntelPackageMsr] = true } };
            using var service = new CpuTemperatureService(IntelCpuName, factory.TryCreate);

            // Assert - a default source must never read as a real sensor, and constructing
            // the service must not touch the machine
            Assert.Equal(CpuTemperatureSource.None, service.Source);
            Assert.Equal(CpuPowerSource.None, service.PowerSource);
            Assert.Null(service.PackagePowerLimitWatts);
            Assert.Empty(factory.Attempts);
        }

        [Fact]
        public void ReadTemperature_ShouldLatchFailed_AndNeverReProbe_WhenNoSourceAnswers()
        {
            // Arrange
            var factory = new RecordingFactory();
            using var service = new CpuTemperatureService(IntelCpuName, factory.TryCreate);

            // Act
            float? first = service.ReadTemperature();
            float? second = service.ReadTemperature();
            float? third = service.ReadTemperature();

            // Assert - a structural failure must cost one probe, then become a field read
            // forever; re-probing at 1 Hz would mean a CreateFile and two module loads a
            // second on every machine without the driver
            Assert.Null(first);
            Assert.Null(second);
            Assert.Null(third);
            Assert.Equal(CpuTemperatureSource.None, service.Source);
            Assert.Equal(3, factory.Attempts.Count);
        }

        [Fact]
        public void ReadTemperature_ShouldSelfHeal_AfterAFailedRead()
        {
            // Arrange - one bad tick, then good ones: an invalid-reading bit or a busy PCI
            // mutex, neither of which says anything about the next second
            var provider = new FakeProvider(CpuTemperatureSource.IntelPackageMsr, new float?[] { null, 61.5f });
            var factory = new RecordingFactory { Providers = { [CpuTemperatureSource.IntelPackageMsr] = provider } };
            using var service = new CpuTemperatureService(IntelCpuName, factory.TryCreate);

            // Act
            float? failed = service.ReadTemperature();
            float? recovered = service.ReadTemperature();

            // Assert - the provider is still there and still being polled: a transient
            // failure must not latch the way a structural one does
            Assert.Null(failed);
            Assert.Equal(61.5f, recovered);
            Assert.Equal(2, provider.Reads);
            Assert.Equal(CpuTemperatureSource.IntelPackageMsr, service.Source);
            Assert.Single(factory.Attempts);
        }

        [Fact]
        public void ReadTemperature_ShouldLatchFailed_WhenAProviderThrows()
        {
            // Arrange - the providers promise never to throw, so one that does is broken
            // rather than unlucky
            var provider = new FakeProvider(CpuTemperatureSource.AcpiThermalZone, new float?[] { 40f }) { ThrowOnRead = true };
            var factory = new RecordingFactory { Providers = { [CpuTemperatureSource.AcpiThermalZone] = provider } };
            using var service = new CpuTemperatureService(IntelCpuName, factory.TryCreate);

            // Act
            float? first = service.ReadTemperature();
            float? second = service.ReadTemperature();

            // Assert - latched, and the provider let go rather than polled again
            Assert.Null(first);
            Assert.Null(second);
            Assert.Equal(1, provider.Reads);
            Assert.Equal(1, provider.Disposals);
        }

        [Theory]
        // hadValue, alreadyLogged -> new state. The whole contract of a failure streak.
        [InlineData(false, false, true)]  // first failure: one line, streak opens
        [InlineData(false, true, true)]   // still failing: silent, streak continues
        [InlineData(true, true, false)]   // recovered: one line, streak closes
        [InlineData(true, false, false)]  // healthy: silent, and stays silent
        public void NoteReadOutcome_ShouldLogOnlyOnTheEdges(bool hadValue, bool alreadyLogged, bool expected)
        {
            // Act
            bool state = CpuTemperatureService.NoteReadOutcome(hadValue, alreadyLogged, "a sensor");

            // Assert - LoggingService collapses duplicate lines, but per CLAUDE.md that
            // safety net is not a substitute for edge-triggering at the call site: a sensor
            // polled at 1 Hz that fails forever must produce exactly one line, not one that
            // is merely deduplicated
            Assert.Equal(expected, state);
        }

        [Fact]
        public void NoteReadOutcome_ShouldOpenAndCloseOneStreakPerOutage()
        {
            // Arrange - a plausible sequence: fine, broken for three ticks, fine again
            bool[] readings = new bool[] { true, false, false, false, true, true };
            bool state = false;
            int transitions = 0;

            // Act
            foreach (bool hadValue in readings)
            {
                bool next = CpuTemperatureService.NoteReadOutcome(hadValue, state, "a sensor");
                if (next != state)
                    transitions++;

                state = next;
            }

            // Assert - exactly two edges, so exactly two lines, for a three-tick outage
            Assert.Equal(2, transitions);
            Assert.False(state);
        }

        [Fact]
        public void Dispose_ShouldReleaseTheProvider_AndStopLaterReads()
        {
            // Arrange
            var provider = new FakeProvider(CpuTemperatureSource.IntelPackageMsr, new float?[] { 55f });
            var factory = new RecordingFactory { Providers = { [CpuTemperatureSource.IntelPackageMsr] = provider } };
            var service = new CpuTemperatureService(IntelCpuName, factory.TryCreate);
            _ = service.Initialize();

            // Act
            service.Dispose();
            service.Dispose();
            float? afterDispose = service.ReadTemperature();

            // Assert - disposal latches the same way a structural failure does, so nothing
            // re-probes and nothing re-opens a device on a service the app has let go
            Assert.Equal(1, provider.Disposals);
            Assert.Null(afterDispose);
            Assert.Single(factory.Attempts);
        }

        [Fact]
        public void Prime_ShouldProbe_SoTheFirstTickAlreadyHasASource()
        {
            // Arrange - Prime is called beside SystemMetricsService.PrimeCpuCounter, before
            // the loop; a caller that only ever primes must still end up initialized
            var factory = new RecordingFactory { Answers = { [CpuTemperatureSource.AcpiThermalZone] = true } };
            using var service = new CpuTemperatureService(null, factory.TryCreate);

            // Act
            service.Prime();

            // Assert
            Assert.Equal(CpuTemperatureSource.AcpiThermalZone, service.Source);
        }

        [Theory]
        [InlineData(CpuTemperatureService.IntelModuleResourceName)]
        [InlineData(CpuTemperatureService.AmdModuleResourceName)]
        public void ModuleResourceNames_ShouldResolveToRealEmbeddedResources(string resourceName)
        {
            // Arrange - the names are path-derived and the path mixes cases: the directory
            // is "PawnIo" with a lowercase o while the modules keep "MSR" and "AMDFamily17"
            // as they are. A typo is invisible at runtime, because a module that cannot be
            // read looks exactly like a machine that has no PawnIO
            using Stream? module = typeof(CpuTemperatureService).Assembly.GetManifestResourceStream(resourceName);

            // Assert
            Assert.NotNull(module);
            Assert.True(module.Length > 0);
        }

        /// <summary>
        /// A provider that answers from a script instead of from hardware, so a sequence of
        /// ticks - fail, then recover - can be written down rather than waited for.
        /// </summary>
        private sealed class FakeProvider : ICpuTemperatureProvider
        {
            /// <summary>The value an unscripted provider returns, so tests can assert on it.</summary>
            public const float DefaultReading = 42f;

            private readonly float?[] _readings;

            public FakeProvider(CpuTemperatureSource source, float?[] readings)
            {
                Source = source;
                _readings = readings;
            }

            public CpuTemperatureSource Source { get; }

            public bool ThrowOnRead { get; init; }

            public int Reads { get; private set; }

            public int Disposals { get; private set; }

            public bool TryRead(out float celsius)
            {
                celsius = 0f;
                Reads++;

                if (ThrowOnRead)
                    throw new InvalidOperationException("scripted provider failure");

                // The last scripted value repeats, so a test only has to write the ticks it
                // cares about.
                float? reading = _readings[Math.Min(Reads - 1, _readings.Length - 1)];
                if (reading == null)
                    return false;

                celsius = reading.Value;
                return true;
            }

            public void Dispose()
            {
                Disposals++;
            }
        }

        /// <summary>
        /// Stands in for everything the probe would otherwise touch - a kernel device, a
        /// signed module, a PDH query - and records which sources were attempted, so
        /// "the Intel module was never asked" can be asserted as an absence rather than
        /// inferred from the outcome.
        /// </summary>
        private sealed class RecordingFactory
        {
            public List<CpuTemperatureSource> Attempts { get; } = new List<CpuTemperatureSource>();

            /// <summary>Sources that answer with a default provider.</summary>
            public Dictionary<CpuTemperatureSource, bool> Answers { get; } = new Dictionary<CpuTemperatureSource, bool>();

            /// <summary>Sources that answer with a specific scripted provider.</summary>
            public Dictionary<CpuTemperatureSource, FakeProvider> Providers { get; } = new Dictionary<CpuTemperatureSource, FakeProvider>();

            public ICpuTemperatureProvider? TryCreate(CpuTemperatureSource source)
            {
                Attempts.Add(source);

                if (Providers.TryGetValue(source, out FakeProvider? scripted))
                    return scripted;

                return Answers.TryGetValue(source, out bool answers) && answers
                    ? new FakeProvider(source, new float?[] { FakeProvider.DefaultReading })
                    : null;
            }
        }
    }
}
