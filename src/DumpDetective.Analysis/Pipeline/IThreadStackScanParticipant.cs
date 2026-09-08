using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Pipeline;

internal interface IThreadStackScanParticipant
{
    // Called by the pipeline before ThreadStackScanDispatcher.Run to size the single shared
    // per-thread frame buffer. The dispatcher walks max(all participants' counts) frames per
    // thread once; a participant that only needs the top frame should return 1, not the largest
    // window some other participant happens to need.
    int GetRequiredFrameCount(AnalysisContext context);

    void BeforeThreadStackScan(AnalysisContext context);
    void OnThreadStack(in ThreadStackSnapshot snapshot);
    void OnThreadStackScanCompleted(bool succeeded) { }
}
