using System;
using System.Collections.Generic;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers.EventLeak;

/// <summary>
/// Per-analysis (not process-lifetime) event-name-set resolver. Replaces
/// <c>EventLeakAnalyzer</c>'s former <c>static ConcurrentDictionary</c> cache (audit P1-2) —
/// this instance is created fresh per analysis and owned by <see cref="PublisherRegistry"/>.
/// </summary>
internal sealed class EventNameResolver
{
    private readonly Dictionary<ulong, HashSet<string>> _cache = new(capacity: 8192);

    public HashSet<string> GetEventNames(ClrType type)
    {
        ulong mt = type.MethodTable;
        if (_cache.TryGetValue(mt, out var cached))
            return cached;

        // Step 1: collect only the concrete type's own add_/remove_ pairs.
        var ownAddNames = new HashSet<string>(StringComparer.Ordinal);
        var ownRemoveNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in type.Methods)
        {
            var name = method.Name;
            if (name == null) continue;

            if (name.StartsWith("add_", StringComparison.Ordinal) && name.Length > 4)
                ownAddNames.Add(name[4..]);
            else if (name.StartsWith("remove_", StringComparison.Ordinal) && name.Length > 7)
                ownRemoveNames.Add(name[7..]);
        }

        var ownEvents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in ownAddNames)
            if (ownRemoveNames.Contains(e)) ownEvents.Add(e);

        // Step 2: if this type declares NO own events, cache empty to mean "all-pass".
        if (ownEvents.Count == 0)
        {
            _cache[mt] = ownEvents;
            return ownEvents;
        }

        // Step 3: include inherited add/remove pairs so inherited backing fields are not lost.
        var allAddNames = new HashSet<string>(ownAddNames, StringComparer.Ordinal);
        var allRemoveNames = new HashSet<string>(ownRemoveNames, StringComparer.Ordinal);

        ClrType? current = type.BaseType;
        while (current != null
            && current.Name != "System.Object"
            && current.Name != "System.Delegate"
            && current.Name != "System.MulticastDelegate")
        {
            foreach (var method in current.Methods)
            {
                var name = method.Name;
                if (name == null) continue;

                if (name.StartsWith("add_", StringComparison.Ordinal) && name.Length > 4)
                    allAddNames.Add(name[4..]);
                else if (name.StartsWith("remove_", StringComparison.Ordinal) && name.Length > 7)
                    allRemoveNames.Add(name[7..]);
            }
            current = current.BaseType;
        }

        var names = EventLeakAnalyzer.BuildEventNameSet(allAddNames, allRemoveNames);
        _cache[mt] = names;
        return names;
    }
}
