using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// One method's leaf-frame CPU-sample attribution over a trace capture — see
/// <see cref="ITraceCpuSampleQuery"/>. Exclusive-only, leaf-attributed: no inclusive/call-tree
/// breakdown, since that needs the full call stack this capability's underlying section
/// deliberately doesn't carry (see <c>CacheSectionId.TraceCpuSamples</c>'s remarks).
/// </summary>
public readonly record struct CpuSampleAttribution(
    MethodRef Method,
    long SampleCount,
    TimeSpan FirstSeenProcessUptime,
    TimeSpan LastSeenProcessUptime);

/// <summary>The <c>trace.cpu-samples</c> capability.</summary>
public interface ITraceCpuSampleQuery
{
    IEnumerable<CpuSampleAttribution> EnumerateSampleAttributions();
}
