using System.Security.Cryptography;
using MetricsPusher.Services;

namespace MetricsPusher.Tests
{
    /// <summary>
    /// Covers the parts of <see cref="PawnIoInstaller"/> that can be reasoned about without a
    /// machine: the version parse and the branch table. Everything the installer actually
    /// touches - the registry, a message box, a child process - is injected here, because on
    /// the dev box PawnIO is already installed and the absent/prompt/install branches can
    /// never be reached live.
    /// </summary>
    public class PawnIoInstallerTests
    {
        private static readonly Version InstalledVersion = new Version(2, 2, 0, 0);

        [Fact]
        public void Decide_ShouldReportAlreadyInstalled_AndNeverPrompt_WhenTheDriverIsPresent()
        {
            // Arrange
            var recorder = new Recorder();

            // Act
            var outcome = PawnIoInstaller.Decide(
                () => InstalledVersion,
                recorder.MarkerIsSet,
                recorder.Prompt,
                recorder.RunInstaller,
                recorder.WriteMarker);

            // Assert - a present driver must cost nothing but the probe
            Assert.Equal(PawnIoInstallOutcome.AlreadyInstalled, outcome);
            Assert.Equal(0, recorder.MarkerReads);
            Assert.Equal(0, recorder.Prompts);
            Assert.Equal(0, recorder.InstallerRuns);
            Assert.Equal(0, recorder.MarkerWrites);
        }

        [Fact]
        public void Decide_ShouldReportDeclinedPreviously_AndNeverPrompt_WhenTheMarkerIsSet()
        {
            // Arrange
            var recorder = new Recorder { MarkerSet = true };

            // Act
            var outcome = PawnIoInstaller.Decide(
                () => null,
                recorder.MarkerIsSet,
                recorder.Prompt,
                recorder.RunInstaller,
                recorder.WriteMarker);

            // Assert - "never ask again" has to mean never, on every later launch
            Assert.Equal(PawnIoInstallOutcome.DeclinedPreviously, outcome);
            Assert.Equal(0, recorder.Prompts);
            Assert.Equal(0, recorder.InstallerRuns);
            Assert.Equal(0, recorder.MarkerWrites);
        }

        [Fact]
        public void Decide_ShouldWriteTheMarker_AndNotRunTheInstaller_WhenTheUserDeclines()
        {
            // Arrange
            var recorder = new Recorder { UserAccepts = false };

            // Act
            var outcome = PawnIoInstaller.Decide(
                () => null,
                recorder.MarkerIsSet,
                recorder.Prompt,
                recorder.RunInstaller,
                recorder.WriteMarker);

            // Assert
            Assert.Equal(PawnIoInstallOutcome.DeclinedNow, outcome);
            Assert.Equal(1, recorder.Prompts);
            Assert.Equal(0, recorder.InstallerRuns);
            Assert.Equal(1, recorder.MarkerWrites);
        }

        [Fact]
        public void Decide_ShouldReportInstalled_AndNotWriteTheMarker_WhenSetupSucceedsAndTheDriverAppears()
        {
            // Arrange - the probe answers "absent" first and "present" on the re-probe
            var recorder = new Recorder { UserAccepts = true, ExitCode = 0 };
            int probes = 0;

            // Act
            var outcome = PawnIoInstaller.Decide(
                () => probes++ == 0 ? null : InstalledVersion,
                recorder.MarkerIsSet,
                recorder.Prompt,
                recorder.RunInstaller,
                recorder.WriteMarker);

            // Assert
            Assert.Equal(PawnIoInstallOutcome.Installed, outcome);
            Assert.Equal(2, probes);
            Assert.Equal(1, recorder.InstallerRuns);
            Assert.Equal(0, recorder.MarkerWrites);
        }

        [Fact]
        public void Decide_ShouldReportFailed_AndWriteTheMarker_WhenSetupSucceedsButTheDriverIsStillAbsent()
        {
            // Arrange - -silent means no UI even on error, so a success code with nothing in
            // the registry is a real possibility and must not re-prompt on every launch
            var recorder = new Recorder { UserAccepts = true, ExitCode = 0 };

            // Act
            var outcome = PawnIoInstaller.Decide(
                () => null,
                recorder.MarkerIsSet,
                recorder.Prompt,
                recorder.RunInstaller,
                recorder.WriteMarker);

            // Assert
            Assert.Equal(PawnIoInstallOutcome.Failed, outcome);
            Assert.Equal(1, recorder.MarkerWrites);
        }

