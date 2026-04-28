using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Dump;

/// <summary>
/// Loads a .NET memory dump using ClrMD, validates that the CLR runtime and heap are walkable,
/// and returns a <see cref="DumpLoadContext"/> that owns all allocated resources.
/// </summary>
internal sealed class DumpLoader : IDumpLoader
{
    public Task<DumpLoadContext> LoadAsync(string dumpPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!File.Exists(dumpPath))
                throw new DumpLoadException($"Dump file not found: {dumpPath}");

            DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
            if (dataTarget.ClrVersions.Length == 0)
            {
                dataTarget.Dispose();
                throw new DumpLoadException($"No CLR versions found in dump: {dumpPath}");
            }

            ClrInfo clr = dataTarget.ClrVersions[0];
            ClrRuntime runtime = clr.CreateRuntime();
            ClrHeap heap = runtime.Heap;

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
