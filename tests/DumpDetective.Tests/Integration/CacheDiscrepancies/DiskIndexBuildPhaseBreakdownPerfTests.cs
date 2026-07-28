using System.Diagnostics;
using System.Linq;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

// Cold-build phase breakdown for DiskBackedObjectIndexWriter.Build: forces the .dumpindex
// cache aside so the run reflects a real from-scratch scan (not a cache-hit fast-path),
// then reconstructs per-phase duration from the writer's own progress reports — no source
// instrumentation needed since every phase transition already reports cumulative Elapsed.
// Baseline for the 250s/25GB report; run first on the standard 3.3GB dump before optimizing.
public sealed class DiskIndexBuildPhaseBreakdownPerfTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void Build_ColdScan_PhaseBreakdown()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath))
        {
            output.WriteLine($"Dump not found, skipping: {dumpPath}");
            return;
        }

        string indexDir = DumpIndexPaths.GetIndexDirectory(dumpPath);
        string backupDir = indexDir + ".perfbak";
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

            output.WriteLine($"Thread count: {runtime.Threads.Count()}");
            output.WriteLine($"Heap segment count: {heap.Segments.Length}");

            var phaseMarks = new List<(string Phase, TimeSpan Elapsed)>();
            string? lastPhase = null;
            var progress = new Progress<AnalyzerProgressReport>(r =>
            {
                if (r.Phase != lastPhase)
                {
                    phaseMarks.Add((r.Phase, r.Elapsed ?? TimeSpan.Zero));
                    lastPhase = r.Phase;
                }
            });

            var writer = new DiskBackedObjectIndexWriter();
            Stopwatch totalSw = Stopwatch.StartNew();
            HeapIndexBuildResult result = writer.Build(heap, CancellationToken.None, progress, dumpPath, sizeTier);
            totalSw.Stop();

            output.WriteLine($"Dump: {Path.GetFileName(dumpPath)} ({dumpBytes / (1024.0 * 1024):F1} MB, tier={sizeTier})");
            output.WriteLine($"Objects indexed: {result.ObjectCount:N0}");
            output.WriteLine($"TOTAL cold build: {totalSw.Elapsed.TotalSeconds:F2}s");
            output.WriteLine("Each mark's Elapsed is the timestamp the phase STARTED, so a phase's own");
            output.WriteLine("duration is (next mark's Elapsed - this mark's Elapsed), not the delta");
            output.WriteLine("printed next to its own start line:");
            for (int i = 0; i < phaseMarks.Count; i++)
            {
                TimeSpan duration = i == phaseMarks.Count - 1
                    ? totalSw.Elapsed - phaseMarks[i].Elapsed
                    : phaseMarks[i + 1].Elapsed - phaseMarks[i].Elapsed;
                output.WriteLine($"  {phaseMarks[i].Phase,-28} started at {phaseMarks[i].Elapsed.TotalSeconds,7:F2}s  duration {duration.TotalSeconds,7:F2}s");
            }

            if (result.SatelliteWarnings is { Count: > 0 })
            {
                foreach (string w in result.SatelliteWarnings)
                    output.WriteLine($"Warning: {w}");
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
