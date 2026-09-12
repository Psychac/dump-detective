using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers.Types;

/// <summary>
/// Runs the retyped <see cref="StringAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
/// <remarks>
/// Bespoke, not built on <c>TypedResourceCandidateStreamingLegacyAdapter</c> — the inner analyzer's
/// P1-3 string-field-ownership feature needs to inspect every heap object's type, not just objects
/// matching a single name/structural candidate filter, so that base's "only accumulate matching
/// candidates" shape doesn't fit. Same reasoning <c>CrashAnalyzer</c>/<c>HangAnalyzer</c>'s own
/// adapters already give for implementing <see cref="IParallelHeapIndexScanParticipant"/> directly.
///
/// Two concerns share one shared heap-index scan pass, exactly like the pre-retyping analyzer did:
/// <list type="bullet">
/// <item>String dedup candidates: every <c>System.String</c> instance is collected as a
/// <see cref="Sdk.Analysis.HeapObjectRef"/>, handed to the inner analyzer via the same
/// <c>heapObjectsOverride</c> mechanism <c>TypedResourceCandidateStreamingLegacyAdapter</c> uses —
/// the inner analyzer does its own content fingerprinting via <c>IDumpObjectFieldQuery.TryGetStringValue</c>,
/// not this adapter.</item>
/// <item>P1-3 string-field ownership: reservoir-sampled per-owner-type byte totals, ported
/// near-verbatim from the pre-retyping analyzer's own <c>AccumulateStringOwnerType</c> (Algorithm-R
/// sampling, bounded to 100 tracked types / 2000 samples each). This is a whole-heap-population
/// aggregate with no per-instance SDK-capability expression, so it's computed here, dump-side, and
/// handed to the inner analyzer pre-aggregated via <see cref="StringAnalyzerOptions"/> — not
/// streamed as objects, which would mean materializing a <see cref="Sdk.Analysis.HeapObjectRef"/>
/// per heap object regardless of type, a real memory regression this design deliberately avoids.</item>
/// </list>
///
/// <b>Dropped, not ported</b>: the pre-retyping analyzer's no-index live fallback
/// (<c>ScanForStringOwnerTypesFallback</c>). Every other Tier-2-wave member already made this exact
/// simplification — an empty/zero result when the shared scan didn't run is accepted, matching
/// <c>TimerLeakAnalyzer</c>/<c>WeakReferenceAnalyzer</c>/<c>AsyncTaskAnalyzer</c>/<c>HangAnalyzer</c>.
/// </remarks>
public sealed class StringAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<StringAnalyzer>, IParallelHeapIndexScanParticipant
{
    private const int MaxStringOwnerTypesToTrack = 100;
    private const int MaxSamplesPerOwnerType = 2000;

    // Field layout cache per MethodTable, avoiding re-enumerating ClrType.Fields.
    private sealed class FieldLayoutCache
    {
        private readonly Dictionary<ulong, ClrInstanceField[]> _cache = new();

        public ClrInstanceField[] GetFields(ClrType? type)
        {
            if (type is null) return [];
            ulong mt = type.MethodTable;
            if (!_cache.TryGetValue(mt, out ClrInstanceField[]? fields))
            {
                fields = type.Fields.ToArray();
                _cache[mt] = fields;
            }
            return fields;
        }
    }

    private ClrHeap? _heap;
    private HashSet<ulong>? _stringMts;
    private IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? _typeAggregates;
    private readonly List<Sdk.Analysis.HeapObjectRef> _candidates = new();
    private bool _scanSucceeded = true;

    // P1-3 accumulator state — ported from the pre-retyping analyzer's own field set.
    private FieldLayoutCache? _fieldCache;
    private Dictionary<ulong, int[]>? _stringOwnerFieldIndices; // owner MT -> string field indices
    private HashSet<ulong>? _typesWithoutStringFields; // negative cache
    private readonly Dictionary<ulong, ulong> _stringOwnerTypeBytes = new(capacity: MaxStringOwnerTypesToTrack);
    private readonly Dictionary<ulong, int> _stringOwnerSampleCounts = new(capacity: MaxStringOwnerTypesToTrack);
    private Dictionary<ulong, int>? _stringOwnerSeenCounts;
    private Dictionary<ulong, ulong[]>? _stringOwnerReservoir;
    private Random? _reservoirRng;
    private IReadOnlyDictionary<ulong, int[]>? _precomputedStringFieldIndices;

    public StringAnalyzerLegacyAdapter() : base(new StringAnalyzer())
    {
    }

    protected override object? ResolveOptions(AnalysisOptions options) => options.StringAnalysis;

