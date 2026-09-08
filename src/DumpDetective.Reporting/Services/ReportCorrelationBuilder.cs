using System.Linq;

using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Derives cross-domain correlation events (co-moving or conflicting signals) from
/// the deduplicated finding list, then merges overlapping clusters.
/// </summary>
internal static class ReportCorrelationBuilder
{
    public static IReadOnlyList<CorrelationEventRecord> BuildCorrelationEvents(IReadOnlyList<FindingRecord> findings)
    {
        var domainByFingerprint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var grouped = new Dictionary<string, List<FindingRecord>>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < findings.Count; i++)
        {
            FindingRecord finding = findings[i];
            string domain = ReportDomainProjector.InferFindingDomain(finding);
            if (string.IsNullOrWhiteSpace(domain))
                continue;

            domainByFingerprint[finding.Id] = domain;

            HashSet<string> signalKeys = ExtractCorrelationSignalKeys(finding);
            if (signalKeys.Count == 0)
                continue;

            foreach (string signalKey in signalKeys)
            {
                if (!grouped.TryGetValue(signalKey, out List<FindingRecord>? list))
                {
                    list = [];
                    grouped[signalKey] = list;
                }

                list.Add(finding);
            }
        }

        var deduped = new Dictionary<string, CorrelationEventRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, List<FindingRecord>> pair in grouped)
        {
            string tag = pair.Key;
            List<FindingRecord> list = pair.Value;
            if (list.Count < 2)
                continue;

            if (IsKeywordSignal(tag) && list.Count < 3)
                continue;

            var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int minSeverity = int.MaxValue;
            int maxSeverity = int.MinValue;
            double minConfidence = 1.0;
            double maxConfidence = 0.0;

            for (int i = 0; i < list.Count; i++)
            {
                FindingRecord finding = list[i];
                if (domainByFingerprint.TryGetValue(finding.Id, out string? domain) && !string.IsNullOrWhiteSpace(domain))
                    domains.Add(domain);
                fingerprints.Add(finding.Id);

                int sev = ReportDomainProjector.SeverityOrdinal(finding.Severity);
                if (sev < minSeverity) minSeverity = sev;
                if (sev > maxSeverity) maxSeverity = sev;

                double conf = finding.Confidence ?? 0.70;
                if (conf < minConfidence) minConfidence = conf;
                if (conf > maxConfidence) maxConfidence = conf;
            }

            if (domains.Count < 2)
                continue;

            bool severityConflict = (maxSeverity - minSeverity) >= 2;
            bool confidenceConflict = (maxConfidence - minConfidence) >= 0.45;
            bool isConflict = severityConflict || confidenceConflict;

            string confidence = domains.Count >= 3 ? "High" : "Medium";
            if (isConflict) confidence = "Medium";
            if (domains.Count == 2 && fingerprints.Count == 2 && !isConflict) confidence = "Medium";

            string eventType = isConflict ? "conflict" : "co-move";

            var orderedDomains = domains.OrderBy(static d => d, StringComparer.OrdinalIgnoreCase).ToArray();
            var orderedFingerprints = fingerprints.OrderBy(static f => f, StringComparer.OrdinalIgnoreCase).Take(6).ToArray();

            string title = BuildCorrelationTitle(eventType, [tag], orderedDomains);
            string rationale = BuildCorrelationRationale(
                eventType,
                [tag],
                orderedDomains,
                orderedFingerprints.Length,
                isConflict);

            string dedupeKey = eventType + "|" + string.Join("|", orderedDomains) + "|" + string.Join("|", orderedFingerprints);

            if (deduped.TryGetValue(dedupeKey, out CorrelationEventRecord? existing))
            {
                var mergedKeys = new HashSet<string>(existing.SignalKeys, StringComparer.OrdinalIgnoreCase);
                mergedKeys.Add(tag);

                deduped[dedupeKey] = existing with
                {
                    SignalKeys = mergedKeys.OrderBy(static s => s, StringComparer.OrdinalIgnoreCase).ToArray()
                };
                continue;
            }

            double confScore = confidence.Equals("High", StringComparison.OrdinalIgnoreCase) ? 0.9 : 0.7;
            deduped[dedupeKey] = new CorrelationEventRecord(
                EventId: Guid.NewGuid().ToString("D"),
                EventType: eventType,
                Title: title,
                Rationale: rationale,
                Confidence: confScore,
                Domains: orderedDomains,
                SnapshotIndices: Array.Empty<int>(),
                SignalKeys: new[] { tag },
                SourceFingerprints: orderedFingerprints,
                PrimarySnapshotIndex: null);
        }

        List<CorrelationEventRecord> events = MergeCorrelationClusters(deduped.Values.ToList());

        events.Sort((a, b) =>
        {
            int typeA = a.EventType.Equals("conflict", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            int typeB = b.EventType.Equals("conflict", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            int typeCmp = typeB.CompareTo(typeA);
            if (typeCmp != 0) return typeCmp;

            int confA = a.Confidence >= 0.85 ? 2 : 1;
            int confB = b.Confidence >= 0.85 ? 2 : 1;
            int confCmp = confB.CompareTo(confA);
            if (confCmp != 0) return confCmp;

            int domainCmp = b.Domains.Count.CompareTo(a.Domains.Count);
            if (domainCmp != 0) return domainCmp;

            return StringComparer.OrdinalIgnoreCase.Compare(ReportDomainProjector.NormalizeSortKey(a.Title), ReportDomainProjector.NormalizeSortKey(b.Title));
        });

        if (events.Count > 8)
            events = events.Take(8).ToList();

        return events;
    }

    private static List<CorrelationEventRecord> MergeCorrelationClusters(List<CorrelationEventRecord> input)
    {
        if (input.Count < 2)
            return input;

        bool merged;
        do
        {
            merged = false;
            for (int i = 0; i < input.Count; i++)
            {
                for (int j = i + 1; j < input.Count; j++)
                {
                    CorrelationEventRecord a = input[i];
                    CorrelationEventRecord b = input[j];
                    if (!ShouldMergeCorrelationEvents(a, b))
                        continue;

                    input[i] = MergeCorrelationEvents(a, b);
                    input.RemoveAt(j);
                    merged = true;
                    break;
                }

                if (merged)
                    break;
            }
        } while (merged);

        return input;
    }

    private static bool ShouldMergeCorrelationEvents(CorrelationEventRecord a, CorrelationEventRecord b)
    {
        if (!a.EventType.Equals(b.EventType, StringComparison.OrdinalIgnoreCase))
            return false;

        int sharedFingerprintCount = 0;
        for (int i = 0; i < a.SourceFingerprints.Count; i++)
        {
            if (b.SourceFingerprints.Contains(a.SourceFingerprints[i], StringComparer.OrdinalIgnoreCase))
                sharedFingerprintCount++;
        }
        if (sharedFingerprintCount > 0)
            return true;

        int sharedDomainCount = 0;
        for (int i = 0; i < a.Domains.Count; i++)
        {
            if (b.Domains.Contains(a.Domains[i], StringComparer.OrdinalIgnoreCase))
                sharedDomainCount++;
        }

        int sharedSignalCount = 0;
        for (int i = 0; i < a.SignalKeys.Count; i++)
        {
            if (b.SignalKeys.Contains(a.SignalKeys[i], StringComparer.OrdinalIgnoreCase))
                sharedSignalCount++;
        }

        if (sharedSignalCount > 0 && sharedDomainCount > 0)
            return true;

        bool sharedNonKeywordSignal = HasSharedSignal(a.SignalKeys, b.SignalKeys, includeKeywordSignals: false);
        if (sharedNonKeywordSignal && sharedDomainCount > 0)
            return true;

        bool sharedKeywordSignal = HasSharedSignal(a.SignalKeys, b.SignalKeys, includeKeywordSignals: true)
            && !sharedNonKeywordSignal;
        if (sharedKeywordSignal && sharedDomainCount >= 3)
            return true;

        return false;
    }

    private static bool HasSharedSignal(IReadOnlyList<string> a, IReadOnlyList<string> b, bool includeKeywordSignals)
    {
        for (int i = 0; i < a.Count; i++)
        {
            string signalA = a[i];
            if (!includeKeywordSignals && IsKeywordSignal(signalA))
                continue;

            for (int j = 0; j < b.Count; j++)
            {
                string signalB = b[j];
                if (!includeKeywordSignals && IsKeywordSignal(signalB))
                    continue;

                if (signalA.Equals(signalB, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static CorrelationEventRecord MergeCorrelationEvents(CorrelationEventRecord a, CorrelationEventRecord b)
    {
        var mergedDomains = new HashSet<string>(a.Domains, StringComparer.OrdinalIgnoreCase);
        mergedDomains.UnionWith(b.Domains);

        var mergedSignals = new HashSet<string>(a.SignalKeys, StringComparer.OrdinalIgnoreCase);
        mergedSignals.UnionWith(b.SignalKeys);

        var mergedFingerprints = new HashSet<string>(a.SourceFingerprints, StringComparer.OrdinalIgnoreCase);
        mergedFingerprints.UnionWith(b.SourceFingerprints);

        string[] orderedDomains = mergedDomains.OrderBy(static d => d, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] orderedSignals = mergedSignals.OrderBy(static s => s, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] orderedFingerprints = mergedFingerprints.OrderBy(static f => f, StringComparer.OrdinalIgnoreCase).Take(8).ToArray();

        bool isConflict = a.EventType.Equals("conflict", StringComparison.OrdinalIgnoreCase);
        string title = BuildCorrelationTitle(a.EventType, orderedSignals, orderedDomains);
        string rationale = BuildCorrelationRationale(
            a.EventType,
            orderedSignals,
            orderedDomains,
            orderedFingerprints.Length,
            isConflict);

        double confNum = orderedDomains.Length >= 3 ? 0.9 : 0.7;
        if (isConflict)
            confNum = 0.7;

        int? primary = a.PrimarySnapshotIndex ?? b.PrimarySnapshotIndex;

        return new CorrelationEventRecord(
            EventId: Guid.NewGuid().ToString("D"),
            EventType: a.EventType,
            Title: title,
            Rationale: rationale,
            Confidence: confNum,
            Domains: orderedDomains,
            SnapshotIndices: Array.Empty<int>(),
            SignalKeys: orderedSignals,
            SourceFingerprints: orderedFingerprints,
            PrimarySnapshotIndex: primary);
    }

    private static string NormalizeCorrelationTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return string.Empty;

        return tag.Trim();
    }

    private static HashSet<string> ExtractCorrelationSignalKeys(FindingRecord finding)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (finding.Tags is { Count: > 0 })
        {
            for (int i = 0; i < finding.Tags.Count; i++)
            {
                string tag = NormalizeCorrelationTag(finding.Tags[i]);
                if (!IsCorrelationTagCandidate(tag))
                    continue;

                keys.Add("tag:" + tag.ToLowerInvariant());
            }
        }

        if (finding.Refs is { Count: > 0 })
        {
            for (int i = 0; i < finding.Refs.Count; i++)
            {
                EvidenceRef evidenceRef = finding.Refs[i];
                if (string.IsNullOrWhiteSpace(evidenceRef.MetricKey))
                    continue;

                string metricKey = NormalizeCorrelationTag(evidenceRef.MetricKey);
                if (metricKey.Length < 3)
                    continue;

                keys.Add("metric:" + metricKey.ToLowerInvariant());
            }
        }

        string text = string.Concat(
            finding.Title, " ",
            finding.GetSummaryText(), " ",
            finding.Recommendation);

        AddBridgeKeyword(keys, text, "deadlock", "kw:deadlock");
        AddBridgeKeyword(keys, text, "thread pool", "kw:thread-pool");
        AddBridgeKeyword(keys, text, "finalizer", "kw:finalizer");
        AddBridgeKeyword(keys, text, "gc handle", "kw:gc-handle");
        AddBridgeKeyword(keys, text, "pinned", "kw:pinned");
        AddBridgeKeyword(keys, text, "retention", "kw:retention");
        AddBridgeKeyword(keys, text, "fragmentation", "kw:fragmentation");
        AddBridgeKeyword(keys, text, "connection pool", "kw:connection-pool");
        AddBridgeKeyword(keys, text, "timeout", "kw:timeout");
        AddBridgeKeyword(keys, text, "latency", "kw:latency");

        return keys;
    }

    private static void AddBridgeKeyword(HashSet<string> keys, string text, string token, string signal)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(token))
            return;

        if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
            keys.Add(signal);
    }

    private static bool IsCorrelationTagCandidate(string tag)
    {
        if (tag.Length < 4)
            return false;

        return !tag.Equals("memory", StringComparison.OrdinalIgnoreCase)
            && !tag.Equals("threads", StringComparison.OrdinalIgnoreCase)
            && !tag.Equals("runtime", StringComparison.OrdinalIgnoreCase)
            && !tag.Equals("gc", StringComparison.OrdinalIgnoreCase)
            && !tag.Equals("leak", StringComparison.OrdinalIgnoreCase)
            && !tag.Equals("heap", StringComparison.OrdinalIgnoreCase)
            && !tag.Equals("allocation", StringComparison.OrdinalIgnoreCase)
            && !tag.Equals("dispose", StringComparison.OrdinalIgnoreCase)
            && !tag.Equals("exceptions", StringComparison.OrdinalIgnoreCase)
            && !tag.Equals("finalizer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKeywordSignal(string signalKey) =>
        signalKey.StartsWith("kw:", StringComparison.OrdinalIgnoreCase);

    private static string ToDisplaySignal(string signalKey)
    {
        if (string.IsNullOrWhiteSpace(signalKey))
            return "unknown";

        int idx = signalKey.IndexOf(':');
        string raw = idx > 0 ? signalKey[(idx + 1)..] : signalKey;
        return HumanizeSignal(raw);
    }

    private static string HumanizeSignal(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        string normalized = value.Trim().ToLowerInvariant();
        normalized = normalized.Replace('-', ' ').Replace('_', ' ');

        return normalized switch
        {
            "thread pool" => "thread pool pressure",
            "gc handle" => "GC handle retention",
            "connection pool" => "connection pool pressure",
            _ => normalized
        };
    }

    private static string BuildCorrelationTitle(string eventType, IReadOnlyList<string> signalKeys, IReadOnlyList<string> domains)
    {
        List<string> subsystems = ConvertDomainsToSubsystems(domains);
        string domainSummary = BuildCompactList(subsystems, 2);
        string signalSummary = BuildCompactList(ConvertSignals(signalKeys), 1);

        string prefix = eventType.Equals("conflict", StringComparison.OrdinalIgnoreCase)
            ? "Conflicting interpretation across "
            : "Shared signal across ";

        if (!string.IsNullOrWhiteSpace(signalSummary))
            return prefix + domainSummary + ": " + signalSummary;

        return prefix + domainSummary;
    }

    private static string BuildCorrelationRationale(
        string eventType,
        IReadOnlyList<string> signalKeys,
        IReadOnlyList<string> domains,
        int findingCount,
        bool isConflict)
    {
        List<string> subsystems = ConvertDomainsToSubsystems(domains);
        string domainSummary = BuildCompactList(subsystems, 3);
        string signalSummary = BuildCompactList(ConvertSignals(signalKeys), 2);

        string rationale = "Why linked: " + domainSummary + " findings share " +
            (string.IsNullOrWhiteSpace(signalSummary) ? "related pressure signals" : signalSummary) +
            " across " + findingCount + " finding" + (findingCount == 1 ? string.Empty : "s") + ".";

        if (isConflict || eventType.Equals("conflict", StringComparison.OrdinalIgnoreCase))
            rationale += " Severity or confidence disagrees between domains; require verification before broad remediation.";

        return rationale;
    }

    private static List<string> ConvertDomainsToSubsystems(IReadOnlyList<string> domains)
    {
        var result = new List<string>(domains.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < domains.Count; i++)
        {
            string mapped = DomainToSubsystemLabel(domains[i]);
            if (string.IsNullOrWhiteSpace(mapped))
                continue;

            if (!seen.Add(mapped))
                continue;

            result.Add(mapped);
        }

        return result;
    }

    private static string DomainToSubsystemLabel(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return string.Empty;

        return domain.Trim() switch
        {
            "Leaks" => "memory retention",
            "Memory" => "managed heap",
            "GC" => "garbage collection",
            "TypeSystem" => "type metadata",
            "Threads" => "thread scheduling",
            "Async" => "async execution",
            "Exceptions" => "exception flow",
            "Runtime" => "runtime services",
            "Infrastructure" => "infrastructure integration",
            _ => domain.Trim().ToLowerInvariant()
        };
    }

    private static List<string> ConvertSignals(IReadOnlyList<string> signalKeys)
    {
        var result = new List<string>(signalKeys.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < signalKeys.Count; i++)
        {
            string label = ToDisplaySignal(signalKeys[i]);
            if (string.IsNullOrWhiteSpace(label))
                continue;

            if (!seen.Add(label))
                continue;

            result.Add(label);
        }

        return result;
    }

    private static string BuildCompactList(IReadOnlyList<string> values, int maxInline)
    {
        if (values.Count == 0)
            return "multiple";

        if (values.Count == 1)
            return values[0];

        int inlineCount = Math.Min(maxInline, values.Count);
        if (inlineCount == 1)
            return values[0];

        if (inlineCount == 2 && values.Count == 2)
            return values[0] + " and " + values[1];

        if (values.Count <= maxInline)
        {
            string joined = string.Empty;
            for (int i = 0; i < values.Count; i++)
            {
                if (i == 0)
                    joined = values[i];
                else if (i == values.Count - 1)
                    joined += " and " + values[i];
                else
                    joined += ", " + values[i];
            }

            return joined;
        }

        return values[0] + ", " + values[1] + ", and " + (values.Count - 2) + " more";
    }
}
