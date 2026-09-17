using DumpDetective.Analysis.Models;

namespace DumpDetective.Analysis.Utilities;

/// <summary>
/// Sole owner of the Event Leak analyzer's per-instance severity judgment. Used both at scan
/// time (the streaming pass that produces <c>EventGroupInfo.MaxSeverity</c>, via
/// <c>EventLeakAnalyzer.CalculateSeverity</c>'s thin wrapper) and by any downstream consumer
/// recomputing <c>EventLeakInstanceSnapshot.SeverityScore</c> from its raw facts plus
/// <see cref="EventLeakDomainResult.ScoringWeights"/> — one formula, not two
/// independently-maintained copies. See
/// docs/refactor/analyzer-pipeline-stages-and-leadfinding-dedup.md, Smell A.
/// </summary>
internal static class EventLeakSeverityClassifier
{
    public static int ComputeSeverity(
        bool isStatic,
        int subscriberCount,
        string rootHint,
        EventLeakSeverityWeights weights,
        int publisherGeneration = -1,
        int duplicateCount = 0,
        bool isDisposedButSubscribed = false,
        bool hasLifetimeMismatch = false,
        bool hasLowIncomingRefs = false)
    {
        int score = subscriberCount;
        score += (int)(Math.Log2(subscriberCount + 1) * weights.SubscriberLogScale);
        if (isStatic) score += weights.StaticPublisherBonus;
        if (!string.IsNullOrEmpty(rootHint)) score += weights.RootHintBonus;
        if (publisherGeneration == 2) score += weights.Gen2PublisherBonus;
        if (duplicateCount > 0) score += weights.DuplicateSubscriptionBonus;
        if (isDisposedButSubscribed) score += weights.DisposedButSubscribedBonus;
        if (hasLifetimeMismatch) score += weights.LifetimeMismatchBonus;
        if (hasLowIncomingRefs) score += weights.LowIncomingRefsBonus;
        return score;
    }

    public static int ComputeSeverity(EventLeakInstanceSnapshot instance, EventLeakSeverityWeights weights) =>
        ComputeSeverity(
            instance.IsStatic,
            instance.SubscriberCount,
            instance.RootHint ?? string.Empty,
            weights,
            instance.PublisherGeneration,
            instance.DuplicateSubscriptionCount,
            instance.IsDisposedButSubscribed,
            instance.HasLifetimeMismatch,
            instance.HasLowIncomingRefs);
}
