using System;
using Xunit;

namespace DumpDetective.Tests.Integration.TraceIngest;

/// <summary>
/// Fact for tests that stream a real, large captured trace file. Unlike
/// <c>CacheDiscrepancies.DiscrepancyFactAttribute</c> (whose skip-by-default rationale is
/// specifically avoiding OOM from loading a full ClrMD dump twice), trace ingest is a bounded-memory
/// streaming pass by design — the concern here is purely wall-clock time against a large real file
/// (minutes, not seconds), so this is skipped by default for the same reason any slow
/// real-artifact-dependent test is: keep the fast suite fast. Reuses the same
/// <c>DD_RUN_DISCREPANCY_TESTS</c> opt-in switch rather than adding a second one, since both
/// categories are "slow test against a real captured artifact, opt-in only."
/// </summary>
public sealed class RealTraceFactAttribute : FactAttribute
{
    public RealTraceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DD_RUN_DISCREPANCY_TESTS") != "1")
        {
            Skip = "Real-trace tests are opt-in only. Set DD_RUN_DISCREPANCY_TESTS=1 to run them.";
        }
    }
}
