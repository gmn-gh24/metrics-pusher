namespace MetricsPusher.Services
{
    /// <summary>
    /// The app's only diagnostic channel - the tray menu holds nothing but Exit, so when the
    /// display stays blank this file is the sole way to learn why.
    /// <para>
    /// A healthy session writes roughly eight lines at startup and one at exit, then nothing:
    /// the services deliberately log failures edge-triggered, one line per failure streak.
    /// The handful of per-tick catch blocks that are NOT edge-triggered (the NVAPI sensor
    /// reads, RAM and free-disk) would otherwise repeat at the 1 Hz sweep rate forever on a
    /// persistently broken sensor, so <see cref="Log"/> collapses consecutive identical lines
    /// - which applies the same "one line per streak" rule to every call site at once, this
    /// one included, rather than to the eight that remembered it.
    /// </para>
    /// </summary>
    internal static class LoggingService
    {
        // Sized for what this actually writes. With duplicate collapsing the pathological
        // case is gone, and a normal session costs about a kilobyte - so 2 MB is already
        // years of history and the folder is capped at 6 MB rather than 40.
        private const long MaxFileSizeBytes = 2L * 1024 * 1024;
        private const int MaxBackupFiles = 2;

        private static readonly string LogFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MetricsPusher", "logs");

        private static readonly string LogPath = Path.Combine(LogFolder, "app.log");
        private static readonly object _lock = new object();

        // Duplicate-collapsing state, all guarded by _lock.
        private static string? _lastLine;
        private static int _suppressedRepeats;

        public static void Debug(string message)
        {
            Log("DEBUG", message);
        }

        public static void Info(string message)
        {
            Log("INFO", message);
        }

        public static void Warn(string message)
        {
            Log("WARN", message);
        }

        public static void Error(string message, Exception? ex = null)
        {
            var fullMessage = ex != null ? $"{message}: {ex}" : message;
            Log("ERROR", fullMessage);
        }

        private static void Log(string level, string message)
        {
            try
            {
                lock (_lock)
                {
                    // Level and text together, so the same words at two levels stay distinct.
                    // Newlines are stripped rather than escaped: a message reaching here can
                    // carry an OS exception string, and one line per event is what makes the
                    // file readable and un-forgeable.
                    string line = $"[{level}] {message}".ReplaceLineEndings(" ");

                    if (line == _lastLine)
                    {
                        _suppressedRepeats++;
                        return;
                    }

                    EnsureDirectory();
                    RotateIfNeeded();

                    if (_suppressedRepeats > 0)
                    {
                        Append($"[INFO] ... previous line repeated {_suppressedRepeats} more times");
                        _suppressedRepeats = 0;
                    }

                    Append(line);
                    _lastLine = line;
                }
            }
            catch
            {
                // Silently fail - logging should never crash the app
            }
        }

        /// <summary>
        /// Writes one already-formatted line, stamped. Opened with
        /// <see cref="FileShare.ReadWrite"/> because the single-instance mutex is
        /// <c>Local\</c> on purpose: RDP and fast user switching give the same user two
        /// instances, and they share this file. <see cref="File.AppendAllText(string, string?)"/>
        /// permits only readers, so the second instance's writes would hit a sharing
        /// violation and vanish into the catch above.
        /// </summary>
        /// <param name="line">The bracketed level and message, without timestamp or newline.</param>
        private static void Append(string line)
        {
            using var stream = new FileStream(
                LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);

            // Local time: this file is read by a person, beside Event Viewer and Task Manager.
            writer.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }

        private static void EnsureDirectory()
        {
            if (!Directory.Exists(LogFolder))
            {
                Directory.CreateDirectory(LogFolder);
            }
        }

        private static void RotateIfNeeded()
        {
            if (!File.Exists(LogPath))
                return;

            var fileInfo = new FileInfo(LogPath);
            if (fileInfo.Length >= MaxFileSizeBytes)
            {
                // Shift existing backups: .1 → .2 (oldest is discarded)
                for (int i = MaxBackupFiles - 1; i >= 1; i--)
                {
                    string source = Path.Combine(LogFolder, $"app.log.{i}");
                    string dest = Path.Combine(LogFolder, $"app.log.{i + 1}");

                    if (File.Exists(dest))
                        File.Delete(dest);
                    if (File.Exists(source))
                        File.Move(source, dest);
                }

                // Move current log to .1
                File.Move(LogPath, Path.Combine(LogFolder, "app.log.1"));
            }
        }
    }
}
