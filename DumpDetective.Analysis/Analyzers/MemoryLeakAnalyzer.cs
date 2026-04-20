using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Options;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Analysis.Analyzers
{
    internal class MemoryLeakAnalyzer : IAnalyzer
    {
        private const int TopFinalizerTypesToShow = 10;
        private const int TopDuplicateStringsToShow = 20;
        private const int TopHighlyReferencedObjectsToShow = 15;

        public string Name => "Memory Leak Analysis";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            MemoryLeakOptions options = context.Options.TryGetValue(nameof(MemoryLeakOptions), out object? configured)
                && configured is MemoryLeakOptions typed
                ? typed
                : new MemoryLeakOptions();

            AnalyzerExecutionResult executionResult = Analyze(context.Heap, context.Runtime, options);
            return ValueTask.FromResult(AnalyzerDomainResultFactory.FromExecutionResult(this, executionResult));
        }

        public AnalyzerExecutionResult Analyze(ClrHeap heap, ClrRuntime runtime, MemoryLeakOptions options)
        {
            var findings = new List<InsightFinding>(capacity: 4);

            FinalizerQueueResult finalizerResult = AnalyzeFinalizerQueue(heap);
            LeakSignals signals = AnalyzeObjectsPass(heap, options);

            AddFindings(findings, finalizerResult.TotalCount, signals, options);

            return new AnalyzerExecutionResult(
                findings,
                new MemoryLeakDomainResult(
                    finalizerResult.TotalCount,
                    signals.DuplicateStringCount,
                    signals.DuplicateStringWastedBytes,
                    signals.TotalStrings,
                    signals.TotalStringMemoryBytes,
                    signals.UniqueStrings,
                    signals.HighlyReferencedObjectCount,
                    signals.SkippedReferenceAddresses,
                    finalizerResult.TopTypes,
                    signals.TopDuplicateStrings,
                    signals.TopHighlyReferencedObjects));
        }

        private FinalizerQueueResult AnalyzeFinalizerQueue(ClrHeap heap)
        {
            // Single pass â€” no intermediate list allocation
            int finalizerCount = 0;
            var topTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            var scanCounter = new ObjectScanCounter("Finalizer queue scan", reportEveryObjects: 1000, reportEveryElapsed: TimeSpan.FromSeconds(1));

            foreach (var obj in heap.EnumerateFinalizableObjects())
            {
                scanCounter.Tick();
                finalizerCount++;

                string typeName = obj.Type?.Name ?? StringConstants.UnknownType;
                topTypes.TryGetValue(typeName, out int count);
                topTypes[typeName] = count + 1;
            }

            scanCounter.Complete();

            return new FinalizerQueueResult(
                finalizerCount,
                topTypes
                    .OrderByDescending(k => k.Value)
                    .Take(TopFinalizerTypesToShow)
                    .Select(k => new NameCountEntry(k.Key, k.Value))
                    .ToList());
        }

        private LeakSignals AnalyzeObjectsPass(ClrHeap heap, MemoryLeakOptions options)
        {
            // Single pass over heap objects â€” collects data for both string analysis and reference counting
            var stringStats = new Dictionary<StringFingerprint, StringLeakInfo>(capacity: 1024);
            int totalStrings = 0;
            ulong totalStringMemory = 0;
            var referenceCount = new Dictionary<ulong, int>(capacity: 4096);
            long skippedReferenceAddresses = 0;
            var scanCounter = new ObjectScanCounter("Memory leak object scan");

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                scanCounter.Tick();

                if (!obj.IsValid) continue;

                if (obj.Type?.Name == "System.String")
                {
                    totalStrings++;
                    totalStringMemory += obj.Size;

                    string? value = obj.AsString();
                    if (value != null && value.Length > 0 && value.Length < options.MaxDuplicateStringLength)
                    {
                        var fingerprint = CreateStringFingerprint(value);

                        if (!stringStats.TryGetValue(fingerprint, out var info))
                        {
                            info = new StringLeakInfo { Preview = CreateStringPreview(value) };
                            stringStats[fingerprint] = info;
                        }
                        info.Count++;
                        info.TotalSize += obj.Size;
                    }
                }

                foreach (ClrObject reference in obj.EnumerateReferences(carefully: true))
                {
                    if (reference.IsValid)
                    {
                        if (referenceCount.TryGetValue(reference.Address, out int count))
                        {
                            referenceCount[reference.Address] = count + 1;
                        }
                        else if (referenceCount.Count < options.MaxReferenceAddresses)
                        {
                            referenceCount[reference.Address] = 1;
                        }
                        else
                        {
                            skippedReferenceAddresses++;
                        }
                    }
                }
            }

            scanCounter.Complete();

            DuplicateStringResult duplicateResult = ComputeDuplicateStrings(stringStats, options);
            IReadOnlyList<HighlyReferencedObjectSnapshot> topHighlyReferencedObjects = ExtractHighlyReferencedObjects(heap, referenceCount, options);
            int highlyReferencedCount = CountHighlyReferencedObjects(referenceCount, options);

            return new LeakSignals(
                duplicateResult.DuplicateCount,
                duplicateResult.TotalWastedBytes,
                totalStrings,
                totalStringMemory,
                stringStats.Count,
                highlyReferencedCount,
                skippedReferenceAddresses,
                duplicateResult.TopDuplicates,
                topHighlyReferencedObjects);
        }

        private DuplicateStringResult ComputeDuplicateStrings(Dictionary<StringFingerprint, StringLeakInfo> stringStats, MemoryLeakOptions options)
        {
            var duplicates = stringStats.Values
                .Where(s => s.Count > options.MinDuplicateStringCount)
                .OrderByDescending(s => s.TotalSize)
                .Take(TopDuplicateStringsToShow)
                .ToList();

            ulong totalWastedBytes = 0;
            foreach (var dup in duplicates)
            {
                totalWastedBytes += dup.TotalSize - (dup.TotalSize / (ulong)dup.Count);
            }

            var topDuplicates = duplicates
                .Select(dup => new DuplicateStringSnapshot(
                    dup.Preview,
                    dup.Count,
                    dup.TotalSize - (dup.TotalSize / (ulong)dup.Count)))
                .ToList();

            return new DuplicateStringResult(topDuplicates.Count, totalWastedBytes, topDuplicates);
        }

        private int CountHighlyReferencedObjects(Dictionary<ulong, int> referenceCount, MemoryLeakOptions options)
        {
            return referenceCount.Count(kvp => kvp.Value > options.HighReferenceThreshold);
        }

        private void AddFindings(List<InsightFinding> findings, int finalizerCount, LeakSignals signals, MemoryLeakOptions options)
        {
            if (finalizerCount >= 1000)
            {
                findings.Add(new InsightFinding(
                    Analyzer: nameof(MemoryLeakAnalyzer),
                    Category: "Leak",
                    Severity: FindingSeverity.Critical,
                    Title: "Finalizer queue backlog is very high",
                    Evidence: $"{finalizerCount:N0} objects are waiting for finalization.",
                    Recommendation: "Investigate finalizers and implement IDisposable/using patterns to reduce finalizer pressure.",
                    Tags: ["finalizer", "memory-leak", "gc"],
                    MetricValue: finalizerCount,
                    MetricUnit: "finalizer-objects"));
            }
            else if (finalizerCount > 0)
            {
                findings.Add(new InsightFinding(
                    Analyzer: nameof(MemoryLeakAnalyzer),
                    Category: "Leak",
                    Severity: FindingSeverity.Warning,
                    Title: "Finalizer queue contains pending objects",
                    Evidence: $"{finalizerCount:N0} objects are waiting for finalization.",
                    Recommendation: "Review top finalizable types and avoid unnecessary finalizers.",
                    Tags: ["finalizer", "memory"],
                    MetricValue: finalizerCount,
                    MetricUnit: "finalizer-objects"));
            }

            if (signals.DuplicateStringCount > 0)
            {
                findings.Add(new InsightFinding(
                    Analyzer: nameof(MemoryLeakAnalyzer),
                    Category: "Optimization",
                    Severity: FindingSeverity.Warning,
                    Title: "High duplicate string pressure detected",
                    Evidence: $"{signals.DuplicateStringCount:N0} duplicate string patterns with ~{FormatHelper.FormatBytes(signals.DuplicateStringWastedBytes)} estimated waste.",
                    Recommendation: "Consider string interning/pooling or de-duplicating repeated payloads.",
                    Tags: ["string", "memory", "allocation"],
                    MetricValue: signals.DuplicateStringWastedBytes,
                    MetricUnit: "wasted-bytes"));
            }

            if (signals.HighlyReferencedObjectCount > 0)
            {
                var severity = signals.HighlyReferencedObjectCount >= 10 ? FindingSeverity.Critical : FindingSeverity.Warning;
                findings.Add(new InsightFinding(
                    Analyzer: nameof(MemoryLeakAnalyzer),
                    Category: "Leak",
                    Severity: severity,
                    Title: "Highly referenced objects detected",
                    Evidence: $"{signals.HighlyReferencedObjectCount:N0} objects exceeded {options.HighReferenceThreshold:N0} incoming references.",
                    Recommendation: "Inspect root paths and long-lived graphs retaining these objects.",
                    Tags: ["retention", "references", "memory-leak"],
                    MetricValue: signals.HighlyReferencedObjectCount,
                    MetricUnit: "objects"));
            }

            if (signals.SkippedReferenceAddresses > 0)
            {
                findings.Add(new InsightFinding(
                    Analyzer: nameof(MemoryLeakAnalyzer),
                    Category: "Diagnostics",
                    Severity: FindingSeverity.Info,
                    Title: "Reference tracking was capped",
                    Evidence: $"Skipped {signals.SkippedReferenceAddresses:N0} references after hitting {options.MaxReferenceAddresses:N0} tracked addresses.",
                    Recommendation: "Increase MaxReferenceAddressesToTrack for deeper incoming-reference coverage.",
                    Tags: ["analysis-quality", "references"],
                    MetricValue: signals.SkippedReferenceAddresses,
                    MetricUnit: "references"));
            }
        }

        private static StringFingerprint CreateStringFingerprint(string value)
        {
            const ulong fnvOffset = 14695981039346656037UL;
            const ulong fnvPrime = 1099511628211UL;

            ulong hash = fnvOffset;
            foreach (char c in value)
            {
                hash ^= c;
                hash *= fnvPrime;
            }

            return new StringFingerprint(hash, value.Length, value[0], value[^1]);
        }

        private static string CreateStringPreview(string value)
        {
            string preview = value.Length > 47 ? value.Substring(0, 47) + "..." : value;
            return preview.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private IReadOnlyList<HighlyReferencedObjectSnapshot> ExtractHighlyReferencedObjects(ClrHeap heap, Dictionary<ulong, int> referenceCount, MemoryLeakOptions options)
        {
            var topAddresses = referenceCount
                .Where(kvp => kvp.Value > options.HighReferenceThreshold)
                .OrderByDescending(kvp => kvp.Value)
                .Take(TopHighlyReferencedObjectsToShow)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            if (topAddresses.Count == 0)
                return [];

            var results = new List<HighlyReferencedObjectSnapshot>(topAddresses.Count);

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid)
                    continue;

                if (!topAddresses.TryGetValue(obj.Address, out int incomingReferences))
                    continue;

                results.Add(new HighlyReferencedObjectSnapshot(
                    obj.Address,
                    obj.Type?.Name ?? StringConstants.UnknownType,
                    obj.Size,
                    incomingReferences));

                if (results.Count == topAddresses.Count)
                    break;
            }

            return results
                .OrderByDescending(r => r.IncomingReferences)
                .ToList();
        }

        private readonly record struct StringFingerprint(ulong Hash, int Length, char FirstChar, char LastChar);
        private readonly record struct DuplicateStringResult(int DuplicateCount, ulong TotalWastedBytes, IReadOnlyList<DuplicateStringSnapshot> TopDuplicates);
        private readonly record struct LeakSignals(
            int DuplicateStringCount,
            ulong DuplicateStringWastedBytes,
            int TotalStrings,
            ulong TotalStringMemoryBytes,
            int UniqueStrings,
            int HighlyReferencedObjectCount,
            long SkippedReferenceAddresses,
            IReadOnlyList<DuplicateStringSnapshot> TopDuplicateStrings,
            IReadOnlyList<HighlyReferencedObjectSnapshot> TopHighlyReferencedObjects);
        private readonly record struct FinalizerQueueResult(int TotalCount, IReadOnlyList<NameCountEntry> TopTypes);
    }
}


