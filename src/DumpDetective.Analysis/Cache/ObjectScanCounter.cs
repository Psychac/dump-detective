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
        private readonly TimeSpan _reportEveryElapsed;
        private readonly Stopwatch _stopwatch;
        private readonly IProgress<AnalyzerProgressReport>? _progress;

        private long _scanned;
        private long _nextCountReport;
        private TimeSpan _lastElapsedReport;

        public long Scanned => _scanned;

        public ObjectScanCounter(
            string phase,
            IProgress<AnalyzerProgressReport>? progress = null,
            int reportEveryObjects = 250_000,
            TimeSpan? reportEveryElapsed = null)
        {
            _phase = phase;
            _progress = progress;
            _reportEveryObjects = reportEveryObjects;
            _reportEveryElapsed = reportEveryElapsed ?? TimeSpan.FromSeconds(2);
            _stopwatch = Stopwatch.StartNew();
            _nextCountReport = _reportEveryObjects;
            _lastElapsedReport = TimeSpan.Zero;
        }

        public void Tick(string? detail = null)
        {
            _scanned++;

            TimeSpan elapsed = _stopwatch.Elapsed;
            bool reportByCount = _scanned >= _nextCountReport;
            bool reportByTime = elapsed - _lastElapsedReport >= _reportEveryElapsed;

            if (!reportByCount && !reportByTime)
                return;

            _lastElapsedReport = elapsed;

            while (_nextCountReport <= _scanned)
                _nextCountReport += _reportEveryObjects;

            _progress?.Report(new AnalyzerProgressReport(_scanned, _phase, detail));
        }

        public void Complete(string? detail = null)
        {
            _stopwatch.Stop();
            _progress?.Report(new AnalyzerProgressReport(_scanned, _phase, detail));
        }
    }
}