        [Fact]
        public void Decide_ShouldReportRebootRequired_AndNotWriteTheMarker_WhenSetupReturns3010()
        {
            // Arrange
            var recorder = new Recorder { UserAccepts = true, ExitCode = 3010 };

            // Act - the probe stays "absent": the driver is not usable until the restart
            var outcome = PawnIoInstaller.Decide(
                () => null,
                recorder.MarkerIsSet,
                recorder.Prompt,
                recorder.RunInstaller,
                recorder.WriteMarker);

            // Assert - no marker: this install worked, so the next launch must not treat it
            // as a refusal
            Assert.Equal(PawnIoInstallOutcome.RebootRequired, outcome);
            Assert.Equal(0, recorder.MarkerWrites);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(1603)]      // classic installer failure
        [InlineData(-1073741819)] // the setup crashed
        [InlineData(null)]      // could not be started, failed verification, or timed out
        public void Decide_ShouldReportFailed_AndWriteTheMarker_ForAnyOtherExitCode(int? exitCode)
        {
            // Arrange
            var recorder = new Recorder { UserAccepts = true, ExitCode = exitCode };

            // Act
            var outcome = PawnIoInstaller.Decide(
                () => null,
                recorder.MarkerIsSet,
                recorder.Prompt,
                recorder.RunInstaller,
                recorder.WriteMarker);

            // Assert - a failing installer must be tried once, not once per launch
            Assert.Equal(PawnIoInstallOutcome.Failed, outcome);
            Assert.Equal(1, recorder.MarkerWrites);
        }

        [Theory]
        [InlineData("2.2.0.0", null)]   // what a real 2.2.0 install writes - four parts
        [InlineData("2.2.0", null)]
        [InlineData("2.2", null)]
        [InlineData(null, "2.2.0.0")]   // only the WOW6432Node view answers
        [InlineData("", "2.2.0.0")]
        [InlineData("garbage", "2.2.0.0")]
        public void SelectInstalledVersion_ShouldReportPresent_WhenEitherViewParses(string? nativeView, string? wowView)
        {
            // Act
            var result = PawnIoInstaller.SelectInstalledVersion(nativeView, wowView);

            // Assert
            Assert.NotNull(result);
        }

        [Theory]
        [InlineData(null, null)]        // key absent in both views
        [InlineData("", "")]
        [InlineData("   ", null)]
        [InlineData("garbage", null)]
        [InlineData("2", null)]         // Version needs at least major.minor
        public void SelectInstalledVersion_ShouldReportAbsent_WhenNeitherViewParses(string? nativeView, string? wowView)
        {
            // Act
            var result = PawnIoInstaller.SelectInstalledVersion(nativeView, wowView);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void SelectInstalledVersion_ShouldPreferTheNativeView_WhenBothParse()
        {
            // Act
            var result = PawnIoInstaller.SelectInstalledVersion("2.2.0.0", "1.0.0.0");

            // Assert
            Assert.Equal(InstalledVersion, result);
        }

        [Fact]
        public void EmbeddedSetup_ShouldMatchThePinnedHash_SoVerificationCannotSilentlyStartFailing()
        {
            // Arrange - the constant the runtime verifier compares against is only useful if
            // it describes the bytes actually embedded. Refreshing the bundled installer
            // without re-recording the hash breaks the install path on a user's machine and
            // nowhere else, so pin the two together here instead.
            using Stream? resource = typeof(PawnIoInstaller).Assembly
                .GetManifestResourceStream(PawnIoInstaller.SetupResourceName);

            // Assert
            Assert.NotNull(resource);
            Assert.Equal(
                PawnIoInstaller.ExpectedSetupSha256,
                Convert.ToHexString(SHA256.HashData(resource)),
                ignoreCase: true);
        }

        /// <summary>
        /// Stands in for everything <see cref="PawnIoInstaller.Decide"/> is not allowed to
        /// touch in a test, and counts the calls so "no prompt" can be asserted as an absence
        /// rather than inferred from the outcome.
        /// </summary>
        private sealed class Recorder
        {
            public bool MarkerSet { get; init; }

            public bool UserAccepts { get; init; }

            public int? ExitCode { get; init; }

            public int MarkerReads { get; private set; }

            public int Prompts { get; private set; }

            public int InstallerRuns { get; private set; }

            public int MarkerWrites { get; private set; }

            public bool MarkerIsSet()
            {
                MarkerReads++;
                return MarkerSet;
            }

            public bool Prompt()
            {
                Prompts++;
                return UserAccepts;
            }

            public int? RunInstaller()
            {
                InstallerRuns++;
                return ExitCode;
            }

            public void WriteMarker()
            {
                MarkerWrites++;
            }
        }
    }
}
