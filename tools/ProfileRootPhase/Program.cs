// Q4 of docs/cache/cache-ideal-design.md §10 — split the opaque 200.3 s "enumerating GC roots"
// phase into its two halves, and size the §E.3 fix.
//
// Runs ONLY the root phase, not a cold build: `tools/ProfileRootEnumeration` calls
// DiskBackedObjectIndexWriter.Build and therefore costs the full 1,310 s on the 27.5 GB dump. The
// question here is worth one dump load plus ~200 s, not 22 minutes.
//
// Phases:
//   1  heap.EnumerateRoots()                     — the DAC stack walk, cold
//   2  BuildMapByRootAddress, instrumented       — cold, broken down by cost centre
//   3  StaticFieldResolver.BuildMapByRootAddress — the real one, WARM, map-equality cross-check
//   4  "reorder" variant, strictly semantics-preserving — WARM, A/B against phase 3
//   5  "module prefilter" variant, heuristic     — WARM, A/B against phase 3
//
// Phases 3/4/5 are all warm, so they are comparable to each other. Phase 2 is the cold breakdown.

using System.Diagnostics;

using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Utilities;

using Microsoft.Diagnostics.Runtime;

const byte ThreadStaticVarKind = 9;
const byte StaticVarKind = 10;

string dumpPath = args.Length > 0
    ? args[0]
    : @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

if (!File.Exists(dumpPath))
{
    Console.Error.WriteLine($"Dump not found: {dumpPath}");
    return 1;
}

var swLoad = Stopwatch.StartNew();
using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
ClrHeap heap = runtime.Heap;
swLoad.Stop();

Console.WriteLine($"Dump: {Path.GetFileName(dumpPath)}  ({new FileInfo(dumpPath).Length / (1024.0 * 1024 * 1024):F2} GB)");
Console.WriteLine($"Load + DAC init: {swLoad.Elapsed.TotalSeconds:F2} s");
Console.WriteLine();

// ---- phase 1: the DAC stack walk -------------------------------------------------------------
var staticRootAddresses = new HashSet<ulong>();
long rootCount = 0;
var sw1 = Stopwatch.StartNew();
foreach (ClrRoot root in heap.EnumerateRoots())
{
    rootCount++;
    byte kind = (byte)root.RootKind;
    if (kind is ThreadStaticVarKind or StaticVarKind && root.Address != 0)
        staticRootAddresses.Add(root.Address);
}
sw1.Stop();

Console.WriteLine($"PHASE 1  heap.EnumerateRoots()            {sw1.Elapsed.TotalSeconds,9:F2} s   " +
                  $"{rootCount:N0} roots, {staticRootAddresses.Count:N0} static/thread-static");

// ---- phase 2: instrumented replica of BuildMapByRootAddress, COLD ----------------------------
long domains = 0, modules = 0, typedefs = 0, mtNonZero = 0, typesResolved = 0, typesSurvivingFilter = 0;
long staticFields = 0, addressHits = 0;
long tMap = 0, tGetType = 0, tNameFilter = 0, tStaticFields = 0, tGetAddress = 0;

