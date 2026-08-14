using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
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
    /// (state value, reference fields) is read from each type's <c>SampleAddress</c>, bounding
    /// deep analysis to one object access per type.
    ///
    /// Bounded: top <see cref="TypeCandidateLimit"/> state machine types by count are
    /// analysed; only top <see cref="TopTypeLimit"/> appear in the report output.
    /// </summary>
    public sealed class AsyncStateMachineAnalyzer : IAnalyzer
    {
        // Compiler-generated async state machine type suffix: <MethodName>d__N
        private static readonly Regex StateMachinePattern =
            new(@"<(.+?)>d__\d+$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

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
            for (int i = 0; i < candidates.Count; i++)
            {
                totalCount += candidates[i].Entry.Count;
                totalBytes += candidates[i].Entry.TotalSize;
            }

            // ── Step 3: Field metadata + sample-based analysis ───────────────────
            // Read ClrType.Fields and the SampleAddress for each candidate type.
            int typeLimit = Math.Min(candidates.Count, options.TopTypeLimit);
            var topTypes = new List<StateMachineTypeProfile>(typeLimit);
            var highCaptures = new List<(ulong Address, string TypeName, ulong CapturedBytes, List<string> LargeCaptures)>(16);

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
                    m = StateMachinePattern.Match(fullName, angleOpen);
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
                int avgStateValue = 0;
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
                            try { avgStateValue = stateField.Read<int>(sample, interior: false); }
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
                    topTypes.Add(new StateMachineTypeProfile(
                        TypeName: clrType.Name ?? $"MT:0x{mt:X}",
                        OriginatingMethod: methodName,
                        DeclaringType: declaringType,
                        Count: (int)Math.Min(entry.Count, int.MaxValue),
                        TotalBytes: entry.TotalSize,
                        SampleStateValue: avgStateValue,
                        ReferenceFieldCount: refFieldCount,
                        Gen2Count: entry.Gen2Count,
                        Gen2Fraction: gen2Fraction,
                        IsAsyncVoid: isAsyncVoid));
                }
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
                ScanLimited: scanLimited);
        }

        public void Dispose() { }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static bool ImplementsIAsyncStateMachine(ClrType type)
        {
            foreach (ClrInterface iface in type.EnumerateInterfaces())
            {
                if (iface.Name is "System.Runtime.CompilerServices.IAsyncStateMachine")
                    return true;
            }
            return false;
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
