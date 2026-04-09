using Microsoft.Diagnostics.Runtime;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal interface IAnalyzer
    {
        string Name { get; }
        void Execute(AnalysisContext context);
    }

    internal class AnalysisContext
    {
        public required ClrRuntime Runtime { get; init; }
        public required ClrHeap Heap { get; init; }
        public required HeapAnalysisCache Cache { get; init; }
    }
}
