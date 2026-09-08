using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;

using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Dump;

/// <summary>
/// Loads a .NET memory dump using ClrMD, validates that the CLR runtime and heap are walkable,
/// and returns a <see cref="DumpLoadContext"/> that owns all allocated resources.
/// </summary>
internal sealed class DumpLoader : IDumpLoader
{
    public Task<DumpLoadContext> LoadAsync(string dumpPath, CancellationToken cancellationToken, IProgress<AnalyzerProgressReport>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!File.Exists(dumpPath))
                throw new DumpLoadException($"Dump file not found: {dumpPath}");

            // Sub-steps reported individually — on large dumps, ClrMD's CreateRuntime() (which
            // locates and loads the matching DAC, then walks the module/AppDomain list to
            // initialize it) is typically the one that dominates this stage's wall-clock, not the
            // initial LoadDump memory-map. Without this, the whole stage looks like a single blank
            // freeze with no indication of which part is actually slow.
            progress?.Report(new(0, "opening dump file", Detail: Path.GetFileName(dumpPath), Elapsed: stopwatch.Elapsed));
            DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
            if (dataTarget.ClrVersions.Length == 0)
            {
                dataTarget.Dispose();
                throw new DumpLoadException($"No CLR versions found in dump: {dumpPath}");
            }

            progress?.Report(new(0, "loading CLR runtime (DAC)", Detail: null, Elapsed: stopwatch.Elapsed));
            ClrInfo clr = dataTarget.ClrVersions[0];
            ClrRuntime runtime = clr.CreateRuntime();
            ClrHeap heap = runtime.Heap;

            progress?.Report(new(0, "validating heap", Detail: null, Elapsed: stopwatch.Elapsed));
            if (!heap.CanWalkHeap)
            {
                runtime.Dispose();
                dataTarget.Dispose();
                throw new DumpLoadException($"Cannot walk heap for dump: {dumpPath}");
            }

            return Task.FromResult(new DumpLoadContext(dumpPath, dataTarget, runtime, heap));
        }
        catch (DumpLoadException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DumpLoadException($"Failed to load dump '{dumpPath}'.", ex);
        }
    }
}

/// <summary>
/// Holds the ClrMD objects produced by loading a dump file.
/// Owns and disposes all underlying ClrMD resources when disposed.
/// </summary>
internal sealed class DumpLoadContext(string dumpPath, DataTarget dataTarget, ClrRuntime runtime, ClrHeap heap) : IDisposable
{
    public string DumpPath { get; } = dumpPath;
    public DataTarget DataTarget { get; } = dataTarget;
    public ClrRuntime Runtime { get; } = runtime;
    public ClrHeap Heap { get; } = heap;

    public void Dispose()
    {
        Runtime.Dispose();
        DataTarget.Dispose();
    }
}

/// <summary>Thrown when a dump file cannot be loaded or is not a valid .NET memory dump.</summary>
internal sealed class DumpLoadException(string message, Exception? innerException = null)
    : Exception(message, innerException);
