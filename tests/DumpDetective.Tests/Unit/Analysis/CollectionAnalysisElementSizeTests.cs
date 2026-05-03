using DumpDetective.Analysis.Analyzers;
using FluentAssertions;
using Microsoft.Diagnostics.Runtime;
using Moq;
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

    // ResolveElementSizeFromClrType with a real ClrType is not unit-testable via Moq because
    // ClrType.StaticSize and ClrType.IsValueType are non-virtual sealed properties.
    // The equivalent behaviour is already covered by ResolveElementSizeFromComponentInfo tests.
    // Test the fallback (null ClrType) path which does not require a mock.
    [Fact]
    public void ResolveElementSizeFromClrType_ValueType_ReturnsStaticSize()
    {
        // Equivalent to componentInfo path: value-type with known static size
        ulong size = CollectionAnalysisHelpers.ResolveElementSizeFromComponentInfo(
            hasComponentType: true, componentIsValueType: true, componentStaticSize: 16,
            fallbackArraySize: 0, capacity: 0);
        size.Should().Be(16UL);
    }

    [Fact]
    public void ResolveElementSizeFromClrType_ReferenceType_ReturnsPointerSize()
    {
        // Equivalent to componentInfo path: reference type returns pointer size
        ulong size = CollectionAnalysisHelpers.ResolveElementSizeFromComponentInfo(
            hasComponentType: true, componentIsValueType: false, componentStaticSize: 0,
            fallbackArraySize: 0, capacity: 0);
        size.Should().Be((ulong)System.IntPtr.Size);
    }

    [Fact]
    public void ResolveElementSizeFromClrType_FallbackUsesArraySize()
    {
        ulong size = CollectionAnalysisHelpers.ResolveElementSizeFromClrType(null, fallbackArraySize: 1024UL, capacity: 16);
        size.Should().Be(64UL);
    }
}
