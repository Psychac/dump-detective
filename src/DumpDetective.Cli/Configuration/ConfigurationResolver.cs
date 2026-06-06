using DumpDetective.Cli.Commands;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Options;
using DumpDetective.Cli.Configuration;
using DumpDetective.Cli.Services;
using DumpDetective.Cli.Models;
using DumpDetective.Cli.Services.Configuration;
using static DumpDetective.Cli.Services.Configuration.ConfigurationParseHelpers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

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

        RetentionOptions memoryLeak = Resolve(usedConfigFile, BuildMemoryLeakFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, RetentionOptions.Preset), fileModel, request);
        ReferenceChainOptions refChain = Resolve(usedConfigFile, BuildReferenceChainFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, ReferenceChainOptions.Preset), fileModel, request);
        EventLeakOptions eventLeak = Resolve(usedConfigFile, BuildEventLeakFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, EventLeakOptions.Preset), fileModel, request);
        DiagnosticsOptions diagnostics = Resolve(usedConfigFile, BuildDiagnosticsFromConfig, AnalyzerOptionsBuilder.BuildDiagnosticsFromCli, fileModel, request);
        ReportOptions report = Resolve(usedConfigFile, BuildReportFromConfig, AnalyzerOptionsBuilder.BuildReportFromCli, fileModel, request);
        HeapIndexPrebuildMode indexMode = Resolve(usedConfigFile, BuildIndexPrebuildModeFromConfig, AnalyzerOptionsBuilder.BuildIndexPrebuildModeFromCli, fileModel, request);
        ExecutionPolicy executionPolicy = BuildExecutionPolicy(fileModel, memoryLeak, refChain, indexMode);
        CrashAnalysisOptions crash = Resolve(usedConfigFile, BuildCrashFromConfig, req => AnalyzerOptionsBuilder.BuildValidatedBalancedPresetFromCli(req, CrashAnalysisOptions.Preset, CrashAnalysisOptions.Validate), fileModel, request);
        AsyncTaskAnalysisOptions asyncTaskAnalysis = Resolve(usedConfigFile, BuildAsyncTaskAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, AsyncTaskAnalysisOptions.Preset), fileModel, request);
        AsyncStateMachineAnalysisOptions asyncStateMachineAnalysis = Resolve(usedConfigFile, BuildAsyncStateMachineAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, AsyncStateMachineAnalysisOptions.Preset), fileModel, request);
        ArrayAnalysisOptions arrayAnalysis = Resolve(usedConfigFile, BuildArrayAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, ArrayAnalysisOptions.Preset), fileModel, request);
        BoxingAnalysisOptions boxingAnalysis = Resolve(usedConfigFile, BuildBoxingAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, BoxingAnalysisOptions.Preset), fileModel, request);
        CollectionAnalysisOptions collection = Resolve(usedConfigFile, BuildCollectionFromConfig, req => AnalyzerOptionsBuilder.BuildValidatedBalancedPresetFromCli(req, CollectionAnalysisOptions.Preset, CollectionAnalysisOptions.Validate), fileModel, request);
        StringAnalysisOptions stringAnalysis = Resolve(usedConfigFile, BuildStringAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, StringAnalysisOptions.Preset), fileModel, request);
        HeapTopologyAnalysisOptions heapTopology = Resolve(usedConfigFile, BuildHeapTopologyAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, HeapTopologyAnalysisOptions.Preset), fileModel, request);
        AppDomainAnalysisOptions appDomainAnalysis = Resolve(usedConfigFile, BuildAppDomainAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, AppDomainAnalysisOptions.Preset), fileModel, request);
        AllocationPatternAnalysisOptions allocationPatternAnalysis = Resolve(usedConfigFile, BuildAllocationPatternAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, AllocationPatternAnalysisOptions.Preset), fileModel, request);
        ThreadStackClusterAnalysisOptions threadStackClusterAnalysis = Resolve(usedConfigFile, BuildThreadStackClusterAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, ThreadStackClusterAnalysisOptions.Preset), fileModel, request);
        LockGraphAnalysisOptions lockGraphAnalysis = Resolve(usedConfigFile, BuildLockGraphAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, LockGraphAnalysisOptions.Preset), fileModel, request);
        FinalizableObjectAnalysisOptions finalizableObjectAnalysis = Resolve(usedConfigFile, BuildFinalizableObjectAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, FinalizableObjectAnalysisOptions.Preset), fileModel, request);
        GCGenerationAnalysisOptions gcGenerationAnalysis = Resolve(usedConfigFile, BuildGCGenerationAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, GCGenerationAnalysisOptions.Preset), fileModel, request);
        GCRootAnalysisOptions gcRootAnalysis = Resolve(usedConfigFile, BuildGCRootAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, GCRootAnalysisOptions.Preset), fileModel, request);
        LohFragmentationAnalysisOptions lohFragmentationAnalysis = Resolve(usedConfigFile, BuildLohFragmentationAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, LohFragmentationAnalysisOptions.Preset), fileModel, request);
        SegmentReservationAnalysisOptions segmentReservationAnalysis = Resolve(usedConfigFile, BuildSegmentReservationAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, SegmentReservationAnalysisOptions.Preset), fileModel, request);
        ThreadAnalysisOptions threadAnalysis = Resolve(usedConfigFile, BuildThreadAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, ThreadAnalysisOptions.Preset), fileModel, request);
        HangAnalysisOptions hangAnalysis = Resolve(usedConfigFile, BuildHangAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, HangAnalysisOptions.Preset), fileModel, request);
        JitAnalysisOptions jitAnalysis = Resolve(usedConfigFile, BuildJitAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, JitAnalysisOptions.Preset), fileModel, request);
        WeakReferenceAnalysisOptions weakReferenceAnalysis = Resolve(usedConfigFile, BuildWeakReferenceAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, WeakReferenceAnalysisOptions.Preset), fileModel, request);
        ObjectShapeAnalysisOptions objectShapeAnalysis = Resolve(usedConfigFile, BuildObjectShapeAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, ObjectShapeAnalysisOptions.Preset), fileModel, request);
        ModuleAnalysisOptions moduleAnalysis = Resolve(usedConfigFile, BuildModuleAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, ModuleAnalysisOptions.Preset), fileModel, request);
        DependentHandleAnalysisOptions dependentHandleAnalysis = Resolve(usedConfigFile, BuildDependentHandleAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, DependentHandleAnalysisOptions.Preset), fileModel, request);
        GCHandleAnalysisOptions gcHandleAnalysis = Resolve(usedConfigFile, BuildGCHandleAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, GCHandleAnalysisOptions.Preset), fileModel, request);
        StaticRootLeakAnalysisOptions staticRootLeakAnalysis = Resolve(usedConfigFile, BuildStaticRootLeakAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, StaticRootLeakAnalysisOptions.Preset), fileModel, request);
        MemoryAnalysisOptions memoryAnalysis = Resolve(usedConfigFile, BuildMemoryAnalysisFromConfig, req => AnalyzerOptionsBuilder.BuildBalancedPresetFromCli(req, MemoryAnalysisOptions.Preset), fileModel, request);

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
            heapTopology,
            appDomainAnalysis,
            allocationPatternAnalysis,
            threadStackClusterAnalysis,
            lockGraphAnalysis,
            finalizableObjectAnalysis,
            gcGenerationAnalysis,
            gcRootAnalysis,
            lohFragmentationAnalysis,
            segmentReservationAnalysis,
            threadAnalysis,
            hangAnalysis,
            jitAnalysis,
            weakReferenceAnalysis,
            objectShapeAnalysis,
            moduleAnalysis,
            dependentHandleAnalysis,
            gcHandleAnalysis,
            staticRootLeakAnalysis,
            memoryAnalysis,
            configPath,
            usedConfigFile,
                effectiveInclude,
                effectiveExclude,
            request.DiagnosticMode,
            indexMode)
        {
            ExecutionPolicy = executionPolicy
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
        => BuildAnalyzerOptionsFromConfig(
            config,
            "MemoryLeak",
            config.MemoryLeak,
            RetentionOptions.Preset);



    private static ReferenceChainOptions BuildReferenceChainFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "ReferenceChain",
            config.ReferenceChain,
            ReferenceChainOptions.Preset);



    private static EventLeakOptions BuildEventLeakFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "EventLeak",
            config.EventLeak,
            EventLeakOptions.Preset);



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



    private static HeapIndexPrebuildMode BuildIndexPrebuildModeFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        return config.ExecutionPolicy?.IndexPrebuildMode
            ?? ParseHeapIndexMode(config.Indexing?.Mode)
            ?? ParseHeapIndexMode(config.IndexMode)
            ?? request.IndexPrebuildMode
            ?? HeapIndexPrebuildMode.Auto;
    }

    private static ExecutionPolicy BuildExecutionPolicy(
        CliConfigurationFileModel? config,
        RetentionOptions memoryLeak,
        ReferenceChainOptions referenceChain,
        HeapIndexPrebuildMode indexMode)
    {
        ExecutionPolicyModel? policy = config?.ExecutionPolicy;

        return new ExecutionPolicy
        {
            MaxLeakScanObjects = PositiveOrNull(policy?.MaxLeakScanObjects) ?? memoryLeak.MaxLeakScanObjects,
            MaxReferenceAddresses = PositiveOrNull(policy?.MaxReferenceAddresses) ?? memoryLeak.MaxReferenceAddresses,
            ReferenceChainMaxPathDepth = PositiveOrNull(policy?.ReferenceChainMaxPathDepth) ?? referenceChain.MaxPathDepth,
            ReferenceChainFastModeMaxDepth = PositiveOrNull(policy?.ReferenceChainFastModeMaxDepth) ?? referenceChain.FastModeMaxDepth,
            ReferenceChainMaxPathSearchObjects = PositiveOrNull(policy?.ReferenceChainMaxPathSearchObjects) ?? referenceChain.MaxPathSearchObjects
        };
    }



    private static CollectionAnalysisOptions BuildCollectionFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        CollectionAnalysisOptionsModel? legacy = config.Collection;
        CollectionAnalysisOptionsModel? modern = null;
        if (TryGetAnalyzerSection(config, "Collection", out JsonElement modernSection))
            modern = modernSection.Deserialize<CollectionAnalysisOptionsModel>();
        CollectionAnalysisOptionsModel? model = MergeCollectionModel(primary: modern, fallback: legacy);

        AnalysisProfile profile = ResolveAnalyzerProfile(model?.Profile, config.Profile);
        CollectionAnalysisOptions preset = CollectionAnalysisOptions.Preset(profile);
        CollectionAnalysisOptions effective = CollectionAnalysisOptions.ApplyOverrides(preset, model);
        effective = CollectionAnalysisOptions.Validate(effective);

        return effective;
    }

    private static CrashAnalysisOptions BuildCrashFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        CrashAnalysisOptionsModel? legacy = config.Crash;
        CrashAnalysisOptionsModel? modern = null;
        if (TryGetAnalyzerSection(config, "Crash", out JsonElement modernSection))
            modern = modernSection.Deserialize<CrashAnalysisOptionsModel>();
        CrashAnalysisOptionsModel? model = MergeCrashModel(primary: modern, fallback: legacy);

        AnalysisProfile profile = ResolveAnalyzerProfile(model?.Profile, config.Profile);
        CrashAnalysisOptions preset = CrashAnalysisOptions.Preset(profile);
        CrashAnalysisOptions effective = CrashAnalysisOptions.ApplyOverrides(preset, model);
        effective = CrashAnalysisOptions.Validate(effective);

        return effective;
    }

    private static AsyncTaskAnalysisOptions BuildAsyncTaskAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "AsyncTask",
            config.AsyncTaskAnalysis,
            AsyncTaskAnalysisOptions.Preset);

    private static AsyncStateMachineAnalysisOptions BuildAsyncStateMachineAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "AsyncStateMachine",
            config.AsyncStateMachineAnalysis,
            AsyncStateMachineAnalysisOptions.Preset);

    private static ArrayAnalysisOptions BuildArrayAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "Array",
            config.ArrayAnalysis,
            ArrayAnalysisOptions.Preset);

    private static BoxingAnalysisOptions BuildBoxingAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "Boxing",
            config.BoxingAnalysis,
            BoxingAnalysisOptions.Preset);

    private static StringAnalysisOptions BuildStringAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "String",
            config.StringAnalysis,
            StringAnalysisOptions.Preset);

    private static HeapTopologyAnalysisOptions BuildHeapTopologyAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "HeapTopology",
            config.HeapTopology,
            HeapTopologyAnalysisOptions.Preset);

    private static AppDomainAnalysisOptions BuildAppDomainAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "AppDomain",
            config.AppDomainAnalysis,
            AppDomainAnalysisOptions.Preset);

    private static AllocationPatternAnalysisOptions BuildAllocationPatternAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "AllocationPattern",
            config.AllocationPatternAnalysis,
            AllocationPatternAnalysisOptions.Preset);

    private static ThreadStackClusterAnalysisOptions BuildThreadStackClusterAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "ThreadStackCluster",
            config.ThreadStackClusterAnalysis,
            ThreadStackClusterAnalysisOptions.Preset);

    private static LockGraphAnalysisOptions BuildLockGraphAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "LockGraph",
            config.LockGraphAnalysis,
            LockGraphAnalysisOptions.Preset);

    private static FinalizableObjectAnalysisOptions BuildFinalizableObjectAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "FinalizableObject",
            config.FinalizableObjectAnalysis,
            FinalizableObjectAnalysisOptions.Preset);

    private static GCGenerationAnalysisOptions BuildGCGenerationAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "GCGeneration",
            config.GCGenerationAnalysis,
            GCGenerationAnalysisOptions.Preset);

    private static GCRootAnalysisOptions BuildGCRootAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "GCRoot",
            config.GCRootAnalysis,
            GCRootAnalysisOptions.Preset);

    private static LohFragmentationAnalysisOptions BuildLohFragmentationAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "LohFragmentation",
            config.LohFragmentationAnalysis,
            LohFragmentationAnalysisOptions.Preset);

    private static SegmentReservationAnalysisOptions BuildSegmentReservationAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "SegmentReservation",
            config.SegmentReservationAnalysis,
            SegmentReservationAnalysisOptions.Preset);

    private static ThreadAnalysisOptions BuildThreadAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "Thread",
            config.ThreadAnalysis,
            ThreadAnalysisOptions.Preset);

    private static HangAnalysisOptions BuildHangAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "Hang",
            config.HangAnalysis,
            HangAnalysisOptions.Preset);

    private static JitAnalysisOptions BuildJitAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "Jit",
            config.JitAnalysis,
            JitAnalysisOptions.Preset);

    private static WeakReferenceAnalysisOptions BuildWeakReferenceAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "WeakReference",
            config.WeakReferenceAnalysis,
            WeakReferenceAnalysisOptions.Preset);

    private static ObjectShapeAnalysisOptions BuildObjectShapeAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "ObjectShape",
            config.ObjectShapeAnalysis,
            ObjectShapeAnalysisOptions.Preset);

    private static ModuleAnalysisOptions BuildModuleAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "Module",
            config.ModuleAnalysis,
            ModuleAnalysisOptions.Preset);

    private static DependentHandleAnalysisOptions BuildDependentHandleAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "DependentHandle",
            config.DependentHandleAnalysis,
            DependentHandleAnalysisOptions.Preset);

    private static GCHandleAnalysisOptions BuildGCHandleAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "GCHandle",
            config.GCHandleAnalysis,
            GCHandleAnalysisOptions.Preset);

    private static StaticRootLeakAnalysisOptions BuildStaticRootLeakAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "StaticRootLeak",
            config.StaticRootLeakAnalysis,
            StaticRootLeakAnalysisOptions.Preset);

    private static MemoryAnalysisOptions BuildMemoryAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "Memory",
            config.MemoryAnalysis,
            MemoryAnalysisOptions.Preset);

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

    private static CrashAnalysisOptionsModel? MergeCrashModel(CrashAnalysisOptionsModel? primary, CrashAnalysisOptionsModel? fallback)
    {
        if (primary is null)
            return fallback;
        if (fallback is null)
            return primary;

        return new CrashAnalysisOptionsModel
        {
            Profile = primary.Profile ?? fallback.Profile,
            MaxExceptionsPerType = primary.MaxExceptionsPerType ?? fallback.MaxExceptionsPerType,
            TopExceptionTypesToInclude = primary.TopExceptionTypesToInclude ?? fallback.TopExceptionTypesToInclude,
            MaxDetailedExceptionsPerType = primary.MaxDetailedExceptionsPerType ?? fallback.MaxDetailedExceptionsPerType,
            MaxOriginalStackFramesToPrint = primary.MaxOriginalStackFramesToPrint ?? fallback.MaxOriginalStackFramesToPrint,
            MaxCurrentThreadFramesToPrint = primary.MaxCurrentThreadFramesToPrint ?? fallback.MaxCurrentThreadFramesToPrint,
            TopCrashThreadCandidates = primary.TopCrashThreadCandidates ?? fallback.TopCrashThreadCandidates,
            TopDetailedExceptionInstances = primary.TopDetailedExceptionInstances ?? fallback.TopDetailedExceptionInstances,
            IncludeAllTypesInPayload = primary.IncludeAllTypesInPayload ?? fallback.IncludeAllTypesInPayload,
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
            Profile = primary.Profile ?? fallback.Profile,
            WasteThresholdBytes = primary.WasteThresholdBytes ?? fallback.WasteThresholdBytes,
            TopWastefulCollectionsToShow = primary.TopWastefulCollectionsToShow ?? fallback.TopWastefulCollectionsToShow,
            MaxDegreeOfParallelism = primary.MaxDegreeOfParallelism ?? fallback.MaxDegreeOfParallelism,
            IncludeQueueAnalysis = primary.IncludeQueueAnalysis ?? fallback.IncludeQueueAnalysis,
            SurfaceProbingExceptions = primary.SurfaceProbingExceptions ?? fallback.SurfaceProbingExceptions,
            PathAnalysisTopN = primary.PathAnalysisTopN ?? fallback.PathAnalysisTopN,
            ReferenceChainOptions = primary.ReferenceChainOptions ?? fallback.ReferenceChainOptions,
            SerializeHeapAccess = primary.SerializeHeapAccess ?? fallback.SerializeHeapAccess,
        };
    }
}
