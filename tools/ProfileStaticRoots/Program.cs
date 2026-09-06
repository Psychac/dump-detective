// Sizes the static-root fix (docs/cache/cache-ideal-design.md §11.6).
//
// RootIndexWriter tries to find static roots by filtering heap.EnumerateRoots() for kind 9/10.
// ClrRootKind has no such members in ClrMD 3.1 OR 4.0 — it stops at 8 — so the predicate never
// fires and every static-root consumer sees an empty set. The persisted Roots sections confirm it:
// only kinds 2/3/4/7/8 appear, 0 of 1,411 and 0 of 5,037 match.
//
// The fix is to SYNTHESIZE static roots by enumerating static fields directly, which is how
// ClrMD 3+ exposes them. This tool measures what that would produce, so the fix is not written
// blind: how many static object fields exist, how many hold a live reference, whether their
// addresses look sane, and how much the Roots section would grow.
//
// Also reports the system/non-system split, because the naming trailer filters framework types
// (noise reduction) while ROOTS arguably should not — a framework static roots a leak just as well.

using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;

string dumpPath = args.Length > 0
    ? args[0]
    : @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

if (!File.Exists(dumpPath))
{
    Console.Error.WriteLine($"Dump not found: {dumpPath}");
    return 1;
}

using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
ClrHeap heap = runtime.Heap;

Console.WriteLine($"Dump: {Path.GetFileName(dumpPath)}  ({new FileInfo(dumpPath).Length / (1024.0 * 1024 * 1024):F2} GB)");

long typedefs = 0, typesResolved = 0, staticFields = 0, objectRefFields = 0;
long addressable = 0, nonNull = 0, systemOwned = 0, nonSystemOwned = 0, distinctTargets = 0;
long threadStatics = 0;
var targets = new HashSet<ulong>();
var fieldAddresses = new HashSet<ulong>();
var samples = new List<(string Type, string Field, ulong FieldAddr, ulong Target, string TargetType)>();
ulong minFieldAddr = ulong.MaxValue, maxFieldAddr = 0;

string[] systemPrefixes = { "System.", "Microsoft.", "MS.", "Internal.", "Windows.", "Interop.", "FxResources.", "System_Private_CoreLib" };
bool IsSystem(string? n) => n is not null && systemPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase));

var sw = Stopwatch.StartNew();
foreach (ClrAppDomain domain in runtime.AppDomains)
{
    foreach (ClrModule module in domain.Modules)
    {
        foreach (var (mt, _) in module.EnumerateTypeDefToMethodTableMap())
        {
            typedefs++;
            if (mt == 0) continue;
            ClrType? type = heap.GetTypeByMethodTable(mt);
            if (type is null) continue;
            typesResolved++;
            bool sys = IsSystem(type.Name);

            foreach (ClrStaticField field in type.StaticFields)
            {
                staticFields++;
                try
                {
                    if (!field.IsObjectReference) continue;
                    objectRefFields++;

                    ulong fieldAddr = field.GetAddress(domain);
                    if (fieldAddr == 0) continue;
                    addressable++;
                    fieldAddresses.Add(fieldAddr);
                    if (fieldAddr < minFieldAddr) minFieldAddr = fieldAddr;
                    if (fieldAddr > maxFieldAddr) maxFieldAddr = fieldAddr;

                    ClrObject obj = field.ReadObject(domain);
                    if (!obj.IsValid || obj.Address == 0) continue;
                    nonNull++;
                    if (sys) systemOwned++; else nonSystemOwned++;
                    targets.Add(obj.Address);

                    if (samples.Count < 12 && !sys)
                        samples.Add((type.Name ?? "?", field.Name ?? "?", fieldAddr, obj.Address, obj.Type?.Name ?? "?"));
                }
                catch { }
            }
        }
    }
}
sw.Stop();
distinctTargets = targets.Count;

Console.WriteLine($"Static-field walk: {sw.Elapsed.TotalSeconds:F2} s");
Console.WriteLine();
Console.WriteLine($"  typedefs                            {typedefs,10:N0}");
Console.WriteLine($"  types resolved                      {typesResolved,10:N0}");
Console.WriteLine($"  static fields (all)                 {staticFields,10:N0}");
Console.WriteLine($"  ... object-reference fields         {objectRefFields,10:N0}");
Console.WriteLine($"  ... with a non-zero field address   {addressable,10:N0}");
Console.WriteLine($"  ... holding a live object  ***      {nonNull,10:N0}   <- synthesized static roots");
Console.WriteLine($"        owned by System.*/Microsoft.* {systemOwned,10:N0}");
Console.WriteLine($"        owned by app types            {nonSystemOwned,10:N0}");
Console.WriteLine($"  distinct field addresses            {fieldAddresses.Count,10:N0}   (root keys — must be unique)");
Console.WriteLine($"  distinct target objects             {distinctTargets,10:N0}");
Console.WriteLine();
Console.WriteLine($"  field address range   0x{minFieldAddr:x} .. 0x{maxFieldAddr:x}");
Console.WriteLine($"  Roots section growth: {nonNull} x 20 B = {nonNull * 20 / 1024.0:N1} KiB");
Console.WriteLine();
Console.WriteLine("  Sample app-owned static roots:");
foreach (var (t, f, fa, ta, tt) in samples)
    Console.WriteLine($"    0x{fa:x12} -> 0x{ta:x12}  {Trim(t, 46)}.{Trim(f, 24)}  : {Trim(tt, 34)}");

return 0;

static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "~";
