using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.ReverseIndex;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

// Verifies the reverse-reference index actually gets built and is queryable end-to-end against a
// real dump — the unit tests for ReverseEdgeExtractor/Sorter/ContainerWriter/IndexReader all drive
// synthetic edges directly and never exercise DiskBackedObjectIndexWriter.Build()'s real wiring
// (obj.EnumerateReferences during the heap scan, then Sort + merge after). Opt-in like its sibling
// DiskIndexBuildPhaseBreakdownPerfTests since it loads a full real dump.
public sealed class ReverseIndexBuildIntegrationTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void Build_ProducesQueryableReverseIndex()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath))
        {
            output.WriteLine($"Dump not found, skipping: {dumpPath}");
            return;
        }

        string indexDir = DumpIndexPaths.GetIndexDirectory(dumpPath);
        string backupDir = indexDir + ".reverseidxbak";
        bool movedExisting = false;
        if (Directory.Exists(indexDir))
        {
            if (Directory.Exists(backupDir))
                Directory.Delete(backupDir, recursive: true);
            Directory.Move(indexDir, backupDir);
            movedExisting = true;
        }

        try
        {
            using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
            ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
            ClrHeap heap = runtime.Heap;

            long dumpBytes = new FileInfo(dumpPath).Length;
            DumpSizeTier sizeTier = dumpBytes > 4L * 1024 * 1024 * 1024 ? DumpSizeTier.Large :
                                    dumpBytes > 512L * 1024 * 1024 ? DumpSizeTier.Medium :
                                    DumpSizeTier.Small;

            // Pick a real object with at least one known incoming reference before building,
            // so we have a concrete (parent, child) pair to check the index against afterward.
            ulong? knownChild = null;
            ulong? knownParent = null;
            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null)
                    continue;
                foreach (ClrObject reference in obj.EnumerateReferences(carefully: true))
                {
                    if (reference.IsValid)
                    {
                        knownParent = obj.Address;
                        knownChild = reference.Address;
                        break;
                    }
                }
                if (knownChild is not null)
                    break;
            }

            Assert.True(knownChild is not null, "Expected at least one forward reference somewhere on this dump's heap.");

            var writer = new DiskBackedObjectIndexWriter();
            HeapIndexBuildResult result = writer.Build(heap, CancellationToken.None, progress: null, dumpPath, sizeTier);

            output.WriteLine($"Objects indexed: {result.ObjectCount:N0}");
            if (result.SatelliteWarnings is { Count: > 0 })
                foreach (string w in result.SatelliteWarnings)
                    output.WriteLine($"Warning: {w}");

            string containerPath = DumpIndexPaths.CacheContainer(dumpPath);
            Assert.True(CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? container), "cache.bin failed to open");
            Assert.True(container!.ContainsSection(CacheSectionId.ReverseEdgeMetadata), "ReverseEdgeMetadata section missing");

            Assert.True(ReverseEdgeIndexReader.TryOpen(container, out ReverseEdgeIndexReader? reader), "ReverseEdgeIndexReader.TryOpen failed");
            using (reader)
            {
                bool found = reader!.TryGetParents(knownChild!.Value, out IReadOnlyList<ulong> parents, out bool truncated);
                output.WriteLine($"Known edge: parent=0x{knownParent:X} child=0x{knownChild:X} found={found} truncated={truncated} parentCount={parents.Count}");
                Assert.True(found, "Reverse index has no record of a child we just observed via EnumerateReferences");
                Assert.Contains(knownParent!.Value, parents);
            }
        }
        finally
        {
            if (Directory.Exists(indexDir))
                Directory.Delete(indexDir, recursive: true);
            if (movedExisting)
                Directory.Move(backupDir, indexDir);
        }
    }
}
