using System.Runtime.InteropServices;
using MetricsPusher.Services;

// Second layer of the DLL-hijack defense, behind SystemLibraryResolver: any P/Invoke in
// this assembly that the resolver does not name still loads from System32 alone, never
// from the current directory, PATH, or the executable's own folder.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace MetricsPusher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // FIRST, before any managed code can trigger a native load: pin nvml/pdh/wscapi/
            // nvapi64 to System32 so a DLL planted beside a portable exe cannot be loaded
            // into this process. A resolver only governs loads that have not happened yet.
            SystemLibraryResolver.Install();

            // Before anything draws: sets visual styles, text rendering and the high-DPI mode
            // used by the tray UI and by the PawnIO install prompt, which is the first thing
            // that can put a window on screen.
            ApplicationConfiguration.Initialize();

            // Single-instance guard: the mutex is held for the app's lifetime and
            // released at process exit. If a previous instance crashed, the OS destroys
            // the mutex with its last handle, so the next launch acquires it fresh.
            using var instanceMutex = new Mutex(initiallyOwned: true, Constants.SingleInstanceMutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                LoggingService.Info("Another instance is already running - exiting");
                return;
            }

            // First-run consent prompt for the PawnIO kernel driver, which is the only route
            // to CPU die temperature. After the mutex so only the one live instance can put
            // that prompt on screen, and before the tray UI so the driver is already present
            // by the time TrayApplicationContext starts the push loop. Never throws - the
            // safety net below is not wired yet.
            PawnIoInstaller.EnsureInstalled();

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
    }
}
