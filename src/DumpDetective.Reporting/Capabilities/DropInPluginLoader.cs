using System.Reflection;
using System.Runtime.Loader;

using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Reporting.Capabilities;

/// <summary>
/// Discovers and loads drop-in analyzer plugins from a directory, one subdirectory per plugin
/// (<c>&lt;pluginsRoot&gt;/&lt;PluginName&gt;/&lt;PluginName&gt;.dll</c>), each isolated in its own
/// collectible <see cref="AssemblyLoadContext"/> — see
/// docs/refactor/modularity/phase-3-plugin-packaging.md migration step 5 and its "ALC isolation for
/// drop-in plugins only" design decision. Built-in domain packages never go through this path; they
/// load into the default context via <see cref="PluginCatalogBuilder.BuildDefault"/>.
/// </summary>
/// <remarks>
/// Deliberately stops at discovering validated <see cref="IAnalyzer"/> types — it does not build
/// <see cref="AnalyzerFeatureModule"/>s for them. A module also needs an
/// <c>IAnalyzerTrendComparer</c>/<c>IAnalyzerSectionBuilder</c> companion (its
/// <c>ISynthesisRule</c> is public SDK, but the catalog only looks for it in Reporting), and both of
/// those interfaces are `internal` to <c>DumpDetective.Core</c>/
/// <c>DumpDetective.Reporting</c> today — unimplementable from a genuinely out-of-tree assembly with
/// no <c>InternalsVisibleTo</c> grant. That gap is the same one already called out in this doc's step
/// 3 notes (`FindingGenerator`/`SectionBuilder` staying interim in `Reporting`) and is explicitly
/// Phase 5's to close, not this step's. Wiring discovered plugin analyzers into report/trend output
/// is left undone until then; this loader's job is proving isolation, discovery, and validation work.
/// </remarks>
internal sealed class DropInPluginLoader
{
    private const string SdkAssemblySimpleName = "DumpDetective.Sdk";

    public IReadOnlyList<DropInPlugin> LoadFrom(string pluginsRoot)
    {
        if (!Directory.Exists(pluginsRoot))
        {
            return [];
        }

        List<DropInPlugin> plugins = [];
        foreach (string pluginDirectory in Directory.EnumerateDirectories(pluginsRoot))
        {
            string pluginName = Path.GetFileName(pluginDirectory);
            string assemblyPath = Path.Combine(pluginDirectory, pluginName + ".dll");
            if (!File.Exists(assemblyPath))
            {
                continue;
            }

            plugins.Add(LoadPlugin(pluginName, assemblyPath));
        }

        return plugins;
    }

    private static DropInPlugin LoadPlugin(string pluginName, string assemblyPath)
    {
        PluginLoadContext loadContext = new(pluginName, assemblyPath);
        Assembly assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

        ValidateSdkCompatibility(pluginName, assembly);

        List<Type> analyzerTypes = [];
        foreach (Type candidate in assembly.GetTypes())
        {
            if (!candidate.IsClass || candidate.IsAbstract || !typeof(IAnalyzer).IsAssignableFrom(candidate))
            {
                continue;
            }

            if (candidate.GetCustomAttribute<AnalyzerModuleAttribute>() is null)
            {
                continue;
            }

            ValidateDeclaredCapabilities(pluginName, candidate);
            analyzerTypes.Add(candidate);
        }

        return new DropInPlugin(pluginName, loadContext, assembly, analyzerTypes);
    }

    private static void ValidateSdkCompatibility(string pluginName, Assembly pluginAssembly)
    {
        AssemblyName? referencedSdk = Array.Find(
            pluginAssembly.GetReferencedAssemblies(), name => name.Name == SdkAssemblySimpleName);
        if (referencedSdk?.Version is not { } pluginSdkVersion)
        {
            // Doesn't reference the SDK (or carries no version info) -- not a valid analyzer host,
            // but not fatal to other plugins either; it will simply contribute zero analyzer types.
            return;
        }

        Version hostSdkVersion = typeof(IAnalyzer).Assembly.GetName().Version ?? new Version(0, 0);
        if (pluginSdkVersion.Major != hostSdkVersion.Major)
        {
            throw new InvalidOperationException(
                $"Plugin '{pluginName}' was built against {SdkAssemblySimpleName} v{pluginSdkVersion}, " +
                $"incompatible with the host's v{hostSdkVersion} (major version mismatch).");
        }
    }

    private static void ValidateDeclaredCapabilities(string pluginName, Type analyzerType)
    {
        IEnumerable<string> declared =
            (analyzerType.GetCustomAttribute<RequiresCapabilityAttribute>()?.Capabilities ?? [])
            .Concat(analyzerType.GetCustomAttributes<OptionalCapabilityAttribute>().Select(a => a.Capability));

        foreach (string capability in declared)
        {
            if (!CapabilityVocabulary.Known.Contains(capability))
            {
                throw new InvalidOperationException(
                    $"Plugin '{pluginName}' analyzer '{analyzerType.FullName}' declares unknown " +
                    $"capability '{capability}' — not present in CapabilityVocabulary.Known. Fix the " +
                    "typo, or add the capability to CapabilityVocabulary if it's genuinely new.");
            }
        }
    }

    /// <summary>
    /// Standard collectible-ALC plugin pattern: resolve the plugin's own private dependencies via
    /// <see cref="AssemblyDependencyResolver"/>; for everything else (notably
    /// <c>DumpDetective.Sdk</c> itself, and the BCL), returning <c>null</c> from
    /// <see cref="Load"/> lets the runtime fall back to <see cref="AssemblyLoadContext.Default"/> —
    /// which is what keeps <c>typeof(IAnalyzer)</c> type identity shared between host and plugin.
    /// </summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginName, string mainAssemblyPath)
            : base(name: $"DropInPlugin:{pluginName}", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string? resolvedPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return resolvedPath is not null ? LoadFromAssemblyPath(resolvedPath) : null;
        }
    }
}

/// <summary>
/// One successfully loaded drop-in plugin: its isolated, collectible <paramref name="LoadContext"/>
/// and the <paramref name="AnalyzerTypes"/> discovered in it (already validated for SDK
/// compatibility and known capabilities).
/// </summary>
internal sealed record DropInPlugin(
    string Name,
    AssemblyLoadContext LoadContext,
    Assembly Assembly,
    IReadOnlyList<Type> AnalyzerTypes);
