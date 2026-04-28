using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Indexing;

internal interface IObjectIndexWriter
{
    HeapIndexBuildResult Build(
        ClrHeap heap,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null,
        string? dumpPath = null,
        DumpSizeTier sizeTier = DumpSizeTier.Medium);
}
