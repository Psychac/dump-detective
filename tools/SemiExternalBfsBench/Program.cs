// Q6 of docs/cache/cache-ideal-design.md §10 — is a semi-external, bitmap-frontier BFS over a
// disk-backed CSR competitive with holding the CSR in RAM, and does sorting the frontier recover
// locality (§4.2 mitigation 1)?
//
// The forward CSR is not persisted, so this harness TRANSPOSES the persisted v8 reverse CSR
// (ReverseEdgeOffsets/ReverseEdgeChildren) into a forward CSR and writes it to a temp file. Two
// things fall out of that:
//
//   * The transpose is a counting sort over the real 137M-edge set — i.e. exactly the
//     "a sort by key IS a CSR" primitive §3.2 rests on. Its measured time and memory are reported.
//   * The resulting forward graph's sources (rows with in-degree 0) are the real GC-root-seeded
//     rows, so a forward BFS from them covers the whole reachable set, not a fragment.
//
// Arms:
//   A  in-memory CSR,  FIFO frontier   — the shape the current walk has (minus its Dictionary)
//   B  mmap'd CSR,     FIFO frontier   — semi-external, discovery order
//   C  mmap'd CSR,     sorted frontier — semi-external, level-synchronous, ascending rows
//
// LIMITATION, stated rather than hidden: the temp CSR was just written, so it is resident in the
// OS page cache for arms B and C. This measures the page-cache-warm case, which is the case §4.2
// argues for (the freed 6 GB is what keeps it warm) — it does NOT measure a cold-storage BFS.

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: SemiExternalBfsBench <cache.bin> [scratchDir]");
    return 1;
}

string path = args[0];
string scratch = args.Length > 1 ? args[1] : Path.GetTempPath();

var toc = ReadToc(path, out int version);
if (!toc.TryGetValue(35, out var offsSec) || !toc.TryGetValue(36, out var kidsSec))
{
    Console.Error.WriteLine("no v8 reverse CSR in this container");
    return 2;
}

long R = offsSec.RecordCount - 1;
long E = kidsSec.RecordCount;
Console.WriteLine($"{path}");
Console.WriteLine($"  format v{version}   R = {R:N0} rows   E = {E:N0} edges");
Console.WriteLine($"  reverse CSR on disk: {(offsSec.Length + kidsSec.Length) / 1048576.0:N1} MiB");
Console.WriteLine();

using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);

// ---- transpose reverse -> forward. This is §3.2's counting sort, on the real edge set. --------
long memBefore = GC.GetTotalMemory(true);
var swT = Stopwatch.StartNew();

int[] revOffsets = new int[R + 1];
using (var v = mmf.CreateViewAccessor(offsSec.Offset, offsSec.Length, MemoryMappedFileAccess.Read))
    v.ReadArray(0, revOffsets, 0, (int)(R + 1));

int[] fwdOffsets = new int[R + 2];
int[] fwdTargets = new int[E];

using (var kv = mmf.CreateViewAccessor(kidsSec.Offset, kidsSec.Length, MemoryMappedFileAccess.Read))
{
    unsafe
    {
        byte* kb = null;
        kv.SafeMemoryMappedViewHandle.AcquirePointer(ref kb);
        try
        {
            int* parents = (int*)(kb + kv.PointerOffset);

            // pass 1 — out-degree histogram (offset by one so the prefix sum doubles as a cursor)
            for (long k = 0; k < E; k++)
            {
                int p = parents[k];
                if ((uint)p < (uint)R) fwdOffsets[p + 2]++;
            }
            // pass 2 — prefix sum
            for (long i = 2; i < R + 2; i++) fwdOffsets[i] += fwdOffsets[i - 1];
            // pass 3 — scatter
            for (long child = 0; child < R; child++)
                for (int k = revOffsets[child]; k < revOffsets[child + 1]; k++)
                {
                    int p = parents[k];
                    if ((uint)p < (uint)R) fwdTargets[fwdOffsets[p + 1]++] = (int)child;
                }
        }
        finally { kv.SafeMemoryMappedViewHandle.ReleasePointer(); }
    }
}
swT.Stop();
long memAfter = GC.GetTotalMemory(false);
Console.WriteLine($"  transpose (counting sort, §3.2 primitive): {swT.Elapsed.TotalSeconds:N2} s, " +
                  $"{(memAfter - memBefore) / 1048576.0:N0} MB resident for offsets+targets");

// seeds = rows with no parents, i.e. the graph's real sources
var seedList = new List<int>();
for (int r = 0; r < R; r++) if (revOffsets[r] == revOffsets[r + 1]) seedList.Add(r);
int[] seeds = seedList.ToArray();
Console.WriteLine($"  sources (in-degree 0): {seeds.Length:N0}");

// persist the forward CSR so arms B/C can map it
string fo = Path.Combine(scratch, "fwd.offsets.bin");
string ft = Path.Combine(scratch, "fwd.targets.bin");
WriteInts(fo, fwdOffsets, 0, R + 1);
WriteInts(ft, fwdTargets, 0, E);
Console.WriteLine($"  forward CSR written: {(new FileInfo(fo).Length + new FileInfo(ft).Length) / 1048576.0:N1} MiB");
Console.WriteLine();

Console.WriteLine($"{"arm",-34}{"wall s",10}{"visited",14}{"edges",16}{"faults",14}{"ns/edge",10}");
Console.WriteLine(new string('-', 98));

