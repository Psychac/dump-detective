using System;
using System.IO;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.Satellite;

internal static class HandleSnapshotProvider
{
    public static IHandleSnapshotReader CreateFromDiskIfExists(string containerPath)
    {
        if (string.IsNullOrEmpty(containerPath)) return null!;

        if (!CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader))
            return null!;

        if (reader is not null && reader.ContainsSection(CacheSectionId.Handles))
            return new DiskHandleSnapshotReader(containerPath);

        return null!;
    }

    public static IHandleSnapshotReader CreateMemoryReader(ClrRuntime runtime, ClrHeap heap, int cap)
        => new MemoryHandleSnapshotReader(runtime, heap, cap);
}
