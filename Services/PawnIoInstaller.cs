using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace MetricsPusher.Services
{
    /// <summary>
    /// How the first-run PawnIO check ended. Only <see cref="AlreadyInstalled"/> and
    /// <see cref="Installed"/> mean CPU die temperature is available this session; every
    /// other value is a normal, degraded outcome that costs exactly that one reading.
    /// </summary>
    internal enum PawnIoInstallOutcome
    {
        /// <summary>The driver was already present. Nothing was asked and nothing was run.</summary>
        AlreadyInstalled,

        /// <summary>The user declined on an earlier launch and the marker says so.</summary>
        DeclinedPreviously,

        /// <summary>The user declined at the prompt just now; the marker was written.</summary>
        DeclinedNow,

        /// <summary>Setup ran, reported success, and the driver is now visible in the registry.</summary>
        Installed,

        /// <summary>Setup reported ERROR_SUCCESS_REBOOT_REQUIRED - installed, unusable until a restart.</summary>
        RebootRequired,

        /// <summary>Setup could not be run, failed verification, timed out, or exited non-zero.</summary>
        Failed,
    }

    /// <summary>
    /// The first-run consent prompt for PawnIO and, if the user accepts, the bundled
    /// installer run.
    /// <para>
    /// PawnIO is a signed third-party kernel driver. It is the only route to CPU die
    /// temperature on Windows - the value lives in model-specific registers that need ring 0 -
    /// but a ring-0 driver is not something to install behind a user's back, so this asks
    /// once, records a refusal, and never asks again. Everything else the app reports works
    /// without it.
    /// </para>
    /// <para>
    /// The decision itself is <see cref="Decide"/>, which touches no registry, no message box
    /// and no process: on a machine that already has PawnIO the absent/prompt/install branches
    /// are unreachable, so they are only ever exercised by the tests that inject those four
    /// effects.
    /// </para>
    /// </summary>
    internal static class PawnIoInstaller
    {
        /// <summary>
        /// Manifest name of the embedded 2.2.0 installer. Read out of the built assembly, not
        /// guessed: the folder is <c>PawnIo</c> with a lowercase o while this file keeps its
        /// uppercase <c>IO</c>, and both spellings survive verbatim into the manifest.
        /// </summary>
        internal const string SetupResourceName = "MetricsPusher.Resources.PawnIO_setup.exe";

        /// <summary>
        /// SHA-256 of the committed <c>Resources/PawnIO_setup.exe</c>, checked against the
        /// extracted file before it is executed. Writing an embedded exe to disk and running
        /// it elevated is the shape of a dropper, and the one thing that makes it defensible
        /// is proving the bytes about to run are the bytes that shipped. A test pins this
        /// constant to the embedded resource so refreshing the installer without re-recording
        /// the hash fails here rather than on a user's machine.
        /// <para>
        /// A hash and not an Authenticode check, deliberately. Verifying the signature means
        /// P/Invoking <c>wintrust.dll</c>, which is not a KnownDLL and would therefore have to
        /// join <see cref="SystemLibraryResolver"/>'s guarded list; and for THESE bytes it adds
        /// nothing a byte-exact match does not already prove. For the record, the file this
        /// hash describes is signed "E=admin@namazso.eu, CN=namazso.eu, O=namazso, L=Debrecen,
        /// C=HU", countersigned by a Microsoft timestamp authority - re-check that when the
        /// bundled installer is refreshed, since the hash alone cannot tell you who signed it.
        /// </para>
        /// </summary>
        internal const string ExpectedSetupSha256 =
            "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";

        /// <summary>
        /// Presence key. Only <c>DisplayVersion</c> is read, and only for "does it parse" -
        /// a real 2.2.0 install writes the four-part string "2.2.0.0", so any comparison
        /// against a literal "2.2.0" would re-prompt forever on a machine that already has it.
        /// </summary>
        private const string UninstallKeyPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

        private const string DisplayVersionValueName = "DisplayVersion";

        /// <summary>
        /// Per-user "do not ask again" marker. HKCU, not HKLM: consent to a driver install is
        /// the user's answer, not the machine's. Note that under over-the-shoulder elevation
        /// HKCU is the approving administrator's hive, so a machine where a different admin
        /// approves each launch can be asked more than once - correct, if surprising.
        /// </summary>
        private const string DeclineMarkerKeyPath = @"Software\MetricsPusher";

        private const string DeclineMarkerValueName = "PawnIoInstallDeclined";

        /// <summary>Clean install, driver usable immediately.</summary>
        private const int ExitSuccess = 0;

        /// <summary>
        /// ERROR_SUCCESS_REBOOT_REQUIRED - installed, usable after a restart.
        /// <para>
        /// Both codes are taken from the PawnIO 2.2.0 release notes ("in silent mode
        /// ERROR_SUCCESS_REBOOT_REQUIRED is appropriately returned if a restart is needed").
        /// NEITHER HAS BEEN OBSERVED: PawnIO was installed interactively on the only machine
        /// available, so no <c>-install -silent</c> run has ever been watched, and whether a
        /// clean Windows 11 install returns 0 or 3010 is unknown. This branch is written to
        /// the documented contract, not to a measurement. Anything unrecognised falls into
        /// the default arm and degrades safely, so a wrong guess here costs the reboot notice,
        /// not correctness.
        /// </para>
        /// </summary>
        private const int ExitRebootRequired = 3010;

        /// <summary>
        /// How long to wait for the silent install. A driver install plus PnP device creation
        /// is seconds, not minutes; this is a ceiling on how long a wedged installer may hold
        /// up startup, since this runs before the tray icon appears.
        /// </summary>
        private const int SetupTimeoutMs = 120_000;

        private const string PromptCaption = "MetricsPusher - PawnIO kernel driver";

        private const string PromptMessage =
            "MetricsPusher can report your CPU's die temperature, but Windows offers no way to " +
            "read it without a kernel driver.\n\n" +
            "PawnIO is a third-party kernel-mode driver, digitally signed by its author " +
            "(namazso). Installing it puts that driver on this machine and leaves it there " +
            "until you remove it, and it runs with full kernel privileges - that is what makes " +
            "the reading possible. The installer is bundled with MetricsPusher, so nothing is " +
            "downloaded. It can be removed later from Windows Settings > Installed apps, or by " +
            "running PawnIO_setup.exe -uninstall.\n\n" +
            "Declining costs exactly one number: CPU die temperature. CPU load, RAM, disk, NVMe " +
            "drive temperature, GPU and everything else keep working unchanged, and MetricsPusher " +
            "falls back to the less accurate ACPI thermal-zone reading where the firmware " +
            "provides one.\n\n" +
            "You will only be asked once.\n\n" +
            "Install PawnIO now?";

        private const string RebootMessage =
            "PawnIO was installed, but Windows needs to restart before the driver can be " +
            "used.\n\n" +
            "MetricsPusher keeps running and reports everything else as usual; CPU die " +
            "temperature starts working after the next restart.";

        /// <summary>
        /// Extraction target, built here rather than taken from anywhere: a per-user path the
        /// elevated process can always write, never the folder holding the exe, which may be
        /// read-only or on removable media.
        /// <para>
        /// Running an elevated child out of a user-writable folder normally invites a planted
        /// DLL beside it, but not here: this process runs as whoever answered the UAC prompt,
        /// so LocalApplicationData resolves to THAT account's profile. Under over-the-shoulder
        /// elevation that is the administrator's own profile, which the standard user cannot
        /// write to; when it is the same user, they are already an administrator and UAC is
        /// not a security boundary to begin with. The hash check below covers the rest.
        /// </para>
        /// </summary>
        private static readonly string ExtractionFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MetricsPusher");

        private static readonly string ExtractedSetupPath =
            Path.Combine(ExtractionFolder, "PawnIO_setup.exe");

        /// <summary>
        /// Runs the whole first-run check: probe, maybe prompt, maybe install. Never throws -
        /// it is called before <c>Program.Main</c> wires the global exception handlers, and a
        /// missing CPU temperature is never worth losing the tray icon over.
        /// </summary>
        public static void EnsureInstalled()
        {
            try
            {
                ReportOutcome(Decide(
                    ProbeInstalledVersion,
                    IsDeclineMarkerSet,
                    AskUserToInstall,
                    RunBundledSetup,
                    WriteDeclineMarker));
            }
            catch (Exception ex)
            {
                LoggingService.Error("PawnIO install check faulted - continuing without it", ex);
            }
        }

        /// <summary>
        /// The whole branch table, with every environment touch injected so it can be tested
        /// on a machine where PawnIO is already installed and the interesting branches are
        /// therefore unreachable. Same shape as
        /// <see cref="SystemMetricsService.DetectPendingReboot(Func{string, bool})"/>.
        /// </summary>
        /// <param name="probeInstalledVersion">Reads the presence key; null when absent. Called
        /// again after a successful install, because <c>-silent</c> means "no UI, even on
        /// error" and the exit code is the installer's only channel - worth confirming.</param>
        /// <param name="declineMarkerIsSet">True when the user has already said no.</param>
        /// <param name="askUserToInstall">Shows the consent prompt; true when the user accepts.</param>
        /// <param name="runInstaller">Extracts, verifies and runs setup; the process exit code,
        /// or null when it could not be run, failed verification, or timed out.</param>
        /// <param name="writeDeclineMarker">Records "never ask again".</param>
        /// <returns>What happened, for logging and for the reboot notice.</returns>
        internal static PawnIoInstallOutcome Decide(
            Func<Version?> probeInstalledVersion,
            Func<bool> declineMarkerIsSet,
            Func<bool> askUserToInstall,
            Func<int?> runInstaller,
            Action writeDeclineMarker)
        {
            if (probeInstalledVersion() != null)
                return PawnIoInstallOutcome.AlreadyInstalled;

            if (declineMarkerIsSet())
                return PawnIoInstallOutcome.DeclinedPreviously;

            if (!askUserToInstall())
            {
                writeDeclineMarker();
                return PawnIoInstallOutcome.DeclinedNow;
            }

            switch (runInstaller())
            {
                case ExitSuccess:
                    // Trust but verify: a silent installer that exits 0 while the presence key
                    // stays absent has not installed anything, and re-prompting on every
                    // launch is the one behavior this class exists to prevent.
                    if (probeInstalledVersion() != null)
                        return PawnIoInstallOutcome.Installed;

                    writeDeclineMarker();
                    return PawnIoInstallOutcome.Failed;

                case ExitRebootRequired:
                    // Deliberately no marker: the install worked, so treating it as a refusal
                    // would strand the user in the fallback forever after they said yes.
                    return PawnIoInstallOutcome.RebootRequired;

                default:
                    // Includes null (never started / verification failed / timed out). The
                    // marker stops a broken install being retried once per launch.
                    writeDeclineMarker();
                    return PawnIoInstallOutcome.Failed;
            }
        }

        /// <summary>
        /// Picks the installed version out of the <c>DisplayVersion</c> strings read from the
        /// registry views, preferring the native one.
        /// <para>
        /// <see cref="Version.TryParse(string?, out Version?)"/> and not a string comparison:
        /// a real 2.2.0 install writes "2.2.0.0", so equality or <c>StartsWith</c> against a
        /// three-part literal reports "absent" on a machine that already has the driver.
        /// </para>
        /// </summary>
        /// <param name="nativeViewDisplayVersion">DisplayVersion from the 64-bit view, or null.</param>
        /// <param name="wowViewDisplayVersion">DisplayVersion from WOW6432Node, or null.</param>
        /// <returns>The parsed version, or null when neither view holds a usable one.</returns>
        internal static Version? SelectInstalledVersion(
            string? nativeViewDisplayVersion, string? wowViewDisplayVersion)
        {
            if (Version.TryParse(nativeViewDisplayVersion, out Version? native))
                return native;

            return Version.TryParse(wowViewDisplayVersion, out Version? wow) ? wow : null;
        }

        private static void ReportOutcome(PawnIoInstallOutcome outcome)
        {
            switch (outcome)
            {
                case PawnIoInstallOutcome.AlreadyInstalled:
                    LoggingService.Info("PawnIO is installed - CPU die temperature is available");
                    break;

                case PawnIoInstallOutcome.DeclinedPreviously:
                    LoggingService.Info("PawnIO was declined on an earlier launch - not asking again");
                    break;

                case PawnIoInstallOutcome.DeclinedNow:
                    LoggingService.Info("PawnIO install declined - recorded, and not asking again");
                    break;

                case PawnIoInstallOutcome.Installed:
                    LoggingService.Info("PawnIO installed - CPU die temperature is available");
                    break;

                case PawnIoInstallOutcome.RebootRequired:
                    LoggingService.Warn("PawnIO setup returned 3010 (reboot required) - CPU die temperature starts working after a restart");
                    MessageBox.Show(RebootMessage, PromptCaption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;

                case PawnIoInstallOutcome.Failed:
                    LoggingService.Warn("PawnIO could not be installed - continuing without CPU die temperature and not retrying");
                    break;
            }
        }

        /// <summary>
        /// Reads the presence key from both registry views.
        /// <para>
        /// This process is pinned to x64, so <see cref="RegistryView.Default"/> already IS the
        /// 64-bit view - the second lookup that can add anything is
        /// <see cref="RegistryView.Registry32"/> (WOW6432Node), which is what
        /// LibreHardwareMonitor checks too. On the dev box only the 64-bit view answers, so
        /// the WOW6432Node branch has never returned a hit and is covered by tests alone.
        /// </para>
        /// </summary>
        private static Version? ProbeInstalledVersion()
        {
            try
            {
                return SelectInstalledVersion(
                    ReadDisplayVersion(RegistryView.Registry64),
                    ReadDisplayVersion(RegistryView.Registry32));
            }
            catch (Exception ex)
            {
                // An unreadable presence key reads as "absent", which at worst costs one
                // prompt the user can decline - the safe direction for this failure.
                LoggingService.Debug($"PawnIoInstaller: Failed to read the PawnIO presence key: {ex.Message}");
                return null;
            }
        }

        private static string? ReadDisplayVersion(RegistryView view)
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using RegistryKey? key = baseKey.OpenSubKey(UninstallKeyPath);
            return key?.GetValue(DisplayVersionValueName) as string;
        }

        private static bool IsDeclineMarkerSet()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(DeclineMarkerKeyPath);
                return key?.GetValue(DeclineMarkerValueName) is int marker && marker != 0;
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"PawnIoInstaller: Failed to read the decline marker: {ex.Message}");
                return false;
            }
        }

        private static void WriteDeclineMarker()
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(DeclineMarkerKeyPath);
                key.SetValue(DeclineMarkerValueName, 1, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                // Not fatal, but say so plainly: the only consequence is being asked again.
                LoggingService.Warn($"PawnIoInstaller: Failed to record the decline marker, so the prompt will return next launch: {ex.Message}");
            }
        }

        private static bool AskUserToInstall()
        {
            // Default button is No. Consent to a kernel driver should not be something a
            // reflexive Enter can grant.
            return MessageBox.Show(
                PromptMessage,
                PromptCaption,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        /// <summary>
        /// Extracts the bundled installer, proves it is the file that shipped, and runs it
        /// <c>-install -silent</c>.
        /// </summary>
        /// <returns>The exit code, or null when setup never ran or never finished.</returns>
        private static int? RunBundledSetup()
        {
            FileStream? pin = null;
            try
            {
                Directory.CreateDirectory(ExtractionFolder);
                if (!ExtractSetup())
                    return null;

                // Held open, read-only, deny-write, for as long as setup runs: nothing can
                // rewrite or delete the file between the hash check below and the moment the
                // loader maps it. Read access ONLY - a handle holding write access would make
                // the image open fail with a sharing violation, because CreateProcess opens
                // the exe without FILE_SHARE_WRITE.
                pin = new FileStream(ExtractedSetupPath, FileMode.Open, FileAccess.Read, FileShare.Read);

                if (!IsExpectedSetup(pin))
                {
                    LoggingService.Error("PawnIoInstaller: the extracted PawnIO installer does not match the bundled one - refusing to run it");
                    return null;
                }

                return RunSetup();
            }
            catch (Exception ex)
            {
                LoggingService.Error("PawnIoInstaller: Failed to run the bundled PawnIO installer", ex);
                return null;
            }
            finally
            {
                // In a finally so a throw anywhere above cannot leave 3.4 MB behind. Releasing
                // the pin first is what makes the delete possible at all.
                pin?.Dispose();
                TryDeleteExtractedSetup();
            }
        }

        private static bool ExtractSetup()
        {
            using Stream? resource = typeof(PawnIoInstaller).Assembly
                .GetManifestResourceStream(SetupResourceName);

            if (resource == null)
            {
                LoggingService.Error($"PawnIoInstaller: embedded resource {SetupResourceName} is missing from this build");
                return false;
            }

            using var file = new FileStream(
                ExtractedSetupPath, FileMode.Create, FileAccess.Write, FileShare.None);
            resource.CopyTo(file);
            return true;
        }

        private static bool IsExpectedSetup(FileStream setup)
        {
            setup.Position = 0;
            string actual = Convert.ToHexString(SHA256.HashData(setup));
            return string.Equals(actual, ExpectedSetupSha256, StringComparison.OrdinalIgnoreCase);
        }

        private static int? RunSetup()
        {
            // -install -silent only. -unrestricted installs the edition that loads UNSIGNED
            // bytecode, which throws away the module-signing property that makes this driver
            // acceptable to bundle at all; -debuginfo and -uninstall are the other two real
            // flags, neither of any use here.
            var startInfo = new ProcessStartInfo
            {
                FileName = ExtractedSetupPath,
                UseShellExecute = false,
                CreateNoWindow = true,

                // Never the app's own working directory, which can be a network share or
                // removable media and would then sit in the child's DLL search path.
                WorkingDirectory = Environment.SystemDirectory
            };

            // ArgumentList, never a concatenated command line: the runtime does the quoting.
            startInfo.ArgumentList.Add("-install");
            startInfo.ArgumentList.Add("-silent");

            using Process? setup = Process.Start(startInfo);
            if (setup == null)
            {
                LoggingService.Error("PawnIoInstaller: the PawnIO installer did not start");
                return null;
            }

            if (!setup.WaitForExit(SetupTimeoutMs))
            {
                // Deliberately not killed. It is halfway through installing a kernel driver,
                // and a killed driver install is a worse state than a slow one; the app
                // carries on with the fallback and the marker keeps it from being retried.
                LoggingService.Warn($"PawnIoInstaller: the PawnIO installer has not finished after {SetupTimeoutMs / 1000} s - leaving it to run and continuing without it");
                return null;
            }

            LoggingService.Info($"PawnIoInstaller: PawnIO installer exited with code {setup.ExitCode}");
            return setup.ExitCode;
        }

        private static void TryDeleteExtractedSetup()
        {
            try
            {
                File.Delete(ExtractedSetupPath);
            }
            catch (Exception ex)
            {
                // The one case that reaches here is the timeout above, where the installer is
                // still running from this file. Harmless: the next accepted prompt overwrites it.
                LoggingService.Debug($"PawnIoInstaller: could not delete the extracted installer: {ex.Message}");
            }
        }
    }
}
