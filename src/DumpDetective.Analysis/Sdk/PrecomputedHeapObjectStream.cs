using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// <see cref="IHeapObjectStream"/> backed by an already-collected list, not a live heap. Same
/// "precomputed capability implementation substituted through <c>BuildSdkContext</c>" shape
/// <see cref="PrecomputedRuntimeThreadQuery"/> established for <c>IRuntimeThreadQuery</c> — here for
/// candidate objects a pipeline scan-participant pass (<see cref="TypedResourceCandidateStreamingLegacyAdapter{TSelf, TSdkAnalyzer}"/>)
/// already discovered before the inner SDK analyzer runs.
/// </summary>
internal sealed class PrecomputedHeapObjectStream : IHeapObjectStream
{
    private readonly IReadOnlyList<HeapObjectRef> _objects;

    public PrecomputedHeapObjectStream(IReadOnlyList<HeapObjectRef> objects) => _objects = objects;

    public IEnumerable<HeapObjectRef> EnumerateObjects() => _objects;
}