var map2 = new Dictionary<ulong, (string, string, int)>(capacity: Math.Max(16, staticRootAddresses.Count));
var sw2 = Stopwatch.StartNew();
foreach (ClrAppDomain domain in heap.Runtime.AppDomains)
{
    domains++;
    int domainId = domain.Id;

    foreach (ClrModule module in domain.Modules)
    {
        modules++;
        long t0 = Stopwatch.GetTimestamp();
        foreach (var (mt, _) in module.EnumerateTypeDefToMethodTableMap())
        {
            tMap += Stopwatch.GetTimestamp() - t0;
            typedefs++;
            if (mt == 0) { t0 = Stopwatch.GetTimestamp(); continue; }
            mtNonZero++;

            long t1 = Stopwatch.GetTimestamp();
            ClrType? type = heap.GetTypeByMethodTable(mt);
            tGetType += Stopwatch.GetTimestamp() - t1;
            if (type is null) { t0 = Stopwatch.GetTimestamp(); continue; }
            typesResolved++;

            long t2 = Stopwatch.GetTimestamp();
            string typeName = type.Name ?? string.Empty;
            bool filtered = TypeFilterHelper.IsSystemType(typeName) || TypeFilterHelper.IsCompilerGenerated(typeName);
            tNameFilter += Stopwatch.GetTimestamp() - t2;
            if (filtered) { t0 = Stopwatch.GetTimestamp(); continue; }
            typesSurvivingFilter++;

            long t3 = Stopwatch.GetTimestamp();
            foreach (ClrStaticField field in type.StaticFields)
            {
                tStaticFields += Stopwatch.GetTimestamp() - t3;
                staticFields++;
                try
                {
                    if (string.IsNullOrEmpty(field.Name)) { t3 = Stopwatch.GetTimestamp(); continue; }
                    long t4 = Stopwatch.GetTimestamp();
                    ulong fieldAddress = field.GetAddress(domain);
                    tGetAddress += Stopwatch.GetTimestamp() - t4;
                    if (fieldAddress == 0) { t3 = Stopwatch.GetTimestamp(); continue; }
                    if (!staticRootAddresses.Contains(fieldAddress)) { t3 = Stopwatch.GetTimestamp(); continue; }
                    addressHits++;
                    map2[fieldAddress] = (typeName, field.Name, domainId);
                }
                catch { }
                t3 = Stopwatch.GetTimestamp();
            }
            t0 = Stopwatch.GetTimestamp();
        }
    }
}
sw2.Stop();

double f = 1000.0 / Stopwatch.Frequency;   // ticks -> ms
Console.WriteLine($"PHASE 2  BuildMapByRootAddress (cold)     {sw2.Elapsed.TotalSeconds,9:F2} s   {map2.Count:N0} map entries");
Console.WriteLine();
Console.WriteLine($"  ROOT PHASE TOTAL (1+2)                  {(sw1.Elapsed + sw2.Elapsed).TotalSeconds,9:F2} s");
Console.WriteLine($"    phase 1 share                         {100.0 * sw1.Elapsed.TotalSeconds / (sw1.Elapsed + sw2.Elapsed).TotalSeconds,8:F1} %");
Console.WriteLine($"    phase 2 share                         {100.0 * sw2.Elapsed.TotalSeconds / (sw1.Elapsed + sw2.Elapsed).TotalSeconds,8:F1} %");
Console.WriteLine();
Console.WriteLine("  Phase 2 breakdown by cost centre:");
Console.WriteLine($"    EnumerateTypeDefToMethodTableMap      {tMap * f / 1000,9:F2} s");
Console.WriteLine($"    heap.GetTypeByMethodTable             {tGetType * f / 1000,9:F2} s   <-- {mtNonZero:N0} calls");
Console.WriteLine($"    type.Name + IsSystem/IsCompilerGen    {tNameFilter * f / 1000,9:F2} s   <-- {typesResolved:N0} calls");
Console.WriteLine($"    type.StaticFields enumeration         {tStaticFields * f / 1000,9:F2} s");
Console.WriteLine($"    field.GetAddress                      {tGetAddress * f / 1000,9:F2} s");
Console.WriteLine();
Console.WriteLine("  Counts:");
Console.WriteLine($"    appdomains {domains:N0}   modules {modules:N0}   typedefs {typedefs:N0}");
Console.WriteLine($"    mt != 0 {mtNonZero:N0}   types resolved {typesResolved:N0}   survived name filter {typesSurvivingFilter:N0}");
Console.WriteLine($"    static fields examined {staticFields:N0}   address hits {addressHits:N0}");
Console.WriteLine();

// ---- phases 3-5: warm A/B of the real implementation against two variants --------------------
var sw3 = Stopwatch.StartNew();
var real = StaticFieldResolver.BuildMapByRootAddress(heap, staticRootAddresses);
sw3.Stop();

var sw4 = Stopwatch.StartNew();
var reordered = BuildReordered(heap, staticRootAddresses);
sw4.Stop();

var sw5 = Stopwatch.StartNew();
var prefiltered = BuildModulePrefiltered(heap, staticRootAddresses);
sw5.Stop();

Console.WriteLine("  WARM A/B (all three warm, so comparable to each other):");
Console.WriteLine($"    PHASE 3  real StaticFieldResolver     {sw3.Elapsed.TotalSeconds,9:F2} s   {real.Count:N0} entries");
Console.WriteLine($"    PHASE 4  reorder (semantics-safe)     {sw4.Elapsed.TotalSeconds,9:F2} s   {reordered.Count:N0} entries   " +
                  $"{(SameMap(real, reordered) ? "IDENTICAL" : "*** DIFFERS ***")}");
