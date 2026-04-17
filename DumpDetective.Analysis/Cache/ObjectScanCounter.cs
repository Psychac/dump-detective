using System.Diagnostics;

namespace DumpDetective.Analysis.Cache
{
    internal sealed class ObjectScanCounter
    {
        private readonly string _operation;
        private readonly int _reportEveryObjects;
        private readonly TimeSpan _reportEveryElapsed;
        private readonly Stopwatch _stopwatch;

        private long _scanned;
        private long _nextCountReport;
        private TimeSpan _lastElapsedReport;

        public ObjectScanCounter(
            string operation,
            int reportEveryObjects = 250_000,
            TimeSpan? reportEveryElapsed = null)
        {
            _operation = operation;
            _reportEveryObjects = reportEveryObjects;
            _reportEveryElapsed = reportEveryElapsed ?? TimeSpan.FromSeconds(2);
            _stopwatch = Stopwatch.StartNew();
            _nextCountReport = _reportEveryObjects;
            _lastElapsedReport = TimeSpan.Zero;
        }

        public void Tick()
        {
            _scanned++;

            TimeSpan elapsed = _stopwatch.Elapsed;
            bool reportByCount = _scanned >= _nextCountReport;
            bool reportByTime = elapsed - _lastElapsedReport >= _reportEveryElapsed;

            if (!reportByCount && !reportByTime)
                return;

            ConsoleUx.ObjectScanProgress(_operation, _scanned, elapsed);
            _lastElapsedReport = elapsed;

            while (_nextCountReport <= _scanned)
            {
                _nextCountReport += _reportEveryObjects;
            }
        }

        public void Complete()
        {
            _stopwatch.Stop();
            ConsoleUx.ObjectScanComplete(_operation, _scanned, _stopwatch.Elapsed);
        }
    }
}


