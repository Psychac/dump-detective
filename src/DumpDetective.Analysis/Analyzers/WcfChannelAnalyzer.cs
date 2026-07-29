using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Scans the managed heap for WCF channel objects (System.ServiceModel).
/// Detects faulted or leaked channels, which cause resource exhaustion and silent failures.
///
/// CommunicationState enum:
///   Created=0, Opening=1, Opened=2, Closing=3, Closed=4, Faulted=5
///
/// A faulted channel must be Abort()ed, not Close()d. Faulted channels on the heap are a
/// strong signal of missing error-handling in WCF proxy usage.
/// </summary>
public sealed class WcfChannelAnalyzer : IAnalyzer, IParallelHeapIndexScanParticipant, ITypedResourceCandidateSource, ITypedResourceInstanceSampler<WcfChannelSnapshot>
{
    public string Name => "WCF Channel Analysis";
    public string Category => "Infrastructure";

    private const int MaxStateSamples = 500;
    private const int TopFaultedCap = 50;

    // CommunicationState enum values
    private const int StateOpened  = 2;
    private const int StateFaulted = 5;
    private const int StateClosed  = 4;

    private static readonly ClrElementType[] StateElementTypes =
        [ClrElementType.Int32, ClrElementType.UInt32, ClrElementType.Object];

    // Types to match: in System.ServiceModel namespace, ending with "Channel" or
    // well-known base/proxy types.
    private static readonly string[] WcfNamespacePrefixes = ["System.ServiceModel."];
    private static readonly string[] WcfContainsTokens = ["Channel", "ClientBase", "CommunicationObject"];

