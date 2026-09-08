using DumpDetective.Analysis.Indexing.Container;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing.Container;

/// <summary>
/// The <c>SectionManifest</c> section — docs/cache/cache-format-clean-slate-redesign.md §10.5. It
/// exists for exactly one case the TOC cannot express: a section that was opened and then aborted,
/// which is indistinguishable from one that was never attempted.
/// </summary>
public class SectionManifestTests : IDisposable
{
    private readonly string _testDir;

    public SectionManifestTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "section-manifest-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Fact]
    public void LostSections_AllSectionsClosed_IsEmpty()
    {
        string containerPath = Path.Combine(_testDir, "healthy.bin");

        using (var writer = new CacheContainerWriter(containerPath))
        {
            WriteTrivialSection(writer, CacheSectionId.Roots);
            WriteTrivialSection(writer, CacheSectionId.Handles);
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();
        reader!.LostSections().Should().BeEmpty();
        reader.ContainsSection(CacheSectionId.SectionManifest).Should().BeTrue();
    }

    [Fact]
    public void LostSections_AbortedConditionalSection_IsReported()
    {
        string containerPath = Path.Combine(_testDir, "aborted.bin");

        using (var writer = new CacheContainerWriter(containerPath))
        {
            WriteTrivialSection(writer, CacheSectionId.Roots);

            // Exactly what TryWriteSection does when a satellite writer throws mid-section.
            writer.BeginSection(CacheSectionId.Handles);
            writer.Stream.WriteByte(1);
            writer.AbortSection();

            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();
        reader!.ContainsSection(CacheSectionId.Handles).Should().BeFalse("the abort left no TOC entry");
        reader.LostSections().Should().Equal(CacheSectionId.Handles);
    }

    [Fact]
    public void LostSections_ManifestListsItself()
    {
        string containerPath = Path.Combine(_testDir, "self.bin");

        using (var writer = new CacheContainerWriter(containerPath))
        {
            WriteTrivialSection(writer, CacheSectionId.Roots);
            writer.Finish();
        }

        CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();
        reader!.TryGetSectionInfo(CacheSectionId.SectionManifest, out CacheTocEntry manifest).Should().BeTrue();
        manifest.RecordCount.Should().Be(2, "Roots plus the manifest itself");
    }

    private static void WriteTrivialSection(CacheContainerWriter writer, CacheSectionId id)
    {
        writer.BeginSection(id);
        writer.Stream.WriteByte(0);
        writer.EndSection(recordCount: 1);
    }
}
