using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing;

internal sealed class DiskBackedObjectIndexWriter
{
    private const int HeaderSize = 24;
    private const int Magic = 0x58494444; // DDIX
    private const int Version = 1;
    private const int RecordSize = sizeof(ulong) * 3;
    private const int ProgressReportEveryObjects = 100_000;

    public HeapIndexBuildResult Build(
        ClrHeap heap,
        string dumpPath,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null,
        DumpDetective.Core.Models.DumpSizeTier sizeTier = DumpDetective.Core.Models.DumpSizeTier.Medium)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string indexPath = CreateIndexPath(dumpPath);

        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);

        long objectCount = 0;

        int writeBuffer = sizeTier switch
        {
            DumpDetective.Core.Models.DumpSizeTier.Large => 4 * 1024 * 1024,
            DumpDetective.Core.Models.DumpSizeTier.Medium => 1 * 1024 * 1024,
            _ => 128 * 1024,
        };
        // Each segment gets its own entry list sized from its own byte length (see below).

        // Cap DOP so ClrMD's minidump page cache never holds more than this many segments'
        // pages resident simultaneously. Uncapped (default -1) causes ProcessorCount threads
        // to each hold a different segment's pages in cache concurrently, which multiplied the
        // working-set footprint proportional to core count after the parallel rearchitecture.
        // 4 concurrent segments gives ~4x speedup over sequential while bounding peak page pressure.
        const int MaxSegmentParallelism = 4;

        var masterBuilder = new TypeAggregateIndexBuilder();
        var moduleRegistry = new ModuleRegistry();
        var allSegmentEntries = new System.Collections.Concurrent.ConcurrentBag<List<HeapEntry>>();

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = MaxSegmentParallelism
        };

        Parallel.ForEach(
            heap.Segments,
            parallelOptions,
            () => new TypeAggregateIndexBuilder(),
            (segment, _, localBuilder) =>
            {
                // Size this segment's list from its own byte length — matches the 128-byte average
                // object size heuristic used for the whole-heap estimate, but applied per-segment
                // so the estimate is local to each segment rather than divided by thread count.
                // This eliminates the ÷ threadCount skew that caused heavily-loaded threads to
                // undershoot their capacity and trigger repeated List<T> doubling.
                int segEstimate = (int)Math.Min(Math.Max(segment.Length / 128, 64), int.MaxValue);
                var segEntries = new List<HeapEntry>(segEstimate);

                foreach (ClrObject obj in segment.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null)
                        continue;
                    ulong mt = obj.Type.MethodTable;
                    if (mt == 0)
                        continue;
                    var entry = new HeapEntry(obj.Address, mt, obj.Size);
                    int moduleId = moduleRegistry.GetOrAdd(obj.Type.Module);
                    segEntries.Add(entry);
                    localBuilder.Add(entry, moduleId);

                    long count = Interlocked.Increment(ref objectCount);
                    if (progress is not null && count % ProgressReportEveryObjects == 0)
                        progress.Report(new(count, "indexing heap", Detail: null, Elapsed: stopwatch.Elapsed));
                }

                allSegmentEntries.Add(segEntries);
                return localBuilder;
            },
            localBuilder =>
            {
                lock (masterBuilder)
                    masterBuilder.Merge(localBuilder);
            });

        // Flatten per-segment lists into a single array.
        // objectCount is exact at this point so we allocate the array at the right size,
        // copying each segment's contiguous span in one pass — no intermediate List resize.
        int totalCount = (int)Math.Min(objectCount, int.MaxValue);
        HeapEntry[] entries = new HeapEntry[Math.Max(totalCount, 1)];
        int writeOffset = 0;
        foreach (List<HeapEntry> segList in allSegmentEntries)
        {
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(segList)
                .CopyTo(entries.AsSpan(writeOffset));
            writeOffset += segList.Count;
        }

        // Now write flattened entries to disk using batched writes
        using (FileStream stream = new(indexPath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: writeBuffer, FileOptions.SequentialScan))
        {
            WriteHeader(stream, recordCount: 0);

            // Rent a larger buffer so we can batch many records per single FileStream.Write
            int batchSize = Math.Max(writeBuffer, RecordSize);
            // Make batchSize a multiple of RecordSize to avoid partial-record writes
            batchSize = (batchSize / RecordSize) * RecordSize;
            if (batchSize == 0) batchSize = RecordSize;

            byte[]? rented = ArrayPool<byte>.Shared.Rent(batchSize);
            try
            {
                int offset = 0;
                long writtenCount = 0;
                foreach (HeapEntry entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var span = new Span<byte>(rented, offset, RecordSize);
                    BinaryPrimitives.WriteUInt64LittleEndian(span, entry.Address);
                    BinaryPrimitives.WriteUInt64LittleEndian(span[8..], entry.MethodTable);
                    BinaryPrimitives.WriteUInt64LittleEndian(span[16..], entry.Size);

                    offset += RecordSize;
                    writtenCount++;

                    if (progress is not null && writtenCount % ProgressReportEveryObjects == 0)
                        progress.Report(new(writtenCount, "writing index", Detail: null, Elapsed: stopwatch.Elapsed));

                    if (offset == batchSize)
                    {
                        stream.Write(rented, 0, offset);
                        offset = 0;
                    }
                }

                if (offset > 0)
                    stream.Write(rented, 0, offset);

                stream.Flush();
                stream.Position = 0;
                WriteHeader(stream, objectCount);
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }

        stopwatch.Stop();
        progress?.Report(new(objectCount, "writing index", Detail: null, Elapsed: stopwatch.Elapsed));

        return new HeapIndexBuildResult(
            HeapIndexStorageKind.Disk,
            indexPath,
            objectCount,
            stopwatch.Elapsed,
            masterBuilder.Build(),
            InMemoryEntries: null,
            Modules: moduleRegistry.Modules);
    }

    private static void WriteHeader(Stream stream, long recordCount)
    {
        Span<byte> headerBuffer = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(headerBuffer, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(headerBuffer[4..], Version);
        BinaryPrimitives.WriteInt64LittleEndian(headerBuffer[8..], DateTime.UtcNow.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(headerBuffer[16..], recordCount);
        stream.Write(headerBuffer);
    }

    private static string CreateIndexPath(string dumpPath)
    {
        string baseName = Path.GetFileNameWithoutExtension(dumpPath);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dumpPath))).Substring(0, 12);
        string fileName = $"{baseName}.{hash}.ddix";
        return Path.Combine(Path.GetTempPath(), "DumpDetective", "indexes", fileName);
    }
}
