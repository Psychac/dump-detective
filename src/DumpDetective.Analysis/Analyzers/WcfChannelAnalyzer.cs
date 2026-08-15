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
    private const int StateOpening = 1;
    private const int StateOpened  = 2;
    private const int StateClosing = 3;
    private const int StateClosed  = 4;
    private const int StateFaulted = 5;

    private static readonly ClrElementType[] StateElementTypes =
        [ClrElementType.Int32, ClrElementType.UInt32, ClrElementType.Object];

    // Types to match: in System.ServiceModel namespace, ending with "Channel" or
    // well-known base/proxy types.
    private static readonly string[] WcfNamespacePrefixes = ["System.ServiceModel."];
    private static readonly string[] WcfContainsTokens = ["Channel", "ClientBase", "CommunicationObject"];
    private static readonly string[] FactoryNamespaces = ["System.ServiceModel."];
    private static readonly string[] FactoryContainsTokens = ["ChannelFactory"];

    public bool IsCandidateType(string typeName) =>
        TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, WcfNamespacePrefixes, ".ServiceChannel", WcfContainsTokens);

    private static bool IsFactoryType(string typeName) =>
        TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, FactoryNamespaces, ".ChannelFactory", FactoryContainsTokens);

    public int MaxStateSamplesPerType => MaxStateSamples;
    public int TopSampleCap => TopFaultedCap;

    private static readonly string[] StateFieldNames = ["_state", "state", "communicationState"];
    private static readonly string[] RemoteAddressFieldNames = ["_remoteAddress", "_via", "remoteAddress", "via"];

    WcfChannelSnapshot? ITypedResourceInstanceSampler<WcfChannelSnapshot>.TrySample(ClrHeap heap, in HeapEntry entry, string typeName)
    {
        int stateVal = InstanceStateSampler<WcfChannelSnapshot>.TryReadIntField(heap, entry.Address, StateFieldNames, StateElementTypes);
        if (stateVal < 0)
            return null;

        string? remoteAddress = TryExtractRemoteAddress(heap, entry.Address);
        return new WcfChannelSnapshot(typeName, entry.Address, MapCommunicationState(stateVal), stateVal, remoteAddress);
    }

    private static string? TryExtractRemoteAddress(ClrHeap heap, ulong channelAddress)
    {
        try
        {
            ClrObject channelObj = heap.GetObject(channelAddress);
            if (!channelObj.IsValid || channelObj.Type == null)
                return null;

            foreach (string fieldName in RemoteAddressFieldNames)
            {
                ClrInstanceField? field = channelObj.Type.GetFieldByName(fieldName);
                if (field == null)
                    continue;

                ClrObject endpointAddress = field.ReadObject(channelAddress, interior: false);
                if (!endpointAddress.IsValid || endpointAddress.Type == null)
                    continue;

                return TryExtractUriFromEndpointAddress(heap, endpointAddress);
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? TryExtractUriFromEndpointAddress(ClrHeap heap, ClrObject endpointAddress)
    {
        try
        {
            if (endpointAddress.Type == null)
                return null;

            string[] uriFieldNames = ["_uri", "uri", "_address", "address"];
            foreach (string fieldName in uriFieldNames)
            {
                ClrInstanceField? field = endpointAddress.Type.GetFieldByName(fieldName);
                if (field == null)
                    continue;

                ClrObject uriObj = field.ReadObject(endpointAddress.Address, interior: false);
                if (!uriObj.IsValid || uriObj.Type == null)
                    continue;

                return TryExtractStringFromUri(heap, uriObj);
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? TryExtractStringFromUri(ClrHeap heap, ClrObject uriObj)
    {
        try
        {
            string? uriStr = uriObj.AsString();
            if (!string.IsNullOrEmpty(uriStr))
                return uriStr;

            return uriObj.ToString();
        }
        catch
        {
        }

        return null;
    }

    private ClrHeap? _heap;
    private Dictionary<ulong, (string TypeName, long Count, ulong Bytes)>? _candidateMts;
    private HashSet<ulong>? _factoryMts;
    private Dictionary<ulong, (string Name, int Total, int Opening, int Opened, int Faulted, int Closing, int Closed, int Other, ulong Bytes)>? _typeStats;
    private InstanceStateSampler<WcfChannelSnapshot>? _sampler;
    private int _factoryCount;

    /// <summary>
    /// Resolves candidate WCF-type MethodTables and pre-seeds per-type counters from
    /// TypeAggregates, exactly mirroring the historical single-shot "Step 1 + pre-seed" logic.
    /// Also resolves factory-type MethodTables here (once per distinct type, bounded by type
    /// count) so OnHeapEntry can classify factories via a MethodTable hashset lookup instead of
    /// resolving heap.GetObject(...).Type.Name for every object in the heap — see OnHeapEntry.
    /// </summary>
    public void BeforeHeapIndexScan(AnalysisContext context)
    {
        ClrHeap heap = context.Heap;
        _heap = heap;

        Dictionary<ulong, (string TypeName, long Count, ulong Bytes)> candidateMts =
            TypedResourceScanDriver.DiscoverCandidates(this, heap, context.Cache);
        _candidateMts = candidateMts;

        Dictionary<ulong, (string TypeName, long Count, ulong Bytes)> factoryCandidates =
            TypedResourceCandidateScanner.DiscoverCandidates(heap, context.Cache, IsFactoryType);
        _factoryMts = new HashSet<ulong>(factoryCandidates.Keys);

        var typeStats = new Dictionary<ulong, (string Name, int Total, int Opening, int Opened, int Faulted, int Closing, int Closed, int Other, ulong Bytes)>(candidateMts.Count);
        foreach (KeyValuePair<ulong, (string TypeName, long Count, ulong Bytes)> kv in candidateMts)
        {
            int total = (int)Math.Min(kv.Value.Count, int.MaxValue);
            typeStats[kv.Key] = (kv.Value.TypeName, total, 0, 0, 0, 0, 0, 0, kv.Value.Bytes);
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

            _factoryCount += other._factoryCount;

            foreach (var kvp in other._typeStats)
            {
                if (!typeStats.TryGetValue(kvp.Key, out var self))
                {
                    typeStats[kvp.Key] = kvp.Value;
                    continue;
                }

                var o = kvp.Value;
                typeStats[kvp.Key] = (self.Name, self.Total,
                    self.Opening + o.Opening,
                    self.Opened + o.Opened,
                    self.Faulted + o.Faulted,
                    self.Closing + o.Closing,
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

        // MethodTable-only checks (both against sets resolved once per distinct type in
        // BeforeHeapIndexScan) — no heap.GetObject/ClrType resolution needed for the ~99.9% of
        // objects that are neither a WCF channel nor a channel factory.
        if (_factoryMts!.Contains(entry.MethodTable))
        {
            _factoryCount++;
            return;
        }

        if (!candidateMts.ContainsKey(entry.MethodTable)) return;
        if (!typeStats.TryGetValue(entry.MethodTable, out var ts)) return;

        WcfChannelSnapshot? snap = TypedResourceScanDriver.TryGetSample(this, sampler, _heap!, in entry, ts.Name);

        int opening = ts.Opening; int opened = ts.Opened; int faulted = ts.Faulted; int closing = ts.Closing; int closed = ts.Closed; int other = ts.Other;
        if (snap is not null)
        {
            if (snap.StateValue == StateOpening)      opening++;
            else if (snap.StateValue == StateOpened)  opened++;
            else if (snap.StateValue == StateFaulted) faulted++;
            else if (snap.StateValue == StateClosing) closing++;
            else if (snap.StateValue == StateClosed)  closed++;
            else                                      other++;
        }

        typeStats[entry.MethodTable] = (ts.Name, ts.Total, opening, opened, faulted, closing, closed, other, ts.Bytes);

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
        int totalChannels = 0, totalOpening = 0, totalOpened = 0, totalFaulted = 0, totalClosing = 0, totalClosed = 0, totalOther = 0;
        ulong totalBytes = 0;
        var byType = new List<WcfChannelTypeSummary>(_typeStats.Count);

        foreach (var kv in _typeStats)
        {
            var ts = kv.Value;
            byType.Add(new WcfChannelTypeSummary(ts.Name, ts.Total, ts.Opening, ts.Opened, ts.Faulted, ts.Closing, ts.Closed, ts.Other, ts.Bytes));
            totalChannels += ts.Total;
            totalOpening  += ts.Opening;
            totalOpened   += ts.Opened;
            totalFaulted  += ts.Faulted;
            totalClosing  += ts.Closing;
            totalClosed   += ts.Closed;
            totalOther    += ts.Other;
            totalBytes    += ts.Bytes;
        }

        byType.Sort(static (a, b) => b.TotalCount.CompareTo(a.TotalCount));

        return new WcfChannelDomainResult(
            WcfPresent:       totalChannels > 0 || _factoryCount > 0,
            TotalChannels:    totalChannels,
            OpeningChannels:  totalOpening,
            OpenedChannels:   totalOpened,
            FaultedChannels:  totalFaulted,
            ClosingChannels:  totalClosing,
            ClosedChannels:   totalClosed,
            OtherChannels:    totalOther,
            ByType:           byType,
            TopFaultedChannels: _sampler?.TopSamples ?? [],
            StateScanCapped:  _sampler?.ScanCapped ?? false,
            FactoryCount:     _factoryCount,
            TotalBytes:       totalBytes);
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
        new(false, 0, 0, 0, 0, 0, 0, 0, [], [], false);
}
