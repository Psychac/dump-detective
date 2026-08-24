using System.Buffers.Binary;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Satellite;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

public class TaskIndexReaderTests : IDisposable
{
    private readonly string _testDir;

    public TaskIndexReaderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "task-index-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Fact]
    public void ReadTaskIndexFile_RoundTrip_ReturnsExactRecords()
    {
        // Arrange
        string containerPath = Path.Combine(_testDir, "tasks.bin");
        var testRecords = new (ulong Address, ulong Mt, int StateFlags)[]
        {
            (0x1000, 0x2000, 0x1000000), // Completed
            (0x1001, 0x2000, 0x200000),  // Faulted
            (0x1002, 0x2001, 0x400000),  // Canceled
            (0x1003, 0x2001, 0x10000),   // Running
            (0x1004, 0x2000, 0),         // Pending (no flags)
        };

        WriteTaskContainerWithRecords(containerPath, testRecords);

        // Act
        var result = TaskIndexReader.ReadTaskIndexFile(containerPath, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Count.Should().Be(testRecords.Length);
        for (int i = 0; i < testRecords.Length; i++)
        {
            result[i].Address.Should().Be(testRecords[i].Address);
            result[i].Mt.Should().Be(testRecords[i].Mt);
            result[i].StateFlags.Should().Be(testRecords[i].StateFlags);
        }
    }

    [Fact]
    public void ReadTaskIndexFile_InvalidContainer_ReturnsNull()
    {
        string invalidPath = Path.Combine(_testDir, "nonexistent.bin");

        var result = TaskIndexReader.ReadTaskIndexFile(invalidPath, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public void ReadTaskIndexFile_InvalidMagic_ReturnsNull()
    {
        // Arrange
        string containerPath = Path.Combine(_testDir, "bad-magic.bin");
        WriteTaskContainerWithBadMagic(containerPath);

        // Act
        var result = TaskIndexReader.ReadTaskIndexFile(containerPath, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ReadTaskIndexFile_EmptyContainer_ReturnsEmptyList()
    {
        // Arrange
        string containerPath = Path.Combine(_testDir, "tasks-empty.bin");
        WriteTaskContainerWithRecords(containerPath, Array.Empty<(ulong, ulong, int)>());

        // Act
        var result = TaskIndexReader.ReadTaskIndexFile(containerPath, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Count.Should().Be(0);
    }

    private void WriteTaskContainerWithRecords(string containerPath, (ulong Address, ulong Mt, int StateFlags)[] records)
    {
        using (var writer = new CacheContainerWriter(containerPath))
        {
            writer.BeginSection(CacheSectionId.Tasks);
            using (var taskWriter = new TaskIndexWriter(writer.Stream))
            {
                foreach (var (address, mt, stateFlags) in records)
                {
                    taskWriter.Add(address, mt, stateFlags);
                }
                taskWriter.Flush();
            }
            writer.EndSection(recordCount: records.Length);
            writer.Finish();
        }
    }

    private void WriteTaskContainerWithBadMagic(string containerPath)
    {
        using (var writer = new CacheContainerWriter(containerPath))
        {
            writer.BeginSection(CacheSectionId.Tasks);
            // Write a header with bad magic (0x12345678 instead of 0x58494B54)
            Span<byte> badHeader = stackalloc byte[24];
            BinaryPrimitives.WriteInt32LittleEndian(badHeader, 0x12345678);
            BinaryPrimitives.WriteInt32LittleEndian(badHeader[4..], 1); // version
            BinaryPrimitives.WriteInt64LittleEndian(badHeader[8..], 0); // recordCount
            writer.Stream.Write(badHeader);
            writer.EndSection(recordCount: 0);
            writer.Finish();
        }
    }
}
