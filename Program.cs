using System.Runtime.InteropServices;
using System.Security.Principal;
using MetricsPusher.Services;
using Microsoft.Win32;

// Second layer of the DLL-hijack defense, behind SystemLibraryResolver: any P/Invoke in
// this assembly that the resolver does not name still loads from System32 alone, never
// from the current directory, PATH, or the executable's own folder.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace MetricsPusher
{
    internal static class Program
    {
        /// <summary>
        /// <c>HKLM\...\Policies\System\EnableLUA</c>: 0 means UAC is switched off machine-wide,
        /// which changes what <see cref="IsElevated"/> can tell us. See <see cref="Main"/>.
        /// </summary>
        private const string UacPolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

        [STAThread]
        private static void Main()
        {
            // FIRST, before any managed code can trigger a native load: pin nvml/pdh/wscapi/
            // nvapi64 to System32 so a DLL planted beside a portable exe cannot be loaded
            // into this process. A resolver only governs loads that have not happened yet.
            SystemLibraryResolver.Install();

            // Before any window: sets visual styles, text rendering, and the high-DPI mode
            // that the elevation message box below is drawn with.
            ApplicationConfiguration.Initialize();

            // The active half of the no-admin rule (app.manifest holds the passive half).
            // Checked BEFORE the mutex on purpose: an elevated launch must leave nothing
            // behind, least of all a handle on the single-instance mutex that the
            // legitimate, unelevated instance owns.
            if (IsElevated())
            {
                // With UAC ON, holding the Administrators group means genuinely elevated and
                // "relaunch normally" is actionable. With UAC OFF there is no filtered token
                // to fall back to, so every process an admin starts looks elevated and that
                // advice cannot work - say what the situation actually is instead.
                bool uacOff = IsUacDisabled();
                LoggingService.Warn(uacOff
                    ? "Administrators token with UAC disabled - refusing to run"
                    : "Launched elevated - refusing to run");

                MessageBox.Show(
                    uacOff
                        ? "MetricsPusher must not run with administrator rights, and UAC is turned "
                          + "off on this PC - so every program you start has them.\n\nRun it from a "
                          + "standard user account, or turn User Account Control back on."
                        : "MetricsPusher must not be run as administrator.\n\nRelaunch it normally.",
                    "MetricsPusher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            // Single-instance guard: the mutex is held for the app's lifetime and
            // released at process exit. If a previous instance crashed, the OS destroys
            // the mutex with its last handle, so the next launch acquires it fresh.
            using var instanceMutex = new Mutex(initiallyOwned: true, Constants.SingleInstanceMutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                LoggingService.Info("Another instance is already running - exiting");
                return;
            }

            // Global exception safety net: without these, a UI-thread exception shows the
            // default WinForms crash dialog and background-thread exceptions kill the process,
            // in both cases with nothing written to app.log.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
                LoggingService.Error("Unhandled UI exception", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                LoggingService.Error(
                    e.IsTerminating ? "Fatal unhandled exception (terminating)" : "Unhandled exception",
                    e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                LoggingService.Error("Unobserved task exception", e.Exception);
                e.SetObserved();
            };

            Application.Run(new TrayApplicationContext());
        }

        /// <summary>
        /// Whether this process is running elevated.
        /// <para>
        /// <c>IsInRole(Administrator)</c> is the right predicate here rather than "is the
        /// user an admin": an admin user running normally holds a FILTERED token, for which
        /// this returns false. It returns true only under actual elevation - exactly the
        /// case to refuse.
        /// </para>
        /// </summary>
        /// <returns>True when the process token carries the Administrators group.</returns>
        private static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>
        /// Whether UAC is switched off machine-wide. Only used to word the refusal above:
        /// the refusal itself does not change, because an unfiltered Administrators token is
        /// exactly the privilege this app declines to hold either way.
        /// </summary>
        /// <returns>True when EnableLUA is present and zero; false when set, missing, or unreadable.</returns>
        private static bool IsUacDisabled()
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(UacPolicyKey);
                return key?.GetValue("EnableLUA") is int enableLua && enableLua == 0;
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"Program: Failed to read the UAC policy: {ex.Message}");
                return false;
            }
        }
    }
}
