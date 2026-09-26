using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Utilities;

internal readonly record struct LeakCandidateAssessment(
    int SuspicionScore,
    FindingSeverity Severity,
    LeakClass Classification,
    string? RootKind);

/// <summary>
/// Sole owner of the Leak Candidate judgment: classification, suspicion score, severity band, and
/// the root kind the classification implies. <see cref="LeakCandidateRecord"/> carries only the raw
/// facts these are computed from, so the analyzer (to rank candidates and pick which get root
/// chains), its section builder, its trend comparer, and <c>LeakCandidateSynthesisRule</c> all
/// compute the same answer from one formula — docs/refactor/analyzer-pipeline-stages-and-leadfinding-dedup.md,
/// Smell A.
/// </summary>
internal static class LeakCandidateClassifier
{
    private const int TimerLeakWarningThreshold = 100;
    private const int TimerLeakCriticalThreshold = 250;

    public static LeakCandidateAssessment Assess(LeakCandidateRecord candidate, int logicalTimerCount) => Assess(
        candidate.TypeName,
        candidate.TotalSize,
        candidate.Gen2Count,
        candidate.Gen2Pct,
        candidate.IsFinalizable,
        candidate.IsStaticRooted,
        candidate.IsPinned,
        candidate.IsDependent,
        candidate.IsContainer,
        candidate.ReferenceFieldRatio,
        candidate.IsDelegate,
        logicalTimerCount);

    public static LeakCandidateAssessment Assess(
        string typeName,
        ulong totalSize,
        long gen2Count,
        double gen2Pct,
        bool isFinalizable,
        bool isStaticRooted,
        bool isPinned,
        bool isDependent,
        bool isContainer,
        double referenceFieldRatio,
        bool isDelegate,
        int logicalTimerCount)
    {
        LeakClass classification = Classify(
            typeName, isStaticRooted, isPinned, isDependent, isFinalizable, isDelegate, isContainer, gen2Pct, logicalTimerCount);
        int score = Score(
            totalSize, gen2Count, gen2Pct, isFinalizable, isStaticRooted, isPinned, isDependent, isContainer,
            referenceFieldRatio, typeName, isDelegate, logicalTimerCount);
        return new LeakCandidateAssessment(score, GetSeverity(score), classification, RootKindOf(classification));
    }

    private static string? RootKindOf(LeakClass classification) => classification switch
    {
        LeakClass.StaticRetention => "StaticRoot",
        LeakClass.GCHandleRetention => "GCHandle",
        LeakClass.DependentHandleLeak => "DependentHandle",
        LeakClass.FinalizerRetention => "FinalizerQueue",
        LeakClass.EventLeak => "EventLeak",
        LeakClass.CacheLeak => "Cache",
        LeakClass.ThreadLocalLeak => "ThreadLocal",
        LeakClass.TimerLeak => "Timer",
        _ => null
    };

    private static LeakClass Classify(
        string typeName,
        bool isStaticRooted,
        bool isPinned,
        bool isDependent,
        bool isFinalizable,
        bool isDelegate,
        bool isContainer,
        double gen2Pct,
        int logicalTimerCount)
    {
        if (typeName.Contains("ThreadLocal", StringComparison.OrdinalIgnoreCase))
            return LeakClass.ThreadLocalLeak;

        if (isDependent)
            return LeakClass.DependentHandleLeak;

        if (isPinned)
            return LeakClass.GCHandleRetention;

        if (isStaticRooted)
            return LeakClass.StaticRetention;

        if (logicalTimerCount >= TimerLeakWarningThreshold && TimerWrapperTypeNames.Names.Contains(typeName))
            return LeakClass.TimerLeak;

        if (isDelegate || typeName.Contains("Event", StringComparison.OrdinalIgnoreCase))
            return LeakClass.EventLeak;

        if (isFinalizable && gen2Pct > 50.0)
            return LeakClass.FinalizerRetention;

        if (isContainer && gen2Pct > 70.0)
            return LeakClass.CacheLeak;

        return LeakClass.Unknown;
    }

    private static int Score(
        ulong totalSize,
        long gen2Count,
        double gen2Pct,
        bool isFinalizable,
        bool isStaticRooted,
        bool isPinned,
        bool isDependent,
        bool isContainer,
        double referenceFieldRatio,
        string typeName,
        bool isDelegate,
        int logicalTimerCount)
    {
        int score = 0;

        if (gen2Pct > 80.0)
            score += 30;
        if (totalSize > 100UL * 1024 * 1024)
            score += 20;
        if (isFinalizable && gen2Count > 1000)
            score += 15;
        if (isStaticRooted)
            score += 10;
        if (isPinned)
            score += 10;
        if (isDependent)
            score += 10;
        if (isContainer)
            score += 5;
        if (referenceFieldRatio > 0.5)
            score += 5;
        if (isDelegate)
            score += 5;
        if (TimerWrapperTypeNames.Names.Contains(typeName))
        {
            if (logicalTimerCount >= TimerLeakCriticalThreshold)
                score += 20;
            else if (logicalTimerCount >= TimerLeakWarningThreshold)
                score += 10;
        }

        return score;
    }

    private static FindingSeverity GetSeverity(int score)
        => score >= 90
            ? FindingSeverity.Critical
            : score >= 70
                ? FindingSeverity.Warning
                : FindingSeverity.Info;
}
