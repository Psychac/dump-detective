using Microsoft.Diagnostics.Runtime;

// Throwaway probe: checks whether two dumps ran the same build of the app, by comparing PDB
// (Guid, Age) of managed modules whose name contains a filter string. PDB Guid is assigned per
// compilation, so an identical Guid across two dumps means identical build; used to sanity-check
// whether an EntityJoinSpike "cross-build" comparison is actually cross-build.
//
// Usage: ModuleTimestampProbe <dump-path> <module-name-filter>

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: ModuleTimestampProbe <dump-path> <module-name-filter>");
    return 1;
}

string dumpPath = args[0];
string filter = args[1];

using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
ClrInfo clrInfo = dataTarget.ClrVersions[0];
using ClrRuntime runtime = clrInfo.CreateRuntime();

foreach (ClrModule module in runtime.EnumerateModules())
{
    string name = module.Name ?? module.AssemblyName ?? "<unknown>";
    if (filter != "*" && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
        continue;

    Console.WriteLine(name);
    Console.WriteLine($"  Size: {module.Size}");
    PdbInfo? pdb = module.Pdb;
    if (pdb is not null)
        Console.WriteLine($"  Pdb: {pdb.Path} Guid={pdb.Guid} Age={pdb.Revision}");
    else
        Console.WriteLine("  Pdb: <none>");
}

return 0;
