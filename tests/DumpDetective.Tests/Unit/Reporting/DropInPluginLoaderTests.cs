using System.Reflection;
using System.Runtime.Loader;

using DumpDetective.Reporting.Capabilities;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Observations;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

/// <summary>
/// Exercises the "plugins/ drop-in + ALC loader path" docs/refactor/modularity/phase-3-plugin-packaging.md
/// migration step 5 asks for, against a real out-of-tree fixture
/// (tests/Fixtures/DumpDetective.TestPlugin, Sdk-only) copied into a scratch drop-in layout for each
/// test — not a mock, so a real <see cref="AssemblyLoadContext"/> load actually happens.
/// </summary>
public sealed class DropInPluginLoaderTests : IDisposable
{
    private const string PluginAssemblyFileName = "DumpDetective.TestPlugin.dll";
    private readonly string _scratchRoot = Path.Combine(Path.GetTempPath(), "dd-dropin-plugin-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // Best-effort only: the collectible ALCs this test creates aren't explicitly unloaded, so
        // Windows may still hold the mmap'd plugin dll open until the next GC finalizes it — not
        // worth synchronizing on for a scratch temp directory.
        try
        {
            if (Directory.Exists(_scratchRoot))
            {
                Directory.Delete(_scratchRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string BuiltPluginAssemblyPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, PluginAssemblyFileName);
        File.Exists(path).Should().BeTrue(
            $"the test host's own build should have copied {PluginAssemblyFileName} next to it via the " +
            "Tests project's ProjectReference to tests/Fixtures/DumpDetective.TestPlugin");
        return path;
    }

    private string CreateDropIn(string pluginDirectoryName, string assemblyFileNameInDirectory)
    {
        string pluginDirectory = Path.Combine(_scratchRoot, pluginDirectoryName);
        Directory.CreateDirectory(pluginDirectory);
        File.Copy(BuiltPluginAssemblyPath(), Path.Combine(pluginDirectory, assemblyFileNameInDirectory));
        return _scratchRoot;
    }

    [Fact]
    public void LoadFrom_NonExistentDirectory_ReturnsEmpty()
    {
        IReadOnlyList<DropInPlugin> plugins = new DropInPluginLoader().LoadFrom(
            Path.Combine(_scratchRoot, "does-not-exist"));

        plugins.Should().BeEmpty();
    }

    [Fact]
    public void LoadFrom_DirectoryWhoseDllDoesNotMatchDirectoryName_IsSkipped()
    {
        // Convention is <pluginsRoot>/<PluginName>/<PluginName>.dll -- a dll present under a
        // differently-named file is not recognized as that directory's plugin.
        string pluginsRoot = CreateDropIn("SomePlugin", "NotTheExpectedName.dll");

        IReadOnlyList<DropInPlugin> plugins = new DropInPluginLoader().LoadFrom(pluginsRoot);

        plugins.Should().BeEmpty();
    }

    [Fact]
    public void LoadFrom_ValidDropIn_DiscoversAnalyzerInIsolatedCollectibleLoadContext()
    {
        string pluginsRoot = CreateDropIn("DumpDetective.TestPlugin", PluginAssemblyFileName);

        IReadOnlyList<DropInPlugin> plugins = new DropInPluginLoader().LoadFrom(pluginsRoot);

        plugins.Should().HaveCount(1);
        DropInPlugin plugin = plugins[0];

        plugin.Name.Should().Be("DumpDetective.TestPlugin");
        plugin.LoadContext.Should().NotBeSameAs(AssemblyLoadContext.Default);
        plugin.LoadContext.IsCollectible.Should().BeTrue();
        plugin.LoadContext.Name.Should().Be("DropInPlugin:DumpDetective.TestPlugin");

        plugin.AnalyzerTypes.Should().ContainSingle();
        Type analyzerType = plugin.AnalyzerTypes[0];
        analyzerType.Name.Should().Be("SampleDropInAnalyzer");
        typeof(IAnalyzer).IsAssignableFrom(analyzerType).Should().BeTrue(
            "the plugin's IAnalyzer must resolve to the same Sdk type the host uses, proving the " +
            "loader's ALC-fallback-to-Default strategy keeps shared types identity-equal");
    }

    [Fact]
    public async Task LoadFrom_ValidDropIn_DiscoveredAnalyzerRunsAcrossTheLoadContextBoundary()
    {
        string pluginsRoot = CreateDropIn("DumpDetective.TestPlugin", PluginAssemblyFileName);
        DropInPlugin plugin = new DropInPluginLoader().LoadFrom(pluginsRoot).Single();
        Type analyzerType = plugin.AnalyzerTypes[0];

        FieldInfo runCountField = analyzerType.GetField("RunCount", BindingFlags.Public | BindingFlags.Static)!;
        int before = (int)runCountField.GetValue(null)!;

        var analyzer = (IAnalyzer)Activator.CreateInstance(analyzerType)!;
        AnalysisContext context = new() { Observations = new NullObservationSink() };
        await analyzer.AnalyzeAsync(context, CancellationToken.None);

        int after = (int)runCountField.GetValue(null)!;
        after.Should().Be(before + 1);
    }

    private sealed class NullObservationSink : IObservationSink
    {
        public ValueTask EmitAsync(Observation observation, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
