using System;
using Xunit;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Fact for disk-vs-memory cache discrepancy tests. These load a full real dump
/// (potentially 1GB-25GB+) and build two heap indexes each, so they are skipped by
/// default to avoid OOM when the suite runs. Set DD_RUN_DISCREPANCY_TESTS=1 to opt in.
/// </summary>
public sealed class DiscrepancyFactAttribute : FactAttribute
{
    public DiscrepancyFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DD_RUN_DISCREPANCY_TESTS") != "1")
        {
            Skip = "Discrepancy tests are opt-in only. Set DD_RUN_DISCREPANCY_TESTS=1 to run them.";
        }
    }
}
