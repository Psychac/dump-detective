using DumpDetective.Analysis.Traversal;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;

namespace DumpDetective.Tests.Unit.Traversal;

/// <summary>
/// Attaches the test process's own live heap via <c>DataTarget.CreateSnapshotAndAttach</c>,
/// same pattern as <c>RetainedSizeCandidateSelectorTests</c> — fast, deterministic, not a
/// real-dump test under the CLAUDE.md "never run in parallel" rule.
/// </summary>
public sealed class BoundedGraphWalkTests
{
    private sealed class WrapperWithReference
    {
        private readonly string _key = "bounded-graph-walk-key";
        public WrapperWithReference() { }
        public override string ToString() => _key;
    }

    private static WrapperWithReference? s_wrapper;

    [Fact]
    public void ComputeExclusiveRetained_PreCancelledToken_ThrowsOperationCanceled()
    {
        s_wrapper = new WrapperWithReference();

        using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        ulong wrapperAddr = 0;
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (obj.IsValid && obj.Type?.Name?.EndsWith(nameof(WrapperWithReference), StringComparison.Ordinal) == true)
            {
                wrapperAddr = obj.Address;
                break;
            }
        }

        wrapperAddr.Should().NotBe(0UL);
        ClrObject wrapperObj = heap.GetObject(wrapperAddr);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var visited = new HashSet<ulong>();
        Action act = () => BoundedGraphWalk.ComputeExclusiveRetained(
            wrapperObj, heap, visited, cancellationToken: cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }
}
