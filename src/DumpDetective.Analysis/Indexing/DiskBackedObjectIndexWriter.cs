using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing;

internal sealed class DiskBackedObjectIndexWriter : IObjectIndexWriter
{
    private const int HeaderSize = 24;
    private const int Magic = 0x58494444; // DDIX
    private const int Version = 1;
    private const int RecordSize = sizeof(ulong) * 3;
    private const int ProgressReportEveryObjects = 100_000;

    public HeapIndexBuildResult Build(
        ClrHeap heap,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null,
        string? dumpPath = null,
        DumpDetective.Core.Models.DumpSizeTier sizeTier = DumpDetective.Core.Models.DumpSizeTier.Medium)
    {
        ArgumentNullException.ThrowIfNull(dumpPath, nameof(dumpPath));
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

        var masterBuilder = new TypeIndexBuilder();
        var moduleRegistry = new ModuleRegistry();
        var allSegmentEntries = new System.Collections.Concurrent.ConcurrentBag<(HeapEntry[] Buffer, int Count)>();

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = MaxSegmentParallelism
        };

        Parallel.ForEach(
            heap.Segments,
            parallelOptions,
            () => new TypeIndexBuilder(),
            (segment, _, localBuilder) =>
            {
                // Use minimum .NET object size (24 bytes on x64) as the upper-bound estimate so the
                // initial rent is guaranteed to hold all objects without resizing in the common case.
                // Capped at 1_000_000 entries (~24 MB) to keep each ArrayPool loan reasonable;
                // segments with more objects than the cap grow via pool doubling below — old buffers
                // are returned to the pool rather than discarded as GC garbage, eliminating the
                // ~800 MB of HeapEntry[] backing-array churn observed in profiling.
                const int MaxInitialRent = 1_000_000;
                int initCapacity = (int)Math.Min(Math.Max((long)segment.Length / 24, 64), MaxInitialRent);
                HeapEntry[] segBuf = ArrayPool<HeapEntry>.Shared.Rent(initCapacity);
                int segCount = 0;

                foreach (ClrObject obj in segment.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null)
                        continue;
                    ulong mt = obj.Type.MethodTable;
                    if (mt == 0)
                        continue;

                    if (segCount == segBuf.Length)
                    {
                        // Grow via pool: return old buffer, rent one twice as large.
                        HeapEntry[] bigger = ArrayPool<HeapEntry>.Shared.Rent(segBuf.Length * 2);
                        segBuf.AsSpan(0, segCount).CopyTo(bigger);
                        ArrayPool<HeapEntry>.Shared.Return(segBuf, clearArray: false);
                        segBuf = bigger;
                    }

                    var entry = new HeapEntry(obj.Address, mt, obj.Size);
                    int moduleId = moduleRegistry.GetOrAdd(obj.Type.Module);
                    segBuf[segCount++] = entry;
                    localBuilder.Add(entry, moduleId);

                    long count = Interlocked.Increment(ref objectCount);
                    if (progress is not null && count % ProgressReportEveryObjects == 0)
                        progress.Report(new(count, "indexing heap", Detail: null, Elapsed: stopwatch.Elapsed));
                }

                allSegmentEntries.Add((segBuf, segCount));
                return localBuilder;
            },
            localBuilder =>
            {
                lock (masterBuilder)
                    masterBuilder.Merge(localBuilder);
            });

        // Flatten per-segment pooled buffers into a single exact-sized array,
        // then return each rented buffer to the pool so it can be reused.
        int totalCount = (int)Math.Min(objectCount, int.MaxValue);
        HeapEntry[] entries = new HeapEntry[Math.Max(totalCount, 1)];
        int writeOffset = 0;
        foreach ((HeapEntry[] segBuf, int segCount) in allSegmentEntries)
        {
            segBuf.AsSpan(0, segCount).CopyTo(entries.AsSpan(writeOffset));
            writeOffset += segCount;
            ArrayPool<HeapEntry>.Shared.Return(segBuf, clearArray: false);
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
                    var span = new Span<byte>(rented, offset, RecordSize);
                    BinaryPrimitives.WriteUInt64LittleEndian(span, entry.Address);
                    BinaryPrimitives.WriteUInt64LittleEndian(span[8..], entry.MethodTable);
                    BinaryPrimitives.WriteUInt64LittleEndian(span[16..], entry.Size);

                    offset += RecordSize;
                    writtenCount++;

                    if (writtenCount % ProgressReportEveryObjects == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (progress is not null)
                            progress.Report(new(writtenCount, "writing index", Detail: null, Elapsed: stopwatch.Elapsed));
                    }

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
