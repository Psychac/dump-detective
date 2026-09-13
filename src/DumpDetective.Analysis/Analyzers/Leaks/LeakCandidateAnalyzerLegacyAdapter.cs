using DumpDetective.Analysis.Analyzers.Gc;
using DumpDetective.Analysis.Analyzers.Infra;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers.Leaks;

/// <summary>
/// Runs the retyped <see cref="LeakCandidateAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md and
/// <see cref="LeakCandidateAnalyzer"/>'s own remarks for the cross-analyzer-dependency design this
/// adapter exists to bridge.
/// </summary>
/// <remarks>
/// Implements <c>IDeferredAnalyzer</c> directly — same reasoning as every other marker-interface
/// placement in this plan (<c>IRequiresReachableGraphIndex</c> etc.): the pipeline's two-phase
/// execution (<c>AnalysisPipeline._deferredAnalyzers</c>) inspects the registered
/// <c>Core.Abstractions.IAnalyzer</c> instances, this adapter, not the inner
/// <see cref="LeakCandidateAnalyzer"/>, which the pipeline never sees directly. Dropping this marker
/// here would silently move this analyzer back into the first wave, before
/// <c>GCHandleAnalyzer</c>/<c>TimerLeakAnalyzer</c>/<c>GCRootAnalyzer</c> have necessarily run.
///
/// Overrides <see cref="LegacyAnalyzerAdapter{TSdkAnalyzer}.BuildSdkContext"/> rather than just
/// <see cref="LegacyAnalyzerAdapter{TSdkAnalyzer}.ResolveOptions"/> — the same shape
/// <c>LockGraphAnalyzerLegacyAdapter</c> uses to swap in precomputed thread-stack data — because the
/// cross-analyzer extraction needs the full legacy <c>AnalysisContext</c> (specifically
/// <c>context.CompletedRunResults</c>), not just its <c>AnalysisOptions</c> sub-object.
/// </remarks>
public sealed class LeakCandidateAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<LeakCandidateAnalyzer>, IDeferredAnalyzer
{
    public LeakCandidateAnalyzerLegacyAdapter() : base(new LeakCandidateAnalyzer())
    {
    }

    protected override Sdk.Analysis.AnalysisContext BuildSdkContext(AnalysisContext context)
    {
        IReadOnlyList<AnalyzerRunResult>? completedRunResults = context.CompletedRunResults;

        // Sourced from the already-completed gc-handle analyzer run rather than re-walking
        // runtime.EnumerateHandles() here — avoids a second full handle scan for the same signal.
        GCHandleDomainResult? gcHandleResult = completedRunResults?.GetResult<GCHandleDomainResult>();
        var pinnedTargetTypes = new HashSet<string>(StringComparer.Ordinal);
        var dependentTargetTypes = new HashSet<string>(StringComparer.Ordinal);
        if (gcHandleResult is not null)
        {
            foreach (NameCountEntry entry in gcHandleResult.TopPinnedTargetTypes ?? [])
                pinnedTargetTypes.Add(entry.Name);
            foreach (NameCountEntry entry in gcHandleResult.DependentTopTargetTypes ?? [])
                dependentTargetTypes.Add(entry.Name);
        }

        // Sourced from the already-completed timer-leak analyzer run — LogicalTimerCount is its
        // de-duplicated count (TimerQueueTimerCount), not the raw double-counted TotalTimers, so
        // it's the right magnitude signal to correlate against each timer wrapper type's row here.
        TimerLeakDomainResult? timerLeakResult = completedRunResults?.GetResult<TimerLeakDomainResult>();
        int logicalTimerCount = timerLeakResult?.LogicalTimerCount ?? 0;

        // Sourced from the already-completed GC-root analyzer run — a cheap, exact shortcut for
        // candidates that are themselves a direct root target, before falling back to a bounded
        // root-path search inside the inner analyzer.
        GCRootDomainResult? gcRootResult = completedRunResults?.GetResult<GCRootDomainResult>();
        Dictionary<ulong, RootFinding>? directRootsByTargetAddress = null;
        if (gcRootResult is not null && gcRootResult.TopRootsBySeverity.Count > 0)
        {
            directRootsByTargetAddress = new Dictionary<ulong, RootFinding>(gcRootResult.TopRootsBySeverity.Count);
            foreach (RootFinding root in gcRootResult.TopRootsBySeverity)
                directRootsByTargetAddress[root.TargetAddress] = root;
        }

        ReferenceChainOptions referenceChainOptions = context.AnalysisOptions.ReferenceChain;
        var options = new LeakCandidateAnalyzerOptions(
            referenceChainOptions, pinnedTargetTypes, dependentTargetTypes, logicalTimerCount, directRootsByTargetAddress);

        return LegacyAnalysisContextTranslator.Translate(context, options);
    }
}
