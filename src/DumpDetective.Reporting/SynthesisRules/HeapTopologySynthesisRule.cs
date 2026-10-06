using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>HeapTopologyFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>TotalCommittedBytes</c>/<c>TotalSegments</c>/per-kind bytes/segment-counts/
/// <c>LohPercent</c>/<c>PohPercent</c> need no whole-run measures: they're exact sums (or
/// sum-then-divide, using the same formula <c>HeapTopologyAnalyzer.Analyze</c> itself uses) over
/// <c>heap.segment-kind-composition</c>'s 5 rows — the same "don't duplicate what summing already
/// gives you" choice <c>DbConnectionSynthesisRule</c> made for <c>OpenConnections</c>.
/// <c>PerLogicalHeapSummaries</c>' two extremes are pre-computed by the analyzer itself onto
/// <c>heap.topology-population</c> rather than observed per logical heap, since the skew finding
/// never reads any individual heap's identity.
/// </remarks>
internal sealed class HeapTopologySynthesisRule : ISynthesisRule
{
    private const string AnalyzerName = "Heap Topology";

    public string RuleId => "heap-topology.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["heap.segment-kind-composition", "heap.topology-population"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        var kindObservations = new List<Observation>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "heap.topology-population":
                    population = observation;
                    break;
                case "heap.segment-kind-composition":
                    kindObservations.Add(observation);
                    break;
            }
        }

        if (population is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        ulong totalCommittedBytes = 0, totalSegmentsUlong = 0;
        ulong sohBytes = 0, lohBytes = 0, pohBytes = 0, frozenBytes = 0;
        int sohSegmentCount = 0, lohSegmentCount = 0, pohSegmentCount = 0;
        ulong sohFragmentedBytes = 0, lohFragmentedBytes = 0, pohFragmentedBytes = 0, frozenFragmentedBytes = 0;
        Observation? unknownKind = null;
        var kindIds = new List<ObservationId>();

        foreach (Observation kind in kindObservations)
        {
            kindIds.Add(kind.Id);
            var ordinal = (HeapSegmentKindOrdinal)(int)kind.Measures["kind.ordinal"].Value;
            ulong committed = (ulong)kind.Measures["committed.bytes"].Value;
            int segmentCount = (int)kind.Measures["segment.count"].Value;

            totalCommittedBytes += committed;
            totalSegmentsUlong += (ulong)segmentCount;

            ulong fragmented = kind.Measures.TryGetValue("fragmented.bytes", out Measure fm) ? (ulong)fm.Value : 0;

            switch (ordinal)
            {
                case HeapSegmentKindOrdinal.SmallObjectHeap:
                    sohBytes = committed; sohSegmentCount = segmentCount; sohFragmentedBytes = fragmented;
                    break;
                case HeapSegmentKindOrdinal.LargeObjectHeap:
                    lohBytes = committed; lohSegmentCount = segmentCount; lohFragmentedBytes = fragmented;
                    break;
                case HeapSegmentKindOrdinal.PinnedObjectHeap:
                    pohBytes = committed; pohSegmentCount = segmentCount; pohFragmentedBytes = fragmented;
                    break;
                case HeapSegmentKindOrdinal.Frozen:
                    frozenBytes = committed; frozenFragmentedBytes = fragmented;
                    break;
                case HeapSegmentKindOrdinal.Unknown:
                    unknownKind = kind;
                    break;
            }
        }

        long totalSegments = (long)totalSegmentsUlong;
        double lohPercent = totalCommittedBytes == 0 ? 0.0 : lohBytes * 100.0 / totalCommittedBytes;
        double pohPercent = totalCommittedBytes == 0 ? 0.0 : pohBytes * 100.0 / totalCommittedBytes;

        long logicalHeapCount = (long)population.Measures["logical-heap.count"].Value;
        ulong maxLogicalHeapBytes = (ulong)population.Measures["logical-heap.max-bytes"].Value;
        ulong minLogicalHeapBytes = (ulong)population.Measures["logical-heap.min-bytes"].Value;
        bool isServerGc = population.Measures["is-server-gc"].Value >= 1.0;

        var derivedFrom = new List<ObservationId>(kindIds) { population.Id };

        var findings = new List<Claim>();

        // ── Heap segment distribution snapshot (always) ─────────────────────────
        Severity lohSeverity = lohPercent >= 40 ? Severity.Critical
            : lohPercent >= 25 ? Severity.Warning
            : Severity.Info;

        findings.Add(new Claim
        {
            Fingerprint = Fingerprint("segment-distribution"),
            Assertion = new Assertion
            {
                Predicate = lohSeverity == Severity.Info ? AssertionPredicate.Composes : AssertionPredicate.Exceeds,
                Measure = ClaimParts.Percent(lohPercent),
                Reference = lohSeverity switch
                {
                    Severity.Critical => ClaimParts.Budget(ClaimParts.Percent(40), "critical LOH-share threshold"),
                    Severity.Warning => ClaimParts.Budget(ClaimParts.Percent(25), "warning LOH-share threshold"),
                    _ => null,
                },
                Qualifiers = new AssertionQualifiers { Location = "the Large Object Heap's share of committed memory" },
            },
            Support = [new EvidenceStatement(derivedFrom, $"{totalCommittedBytes / (1024 * 1024):N0} MB committed across {totalSegments} segments; the LOH is {lohPercent:F1}% of it.")],
            Counter = Counter.None(lohSeverity == Severity.Info
                ? "This describes segment distribution and asserts no problem, so there is nothing to weaken."
                : "A large LOH share is normal for a service that holds large buffers; the share does not show the LOH is growing."),
            FalsifiedBy =
            [
                new FalsificationTest
                {
                    Kind = FalsificationKind.Windbg,
                    Command = "!eeheap -gc",
                    Test = $"If the LOH and total committed sizes in !eeheap -gc do not give a {lohPercent:F1}% LOH share, the distribution is wrong.",
                },
            ],
            Analyzer = AnalyzerName,
            Category = "Memory",
            Severity = lohSeverity,
            Title = "Heap segment distribution snapshot",
            Narrative = $"Total committed: {totalCommittedBytes / (1024 * 1024):N0} MB across {totalSegments} segments. " +
                        $"SOH: {sohBytes / (1024 * 1024):N0} MB ({sohSegmentCount} segs), " +
                        $"LOH: {lohBytes / (1024 * 1024):N0} MB ({lohSegmentCount} segs, {lohPercent:F1}%), " +
                        $"POH: {pohBytes / (1024 * 1024):N0} MB ({pohSegmentCount} segs, {pohPercent:F1}%).",
            Recommendation = lohSeverity >= Severity.Warning
                ? "LOH is large relative to total heap. Investigate large object allocations and LOH fragmentation."
                : "Segment distribution appears within expected range.",
            Tags = ["segments", "loh", "poh", "soh", "memory"],
            MetricValue = lohPercent,
            MetricUnit = "%",
            DerivedFrom = derivedFrom,
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        });

        // ── Elevated POH usage ────────────────────────────────────────────────────
        if (pohPercent >= 10)
        {
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("poh-usage"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Percent(pohPercent),
                    Reference = ClaimParts.Budget(ClaimParts.Percent(10), "POH share threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "the Pinned Object Heap's share of committed memory" },
                },
                Support = [new EvidenceStatement(derivedFrom, $"The POH holds {pohBytes / (1024 * 1024):N0} MB across {pohSegmentCount} segments, {pohPercent:F1}% of committed memory.")],
                Counter = Counter.None("The POH holds buffers pinned on purpose for I/O; a large share does not by itself show pinning is excessive."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!eeheap -gc",
                        Test = $"If the POH size in !eeheap -gc is far from {pohBytes / (1024 * 1024):N0} MB, the POH share is wrong.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Warning,
                Title = "Elevated Pinned Object Heap (POH) usage",
                Narrative = $"POH occupies {pohPercent:F1}% ({pohBytes / (1024 * 1024):N0} MB) of committed heap across {pohSegmentCount} segments.",
                Recommendation = "High POH usage can increase GC pause times. Review pinned buffer pools and interop code.",
                Tags = ["segments", "poh", "pinned", "gc"],
                MetricValue = pohPercent,
                MetricUnit = "%",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Logical heap skew ─────────────────────────────────────────────────────
        if (logicalHeapCount > 1 && minLogicalHeapBytes > 0 && maxLogicalHeapBytes > minLogicalHeapBytes * 2)
        {
            double skewRatio = maxLogicalHeapBytes / (double)minLogicalHeapBytes;
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("logical-heap-skew"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Ratio(skewRatio),
                    Reference = ClaimParts.Budget(ClaimParts.Ratio(2), "skew threshold"),
                    Qualifiers = new AssertionQualifiers { Location = $"{logicalHeapCount} logical heaps", Mechanism = isServerGc ? "Server GC per-CPU heaps" : null },
                },
                Support = [new EvidenceStatement([population.Id], $"The largest logical heap is {FormatBytes(maxLogicalHeapBytes)} and the smallest {FormatBytes(minLogicalHeapBytes)}.")],
                Counter = Counter.None(isServerGc
                    ? "Server GC heaps differ in size whenever allocation is uneven across threads; the ratio does not show a defect."
                    : "Uneven logical heaps outside Server GC are unexpected, and nothing observed explains them."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!eeheap -gc",
                        Test = $"If the per-heap sizes in !eeheap -gc are within a factor of {skewRatio:F1} of each other, the skew is overstated.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump; if a different heap is now the largest, the skew is transient.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Warning,
                Title = $"Logical heaps are skewed ({skewRatio:F1}x)",
                Narrative = $"Largest logical heap ({FormatBytes(maxLogicalHeapBytes)}) is {skewRatio:F1}x the smallest " +
                            $"({FormatBytes(minLogicalHeapBytes)}) across {logicalHeapCount} logical heaps" +
                            (isServerGc ? " (Server GC)." : "."),
                Recommendation = isServerGc
                    ? "Uneven per-CPU heap sizes on Server GC usually indicate skewed allocation patterns " +
                      "across threads/cores. Review thread-affinity of large allocators."
                    : "Uneven logical heap sizes are unexpected outside Server GC; verify GC mode and segment classification.",
                Tags = ["segments", "skew", "server-gc", "memory"],
                MetricValue = skewRatio,
                MetricUnit = "x",
                DerivedFrom = [population.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Unrecognized segment kind ─────────────────────────────────────────────
        if (unknownKind is not null)
        {
            int unknownSegmentCount = (int)unknownKind.Measures["segment.count"].Value;
            ulong unknownBytes = (ulong)unknownKind.Measures["committed.bytes"].Value;

            if (unknownSegmentCount > 0)
            {
                findings.Add(new Claim
                {
                    Fingerprint = Fingerprint("unrecognized-kind"),
                    Assertion = new Assertion
                    {
                        Predicate = AssertionPredicate.Missing,
                        Measure = ClaimParts.Count(unknownSegmentCount),
                        Qualifiers = new AssertionQualifiers { Location = "GC segments of an unrecognized kind", Scope = FormatBytes(unknownBytes) },
                    },
                    Support = [new EvidenceStatement([unknownKind.Id], $"{unknownSegmentCount} segment(s) totaling {FormatBytes(unknownBytes)} matched no known kind.")],
                    Counter = Counter.None("A newer runtime can introduce segment kinds this analyzer does not know; that is a tool gap, not evidence of a corrupt dump."),
                    FalsifiedBy =
                    [
                        new FalsificationTest
                        {
                            Kind = FalsificationKind.Windbg,
                            Command = "!eeheap -gc",
                            Test = "If !eeheap -gc classifies every segment as SOH, LOH, POH or frozen, the analyzer's classification is at fault.",
                        },
                    ],
                    Analyzer = AnalyzerName,
                    Category = "Memory",
                    Severity = Severity.Warning,
                    Title = $"{unknownSegmentCount} segment(s) with an unrecognized kind",
                    Narrative = $"{unknownSegmentCount} segment(s) totaling {FormatBytes(unknownBytes)} did not match any " +
                                "known GC segment kind (SOH/LOH/POH/Frozen).",
                    Recommendation = "This usually indicates a corrupted dump or a ClrMD version this analyzer hasn't been " +
                                      "updated for. Verify the dump captured cleanly and cross-check with `!eeheap -gc` in WinDbg.",
                    Tags = ["segments", "unknown", "corruption", "memory"],
                    MetricValue = unknownSegmentCount,
                    MetricUnit = "segments",
                    DerivedFrom = [unknownKind.Id],
                    Confidence = ConfidenceBreakdown.MeasurementCertain,
                });
            }
        }

        // ── SOH fragmentation ─────────────────────────────────────────────────────
        if (sohBytes > 0 && sohFragmentedBytes > 0)
        {
            double sohFragPct = sohFragmentedBytes * 100.0 / sohBytes;
            if (sohFragPct >= 25.0)
            {
                Severity sohFragSev = sohFragPct >= 45.0 ? Severity.Warning : Severity.Info;
                findings.Add(new Claim
                {
                    Fingerprint = Fingerprint("soh-fragmentation"),
                    Assertion = new Assertion
                    {
                        Predicate = AssertionPredicate.Fragments,
                        Measure = ClaimParts.Percent(sohFragPct),
                        Reference = sohFragSev == Severity.Warning
                            ? ClaimParts.Budget(ClaimParts.Percent(45), "warning threshold")
                            : ClaimParts.Budget(ClaimParts.Percent(25), "notice threshold"),
                        Qualifiers = new AssertionQualifiers { Location = "the Small Object Heap", Mechanism = "pinned handles blocking compaction" },
                    },
                    Support = [new EvidenceStatement(derivedFrom, $"The SOH has {FormatBytes(sohFragmentedBytes)} of {FormatBytes(sohBytes)} committed free.")],
                    Counter = Counter.None("The SOH is compacted during Gen2 collections; one snapshot does not show the free space persists."),
                    FalsifiedBy =
                    [
                        new FalsificationTest
                        {
                            Kind = FalsificationKind.Windbg,
                            Command = "!eeheap -gc",
                            Test = $"If the free space on SOH segments in !eeheap -gc is far from {FormatBytes(sohFragmentedBytes)}, the fragmentation is wrong.",
                        },
                        new FalsificationTest
                        {
                            Kind = FalsificationKind.Capture,
                            Test = "Capture a second dump after a Gen2 collection; if the free space is gone, it was reclaimed by compaction.",
                        },
                    ],
                    Analyzer = AnalyzerName,
                    Category = "Memory",
                    Severity = sohFragSev,
                    Title = $"SOH fragmentation {sohFragPct:F1}%",
                    Narrative = $"Small Object Heap has {FormatBytes(sohFragmentedBytes)} of {FormatBytes(sohBytes)} committed free.",
                    Recommendation = "SOH is compacted automatically during Gen2 collections, so persistent free space " +
                                      "usually indicates pinned handles blocking compaction. Review GCHandle.Alloc(Pinned) " +
                                      "usage and interop buffers rather than expecting manual compaction to help.",
                    Tags = ["fragmentation", "soh", "memory", "gc"],
                    MetricValue = sohFragPct,
                    MetricUnit = "%",
                    DerivedFrom = derivedFrom,
                    Confidence = ConfidenceBreakdown.MeasurementCertain,
                });
            }
        }

        // ── LOH fragmentation ─────────────────────────────────────────────────────
        if (lohBytes > 0)
        {
            double lohFragPct = lohFragmentedBytes * 100.0 / lohBytes;
            if (lohFragPct >= 30.0)
            {
                Severity lohFragSev = lohFragPct >= 50.0 ? Severity.Critical : Severity.Warning;
                findings.Add(new Claim
                {
                    Fingerprint = Fingerprint("loh-fragmentation"),
                    Assertion = new Assertion
                    {
                        Predicate = AssertionPredicate.Fragments,
                        Measure = ClaimParts.Percent(lohFragPct),
                        Reference = lohFragSev == Severity.Critical
                            ? ClaimParts.Budget(ClaimParts.Percent(50), "critical threshold")
                            : ClaimParts.Budget(ClaimParts.Percent(30), "warning threshold"),
                        Qualifiers = new AssertionQualifiers { Location = "the Large Object Heap" },
                    },
                    Support = [new EvidenceStatement(derivedFrom, $"The LOH has {FormatBytes(lohFragmentedBytes)} of {FormatBytes(lohBytes)} committed free.")],
                    Counter = Counter.None("Free LOH space can be reused by the next large allocation; its size does not show it is unusable."),
                    FalsifiedBy =
                    [
                        new FalsificationTest
                        {
                            Kind = FalsificationKind.Windbg,
                            Command = "!eeheap -gc",
                            Test = $"If the free space on LOH segments in !eeheap -gc is far from {FormatBytes(lohFragmentedBytes)}, the fragmentation is wrong.",
                        },
                        new FalsificationTest
                        {
                            Kind = FalsificationKind.Capture,
                            Test = "Capture a second dump under comparable load; if the free LOH space is reused and the percentage falls, it was reusable, not stranded.",
                        },
                    ],
                    Analyzer = AnalyzerName,
                    Category = "Memory",
                    Severity = lohFragSev,
                    Title = $"LOH fragmentation {lohFragPct:F1}%",
                    Narrative = $"Large Object Heap has {FormatBytes(lohFragmentedBytes)} of {FormatBytes(lohBytes)} committed free. " +
                                "This indicates LOH segments with reclaimed space not yet compacted.",
                    Recommendation = "Enable LOH compaction via GCSettings.LargeObjectHeapCompactionMode = " +
                                      "GCLargeObjectHeapCompactionMode.Default or Aggressive, or reduce large object allocations.",
                    Tags = ["fragmentation", "loh", "memory", "gc"],
                    MetricValue = lohFragPct,
                    MetricUnit = "%",
                    DerivedFrom = derivedFrom,
                    Confidence = ConfidenceBreakdown.MeasurementCertain,
                });
            }
        }

        // ── POH fragmentation ─────────────────────────────────────────────────────
        if (pohBytes > 0 && pohFragmentedBytes > 0)
        {
            double pohFragPct = pohFragmentedBytes * 100.0 / pohBytes;
            if (pohFragPct >= 20.0)
            {
                findings.Add(new Claim
                {
                    Fingerprint = Fingerprint("poh-fragmentation"),
                    Assertion = new Assertion
                    {
                        Predicate = AssertionPredicate.Fragments,
                        Measure = ClaimParts.Percent(pohFragPct),
                        Reference = ClaimParts.Budget(ClaimParts.Percent(20), "warning threshold"),
                        Qualifiers = new AssertionQualifiers { Location = "the Pinned Object Heap" },
                    },
                    Support = [new EvidenceStatement(derivedFrom, $"The POH has {FormatBytes(pohFragmentedBytes)} of {FormatBytes(pohBytes)} committed free.")],
                    Counter = Counter.Of(new EvidenceStatement(derivedFrom, "Free space in the POH is typical of pinned allocations, so the percentage alone does not show a problem.")),
                    FalsifiedBy =
                    [
                        new FalsificationTest
                        {
                            Kind = FalsificationKind.Windbg,
                            Command = "!eeheap -gc",
                            Test = $"If the free space on POH segments in !eeheap -gc is far from {FormatBytes(pohFragmentedBytes)}, the fragmentation is wrong.",
                        },
                    ],
                    Analyzer = AnalyzerName,
                    Category = "Memory",
                    Severity = Severity.Info,
                    Title = $"POH fragmentation {pohFragPct:F1}%",
                    Narrative = $"Pinned Object Heap has {FormatBytes(pohFragmentedBytes)} of {FormatBytes(pohBytes)} committed free. " +
                                "This is typical for pinned allocations.",
                    Recommendation = "Review pinned buffer pools and consider pooling strategies to reduce allocations.",
                    Tags = ["fragmentation", "poh", "pinning", "memory"],
                    MetricValue = pohFragPct,
                    MetricUnit = "%",
                    DerivedFrom = derivedFrom,
                    Confidence = ConfidenceBreakdown.MeasurementCertain,
                });
            }
        }

        // ── Frozen heap fragmentation ─────────────────────────────────────────────
        if (frozenBytes > 0 && frozenFragmentedBytes > 0)
        {
            double frozenFragPct = frozenFragmentedBytes * 100.0 / frozenBytes;
            if (frozenFragPct >= 30.0)
            {
                findings.Add(new Claim
                {
                    Fingerprint = Fingerprint("frozen-fragmentation"),
                    Assertion = new Assertion
                    {
                        Predicate = AssertionPredicate.Fragments,
                        Measure = ClaimParts.Percent(frozenFragPct),
                        Reference = ClaimParts.Budget(ClaimParts.Percent(30), "warning threshold"),
                        Qualifiers = new AssertionQualifiers { Location = "the frozen object heap" },
                    },
                    Support = [new EvidenceStatement(derivedFrom, $"The frozen heap has {FormatBytes(frozenFragmentedBytes)} of {FormatBytes(frozenBytes)} committed free.")],
                    Counter = Counter.Of(new EvidenceStatement(derivedFrom, "Frozen segments hold immutable data that is never collected; this is address-space overhead, not reclaimable fragmentation.")),
                    FalsifiedBy =
                    [
                        new FalsificationTest
                        {
                            Kind = FalsificationKind.Windbg,
                            Command = "!eeheap -gc",
                            Test = $"If the free space on frozen segments in !eeheap -gc is far from {FormatBytes(frozenFragmentedBytes)}, the figure is wrong.",
                        },
                    ],
                    Analyzer = AnalyzerName,
                    Category = "Memory",
                    Severity = Severity.Info,
                    Title = $"Frozen heap fragmentation {frozenFragPct:F1}%",
                    Narrative = $"Frozen Object Heap has {FormatBytes(frozenFragmentedBytes)} of {FormatBytes(frozenBytes)} committed free.",
                    Recommendation = "Frozen segments hold immutable data that is never collected; this is address-space " +
                                      "overhead rather than reclaimable fragmentation and typically requires no action.",
                    Tags = ["fragmentation", "frozen", "memory"],
                    MetricValue = frozenFragPct,
                    MetricUnit = "%",
                    DerivedFrom = derivedFrom,
                    Confidence = ConfidenceBreakdown.MeasurementCertain,
                });
            }
        }

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    // Mirrors DumpDetective.Analysis.Models.HeapSegmentKind's ordinal values, encoded as
    // kind.ordinal by the analyzer — that enum lives in the Analysis project, inaccessible here.
    private enum HeapSegmentKindOrdinal
    {
        SmallObjectHeap = 0,
        LargeObjectHeap = 1,
        PinnedObjectHeap = 2,
        Frozen = 3,
        Unknown = 4,
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();

    private static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        if (bytes >= 1024UL * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
        if (bytes >= 1024UL) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
