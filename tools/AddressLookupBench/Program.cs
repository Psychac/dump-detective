// Q5 of docs/cache/cache-ideal-design.md §10 — the load-bearing assumption of R1.
//
// Compares the two candidate `address -> object row` mechanisms against a REAL cache.bin,
// with no dump load:
//
//   Arm A  Dictionary<ulong,int>   — what ReachableGraphWalker.WalkWithCsr uses today
//   Arm B  two-level rank          — binary search ObjectAddressBlockBases (resident, ~0.65 MiB),
//                                    then search inside one 1024-record block of the mmap'd
//                                    4-byte delta column
//
// Three probe orders, because the walk's real order sits between them:
//   sequential  — ascending addresses (what a sorted BFS frontier approximates, §4.2)
//   random      — uniform over the live set (the pessimistic bound)
//   edge        — child addresses in parent order, reconstructed from the persisted reverse CSR
//                 (real heap reference locality)
//
// Self-contained cache.bin parsing on purpose: this is a benchmark, not production code, and
// keeping it free of project references keeps it runnable against any container.

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

const int BlockShift = 10;
const int BlockRecords = 1 << BlockShift;
const uint EscapeSentinel = uint.MaxValue;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: AddressLookupBench <cache.bin> [probeCount]");
    return 1;
}

string path = args[0];
int probeCount = args.Length > 1 ? int.Parse(args[1]) : 20_000_000;

var toc = ReadToc(path, out int formatVersion);
Console.WriteLine($"{path}");
Console.WriteLine($"  format v{formatVersion}, {new FileInfo(path).Length / 1048576.0:N1} MiB, {toc.Count} sections");

// ---- load the address column and its block bases -------------------------------------------
(long addrOff, long addrLen, long objectCount) = toc[10];   // ObjectAddresses
(long baseOff, long baseLen, long _) = toc[30];             // ObjectAddressBlockBases
int addrWidth = (int)(addrLen / objectCount);
if (addrWidth != 4)
{
    Console.Error.WriteLine($"expected a 4-byte block-delta address column, got {addrWidth} B/record");
    return 2;
}

using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, 0, MemoryMappedFileAccess.Read);
using var addrView = mmf.CreateViewAccessor(addrOff, addrLen, MemoryMappedFileAccess.Read);

ulong[] blockBases = new ulong[baseLen / 8];
using (var baseView = mmf.CreateViewAccessor(baseOff, baseLen, MemoryMappedFileAccess.Read))
    for (int i = 0; i < blockBases.Length; i++)
        blockBases[i] = baseView.ReadUInt64(i * 8L);

int expectedBlocks = (int)((objectCount + BlockRecords - 1) / BlockRecords);
if (blockBases.Length != expectedBlocks)
{
    Console.Error.WriteLine($"block base count {blockBases.Length} != expected {expectedBlocks}");
    return 2;
}

Console.WriteLine($"  O = {objectCount:N0} objects, {blockBases.Length:N0} block bases ({baseLen / 1048576.0:N2} MiB resident)");

// Touch the whole delta column once so both arms face warm pages — we are measuring lookup
// cost, not first-touch I/O, and Arm A gets warm pages for free by construction.
unsafe
{
    byte* p = null;
    addrView.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
    try
    {
        p += addrView.PointerOffset;
        long checksum = 0;
        for (long i = 0; i < addrLen; i += 4096) checksum += p[i];
        GC.KeepAlive(checksum);
    }
    finally { addrView.SafeMemoryMappedViewHandle.ReleasePointer(); }
}

