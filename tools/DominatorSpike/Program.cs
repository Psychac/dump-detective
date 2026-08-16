using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;

// Rollout Plan step 1 spike for
// docs/analysis/phase1-redesigns/dominator-tree-lengauer-tarjan.md
//
// Option A (whole-live-heap graph, generation used only as a report-time filter — see
// "Finding 2" / Option A decision in the design doc). Node set = every live object reachable
// from a GC root, regardless of generation. Measures, without implementing Lengauer-Tarjan
// itself:
//   - N: whole-live-heap node count (Node Cap sizing)
//   - Forward-walk wall-clock cost, fused with BFS reachability from GC roots
//   - Unreachable population: live objects with no path from any root (should be ~0 by the
//     live-heap invariant — any non-zero count here is a bug in this tool, not a real gap,
//     unlike the earlier Gen2+LOH-scoped run)
//   - Whole-graph in-degree distribution / max in-degree (fanout-cap risk for any reuse of the
//     disk reverse index's 10K-parents-per-child cap)
//   - Gen2+LOH share of the whole-graph node/edge/byte totals, for report-filter sizing
//
// Single dump load, two logical passes (classify, then BFS) — no per-edge storage, no CSR
// build; forward refs are re-walked lazily per visited node exactly once, matching the
// project's "compute forward refs lazily from ClrObject fields" rule.

// mode:
//   full        (default) — BFS from roots, full bookkeeping (visited set, in-degree dict) — this
//                is the number the design doc's wall-clock estimate is based on.
//   walkonly    — sequential pass over every live object, heap.GetObject + EnumerateReferences,
//                no containment checks, no dictionaries — isolates raw ClrMD per-object walk cost.
//   walkcontains — walkonly + an allLive.Contains(target) check per edge (no dictionary) — isolates
//                HashSet lookup cost on top of the raw walk.
//   packed      — real packed-array prototype for design doc §Phase 1/2/3: dense int ids via a
//                custom open-addressed ulong->int map (not Dictionary<ulong,int>), CSR forward +
//                reverse edge arrays (two-pass: count degrees, then fill), generation tag array.
//                Measures actual wall-clock/memory for the structures the design specifies,
//                instead of the ablation-based estimate from walkonly/full.
//   packed1     — same as packed but single-pass edge capture + O(N+E) counting-sort CSR build
//                (no second ClrMD walk) — the corrected, measured-faster design (see doc §D4).
//   diskindex   — design doc §D5 spike: simulates "Phase 1 also persists a forward-edge index."
//                Pass 1: walk EVERY live object once (not just reachable), capture forward edges —
//                this is the proxy for the one-time Phase 1 extraction cost. Pass 2: build the
//                whole-heap forward CSR (O(N+E), no ClrMD). Pass 3: in-memory BFS over that CSR
//                from roots — zero ClrMD calls, pure array traversal — to find the reachable
//                subgraph (this is what DominatorAnalyzer's Phase 1/2 would cost once the index is
//                loaded from disk). Pass 4: invert the reachable edges into a reverse CSR, same way.
//                Reports both the one-time "index build" cost and the near-free "consumption" cost
//                separately, and cross-checks N/E/max-in-degree against the packed1 numbers already
//                in the design doc to validate that forward-then-invert produces an identical
//                reachable subgraph to building the reverse directly via live BFS.
// Comparing throughput (nodes/sec) across modes attributes the "full" mode's wall-clock cost
// between {raw ClrMD reads, HashSet lookups, BFS-specific bookkeeping (visited.Add/Enqueue,
// in-degree Dictionary)}.
string dumpPath = args.Length > 0
    ? args[0]
    : throw new ArgumentException("Usage: DominatorSpike <dump-path> [full|walkonly|walkcontains|packed|packed1|diskindex]");
string mode = args.Length > 1 ? args[1] : "full";

if (!File.Exists(dumpPath))
{
    Console.Error.WriteLine($"Dump not found: {dumpPath}");
    return 1;
}

