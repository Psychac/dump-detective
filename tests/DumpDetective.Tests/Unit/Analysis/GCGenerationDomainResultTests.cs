using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class GCGenerationDomainResultTests
{
    [Fact]
    public void SohTotal_SumsGen0Gen1Gen2Bytes_ExcludingLohAndPoh()
    {
        GCGenerationDomainResult result = new(
            Gen0Bytes: 1_000, Gen0Objects: 10,
            Gen1Bytes: 2_000, Gen1Objects: 20,
            Gen2Bytes: 3_000, Gen2Objects: 30,
            LohBytes: 50_000, LohPercent: 0,
            TotalObjects: 60, LohObjects: 5,
            TopLohTypes: System.Array.Empty<TypeSnapshot>(),
            PohBytes: 7_000, PohObjects: 1);

        result.SohTotal.Should().Be(6_000UL);
    }
}
