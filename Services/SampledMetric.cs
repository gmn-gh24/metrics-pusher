#pragma warning disable SA1402, SA1649 // File may only contain a single type - the non-generic base exists solely so GpuMonitorService can hold every metric in one array

namespace MetricsPusher.Services
{
    /// <summary>
    /// Non-generic base for <see cref="SampledMetric{T}"/>: lets the owner keep one
    /// array of differently-typed metrics to reset together, and exposes what the
    /// last <c>Get</c> actually did (the handle-loss rule counts executed reads only).
    /// </summary>
    internal abstract class SampledMetric
    {
        /// <summary>Cadence sentinel: read once per session, latched on the first non-null result.</summary>
        internal const int Session = -1;

        /// <summary>Cadence sentinel: read on every sweep.</summary>
        internal const int Live = 0;

        /// <summary>
        /// Gets a value indicating whether the last <c>Get</c> issued its underlying
        /// read; false means the cached value was served.
        /// </summary>
        internal bool LastGetExecuted { get; private protected set; }

        /// <summary>
        /// Gets a value indicating whether the read issued by the last <c>Get</c>
        /// returned null. Meaningless while <see cref="LastGetExecuted"/> is false.
        /// </summary>
        internal bool LastGetReturnedNull { get; private protected set; }

        /// <summary>Drops the cached value so the next <c>Get</c> reads again.</summary>
        internal abstract void Reset();
    }

    /// <summary>
    /// One sensor value plus the cadence at which paying for its read is worth it.
    /// The owner assembles every field on every sweep; this class decides which of
    /// those fields cost an NVAPI call this time and which are re-served from cache.
    /// <para>
    /// Deliberately not thread-safe: the caller must already hold
    /// <c>GpuMonitorService._lock</c>, which is the same lock every NVAPI call in the
    /// sweep runs under (the startup probe deliberately runs outside it), so a lock of
    /// its own would only add cost.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The nullable value type the read produces (null = unavailable).</typeparam>
    internal sealed class SampledMetric<T> : SampledMetric
    {
        private readonly int _intervalMs;
        private readonly Func<T?> _read;
        private long _lastTicks;
        private T? _value;
        private bool _hasValue;

        /// <param name="intervalMs"><see cref="Session"/>, <see cref="Live"/>, or a millisecond interval.</param>
        /// <param name="read">The underlying read; must return null rather than throw when unavailable.</param>
        internal SampledMetric(int intervalMs, Func<T?> read)
        {
            _intervalMs = intervalMs;
            _read = read;
        }

        /// <summary>
        /// The current value: freshly read when due, otherwise the cached one.
        /// </summary>
        /// <param name="nowTicks">The sweep's Environment.TickCount64.</param>
        /// <param name="highFidelity">True while a consumer needs every metric live (GPU Monitor window open).</param>
        internal T? Get(long nowTicks, bool highFidelity)
        {
            bool due = !_hasValue
                || _intervalMs == Live
                || highFidelity
                || (_intervalMs != Session && nowTicks - _lastTicks >= _intervalMs);

            LastGetExecuted = due;
            if (!due)
                return _value;

            _value = _read();
            _lastTicks = nowTicks;
            LastGetReturnedNull = _value is null;

            // A session read that failed must retry on the next sweep instead of
            // caching the failure forever; every other cadence latches either way and
            // retries on its own clock, so a failing sensor costs one read per period.
            _hasValue = _intervalMs != Session || _value is not null;
            return _value;
        }

        /// <inheritdoc/>
        internal override void Reset()
        {
            _value = default;
            _hasValue = false;
            _lastTicks = 0;
            LastGetExecuted = false;
            LastGetReturnedNull = false;
        }
    }
}
