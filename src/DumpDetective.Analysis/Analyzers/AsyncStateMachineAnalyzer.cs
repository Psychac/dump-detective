using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;

using Microsoft.Diagnostics.Runtime;

using System.Text.RegularExpressions;

namespace DumpDetective.Analysis.Analyzers
{
    /// <summary>
    /// Phase-2 analyzer covering §23.1 (state machine population), §23.2 (captured closure
    /// analysis), and §23.3 (suspended method map).
    ///
    /// Detection uses <c>TypeAggregates</c> type names (O(types) string match) — no full heap
    /// scan. Instance counts and sizes come from <c>TypeAggregates</c>. Field-level data
    /// (reference fields) is read from each type's <c>SampleAddress</c>, bounding deep
    /// analysis to one object access per type.
    ///
    /// The suspend-state histogram (<c>DominantState</c>/<c>StateDistribution</c>) needs more
    /// than one sample per type, so it runs a second bounded pass over
    /// <c>IHeapAnalysisCache.EnumerateIndexedEntriesAsTuples</c> (the disk-backed object index —
    /// falls back to a live heap walk only when no disk index exists), scoped to the top
    /// <see cref="AsyncStateMachineAnalysisOptions.HistogramTopTypeLimit"/> types and capped at
    /// <see cref="AsyncStateMachineAnalysisOptions.HistogramInstanceCapPerType"/> instances/type.
    ///
    /// Bounded: top <see cref="TypeCandidateLimit"/> state machine types by count are
    /// analysed; only top <see cref="TopTypeLimit"/> appear in the report output.
    /// </summary>
    public sealed class AsyncStateMachineAnalyzer : IAnalyzer
    {
        public string Name => "Async State Machine Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(
            AnalysisContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AsyncStateMachineAnalysisOptions options = context.AnalysisOptions.AsyncStateMachineAnalysis;
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, options, cancellationToken).Stamp(this));
        }

        private static AnalyzerDomainResult Analyze(
            ClrHeap heap,
            IHeapAnalysisCache cache,
            AsyncStateMachineAnalysisOptions options,
            CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? typeAggregates = null;
            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? idx))
                typeAggregates = idx.TypeAggregates;

            if (typeAggregates is null)
                return new AsyncStateMachineDomainResult(0, 0, [], [], [], false);

            // ── Step 1: Identify async state machine types from TypeAggregates ─────
            // Pattern: <MethodName>d__N in the type name (last component of full name)
            var candidates = new List<(ulong Mt, TypeAggregateIndexEntry Entry)>(32);
            bool scanLimited = false;

            foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in typeAggregates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Phase 1 flag already verified pattern match; skip expensive ClrMD call here
                if ((kv.Value.Flags & TypeAggregateFlags.IsAsyncStateMachineType) == 0)
                    continue;

                candidates.Add((kv.Key, kv.Value));

                if (candidates.Count >= options.TypeCandidateLimit)
                {
                    scanLimited = true;
                    break;
                }
            }

            if (candidates.Count == 0)
                return new AsyncStateMachineDomainResult(0, 0, [], [], [], false);

            // Sort by count descending
            candidates.Sort(static (a, b) => b.Entry.Count.CompareTo(a.Entry.Count));

            // ── Step 2: Aggregate totals ──────────────────────────────────────────
            long totalCount = 0;
            ulong totalBytes = 0;
            long totalGen2Count = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                totalCount += candidates[i].Entry.Count;
                totalBytes += candidates[i].Entry.TotalSize;
                totalGen2Count += candidates[i].Entry.Gen2Count;
            }

            // ── Step 3: Field metadata + sample-based analysis ───────────────────
            // Read ClrType.Fields and the SampleAddress for each candidate type.
            int typeLimit = Math.Min(candidates.Count, options.TopTypeLimit);
            var pendingProfiles = new List<(ulong Mt, string TypeName, string OriginatingMethod, string DeclaringType,
                int Count, ulong TotalBytes, int SampleStateValue, int ReferenceFieldCount, long Gen2Count,
                double Gen2Fraction, bool IsAsyncVoid)>(typeLimit);
            var highCaptures = new List<(ulong Address, string TypeName, ulong CapturedBytes, List<string> LargeCaptures)>(16);
            var stateFieldByMt = new Dictionary<ulong, ClrInstanceField?>(typeLimit);

            for (int i = 0; i < candidates.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (ulong mt, TypeAggregateIndexEntry entry) = candidates[i];

                ClrType? clrType = heap.GetTypeByMethodTable(mt);
                if (clrType?.Name is not string fullName) continue;

                // Extract method name from <MethodName>d__N pattern
                int angleOpen = fullName.LastIndexOf('<');
                if (angleOpen < 0) continue;

                Match m;
                try
                {
                    m = AsyncStateMachineNamePattern.Regex.Match(fullName, angleOpen);
                }
                catch (RegexMatchTimeoutException)
                {
                    // Regex timeout on this type name; skip it
                    continue;
                }

                if (!m.Success) continue;

                string methodName = m.Groups[1].Value;
                string declaringType = angleOpen > 0 ? fullName[..(angleOpen - 1)] : string.Empty;

                // Count reference fields and locate <>1__state
                ClrInstanceField? stateField = null;
                int refFieldCount = 0;
                foreach (ClrInstanceField f in clrType.Fields)
                {
                    if (f.Name == "<>1__state")
                        stateField = f;
                    if (f.IsObjectReference)
                        refFieldCount++;
                }

                // Read state value and captured ref bytes from the sample instance
                int sampleStateValue = 0;
                ulong capturedBytes = 0;
                var largeCaptures = new List<string>(4);

                if (entry.SampleAddress != 0)
                {
                    ClrObject sample = heap.GetObject(entry.SampleAddress);
                    if (sample.IsValid && sample.Type is not null)
                    {
                        // State field value
                        if (stateField is not null)
                        {
                            try { sampleStateValue = stateField.Read<int>(sample, interior: false); }
                            catch { /* unreadable */ }
                        }

                        // Reference field sizes (captured closure estimate)
                        foreach (ClrInstanceField f in clrType.Fields)
                        {
                            if (!f.IsObjectReference) continue;
                            try
                            {
                                ClrObject refObj = f.ReadObject(sample.Address, interior: false);
                                if (!refObj.IsValid || refObj.Address == 0) continue;
                                ulong sz = refObj.Size;
                                capturedBytes += sz;
                                if (sz >= options.LargeCaptureThresholdBytes)
                                    largeCaptures.Add($"{f.Name} ({refObj.Type?.Name ?? "?"}, {FormatHelper.FormatBytes(sz)})");
                            }
                            catch { /* field unreadable */ }
                        }

                        if (capturedBytes > 0)
                            highCaptures.Add((entry.SampleAddress, clrType.Name ?? $"MT:0x{mt:X}", capturedBytes, largeCaptures));
                    }
                }

                double gen2Fraction = entry.Count > 0 ? entry.Gen2Count / (double)entry.Count : 0.0;
                bool isAsyncVoid = IsAsyncVoidStateMachine(clrType);
                if (i < typeLimit)
                {
                    pendingProfiles.Add((
                        Mt: mt,
                        TypeName: clrType.Name ?? $"MT:0x{mt:X}",
                        OriginatingMethod: methodName,
                        DeclaringType: declaringType,
                        Count: (int)Math.Min(entry.Count, int.MaxValue),
                        TotalBytes: entry.TotalSize,
                        SampleStateValue: sampleStateValue,
                        ReferenceFieldCount: refFieldCount,
                        Gen2Count: entry.Gen2Count,
                        Gen2Fraction: gen2Fraction,
                        IsAsyncVoid: isAsyncVoid));
                    stateFieldByMt[mt] = stateField;
                }
            }

            // ── Step 3b: Suspend-state histogram ──────────────────────────────────
            // TypeAggregates only retain one SampleAddress per type, so the state
            // distribution requires a second bounded heap pass, scoped to the top
            // HistogramTopTypeLimit types and capped at HistogramInstanceCapPerType
            // instances per type.
            int histogramTypeCount = Math.Min(pendingProfiles.Count, options.HistogramTopTypeLimit);
            var histogramMts = new HashSet<ulong>(histogramTypeCount);
            var histograms = new Dictionary<ulong, Dictionary<int, int>>(histogramTypeCount);
            var histogramRemaining = new Dictionary<ulong, int>(histogramTypeCount);
            for (int i = 0; i < histogramTypeCount; i++)
            {
                ulong mt = pendingProfiles[i].Mt;
                if (stateFieldByMt.TryGetValue(mt, out ClrInstanceField? sf) && sf is not null)
                {
                    histogramMts.Add(mt);
                    histograms[mt] = new Dictionary<int, int>(8);
                    histogramRemaining[mt] = options.HistogramInstanceCapPerType;
                }
            }

            if (histogramMts.Count > 0)
            {
                // Prefer the disk-backed object index (sequential small-record reads via
                // ObjectIndexReader) over a live ClrMD heap.EnumerateObjects() pass, which
                // touches the mapped dump file per object. Only addresses matching a candidate
                // MT ever get a live heap.GetObject() call, bounded by the per-type cap.
                bool hasDiskIndex = cache.EnumerateIndexedEntriesAsTuples().Any();
                IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> entries = hasDiskIndex
                    ? cache.EnumerateIndexedEntriesAsTuples()
                    : LiveHeapEntries(heap);

                int typesStillOpen = histogramMts.Count;
                foreach ((ulong address, ulong mt, ulong _) in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!histogramRemaining.TryGetValue(mt, out int remaining) || remaining <= 0) continue;

                    ClrObject obj = heap.GetObject(address);
                    if (!obj.IsValid) continue;

                    ClrInstanceField? sf = stateFieldByMt[mt];
                    int stateValue;
                    try { stateValue = sf!.Read<int>(obj, interior: false); }
                    catch { continue; }

                    Dictionary<int, int> hist = histograms[mt];
                    hist[stateValue] = hist.GetValueOrDefault(stateValue) + 1;

                    remaining--;
                    histogramRemaining[mt] = remaining;
                    if (remaining == 0 && --typesStillOpen == 0)
                        break;
                }
            }

            var topTypes = new List<StateMachineTypeProfile>(pendingProfiles.Count);
            foreach (var p in pendingProfiles)
            {
                IReadOnlyList<(int State, int Count)> distribution = [];
                int dominantState = p.SampleStateValue;

                if (histograms.TryGetValue(p.Mt, out Dictionary<int, int>? hist) && hist.Count > 0)
                {
                    var sorted = new List<(int State, int Count)>(hist.Count);
                    foreach (KeyValuePair<int, int> kv in hist)
                        sorted.Add((kv.Key, kv.Value));
                    sorted.Sort(static (a, b) => b.Count.CompareTo(a.Count));
                    if (sorted.Count > 3)
                        sorted.RemoveRange(3, sorted.Count - 3);
                    distribution = sorted;
                    dominantState = sorted[0].State;
                }

                topTypes.Add(new StateMachineTypeProfile(
                    TypeName: p.TypeName,
                    OriginatingMethod: p.OriginatingMethod,
                    DeclaringType: p.DeclaringType,
                    Count: p.Count,
                    TotalBytes: p.TotalBytes,
                    DominantState: dominantState,
                    StateDistribution: distribution,
                    ReferenceFieldCount: p.ReferenceFieldCount,
                    Gen2Count: p.Gen2Count,
                    Gen2Fraction: p.Gen2Fraction,
                    IsAsyncVoid: p.IsAsyncVoid));
            }

            // ── Step 4: TopByCapturedSize ─────────────────────────────────────────
            highCaptures.Sort(static (a, b) => b.CapturedBytes.CompareTo(a.CapturedBytes));
            int captureLimit = Math.Min(highCaptures.Count, options.TopCapturedSizeEntries);
            var topByCapturedSize = new List<HighCaptureStateMachine>(captureLimit);
            for (int i = 0; i < captureLimit; i++)
            {
                (ulong addr, string typeName, ulong captured, List<string> captures) = highCaptures[i];
                topByCapturedSize.Add(new HighCaptureStateMachine(
                    Address: addr,
                    TypeName: typeName,
                    TotalCapturedRefBytes: captured,
                    LargeCaptures: captures));
            }

            // ── Step 5: SuspendedMethodMap ────────────────────────────────────────
            // Group by (DeclaringType, MethodName) — same method can produce multiple
            // compiler-generated state machine types for different overloads or MoveNext versions.
            var methodMap = new Dictionary<(string DeclaringType, string Method), (long Count, ulong Bytes)>(16);
            
            // Build from topTypes which have methodName and declaringType already extracted
            foreach (StateMachineTypeProfile profile in topTypes)
            {
                var key = (profile.DeclaringType, profile.OriginatingMethod);
                if (methodMap.TryGetValue(key, out (long Count, ulong Bytes) existing))
                    methodMap[key] = (existing.Count + profile.Count, existing.Bytes + profile.TotalBytes);
                else
                    methodMap[key] = (profile.Count, profile.TotalBytes);
            }

            var suspendedMap = new List<SuspendedMethodEntry>(methodMap.Count);
            foreach (KeyValuePair<(string, string), (long Count, ulong Bytes)> kv in methodMap)
                suspendedMap.Add(new SuspendedMethodEntry(kv.Key.Item1, kv.Key.Item2, (int)Math.Min(kv.Value.Count, int.MaxValue), kv.Value.Bytes));

            suspendedMap.Sort(static (a, b) => b.SuspendedCount.CompareTo(a.SuspendedCount));
            if (suspendedMap.Count > options.SuspendedMethodMapLimit)
                suspendedMap.RemoveRange(options.SuspendedMethodMapLimit, suspendedMap.Count - options.SuspendedMethodMapLimit);

            return new AsyncStateMachineDomainResult(
                TotalStateMachines: (int)Math.Min(totalCount, int.MaxValue),
                TotalStateMachineBytes: totalBytes,
                TopStateMachineTypes: topTypes,
                TopByCapturedSize: topByCapturedSize,
                SuspendedMethodMap: suspendedMap,
                ScanLimited: scanLimited,
                TotalGen2Count: totalGen2Count);
        }

        public void Dispose() { }

        // ── Helpers ───────────────────────────────────────────────────────────────

        // Fallback for in-memory cache mode (no disk-backed object index available).
        private static IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> LiveHeapEntries(ClrHeap heap)
        {
            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type is null) continue;
                yield return (obj.Address, obj.Type.MethodTable, obj.Size);
            }
        }

        private static bool IsAsyncVoidStateMachine(ClrType type)
        {
            foreach (ClrInstanceField f in type.Fields)
            {
                if (f.Name != "<>t__builder") continue;
                ClrType? builderType = f.Type;
                if (builderType?.Name is not null)
                {
                    return builderType.Name.Contains("AsyncVoidMethodBuilder");
                }
            }
            return false;
        }

    }
}
