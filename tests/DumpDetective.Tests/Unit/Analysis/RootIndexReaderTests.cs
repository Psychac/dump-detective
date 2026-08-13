using System.Buffers.Binary;
using System.Text;

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

    [Fact]
    public void ReadRootIndexFile_ShouldReturnEmpty_WhenHeaderVersionIsStaleV1()
    {
        // A pre-trailer (v1) RootIndex.bin should be treated as a cold cache, not parsed
        // partially — see docs/analysis/root-field-name-index-plan.md.
        string path = Path.Combine(Path.GetTempPath(), $"root-index-{Guid.NewGuid():N}.bin");
        var writer = new CacheContainerWriter(path);
        try
        {
            writer.BeginSection(CacheSectionId.Roots);
            new IndexHeader(0x58495452, version: 1, recordCount: 1).WriteTo(writer.Stream);
            byte[] record = new byte[20];
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0, 8), 0x111UL);
            writer.Stream.Write(record, 0, record.Length);
            writer.EndSection(1);
            writer.Finish();
        }
        catch
        {
            writer.Dispose();
            throw;
        }

        try
        {
            RootIndexReader.ReadRootIndexFile(path, CancellationToken.None).Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadRootFieldNames_ShouldReturnEmpty_WhenFileMissing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"missing-roots-{Guid.NewGuid():N}.bin");

        var names = RootIndexReader.ReadRootFieldNames(MakeIndexResult(path), CancellationToken.None);

        names.Should().BeEmpty();
    }

    [Fact]
    public void ReadRootFieldNames_ShouldParseTrailer_AfterFixedRecords()
    {
        string path = CreateTempRootIndexFileWithFieldNameTrailer(
            roots: new[] { (0x111UL, 0xAAAUL, (byte)10), (0x222UL, 0xBBBUL, (byte)9) },
            names: new[] { (0xAAAUL, "MyNamespace.MyType", "s_cache", 1), (0xBBBUL, "Other.Type", "t_local", 3) });

        try
        {
            var names = RootIndexReader.ReadRootFieldNames(MakeIndexResult(path), CancellationToken.None);

            names.Should().HaveCount(2);
            names[0xAAAUL].Should().Be(("MyNamespace.MyType", "s_cache", 1));
            names[0xBBBUL].Should().Be(("Other.Type", "t_local", 3));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadRootFieldNames_ShouldReturnEmpty_WhenNoTrailerWritten()
    {
        string path = CreateTempRootIndexFileWithFieldNameTrailer(
            roots: new[] { (0x111UL, 0xAAAUL, (byte)2) },
            names: Array.Empty<(ulong, string, string, int)>());

        try
        {
            RootIndexReader.ReadRootFieldNames(MakeIndexResult(path), CancellationToken.None).Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static HeapIndexBuildResult MakeIndexResult(string path) =>
        new(HeapIndexStorageKind.Disk, path, ObjectCount: 0, Elapsed: TimeSpan.Zero,
            TypeAggregates: new Dictionary<ulong, TypeAggregateIndexEntry>());

    private static string CreateTempRootIndexFile(params (ulong Target, ulong Root, byte Kind)[] records)
    {
        string path = Path.Combine(Path.GetTempPath(), $"root-index-{Guid.NewGuid():N}.bin");

        var writer = new CacheContainerWriter(path);
        try
        {
            writer.BeginSection(CacheSectionId.Roots);

            var indexHeader = new IndexHeader(0x58495452, 2, records.Length);
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

    private static string CreateTempRootIndexFileWithFieldNameTrailer(
        (ulong Target, ulong Root, byte Kind)[] roots,
        (ulong RootAddr, string OwnerType, string FieldName, int AppDomainId)[] names)
    {
        string path = Path.Combine(Path.GetTempPath(), $"root-index-{Guid.NewGuid():N}.bin");

        var writer = new CacheContainerWriter(path);
        try
        {
            writer.BeginSection(CacheSectionId.Roots);
            long baseOffset = writer.Stream.Position;

            var indexHeader = new IndexHeader(0x58495452, 2, roots.Length);
            indexHeader.WriteTo(writer.Stream);

            for (int i = 0; i < roots.Length; i++)
            {
                (ulong target, ulong root, byte kind) = roots[i];
                byte[] record = new byte[20];
                BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0, 8), target);
                BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8, 8), root);
                record[16] = kind;
                writer.Stream.Write(record, 0, record.Length);
            }

            for (int i = 0; i < names.Length; i++)
            {
                (ulong rootAddr, string ownerType, string fieldName, int appDomainId) = names[i];
                byte[] ownerBytes = Encoding.UTF8.GetBytes(ownerType);
                byte[] fieldBytes = Encoding.UTF8.GetBytes(fieldName);
                byte[] record = new byte[16 + ownerBytes.Length + fieldBytes.Length];
                BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0, 8), rootAddr);
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8, 2), (ushort)ownerBytes.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(10, 2), (ushort)fieldBytes.Length);
                BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(12, 4), appDomainId);
                ownerBytes.CopyTo(record.AsSpan(16));
                fieldBytes.CopyTo(record.AsSpan(16 + ownerBytes.Length));
                writer.Stream.Write(record, 0, record.Length);
            }

            // Reserved must be patched before EndSection computes the section checksum —
            // patching afterward would leave the stored checksum stale (section bytes changed
            // after the hash was taken), and CacheContainerReader treats a checksum mismatch as
            // "section missing." Same ordering RootIndexWriter.Write itself uses in production.
            IndexHeader.PatchReserved(writer.Stream, names.Length, baseOffset);
            writer.EndSection(roots.Length);
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
