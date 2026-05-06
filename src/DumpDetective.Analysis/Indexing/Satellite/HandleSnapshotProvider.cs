using System;
using System.IO;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Indexing.Satellite;

internal static class HandleSnapshotProvider
{
    public static IHandleSnapshotReader CreateFromDiskIfExists(string indexPath)
    {
        if (string.IsNullOrEmpty(indexPath)) return null!;
        string indexDir = Path.GetDirectoryName(indexPath) ?? string.Empty;
        string snapshotPath = Path.Combine(indexDir, DumpIndexPaths.HandleSnapshotFile);
        if (File.Exists(snapshotPath)) return new DiskHandleSnapshotReader(snapshotPath);
        return null!;
    }

    public static IHandleSnapshotReader CreateMemoryReader(ClrRuntime runtime, ClrHeap heap, int cap)
        => new MemoryHandleSnapshotReader(runtime, heap, cap);
}
