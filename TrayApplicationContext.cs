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
        /// <summary>
        /// How many times to re-check for a GPU after <see cref="GpuMonitorService.Initialize"/>
        /// returned without finding one. See <see cref="InitializeAndStartPushAsync"/>.
        /// </summary>
        private const int LateGpuDetectionAttempts = 24;

        private static readonly TimeSpan LateGpuDetectionInterval = TimeSpan.FromSeconds(5);
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
            _ = Task.Run(() => InitializeAndStartPushAsync(pushToken));
        }

        private static Icon LoadTrayIcon()
        {
            var stream = ExecutingAssembly.GetManifestResourceStream("MetricsPusher.Resources.trayicon.ico");
            if (stream != null)
                return new Icon(stream);
            return SystemIcons.Application;
        }

        /// <summary>
        /// Probes for a GPU and starts the push loop once one is found.
        /// <para>
        /// <see cref="GpuMonitorService.Initialize"/> waits at most 30 seconds for the NVAPI
        /// probe, but on timeout the probe task keeps running and can report a GPU AFTER
        /// Initialize has already returned. Something has to notice that late answer or the
        /// app silently never pushes on a slow-probing machine; this poll is that something.
        /// </para>
        /// </summary>
        /// <param name="token">Cancelled when the app exits.</param>
        private async Task InitializeAndStartPushAsync(CancellationToken token)
        {
            try
            {
                GpuMonitorService.Initialize();

                if (GpuMonitorService.IsGpuAvailable)
                {
                    StartPushOnce(token);
                    return;
                }

                using var timer = new PeriodicTimer(LateGpuDetectionInterval);
                for (int attempt = 0; attempt < LateGpuDetectionAttempts; attempt++)
                {
                    if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                        return;

                    if (GpuMonitorService.IsGpuAvailable)
                    {
                        LoggingService.Info("NVIDIA GPU detected after the startup probe timed out");
                        StartPushOnce(token);
                        return;
                    }
                }

                LoggingService.Info("No NVIDIA GPU detected - metrics push disabled for this session");
            }
            catch (OperationCanceledException)
            {
                // App exit during init - silently stop
            }
            catch (Exception ex)
            {
                LoggingService.Error("GPU initialization faulted", ex);
            }
        }

        /// <summary>
        /// Starts the metrics push loop exactly once per session.
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
