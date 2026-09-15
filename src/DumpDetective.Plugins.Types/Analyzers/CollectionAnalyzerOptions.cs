using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Analyzers.Types;

/// <summary>
/// <c>Sdk.Analysis.AnalysisContext.AnalyzerOptions</c> is a single untyped slot, but
/// <see cref="CollectionAnalyzer"/> needs two genuinely separate legacy option records
/// (<see cref="CollectionAnalysisOptions"/> for its own thresholds, <see cref="ReferenceChainOptions"/>
/// for the root-path search it drives) — every prior consumer of that slot needed exactly one.
/// A small wrapper record, not a second keyed slot on <c>AnalysisContext</c> itself: only one
/// analyzer needs this so far, so a real multi-option-record mechanism would be speculative ahead
/// of a second measured need (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md).
/// </summary>
/// <param name="ReferenceChain">
/// Null when the legacy options bag has none configured — <see cref="CollectionAnalyzer"/> treats a
/// null value the same as "root-path enrichment disabled," matching the pre-retyping analyzer's own
/// <c>_refChainOptions is null</c> guard in <c>PopulateRootDescriptions</c>.
/// </param>
internal sealed record CollectionAnalyzerOptions(CollectionAnalysisOptions Collection, ReferenceChainOptions? ReferenceChain);
