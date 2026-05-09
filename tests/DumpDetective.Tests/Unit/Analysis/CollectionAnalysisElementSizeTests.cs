using DumpDetective.Analysis.Analyzers;
using FluentAssertions;
using Microsoft.Diagnostics.Runtime;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class CollectionAnalysisElementSizeTests
{
    [Fact]
    public void ResolveElementSizeFromComponentInfo_ValueType_ReturnsStaticSize()
    {
        ulong size = CollectionAnalysisHelpers.ResolveElementSizeFromComponentInfo(hasComponentType: true, componentIsValueType: true, componentStaticSize: 24, fallbackArraySize: 0, capacity: 0);
        size.Should().Be(24UL);
    }

    [Fact]
    public void ResolveElementSizeFromComponentInfo_ReferenceType_ReturnsPointerSize()
    {
        ulong size = CollectionAnalysisHelpers.ResolveElementSizeFromComponentInfo(hasComponentType: true, componentIsValueType: false, componentStaticSize: 0, fallbackArraySize: 0, capacity: 0);
        size.Should().Be((ulong)System.IntPtr.Size);
    }

    [Fact]
    public void ResolveElementSizeFromComponentInfo_FallbackUsesArraySize()
    {
        ulong size = CollectionAnalysisHelpers.ResolveElementSizeFromComponentInfo(hasComponentType: false, componentIsValueType: false, componentStaticSize: 0, fallbackArraySize: 800UL, capacity: 10);
        size.Should().Be(80UL);
    }

    [Fact]
    public void ResolveElementSizeFromClrType_FallbackUsesArraySize()
    {
        ulong size = CollectionAnalysisHelpers.ResolveElementSizeFromClrType(null, fallbackArraySize: 1024UL, capacity: 16);
        size.Should().Be(64UL);
    }
}