unsafe
{
    byte* deltaBase = null;
    addrView.SafeMemoryMappedViewHandle.AcquirePointer(ref deltaBase);
    // AcquirePointer returns the allocation-granularity-aligned base of the view, not the byte
    // at the requested section offset. PointerOffset is the difference and must be applied.
    uint* deltas = (uint*)(deltaBase + addrView.PointerOffset);

    // Materialise the decoded addresses once. Both arms need them (Arm A to build its table,
    // every arm to build probe sets); this array is NOT charged to either arm.
    ulong[] addresses = new ulong[objectCount];
    for (long i = 0; i < objectCount; i++)
    {
        uint d = deltas[i];
        addresses[i] = d == EscapeSentinel ? 0UL : blockBases[i >> BlockShift] + d;
    }
    // Patch escapes from the overflow table so the probe sets never contain a hole.
    if (toc.TryGetValue(31, out var ovf) && ovf.Length > 0)
    {
        using var ovfView = mmf.CreateViewAccessor(ovf.Offset, ovf.Length, MemoryMappedFileAccess.Read);
        for (long i = 0; i < ovf.Length / 12; i++)
            addresses[ovfView.ReadUInt32(i * 12L)] = ovfView.ReadUInt64(i * 12L + 4);
    }

    bool ascending = true;
    for (long i = 1; i < objectCount && ascending; i++)
        if (addresses[i] <= addresses[i - 1]) ascending = false;
    Console.WriteLine($"  address column strictly ascending: {ascending}   (R1's precondition)");
    Console.WriteLine();

    // ---- Arm A: build the Dictionary, charging its memory -----------------------------------
    long before = GC.GetTotalMemory(forceFullCollection: true);
    var sw = Stopwatch.StartNew();
    var dict = new Dictionary<ulong, int>((int)Math.Min(objectCount, int.MaxValue));
    for (long i = 0; i < objectCount; i++) dict[addresses[i]] = (int)i;
    sw.Stop();
    long after = GC.GetTotalMemory(forceFullCollection: true);
    double dictMb = (after - before) / 1048576.0;
    Console.WriteLine($"Arm A  Dictionary<ulong,int>  build {sw.Elapsed.TotalSeconds,6:N2} s   resident {dictMb,9:N1} MB   ({(after - before) / (double)objectCount:N1} B/entry)");
    Console.WriteLine($"Arm B  two-level rank         build {0.0,6:N2} s   resident {baseLen / 1048576.0,9:N1} MB   ({baseLen / (double)objectCount:N3} B/entry)");
    Console.WriteLine($"       ratio                                          {dictMb / (baseLen / 1048576.0),9:N0}x more memory for Arm A");
    Console.WriteLine();

    // ---- probe sets -------------------------------------------------------------------------
    int n = (int)Math.Min(probeCount, objectCount);
    var rng = new Random(12345);

    ulong[] seq = new ulong[n];
    long stride = objectCount / n;
    for (int i = 0; i < n; i++) seq[i] = addresses[i * stride];

    ulong[] rnd = new ulong[n];
    for (int i = 0; i < n; i++) rnd[i] = addresses[rng.NextInt64(objectCount)];

    ulong[] edge = BuildEdgeProbes(mmf, toc, blockBases, n, rng, out string edgeNote);

    Console.WriteLine($"  probes per order: {n:N0}   edge set: {edgeNote}");
    Console.WriteLine();
    Console.WriteLine($"{"order",-12}{"Arm A ns/probe",18}{"Arm B ns/probe",18}{"B/A",10}{"verified",12}");
    Console.WriteLine(new string('-', 70));

    foreach (var (label, probes) in new[] { ("sequential", seq), ("random", rnd), ("edge", edge) })
    {
        if (probes.Length == 0) { Console.WriteLine($"{label,-12}{"(unavailable)",18}"); continue; }

        // warm both arms on this probe set before timing
        long warm = 0;
        for (int i = 0; i < Math.Min(probes.Length, 100_000); i++)
        {
            dict.TryGetValue(probes[i], out int a);
            warm += a + RankLookup(probes[i], blockBases, deltas, objectCount);
        }
        GC.KeepAlive(warm);

        int[] rowsA = new int[probes.Length];
        var swA = Stopwatch.StartNew();
        for (int i = 0; i < probes.Length; i++)
            rowsA[i] = dict.TryGetValue(probes[i], out int row) ? row : -1;
        swA.Stop();

        int[] rowsB = new int[probes.Length];
        var swB = Stopwatch.StartNew();
        for (int i = 0; i < probes.Length; i++)
            rowsB[i] = RankLookup(probes[i], blockBases, deltas, objectCount);
        swB.Stop();

        int mismatches = 0;
        for (int i = 0; i < probes.Length; i++) if (rowsA[i] != rowsB[i]) mismatches++;

        double nsA = swA.Elapsed.TotalNanoseconds / probes.Length;
        double nsB = swB.Elapsed.TotalNanoseconds / probes.Length;
        Console.WriteLine($"{label,-12}{nsA,18:N1}{nsB,18:N1}{nsB / nsA,10:N2}{(mismatches == 0 ? "yes" : $"{mismatches:N0} BAD"),12}");
    }

    addrView.SafeMemoryMappedViewHandle.ReleasePointer();
}

return 0;

