using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal class GCGenerationAnalyzer
    {
        private readonly OutputWriter _writer;

        public GCGenerationAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Analyze(ClrHeap heap)
        {
            _writer.WriteHeader("GC GENERATIONS BREAKDOWN:");

            var gen2Objects = new Dictionary<string, GenStats>();
            var lohObjects = new Dictionary<string, GenStats>();

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type == null)
                    continue;

                string typeName = obj.Type.Name ?? "Unknown";
                ulong size = obj.Size;

                Dictionary<string, GenStats> targetGen;
                if (size >= 85000)
                {
                    targetGen = lohObjects;
                }
                else
                {
                    targetGen = gen2Objects;
                }

                if (!targetGen.ContainsKey(typeName))
                {
                    targetGen[typeName] = new GenStats { TypeName = typeName };
                }
                targetGen[typeName].Count++;
                targetGen[typeName].TotalSize += size;
            }

            PrintSummary(gen2Objects, lohObjects);
            PrintTopTypes(gen2Objects);

            _writer.WriteLine($"\n{new string('=', 80)}");
        }

        private void PrintSummary(Dictionary<string, GenStats> gen2Objects, Dictionary<string, GenStats> lohObjects)
        {
            int totalGen2 = gen2Objects.Values.Sum(s => s.Count);
            int totalLOH = lohObjects.Values.Sum(s => s.Count);

            ulong sizeGen2 = (ulong)gen2Objects.Values.Sum(s => (long)s.TotalSize);
            ulong sizeLOH = (ulong)lohObjects.Values.Sum(s => (long)s.TotalSize);

            _writer.WriteLine("\nHeap Summary:");
            _writer.WriteLine($"  Small/Medium Objects (< 85KB): {totalGen2,12:N0} objects  {FormatHelper.FormatBytes(sizeGen2),12}");
            _writer.WriteLine($"  Large Objects (LOH >= 85KB):   {totalLOH,12:N0} objects  {FormatHelper.FormatBytes(sizeLOH),12}");
            _writer.WriteLine($"  Total:                          {totalGen2 + totalLOH,12:N0} objects  {FormatHelper.FormatBytes(sizeGen2 + sizeLOH),12}");
        }

        private void PrintTopTypes(Dictionary<string, GenStats> gen2Objects)
        {
            if (gen2Objects.Any())
            {
                _writer.WriteLine("\nTop 15 Object Types by Count (potential leak sources if excessive):");
                _writer.WriteLine($"{"Type",-60} {"Count",12} {"Size",12}");
                _writer.WriteSeparator();
                foreach (var stat in gen2Objects.Values.OrderByDescending(s => s.Count).Take(15))
                {
                    _writer.WriteLine($"{FormatHelper.TruncateString(stat.TypeName, 60),-60} {stat.Count,12:N0} {FormatHelper.FormatBytes(stat.TotalSize),12}");
                }
            }
        }
    }
}
