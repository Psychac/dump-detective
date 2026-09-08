namespace DumpDetective.Cli.Pipeline;

/// <summary>
/// Records phase-transition timestamps from a stage's progress callback so the phase breakdown
/// survives after the live status line is overwritten by the stage-completion summary — without
/// this, only the last-reported phase is ever visible once a stage finishes, even though most of
/// the elapsed time may have been spent in an earlier phase.
/// </summary>
internal sealed class PhaseTimeline
{
    private readonly List<(string Phase, TimeSpan StartedAt)> _transitions = [];
    private readonly Lock _gate = new();
    // Count of transitions already handed out via DrainCompletedSegments — lets a heartbeat loop
    // poll for newly-finished phases without re-printing ones it already reported.
    private int _drained;

    /// <summary>
    /// Records a transition at <paramref name="elapsed"/>, inserted in timestamp order rather than
    /// blindly appended. <see cref="Progress{T}"/> callbacks without a captured
    /// <see cref="SynchronizationContext"/> are dispatched via the thread pool, so
    /// several reports fired microseconds apart (as the pre-scan phases are) aren't guaranteed to
    /// have their callbacks *execute* in report order — appending blindly could otherwise corrupt
    /// the timeline with an out-of-order, negative-duration segment. Never reorders before the
    /// already-drained boundary — that history is fixed once printed.
    /// </summary>
    public void Record(string phase, TimeSpan elapsed)
    {
        lock (_gate)
        {
            int insertAt = _transitions.Count;
            while (insertAt > _drained && _transitions[insertAt - 1].StartedAt > elapsed)
                insertAt--;

            // Skip if this would be a no-op transition against either neighbor it'd land between.
            if (insertAt > 0 && _transitions[insertAt - 1].Phase == phase)
                return;
            if (insertAt < _transitions.Count && _transitions[insertAt].Phase == phase)
                return;

            _transitions.Insert(insertAt, (phase, elapsed));
        }
    }

    /// <summary>
    /// Returns phases that have finished (i.e. a later phase has already started) since the last
    /// call, and marks them as drained. The most recent phase is never returned here — its duration
    /// isn't known until either the next transition or the stage's final elapsed time, via
    /// <see cref="GetRemainingSegments"/>. Intended to be polled from a heartbeat loop so each
    /// phase can be printed as soon as it's over, instead of only once the whole stage finishes.
    /// </summary>
    public IReadOnlyList<(string Phase, TimeSpan Duration)> DrainCompletedSegments()
    {
        lock (_gate)
        {
            int completedCount = _transitions.Count - 1; // last transition is still in progress
            if (_drained >= completedCount)
                return [];

            var segments = new List<(string, TimeSpan)>(completedCount - _drained);
            for (int i = _drained; i < completedCount; i++)
                segments.Add((_transitions[i].Phase, _transitions[i + 1].StartedAt - _transitions[i].StartedAt));

            _drained = completedCount;
            return segments;
        }
    }

    /// <summary>
    /// Returns every phase not yet handed out by <see cref="DrainCompletedSegments"/>, with the
    /// final (still-open) phase's duration computed against <paramref name="totalElapsed"/>. Call
    /// once after the stage finishes to flush whatever <see cref="DrainCompletedSegments"/> hadn't
    /// caught yet — normally just the last phase.
    /// </summary>
    public IReadOnlyList<(string Phase, TimeSpan Duration)> GetRemainingSegments(TimeSpan totalElapsed)
    {
        lock (_gate)
        {
            if (_drained >= _transitions.Count)
                return [];

            var segments = new List<(string, TimeSpan)>(_transitions.Count - _drained);
            for (int i = _drained; i < _transitions.Count; i++)
            {
                TimeSpan end = i + 1 < _transitions.Count ? _transitions[i + 1].StartedAt : totalElapsed;
                segments.Add((_transitions[i].Phase, end - _transitions[i].StartedAt));
            }

            _drained = _transitions.Count;
            return segments;
        }
    }
}
