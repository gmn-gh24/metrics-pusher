namespace MetricsPusher.Services
{
    internal static class LoggingService
    {
        private const long MaxFileSizeBytes = 10L * 1024 * 1024; // 10MB
        private const int MaxBackupFiles = 3;

        private static readonly string LogFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MetricsPusher", "logs");

        private static readonly string LogPath = Path.Combine(LogFolder, "app.log");
        private static readonly object _lock = new object();

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
                    EnsureDirectory();
                    RotateIfNeeded();

                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    var logLine = $"[{timestamp}] [{level}] {message}{Environment.NewLine}";
                    File.AppendAllText(LogPath, logLine);
                }
            }
            catch
            {
                // Silently fail - logging should never crash the app
            }
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
                // Shift existing backups: .2 → .3, .1 → .2 (oldest is discarded)
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