// Two-level rank: binary search the resident block bases, then search inside one block.
[MethodImpl(MethodImplOptions.AggressiveInlining)]
static unsafe int RankLookup(ulong address, ulong[] blockBases, uint* deltas, long objectCount)
{
    int lo = 0, hi = blockBases.Length - 1, block = -1;
    while (lo <= hi)
    {
        int mid = lo + ((hi - lo) >> 1);
        if (blockBases[mid] <= address) { block = mid; lo = mid + 1; }
        else hi = mid - 1;
    }
    if (block < 0) return -1;

    ulong bas = blockBases[block];
    long start = (long)block << BlockShift;
    long end = Math.Min(start + BlockRecords, objectCount) - 1;
    if (address - bas >= EscapeSentinel) return -1;
    uint target = (uint)(address - bas);

    long l = start, h = end;
    while (l <= h)
    {
        long mid = l + ((h - l) >> 1);
        uint c = deltas[mid];
        if (c < target) l = mid + 1;
        else if (c > target) h = mid - 1;
        else return (int)mid;
    }
    return -1;
}

static ulong[] BuildEdgeProbes(MemoryMappedFile mmf, Dictionary<int, (long Offset, long Length, long RecordCount)> toc,
    ulong[] blockBases, int n, Random rng, out string note)
{
    note = "n/a";
    // Reconstruct real (parent -> child) address pairs from the persisted reverse CSR, then probe
    // child addresses in parent order — the reference locality a real heap walk sees.
    if (!toc.TryGetValue(35, out var offs) || !toc.TryGetValue(36, out var kids)
        || !toc.TryGetValue(21, out var reach) || !toc.TryGetValue(32, out var reachBases))
        return Array.Empty<ulong>();

    long R = offs.RecordCount - 1;
    ulong[] reachBaseArr = new ulong[reachBases.Length / 8];
    using (var v = mmf.CreateViewAccessor(reachBases.Offset, reachBases.Length, MemoryMappedFileAccess.Read))
        for (int i = 0; i < reachBaseArr.Length; i++) reachBaseArr[i] = v.ReadUInt64(i * 8L);

    ulong[] reachAddr = new ulong[R];
    using (var v = mmf.CreateViewAccessor(reach.Offset, reach.Length, MemoryMappedFileAccess.Read))
        for (long i = 0; i < R; i++)
        {
            uint d = v.ReadUInt32(i * 4L);
            reachAddr[i] = d == EscapeSentinel ? 0UL : reachBaseArr[i >> BlockShift] + d;
        }

    // (parentRow, childRow) pairs, then order by parent so probes follow parent-side locality.
    var pairs = new List<(int Parent, int Child)>(n);
    using (var ov = mmf.CreateViewAccessor(offs.Offset, offs.Length, MemoryMappedFileAccess.Read))
    using (var kv = mmf.CreateViewAccessor(kids.Offset, kids.Length, MemoryMappedFileAccess.Read))
    {
        long totalEdges = kids.RecordCount;
        long stepRows = Math.Max(1, R / Math.Max(1, n / 2));
        for (long r = 0; r < R && pairs.Count < n; r += stepRows)
        {
            int s = ov.ReadInt32(r * 4L), e = ov.ReadInt32((r + 1) * 4L);
            for (int k = s; k < e && k < totalEdges && pairs.Count < n; k++)
                pairs.Add((kv.ReadInt32(k * 4L), (int)r));
        }
    }
    if (pairs.Count == 0) return Array.Empty<ulong>();

    pairs.Sort((a, b) => a.Parent.CompareTo(b.Parent));
    var probes = new ulong[pairs.Count];
    for (int i = 0; i < pairs.Count; i++)
    {
        int c = pairs[i].Child;
        probes[i] = (uint)c < (uint)reachAddr.Length ? reachAddr[c] : 0UL;
    }
    note = $"{pairs.Count:N0} real edges, child addresses probed in parent-row order";
    return probes;
}

static Dictionary<int, (long Offset, long Length, long RecordCount)> ReadToc(string path, out int formatVersion)
{
    using var fs = File.OpenRead(path);
    Span<byte> hdr = stackalloc byte[64];
    fs.ReadExactly(hdr);
    if (!hdr[..8].SequenceEqual("DDCACHE1"u8)) throw new InvalidDataException("bad magic");
    formatVersion = BinaryPrimitives.ReadInt32LittleEndian(hdr[8..]);
    int count = BinaryPrimitives.ReadInt32LittleEndian(hdr[44..]);
    long tocOffset = BinaryPrimitives.ReadInt64LittleEndian(hdr[48..]);

    fs.Position = tocOffset;
    byte[] raw = new byte[count * 32];
    fs.ReadExactly(raw);

    var toc = new Dictionary<int, (long, long, long)>(count);
    for (int i = 0; i < count; i++)
    {
        var e = raw.AsSpan(i * 32);
        toc[BinaryPrimitives.ReadInt32LittleEndian(e)] = (
            BinaryPrimitives.ReadInt64LittleEndian(e[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(e[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(e[20..]));
    }
    return toc;
}
