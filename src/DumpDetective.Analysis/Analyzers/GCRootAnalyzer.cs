using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Analyzers
{
    /// <summary>
    /// Phase-2 analyzer: unified GC root intelligence covering all root kinds
    /// (Stack, Static/StrongHandle, Finalizer, Pinned, etc.) with retention estimates
    /// and bounded BFS path tracing for the top suspects.
    ///
    /// Root data is sourced entirely from Phase 1:
    ///   Memory mode — <see cref="HeapIndexBuildResult.InMemoryRootCandidates"/>
    ///   Disk mode   — reads <c>RootIndex.bin</c> from the dump index directory
    ///
    /// No direct <c>heap.EnumerateRoots()</c> call is made in Phase 2.
    /// </summary>
    public sealed class GCRootAnalyzer : IAnalyzer
    {
        private const int TopSeverityLimit  = 20;
        private const int PathSearchTopN    = 25;
        private const int MaxBfsNodes       = 500;
        private const int MaxBfsDepth       = 20;

        // RootIndex.bin binary layout constants
        private const int  RootRecordSize   = 20; // TargetAddr(8) | RootAddr(8) | Kind(1) | Pad(3)
        private const int  RootHeaderMagic  = 0x58495452; // "RTIX"
        private const int  RootHeaderVersion = 1;
        private const long RootHeaderSize    = 24; // see IndexHeader

        public string Name => "GC Root Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, cancellationToken).Stamp(this));
        }

        private static AnalyzerDomainResult Analyze(
            ClrHeap heap,
            IHeapAnalysisCache cache,
            CancellationToken cancellationToken)
        {
            if (cache is not HeapAnalysisCache heapCache
                || !heapCache.TryGetHeapIndex(out HeapIndexBuildResult? idx))
            {
                return EmptyResult();
            }

            // ── Step 1: Read all roots from Phase 1 index ──────────────────────
            var roots = ReadRoots(idx, cancellationToken);
            if (roots.Count == 0)
                return EmptyResult();

            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates = idx.TypeAggregates;

            // ── Step 2: Compute total managed heap bytes for PctOfManagedHeap ──
            ulong totalHeapBytes = 0;
            foreach (TypeAggregateIndexEntry agg in aggregates.Values)
                totalHeapBytes += agg.TotalSize;

            // ── Step 3: Group roots by kind, compute per-kind retained estimate ─
            var kindCounts   = new Dictionary<string, int>(8);
            var kindBytes    = new Dictionary<string, ulong>(8);

            foreach (var root in roots)
            {
                string kind = KindToString(root.Kind);
                kindCounts[kind] = (kindCounts.TryGetValue(kind, out int c) ? c : 0) + 1;

                // Estimate retained bytes for this root: avg size of target's type
                ulong estimate = EstimateRetainedBytes(root.TargetAddr, heap, aggregates);
                kindBytes[kind] = (kindBytes.TryGetValue(kind, out ulong b) ? b : 0UL) + estimate;
            }

            var byKind = new List<RootKindSummary>(kindCounts.Count);
            foreach (var kv in kindCounts)
            {
                string kind = kv.Key;
                ulong  estBytes = kindBytes.TryGetValue(kind, out ulong kb) ? kb : 0UL;
                double pct = totalHeapBytes > 0 ? (double)estBytes / totalHeapBytes * 100.0 : 0.0;
                byKind.Add(new RootKindSummary(kind, kv.Value, estBytes, pct));
            }
            byKind.Sort(static (a, b) => b.EstimatedRetainedBytes.CompareTo(a.EstimatedRetainedBytes));

            // ── Step 4: Build per-root findings and score ──────────────────────
            var findings = new List<RootFinding>(Math.Min(roots.Count, TopSeverityLimit * 4));

            for (int i = 0; i < roots.Count; i++)
            {
                var root = roots[i];
                ulong estimate = EstimateRetainedBytes(root.TargetAddr, heap, aggregates);
                if (estimate == 0)
                    continue;

                string kind = KindToString(root.Kind);
                string targetType = ResolveTypeName(root.TargetAddr, heap, aggregates);
                int severity = ComputeSeverity(estimate, kind);

                findings.Add(new RootFinding(
                    RootKind: kind,
                    RootAddress: root.RootAddr,
                    FieldDescription: null,
                    TargetTypeName: targetType,
                    TargetAddress: root.TargetAddr,
                    EstimatedRetainedBytes: estimate,
                    SeverityScore: severity));
            }

            findings.Sort(static (a, b) => b.SeverityScore.CompareTo(a.SeverityScore));
            int topCount = Math.Min(findings.Count, TopSeverityLimit);
            var topFindings = findings.Count <= TopSeverityLimit
                ? (IReadOnlyList<RootFinding>)findings
                : findings.GetRange(0, topCount);

            // ── Step 5: BFS path tracing for top-N roots ──────────────────────
            int pathCappedCount = 0;
            var pathFindings    = new List<RootPathFinding>(Math.Min(findings.Count, PathSearchTopN));
            int pathN           = Math.Min(findings.Count, PathSearchTopN);

            for (int i = 0; i < pathN; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var f = findings[i];

                bool wasCapped = false;
                var pathTypes = BfsForwardPath(heap, f.TargetAddress, MaxBfsNodes, MaxBfsDepth, out wasCapped);

                if (wasCapped)
                    pathCappedCount++;

                pathFindings.Add(new RootPathFinding(
                    TargetAddress:  f.TargetAddress,
                    TargetTypeName: f.TargetTypeName,
                    RootKind:       f.RootKind,
                    PathTypeNames:  pathTypes,
                    PathLength:     pathTypes.Count,
                    WasCapped:      wasCapped));
            }

            return new GCRootDomainResult(
                TotalRoots:          roots.Count,
                ByKind:              byKind,
                TopRootsBySeverity:  topFindings,
                RootPaths:           pathFindings,
                PathSearchCapped:    pathCappedCount > 0,
                PathSearchCappedCount: pathCappedCount);
        }

        // ── Root reading ──────────────────────────────────────────────────────

        private static List<(ulong TargetAddr, ulong RootAddr, byte Kind)> ReadRoots(
            HeapIndexBuildResult idx,
            CancellationToken cancellationToken)
        {
            // Memory mode — use pre-built in-memory candidates
            if (idx.StorageKind == HeapIndexStorageKind.Memory && idx.InMemoryRootCandidates is { } candidates)
            {
                var result = new List<(ulong, ulong, byte)>(candidates.Length);
                foreach (var item in candidates)
                    result.Add(item);
                return result;
            }

            // Disk mode — read RootIndex.bin
            string rootPath = DumpIndexPaths.RootIndex(idx.IndexPath);
            if (!File.Exists(rootPath))
                return new List<(ulong, ulong, byte)>();

            return ReadRootIndexBin(rootPath, cancellationToken);
        }

        private static List<(ulong TargetAddr, ulong RootAddr, byte Kind)> ReadRootIndexBin(
            string filePath,
            CancellationToken cancellationToken)
        {
            var roots = new List<(ulong, ulong, byte)>(capacity: 16_384);

            using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 256 * 1024, FileOptions.SequentialScan);

            // Validate header
            Span<byte> headerBuf = stackalloc byte[24];
            if (fs.Read(headerBuf) < 24)
                return roots;

            int magic   = BinaryPrimitives.ReadInt32LittleEndian(headerBuf);
            int version = BinaryPrimitives.ReadInt32LittleEndian(headerBuf[4..]);
            if (magic != RootHeaderMagic || version != RootHeaderVersion)
                return roots;

            long recordCount = BinaryPrimitives.ReadInt64LittleEndian(headerBuf[8..]);
            if (recordCount <= 0)
                return roots;

            byte[] buf = ArrayPool<byte>.Shared.Rent(RootRecordSize * 4096);
            try
            {
                int bytesRead;
                while ((bytesRead = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int records = bytesRead / RootRecordSize;
                    for (int i = 0; i < records; i++)
                    {
                        int off = i * RootRecordSize;
                        ulong target = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off));
                        ulong rootA  = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off + 8));
                        byte  kind   = buf[off + 16];
                        roots.Add((target, rootA, kind));
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }

            return roots;
        }

        // ── BFS path tracing ──────────────────────────────────────────────────

        /// <summary>
        /// Forward BFS from <paramref name="startAddr"/>. Returns the distinct type names
        /// encountered in BFS order (excluding the start object itself), bounded by
        /// <paramref name="maxNodes"/> and <paramref name="maxDepth"/>.
        /// </summary>
        private static IReadOnlyList<string> BfsForwardPath(
            ClrHeap heap,
            ulong startAddr,
            int maxNodes,
            int maxDepth,
            out bool wasCapped)
        {
            wasCapped = false;
            if (startAddr == 0)
                return [];

            var visited   = new HashSet<ulong>(capacity: 64) { startAddr };
            var queue     = new Queue<(ulong Addr, int Depth)>(capacity: 64);
            var typeNames = new List<string>(capacity: 16);

            queue.Enqueue((startAddr, 0));
            int nodesVisited = 0;

            while (queue.Count > 0)
            {
                var (addr, depth) = queue.Dequeue();
                nodesVisited++;

                if (nodesVisited > maxNodes || depth >= maxDepth)
                {
                    wasCapped = true;
                    break;
                }

                ClrObject obj = heap.GetObject(addr);
                if (!obj.IsValid || obj.Type is null)
                    continue;

                if (depth > 0 && obj.Type.Name is string name)
                {
                    // Deduplicate type names in path — skip already seen types.
                    if (typeNames.Count == 0 || typeNames[typeNames.Count - 1] != name)
                        typeNames.Add(name);
                }

                foreach (ClrObject child in obj.EnumerateReferences(carefully: true))
                {
                    if (child.IsValid && child.Address != 0 && visited.Add(child.Address))
                        queue.Enqueue((child.Address, depth + 1));
                }
            }

            return typeNames;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static ulong EstimateRetainedBytes(
            ulong targetAddr,
            ClrHeap heap,
            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates)
        {
            if (targetAddr == 0)
                return 0;

            try
            {
                ClrObject obj = heap.GetObject(targetAddr);
                if (!obj.IsValid || obj.Type is null)
                    return 0;

                ulong mt = obj.Type.MethodTable;
                if (mt != 0 && aggregates.TryGetValue(mt, out TypeAggregateIndexEntry agg) && agg.Count > 0)
                    return agg.TotalSize / (ulong)agg.Count; // avg size as single-object estimate

                return obj.Size;
            }
            catch
            {
                return 0;
            }
        }

        private static string ResolveTypeName(
            ulong targetAddr,
            ClrHeap heap,
            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates)
        {
            if (targetAddr == 0)
                return "(unknown)";
            try
            {
                ClrObject obj = heap.GetObject(targetAddr);
                if (obj.IsValid && obj.Type?.Name is string name)
                    return name;
            }
            catch { }
            return $"0x{targetAddr:X}";
        }

        private static int ComputeSeverity(ulong retainedBytes, string kind)
        {
            // Base score from retained size (log-scaled)
            int baseScore = retainedBytes switch
            {
                >= 100_000_000 => 100,
                >= 10_000_000  => 80,
                >= 1_000_000   => 60,
                >= 100_000     => 40,
                >= 10_000      => 20,
                _              => 5
            };

            // Kind multiplier: static roots are hardest to release
            int multiplier = kind switch
            {
                "StrongHandle" => 3,  // static / global
                "FinalizerQueue" => 2,
                "PinnedHandle"   => 2,
                "Stack"          => 1,
                _                => 1
            };

            return Math.Min(baseScore * multiplier, 300);
        }

        private static string KindToString(byte kind)
        {
            // ClrRootKind enum byte values
            return kind switch
            {
                0 => "None",
                1 => "FinalizerQueue",
                2 => "StrongHandle",
                3 => "PinnedHandle",
                4 => "Stack",
                5 => "RefCountedHandle",
                6 => "AsyncPinnedHandle",
                7 => "SizedRefHandle",
                _ => $"Unknown({kind})"
            };
        }

        private static GCRootDomainResult EmptyResult() =>
            new(TotalRoots: 0,
                ByKind: [],
                TopRootsBySeverity: [],
                RootPaths: [],
                PathSearchCapped: false,
                PathSearchCappedCount: 0);
    
        public void Dispose() { }
        }
    }
