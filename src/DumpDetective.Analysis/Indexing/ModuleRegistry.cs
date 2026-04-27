using Microsoft.Diagnostics.Runtime;
using System.Collections.Generic;

namespace DumpDetective.Analysis.Indexing
{
    internal sealed class ModuleRegistry
    {
        private readonly Dictionary<ulong, int> _moduleIdMap = new();
        private readonly List<ModuleInfo> _modules = new();

        public IReadOnlyList<ModuleInfo> Modules => _modules;

        public int GetOrAdd(ClrModule? module)
        {
            if (module is null)
                return -1;

            lock (_modules)
            {
                if (_moduleIdMap.TryGetValue(module.Address, out var id))
                    return id;

                id = _modules.Count;
                _moduleIdMap[module.Address] = id;

                _modules.Add(new ModuleInfo
                {
                    Id = id,
                    Name = module.Name ?? string.Empty,
                    AssemblyName = module.AssemblyName ?? string.Empty,
                });

                return id;
            }
        }
    }

    internal sealed class ModuleInfo
    {
        public int Id;
        public string? Name;
        public string? AssemblyName;
    }
}
