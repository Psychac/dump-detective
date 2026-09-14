using DumpDetective.Analysis.Pipeline;
using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Infra;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Exercises <see cref="TimerLeakAnalyzer"/>'s real field-reading code path against a live
/// self-process heap snapshot through the real <see cref="DumpObjectFieldQuery"/>/
/// <see cref="HeapObjectLookup"/> -- not the dictionary-backed fakes
/// <c>TimerLeakAnalyzerRetypingCharacterizationTests</c> uses. This process runs on modern .NET, so
/// it exercises the "_period"/"_timerCallback" field names (verified via decompile against
/// <c>System.Private.CoreLib.dll</c>); the .NET Framework "m_period"/"m_timerCallback" names are
/// decompile-verified against the real <c>mscorlib.dll</c> but only exercised for real by
/// <c>TimerLeakAnalyzerRealDumpTests</c> if that dump happens to be a .NET Framework process.
///
/// Picks the candidate whose real "_period" field reads back as this test's own known value,
/// rather than assuming <see cref="LiveHeapSnapshotFixture"/>'s first match is this test's timer --
/// a real .NET process's heap routinely holds other, unrelated TimerQueueTimer instances (framework
/// internals, the test runner itself), so a first-match assumption is not safe here the way it is
/// for the fake, test-file-only fixture types other live-heap tests in this suite use.
/// </summary>
public sealed class TimerLeakAnalyzerLiveHeapTests
{
    // Kept alive as static fields so the timer and its underlying TimerQueueTimer stay reachable
    // for the heap snapshot.
    private static System.Threading.Timer? s_timer;
    private static TimerLeakAnalyzerLiveHeapTests? s_callbackOwner;
    private const uint KnownPeriodMs = 54_321;

    [Fact]
    public async Task AnalyzeAsync_RealThreadingTimer_ReadsPeriodAndResolvesCallbackOwner()
    {
        s_callbackOwner = new TimerLeakAnalyzerLiveHeapTests();
        s_timer = new System.Threading.Timer(s_callbackOwner.OnTick, null, dueTime: 60_000, period: KnownPeriodMs);

        (DataTarget dataTarget, ClrHeap heap, DumpObjectFieldQuery fields, ulong address, ulong methodTable) = AttachAndFindOwnTimerQueueTimer();
        using (dataTarget)
        {
            HeapObjectRef candidate = new(address, SdkTypeRefFactory.FromName("System.Threading.TimerQueueTimer", methodTable), Size: 64, TypeDisplayName: "System.Threading.TimerQueueTimer");
            HeapAnalysisCache cache = new();

            TimerLeakAnalyzer analyzer = new();
            DumpDetective.Sdk.Analysis.AnalysisContext context = new() {
                Observations = NoOpObservationSink.Instance,
                DumpObjectFields = fields,
                HeapSegments = new HeapSegmentQuery(heap, heap.Runtime, cache),
                HeapObjectLookup = new HeapObjectLookup(heap, cache),
                HeapRootPaths = new EmptyHeapRootPathQuery(),
                HeapObjects = new PrecomputedHeapObjectStream([candidate]),
            };

            await analyzer.AnalyzeAsync(context, CancellationToken.None);
            var result = (TimerLeakDomainResult)analyzer.LastResult!;

            result.IntervalHistogram.Should().ContainSingle(b => b.Bucket == "> 1 s" && b.Count == 1);
            result.ByType.Should().ContainSingle().Which.Samples.Should().ContainSingle()
                .Which.CallbackOwnerType.Should().Be(typeof(TimerLeakAnalyzerLiveHeapTests).FullName);
        }
    }

    // Mirrors LiveHeapSnapshotFixture.AttachAndFind's retry-the-whole-attach pattern (its own
    // remarks: DataTarget.CreateSnapshotAndAttach occasionally races against this process's own
    // state) -- can't reuse that helper directly since it only matches by type-name suffix, not
    // this test's "_period == known value" match.
    private static (DataTarget DataTarget, ClrHeap Heap, DumpObjectFieldQuery Fields, ulong Address, ulong MethodTable) AttachAndFindOwnTimerQueueTimer(int maxAttempts = 5)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            ClrHeap heap = dataTarget.ClrVersions[0].CreateRuntime().Heap;
            var fields = new DumpObjectFieldQuery(heap);

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (obj.Type?.Name != "System.Threading.TimerQueueTimer")
                    continue;

                if (fields.TryReadField(obj.Address, "_period", out FieldValue value)
                    && value.Kind == FieldValueKind.UInt32 && value.AsUInt32() == KnownPeriodMs
                    && obj.Type.MethodTable != 0)
                {
                    return (dataTarget, heap, fields, obj.Address, obj.Type.MethodTable);
                }
            }

            dataTarget.Dispose();
        }

        throw new InvalidOperationException($"No TimerQueueTimer with _period == {KnownPeriodMs} found on the live heap after {maxAttempts} attempts.");
    }

    private void OnTick(object? state)
    {
    }

    private sealed class EmptyHeapRootPathQuery : IHeapRootPathQuery
    {
        public bool TryFindRootPath(ulong targetAddress, RootPathSearchOptions options, out string? formattedPath, out bool searchTruncated)
        {
            formattedPath = null;
            searchTruncated = false;
            return false;
        }

        public bool TryFindRootPathHops(ulong targetAddress, RootPathSearchOptions options, out string? rootKind, out IReadOnlyList<string>? hopTypeNames, out bool searchTruncated)
        {
            rootKind = null;
            hopTypeNames = null;
            searchTruncated = false;
            return false;
        }

        public bool TryFindRootPathAddresses(ulong targetAddress, RootPathSearchOptions options, out string? rootKind, out IReadOnlyList<ulong>? hopAddresses, out bool searchTruncated)
        {
            rootKind = null;
            hopAddresses = null;
            searchTruncated = false;
            return false;
        }
    }
}
