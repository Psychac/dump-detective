using DumpDetective.Cli.Commands;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Options;
using DumpDetective.Cli.Configuration;
using DumpDetective.Cli.Services;
using DumpDetective.Cli.Models;

using static DumpDetective.Cli.Configuration.ConfigurationParseHelpers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DumpDetective.Core.Enums;

namespace DumpDetective.Cli.Configuration;

internal sealed class ConfigurationResolver
{
    private const string DefaultConfigFileName = "config.json";
    private const string FallbackSampleConfigFileName = "config.sample.json";
    private static readonly JsonSerializerOptions s_ignoreDefaultWriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    public ResolvedExecutionOptions Resolve(AnalysisCommandRequest request)
    {
        try
        {
        string? configPath = ResolveConfigPath(request.ConfigPath);
        CliConfigurationFileModel? fileModel = configPath is null ? null : LoadConfigurationFile(configPath);

        bool usedConfigFile = fileModel is not null;

        RetentionOptions memoryLeak = Resolve(usedConfigFile, BuildMemoryLeakFromConfig, _ => new RetentionOptions(), fileModel, request);
        ReferenceChainOptions refChain = Resolve(usedConfigFile, BuildReferenceChainFromConfig, _ => new ReferenceChainOptions(), fileModel, request);
        EventLeakOptions eventLeak = Resolve(usedConfigFile, BuildEventLeakFromConfig, _ => new EventLeakOptions(), fileModel, request);
        DiagnosticsOptions diagnostics = Resolve(usedConfigFile, BuildDiagnosticsFromConfig, AnalyzerOptionsBuilder.BuildDiagnosticsFromCli, fileModel, request);
        ReportOptions report = Resolve(usedConfigFile, BuildReportFromConfig, AnalyzerOptionsBuilder.BuildReportFromCli, fileModel, request);
        ExecutionPolicy executionPolicy = BuildExecutionPolicy(fileModel, memoryLeak);
        CrashAnalysisOptions crash = Resolve(usedConfigFile, BuildCrashFromConfig, _ => new CrashAnalysisOptions(), fileModel, request);
        AsyncTaskAnalysisOptions asyncTaskAnalysis = Resolve(usedConfigFile, BuildAsyncTaskAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, AsyncTaskAnalysisOptions.Preset), fileModel, request);
        AsyncStateMachineAnalysisOptions asyncStateMachineAnalysis = Resolve(usedConfigFile, BuildAsyncStateMachineAnalysisFromConfig, _ => new AsyncStateMachineAnalysisOptions(), fileModel, request);
        ArrayAnalysisOptions arrayAnalysis = Resolve(usedConfigFile, BuildArrayAnalysisFromConfig, _ => new ArrayAnalysisOptions(), fileModel, request);
        BoxingAnalysisOptions boxingAnalysis = Resolve(usedConfigFile, BuildBoxingAnalysisFromConfig, _ => new BoxingAnalysisOptions(), fileModel, request);
        CollectionAnalysisOptions collection = Resolve(usedConfigFile, BuildCollectionFromConfig, _ => CollectionAnalysisOptions.Validate(new CollectionAnalysisOptions()), fileModel, request);
        StringAnalysisOptions stringAnalysis = Resolve(usedConfigFile, BuildStringAnalysisFromConfig, AnalyzerOptionsBuilder.BuildStringAnalysisFromCli, fileModel, request);
        AllocationPatternAnalysisOptions allocationPatternAnalysis = Resolve(usedConfigFile, BuildAllocationPatternAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, AllocationPatternAnalysisOptions.Preset), fileModel, request);
        ThreadStackClusterAnalysisOptions threadStackClusterAnalysis = Resolve(usedConfigFile, BuildThreadStackClusterAnalysisFromConfig, _ => new ThreadStackClusterAnalysisOptions(), fileModel, request);
        GCGenerationAnalysisOptions gcGenerationAnalysis = Resolve(usedConfigFile, BuildGCGenerationAnalysisFromConfig, _ => new GCGenerationAnalysisOptions(), fileModel, request);
        SegmentReservationAnalysisOptions segmentReservationAnalysis = Resolve(usedConfigFile, BuildSegmentReservationAnalysisFromConfig, _ => new SegmentReservationAnalysisOptions(), fileModel, request);
        ThreadAnalysisOptions threadAnalysis = Resolve(usedConfigFile, BuildThreadAnalysisFromConfig, _ => new ThreadAnalysisOptions(), fileModel, request);
        HangAnalysisOptions hangAnalysis = Resolve(usedConfigFile, BuildHangAnalysisFromConfig, _ => new HangAnalysisOptions(), fileModel, request);
        JitAnalysisOptions jitAnalysis = Resolve(usedConfigFile, BuildJitAnalysisFromConfig, _ => new JitAnalysisOptions(), fileModel, request);
        WeakReferenceAnalysisOptions weakReferenceAnalysis = Resolve(usedConfigFile, BuildWeakReferenceAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, WeakReferenceAnalysisOptions.Preset), fileModel, request);
        ModuleAnalysisOptions moduleAnalysis = Resolve(usedConfigFile, BuildModuleAnalysisFromConfig, _ => new ModuleAnalysisOptions(), fileModel, request);
        GCHandleAnalysisOptions gcHandleAnalysis = Resolve(usedConfigFile, BuildGCHandleAnalysisFromConfig, _ => new GCHandleAnalysisOptions(), fileModel, request);
        StaticRootLeakAnalysisOptions staticRootLeakAnalysis = Resolve(usedConfigFile, BuildStaticRootLeakAnalysisFromConfig, _ => new StaticRootLeakAnalysisOptions(), fileModel, request);
        MemoryAnalysisOptions memoryAnalysis = Resolve(usedConfigFile, BuildMemoryAnalysisFromConfig, _ => new MemoryAnalysisOptions(), fileModel, request);

        string? configuredDumpPath = fileModel?.DumpPath;
        string? configuredBaseline = fileModel?.BaselineDumpPath;
        IReadOnlyList<string>? configuredTrend = fileModel?.TrendDumpPaths;
        IReadOnlyList<string>? effectiveTrend = configuredTrend ?? request.TrendDumpPaths;
        IReadOnlyCollection<string>? configuredInclude = fileModel?.IncludeAnalyzers;
        IReadOnlyCollection<string>? configuredExclude = fileModel?.ExcludeAnalyzers;
        IReadOnlyCollection<string> effectiveInclude = configuredInclude ?? request.IncludeAnalyzers;
        IReadOnlyCollection<string> effectiveExclude = configuredExclude ?? request.ExcludeAnalyzers;

        // Determine effective dump path.
        // If the user explicitly provided a config path, honor the configured DumpPath when present.
        // Otherwise prefer the request-provided DumpPath (positional CLI) over any implicit config file value.
        string? effectiveDumpPath;
        if (!string.IsNullOrWhiteSpace(request.ConfigPath))
        {
            effectiveDumpPath = !string.IsNullOrWhiteSpace(configuredDumpPath)
                ? configuredDumpPath
                : !string.IsNullOrWhiteSpace(request.DumpPath) ? request.DumpPath : effectiveTrend?.LastOrDefault();
        }
        else
        {
            effectiveDumpPath = !string.IsNullOrWhiteSpace(request.DumpPath)
                ? request.DumpPath
                : !string.IsNullOrWhiteSpace(configuredDumpPath) ? configuredDumpPath : effectiveTrend?.LastOrDefault();
        }
        if (string.IsNullOrWhiteSpace(effectiveDumpPath))
        {
            throw new ArgumentException("Dump path is required. Provide positional dump-path, --trend, or DumpPath in config.");
        }

        string outputPath = !string.IsNullOrWhiteSpace(request.OutputPath)
            ? request.OutputPath!
            : BuildOutputPath(effectiveDumpPath!, report.Format);

        return new ResolvedExecutionOptions(
            effectiveDumpPath!,
            outputPath,
            configuredBaseline ?? request.BaselineDumpPath,
            effectiveTrend,
            memoryLeak,
            refChain,
            eventLeak,
            diagnostics,
            report,
            crash,
            asyncTaskAnalysis,
            asyncStateMachineAnalysis,
            arrayAnalysis,
            boxingAnalysis,
            collection,
            stringAnalysis,
            allocationPatternAnalysis,
            threadStackClusterAnalysis,
            gcGenerationAnalysis,
            segmentReservationAnalysis,
            threadAnalysis,
            hangAnalysis,
            jitAnalysis,
            weakReferenceAnalysis,
            moduleAnalysis,
            gcHandleAnalysis,
            staticRootLeakAnalysis,
            memoryAnalysis,
            configPath,
            usedConfigFile,
                effectiveInclude,
                effectiveExclude,
            request.DiagnosticMode)
        {
            ExecutionPolicy = executionPolicy,
            CacheDirectory = fileModel?.CacheDirectory ?? request.CacheDirectory
        };
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
        {
            throw new DumpDetective.Cli.Diagnostics.ConfigurationException(ex.Message, ex);
        }
    }

    private static string? ResolveConfigPath(string? cliConfigPath)
    {
        if (!string.IsNullOrWhiteSpace(cliConfigPath))
        {
            if (!File.Exists(cliConfigPath))
            {
                throw new FileNotFoundException($"Config file not found at '{cliConfigPath}'.", cliConfigPath);
            }

            return cliConfigPath;
        }

        string baseDirectory = AppContext.BaseDirectory;
        string primaryPath = Path.Combine(baseDirectory, DefaultConfigFileName);
        if (File.Exists(primaryPath))
        {
            return primaryPath;
        }

        string samplePath = Path.Combine(baseDirectory, FallbackSampleConfigFileName);
        return File.Exists(samplePath) ? samplePath : null;
    }



    private static CliConfigurationFileModel LoadConfigurationFile(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Config file not found at '{configPath}'.", configPath);
        }

        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            TypeInfoResolver = CliConfigurationJsonSerializerContext.Default
        };
        serializerOptions.Converters.Add(new JsonStringEnumConverter());

        string json = File.ReadAllText(configPath);
        CliConfigurationFileModel? model = JsonSerializer.Deserialize<CliConfigurationFileModel>(json, serializerOptions);
        if (model is null)
        {
            throw new ArgumentException($"Config file '{configPath}' is empty or invalid.");
        }

        return model;
    }

    private static RetentionOptions BuildMemoryLeakFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "MemoryLeak", out JsonElement section))
            return ApplySectionOverrides(new RetentionOptions(), section);

        return config.MemoryLeak is null
            ? new RetentionOptions()
            : ApplyOptionsOverrides(new RetentionOptions(), config.MemoryLeak);
    }



    private static ReferenceChainOptions BuildReferenceChainFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "ReferenceChain", out JsonElement section))
            return ApplySectionOverrides(new ReferenceChainOptions(), section);

        return config.ReferenceChain is null
            ? new ReferenceChainOptions()
            : ApplyOptionsOverrides(new ReferenceChainOptions(), config.ReferenceChain);
    }



    private static EventLeakOptions BuildEventLeakFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "EventLeak", out JsonElement section))
            return ApplySectionOverrides(new EventLeakOptions(), section);

        return config.EventLeak is null
            ? new EventLeakOptions()
            : ApplyOptionsOverrides(new EventLeakOptions(), config.EventLeak);
    }



    private static DiagnosticsOptions BuildDiagnosticsFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        bool enableMemoryDiagnostics = config.Diagnostics?.EnableMemoryDiagnostics
            ?? config.EnableMemoryDiagnostics
            ?? request.EnableMemoryDiagnostics;

        bool enablePerformanceDiagnostics = config.Diagnostics?.EnablePerformanceDiagnostics
            ?? config.EnablePerformanceDiagnostics
            ?? request.EnablePerformanceDiagnostics;

        bool collectAfterAnalyzerRun = config.Diagnostics?.CollectAfterAnalyzerRun ?? false;
        int collectAfterAnalyzerRunEveryKAnalyzers = config.Diagnostics?.CollectAfterAnalyzerRunEveryKAnalyzers ?? 0;
        long collectAfterAnalyzerRunWorkingSetThresholdBytes = config.Diagnostics?.CollectAfterAnalyzerRunWorkingSetThresholdBytes ?? 0;
        bool compactLargeObjectHeapAfterAnalyzerCollection = config.Diagnostics?.CompactLargeObjectHeapAfterAnalyzerCollection ?? true;

        return new DiagnosticsOptions
        {
            EnableMemoryDiagnostics = enableMemoryDiagnostics,
            EnablePerformanceDiagnostics = enablePerformanceDiagnostics,
            CollectAfterAnalyzerRun = collectAfterAnalyzerRun,
            CollectAfterAnalyzerRunEveryKAnalyzers = collectAfterAnalyzerRunEveryKAnalyzers,
            CollectAfterAnalyzerRunWorkingSetThresholdBytes = collectAfterAnalyzerRunWorkingSetThresholdBytes,
            CompactLargeObjectHeapAfterAnalyzerCollection = compactLargeObjectHeapAfterAnalyzerCollection
        };
    }



    private static ReportOptions BuildReportFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        return new ReportOptions
        {
            Format = config.Report?.Format ?? ParseReportFormat(config.ReportFormat) ?? request.OutputFormat ?? ReportFormat.Html,
            StyleVersion = config.Report?.StyleVersion ?? ParseReportStyle(config.ReportStyleVersion) ?? request.ReportStyleVersion ?? ReportStyleVersion.V1,
            PreRender = config.Report?.PreRender ?? request.PreRender,
            SeparateJson = config.Report?.SeparateJson ?? request.SeparateJson
        };
    }



    private static ExecutionPolicy BuildExecutionPolicy(
        CliConfigurationFileModel? config,
        RetentionOptions memoryLeak)
    {
        ExecutionPolicyModel? policy = config?.ExecutionPolicy;

        return new ExecutionPolicy
        {
            MaxLeakScanObjects = PositiveOrNull(policy?.MaxLeakScanObjects) ?? memoryLeak.MaxLeakScanObjects,
            MaxReferenceAddresses = PositiveOrNull(policy?.MaxReferenceAddresses) ?? memoryLeak.MaxReferenceAddresses,
        };
    }



    private static CollectionAnalysisOptions BuildCollectionFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        CollectionAnalysisOptionsModel? legacy = config.Collection;
        CollectionAnalysisOptionsModel? modern = null;
        if (TryGetAnalyzerSection(config, "Collection", out JsonElement modernSection))
            modern = modernSection.Deserialize<CollectionAnalysisOptionsModel>();
        CollectionAnalysisOptionsModel? model = MergeCollectionModel(primary: modern, fallback: legacy);

        CollectionAnalysisOptions effective = CollectionAnalysisOptions.ApplyOverrides(new CollectionAnalysisOptions(), model);
        effective = CollectionAnalysisOptions.Validate(effective);

        return effective;
    }

    private static CrashAnalysisOptions BuildCrashFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "Crash", out JsonElement section))
            return ApplySectionOverrides(new CrashAnalysisOptions(), section);

        return config.Crash is null
            ? new CrashAnalysisOptions()
            : ApplyOptionsOverrides(new CrashAnalysisOptions(), config.Crash);
    }

    private static AsyncTaskAnalysisOptions BuildAsyncTaskAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "AsyncTask",
            config.AsyncTaskAnalysis,
            AsyncTaskAnalysisOptions.Preset);

    private static AsyncStateMachineAnalysisOptions BuildAsyncStateMachineAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "AsyncStateMachine", out JsonElement section))
            return ApplySectionOverrides(new AsyncStateMachineAnalysisOptions(), section);

        return config.AsyncStateMachineAnalysis is null
            ? new AsyncStateMachineAnalysisOptions()
            : ApplyOptionsOverrides(new AsyncStateMachineAnalysisOptions(), config.AsyncStateMachineAnalysis);
    }

    private static ArrayAnalysisOptions BuildArrayAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "Array", out JsonElement section))
            return ApplySectionOverrides(new ArrayAnalysisOptions(), section);

        return config.ArrayAnalysis is null
            ? new ArrayAnalysisOptions()
            : ApplyOptionsOverrides(new ArrayAnalysisOptions(), config.ArrayAnalysis);
    }

    private static BoxingAnalysisOptions BuildBoxingAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "Boxing", out JsonElement section))
            return ApplySectionOverrides(new BoxingAnalysisOptions(), section);

        return config.BoxingAnalysis is null
            ? new BoxingAnalysisOptions()
            : ApplyOptionsOverrides(new BoxingAnalysisOptions(), config.BoxingAnalysis);
    }

    private static StringAnalysisOptions BuildStringAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "String", out JsonElement section))
            return ApplySectionOverrides(new StringAnalysisOptions(), section);

        return config.StringAnalysis is null
            ? new StringAnalysisOptions()
            : ApplyOptionsOverrides(new StringAnalysisOptions(), config.StringAnalysis);
    }

    private static AllocationPatternAnalysisOptions BuildAllocationPatternAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "AllocationPattern",
            config.AllocationPatternAnalysis,
            AllocationPatternAnalysisOptions.Preset);

    private static ThreadStackClusterAnalysisOptions BuildThreadStackClusterAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "ThreadStackCluster", out JsonElement section))
            return ApplySectionOverrides(new ThreadStackClusterAnalysisOptions(), section);

        return config.ThreadStackClusterAnalysis is null
            ? new ThreadStackClusterAnalysisOptions()
            : ApplyOptionsOverrides(new ThreadStackClusterAnalysisOptions(), config.ThreadStackClusterAnalysis);
    }

    private static GCGenerationAnalysisOptions BuildGCGenerationAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "GCGeneration", out JsonElement section))
            return ApplySectionOverrides(new GCGenerationAnalysisOptions(), section);

        return config.GCGenerationAnalysis is null
            ? new GCGenerationAnalysisOptions()
            : ApplyOptionsOverrides(new GCGenerationAnalysisOptions(), config.GCGenerationAnalysis);
    }

    private static SegmentReservationAnalysisOptions BuildSegmentReservationAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "SegmentReservation", out JsonElement section))
            return ApplySectionOverrides(new SegmentReservationAnalysisOptions(), section);

        return config.SegmentReservationAnalysis is null
            ? new SegmentReservationAnalysisOptions()
            : ApplyOptionsOverrides(new SegmentReservationAnalysisOptions(), config.SegmentReservationAnalysis);
    }

    private static ThreadAnalysisOptions BuildThreadAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "Thread", out JsonElement section))
            return ApplySectionOverrides(new ThreadAnalysisOptions(), section);

        return config.ThreadAnalysis is null
            ? new ThreadAnalysisOptions()
            : ApplyOptionsOverrides(new ThreadAnalysisOptions(), config.ThreadAnalysis);
    }

    private static HangAnalysisOptions BuildHangAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "Hang", out JsonElement section))
            return ApplySectionOverrides(new HangAnalysisOptions(), section);

        return config.HangAnalysis is null
            ? new HangAnalysisOptions()
            : ApplyOptionsOverrides(new HangAnalysisOptions(), config.HangAnalysis);
    }

    private static JitAnalysisOptions BuildJitAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "Jit", out JsonElement section))
            return ApplySectionOverrides(new JitAnalysisOptions(), section);

        return config.JitAnalysis is null
            ? new JitAnalysisOptions()
            : ApplyOptionsOverrides(new JitAnalysisOptions(), config.JitAnalysis);
    }

    private static WeakReferenceAnalysisOptions BuildWeakReferenceAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "WeakReference",
            config.WeakReferenceAnalysis,
            WeakReferenceAnalysisOptions.Preset);

    private static ModuleAnalysisOptions BuildModuleAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "Module", out JsonElement section))
            return ApplySectionOverrides(new ModuleAnalysisOptions(), section);

        return config.ModuleAnalysis is null
            ? new ModuleAnalysisOptions()
            : ApplyOptionsOverrides(new ModuleAnalysisOptions(), config.ModuleAnalysis);
    }

    private static GCHandleAnalysisOptions BuildGCHandleAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "GCHandle", out JsonElement section))
            return ApplySectionOverrides(new GCHandleAnalysisOptions(), section);

        return config.GCHandleAnalysis is null
            ? new GCHandleAnalysisOptions()
            : ApplyOptionsOverrides(new GCHandleAnalysisOptions(), config.GCHandleAnalysis);
    }

    private static StaticRootLeakAnalysisOptions BuildStaticRootLeakAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "StaticRootLeak", out JsonElement section))
            return ApplySectionOverrides(new StaticRootLeakAnalysisOptions(), section);

        return config.StaticRootLeakAnalysis is null
            ? new StaticRootLeakAnalysisOptions()
            : ApplyOptionsOverrides(new StaticRootLeakAnalysisOptions(), config.StaticRootLeakAnalysis);
    }

    private static MemoryAnalysisOptions BuildMemoryAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "Memory", out JsonElement section))
            return ApplySectionOverrides(new MemoryAnalysisOptions(), section);

        return config.MemoryAnalysis is null
            ? new MemoryAnalysisOptions()
            : ApplyOptionsOverrides(new MemoryAnalysisOptions(), config.MemoryAnalysis);
    }

    private static T Resolve<T>(
        bool fromFile,
        Func<CliConfigurationFileModel, AnalysisCommandRequest, T> fromConfig,
        Func<AnalysisCommandRequest, T> fromCli,
        CliConfigurationFileModel? fileModel,
        AnalysisCommandRequest request)
        => fromFile ? fromConfig(fileModel!, request) : fromCli(request);

    private static string BuildOutputPath(string dumpPath, ReportFormat format)
    {
        string extension = format switch
        {
            ReportFormat.Markdown => ".md",
            ReportFormat.Text => ".txt",
            _ => ".html"
        };

        return Path.ChangeExtension(dumpPath, extension);
    }

    private static AnalysisProfile ResolveAnalyzerProfile(string? analyzerProfile, string? globalProfile)
        => ConfigurationParseHelpers.ParseAnalysisProfile(analyzerProfile)
           ?? ConfigurationParseHelpers.ParseAnalysisProfile(globalProfile)
           ?? AnalysisProfile.Balanced;

    private static bool TryGetAnalyzerSection(CliConfigurationFileModel config, string analyzerName, out JsonElement section)
    {
        section = default;
        if (config.Analyzers?.Sections is null)
            return false;

        foreach ((string key, JsonElement value) in config.Analyzers.Sections)
        {
            if (string.Equals(key, analyzerName, StringComparison.OrdinalIgnoreCase))
            {
                section = value;
                return value.ValueKind == JsonValueKind.Object;
            }
        }

        return false;
    }

    private static string? GetAnalyzerProfile(JsonElement section)
    {
        if (section.ValueKind != JsonValueKind.Object)
            return null;

        foreach (JsonProperty prop in section.EnumerateObject())
        {
            if (string.Equals(prop.Name, "Profile", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                return prop.Value.GetString();
        }

        return null;
    }

    private static T ApplySectionOverrides<T>(T baseOptions, JsonElement section) where T : class
    {
        JsonNode? baseNode = JsonSerializer.SerializeToNode(baseOptions);
        if (baseNode is not JsonObject obj || section.ValueKind != JsonValueKind.Object)
            return baseOptions;

        foreach (JsonProperty prop in section.EnumerateObject())
        {
            if (string.Equals(prop.Name, "Profile", StringComparison.OrdinalIgnoreCase))
                continue;

            obj[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }

        T? merged = obj.Deserialize<T>();
        return merged ?? baseOptions;
    }

    private static T ApplyOptionsOverrides<T>(T baseOptions, T overrideOptions) where T : class
    {
        JsonNode? baseNode = JsonSerializer.SerializeToNode(baseOptions);
        JsonNode? overrideNode = JsonSerializer.SerializeToNode(overrideOptions, s_ignoreDefaultWriteOptions);
        JsonObject? defaultObj = JsonSerializer.SerializeToNode(Activator.CreateInstance<T>()) as JsonObject;
        if (baseNode is not JsonObject baseObj || overrideNode is not JsonObject overrideObj)
            return baseOptions;

        foreach ((string key, JsonNode? value) in overrideObj)
        {
            if (defaultObj is not null
                && defaultObj.TryGetPropertyValue(key, out JsonNode? defaultValue)
                && JsonNode.DeepEquals(value, defaultValue))
            {
                continue;
            }

            baseObj[key] = value?.DeepClone();
        }

        T? merged = baseObj.Deserialize<T>();
        return merged ?? baseOptions;
    }

    private static T BuildAnalyzerOptionsFromConfig<T>(
        CliConfigurationFileModel config,
        string analyzerName,
        T? legacyOptions,
        Func<AnalysisProfile, T> createPreset) where T : class, new()
    {
        if (TryGetAnalyzerSection(config, analyzerName, out JsonElement section))
        {
            AnalysisProfile profile = ResolveAnalyzerProfile(GetAnalyzerProfile(section), config.Profile);
            T preset = createPreset(profile);
            return ApplySectionOverrides(preset, section);
        }

        AnalysisProfile globalProfile = ResolveAnalyzerProfile(analyzerProfile: null, config.Profile);
        T fallbackPreset = createPreset(globalProfile);
        return legacyOptions is null
            ? fallbackPreset
            : ApplyOptionsOverrides(fallbackPreset, legacyOptions);
    }

    private static AnalysisProfile? ParseAnalysisProfile(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return raw.Trim().ToLowerInvariant() switch
        {
            "fast" => AnalysisProfile.Fast,
            "balanced" => AnalysisProfile.Balanced,
            "full" => AnalysisProfile.Full,
            "deep" => AnalysisProfile.Full,
            _ => throw new ArgumentException($"Invalid Analysis Profile value '{raw}' in config.")
        };
    }

    private static CollectionAnalysisOptionsModel? MergeCollectionModel(CollectionAnalysisOptionsModel? primary, CollectionAnalysisOptionsModel? fallback)
    {
        if (primary is null)
            return fallback;
        if (fallback is null)
            return primary;

        return new CollectionAnalysisOptionsModel
        {
            WasteThresholdBytes = primary.WasteThresholdBytes ?? fallback.WasteThresholdBytes,
            TopWastefulCollectionsToShow = primary.TopWastefulCollectionsToShow ?? fallback.TopWastefulCollectionsToShow,
            MaxDegreeOfParallelism = primary.MaxDegreeOfParallelism ?? fallback.MaxDegreeOfParallelism,
            SurfaceProbingExceptions = primary.SurfaceProbingExceptions ?? fallback.SurfaceProbingExceptions,
            PathAnalysisTopN = primary.PathAnalysisTopN ?? fallback.PathAnalysisTopN,
            SerializeHeapAccess = primary.SerializeHeapAccess ?? fallback.SerializeHeapAccess,
        };
    }
}
