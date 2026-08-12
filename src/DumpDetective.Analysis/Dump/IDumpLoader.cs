using Microsoft.Diagnostics.Runtime;

using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Dump;

/// <summary>
/// Contract for loading a .NET memory dump and providing access to the ClrMD runtime objects.
/// Implementations are responsible for proper disposal of all underlying resources.
/// </summary>
internal interface IDumpLoader
{
    /// <summary>
    /// Loads the dump at <paramref name="dumpPath"/> and returns a <see cref="DumpLoadContext"/>
    /// that grants access to the <see cref="ClrRuntime"/> and <see cref="ClrHeap"/>.
    /// The caller is responsible for disposing the returned context.
    /// </summary>
    /// <param name="progress">
    /// Optional phase-label reports (e.g. "opening dump file", "loading CLR runtime (DAC)") — this
    /// stage has no per-object count to drive a rate, so callers use it purely to know which of the
    /// (usually single-digit-second, occasionally much slower) sub-steps is currently running.
    /// </param>
    Task<DumpLoadContext> LoadAsync(string dumpPath, CancellationToken cancellationToken, IProgress<AnalyzerProgressReport>? progress = null);
}
