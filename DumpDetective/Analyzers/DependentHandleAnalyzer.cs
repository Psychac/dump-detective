using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;
using System.Reflection;

namespace DumpDetective.Analyzers
{
    internal class DependentHandleAnalyzer
    {
        private const int TopCount = 15;
        private readonly OutputWriter _writer;

        public DependentHandleAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Analyze(ClrRuntime runtime)
        {
            _writer.WriteHeader("DEPENDENT HANDLE ANALYSIS:");
            _writer.WriteLine("Analyzing dependent handles (ConditionalWeakTable-style retention edges)...\n");

            int dependentHandleCount = 0;
            int resolvedEdgeCount = 0;
            int unresolvedTargetCount = 0;

            var sourceTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var targetTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var sourceTargetPairCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (ClrHandle handle in runtime.EnumerateHandles())
            {
                string kind = handle.HandleKind.ToString();
                if (!kind.Contains("Dependent", StringComparison.OrdinalIgnoreCase))
                    continue;

                dependentHandleCount++;

                if (!TryGetHandleObject(handle.Object, runtime.Heap, out ClrObject sourceObj))
                {
                    unresolvedTargetCount++;
                    continue;
                }

                string sourceType = sourceObj.Type?.Name ?? StringConstants.UnknownType;
                Increment(sourceTypeCounts, sourceType);

                if (!TryGetDependentTargetObject(handle, runtime.Heap, out ClrObject targetObj))
                {
                    unresolvedTargetCount++;
                    continue;
                }

                resolvedEdgeCount++;
                string targetType = targetObj.Type?.Name ?? StringConstants.UnknownType;
                Increment(targetTypeCounts, targetType);
                Increment(sourceTargetPairCounts, $"{sourceType} -> {targetType}");
            }

            _writer.WriteLine("DEPENDENT HANDLE SUMMARY:");
            _writer.WriteSeparator();
            _writer.WriteLine($"Dependent Handles Found: {dependentHandleCount:N0}");
            _writer.WriteLine($"Resolved Source->Target Edges: {resolvedEdgeCount:N0}");
            _writer.WriteLine($"Unresolved Targets: {unresolvedTargetCount:N0}");

            if (dependentHandleCount == 0)
            {
                _writer.WriteLine("\nNo dependent handles were found in this dump.");
                _writer.WriteLine(StringConstants.Equals80);
                return;
            }

            PrintTop("TOP SOURCE TYPES IN DEPENDENT HANDLES:", sourceTypeCounts);
            PrintTop("TOP TARGET TYPES KEPT ALIVE BY DEPENDENT HANDLES:", targetTypeCounts);
            PrintTop("TOP SOURCE -> TARGET RETENTION EDGES:", sourceTargetPairCounts);

            _writer.WriteLine("\nNote: Some runtimes do not expose dependent targets via DAC. Unresolved targets are expected in that case.");
            _writer.WriteLine(StringConstants.Equals80);
        }

        private void PrintTop(string title, Dictionary<string, int> counts)
        {
            _writer.WriteLine($"\n{title}");
            _writer.WriteSeparator();

            if (counts.Count == 0)
            {
                _writer.WriteLine("No data.");
                return;
            }

            _writer.WriteLine($"{"Item",-90} {"Count",12}");
            _writer.WriteSeparator();

            int written = 0;
            foreach ((string key, int count) in counts.OrderByDescending(c => c.Value))
            {
                if (written >= TopCount)
                    break;

                _writer.WriteLine($"{FormatHelper.TruncateString(key, 90),-90} {count,12:N0}");
                written++;
            }
        }

        private static bool TryGetDependentTargetObject(ClrHandle handle, ClrHeap heap, out ClrObject targetObj)
        {
            targetObj = default;

            string[] propertyCandidates =
            [
                "DependentTarget",
                "Target",
                "Secondary",
                "DependentObject",
                "Dependent"
            ];

            Type handleType = handle.GetType();
            foreach (string propertyName in propertyCandidates)
            {
                PropertyInfo? property = handleType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                if (property == null)
                    continue;

                object? value = property.GetValue(handle);
                if (value == null)
                    continue;

                if (TryGetHandleObject(value, heap, out targetObj))
                    return true;
            }

            return false;
        }

        private static bool TryGetHandleObject(object value, ClrHeap heap, out ClrObject obj)
        {
            obj = default;

            if (value is ClrObject clrObject)
            {
                if (!clrObject.IsValid || clrObject.Type == null)
                    return false;

                obj = clrObject;
                return true;
            }

            if (value is ulong address && address != 0)
            {
                ClrObject fromAddress = heap.GetObject(address);
                if (fromAddress.IsValid && fromAddress.Type != null)
                {
                    obj = fromAddress;
                    return true;
                }
            }

            return false;
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            if (counts.TryGetValue(key, out int count))
                counts[key] = count + 1;
            else
                counts[key] = 1;
        }
    }
}
