using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;
using System.Text.RegularExpressions;

namespace DumpDetective.Reporting.Services;

internal static class ActionPriorityService
{
    public const string ScoringModelVersion = "v1";

    public static IReadOnlyList<RankedActionRecord> BuildTopActions(
        IReadOnlyList<FindingRecord> findings,
        int maxActions = 20)
    {
        if (findings.Count == 0 || maxActions <= 0)
            return [];

        var ranked = new List<RankedActionRecord>(Math.Min(findings.Count, maxActions));

        IReadOnlyList<IReadOnlyList<FindingRecord>> clusters = BuildClusters(findings);
        for (int i = 0; i < clusters.Count; i++)
        {
            IReadOnlyList<FindingRecord> cluster = clusters[i];
            if (cluster.Count == 0)
                continue;

            FindingRecord finding = SelectClusterRepresentative(cluster);

            ActionPriorityFactors factors = ComputeFactors(finding);
            ActionConfidenceRecord confidence = ComputeConfidence(finding);
            string action = ResolveActionText(finding);
            string impact = ResolveImpactLabel(finding, factors);
            int relatedCount = cluster.Count - 1;
            int distinctAnalyzers = CountDistinctAnalyzers(cluster);
            string whyNow = ResolveWhyNow(finding, factors, confidence, relatedCount, distinctAnalyzers);

            string title = finding.Title;
            if (relatedCount > 0)
            {
                title = finding.Title + " (+" + relatedCount + " related)";
            }

            ranked.Add(new RankedActionRecord(
                Priority: 0,
                Title: title,
                Action: action,
                Impact: impact,
                WhyNow: whyNow,
                FindingFingerprint: finding.Id,
                Analyzer: finding.Analyzer,
                Validation: ResolveValidationStep(finding, confidence),
                Confidence: confidence,
                Factors: factors));
        }

        ranked.Sort(static (a, b) =>
        {
            int scoreCmp = (b.Factors?.TotalScore ?? 0).CompareTo(a.Factors?.TotalScore ?? 0);
            if (scoreCmp != 0) return scoreCmp;

            int sevCmp = (b.Factors?.SeverityWeight ?? 0).CompareTo(a.Factors?.SeverityWeight ?? 0);
            if (sevCmp != 0) return sevCmp;

            int fpCmp = StringComparer.Ordinal.Compare(NormalizeSortKey(a.FindingFingerprint), NormalizeSortKey(b.FindingFingerprint));
            if (fpCmp != 0) return fpCmp;

            int analyzerCmp = StringComparer.Ordinal.Compare(NormalizeSortKey(a.Analyzer), NormalizeSortKey(b.Analyzer));
            if (analyzerCmp != 0) return analyzerCmp;

            return StringComparer.Ordinal.Compare(NormalizeSortKey(a.Title), NormalizeSortKey(b.Title));
        });

        int count = Math.Min(maxActions, ranked.Count);
        var top = new List<RankedActionRecord>(count);
        for (int i = 0; i < count; i++)
        {
            RankedActionRecord action = ranked[i] with { Priority = i + 1 };
            top.Add(action);
        }

        return top;
    }

    private static IReadOnlyList<IReadOnlyList<FindingRecord>> BuildClusters(IReadOnlyList<FindingRecord> findings)
    {
        Dictionary<string, List<FindingRecord>> groups = new(StringComparer.Ordinal);

        for (int i = 0; i < findings.Count; i++)
        {
            FindingRecord finding = findings[i];
            if (!IsActionable(finding))
                continue;

            string key = BuildClusterKey(finding);
            if (!groups.TryGetValue(key, out List<FindingRecord>? bucket))
            {
                bucket = [];
                groups[key] = bucket;
            }

            bucket.Add(finding);
        }

        List<IReadOnlyList<FindingRecord>> clusters = [];
        foreach (List<FindingRecord> bucket in groups.Values)
        {
            bucket.Sort(static (a, b) =>
            {
                int sevCmp = SeverityWeight(b.Severity).CompareTo(SeverityWeight(a.Severity));
                if (sevCmp != 0) return sevCmp;

                int confCmp = (b.Confidence ?? 0).CompareTo(a.Confidence ?? 0);
                if (confCmp != 0) return confCmp;

                int titleCmp = StringComparer.Ordinal.Compare(NormalizeSortKey(a.Title), NormalizeSortKey(b.Title));
                if (titleCmp != 0) return titleCmp;

                return StringComparer.Ordinal.Compare(NormalizeSortKey(a.Id), NormalizeSortKey(b.Id));
            });

            clusters.Add(bucket);
        }

        return clusters;
    }

