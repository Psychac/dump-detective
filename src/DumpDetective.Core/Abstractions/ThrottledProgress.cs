using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace DumpDetective.Core.Abstractions;

/// <summary>
/// Wraps an <see cref="IProgress{T}"/> and silently drops calls that arrive within
/// <paramref name="intervalMs"/> milliseconds of the last accepted report.
/// All reporters — indexers, analyzers, phase transitions — can call <c>Report</c>
/// as aggressively as they want (even per-object); the throttle enforces a single
/// global rate limit so the console heartbeat always gets a consistent update rate.
/// Thread-safe and lock-free.
/// </summary>
public sealed class ThrottledProgress<T> : IProgress<T>
{
    private readonly IProgress<T> _inner;
    private readonly long _intervalTicks;
    private long _lastTicks;

    /// <param name="inner">The underlying progress sink to forward accepted reports to.</param>
    /// <param name="intervalMs">Minimum milliseconds between forwarded reports. Default 150ms.</param>
    public ThrottledProgress(IProgress<T> inner, int intervalMs = 150)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _intervalTicks = (long)(intervalMs * (Stopwatch.Frequency / 1000.0));
        _lastTicks = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Report(T value)
    {
        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref _lastTicks);
        if (now - last < _intervalTicks) return;
        // Only one concurrent caller wins the CAS and forwards the report.
        if (Interlocked.CompareExchange(ref _lastTicks, now, last) != last) return;
        _inner.Report(value);
    }
}
