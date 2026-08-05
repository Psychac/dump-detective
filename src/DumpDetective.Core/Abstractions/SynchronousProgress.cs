namespace DumpDetective.Core.Abstractions;

/// <summary>
/// Invokes its callback synchronously, on whatever thread calls <see cref="Report"/> — unlike
/// the built-in <see cref="Progress{T}"/>, which always marshals the callback through a
/// <see cref="SynchronizationContext"/> or, in a console app with none, the ThreadPool.
/// That marshaling starves entirely when the reporting thread is itself a ThreadPool worker
/// inside a CPU-bound <c>Parallel.For</c> that occupies every worker thread: the queued callback
/// never runs until the parallel work finishes, so live progress silently stalls at 0 for the
/// whole pass. Safe here because the wrapped handler (console diagnostics publishing) already
/// takes its own lock.
/// </summary>
public sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly Action<T> _handler = handler;

    public void Report(T value) => _handler(value);
}