    private static FindingRecord SelectClusterRepresentative(IReadOnlyList<FindingRecord> cluster)
        => cluster[0];

    private static int CountDistinctAnalyzers(IReadOnlyList<FindingRecord> cluster)
    {
        HashSet<string> analyzers = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < cluster.Count; i++)
        {
            analyzers.Add(cluster[i].Analyzer);
        }

        return analyzers.Count;
    }

    private static string BuildClusterKey(FindingRecord finding)
    {
        string action = ResolveActionText(finding);
        return NormalizeClusterToken(finding.Category)
            + "|" + NormalizeClusterToken(finding.Title)
            + "|" + NormalizeClusterToken(action);
    }

    private static string NormalizeClusterToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string normalized = value.Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, "0x[0-9a-f]+", "#");
        normalized = Regex.Replace(normalized, "\\d+", "#");
        normalized = Regex.Replace(normalized, "\\s+", " ");
        if (normalized.Length > 140)
            normalized = normalized[..140];
        return normalized;
    }

    private static bool IsActionable(FindingRecord finding)
    {
        if (!string.IsNullOrWhiteSpace(finding.Recommendation)) return true;
        if (finding.Caveats is { Count: > 0 }) return true;
        if (finding.Details is { Count: > 0 }) return true;
        return false;
    }

    private static ActionPriorityFactors ComputeFactors(FindingRecord finding)
    {
        int severity = SeverityWeight(finding.Severity);
        int blastRadius = BlastRadiusWeight(finding);
        int impact = ImpactLikelihoodWeight(finding);
        int timeToMitigate = TimeToMitigateWeight(finding);
        ActionConfidenceRecord confidenceRecord = ComputeConfidence(finding);
        int confidence = ConfidenceWeight(confidenceRecord);
        int dependencyRisk = DependencyRiskWeight(finding);

        return new ActionPriorityFactors(
            SeverityWeight: severity,
            BlastRadiusWeight: blastRadius,
            ImpactLikelihoodWeight: impact,
            TimeToMitigateWeight: timeToMitigate,
            ConfidenceWeight: confidence,
            DependencyRiskWeight: dependencyRisk,
            TotalScore: severity + blastRadius + impact + timeToMitigate + confidence + dependencyRisk);
    }

    private static int SeverityWeight(string severity) => severity switch
    {
        nameof(FindingSeverity.Critical) => 50,
        nameof(FindingSeverity.Warning) => 30,
        nameof(FindingSeverity.Info) => 10,
        _ => 6
    };

    private static int BlastRadiusWeight(FindingRecord finding)
    {
        int score = 8;
        string category = string.IsNullOrWhiteSpace(finding.Category) ? string.Empty : finding.Category;

        if (category.Contains("CrossDomain", StringComparison.OrdinalIgnoreCase)) score += 16;
        if (category.Contains("Thread", StringComparison.OrdinalIgnoreCase)) score += 6;
        if (category.Contains("Leak", StringComparison.OrdinalIgnoreCase)) score += 8;
        if (category.Contains("GC", StringComparison.OrdinalIgnoreCase)) score += 8;
        if (category.Contains("Crash", StringComparison.OrdinalIgnoreCase)) score += 10;

        if (finding.Tags is { Count: > 3 }) score += 4;
        return score;
    }

    private static int ImpactLikelihoodWeight(FindingRecord finding)
    {
        int score = 10;
        string text = string.Concat(finding.Title, " ", finding.GetSummaryText());

        if (ContainsAny(text, "deadlock", "hang", "outofmemory", "oom", "crash", "fault")) score += 20;
        else if (ContainsAny(text, "blocked", "latency", "retention", "fragmentation", "thread pool")) score += 12;

        return score;
    }

    private static int TimeToMitigateWeight(FindingRecord finding)
    {
        if (finding.Severity == nameof(FindingSeverity.Info)) return 14;
        if (finding.Severity == nameof(FindingSeverity.Warning)) return 10;
        if (finding.Severity == nameof(FindingSeverity.Critical)) return 6;
        return 9;
    }

    private static int ConfidenceWeight(ActionConfidenceRecord confidence)
    {
        double clamped = Math.Max(0.0, Math.Min(1.0, confidence.Composite));
        return (int)Math.Round(clamped * 12.0);
    }

    private static ActionConfidenceRecord ComputeConfidence(FindingRecord finding)
    {
        double baseConfidence = finding.Confidence ?? 0.70;

        int evidenceSignals = 0;
        if (!string.IsNullOrWhiteSpace(finding.GetSummaryText())) evidenceSignals++;
        if (!string.IsNullOrWhiteSpace(finding.Recommendation)) evidenceSignals++;
        if (finding.Details is { Count: > 0 }) evidenceSignals++;
        if (finding.Refs is { Count: > 0 }) evidenceSignals++;
        double evidenceCompleteness = evidenceSignals / 4.0;

        double consistency = 0.55;
        if (string.Equals(finding.Analyzer, "InsightEngine", StringComparison.OrdinalIgnoreCase)) consistency += 0.25;
        if (string.Equals(finding.Category, "CrossDomain", StringComparison.OrdinalIgnoreCase)) consistency += 0.15;
        if (finding.Tags is { Count: > 2 }) consistency += 0.05;
        consistency = Math.Min(1.0, consistency);

        double heuristicPenalty = 0.0;
        var caveats = new List<string>();
        if (finding.Caveats is { Count: > 0 })
        {
            for (int i = 0; i < finding.Caveats.Count; i++)
            {
                string c = finding.Caveats[i];
                if (ContainsAny(c, "heuristic", "approximate", "estimated", "partial"))
                    heuristicPenalty += 0.08;
                caveats.Add(c);
            }
        }
        heuristicPenalty = Math.Min(0.35, heuristicPenalty);

        double coverageFreshness = 0.65;
        if (finding.Refs is { Count: > 0 })
        {
            bool hasSnapshot = false;
            for (int i = 0; i < finding.Refs.Count; i++)
            {
                if (finding.Refs[i].SnapshotIndex.HasValue)
                {
                    hasSnapshot = true;
                    break;
                }
            }
            coverageFreshness = hasSnapshot ? 0.85 : 0.75;
        }

        double composite = (baseConfidence * 0.45)
                         + (evidenceCompleteness * 0.20)
                         + (consistency * 0.20)
                         + (coverageFreshness * 0.15)
                         - heuristicPenalty;
        composite = Math.Min(composite, baseConfidence + 0.12);
        composite = Math.Max(0.0, Math.Min(1.0, composite));

        return new ActionConfidenceRecord(
            EvidenceCompleteness: Math.Round(evidenceCompleteness, 2),
            CrossAnalyzerConsistency: Math.Round(consistency, 2),
            HeuristicPenalty: Math.Round(heuristicPenalty, 2),
            CoverageFreshness: Math.Round(coverageFreshness, 2),
            Composite: Math.Round(composite, 2),
            Caveats: caveats.Count == 0 ? null : caveats);
    }

    private static int DependencyRiskWeight(FindingRecord finding)
    {
        int score = 6;

        if (finding.Tags is { Count: > 0 })
        {
            bool hasRuntime = false;
            bool hasMemory = false;
            bool hasThreading = false;
            for (int i = 0; i < finding.Tags.Count; i++)
            {
                string tag = finding.Tags[i];
                if (tag.Contains("runtime", StringComparison.OrdinalIgnoreCase)) hasRuntime = true;
                if (tag.Contains("memory", StringComparison.OrdinalIgnoreCase) || tag.Contains("gc", StringComparison.OrdinalIgnoreCase)) hasMemory = true;
                if (tag.Contains("thread", StringComparison.OrdinalIgnoreCase) || tag.Contains("async", StringComparison.OrdinalIgnoreCase)) hasThreading = true;
            }

            if ((hasRuntime && hasMemory) || (hasRuntime && hasThreading) || (hasMemory && hasThreading))
                score += 10;
        }

        return score;
    }

    private static string ResolveActionText(FindingRecord finding)
    {
        if (!string.IsNullOrWhiteSpace(finding.Recommendation)) return finding.Recommendation;
        if (finding.Caveats is { Count: > 0 }) return finding.Caveats[0];
        return "Investigate and mitigate the finding path.";
    }

    private static string ResolveImpactLabel(FindingRecord finding, ActionPriorityFactors factors)
    {
        if (factors.SeverityWeight >= 50) return "Immediate operational risk";
        if (factors.TotalScore >= 80) return "High near-term risk";
        if (factors.TotalScore >= 60) return "Moderate risk";
        return "Contained risk";
    }

    private static string ResolveWhyNow(
        FindingRecord finding,
        ActionPriorityFactors factors,
        ActionConfidenceRecord confidence,
        int relatedCount,
        int distinctAnalyzers)
    {
        string severity = string.IsNullOrWhiteSpace(finding.Severity) ? "Unknown" : finding.Severity;
        string text = severity + " signal with composite score " + factors.TotalScore +
               " (impact " + factors.ImpactLikelihoodWeight +
               ", blast radius " + factors.BlastRadiusWeight +
               ", dependency risk " + factors.DependencyRiskWeight +
               ", confidence " + confidence.Composite.ToString("0.00") + ").";

        if (relatedCount > 0)
        {
            text += " Consolidates " + relatedCount + " related finding" + (relatedCount == 1 ? "" : "s") +
                " across " + distinctAnalyzers + " analyzer" + (distinctAnalyzers == 1 ? "" : "s") + ".";
        }

        if (finding.Severity == nameof(FindingSeverity.Critical) && confidence.Composite < 0.45)
            text += " Confidence is low; verify root-cause evidence before broad remediation.";

        return text;
    }

    private static string? ResolveValidationStep(FindingRecord finding, ActionConfidenceRecord confidence)
    {
        string expectedDirection = ResolveExpectedDirection(finding);

        if (finding.Severity == nameof(FindingSeverity.Critical) && confidence.Composite < 0.45)
            return "Re-check evidence path and confirm with a second analyzer signal before rollout. Expected trend: " + expectedDirection;

        return "Re-run analysis and confirm improvement. Expected trend: " + expectedDirection;
    }

    private static string ResolveExpectedDirection(FindingRecord finding)
    {
        string text = string.Concat(finding.Category, " ", finding.Title, " ", finding.GetSummaryText(), " ", finding.Analyzer);
        if (ContainsAny(text, "leak", "retention", "fragmentation", "loh", "gen2", "handle"))
            return "Retained bytes and leak pressure indicators decrease.";
        if (ContainsAny(text, "thread", "hang", "deadlock", "blocked", "lock"))
            return "Blocked/hung thread indicators decrease and execution responsiveness improves.";
        if (ContainsAny(text, "crash", "exception", "fault"))
            return "Active exception and fatal signal counts decrease.";

        return "Composite risk score decreases with fewer caveats.";
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        for (int i = 0; i < needles.Length; i++)
        {
            if (text.Contains(needles[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string NormalizeSortKey(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
