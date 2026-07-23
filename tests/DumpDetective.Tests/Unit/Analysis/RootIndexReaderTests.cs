using System.Buffers.Binary;

using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class RootIndexReaderTests
{
    [Fact]
    public void ReadRootIndexFile_ShouldReturnEmpty_WhenFileMissing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"missing-roots-{Guid.NewGuid():N}.bin");

        List<(ulong TargetAddr, ulong RootAddr, byte Kind)> roots = RootIndexReader.ReadRootIndexFile(path, CancellationToken.None);

        roots.Should().BeEmpty();
    }

    [Fact]
    public void ReadRootIndexFile_ShouldParseRecords_WhenHeaderAndBodyValid()
    {
        string path = CreateTempRootIndexFile(
            (0x111UL, 0xAAAUL, 2),
            (0x222UL, 0xBBBUL, 4));

        try
        {
            List<(ulong TargetAddr, ulong RootAddr, byte Kind)> roots = RootIndexReader.ReadRootIndexFile(path, CancellationToken.None);

            roots.Should().HaveCount(2);
            roots[0].Should().Be((0x111UL, 0xAAAUL, (byte)2));
            roots[1].Should().Be((0x222UL, 0xBBBUL, (byte)4));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadRootTargets_ShouldMapKindBytesToExpectedNames()
    {
        string path = CreateTempRootIndexFile(
            (0x10UL, 0x20UL, 1),
            (0x11UL, 0x21UL, 2),
            (0x12UL, 0x22UL, 7),
            (0x13UL, 0x23UL, 99));

        try
        {
            List<(string RootKind, ulong Address)> roots = RootIndexReader.ReadRootTargets(path, CancellationToken.None);

            roots.Should().ContainInOrder(
                ("FinalizerQueue", 0x10UL),
                ("StrongHandle", 0x11UL),
                ("AsyncPinnedHandle", 0x12UL),
                ("Unknown(99)", 0x13UL));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateTempRootIndexFile(params (ulong Target, ulong Root, byte Kind)[] records)
    {
        string path = Path.Combine(Path.GetTempPath(), $"root-index-{Guid.NewGuid():N}.bin");

        var writer = new CacheContainerWriter(path);
        try
        {
            writer.BeginSection(CacheSectionId.Roots);

            var indexHeader = new IndexHeader(0x58495452, 1, records.Length);
            indexHeader.WriteTo(writer.Stream);

            for (int i = 0; i < records.Length; i++)
            {
                (ulong target, ulong root, byte kind) = records[i];
                byte[] record = new byte[20];
                BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0, 8), target);
                BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8, 8), root);
                record[16] = kind;
                writer.Stream.Write(record, 0, record.Length);
            }

            writer.EndSection(records.Length);
            writer.Finish();
        }
        catch
        {
            writer.Dispose();
            throw;
        }

        return path;
    }
}
