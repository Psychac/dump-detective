using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Shared candidate-type discovery for the typed-resource quartet (<c>DbConnectionAnalyzer</c>,
/// <c>WcfChannelAnalyzer</c>, <c>HttpObjectAnalyzer</c>, <c>TimerLeakAnalyzer</c>): reads
/// <c>TypeAggregates</c> from the Phase-1 heap index when one is available, resolving each
/// candidate MethodTable's name via <see cref="TypeAggregateNameResolver"/>; falls back to a
/// full <c>heap.EnumerateObjects()</c> type-discovery sweep when no index is present. Each
/// caller supplies its own classification predicate — only the discovery mechanics are shared.
/// </summary>
internal static class TypedResourceCandidateScanner
{
    public static Dictionary<ulong, (string TypeName, long Count, ulong Bytes)> DiscoverCandidates(
        ClrHeap heap,
        IHeapAnalysisCache? cache,
        Func<string, bool> classifier,
        CancellationToken cancellationToken = default)
    {
        var candidateMts = new Dictionary<ulong, (string TypeName, long Count, ulong Bytes)>(16);

        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? typeAggregates = null;
        if (cache is HeapAnalysisCache hc && hc.TryGetHeapIndex(out HeapIndexBuildResult? idx))
            typeAggregates = idx?.TypeAggregates;

        if (typeAggregates is not null)
        {
            foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in typeAggregates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string typeName = TypeAggregateNameResolver.ResolveTypeName(heap, kv.Key, kv.Value.SampleAddress);
                if (!classifier(typeName)) continue;
                candidateMts[kv.Key] = (typeName, kv.Value.Count, kv.Value.TotalSize);
            }
        }
        else
        {
            // Fallback: discover candidate types by scanning the live heap (slower on large dumps).
            var seenMts = new HashSet<ulong>();
            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!obj.IsValid || obj.Type is null) continue;
                ulong mt = obj.Type.MethodTable;
                if (!seenMts.Add(mt)) continue; // only classify each MT once
                string typeName = obj.Type.Name ?? string.Empty;
                if (!classifier(typeName)) continue;
                candidateMts[mt] = (typeName, 0, 0);
            }
        }

        return candidateMts;
    }
}

/// <summary>
/// Shared per-instance capped state-field sampling for the two members of the typed-resource
/// quartet that read a runtime state field per instance (<c>DbConnectionAnalyzer</c>,
/// <c>WcfChannelAnalyzer</c>). Caps state-field reads per MethodTable (ClrMD field access is
/// costly at heap scale) and holds a bounded top-N list of "interesting" instance snapshots
/// the caller decides to keep (e.g. open connections, faulted channels).
/// </summary>
internal sealed class InstanceStateSampler<TSnapshot>
{
    private readonly int _maxSamplesPerType;
    private readonly int _topNCap;
    private readonly Dictionary<ulong, int> _perTypeSamples = new();
    private readonly List<TSnapshot> _topSamples;
    private bool _capped;

    public InstanceStateSampler(int maxSamplesPerType, int topNCap)
    {
        _maxSamplesPerType = maxSamplesPerType;
        _topNCap = topNCap;
        _topSamples = new List<TSnapshot>(topNCap);
    }

    public IReadOnlyList<TSnapshot> TopSamples => _topSamples;
    public bool ScanCapped => _capped;

    /// <summary>
    /// Reserves a state-field read slot for <paramref name="methodTable"/> if its per-type cap
    /// hasn't been reached yet. Returns false (and marks the scan capped) once the cap is hit —
    /// callers should skip the field read for this entry when this returns false.
    /// </summary>
    public bool TryReserveSample(ulong methodTable)
    {
        _perTypeSamples.TryGetValue(methodTable, out int count);
        if (count >= _maxSamplesPerType)
        {
            _capped = true;
            return false;
        }

        _perTypeSamples[methodTable] = count + 1;
        return true;
    }

    public void AddTopSample(TSnapshot snapshot)
    {
        if (_topSamples.Count < _topNCap)
            _topSamples.Add(snapshot);
    }

    /// <summary>
    /// Merges top samples and per-type counts from <paramref name="other"/> into this instance.
    /// Called once per worker partial after a parallel heap-index scan completes.
    /// Appends samples until <c>_topNCap</c> is reached (all samples are equal priority for
    /// WcfChannel/DbConnection — the cap merely limits report verbosity).
    /// </summary>
    internal void MergeFrom(InstanceStateSampler<TSnapshot> other)
    {
        _capped |= other._capped;

        foreach (TSnapshot sample in other._topSamples)
            if (_topSamples.Count < _topNCap)
                _topSamples.Add(sample);

        foreach (var kvp in other._perTypeSamples)
        {
            _perTypeSamples.TryGetValue(kvp.Key, out int existing);
            _perTypeSamples[kvp.Key] = existing + kvp.Value;
        }
    }

    /// <summary>
    /// Reads the first matching <c>int</c>-backed field (tried in order) off the object at
    /// <paramref name="address"/>. Returns -1 if the object is invalid, no field name matches,
    /// or ClrMD throws on a corrupt/unloaded object. When <paramref name="acceptedElementTypes"/>
    /// is omitted, only <see cref="ClrElementType.Int32"/> fields are accepted.
    /// </summary>
    public static int TryReadIntField(
        ClrHeap heap, ulong address, string[] fieldNames, ClrElementType[]? acceptedElementTypes = null)
    {
        try
        {
            ClrObject obj = heap.GetObject(address);
            if (!obj.IsValid || obj.Type is null) return -1;

            for (int i = 0; i < fieldNames.Length; i++)
            {
                ClrInstanceField? field = obj.Type.GetFieldByName(fieldNames[i]);
                if (field is null) continue;

                if (acceptedElementTypes is null)
                {
                    if (field.ElementType != ClrElementType.Int32) continue;
                }
                else if (Array.IndexOf(acceptedElementTypes, field.ElementType) < 0)
                {
                    continue;
                }

                return field.Read<int>(obj.Address, interior: false);
            }
        }
        catch { /* ClrMD can throw on corrupt/unloaded objects */ }

        return -1;
    }
}
