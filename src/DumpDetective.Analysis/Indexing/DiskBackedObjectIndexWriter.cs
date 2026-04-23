using System.Buffers.Binary;
using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;

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
        Action<long, TimeSpan>? progress = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string indexPath = CreateIndexPath(dumpPath);

        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);

        TypeAggregateIndexBuilder aggregateBuilder = new();
        long objectCount = 0;

        using (FileStream stream = new(indexPath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 128 * 1024, FileOptions.SequentialScan))
        {
            WriteHeader(stream, recordCount: 0);

            Span<byte> recordBuffer = stackalloc byte[RecordSize];
            foreach (HeapEntry entry in HeapStreamer.Stream(heap))
            {
                cancellationToken.ThrowIfCancellationRequested();

                BinaryPrimitives.WriteUInt64LittleEndian(recordBuffer, entry.Address);
                BinaryPrimitives.WriteUInt64LittleEndian(recordBuffer[8..], entry.MethodTable);
                BinaryPrimitives.WriteUInt64LittleEndian(recordBuffer[16..], entry.Size);
                stream.Write(recordBuffer);

                aggregateBuilder.Add(entry);
                objectCount++;

                if (progress is not null && objectCount % ProgressReportEveryObjects == 0)
                {
                    progress(objectCount, stopwatch.Elapsed);
                }
            }

            stream.Flush();
            stream.Position = 0;
            WriteHeader(stream, objectCount);
        }

        stopwatch.Stop();

        return new HeapIndexBuildResult(HeapIndexStorageKind.Disk, indexPath, objectCount, stopwatch.Elapsed, aggregateBuilder.Build());
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
