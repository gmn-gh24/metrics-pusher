namespace MetricsPusher
{
    /// <summary>
    /// Shared constants used across the application.
    /// </summary>
    internal static class Constants
    {
        /// <summary>
        /// Named mutex enforcing a single app instance per user session.
        /// Local\ (not Global\) on purpose: with fast user switching or RDP,
        /// each logged-on session should still get its own tray icon.
        /// </summary>
        public const string SingleInstanceMutexName = "Local\\MetricsPusher_SingleInstance";

        /// <summary>
        /// Minimum valid temperature in Celsius.
        /// GPUs never report below 0°C in normal operation.
        /// </summary>
        public const float MinValidTemperature = 0f;

        /// <summary>
        /// Maximum valid temperature in Celsius.
        /// </summary>
        public const float MaxValidTemperature = 150f;

        /// <summary>
        /// UDP port the display panel listens on. Firmware must match.
        /// </summary>
        public const int DisplayUdpPort = 4210;

        /// <summary>
        /// Fixed host octet of the display panel on the PC's /24 subnet
        /// (e.g. PC 192.168.1.42 → display 192.168.1.99).
        /// </summary>
        public const int DisplayHostOctet = 99;

        /// <summary>
        /// Maximum number of discovery ping attempts before the display
        /// push feature is disabled for the session.
        /// </summary>
        public const int DisplayDiscoveryAttempts = 10;

        /// <summary>
        /// Seconds between discovery ping attempts (hard wall-clock window of
        /// roughly DisplayDiscoveryAttempts × this value).
        /// </summary>
        public const int DisplayDiscoveryIntervalSeconds = 60;

        /// <summary>
        /// Timeout in milliseconds for each discovery ping.
        /// </summary>
        public const int DisplayPingTimeoutMs = 1000;

        /// <summary>
        /// Validates temperature is within reasonable bounds.
        /// Returns false for NaN, infinity, or out-of-bounds values.
        /// </summary>
        /// <param name="temp">The temperature in Celsius.</param>
        /// <returns>True when the value is a real, in-range reading.</returns>
        public static bool IsValidTemperature(float temp) =>
            !float.IsNaN(temp) && !float.IsInfinity(temp) &&
            temp >= MinValidTemperature && temp <= MaxValidTemperature;
    }
}
