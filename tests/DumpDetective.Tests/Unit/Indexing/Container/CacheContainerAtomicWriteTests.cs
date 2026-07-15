using DumpDetective.Analysis.Indexing.Container;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Indexing.Container;

public class CacheContainerAtomicWriteTests : IDisposable
{
    private readonly string _testDir;

    public CacheContainerAtomicWriteTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "cache-atomic-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Fact]
    public void IncompleteWrite_NoFinalFileLeft()
    {
        string containerPath = Path.Combine(_testDir, "incomplete.bin");

        // Begin writing but don't call Finish()
        using (var writer = new CacheContainerWriter(containerPath))
        {
            writer.BeginSection(CacheSectionId.Objects);
            writer.Stream.Write(new byte[100], 0, 100);
            writer.EndSection(recordCount: 1);
            // Intentionally don't call Finish()
        }

        // Verify no final cache.bin exists
        File.Exists(containerPath).Should().BeFalse("cache.bin should not exist after incomplete write");

        // But .tmp file should also be gone (disposed)
        string tmpPath = containerPath + ".tmp";
        File.Exists(tmpPath).Should().BeFalse(".tmp file should be cleaned up after disposal");
    }

    [Fact]
    public void ExceptionDuringWrite_NoFinalFileLeft()
    {
        string containerPath = Path.Combine(_testDir, "exception.bin");

        try
        {
            using (var writer = new CacheContainerWriter(containerPath))
            {
                writer.BeginSection(CacheSectionId.Objects);
                writer.Stream.Write(new byte[100], 0, 100);
                writer.EndSection(recordCount: 1);

                // Simulate an error that prevents Finish()
                throw new InvalidOperationException("Simulated write failure");
            }
        }
        catch (InvalidOperationException)
        {
            // Expected
        }

        // Verify no final cache.bin exists
        File.Exists(containerPath).Should().BeFalse(
            "cache.bin should not exist after exception during write");
    }

    [Fact]
    public void AbortedSection_NextSectionOverwritesPreviousData()
    {
        string containerPath = Path.Combine(_testDir, "abort.bin");

        using var writer = new CacheContainerWriter(containerPath);

        // Write first section
        writer.BeginSection(CacheSectionId.Objects);
        byte[] data1 = new byte[100];
        Array.Fill(data1, (byte)0xAA);
        writer.Stream.Write(data1, 0, data1.Length);
        writer.EndSection(recordCount: 1);

        // Start second section but abort it
        writer.BeginSection(CacheSectionId.TypeAggregates);
        byte[] data2 = new byte[100];
        Array.Fill(data2, (byte)0xBB);
        writer.Stream.Write(data2, 0, data2.Length);
        writer.AbortSection();

        // Write third section — should not contain aborted section's data
        writer.BeginSection(CacheSectionId.Roots);
        byte[] data3 = new byte[100];
        Array.Fill(data3, (byte)0xCC);
        writer.Stream.Write(data3, 0, data3.Length);
        writer.EndSection(recordCount: 1);

        writer.Finish();

        // Read and verify
        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeTrue();
        reader!.ContainsSection(CacheSectionId.Objects).Should().BeTrue();
        reader.ContainsSection(CacheSectionId.TypeAggregates).Should().BeFalse("aborted section should not be in TOC");
        reader.ContainsSection(CacheSectionId.Roots).Should().BeTrue();
    }

    [Fact]
    public void PartialWrite_SubsequentRunCanRebuild()
    {
        string containerPath = Path.Combine(_testDir, "rebuild.bin");

        // Simulate a partial write by not calling Finish
        {
            using var writer = new CacheContainerWriter(containerPath);
            writer.BeginSection(CacheSectionId.Objects);
            writer.Stream.Write(new byte[100], 0, 100);
            writer.EndSection(recordCount: 1);
            // Don't call Finish() — simulates crash
        }

        // Verify we can't open the incomplete container
        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeFalse(
            "incomplete container should not open");

        // Now write a complete container (simulating a retry/rebuild)
        using (var writer = new CacheContainerWriter(containerPath))
        {
            writer.BeginSection(CacheSectionId.Objects);
            writer.Stream.Write(new byte[100], 0, 100);
            writer.EndSection(recordCount: 1);
            writer.Finish();
        }

        // Now it should open successfully
        CacheContainerReader.TryOpen(containerPath, out reader).Should().BeTrue(
            "complete container should open after rebuild");
    }

    [Fact]
    public void TempFile_CleanedUpByDisposal()
    {
        string containerPath = Path.Combine(_testDir, "cleanup.bin");
        string tmpPath = containerPath + ".tmp";

        // Create and dispose writer without finishing
        using (var writer = new CacheContainerWriter(containerPath))
        {
            writer.BeginSection(CacheSectionId.Objects);
            writer.Stream.Write(new byte[100], 0, 100);
            writer.EndSection(recordCount: 1);
            // Dispose without Finish()
        }

        // Temp file should be cleaned up
        File.Exists(tmpPath).Should().BeFalse(".tmp file should be deleted after disposal without Finish()");
    }

    [Fact]
    public void SuccessfulWrite_FinalFileExists()
    {
        string containerPath = Path.Combine(_testDir, "success.bin");

        using var writer = new CacheContainerWriter(containerPath);
        writer.BeginSection(CacheSectionId.Objects);
        writer.Stream.Write(new byte[100], 0, 100);
        writer.EndSection(recordCount: 1);
        writer.Finish();

        // Verify final file exists and can be opened
        File.Exists(containerPath).Should().BeTrue("cache.bin should exist after successful Finish()");

        CacheContainerReader.TryOpen(containerPath, out var reader).Should().BeTrue(
            "completed container should open successfully");
    }

    [Fact]
    public async Task ConcurrentWrites_EachUsesOwnTempFile()
    {
        var tasks = new Task[3];

        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            tasks[i] = Task.Run(() =>
            {
                string containerPath = Path.Combine(_testDir, $"concurrent-{idx}.bin");

                using var writer = new CacheContainerWriter(containerPath);
                writer.BeginSection(CacheSectionId.Objects);
                writer.Stream.Write(new byte[100], 0, 100);
                writer.EndSection(recordCount: 1);
                writer.Finish();
            });
        }

        await Task.WhenAll(tasks);

        // Verify all files were created
        File.Exists(Path.Combine(_testDir, "concurrent-0.bin")).Should().BeTrue();
        File.Exists(Path.Combine(_testDir, "concurrent-1.bin")).Should().BeTrue();
        File.Exists(Path.Combine(_testDir, "concurrent-2.bin")).Should().BeTrue();

        // Verify all can be read
        for (int i = 0; i < 3; i++)
        {
            string path = Path.Combine(_testDir, $"concurrent-{i}.bin");
            CacheContainerReader.TryOpen(path, out _).Should().BeTrue($"concurrent file {i} should be readable");
        }
    }
}