RunInMemory(fwdOffsets, fwdTargets, R, seeds);
fwdOffsets = null!; fwdTargets = null!;
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

RunMapped(fo, ft, R, E, seeds, sorted: false);
RunMapped(fo, ft, R, E, seeds, sorted: true);

File.Delete(fo);
File.Delete(ft);
return 0;

static void RunInMemory(int[] offsets, int[] targets, long R, int[] seeds)
{
    long baseFaults = HardFaults();
    var sw = Stopwatch.StartNew();
    var (visited, edges) = Bfs(R, seeds, sorted: false,
        (int row, out int s, out int e) => { s = offsets[row]; e = offsets[row + 1]; },
        i => targets[i]);
    sw.Stop();
    Report("A  in-memory CSR, FIFO", sw.Elapsed.TotalSeconds, visited, edges, baseFaults);
}

static unsafe void RunMapped(string offsetsPath, string targetsPath, long R, long E, int[] seeds, bool sorted)
{
    using var om = MemoryMappedFile.CreateFromFile(offsetsPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
    using var tm = MemoryMappedFile.CreateFromFile(targetsPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
    using var ov = om.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    using var tv = tm.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

    byte* ob = null, tb = null;
    ov.SafeMemoryMappedViewHandle.AcquirePointer(ref ob);
    tv.SafeMemoryMappedViewHandle.AcquirePointer(ref tb);
    try
    {
        int* offsets = (int*)(ob + ov.PointerOffset);
        int* targets = (int*)(tb + tv.PointerOffset);

        long baseFaults = HardFaults();
        var sw = Stopwatch.StartNew();
        var (visited, edges) = Bfs(R, seeds, sorted,
            (int row, out int s, out int e) => { s = offsets[row]; e = offsets[row + 1]; },
            i => targets[i]);
        sw.Stop();
        Report(sorted ? "C  mmap'd CSR, SORTED frontier" : "B  mmap'd CSR, FIFO frontier",
            sw.Elapsed.TotalSeconds, visited, edges, baseFaults);
    }
    finally
    {
        ov.SafeMemoryMappedViewHandle.ReleasePointer();
        tv.SafeMemoryMappedViewHandle.ReleasePointer();
    }
}

static (long Visited, long Edges) Bfs(long R, int[] seeds, bool sorted, RowRange range, Func<int, int> child)
{
    // Visited is a bitmap over rows — the ideal design's representation, 1 bit/row.
    var visited = new ulong[(R + 63) / 64];
    long visitedCount = 0, edgeCount = 0;

    var frontier = new List<int>(seeds.Length);
    foreach (int s in seeds)
        if (TrySet(visited, s)) { frontier.Add(s); visitedCount++; }

    var next = new List<int>(1 << 20);
    while (frontier.Count > 0)
    {
        if (sorted) frontier.Sort();

        foreach (int row in frontier)
        {
            range(row, out int s, out int e);
            edgeCount += e - s;
            for (int k = s; k < e; k++)
            {
                int c = child(k);
                if ((uint)c < (uint)R && TrySet(visited, c)) { next.Add(c); visitedCount++; }
            }
        }

        (frontier, next) = (next, frontier);
        next.Clear();
    }
    return (visitedCount, edgeCount);
}

static bool TrySet(ulong[] bits, int i)
{
    ulong mask = 1UL << (i & 63);
    ref ulong w = ref bits[i >> 6];
    if ((w & mask) != 0) return false;
    w |= mask;
    return true;
}

static void Report(string label, double sec, long visited, long edges, long baseFaults) =>
    Console.WriteLine($"{label,-34}{sec,10:N2}{visited,14:N0}{edges,16:N0}" +
                      $"{HardFaults() - baseFaults,14:N0}{sec * 1e9 / Math.Max(1, edges),10:N1}");

static void WriteInts(string path, int[] data, long start, long count)
{
    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
    byte[] buf = new byte[1 << 20];
    long i = start, end = start + count;
    while (i < end)
    {
        int n = (int)Math.Min(buf.Length / 4, end - i);
        for (int j = 0; j < n; j++) BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(j * 4), data[i + j]);
        fs.Write(buf, 0, n * 4);
        i += n;
    }
}

static long HardFaults()
{
    var c = new PROCESS_MEMORY_COUNTERS { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>() };
    return GetProcessMemoryInfo(Process.GetCurrentProcess().Handle, ref c, c.cb) ? c.PageFaultCount : 0;
}

[DllImport("psapi.dll", SetLastError = true)]
static extern bool GetProcessMemoryInfo(IntPtr hProcess, ref PROCESS_MEMORY_COUNTERS counters, uint size);

static Dictionary<int, Sec> ReadToc(string path, out int formatVersion)
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
    var toc = new Dictionary<int, Sec>(count);
    for (int i = 0; i < count; i++)
    {
        var e = raw.AsSpan(i * 32);
        toc[BinaryPrimitives.ReadInt32LittleEndian(e)] = new Sec(
            BinaryPrimitives.ReadInt64LittleEndian(e[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(e[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(e[20..]));
    }
    return toc;
}

delegate void RowRange(int row, out int start, out int end);

record struct Sec(long Offset, long Length, long RecordCount);

[StructLayout(LayoutKind.Sequential)]
struct PROCESS_MEMORY_COUNTERS
{
    public uint cb;
    public uint PageFaultCount;
    public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
    public UIntPtr QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
}
