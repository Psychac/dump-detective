using System.Diagnostics;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Cache
{
    /// <summary>
    /// Tracks scanned object counts and fires a throttled <see cref="IProgress{T}"/> report
    /// on cadence so all analyzers get consistent live console output without hand-rolling
    /// interval checks. Pass <c>context.Progress</c> to get reporting for free.
    /// </summary>
    internal sealed class ObjectScanCounter
    {
        private readonly string _phase;
        private readonly int _reportEveryObjects;
        private readonly long _reportEveryElapsedTicks;
        private readonly Stopwatch _stopwatch;
        private readonly IProgress<AnalyzerProgressReport>? _progress;
        // Total object-count hint (e.g. from the disk index header) used to render a percentage
        // instead of a raw scanned count. Null when no caller-supplied hint is available — the
        // counter falls back to raw-count reporting exactly as before.
        private readonly long? _total;

        private long _scanned;
        private long _nextCountReport;
        // Ticks (not TimeSpan) so TickParallel can gate reports with Interlocked ops.
        private long _lastReportTicks;

        public long Scanned => Interlocked.Read(ref _scanned);

        public ObjectScanCounter(
            string phase,
            IProgress<AnalyzerProgressReport>? progress = null,
            int reportEveryObjects = 250_000,
            TimeSpan? reportEveryElapsed = null,
            long? total = null)
        {
            _phase = phase;
            _progress = progress;
            _reportEveryObjects = reportEveryObjects;
            _reportEveryElapsedTicks = (reportEveryElapsed ?? TimeSpan.FromSeconds(2)).Ticks;
            _stopwatch = Stopwatch.StartNew();
            _nextCountReport = _reportEveryObjects;
            _lastReportTicks = 0;
            _total = total is > 0 ? total : null;
        }

        public void Tick(string? detail = null)
        {
            if (ShouldReport())
                Report(detail);
        }

        /// <summary>
        /// Advances the scan count and returns whether a report is due on this cadence, without
        /// building any report payload. Lets hot per-entry callers skip constructing an expensive
        /// <c>detail</c> string (e.g. via interpolation) on every call — only pay for it on the
        /// throttled subset of calls where <see cref="Report"/> will actually be invoked.
        /// Single-threaded callers only — see <see cref="TickParallel"/> for concurrent use.
        /// </summary>
        public bool ShouldReport()
        {
            _scanned++;

            long nowTicks = _stopwatch.ElapsedTicks;
            bool reportByCount = _scanned >= _nextCountReport;
            bool reportByTime = nowTicks - _lastReportTicks >= _reportEveryElapsedTicks;

            if (!reportByCount && !reportByTime)
                return false;

            _lastReportTicks = nowTicks;

            while (_nextCountReport <= _scanned)
                _nextCountReport += _reportEveryObjects;

            return true;
        }

        /// <summary>
        /// Thread-safe equivalent of <see cref="Tick"/> for a scan shared across worker threads
        /// (e.g. <c>HeapIndexScanDispatcher</c>'s parallel pass). Every worker's entries count
        /// toward the same cadence instead of each worker being blind until one coarse report at
        /// the end — only one thread wins the report slot per interval; losers skip without
        /// retrying, since an approximate cadence is fine for a progress indicator.
        /// </summary>
        public void TickParallel(string? detail = null)
        {
            long scanned = Interlocked.Increment(ref _scanned);

            long nextCountReport = Interlocked.Read(ref _nextCountReport);
            long lastReportTicks = Interlocked.Read(ref _lastReportTicks);
            long nowTicks = _stopwatch.ElapsedTicks;

            bool dueByCount = scanned >= nextCountReport;
            bool dueByTime = nowTicks - lastReportTicks >= _reportEveryElapsedTicks;
            if (!dueByCount && !dueByTime)
                return;

            if (Interlocked.CompareExchange(ref _lastReportTicks, nowTicks, lastReportTicks) != lastReportTicks)
                return;

            long next = nextCountReport;
            while (next <= scanned)
                next += _reportEveryObjects;
            Interlocked.Exchange(ref _nextCountReport, next);

            Report(detail);
        }

        /// <summary>
        /// Thread-safe bulk equivalent of <see cref="TickParallel"/> — folds <paramref name="count"/>
        /// locally-accumulated ticks into the shared counter in one atomic op instead of one per
        /// object. Callers should buffer counts in a per-worker local and flush periodically (e.g.
        /// every few thousand objects) rather than calling this once per entry, otherwise it
        /// degenerates back into the same per-object cache-line contention this exists to avoid.
        /// </summary>
        public void AddParallel(long count, string? detail = null)
        {
            if (count <= 0)
                return;

            long scanned = Interlocked.Add(ref _scanned, count);

            long nextCountReport = Interlocked.Read(ref _nextCountReport);
            long lastReportTicks = Interlocked.Read(ref _lastReportTicks);
            long nowTicks = _stopwatch.ElapsedTicks;

            bool dueByCount = scanned >= nextCountReport;
            bool dueByTime = nowTicks - lastReportTicks >= _reportEveryElapsedTicks;
            if (!dueByCount && !dueByTime)
                return;

            if (Interlocked.CompareExchange(ref _lastReportTicks, nowTicks, lastReportTicks) != lastReportTicks)
                return;

            long next = nextCountReport;
            while (next <= scanned)
                next += _reportEveryObjects;
            Interlocked.Exchange(ref _nextCountReport, next);

            Report(detail);
        }

        public void Report(string? detail = null) =>
            _progress?.Report(new AnalyzerProgressReport(Interlocked.Read(ref _scanned), _phase, WithPercent(detail)));

        public void Complete(string? detail = null)
        {
            _stopwatch.Stop();
            _progress?.Report(new AnalyzerProgressReport(Interlocked.Read(ref _scanned), _phase, WithPercent(detail)));
        }

        /// <summary>
        /// Prefixes <paramref name="detail"/> with a "{pct}%" computed against the caller-supplied
        /// <see cref="_total"/> hint, or returns <paramref name="detail"/> unchanged when no hint
        /// was given — the raw scanned count already flows through
        /// <see cref="AnalyzerProgressReport.ScannedCount"/> regardless.
        /// </summary>
        private string? WithPercent(string? detail)
        {
            if (_total is not > 0)
                return detail;

            double pct = Math.Min(100.0, Interlocked.Read(ref _scanned) * 100.0 / _total.Value);
            string pctText = $"{pct:F0}%";
            return string.IsNullOrEmpty(detail) ? pctText : $"{pctText} · {detail}";
        }
    }
}