var fileInfo = new FileInfo(dumpPath);
Console.WriteLine(new string('=', 70));
Console.WriteLine("Dominator Tree Spike — Option A: Whole-Live-Heap Graph");
Console.WriteLine(new string('=', 70));
Console.WriteLine($"Dump: {Path.GetFileName(dumpPath)} ({fileInfo.Length / (1024.0 * 1024 * 1024):F2} GB)");
Console.WriteLine($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

try
{
    var options = new DataTargetOptions { UseLockFreeMemoryMapReader = true };

    Stopwatch loadSw = Stopwatch.StartNew();
    using DataTarget dt = DataTarget.LoadDump(dumpPath, options);
    ClrRuntime rt = dt.ClrVersions[0].CreateRuntime();
    ClrHeap heap = rt.Heap;
    loadSw.Stop();
    Console.WriteLine($"\nDump loaded in {loadSw.Elapsed.TotalSeconds:F2}s");

    // --- Pass 1: enumerate all live objects; tag Gen2/LOH/POH/Frozen for report-filter sizing --
    Console.WriteLine("\n--- Pass 1: whole-heap enumeration + generation tagging ---");

    Stopwatch pass1Sw = Stopwatch.StartNew();
    long totalObjects = 0;
    ulong totalHeapBytes = 0;
    ulong gen2LohBytes = 0;
    var allLive = new HashSet<ulong>(capacity: 1 << 20);
    var gen2Loh = new HashSet<ulong>(capacity: 1 << 20);

    foreach (ClrObject obj in heap.EnumerateObjects())
    {
        totalObjects++;
        if (totalObjects % 1_000_000 == 0)
            Console.Write($"\r  Objects: {totalObjects:N0}");

        if (!obj.IsValid || obj.Type is null)
            continue;

        totalHeapBytes += obj.Size;
        allLive.Add(obj.Address);

        if (IsGen2LohPohFrozen(heap, obj.Address))
        {
            gen2Loh.Add(obj.Address);
            gen2LohBytes += obj.Size;
        }
    }

    pass1Sw.Stop();
    Console.WriteLine($"\r  Objects scanned: {totalObjects:N0}                                   ");
    Console.WriteLine($"Pass 1 complete in {pass1Sw.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine($"  Total live objects:      {totalObjects:N0} ({FormatBytes(totalHeapBytes)})");
    Console.WriteLine($"  Gen2/LOH/POH/Frozen tag: {gen2Loh.Count:N0} ({FormatBytes(gen2LohBytes)}) — report-filter share, "
        + $"{(totalObjects > 0 ? gen2Loh.Count * 100.0 / totalObjects : 0):F1}% of objects, "
        + $"{(totalHeapBytes > 0 ? gen2LohBytes * 100.0 / totalHeapBytes : 0):F1}% of bytes");
    Console.WriteLine($"  Working-set estimate (whole-heap node cap): ~{FormatBytes((ulong)allLive.Count * 48)} for Phase 1 id maps alone "
        + $"(before LT's 7 int[] arrays add ~{FormatBytes((ulong)allLive.Count * 28)} more)");

    if (mode == "packed")
    {
        Console.WriteLine("\n--- Packed dense-id reachability build (Phase 1/2/3 prototype) ---");

        var idMap = new DenseIdMap(1 << 20);
        var addresses = new List<ulong>(capacity: 1 << 20);
        var outDegree = new List<int>(capacity: 1 << 20);
        var inDegreeList = new List<int>(capacity: 1 << 20);
        var genTag = new List<byte>(capacity: 1 << 20);
        var frontier2 = new Queue<int>();

        (int id, bool isNew) GetOrAddId(ulong addr)
        {
            if (idMap.TryGetValue(addr, out int existing))
                return (existing, false);

            int newId = addresses.Count;
            idMap.Add(addr, newId);
            addresses.Add(addr);
            outDegree.Add(0);
            inDegreeList.Add(0);
            genTag.Add(IsGen2LohPohFrozen(heap, addr) ? (byte)1 : (byte)0);
            return (newId, true);
        }

        Stopwatch rootSw2 = Stopwatch.StartNew();
        long rootCount2 = 0;
        long resolvedRootCount2 = 0;
        foreach (ClrRoot root in heap.EnumerateRoots())
        {
            rootCount2++;
            ulong target = root.Object.Address;
            // Validate the root resolves to a real live object before assigning it a node id —
            // matches Round 2/diskindex's stricter check. An earlier version of this mode accepted
            // any nonzero root address unconditionally, silently counting a handful of
            // invalid/phantom addresses as reachable nodes (caught via §D5's diskindex cross-check
            // against this same figure — see design doc §D5).
            if (target == 0 || !heap.GetObject(target).IsValid)
                continue;

            resolvedRootCount2++;
            (int id, bool isNew) = GetOrAddId(target);
            if (isNew)
                frontier2.Enqueue(id);
        }
        rootSw2.Stop();
        Console.WriteLine($"Roots enumerated: {rootCount2:N0} in {rootSw2.Elapsed.TotalSeconds:F2}s "
            + $"({resolvedRootCount2:N0} non-null)");

        // Phase 1/2, Pass A — reachability discovery + out/in-degree counting. Dense ids assigned
        // on first discovery via the open-addressed map (not Dictionary<ulong,int>).
        Stopwatch passASw = Stopwatch.StartNew();
        long passANodes = 0;
        long passAEdges = 0;
        while (frontier2.Count > 0)
        {
            int id = frontier2.Dequeue();
            passANodes++;
            if (passANodes % 1_000_000 == 0)
                Console.Write($"\r  Pass A: {passANodes:N0}, edges: {passAEdges:N0}");

            ulong address = addresses[id];
            ClrObject obj = heap.GetObject(address);
            if (!obj.IsValid || obj.Type is null)
                continue;

            int localOutDegree = 0;
            foreach (ClrObject child in obj.EnumerateReferences(carefully: true))
            {
                if (!child.IsValid || child.Address == 0)
                    continue;

                passAEdges++;
                localOutDegree++;
                (int childId, bool isNew) = GetOrAddId(child.Address);
                inDegreeList[childId] = inDegreeList[childId] + 1;
                if (isNew)
                    frontier2.Enqueue(childId);
            }
            outDegree[id] = localOutDegree;
        }
        passASw.Stop();
        int nodeCountPacked = addresses.Count;
        Console.WriteLine($"\r  Pass A: {passANodes:N0}, edges: {passAEdges:N0}                                  ");
        Console.WriteLine($"Pass A (discovery + degree count) complete in {passASw.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"  Reachable nodes (N):  {nodeCountPacked:N0}");
        Console.WriteLine($"  Edges (E):            {passAEdges:N0}");
        double passAThroughput = passASw.Elapsed.TotalSeconds > 0 ? passANodes / passASw.Elapsed.TotalSeconds : 0;
        Console.WriteLine($"  Throughput:            {passAThroughput:N0} nodes/sec");

        // Phase 2/3, Pass B — CSR fill (forward + reverse) from precomputed degree offsets.
        // Re-walks each node's references a second time (this is the real, measured cost of the
        // two-pass CSR build design already specified in the doc, not included in the walkonly
        // ablation).
        Stopwatch csrBuildSw = Stopwatch.StartNew();
        int[] fwdOffset = new int[nodeCountPacked + 1];
        int[] revOffset = new int[nodeCountPacked + 1];
        for (int i = 0; i < nodeCountPacked; i++)
        {
            fwdOffset[i + 1] = fwdOffset[i] + outDegree[i];
            revOffset[i + 1] = revOffset[i] + inDegreeList[i];
        }
        long edgeCount = fwdOffset[nodeCountPacked];
        int[] fwdTargets = new int[edgeCount];
        int[] revSources = new int[edgeCount];
        int[] fwdCursor = (int[])fwdOffset.Clone();
        int[] revCursor = (int[])revOffset.Clone();
        csrBuildSw.Stop();

        Stopwatch passBSw = Stopwatch.StartNew();
        long passBNodes = 0;
        for (int id = 0; id < nodeCountPacked; id++)
        {
            passBNodes++;
            if (passBNodes % 1_000_000 == 0)
                Console.Write($"\r  Pass B: {passBNodes:N0} / {nodeCountPacked:N0}");

            ulong address = addresses[id];
            ClrObject obj = heap.GetObject(address);
            if (!obj.IsValid || obj.Type is null)
                continue;

            foreach (ClrObject child in obj.EnumerateReferences(carefully: true))
            {
                if (!child.IsValid || child.Address == 0)
                    continue;

                if (!idMap.TryGetValue(child.Address, out int childId))
                    continue; // should always resolve — every edge target was discovered in Pass A

                fwdTargets[fwdCursor[id]++] = childId;
                revSources[revCursor[childId]++] = id;
            }
        }
        passBSw.Stop();
        Console.WriteLine($"\r  Pass B: {passBNodes:N0} / {nodeCountPacked:N0}                                  ");
        Console.WriteLine($"CSR offset build in {csrBuildSw.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"Pass B (CSR fill, second walk) complete in {passBSw.Elapsed.TotalSeconds:F2}s");
        double passBThroughput = passBSw.Elapsed.TotalSeconds > 0 ? passBNodes / passBSw.Elapsed.TotalSeconds : 0;
        Console.WriteLine($"  Throughput:            {passBThroughput:N0} nodes/sec");

        long idMapBytes = idMap.EstimatedBytes;
        long addressesBytes = (long)addresses.Capacity * 8;
        long degreeBytes = (long)outDegree.Capacity * 4 + (long)inDegreeList.Capacity * 4;
        long genTagBytes = genTag.Capacity;
        long offsetBytes = (long)(fwdOffset.Length + revOffset.Length) * 4;
        long csrBytes = edgeCount * 4 * 2; // fwdTargets + revSources, int each

        Console.WriteLine("\n--- Packed structures: measured memory breakdown ---");
        Console.WriteLine($"  DenseIdMap (ulong->int, open-addressed):  {FormatBytes((ulong)idMapBytes)}");
        Console.WriteLine($"  addresses[] (List<ulong>, capacity):      {FormatBytes((ulong)addressesBytes)}");
        Console.WriteLine($"  outDegree[]+inDegree[] (List<int> x2):    {FormatBytes((ulong)degreeBytes)}");
        Console.WriteLine($"  genTag[] (List<byte>):                    {FormatBytes((ulong)genTagBytes)}");
        Console.WriteLine($"  fwdOffset[]+revOffset[] (int[N+1] x2):    {FormatBytes((ulong)offsetBytes)}");
        Console.WriteLine($"  fwdTargets[]+revSources[] (int[E] x2):    {FormatBytes((ulong)csrBytes)}");
        long structuralTotal = idMapBytes + addressesBytes + degreeBytes + genTagBytes + offsetBytes + csrBytes;
        Console.WriteLine($"  Structural total (analytic):              {FormatBytes((ulong)structuralTotal)}");

        long managedBytesPacked = GC.GetTotalMemory(forceFullCollection: true);
        Console.WriteLine($"  Managed memory at end of run (GC.GetTotalMemory): {FormatBytes((ulong)managedBytesPacked)}");

        Console.WriteLine("\n--- Summary ---");
        double totalPackedSeconds = (loadSw.Elapsed + pass1Sw.Elapsed + rootSw2.Elapsed + passASw.Elapsed
            + csrBuildSw.Elapsed + passBSw.Elapsed).TotalSeconds;
        Console.WriteLine($"  Pass A + Pass B (the two-pass reachability+CSR cost):  {(passASw.Elapsed + passBSw.Elapsed).TotalSeconds:F2}s");
        Console.WriteLine($"  Total wall clock (incl. dump load, Pass 1 tagging):    {totalPackedSeconds:F2}s");

        return 0;
    }

    if (mode == "packed1")
    {
        // Single-pass variant: capture (fromId, toId) edges into a flat growable buffer during
        // ONE ClrMD walk (instead of "packed" mode's count-then-fill two-walk design), then build
        // the CSR arrays via an O(N+E) counting-sort redistribution pass with no further dump
        // reads. Tests whether avoiding the second ClrMD walk is worth the temporary edge-list
        // memory (2x 4 bytes/edge, freed before final measurement).
        Console.WriteLine("\n--- Packed dense-id reachability build, single-pass edge capture ---");

        var idMap = new DenseIdMap(1 << 20);
        var addresses = new List<ulong>(capacity: 1 << 20);
        var outDegree = new List<int>(capacity: 1 << 20);
        var inDegreeList = new List<int>(capacity: 1 << 20);
        var genTag = new List<byte>(capacity: 1 << 20);
        var frontier2 = new Queue<int>();
        var edgeFrom = new List<int>(capacity: 1 << 22);
        var edgeTo = new List<int>(capacity: 1 << 22);

        (int id, bool isNew) GetOrAddId(ulong addr)
        {
            if (idMap.TryGetValue(addr, out int existing))
                return (existing, false);

            int newId = addresses.Count;
            idMap.Add(addr, newId);
            addresses.Add(addr);
            outDegree.Add(0);
            inDegreeList.Add(0);
            genTag.Add(IsGen2LohPohFrozen(heap, addr) ? (byte)1 : (byte)0);
            return (newId, true);
        }

        Stopwatch rootSw3 = Stopwatch.StartNew();
        long rootCount3 = 0;
        foreach (ClrRoot root in heap.EnumerateRoots())
        {
            rootCount3++;
            ulong target = root.Object.Address;
            // Validate liveness before assigning a node id — see the matching fix/comment in
            // "packed" mode's root loop above; this is the same bug (fixed here too).
            if (target == 0 || !heap.GetObject(target).IsValid)
                continue;

            (int id, bool isNew) = GetOrAddId(target);
            if (isNew)
                frontier2.Enqueue(id);
        }
        rootSw3.Stop();
        Console.WriteLine($"Roots enumerated: {rootCount3:N0} in {rootSw3.Elapsed.TotalSeconds:F2}s");

        Stopwatch walkSw2 = Stopwatch.StartNew();
        long walkNodes2 = 0;
        while (frontier2.Count > 0)
        {
            int id = frontier2.Dequeue();
            walkNodes2++;
            if (walkNodes2 % 1_000_000 == 0)
                Console.Write($"\r  Walked: {walkNodes2:N0}, edges: {edgeFrom.Count:N0}");

            ulong address = addresses[id];
            ClrObject obj = heap.GetObject(address);
            if (!obj.IsValid || obj.Type is null)
                continue;

            foreach (ClrObject child in obj.EnumerateReferences(carefully: true))
            {
                if (!child.IsValid || child.Address == 0)
                    continue;

                (int childId, bool isNew) = GetOrAddId(child.Address);
                edgeFrom.Add(id);
                edgeTo.Add(childId);
                outDegree[id] = outDegree[id] + 1;
                inDegreeList[childId] = inDegreeList[childId] + 1;
                if (isNew)
                    frontier2.Enqueue(childId);
            }
        }
        walkSw2.Stop();
        int nodeCountPacked1 = addresses.Count;
        long edgeCountPacked1 = edgeFrom.Count;
        Console.WriteLine($"\r  Walked: {walkNodes2:N0}, edges: {edgeCountPacked1:N0}                                  ");
        Console.WriteLine($"Single walk (discovery + edge capture) complete in {walkSw2.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"  Reachable nodes (N):  {nodeCountPacked1:N0}");
        Console.WriteLine($"  Edges (E):            {edgeCountPacked1:N0}");
        double walkThroughput2 = walkSw2.Elapsed.TotalSeconds > 0 ? walkNodes2 / walkSw2.Elapsed.TotalSeconds : 0;
        Console.WriteLine($"  Throughput:            {walkThroughput2:N0} nodes/sec");

        // O(N+E) CSR redistribution — no ClrMD reads, pure array/counting-sort work.
        Stopwatch csrSw = Stopwatch.StartNew();
        int[] fwdOffset = new int[nodeCountPacked1 + 1];
        int[] revOffset = new int[nodeCountPacked1 + 1];
        for (int i = 0; i < nodeCountPacked1; i++)
        {
            fwdOffset[i + 1] = fwdOffset[i] + outDegree[i];
            revOffset[i + 1] = revOffset[i] + inDegreeList[i];
        }
        int[] fwdTargets = new int[edgeCountPacked1];
        int[] revSources = new int[edgeCountPacked1];
        int[] fwdCursor = (int[])fwdOffset.Clone();
        int[] revCursor = (int[])revOffset.Clone();
        for (int e = 0; e < edgeCountPacked1; e++)
        {
            int from = edgeFrom[e];
            int to = edgeTo[e];
            fwdTargets[fwdCursor[from]++] = to;
            revSources[revCursor[to]++] = from;
        }
        csrSw.Stop();
        Console.WriteLine($"CSR redistribution (no dump reads) complete in {csrSw.Elapsed.TotalSeconds:F2}s");

        // Free the temporary edge-list buffers before measuring final memory — a real
        // implementation would never hold both the edge list and the CSR arrays at once past this
        // point.
        edgeFrom.Clear();
        edgeFrom.Capacity = 0;
        edgeTo.Clear();
        edgeTo.Capacity = 0;

        // Open Question 5 spike: pure graph-structural leaf-fold-in candidates — nodes with zero
        // out-degree (can't dominate anything) AND exactly one in-degree (idom is trivially that
        // sole parent, no LT needed) — the population that could skip a full node/LT-array slot
        // entirely. No ClrType/MethodTableHasOutgoingRefs lookup needed; out/in-degree are already
        // known from the CSR degree arrays.
        long leafCount = 0;             // outDegree == 0 (any in-degree)
        long foldableLeafCount = 0;     // outDegree == 0 AND inDegree == 1 (the safe shortcut)
        long sharedLeafCount = 0;       // outDegree == 0 AND inDegree > 1 (needs real LT, no shortcut)
        for (int i = 0; i < nodeCountPacked1; i++)
        {
            if (outDegree[i] != 0)
                continue;

            leafCount++;
            if (inDegreeList[i] == 1)
                foldableLeafCount++;
            else if (inDegreeList[i] > 1)
                sharedLeafCount++;
        }
        Console.WriteLine("\n--- Open Question 5: leaf fold-in candidates (graph-structural, no type lookup) ---");
        Console.WriteLine($"  Leaves (out-degree 0):                    {leafCount:N0} ({(nodeCountPacked1 > 0 ? leafCount * 100.0 / nodeCountPacked1 : 0):F1}% of N)");
        Console.WriteLine($"  Foldable (out-degree 0, in-degree 1):     {foldableLeafCount:N0} ({(nodeCountPacked1 > 0 ? foldableLeafCount * 100.0 / nodeCountPacked1 : 0):F1}% of N) — safe to skip, no LT slot needed");
        Console.WriteLine($"  Shared leaves (out-degree 0, in-degree >1): {sharedLeafCount:N0} ({(nodeCountPacked1 > 0 ? sharedLeafCount * 100.0 / nodeCountPacked1 : 0):F1}% of N) — still need real LT, no shortcut");
        Console.WriteLine($"  Estimated LT-array memory saved if foldable leaves excluded: ~{FormatBytes((ulong)foldableLeafCount * 28)}");

        long idMapBytes = idMap.EstimatedBytes;
        long addressesBytes = (long)addresses.Capacity * 8;
        long degreeBytes = (long)outDegree.Capacity * 4 + (long)inDegreeList.Capacity * 4;
        long genTagBytes = genTag.Capacity;
        long offsetBytes = (long)(fwdOffset.Length + revOffset.Length) * 4;
        long csrBytes = edgeCountPacked1 * 4 * 2;

        Console.WriteLine("\n--- Packed structures: measured memory breakdown (post edge-list free) ---");
        Console.WriteLine($"  DenseIdMap (ulong->int, open-addressed):  {FormatBytes((ulong)idMapBytes)}");
        Console.WriteLine($"  addresses[] (List<ulong>, capacity):      {FormatBytes((ulong)addressesBytes)}");
        Console.WriteLine($"  outDegree[]+inDegree[] (List<int> x2):    {FormatBytes((ulong)degreeBytes)}");
        Console.WriteLine($"  genTag[] (List<byte>):                    {FormatBytes((ulong)genTagBytes)}");
        Console.WriteLine($"  fwdOffset[]+revOffset[] (int[N+1] x2):    {FormatBytes((ulong)offsetBytes)}");
        Console.WriteLine($"  fwdTargets[]+revSources[] (int[E] x2):    {FormatBytes((ulong)csrBytes)}");
        long structuralTotal = idMapBytes + addressesBytes + degreeBytes + genTagBytes + offsetBytes + csrBytes;
        Console.WriteLine($"  Structural total (analytic):              {FormatBytes((ulong)structuralTotal)}");

        long managedBytesPacked1 = GC.GetTotalMemory(forceFullCollection: true);
        Console.WriteLine($"  Managed memory at end of run (GC.GetTotalMemory): {FormatBytes((ulong)managedBytesPacked1)}");

        Console.WriteLine("\n--- Summary ---");
        double totalPacked1Seconds = (loadSw.Elapsed + pass1Sw.Elapsed + rootSw3.Elapsed + walkSw2.Elapsed + csrSw.Elapsed).TotalSeconds;
        Console.WriteLine($"  Single walk + CSR redistribution: {(walkSw2.Elapsed + csrSw.Elapsed).TotalSeconds:F2}s");
        Console.WriteLine($"  Total wall clock (incl. dump load, Pass 1 tagging): {totalPacked1Seconds:F2}s");

        return 0;
    }

    if (mode == "diskindex")
    {
        // Design doc §D5 spike: does persisting a whole-heap forward-edge index during Phase 1
        // actually eliminate DominatorAnalyzer's live-walk cost, and does inverting it produce the
        // identical reachable subgraph the direct live-BFS (packed1) already measured?
        Console.WriteLine("\n--- §D5 spike: whole-heap forward index build + in-memory consumption ---");

        var idMap = new DenseIdMap(1 << 21);
        var addresses = new List<ulong>(capacity: 1 << 21);
        var outDegree = new List<int>(capacity: 1 << 21);
        var edgeFrom = new List<int>(capacity: 1 << 23);
        var edgeTo = new List<int>(capacity: 1 << 23);

        (int id, bool isNew) GetOrAddId(ulong addr)
        {
            if (idMap.TryGetValue(addr, out int existing))
                return (existing, false);

            int newId = addresses.Count;
            idMap.Add(addr, newId);
            addresses.Add(addr);
            outDegree.Add(0);
            return (newId, true);
        }

        // --- Step 1: whole-heap forward-edge extraction (proxy for "Phase 1 also emits this") ---
        Stopwatch extractSw = Stopwatch.StartNew();
        long objectsWalked = 0;
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            objectsWalked++;
            if (objectsWalked % 5_000_000 == 0)
                Console.Write($"\r  Extracted: {objectsWalked:N0} objects, {edgeFrom.Count:N0} edges");

            if (!obj.IsValid || obj.Type is null)
                continue;

            (int fromId, _) = GetOrAddId(obj.Address);

            foreach (ClrObject child in obj.EnumerateReferences(carefully: true))
            {
                if (!child.IsValid || child.Address == 0)
                    continue;

                (int toId, _) = GetOrAddId(child.Address);
                edgeFrom.Add(fromId);
                edgeTo.Add(toId);
                outDegree[fromId] = outDegree[fromId] + 1;
            }
        }
        extractSw.Stop();
        int wholeHeapNodeCount = addresses.Count;
        long wholeHeapEdgeCount = edgeFrom.Count;
        Console.WriteLine($"\r  Extracted: {objectsWalked:N0} objects, {wholeHeapEdgeCount:N0} edges                          ");
        Console.WriteLine($"Step 1 — whole-heap extraction (Phase 1 build-cost proxy): {extractSw.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"  Whole-heap node ids assigned: {wholeHeapNodeCount:N0}");
        Console.WriteLine($"  Whole-heap edges captured:    {wholeHeapEdgeCount:N0}");

        // --- Step 2: build the whole-heap forward CSR (no ClrMD reads) ---
        Stopwatch buildCsrSw = Stopwatch.StartNew();
        var fwdOffset = new int[wholeHeapNodeCount + 1];
        for (int i = 0; i < wholeHeapNodeCount; i++)
            fwdOffset[i + 1] = fwdOffset[i] + outDegree[i];
        var fwdTargets = new int[wholeHeapEdgeCount];
        var fwdCursor = (int[])fwdOffset.Clone();
        for (int e = 0; e < wholeHeapEdgeCount; e++)
            fwdTargets[fwdCursor[edgeFrom[e]]++] = edgeTo[e];
        buildCsrSw.Stop();
        Console.WriteLine($"Step 2 — whole-heap forward CSR build (no ClrMD): {buildCsrSw.Elapsed.TotalSeconds:F2}s");

        double indexBuildSeconds = extractSw.Elapsed.TotalSeconds + buildCsrSw.Elapsed.TotalSeconds;
        Console.WriteLine($"  Total simulated Phase-1-index-build cost: {indexBuildSeconds:F2}s");

        // Free the edge-list buffers before the next phase — a real implementation would have
        // already written them to disk and released them by this point.
        edgeFrom.Clear();
        edgeFrom.Capacity = 0;
        edgeTo.Clear();
        edgeTo.Capacity = 0;

        // --- Step 3: in-memory BFS from roots over the whole-heap forward CSR (zero ClrMD calls) ---
        Stopwatch consumeBfsSw = Stopwatch.StartNew();
        var reachable = new bool[wholeHeapNodeCount];
        var diskFrontier = new Queue<int>();
        long rootsResolved = 0;
        foreach (ClrRoot root in heap.EnumerateRoots())
        {
            ulong target = root.Object.Address;
            if (target == 0 || !idMap.TryGetValue(target, out int id))
                continue;

            rootsResolved++;
            if (!reachable[id])
            {
                reachable[id] = true;
                diskFrontier.Enqueue(id);
            }
        }

        long reachableEdgeCount = 0;
        while (diskFrontier.Count > 0)
        {
            int id = diskFrontier.Dequeue();
            for (int e = fwdOffset[id]; e < fwdOffset[id + 1]; e++)
            {
                int childId = fwdTargets[e];
                reachableEdgeCount++;
                if (!reachable[childId])
                {
                    reachable[childId] = true;
                    diskFrontier.Enqueue(childId);
                }
            }
        }
        consumeBfsSw.Stop();

        int reachableNodeCount = 0;
        for (int i = 0; i < wholeHeapNodeCount; i++)
            if (reachable[i]) reachableNodeCount++;

        Console.WriteLine($"Step 3 — in-memory reachability BFS over persisted CSR (zero ClrMD): {consumeBfsSw.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"  Roots resolved: {rootsResolved:N0}");
        Console.WriteLine($"  Reachable nodes (N):  {reachableNodeCount:N0}");
        Console.WriteLine($"  Reachable edges (E):  {reachableEdgeCount:N0}");

        // --- Step 4: invert the reachable-subgraph edges into a reverse CSR (zero ClrMD calls) ---
        Stopwatch invertSw = Stopwatch.StartNew();
        var reachableInDegree = new int[wholeHeapNodeCount];
        int maxReachableInDegree = 0;
        ulong maxReachableInDegreeAddress = 0;
        for (int id = 0; id < wholeHeapNodeCount; id++)
        {
            if (!reachable[id])
                continue;

            for (int e = fwdOffset[id]; e < fwdOffset[id + 1]; e++)
            {
                int childId = fwdTargets[e];
                int updated = reachableInDegree[childId] + 1;
                reachableInDegree[childId] = updated;
                if (updated > maxReachableInDegree)
                {
                    maxReachableInDegree = updated;
                    maxReachableInDegreeAddress = addresses[childId];
                }
            }
        }
        invertSw.Stop();
        Console.WriteLine($"Step 4 — reverse-CSR inversion of reachable edges (zero ClrMD): {invertSw.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"  Max in-degree among reachable nodes: {maxReachableInDegree:N0} (0x{maxReachableInDegreeAddress:X})");

        double consumptionSeconds = consumeBfsSw.Elapsed.TotalSeconds + invertSw.Elapsed.TotalSeconds;

        Console.WriteLine("\n--- Summary ---");
        Console.WriteLine($"  Simulated one-time Phase 1 index-build cost: {indexBuildSeconds:F2}s (paid once per dump, amortized across all analyzer runs)");
        Console.WriteLine($"  Simulated DominatorAnalyzer consumption cost (steps 3+4, zero ClrMD): {consumptionSeconds:F2}s");
        Console.WriteLine($"  Cross-check against Round 4 (packed1, live BFS): compare N={reachableNodeCount:N0}, "
            + $"E={reachableEdgeCount:N0}, maxInDegree={maxReachableInDegree:N0} against the design doc's measured "
            + "packed1 figures for this dump — exact match validates forward-then-invert correctness.");

        return 0;
    }

    if (mode is "walkonly" or "walkcontains")
    {
        bool checkContains = mode == "walkcontains";
        Console.WriteLine($"\n--- Isolated walk-cost pass (mode={mode}) ---");

        Stopwatch walkSw = Stopwatch.StartNew();
        long walkNodes = 0;
        long walkEdges = 0;
        long walkLiveEdges = 0;

        foreach (ulong address in allLive)
        {
            walkNodes++;
            if (walkNodes % 1_000_000 == 0)
                Console.Write($"\r  Walked: {walkNodes:N0} / {allLive.Count:N0}");

            ClrObject obj = heap.GetObject(address);
            if (!obj.IsValid || obj.Type is null)
                continue;

            foreach (ClrObject child in obj.EnumerateReferences(carefully: true))
            {
                if (!child.IsValid || child.Address == 0)
                    continue;

                walkEdges++;
                if (checkContains && allLive.Contains(child.Address))
                    walkLiveEdges++;
            }
        }

        walkSw.Stop();
        Console.WriteLine($"\r  Walked: {walkNodes:N0} / {allLive.Count:N0}                                            ");
        Console.WriteLine($"Isolated walk complete in {walkSw.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"  Nodes walked:          {walkNodes:N0}");
        Console.WriteLine($"  Edges enumerated:      {walkEdges:N0}" + (checkContains ? $" ({walkLiveEdges:N0} live)" : ""));
        double walkThroughput = walkSw.Elapsed.TotalSeconds > 0 ? walkNodes / walkSw.Elapsed.TotalSeconds : 0;
        Console.WriteLine($"  Throughput:            {walkThroughput:N0} nodes/sec");
        Console.WriteLine($"  Total wall clock:      {(loadSw.Elapsed + pass1Sw.Elapsed + walkSw.Elapsed).TotalSeconds:F2}s");
        return 0;
    }

    // --- Pass 2: BFS from GC roots over the WHOLE live graph, fused with forward-walk timing --
    Console.WriteLine("\n--- Pass 2: BFS reachability + forward-walk cost (whole heap) ---");

    Stopwatch rootSw = Stopwatch.StartNew();
    var frontier = new Queue<ulong>();
    var visited = new HashSet<ulong>(capacity: allLive.Count);
    long rootCount = 0;
    long resolvedRootCount = 0;

    foreach (ClrRoot root in heap.EnumerateRoots())
    {
        rootCount++;
        ulong target = root.Object.Address;
        if (target == 0 || !allLive.Contains(target))
            continue;

        resolvedRootCount++;
        if (visited.Add(target))
            frontier.Enqueue(target);
    }
    rootSw.Stop();
    Console.WriteLine($"Roots enumerated: {rootCount:N0} in {rootSw.Elapsed.TotalSeconds:F2}s "
        + $"({resolvedRootCount:N0} resolve to a live object)");

    Stopwatch bfsSw = Stopwatch.StartNew();
    long edgesWalked = 0;   // every outgoing reference read
    long liveEdges = 0;     // subset whose target is a live object (should be ~= edgesWalked)
    long gen2LohEdges = 0;  // subset whose target is Gen2/LOH/POH/Frozen (for comparison to the scoped run)
    long nodesVisited = 0;
    var inDegree = new Dictionary<ulong, int>(capacity: allLive.Count);
    int maxInDegree = 0;
    ulong maxInDegreeAddress = 0;

    while (frontier.Count > 0)
    {
        ulong address = frontier.Dequeue();
        nodesVisited++;
        if (nodesVisited % 1_000_000 == 0)
            Console.Write($"\r  Visited: {nodesVisited:N0} / {allLive.Count:N0}, edges: {liveEdges:N0}");

        ClrObject obj = heap.GetObject(address);
        if (!obj.IsValid || obj.Type is null)
            continue;

        // carefully:true — matches the codebase's own correct traversal
        // (ObjectGraphTraversal.TryFindByPredicate) — walks struct-typed array elements
        // (e.g. Dictionary<K,V>'s Entry[]) and nested struct fields that a manual
        // top-level-fields-only walk silently misses.
        foreach (ClrObject child in obj.EnumerateReferences(carefully: true))
        {
            if (!child.IsValid || child.Address == 0)
                continue;

            edgesWalked++;
            RecordEdge(child.Address, allLive, gen2Loh, inDegree, ref liveEdges, ref gen2LohEdges, ref maxInDegree, ref maxInDegreeAddress);
            if (visited.Add(child.Address))
                frontier.Enqueue(child.Address);
        }
    }

    bfsSw.Stop();
    Console.WriteLine($"\r  Visited: {nodesVisited:N0} / {allLive.Count:N0}                                            ");
    Console.WriteLine($"Pass 2 complete in {bfsSw.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine($"  Outgoing references read:        {edgesWalked:N0}");
    Console.WriteLine($"  Edges to a live object:          {liveEdges:N0}");
    Console.WriteLine($"  Edges to a Gen2/LOH/POH/Frozen:  {gen2LohEdges:N0}");
    Console.WriteLine($"  Avg out-degree (whole heap):     {(nodesVisited > 0 ? liveEdges / (double)nodesVisited : 0):F2}");
    double throughput = bfsSw.Elapsed.TotalSeconds > 0 ? nodesVisited / bfsSw.Elapsed.TotalSeconds : 0;
    Console.WriteLine($"  Forward-walk throughput:         {throughput:N0} nodes/sec");

    // --- Unreachable: expected to be substantial for Gen0/1 (churny, mostly dead-not-yet-swept
    // at snapshot time, per docs/analysis/phase1-redesigns/root-path-finder.md §4.1) — the number
    // to watch is the Gen2/LOH-tagged share of the unreachable population, not the raw total.
    long unreachableCount = allLive.Count - visited.Count;
    long gen2LohUnreachable = 0;
    foreach (ulong addr in gen2Loh)
    {
        if (!visited.Contains(addr))
            gen2LohUnreachable++;
    }
    Console.WriteLine("\n--- Unreachable (whole-heap) ---");
    Console.WriteLine($"  Live but not reached from any root: {unreachableCount:N0} "
        + $"({(allLive.Count > 0 ? unreachableCount * 100.0 / allLive.Count : 0):F2}% of live population)");
    Console.WriteLine($"  Of which Gen2/LOH/POH/Frozen-tagged: {gen2LohUnreachable:N0} "
        + $"({(gen2Loh.Count > 0 ? gen2LohUnreachable * 100.0 / gen2Loh.Count : 0):F2}% of that tagged population)");

    // --- In-degree distribution (fanout-cap risk for any reverse-index reuse) ------------------
    Console.WriteLine("\n--- In-degree distribution, whole-heap graph ---");
    Console.WriteLine($"  Max in-degree observed:      {maxInDegree:N0} (0x{maxInDegreeAddress:X})");
    const int FanoutCap = 10_000; // matches ReverseEdgeExtractor.MaxParentsPerChild
    int nodesNearCap = 0;
    int nodesOverHalfCap = 0;
    foreach (int deg in inDegree.Values)
    {
        if (deg >= FanoutCap) nodesNearCap++;
        else if (deg >= FanoutCap / 2) nodesOverHalfCap++;
    }
    Console.WriteLine($"  Nodes with in-degree >= {FanoutCap:N0} (would be truncated by disk reverse index): {nodesNearCap:N0}");
    Console.WriteLine($"  Nodes with in-degree in [{FanoutCap / 2:N0}, {FanoutCap:N0}):                          {nodesOverHalfCap:N0}");

    // --- Memory / summary ------------------------------------------------------------------------
    Console.WriteLine("\n--- Summary ---");
    long managedBytes = GC.GetTotalMemory(forceFullCollection: true);
    Console.WriteLine($"  Managed memory at end of run: {FormatBytes((ulong)managedBytes)}");
    Console.WriteLine($"  Estimated edge-array cost (2x int[] pairs, both directions, 8 bytes/edge/direction): "
        + $"~{FormatBytes((ulong)liveEdges * 16)}");
    Console.WriteLine($"  Total wall clock:              {(loadSw.Elapsed + pass1Sw.Elapsed + rootSw.Elapsed + bfsSw.Elapsed).TotalSeconds:F2}s");

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    return 2;
}

static bool IsGen2LohPohFrozen(ClrHeap heap, ulong address)
{
    ClrSegment? seg = heap.GetSegmentByAddress(address);
    if (seg is null)
        return false;

    switch (seg.Kind)
    {
        case GCSegmentKind.Large:
        case GCSegmentKind.Pinned:
        case GCSegmentKind.Frozen:
            return true;
        case GCSegmentKind.Generation2:
            return true;
        case GCSegmentKind.Generation0:
        case GCSegmentKind.Generation1:
            return false;
        default:
            // Ephemeral (workstation GC) segment holds gen0/1/2 together — resolve per-object.
            try
            {
                return seg.GetGeneration(address) == Generation.Generation2;
            }
            catch
            {
                return false;
            }
    }
}

static void RecordEdge(
    ulong target,
    HashSet<ulong> allLive,
    HashSet<ulong> gen2Loh,
    Dictionary<ulong, int> inDegree,
    ref long liveEdges,
    ref long gen2LohEdges,
    ref int maxInDegree,
    ref ulong maxInDegreeAddress)
{
    if (!allLive.Contains(target))
        return;

    liveEdges++;
    if (gen2Loh.Contains(target))
        gen2LohEdges++;

    int updated = inDegree.TryGetValue(target, out int existing) ? existing + 1 : 1;
    inDegree[target] = updated;
    if (updated > maxInDegree)
    {
        maxInDegree = updated;
        maxInDegreeAddress = target;
    }
}

static string FormatBytes(ulong bytes)
{
    string[] units = ["B", "KB", "MB", "GB", "TB"];
    double value = bytes;
    int unit = 0;
    while (value >= 1024 && unit < units.Length - 1)
    {
        value /= 1024;
        unit++;
    }
    return $"{value:F2} {units[unit]}";
}

// Open-addressed ulong -> int map (linear probing, power-of-two capacity), replacing
// Dictionary<ulong,int> for the packed-array prototype per design doc §Phase 1 — avoids
// Dictionary's ~28-32 bytes/entry bucket+entry overhead in favor of ~13 bytes/slot flat arrays.
sealed class DenseIdMap
{
    private ulong[] _keys;
    private int[] _values;
    private bool[] _occupied;
    private int _mask;
    private int _count;

    public DenseIdMap(int initialCapacityPow2)
    {
        _keys = new ulong[initialCapacityPow2];
        _values = new int[initialCapacityPow2];
        _occupied = new bool[initialCapacityPow2];
        _mask = initialCapacityPow2 - 1;
    }

    public long EstimatedBytes => (long)_keys.Length * (8 + 4 + 1);

    public bool TryGetValue(ulong key, out int value)
    {
        int mask = _mask;
        int slot = Hash(key) & mask;
        while (_occupied[slot])
        {
            if (_keys[slot] == key)
            {
                value = _values[slot];
                return true;
            }
            slot = (slot + 1) & mask;
        }
        value = -1;
        return false;
    }

    public void Add(ulong key, int value)
    {
        if ((_count + 1) * 10 > _keys.Length * 7)
            Grow();

        InsertUnchecked(key, value);
        _count++;
    }

    private void InsertUnchecked(ulong key, int value)
    {
        int mask = _mask;
        int slot = Hash(key) & mask;
        while (_occupied[slot])
            slot = (slot + 1) & mask;

        _occupied[slot] = true;
        _keys[slot] = key;
        _values[slot] = value;
    }

    private void Grow()
    {
        ulong[] oldKeys = _keys;
        int[] oldValues = _values;
        bool[] oldOccupied = _occupied;

        int newCap = _keys.Length * 2;
        _keys = new ulong[newCap];
        _values = new int[newCap];
        _occupied = new bool[newCap];
        _mask = newCap - 1;

        for (int i = 0; i < oldKeys.Length; i++)
        {
            if (oldOccupied[i])
                InsertUnchecked(oldKeys[i], oldValues[i]);
        }
    }

    private static int Hash(ulong key)
    {
        unchecked
        {
            ulong h = key * 0x9E3779B97F4A7C15UL;
            return (int)(h >> 32) & int.MaxValue;
        }
    }
}
