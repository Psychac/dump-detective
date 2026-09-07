// Opens every disk-index reader against a cache.bin and reports which succeed. No dump load —
// these readers only need the container, so a failure here is a container/reader problem rather
// than anything to do with the dump.
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Columns;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Dominator;
using DumpDetective.Analysis.Indexing.ReverseIndex;

string path = args[0];
if (!CacheContainerReader.TryOpen(path, out CacheContainerReader? c) || c is null)
{
    Console.WriteLine("CacheContainerReader.TryOpen: FAILED");
    return 1;
}
Console.WriteLine("CacheContainerReader.TryOpen: ok");

Console.WriteLine($"ObjectColumnSet.TryOpen: {(ObjectColumnSet.TryOpen(c, out ObjectColumnSet? cols) ? $"ok (records {cols!.RecordCount:N0})" : "FAILED")}");

Console.WriteLine($"MonotonicAddressColumn(ObjectAddresses): {(MonotonicAddressColumn.TryOpen(c, CacheSectionId.ObjectAddresses, CacheSectionId.ObjectAddressBlockBases, CacheSectionId.ObjectAddressOverflow, out MonotonicAddressColumn? mac) ? $"ok (rows {mac!.RowCount:N0})" : "FAILED")}");

Console.WriteLine($"ReachableRowBitmap.TryOpen: {(ReachableRowBitmap.TryOpen(c, out ReachableRowBitmap? bm) ? $"ok (objectRows {bm!.ObjectRowCount:N0}, reachable {bm.ReachableRowCount:N0})" : "FAILED")}");

Console.WriteLine($"DominatorRowIndex.TryOpen: {(DominatorRowIndex.TryOpen(c, out DominatorRowIndex? rows) ? $"ok (rows {rows!.RowCount:N0})" : "FAILED")}");

Console.WriteLine($"DominatorTreeIndexReader.TryOpen: {(DominatorTreeIndexReader.TryOpen(c, out DominatorTreeIndexReader? dt) ? "ok" : "FAILED")}");
Console.WriteLine($"ReverseEdgeIndexReader.TryOpen: {(ReverseEdgeIndexReader.TryOpen(c, out ReverseEdgeIndexReader? rev) ? "ok" : "FAILED")}");

if (rows is not null && mac is not null && bm is not null)
{
    Console.WriteLine("\nspot checks:");
    for (long r = 0; r < Math.Min(3, rows.RowCount); r++)
        Console.WriteLine($"  row {r} -> 0x{rows.ReadAddress(r):x}  roundtrip row {rows.FindRow(rows.ReadAddress(r))}");
    long mid = rows.RowCount / 2;
    ulong a = rows.ReadAddress(mid);
    Console.WriteLine($"  row {mid:N0} -> 0x{a:x}  roundtrip {rows.FindRow(a)}");
}
return 0;