    public bool IsCandidateType(string typeName) =>
        TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, WcfNamespacePrefixes, ".ServiceChannel", WcfContainsTokens);

    public int MaxStateSamplesPerType => MaxStateSamples;
    public int TopSampleCap => TopFaultedCap;

    private static readonly string[] StateFieldNames = ["_state", "state", "communicationState"];

    WcfChannelSnapshot? ITypedResourceInstanceSampler<WcfChannelSnapshot>.TrySample(ClrHeap heap, in HeapEntry entry, string typeName)
    {
        int stateVal = InstanceStateSampler<WcfChannelSnapshot>.TryReadIntField(heap, entry.Address, StateFieldNames, StateElementTypes);
        if (stateVal < 0)
            return null;

        return new WcfChannelSnapshot(typeName, entry.Address, MapCommunicationState(stateVal), stateVal);
    }

    private ClrHeap? _heap;
    private Dictionary<ulong, (string TypeName, long Count, ulong Bytes)>? _candidateMts;
    private Dictionary<ulong, (string Name, int Total, int Opened, int Faulted, int Closed, int Other, ulong Bytes)>? _typeStats;
    private InstanceStateSampler<WcfChannelSnapshot>? _sampler;

    /// <summary>
    /// Resolves candidate WCF-type MethodTables and pre-seeds per-type counters from
    /// TypeAggregates, exactly mirroring the historical single-shot "Step 1 + pre-seed" logic.
    /// </summary>
    public void BeforeHeapIndexScan(AnalysisContext context)
    {
        ClrHeap heap = context.Heap;
        _heap = heap;

        Dictionary<ulong, (string TypeName, long Count, ulong Bytes)> candidateMts =
            TypedResourceScanDriver.DiscoverCandidates(this, heap, context.Cache);
        _candidateMts = candidateMts;

        var typeStats = new Dictionary<ulong, (string Name, int Total, int Opened, int Faulted, int Closed, int Other, ulong Bytes)>(candidateMts.Count);
        foreach (KeyValuePair<ulong, (string TypeName, long Count, ulong Bytes)> kv in candidateMts)
        {
            int total = (int)Math.Min(kv.Value.Count, int.MaxValue);
            typeStats[kv.Key] = (kv.Value.TypeName, total, 0, 0, 0, 0, kv.Value.Bytes);
        }

        _typeStats = typeStats;
        _sampler = TypedResourceScanDriver.CreateSampler(this);
    }

    /// <summary>
    /// Explicit interface forwarder - keeps HeapEntry's internal-ness from leaking into
    /// this analyzer's public API.
    /// </summary>
    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry) => OnHeapEntry(in entry);

    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() =>
        new WcfChannelAnalyzer();

    // Merges per-type state-change counts (opened/faulted/closed/other) and top faulted
    // samples from disjoint-range workers. Total and Bytes come from TypeAggregates
    // (pre-seeded identically on every worker by BeforeHeapIndexScan) and are not summed.
    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
    {
        var typeStats = _typeStats!;
        var sampler = _sampler!;

        foreach (IHeapIndexScanParticipant p in partials)
        {
            var other = (WcfChannelAnalyzer)p;
            if (other._typeStats is null) continue;

            foreach (var kvp in other._typeStats)
            {
                if (!typeStats.TryGetValue(kvp.Key, out var self))
                {
                    typeStats[kvp.Key] = kvp.Value;
                    continue;
                }

                var o = kvp.Value;
                typeStats[kvp.Key] = (self.Name, self.Total,
                    self.Opened + o.Opened,
                    self.Faulted + o.Faulted,
                    self.Closed + o.Closed,
                    self.Other + o.Other,
                    self.Bytes);
            }

            if (other._sampler is not null)
                sampler.MergeFrom(other._sampler);
        }
    }

    private void OnHeapEntry(in HeapEntry entry)
    {
        var candidateMts = _candidateMts!;
        var typeStats = _typeStats!;
        var sampler = _sampler!;

        if (!candidateMts.ContainsKey(entry.MethodTable)) return;
        if (!typeStats.TryGetValue(entry.MethodTable, out var ts)) return;

        WcfChannelSnapshot? snap = TypedResourceScanDriver.TryGetSample(this, sampler, _heap!, in entry, ts.Name);

        int opened = ts.Opened; int faulted = ts.Faulted; int closed = ts.Closed; int other = ts.Other;
        if (snap is not null)
        {
            if (snap.StateValue == StateOpened)       opened++;
            else if (snap.StateValue == StateFaulted) faulted++;
            else if (snap.StateValue == StateClosed)  closed++;
            else                                      other++;
        }

        typeStats[entry.MethodTable] = (ts.Name, ts.Total, opened, faulted, closed, other, ts.Bytes);

        if (snap is not null && snap.StateValue == StateFaulted)
            sampler.AddTopSample(snap);
    }

    // Relies on the pipeline dispatcher already having called BeforeHeapIndexScan/OnHeapEntry
    // on this context before AnalyzeAsync runs (see AnalysisPipeline.ExecuteAsync).
    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(BuildResult().Stamp(this));
    }

    private WcfChannelDomainResult BuildResult()
    {
        if (_typeStats is null || _typeStats.Count == 0)
            return Empty();

        // ── Build result ──────────────────────────────────────────────────────
        int totalChannels = 0, totalOpened = 0, totalFaulted = 0, totalClosed = 0, totalOther = 0;
        var byType = new List<WcfChannelTypeSummary>(_typeStats.Count);

        foreach (var kv in _typeStats)
        {
            var ts = kv.Value;
            byType.Add(new WcfChannelTypeSummary(ts.Name, ts.Total, ts.Opened, ts.Faulted, ts.Closed, ts.Other, ts.Bytes));
            totalChannels += ts.Total;
            totalOpened   += ts.Opened;
            totalFaulted  += ts.Faulted;
            totalClosed   += ts.Closed;
            totalOther    += ts.Other;
        }

        byType.Sort(static (a, b) => b.TotalCount.CompareTo(a.TotalCount));

        return new WcfChannelDomainResult(
            WcfPresent:       totalChannels > 0,
            TotalChannels:    totalChannels,
            OpenedChannels:   totalOpened,
            FaultedChannels:  totalFaulted,
            ClosedChannels:   totalClosed,
            OtherChannels:    totalOther,
            ByType:           byType,
            TopFaultedChannels: _sampler?.TopSamples ?? [],
            StateScanCapped:  _sampler?.ScanCapped ?? false);
    }

    private static string MapCommunicationState(int state) => state switch
    {
        0 => "Created",
        1 => "Opening",
        2 => "Opened",
        3 => "Closing",
        4 => "Closed",
        5 => "Faulted",
        _ => "Unknown",
    };

    private static WcfChannelDomainResult Empty() =>
        new(false, 0, 0, 0, 0, 0, [], [], false);
}
