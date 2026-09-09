using System.Buffers.Binary;
using System.Text;

using DumpDetective.Platform.Storage.Container;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sources.NetTrace;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Integration.TraceIngest;

/// <summary>
/// End-to-end verification of Phase 6a's first slice (trace.methods) against a real capture —
/// see docs/refactor/modularity/phase-6-trace-source.md § Phase 6a.
/// </summary>
public sealed class TraceMethodIndexerRealTraceTests : IDisposable
{
    private static string TracePath => Environment.GetEnvironmentVariable("DD_BENCHMARK_ETL")
        ?? @"D:\Dumps\08-05\etls\HighCPU_11.etl";

    private readonly string _containerPath;

    public TraceMethodIndexerRealTraceTests()
    {
        _containerPath = Path.Combine(Path.GetTempPath(), $"trace-methods-test-{Guid.NewGuid():N}.bin");
    }

    public void Dispose()
    {
        if (File.Exists(_containerPath))
            File.Delete(_containerPath);
    }

    [RealTraceFact]
    public void Build_RealEtlCapture_ProducesReadableMethodsSection()
    {
        File.Exists(TracePath).Should().BeTrue($"expected a real .etl at {TracePath} — see DD_BENCHMARK_ETL to override.");

        TraceIndexBuilder.Build(TracePath, _containerPath, targetProcessId: null);

        CacheContainerReader.TryOpen(_containerPath, out CacheContainerReader? reader).Should().BeTrue();
        reader.Should().NotBeNull();
        reader!.ContainsSection(CacheSectionId.TraceMethods).Should().BeTrue();
        reader.TryGetSectionInfo(CacheSectionId.TraceMethods, out CacheTocEntry entry).Should().BeTrue();
        entry.RecordCount.Should().BeGreaterThan(0);

        reader.TryOpenSection(CacheSectionId.TraceMethods, out Stream? sectionStream).Should().BeTrue();
        sectionStream.Should().NotBeNull();

        List<(long MethodId, long ModuleId, ulong StartAddress, int Size, int Token, MatchFidelity Fidelity, string TypeName, string MethodName, string Signature)> records = ReadAllRecords(sectionStream!, (int)Math.Min(entry.RecordCount, 500));

        records.Should().NotBeEmpty();
        records.Should().OnlyHaveUniqueItems(r => r.MethodId, "each MethodID is deduplicated to a single record");
        records.Should().Contain(r => !string.IsNullOrEmpty(r.MethodName), "every real record should carry a method name");

        // The real, measured case from tools/MethodEventProbe: IL_STUB_PInvoke methods report
        // IsDynamic and must come through as None fidelity, not guessed as Exact.
        records.Where(r => r.MethodName == "IL_STUB_PInvoke")
            .Should().OnlyContain(r => r.Fidelity == MatchFidelity.None);

        // A normal, non-dynamic method should canonicalize at Exact fidelity.
        records.Where(r => r.MethodName != "IL_STUB_PInvoke" && !r.TypeName.Contains("dynamicClass", StringComparison.Ordinal))
            .Should().Contain(r => r.Fidelity == MatchFidelity.Exact);
    }

    private static List<(long, long, ulong, int, int, MatchFidelity, string, string, string)> ReadAllRecords(Stream stream, int maxRecords)
    {
        var results = new List<(long, long, ulong, int, int, MatchFidelity, string, string, string)>(maxRecords);
        Span<byte> fixedFields = stackalloc byte[8 + 8 + 8 + 4 + 4 + 1];

        for (int i = 0; i < maxRecords; i++)
        {
            int read = stream.ReadAtLeast(fixedFields, fixedFields.Length, throwOnEndOfStream: false);
            if (read < fixedFields.Length)
                break;

            long methodId = BinaryPrimitives.ReadInt64LittleEndian(fixedFields);
            long moduleId = BinaryPrimitives.ReadInt64LittleEndian(fixedFields[8..]);
            ulong startAddress = BinaryPrimitives.ReadUInt64LittleEndian(fixedFields[16..]);
            int size = BinaryPrimitives.ReadInt32LittleEndian(fixedFields[24..]);
            int token = BinaryPrimitives.ReadInt32LittleEndian(fixedFields[28..]);
            var fidelity = (MatchFidelity)fixedFields[32];

            string typeName = ReadLengthPrefixedUtf8(stream);
            string methodName = ReadLengthPrefixedUtf8(stream);
            string signature = ReadLengthPrefixedUtf8(stream);

            results.Add((methodId, moduleId, startAddress, size, token, fidelity, typeName, methodName, signature));
        }

        return results;
    }

    private static string ReadLengthPrefixedUtf8(Stream stream)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        stream.ReadExactly(lenBuf);
        int length = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);

        byte[] bytes = new byte[length];
        stream.ReadExactly(bytes);
        return Encoding.UTF8.GetString(bytes);
    }
}
