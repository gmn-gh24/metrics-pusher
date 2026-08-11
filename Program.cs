using System.Security.Principal;
using MetricsPusher.Services;

namespace MetricsPusher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // Before any window: sets visual styles, text rendering, and the high-DPI mode
            // that the elevation message box below is drawn with.
            ApplicationConfiguration.Initialize();

            // The active half of the no-admin rule (app.manifest holds the passive half).
            // Checked BEFORE the mutex on purpose: an elevated launch must leave nothing
            // behind, least of all a handle on the single-instance mutex that the
            // legitimate, unelevated instance owns.
            if (IsElevated())
            {
                LoggingService.Warn("Launched elevated - refusing to run");
                MessageBox.Show(
                    "MetricsPusher must not be run as administrator.\n\nRelaunch it normally.",
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
    }
}
