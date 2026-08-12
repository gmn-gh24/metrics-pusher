using System.Reflection;
using MetricsPusher.Services;

namespace MetricsPusher
{
    /// <summary>
    /// The whole application: a tray icon, a menu holding nothing but Exit, and the
    /// background task that starts the 1 Hz UDP metrics push. All of the interesting
    /// behavior lives in <see cref="GpuDisplayPushService"/> and the services it reads.
    /// </summary>
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private static readonly Assembly ExecutingAssembly = Assembly.GetExecutingAssembly();

        private readonly NotifyIcon _notifyIcon;
        private readonly ToolStripMenuItem _exitItem;
        private readonly CancellationTokenSource _pushCts = new CancellationTokenSource();
        private int _pushStarted; // Interlocked gate: the push loop must start at most once per session

        public TrayApplicationContext()
        {
            _exitItem = new ToolStripMenuItem("Exit");
            _exitItem.Click += OnExitClicked;

            var menu = new ContextMenuStrip();
            menu.Items.Add(_exitItem);

            _notifyIcon = new NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = "Metrics Pusher",
                Visible = true,
                ContextMenuStrip = menu
            };

            var version = ExecutingAssembly.GetName().Version?.ToString() ?? "unknown";
            LoggingService.Info($"MetricsPusher started - Version {version}");

            // Capture the token BEFORE Task.Run: a captured token stays readable after the
            // CTS is disposed (app exit during init), while reading _pushCts.Token inside
            // the lambda would throw ObjectDisposedException.
            CancellationToken pushToken = _pushCts.Token;

            // The GPU probe and the push loop start together, and neither gates the other.
            // A machine with no NVIDIA GPU still pushes its CPU/RAM/disk/network/OS metrics:
            // absent sensors are omitted from the datagram rather than zeroed, so the
            // consumer reads "unknown" for the gpu* keys and renders the rest.
            _ = Task.Run(ProbeGpuInBackground);
            _ = Task.Run(() => StartPushOnce(pushToken));
        }

        private static Icon LoadTrayIcon()
        {
            var stream = ExecutingAssembly.GetManifestResourceStream("MetricsPusher.Resources.trayicon.ico");
            if (stream != null)
                return new Icon(stream);
            return SystemIcons.Application;
        }

        /// <summary>
        /// Probes for an NVIDIA GPU, off the push loop and without gating it.
        /// <para>
        /// <see cref="GpuMonitorService.Initialize"/> blocks for up to 30 seconds, and on
        /// timeout the probe task keeps running and can report a GPU later still. Nothing
        /// needs to watch for that late answer: <see cref="GpuMonitorService.GetGpuMetrics"/>
        /// re-reads the probe's result on every tick, so the gpu* fields simply begin
        /// appearing in the datagram on the first tick after the probe succeeds. The
        /// converse is the point of running this off the loop - a machine that never finds
        /// a GPU pushes every other metric anyway.
        /// </para>
        /// </summary>
        private static void ProbeGpuInBackground()
        {
            try
            {
                GpuMonitorService.Initialize();
            }
            catch (Exception ex)
            {
                LoggingService.Error("GPU initialization faulted", ex);
            }
        }

        /// <summary>
        /// Starts the metrics push loop exactly once per session. The gate is kept even
        /// though one call site remains: it states the invariant rather than relying on
        /// there never being a second caller.
        /// </summary>
        /// <param name="token">Cancelled when the app exits.</param>
        private void StartPushOnce(CancellationToken token)
        {
            if (Interlocked.Exchange(ref _pushStarted, 1) == 0)
                _ = GpuDisplayPushService.RunAsync(token);
        }

        private void OnExitClicked(object? sender, EventArgs e)
        {
            // Hide first: the icon would otherwise linger in the tray until the shell
            // notices the owning process is gone.
            _notifyIcon.Visible = false;
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                LoggingService.Info("MetricsPusher exiting");
                _pushCts.Cancel();
                _pushCts.Dispose();
                GpuMonitorService.Shutdown();

                _exitItem.Click -= OnExitClicked;
                _notifyIcon.ContextMenuStrip?.Dispose();
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
