using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

internal sealed class ExecutiveSummaryProjector
{
    public long ComputeTotalManagedBytes(IReadOnlyList<AnalyzerRunResult> runs)
    {
        long totalManagedBytes = 0;
        for (int i = 0; i < runs.Count; i++)
        {
            AnalyzerRunResult run = runs[i];
            if (run.Result is MemoryDomainResult mem)
            {
                totalManagedBytes = (long)mem.TotalBytes;
                break;
            }

            if (run.Result is GCGenerationDomainResult gc)
            {
                try
                {
                    ulong sum = gc.Gen0Bytes + gc.Gen1Bytes + gc.Gen2Bytes + gc.LohBytes;
                    totalManagedBytes = (long)Math.Min((ulong)long.MaxValue, sum);
                    break;
                }
                catch
                {
                }
            }

            if (run.Result is AppDomainDomainResult app)
            {
                try
                {
                    ulong sum = 0;
                    for (int d = 0; d < app.Domains.Count; d++)
                        sum += app.Domains[d].EstimatedManagedBytes;

                    totalManagedBytes = (long)Math.Min((ulong)long.MaxValue, sum);
                    break;
                }
                catch
                {
                }
            }
        }

        return totalManagedBytes;
    }

    public ExecutiveSummaryRecord Build(
        IReadOnlyList<FindingRecord> findings,
        HealthScorecard scorecard,
        IReadOnlyList<AnalyzerRunResult> runs,
        long? totalManagedBytes = null)
    {
        var (leak, gcPressure, thread) = ExplainableScoringEngine.ComputeScores(findings);

        List<FindingRecord> criticalFindings = new(5);
        List<FindingRecord> warningFindings = new(5);
        List<FindingRecord> top3 = new(3);

        for (int i = 0; i < findings.Count && top3.Count < 3; i++)
        {
            int ord = SeverityOrdinal(findings[i].Severity);
            if (ord >= 1)
                top3.Add(findings[i]);
        }

        for (int i = 0; i < findings.Count; i++)
        {
            FindingRecord finding = findings[i];
            int ord = SeverityOrdinal(finding.Severity);
            if (ord == 2 && criticalFindings.Count < 5)
                criticalFindings.Add(finding);
            else if (ord == 1 && warningFindings.Count < 5)
                warningFindings.Add(finding);

            if (criticalFindings.Count == 5 && warningFindings.Count == 5)
                break;
        }

        IReadOnlyList<RankedActionRecord> topActions = ActionPriorityService.BuildTopActions(findings);

        return new ExecutiveSummaryRecord(
            TotalManagedBytes: totalManagedBytes ?? ComputeTotalManagedBytes(runs),
            LeakLikelihoodScore: leak.Score,
            GcPressureScore: gcPressure.Score,
            ThreadContentionScore: thread.Score,
            TopRecommendations: top3)
        {
            HealthScorecard = scorecard,
            CriticalFindings = criticalFindings,
            WarningFindings = warningFindings,
            ScoreBreakdowns = [leak, gcPressure, thread],
            LohBytes = ExtractLohBytes(runs),
            LohPercent = ExtractLohPercent(runs),
            Gen2Percent = ExtractGen2Percent(runs),
            LeakCandidateCount = ExtractLeakCandidateCount(runs),
            HangScore = ExtractHangScore(runs),
            BlockedThreads = ExtractBlockedThreads(runs),
            DeadlockCycles = ExtractDeadlockCycles(runs),
            ActiveExceptions = ExtractActiveExceptions(runs),
            FinalizerQueueCount = ExtractFinalizerQueueCount(runs),
            TotalObjects = ExtractTotalObjects(runs),
            UniqueTypes = ExtractUniqueTypes(runs),
            GcPressureLevel = ExtractGcPressureLevel(runs),
            TopActions = topActions,
            ActionScoringModelVersion = ActionPriorityService.ScoringModelVersion,
        };
    }

    private static int SeverityOrdinal(string severity) => severity switch
    {
        nameof(FindingSeverity.Critical) => 2,
        nameof(FindingSeverity.Warning) => 1,
        _ => 0
    };

    private static long? ExtractLohBytes(IReadOnlyList<AnalyzerRunResult> runs)
    {
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Result is MemoryDomainResult mem) return (long)mem.LohBytes;
            if (runs[i].Result is GCGenerationDomainResult gc) return (long)gc.LohBytes;
        }

        return null;
    }

    private static double? ExtractLohPercent(IReadOnlyList<AnalyzerRunResult> runs)
    {
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Result is MemoryDomainResult mem) return mem.LohPercent;
            if (runs[i].Result is GCGenerationDomainResult gc) return gc.LohPercent;
        }

        return null;
    }

    private static double? ExtractGen2Percent(IReadOnlyList<AnalyzerRunResult> runs)
    {
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Result is GCGenerationDomainResult gc) return gc.Gen2Pct;
        }

        return null;
    }

    private static int? ExtractLeakCandidateCount(IReadOnlyList<AnalyzerRunResult> runs)
    {
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Result is LeakCandidateDomainResult leak) return leak.TotalCandidates;
        }

        return null;
    }

    private static int? ExtractHangScore(IReadOnlyList<AnalyzerRunResult> runs)
    {
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Result is HangDomainResult hang) return hang.HealthScore;
        }

        return null;
    }

    private static int? ExtractBlockedThreads(IReadOnlyList<AnalyzerRunResult> runs)
    {
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Result is ThreadDomainResult thread) return thread.BlockedThreadCount;
        }

        return null;
    }

    private static int? ExtractDeadlockCycles(IReadOnlyList<AnalyzerRunResult> runs)
    {
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Result is LockGraphDomainResult lockGraph) return lockGraph.DeadlockCandidateCount;
        }

        return null;
    }

    private static int? ExtractActiveExceptions(IReadOnlyList<AnalyzerRunResult> runs)
    {
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Result is CrashDomainResult crash) return crash.ActiveExceptions;
        }

        return null;
    }

    private static int? ExtractFinalizerQueueCount(IReadOnlyList<AnalyzerRunResult> runs)
    {
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Result is FinalizableObjectDomainResult fin) return fin.FinalizerQueueCount;
        }

        return null;
    }

    private static int? ExtractTotalObjects(IReadOnlyList<AnalyzerRunResult> runs)
    {
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Result is MemoryDomainResult mem) return mem.TotalObjects;
            if (runs[i].Result is GCGenerationDomainResult gc) return gc.TotalObjects;
        }

        return null;
    }

    private static int? ExtractUniqueTypes(IReadOnlyList<AnalyzerRunResult> runs)
    {
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Result is MemoryDomainResult mem) return mem.UniqueTypes;
        }

        return null;
    }

    private static string? ExtractGcPressureLevel(IReadOnlyList<AnalyzerRunResult> runs)
    {
        for (int i = 0; i < runs.Count; i++)
        {
            if (runs[i].Result is AllocationPatternDomainResult alloc)
                return alloc.GCPressure.ToString();
        }

        return null;
    }
}