using Microsoft.Diagnostics.Runtime;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// One-off diagnostic, not a regression test: tallies <see cref="ClrRoot.RootKind"/> directly from
/// <see cref="ClrHeap.EnumerateRoots"/>, with zero dependency on this project's root cache, index
/// writer, or classification logic — the point is to answer "does this dump have any
/// StaticVar/ThreadStaticVar roots at all" independent of whether our own pipeline is the one asking.
/// Written to settle whether <c>StaticRootLeakDetector</c> reporting zero static roots on two real
/// dumps is a real dump characteristic or a bug in how this project reads/classifies roots.
/// </summary>
public sealed class RootKindHistogramDiagnosticTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void EnumerateRoots_RootKindHistogram()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        var counts = new Dictionary<ClrRootKind, int>();
        long total = 0;
        int staticVarSamplesShown = 0;

        foreach (ClrRoot root in heap.EnumerateRoots())
        {
            total++;
            counts.TryGetValue(root.RootKind, out int c);
            counts[root.RootKind] = c + 1;

            if (root.RootKind == ClrRootKind.StaticVar && staticVarSamplesShown < 5)
            {
                output.WriteLine($"  sample StaticVar root: Address=0x{root.Address:X} Object=0x{root.Object:X}");
                staticVarSamplesShown++;
            }
        }

        output.WriteLine($"dump: {dumpPath}");
        output.WriteLine($"total roots: {total:N0}");
        foreach ((ClrRootKind kind, int count) in counts.OrderByDescending(kv => kv.Value))
            output.WriteLine($"  {kind}: {count:N0}");

        total.Should().BeGreaterThan(0);
    }
}
