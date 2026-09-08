using System.Buffers.Binary;

using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Dominator;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// §12.2 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md):
/// <see cref="RootStackThreadIndexReader"/> and <see cref="ThreadRetentionReaderProvider"/>, tested
/// against hand-built <c>cache.bin</c> fixtures — same style as <c>RootIndexReaderTests</c>.
/// </summary>
public sealed class RootStackThreadIndexTests
{
    private const byte StackKind = 4;

    private sealed class FakeDominatorTreeProvider(
        Dictionary<ulong, ulong> idomByAddress, Dictionary<ulong, ulong> retainedByAddress) : IDominatorTreeProvider
    {
        public bool TryGetImmediateDominator(ulong address, out ulong dominatorAddress) =>
            idomByAddress.TryGetValue(address, out dominatorAddress);

        public bool TryGetRetainedBytes(ulong address, out ulong retainedBytes) =>
            retainedByAddress.TryGetValue(address, out retainedBytes);

        public IEnumerable<ulong> EnumerateRetainedSet(ulong address) => throw new NotSupportedException();

        public ulong TotalRetainedBytes => throw new NotSupportedException();

        public bool TryGetRetainedBytesByMethodTable(ulong methodTable, out ulong retainedBytes) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void Read_ShouldReturnEmpty_WhenSectionMissing()
    {
        string path = CreateContainerWithSections(roots: null, stackThreads: null);
        try
        {
            CacheContainerReader.TryOpen(path, out CacheContainerReader? reader).Should().BeTrue();
            Dictionary<ulong, (uint OSThreadId, int ManagedThreadId)> map =
                RootStackThreadIndexReader.Read(reader!, CancellationToken.None);

            map.Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_ShouldRoundTripRecords()
    {
        string path = CreateContainerWithSections(
            roots: null,
            stackThreads: [(0xAAAUL, 111u, 1), (0xBBBUL, 222u, 2)]);

        try
        {
            CacheContainerReader.TryOpen(path, out CacheContainerReader? reader).Should().BeTrue();
            Dictionary<ulong, (uint OSThreadId, int ManagedThreadId)> map =
                RootStackThreadIndexReader.Read(reader!, CancellationToken.None);

            map.Should().HaveCount(2);
            map[0xAAAUL].Should().Be((111u, 1));
            map[0xBBBUL].Should().Be((222u, 2));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ThreadRetentionReaderProvider_TryOpen_AttributesRetainedBytesPerThread()
    {
        // Thread 111 owns stack roots pointing at 0x100 (500 bytes) and 0x200 — but 0x200 is
        // dominated by 0x100, so it must not be double-counted. Thread 222 owns a stack root
        // pointing at 0x300 (300 bytes), unrelated to thread 111's subtree.
        string path = CreateContainerWithSections(
            roots:
            [
                (0x100UL, 0xAAA1UL, StackKind),
                (0x200UL, 0xAAA2UL, StackKind),
                (0x300UL, 0xBBB1UL, StackKind),
            ],
            stackThreads:
            [
                (0xAAA1UL, 111u, 1),
                (0xAAA2UL, 111u, 1),
                (0xBBB1UL, 222u, 2),
            ]);

        try
        {
            CacheContainerReader.TryOpen(path, out CacheContainerReader? reader).Should().BeTrue();

            var treeProvider = new FakeDominatorTreeProvider(
                idomByAddress: new() { [0x100] = 0, [0x200] = 0x100, [0x300] = 0 },
                retainedByAddress: new() { [0x100] = 500, [0x300] = 300 });

            bool opened = ThreadRetentionReaderProvider.TryOpen(
                reader!, treeProvider, CancellationToken.None, out ThreadRetentionReaderProvider? provider);

            opened.Should().BeTrue();
            provider!.TryGetRetainedBytesForThread(111u, out ulong thread111Bytes).Should().BeTrue();
            thread111Bytes.Should().Be(500);

            provider.TryGetRetainedBytesForThread(222u, out ulong thread222Bytes).Should().BeTrue();
            thread222Bytes.Should().Be(300);

            provider.TryGetRetainedBytesForThread(999u, out ulong unknownThreadBytes).Should().BeFalse();
            unknownThreadBytes.Should().Be(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ThreadRetentionReaderProvider_TryOpen_ReturnsFalse_WhenStackThreadSectionMissing()
    {
        string path = CreateContainerWithSections(
            roots: [(0x100UL, 0xAAA1UL, StackKind)],
            stackThreads: null);

        try
        {
            CacheContainerReader.TryOpen(path, out CacheContainerReader? reader).Should().BeTrue();
            var treeProvider = new FakeDominatorTreeProvider(new(), new());

            bool opened = ThreadRetentionReaderProvider.TryOpen(
                reader!, treeProvider, CancellationToken.None, out ThreadRetentionReaderProvider? provider);

            opened.Should().BeFalse();
            provider.Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateContainerWithSections(
        (ulong Target, ulong Root, byte Kind)[]? roots,
        (ulong RootAddr, uint OSThreadId, int ManagedThreadId)[]? stackThreads)
    {
        string path = Path.Combine(Path.GetTempPath(), $"root-stack-thread-{Guid.NewGuid():N}.bin");

        var writer = new CacheContainerWriter(path);
        try
        {
            if (roots is not null)
            {
                writer.BeginSection(CacheSectionId.Roots);
                var rootsHeader = new IndexHeader(0x58495452, 2, roots.Length);
                rootsHeader.WriteTo(writer.Stream);
                foreach ((ulong target, ulong root, byte kind) in roots)
                {
                    byte[] record = new byte[20];
                    BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0, 8), target);
                    BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8, 8), root);
                    record[16] = kind;
                    writer.Stream.Write(record, 0, record.Length);
                }
                writer.EndSection(roots.Length);
            }

            if (stackThreads is not null)
            {
                writer.BeginSection(CacheSectionId.RootStackThreadAttribution);
                var stackThreadHeader = new IndexHeader(0x41545452, 1, stackThreads.Length);
                stackThreadHeader.WriteTo(writer.Stream);
                foreach ((ulong rootAddr, uint osThreadId, int managedThreadId) in stackThreads)
                {
                    byte[] record = new byte[16];
                    BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0, 8), rootAddr);
                    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8, 4), osThreadId);
                    BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(12, 4), managedThreadId);
                    writer.Stream.Write(record, 0, record.Length);
                }
                writer.EndSection(stackThreads.Length);
            }

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
