using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing.Container;

public class CacheContainerWriterChecksumProgressTests : IDisposable
{
    private readonly string _testDir;

    public CacheContainerWriterChecksumProgressTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "cache-checksum-progress-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Fact]
    public void EndSection_LargeSection_ReportsChecksumProgress()
    {
        string containerPath = Path.Combine(_testDir, "cache.bin");
        var reports = new List<AnalyzerProgressReport>();
        var progress = new SynchronousProgress<AnalyzerProgressReport>(r => reports.Add(r));

        // Above both the 32 MB reporting threshold and the 64 MB report-every interval, so
        // EndSection's checksum re-read should surface at least one progress tick instead of
        // running fully silent.
        byte[] chunk = new byte[1024 * 1024];
        using (var writer = new CacheContainerWriter(containerPath, dumpPath: null, progress))
        {
            writer.BeginSection(CacheSectionId.Roots);
            for (int i = 0; i < 70; i++)
                writer.Stream.Write(chunk, 0, chunk.Length);
            writer.EndSection(recordCount: 1);
            writer.Finish();
        }

        reports.Should().NotBeEmpty();
        reports.Should().OnlyContain(r => r.Phase.StartsWith("verifying "));
    }

    [Fact]
    public void EndSection_SmallSection_ReportsNoChecksumProgress()
    {
        string containerPath = Path.Combine(_testDir, "cache.bin");
        var reports = new List<AnalyzerProgressReport>();
        var progress = new SynchronousProgress<AnalyzerProgressReport>(r => reports.Add(r));

        using (var writer = new CacheContainerWriter(containerPath, dumpPath: null, progress))
        {
            writer.BeginSection(CacheSectionId.Roots);
            writer.Stream.Write(new byte[1024], 0, 1024);
            writer.EndSection(recordCount: 1);
            writer.Finish();
        }

        reports.Should().BeEmpty();
    }

    [Fact]
    public void EndSection_NoProgressInstanceSupplied_DoesNotThrow()
    {
        string containerPath = Path.Combine(_testDir, "cache.bin");

        using var writer = new CacheContainerWriter(containerPath);
        writer.BeginSection(CacheSectionId.Roots);
        writer.Stream.Write(new byte[1024 * 1024], 0, 1024 * 1024);
        var act = () => writer.EndSection(recordCount: 1);

        act.Should().NotThrow();
    }

    private sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