Console.WriteLine($"    PHASE 5  module prefilter (heuristic) {sw5.Elapsed.TotalSeconds,9:F2} s   {prefiltered.Count:N0} entries   " +
                  $"{(SameMap(real, prefiltered) ? "IDENTICAL" : "*** DIFFERS ***")}");
Console.WriteLine();
Console.WriteLine($"    cold/warm ratio for the same work: phase 2 {sw2.Elapsed.TotalSeconds:F2} s vs phase 3 {sw3.Elapsed.TotalSeconds:F2} s");
Console.WriteLine($"    phase 2 map == phase 3 map: {(SameMap(real, map2) ? "yes" : "*** NO ***")}");

return 0;

// Strictly semantics-preserving: the name filter is applied to exactly the same candidate set,
// just after the cheap address-membership test instead of before it. No behaviour change.
static Dictionary<ulong, (string TypeName, string FieldName, int AppDomainId)> BuildReordered(
    ClrHeap heap, IReadOnlySet<ulong> relevant)
{
    var map = new Dictionary<ulong, (string, string, int)>(capacity: Math.Max(16, relevant.Count));
    foreach (ClrAppDomain domain in heap.Runtime.AppDomains)
    {
        int domainId = domain.Id;
        foreach (ClrModule module in domain.Modules)
            foreach (var (mt, _) in module.EnumerateTypeDefToMethodTableMap())
            {
                if (mt == 0) continue;
                ClrType? type = heap.GetTypeByMethodTable(mt);
                if (type is null) continue;

                foreach (ClrStaticField field in type.StaticFields)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(field.Name)) continue;
                        ulong addr = field.GetAddress(domain);
                        if (addr == 0 || !relevant.Contains(addr)) continue;

                        // Only now is the name worth materialising.
                        string typeName = type.Name ?? string.Empty;
                        if (TypeFilterHelper.IsSystemType(typeName) || TypeFilterHelper.IsCompilerGenerated(typeName))
                            continue;
                        map[addr] = (typeName, field.Name, domainId);
                    }
                    catch { }
                }
            }
    }
    return map;
}

// Heuristic: skip framework modules outright, before touching any of their types.
static Dictionary<ulong, (string TypeName, string FieldName, int AppDomainId)> BuildModulePrefiltered(
    ClrHeap heap, IReadOnlySet<ulong> relevant)
{
    var map = new Dictionary<ulong, (string, string, int)>(capacity: Math.Max(16, relevant.Count));
    foreach (ClrAppDomain domain in heap.Runtime.AppDomains)
    {
        int domainId = domain.Id;
        foreach (ClrModule module in domain.Modules)
        {
            if (IsFrameworkModule(module.Name)) continue;

            foreach (var (mt, _) in module.EnumerateTypeDefToMethodTableMap())
            {
                if (mt == 0) continue;
                ClrType? type = heap.GetTypeByMethodTable(mt);
                if (type is null) continue;

                foreach (ClrStaticField field in type.StaticFields)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(field.Name)) continue;
                        ulong addr = field.GetAddress(domain);
                        if (addr == 0 || !relevant.Contains(addr)) continue;
                        string typeName = type.Name ?? string.Empty;
                        if (TypeFilterHelper.IsSystemType(typeName) || TypeFilterHelper.IsCompilerGenerated(typeName))
                            continue;
                        map[addr] = (typeName, field.Name, domainId);
                    }
                    catch { }
                }
            }
        }
    }
    return map;
}

static bool IsFrameworkModule(string? moduleName)
{
    if (string.IsNullOrEmpty(moduleName)) return false;
    string file = Path.GetFileName(moduleName);
    foreach (string p in new[] { "System.", "Microsoft.", "mscorlib", "netstandard", "WindowsBase", "PresentationCore", "PresentationFramework" })
        if (file.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
    return false;
}

static bool SameMap(Dictionary<ulong, (string, string, int)> a, Dictionary<ulong, (string, string, int)> b)
{
    if (a.Count != b.Count) return false;
    foreach (var kv in a)
        if (!b.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
    return true;
}