    public void BeforeHeapIndexScan(AnalysisContext context)
    {
        _heap = context.Heap;

        var stringMts = new HashSet<ulong>(capacity: 4);
        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? typeAggregates = null;
        HeapIndexBuildResult? heapIndex = null;

        if (context.Cache is HeapAnalysisCache concreteCache && concreteCache.TryGetHeapIndex(out heapIndex))
        {
            typeAggregates = heapIndex.TypeAggregates;
            foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kvp in heapIndex.TypeAggregates)
                if ((kvp.Value.Flags & TypeAggregateFlags.IsStringType) != 0)
                    stringMts.Add(kvp.Key);
        }

        _stringMts = stringMts;
        _typeAggregates = typeAggregates;

        _fieldCache = new FieldLayoutCache();
        _stringOwnerFieldIndices = new Dictionary<ulong, int[]>(capacity: 1000);
        _typesWithoutStringFields = new HashSet<ulong>(capacity: 1000);
        _stringOwnerSeenCounts = new Dictionary<ulong, int>(capacity: MaxStringOwnerTypesToTrack);
        _stringOwnerReservoir = new Dictionary<ulong, ulong[]>(capacity: MaxStringOwnerTypesToTrack);
        _reservoirRng = new Random();
        _precomputedStringFieldIndices = heapIndex?.StringFieldIndicesByMethodTable;
    }

    void IHeapIndexScanParticipant.OnHeapEntry(in HeapEntry entry)
    {
        AccumulateStringOwnerType(in entry);

        if (_stringMts is null || !_stringMts.Contains(entry.MethodTable))
            return;

        string name = _heap!.GetTypeByMethodTable(entry.MethodTable)?.Name ?? $"MT:0x{entry.MethodTable:x}";
        _candidates.Add(new Sdk.Analysis.HeapObjectRef(entry.Address, SdkTypeRefFactory.FromName(name, entry.MethodTable), entry.Size, name));
    }

    /// <summary>
    /// P1-3: accumulate string-field bytes owned by <paramref name="entry"/>'s type, ported
    /// near-verbatim from the pre-retyping analyzer's own <c>AccumulateStringOwnerType</c> — see
    /// that method's own remarks (preserved below) for the perf/sampling rationale.
    /// </summary>
    /// <remarks>
    /// Field-type checks below use <see cref="ClrInstanceField.ElementType"/>, never
    /// <c>ClrInstanceField.Type</c> — full <see cref="ClrType"/> resolution under a concurrent/
    /// parallel caller turned a ~20s scan into 5+ minutes (see
    /// <c>DiskBackedObjectIndexWriter.ComputeTypeShapeAndStringFields</c>'s own PERF remarks).
    /// <para>
    /// Sampling uses Algorithm-R reservoir sampling (bounded to <see cref="MaxSamplesPerOwnerType"/>
    /// instances per type) rather than taking the first N instances encountered — address/
    /// allocation-scan order otherwise biases the sample toward older instances, which can
    /// systematically underestimate types that grow over time, exactly the leak-detection pattern
    /// this table exists to surface.
    /// </para>
    /// </remarks>
    private void AccumulateStringOwnerType(in HeapEntry entry)
    {
        ulong mt = entry.MethodTable;
        int[]? stringFieldIndices;

        if (_precomputedStringFieldIndices is not null)
        {
            if (!_precomputedStringFieldIndices.TryGetValue(mt, out stringFieldIndices))
                return;
        }
        else
        {
            if (_typesWithoutStringFields!.Contains(mt))
                return;

            if (!_stringOwnerFieldIndices!.TryGetValue(mt, out stringFieldIndices))
            {
                ClrType? type = _heap!.GetTypeByMethodTable(mt);
                if (type is null)
                {
                    _typesWithoutStringFields.Add(mt);
                    return;
                }

                ClrInstanceField[] fields = _fieldCache!.GetFields(type);
                var indices = new List<int>(capacity: 4);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (fields[i].ElementType == ClrElementType.String)
                        indices.Add(i);
                }

                if (indices.Count == 0)
                {
                    _typesWithoutStringFields.Add(mt);
                    return;
                }

                stringFieldIndices = indices.ToArray();
                _stringOwnerFieldIndices[mt] = stringFieldIndices;
            }
        }

        bool alreadyTracked = _stringOwnerTypeBytes.ContainsKey(mt);
        if (!alreadyTracked && _stringOwnerTypeBytes.Count >= MaxStringOwnerTypesToTrack)
            return; // cap reached — only keep accumulating types already being tracked

        Dictionary<ulong, int> seenCounts = _stringOwnerSeenCounts!;
        seenCounts.TryGetValue(mt, out int n);
        n++;
        seenCounts[mt] = n;

        int slot;
        if (n <= MaxSamplesPerOwnerType)
        {
            slot = n - 1; // still filling the reservoir — always accept
        }
        else
        {
            int j = _reservoirRng!.Next(n); // uniform in [0, n)
            if (j >= MaxSamplesPerOwnerType)
                return; // not selected — skip the expensive per-object read entirely
            slot = j;
        }

        // Only past this point do we touch ClrMD's per-object machinery (GetObject/ReadObject),
        // which is the expensive part — bounded above to MaxSamplesPerOwnerType reads per type.
        ClrObject obj = _heap!.GetObject(entry.Address);
        if (!obj.IsValid) return;

        ClrInstanceField[] objFields = _fieldCache!.GetFields(obj.Type);
        ulong addBytes = 0;
        foreach (int fieldIndex in stringFieldIndices)
        {
            try
            {
                if (fieldIndex >= objFields.Length) continue;
                ClrObject stringRef = objFields[fieldIndex].ReadObject(obj, interior: false);
                if (!stringRef.IsValid) continue;
                addBytes += stringRef.Size;
            }
            catch
            {
                // Skip malformed field reads.
            }
        }

        if (!_stringOwnerReservoir!.TryGetValue(mt, out ulong[]? reservoir))
        {
            reservoir = new ulong[MaxSamplesPerOwnerType];
            _stringOwnerReservoir[mt] = reservoir;
        }

        _stringOwnerTypeBytes.TryGetValue(mt, out ulong existing);
        _stringOwnerTypeBytes[mt] = existing - reservoir[slot] + addBytes; // subtract replaced sample, if any
        reservoir[slot] = addBytes;
        _stringOwnerSampleCounts[mt] = Math.Min(n, MaxSamplesPerOwnerType);
    }

    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() => new StringAnalyzerLegacyAdapter();

    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
    {
        foreach (IHeapIndexScanParticipant p in partials)
        {
            if (p is not StringAnalyzerLegacyAdapter other)
                continue;

            _candidates.AddRange(other._candidates);
            if (!other._scanSucceeded)
                _scanSucceeded = false;

            foreach (KeyValuePair<ulong, ulong> kvp in other._stringOwnerTypeBytes)
            {
                if (!_stringOwnerTypeBytes.ContainsKey(kvp.Key) && _stringOwnerTypeBytes.Count >= MaxStringOwnerTypesToTrack)
                    continue;

                _stringOwnerTypeBytes.TryGetValue(kvp.Key, out ulong existingBytes);
                _stringOwnerTypeBytes[kvp.Key] = existingBytes + kvp.Value;

                _stringOwnerSampleCounts.TryGetValue(kvp.Key, out int existingSamples);
                other._stringOwnerSampleCounts.TryGetValue(kvp.Key, out int otherSamples);
                _stringOwnerSampleCounts[kvp.Key] = existingSamples + otherSamples;
            }
        }
    }

    void IHeapIndexScanParticipant.OnHeapIndexScanCompleted(bool succeeded) => _scanSucceeded = succeeded;

    protected override Sdk.Analysis.AnalysisContext BuildSdkContext(AnalysisContext context)
    {
        var options = (StringAnalysisOptions)ResolveOptions(context.AnalysisOptions)!;
        IReadOnlyList<(string TypeName, ulong TotalBytes)>? ownerTypeBytes = BuildOwnerTypeBytes();

        return LegacyAnalysisContextTranslator.Translate(
            context, new StringAnalyzerOptions(options, ownerTypeBytes),
            heapObjectsOverride: new PrecomputedHeapObjectStream(_scanSucceeded ? _candidates : []));
    }

    /// <summary>
    /// Extrapolates each tracked owner type's reservoir-sampled bytes against its real instance
    /// count from the pre-built index (bytes were sampled up to <see cref="MaxSamplesPerOwnerType"/>
    /// instances per type) — ported from the pre-retyping analyzer's own equivalent step.
    /// </summary>
    private IReadOnlyList<(string TypeName, ulong TotalBytes)>? BuildOwnerTypeBytes()
    {
        if (_heap is null || _stringOwnerTypeBytes.Count == 0)
            return null;

        var extrapolated = new Dictionary<ulong, ulong>(capacity: _stringOwnerTypeBytes.Count);
        foreach (KeyValuePair<ulong, ulong> kv in _stringOwnerTypeBytes)
        {
            _stringOwnerSampleCounts.TryGetValue(kv.Key, out int samples);
            if (samples <= 0) continue;

            ulong realCount = _typeAggregates is not null && _typeAggregates.TryGetValue(kv.Key, out TypeAggregateIndexEntry agg)
                ? (ulong)Math.Max(agg.Count, 0)
                : (ulong)samples;
            double avgBytesPerInstance = (double)kv.Value / samples;
            extrapolated[kv.Key] = (ulong)(avgBytesPerInstance * realCount);
        }

        if (extrapolated.Count == 0)
            return null;

        var topOwners = new List<(string, ulong)>(capacity: extrapolated.Count);
        foreach (KeyValuePair<ulong, ulong> kv in extrapolated.OrderByDescending(static kv => kv.Value))
        {
            string typeName = _heap.GetTypeByMethodTable(kv.Key)?.Name ?? $"MT:0x{kv.Key:x}";
            topOwners.Add((typeName, kv.Value));
        }
        return topOwners;
    }
}
