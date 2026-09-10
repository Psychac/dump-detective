using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// An <see cref="IRuntimeThreadQuery"/> backed by data already accumulated during the pipeline's
/// shared <c>ThreadStackScanDispatcher</c> pass, instead of a live walk — the bridge that lets a
/// retyped thread-domain-quartet analyzer's inner <c>AnalyzeAsync</c> call
/// <see cref="EnumerateStackFrames"/> exactly as it always would, with no idea whether the data came
/// from a live walk or this cache, while the pipeline's single shared stack walk still runs exactly
/// once (docs/refactor/modularity/phase-1-thread-quartet-plan.md § 3). Built once per analyzer run by
/// the analyzer's own <c>LegacyAnalyzerAdapter&lt;T&gt;.BuildSdkContext</c> override (e.g.
/// <c>LockGraphAnalyzerLegacyAdapter</c>), from state its <c>IThreadStackScanParticipant.OnThreadStack</c>
/// accumulated.
/// </summary>
internal sealed class PrecomputedRuntimeThreadQuery(
    IReadOnlyDictionary<uint, RuntimeThreadRef> threadsByOsId,
    IReadOnlyDictionary<uint, IReadOnlyList<ThreadStackFrameRef>> framesByOsId) : IRuntimeThreadQuery
{
    public IEnumerable<RuntimeThreadRef> EnumerateThreads() => threadsByOsId.Values;

    public IEnumerable<ThreadStackFrameRef> EnumerateStackFrames(RuntimeThreadRef thread) =>
        framesByOsId.TryGetValue(thread.Thread.OsThreadId, out IReadOnlyList<ThreadStackFrameRef>? frames)
            ? frames
            : [];
}
