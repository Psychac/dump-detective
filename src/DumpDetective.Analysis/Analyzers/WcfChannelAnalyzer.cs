using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

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
public sealed class WcfChannelAnalyzer : IAnalyzer
{
    public string Name => "WCF Channel Analysis";
    public string Category => "Infrastructure";

    private const int MaxStateSamples = 500;

    // CommunicationState enum values
    private const int StateOpened  = 2;
    private const int StateFaulted = 5;
    private const int StateClosed  = 4;

    // Types to match: in System.ServiceModel namespace, ending with "Channel" or
    // well-known base/proxy types.
    private static bool IsWcfChannelType(string typeName)
    {
        if (!typeName.StartsWith("System.ServiceModel.", StringComparison.Ordinal)) return false;
        // Accept any channel or service-model communication object
        return typeName.Contains("Channel", StringComparison.Ordinal)
            || typeName.EndsWith(".ServiceChannel", StringComparison.Ordinal)
            || typeName.Contains("ClientBase", StringComparison.Ordinal)
            || typeName.Contains("CommunicationObject", StringComparison.Ordinal);
    }

    private static readonly string[] StateFieldNames = ["_state", "state", "communicationState"];

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            Analyze(context.Heap, context.Cache, cancellationToken).Stamp(this));
    }

    private static AnalyzerDomainResult Analyze(
        ClrHeap? heap,
        IHeapAnalysisCache? cache,
        CancellationToken cancellationToken)
    {
        if (heap is null)
            return Empty();

        // ── Step 1: Find matching MTs ─────────────────────────────────────────
        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? typeAggregates = null;
        if (cache is HeapAnalysisCache hc && hc.TryGetHeapIndex(out HeapIndexBuildResult? idx))
            typeAggregates = idx?.TypeAggregates;

        var candidateMts = new Dictionary<ulong, (string TypeName, TypeAggregateIndexEntry Entry)>(16);

        if (typeAggregates is not null)
        {
            foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in typeAggregates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ClrType? clrType = heap.GetTypeByMethodTable(kv.Key);
                if (clrType?.Name is not string fullName) continue;
                if (IsWcfChannelType(fullName))
                    candidateMts[kv.Key] = (fullName, kv.Value);
            }
        }

        if (candidateMts.Count == 0)
            return Empty();

        // ── Step 2: Aggregate per-type counters + sample state reading ────────
        var typeStats = new Dictionary<ulong, (string Name, int Total, int Opened, int Faulted, int Closed, int Other, ulong Bytes)>(candidateMts.Count);
        foreach (KeyValuePair<ulong, (string TypeName, TypeAggregateIndexEntry Entry)> kv in candidateMts)
        {
            int total = (int)Math.Min(kv.Value.Entry.Count, int.MaxValue);
            typeStats[kv.Key] = (kv.Value.TypeName, total, 0, 0, 0, 0, kv.Value.Entry.TotalSize);
        }

        var topFaulted = new List<WcfChannelSnapshot>(32);
        int stateSamples = 0;
        bool stateScanCapped = false;
        var perTypeSamples = new Dictionary<ulong, int>(candidateMts.Count);

        if (cache is HeapAnalysisCache heapCache2 && heapCache2.TryGetHeapIndex(out HeapIndexBuildResult? idx2))
        {
            foreach (HeapEntry entry in heapCache2.EnumerateIndexedEntries())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!typeStats.TryGetValue(entry.MethodTable, out var ts)) continue;

                int stateVal = -1;
                perTypeSamples.TryGetValue(entry.MethodTable, out int typeSampleCount);
                if (typeSampleCount < MaxStateSamples)
                {
                    stateVal = TryReadCommunicationState(heap, entry.Address);
                    perTypeSamples[entry.MethodTable] = typeSampleCount + 1;
                    stateSamples++;
                }
                else stateScanCapped = true;

                int opened = ts.Opened; int faulted = ts.Faulted; int closed = ts.Closed; int other = ts.Other;
                string stateLabel = MapCommunicationState(stateVal);
                if (stateVal == StateOpened)       opened++;
                else if (stateVal == StateFaulted) faulted++;
                else if (stateVal == StateClosed)  closed++;
                else if (stateVal >= 0)            other++;

                typeStats[entry.MethodTable] = (ts.Name, ts.Total, opened, faulted, closed, other, ts.Bytes);

                if (stateVal == StateFaulted && topFaulted.Count < 50)
                    topFaulted.Add(new WcfChannelSnapshot(ts.Name, entry.Address, stateLabel, stateVal));
            }
        }
        else
        {
            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!obj.IsValid || obj.Type is null) continue;
                ulong mt = obj.Type.MethodTable;
                if (!typeStats.TryGetValue(mt, out var ts)) continue;

                int stateVal = -1;
                perTypeSamples.TryGetValue(mt, out int typeSampleCount);
                if (typeSampleCount < MaxStateSamples)
                {
                    stateVal = TryReadCommunicationState(heap, obj.Address);
                    perTypeSamples[mt] = typeSampleCount + 1;
                }
                else stateScanCapped = true;

                string stateLabel = MapCommunicationState(stateVal);
                int opened = ts.Opened; int faulted = ts.Faulted; int closed = ts.Closed; int other = ts.Other;
                if (stateVal == StateOpened)       opened++;
                else if (stateVal == StateFaulted) faulted++;
                else if (stateVal == StateClosed)  closed++;
                else if (stateVal >= 0)            other++;

                typeStats[mt] = (ts.Name, ts.Total + 1, opened, faulted, closed, other, ts.Bytes + (ulong)obj.Size);

                if (stateVal == StateFaulted && topFaulted.Count < 50)
                    topFaulted.Add(new WcfChannelSnapshot(ts.Name, obj.Address, stateLabel, stateVal));
            }
        }

        // ── Step 3: Build result ──────────────────────────────────────────────
        int totalChannels = 0, totalOpened = 0, totalFaulted = 0, totalClosed = 0, totalOther = 0;
        var byType = new List<WcfChannelTypeSummary>(typeStats.Count);

        foreach (var kv in typeStats)
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
            TopFaultedChannels: topFaulted,
            StateScanCapped:  stateScanCapped);
    }

    private static int TryReadCommunicationState(ClrHeap heap, ulong address)
    {
        try
        {
            ClrObject obj = heap.GetObject(address);
            if (!obj.IsValid || obj.Type is null) return -1;

            for (int i = 0; i < StateFieldNames.Length; i++)
            {
                ClrInstanceField? field = obj.Type.GetFieldByName(StateFieldNames[i]);
                if (field is null) continue;
                // State may be stored as int or enum (backed by int)
                if (field.ElementType == ClrElementType.Int32 ||
                    field.ElementType == ClrElementType.UInt32 ||
                    field.ElementType == ClrElementType.Object)
                {
                    return field.Read<int>(obj.Address, interior: false);
                }
            }
        }
        catch { }
        return -1;
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
