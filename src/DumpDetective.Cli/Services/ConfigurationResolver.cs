using DumpDetective.Cli.Commands;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Options;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DumpDetective.Cli.Services;

internal sealed class ConfigurationResolver
{
    private const string DefaultConfigFileName = "config.json";
    private const string FallbackSampleConfigFileName = "config.sample.json";

    public ResolvedExecutionOptions Resolve(AnalysisCommandRequest request)
    {
        string? configPath = ResolveConfigPath(request.ConfigPath);
        CliConfigurationFileModel? fileModel = configPath is null ? null : LoadConfigurationFile(configPath);

        bool usedConfigFile = fileModel is not null;

        MemoryLeakOptions memoryLeak      = Resolve(usedConfigFile, BuildMemoryLeakFromConfig,         BuildMemoryLeakFromCli,         fileModel, request);
        ReferenceChainOptions refChain    = Resolve(usedConfigFile, BuildReferenceChainFromConfig,     BuildReferenceChainFromCli,     fileModel, request);
        EventLeakOptions eventLeak        = Resolve(usedConfigFile, BuildEventLeakFromConfig,          BuildEventLeakFromCli,          fileModel, request);
        DiagnosticsOptions diagnostics    = Resolve(usedConfigFile, BuildDiagnosticsFromConfig,        BuildDiagnosticsFromCli,        fileModel, request);
        ReportOptions report              = Resolve(usedConfigFile, BuildReportFromConfig,             BuildReportFromCli,             fileModel, request);
        HeapIndexPrebuildMode indexMode   = Resolve(usedConfigFile, BuildIndexPrebuildModeFromConfig,  BuildIndexPrebuildModeFromCli,  fileModel, request);
        CrashAnalysisOptions crash = Resolve(usedConfigFile, BuildCrashFromConfig, BuildCrashFromCli, fileModel, request);
        AsyncTaskAnalysisOptions asyncTaskAnalysis = Resolve(usedConfigFile, BuildAsyncTaskAnalysisFromConfig, BuildAsyncTaskAnalysisFromCli, fileModel, request);
        AsyncStateMachineAnalysisOptions asyncStateMachineAnalysis = Resolve(usedConfigFile, BuildAsyncStateMachineAnalysisFromConfig, BuildAsyncStateMachineAnalysisFromCli, fileModel, request);
        ArrayAnalysisOptions arrayAnalysis = Resolve(usedConfigFile, BuildArrayAnalysisFromConfig, BuildArrayAnalysisFromCli, fileModel, request);
        BoxingAnalysisOptions boxingAnalysis = Resolve(usedConfigFile, BuildBoxingAnalysisFromConfig, BuildBoxingAnalysisFromCli, fileModel, request);
        CollectionAnalysisOptions collection = Resolve(usedConfigFile, BuildCollectionFromConfig,     BuildCollectionFromCli,         fileModel, request);
        StringAnalysisOptions  stringAnalysis = Resolve(usedConfigFile, BuildStringAnalysisFromConfig, BuildStringAnalysisFromCli,     fileModel, request);
        SegmentAnalysisOptions segmentAnalysis = Resolve(usedConfigFile, BuildSegmentAnalysisFromConfig, BuildSegmentAnalysisFromCli,  fileModel, request);
        AppDomainAnalysisOptions appDomainAnalysis = Resolve(usedConfigFile, BuildAppDomainAnalysisFromConfig, BuildAppDomainAnalysisFromCli, fileModel, request);
        AllocationPatternAnalysisOptions allocationPatternAnalysis = Resolve(usedConfigFile, BuildAllocationPatternAnalysisFromConfig, BuildAllocationPatternAnalysisFromCli, fileModel, request);
        ThreadStackClusterAnalysisOptions threadStackClusterAnalysis = Resolve(usedConfigFile, BuildThreadStackClusterAnalysisFromConfig, BuildThreadStackClusterAnalysisFromCli, fileModel, request);
        LockGraphAnalysisOptions lockGraphAnalysis = Resolve(usedConfigFile, BuildLockGraphAnalysisFromConfig, BuildLockGraphAnalysisFromCli, fileModel, request);
        FinalizableObjectAnalysisOptions finalizableObjectAnalysis = Resolve(usedConfigFile, BuildFinalizableObjectAnalysisFromConfig, BuildFinalizableObjectAnalysisFromCli, fileModel, request);
        GCGenerationAnalysisOptions gcGenerationAnalysis = Resolve(usedConfigFile, BuildGCGenerationAnalysisFromConfig, BuildGCGenerationAnalysisFromCli, fileModel, request);
        GCRootAnalysisOptions gcRootAnalysis = Resolve(usedConfigFile, BuildGCRootAnalysisFromConfig, BuildGCRootAnalysisFromCli, fileModel, request);
        LohFragmentationAnalysisOptions lohFragmentationAnalysis = Resolve(usedConfigFile, BuildLohFragmentationAnalysisFromConfig, BuildLohFragmentationAnalysisFromCli, fileModel, request);
        SegmentReservationAnalysisOptions segmentReservationAnalysis = Resolve(usedConfigFile, BuildSegmentReservationAnalysisFromConfig, BuildSegmentReservationAnalysisFromCli, fileModel, request);
        ThreadAnalysisOptions threadAnalysis = Resolve(usedConfigFile, BuildThreadAnalysisFromConfig, BuildThreadAnalysisFromCli, fileModel, request);
        HangAnalysisOptions hangAnalysis = Resolve(usedConfigFile, BuildHangAnalysisFromConfig, BuildHangAnalysisFromCli, fileModel, request);
        JitAnalysisOptions jitAnalysis = Resolve(usedConfigFile, BuildJitAnalysisFromConfig, BuildJitAnalysisFromCli, fileModel, request);
        WeakReferenceAnalysisOptions weakReferenceAnalysis = Resolve(usedConfigFile, BuildWeakReferenceAnalysisFromConfig, BuildWeakReferenceAnalysisFromCli, fileModel, request);
        ObjectShapeAnalysisOptions objectShapeAnalysis = Resolve(usedConfigFile, BuildObjectShapeAnalysisFromConfig, BuildObjectShapeAnalysisFromCli, fileModel, request);
        ModuleAnalysisOptions moduleAnalysis = Resolve(usedConfigFile, BuildModuleAnalysisFromConfig, BuildModuleAnalysisFromCli, fileModel, request);
        DependentHandleAnalysisOptions dependentHandleAnalysis = Resolve(usedConfigFile, BuildDependentHandleAnalysisFromConfig, BuildDependentHandleAnalysisFromCli, fileModel, request);
        GCHandleAnalysisOptions gcHandleAnalysis = Resolve(usedConfigFile, BuildGCHandleAnalysisFromConfig, BuildGCHandleAnalysisFromCli, fileModel, request);
        StaticRootLeakAnalysisOptions staticRootLeakAnalysis = Resolve(usedConfigFile, BuildStaticRootLeakAnalysisFromConfig, BuildStaticRootLeakAnalysisFromCli, fileModel, request);
        MemoryAnalysisOptions memoryAnalysis = Resolve(usedConfigFile, BuildMemoryAnalysisFromConfig, BuildMemoryAnalysisFromCli, fileModel, request);

        string? configuredDumpPath = fileModel?.DumpPath;
        string? configuredBaseline = fileModel?.BaselineDumpPath;
        IReadOnlyList<string>? configuredTrend = fileModel?.TrendDumpPaths;
        IReadOnlyList<string>? effectiveTrend = configuredTrend ?? request.TrendDumpPaths;

        string? effectiveDumpPath = !string.IsNullOrWhiteSpace(configuredDumpPath)
            ? configuredDumpPath
            : !string.IsNullOrWhiteSpace(request.DumpPath)
                ? request.DumpPath
                : effectiveTrend?.LastOrDefault();
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
            segmentAnalysis,
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
            request.IncludeAnalyzers,
            request.ExcludeAnalyzers,
            request.DiagnosticMode,
            indexMode);
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

    private static MemoryLeakOptions BuildMemoryLeakFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "MemoryLeak", out JsonElement section))
        {
            AnalysisProfile profile = ResolveAnalyzerProfile(GetAnalyzerProfile(section), config.Profile);
            MemoryLeakOptions preset = MemoryLeakOptions.Preset(profile);

            return ApplySectionOverrides(preset, section);
        }

        int highReferenceThreshold = PositiveOrNull(config.MemoryLeak?.HighReferenceThreshold)
            ?? PositiveOrNull(config.HighReferenceThreshold)
            ?? request.HighReferenceThreshold
            ?? 50;

        int maxDuplicateStringLength = PositiveOrNull(config.MemoryLeak?.MaxDuplicateStringLength)
            ?? PositiveOrNull(config.MaxDuplicateStringLength)
            ?? request.MaxDuplicateStringLength
            ?? 500;

        int minDuplicateStringCount = PositiveOrNull(config.MemoryLeak?.MinDuplicateStringCount)
            ?? PositiveOrNull(config.MinDuplicateStringCount)
            ?? request.MinDuplicateStringCount
            ?? 10;

        int maxReferenceAddresses = PositiveOrNull(config.MemoryLeak?.MaxReferenceAddresses)
            ?? PositiveOrNull(config.MaxReferenceAddressesToTrack)
            ?? request.MaxReferenceAddresses
            ?? 1_000_000;

        return new MemoryLeakOptions
        {
            HighReferenceThreshold = highReferenceThreshold,
            MaxDuplicateStringLength = maxDuplicateStringLength,
            MinDuplicateStringCount = minDuplicateStringCount,
            MaxReferenceAddresses = maxReferenceAddresses
        };
    }

    private static MemoryLeakOptions BuildMemoryLeakFromCli(AnalysisCommandRequest request)
    {
        return new MemoryLeakOptions
        {
            HighReferenceThreshold = request.HighReferenceThreshold ?? 50,
            MaxDuplicateStringLength = request.MaxDuplicateStringLength ?? 500,
            MinDuplicateStringCount = request.MinDuplicateStringCount ?? 10,
            MaxReferenceAddresses = request.MaxReferenceAddresses ?? 1_000_000
        };
    }

    private static ReferenceChainOptions BuildReferenceChainFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "ReferenceChain", out JsonElement section))
        {
            AnalysisProfile profile = ResolveAnalyzerProfile(GetAnalyzerProfile(section), config.Profile);
            ReferenceChainOptions preset = ReferenceChainOptions.Preset(profile);

            return ApplySectionOverrides(preset, section);
        }

        int topCount = PositiveOrNull(config.ReferenceChain?.TopCount)
            ?? PositiveOrNull(config.ReferenceChainTopCount)
            ?? request.ReferenceChainTopCount
            ?? 5;

        int maxPathSearchObjects = PositiveOrNull(config.ReferenceChain?.MaxPathSearchObjects)
            ?? PositiveOrNull(config.ReferenceChainMaxPathSearchObjects)
            ?? request.ReferenceChainMaxPathSearchObjects
            ?? 5_000;

        return new ReferenceChainOptions
        {
            TopCount = topCount,
            MaxPathSearchObjects = maxPathSearchObjects
        };
    }

    private static ReferenceChainOptions BuildReferenceChainFromCli(AnalysisCommandRequest request)
    {
        return new ReferenceChainOptions
        {
            TopCount = request.ReferenceChainTopCount ?? 5,
            MaxPathSearchObjects = request.ReferenceChainMaxPathSearchObjects ?? 5_000
        };
    }

    private static EventLeakOptions BuildEventLeakFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "EventLeak", out JsonElement section))
        {
            AnalysisProfile profile = ResolveAnalyzerProfile(GetAnalyzerProfile(section), config.Profile);
            EventLeakOptions preset = EventLeakOptions.Preset(profile);

            return ApplySectionOverrides(preset, section);
        }

        int minSubscribers = NonNegativeOrNull(config.EventLeak?.MinSubscribers)
            ?? NonNegativeOrNull(config.EventLeakMinSubscribers)
            ?? request.EventLeakMinSubscribers
            ?? 0;

        bool includeNonLeaking = config.EventLeak?.IncludeNonLeakingEvents ?? false;

        return new EventLeakOptions
        {
            MinSubscribers = minSubscribers,
            IncludeNonLeakingEvents = includeNonLeaking
        };
    }

    private static EventLeakOptions BuildEventLeakFromCli(AnalysisCommandRequest request)
    {
        return new EventLeakOptions
        {
            MinSubscribers = request.EventLeakMinSubscribers ?? 0
        };
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

        return new DiagnosticsOptions
        {
            EnableMemoryDiagnostics = enableMemoryDiagnostics,
            EnablePerformanceDiagnostics = enablePerformanceDiagnostics
            , CollectAfterAnalyzerRun = collectAfterAnalyzerRun
        };
    }

    private static DiagnosticsOptions BuildDiagnosticsFromCli(AnalysisCommandRequest request)
    {
        return new DiagnosticsOptions
        {
            EnableMemoryDiagnostics = request.EnableMemoryDiagnostics,
            EnablePerformanceDiagnostics = request.EnablePerformanceDiagnostics
            , CollectAfterAnalyzerRun = false
        };
    }

    private static ReportOptions BuildReportFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        return new ReportOptions
        {
            Format = config.Report?.Format ?? ParseReportFormat(config.ReportFormat) ?? request.OutputFormat ?? ReportFormat.Html,
            Audience = config.Report?.Audience ?? ParseReportAudience(config.ReportAudience) ?? request.ReportAudience ?? ReportAudience.All
        };
    }

    private static ReportOptions BuildReportFromCli(AnalysisCommandRequest request)
    {
        return new ReportOptions
        {
            Format = request.OutputFormat ?? ReportFormat.Html,
            Audience = request.ReportAudience ?? ReportAudience.All
        };
    }

    private static HeapIndexPrebuildMode BuildIndexPrebuildModeFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        return ParseHeapIndexMode(config.Indexing?.Mode)
            ?? ParseHeapIndexMode(config.IndexMode)
            ?? request.IndexPrebuildMode
            ?? HeapIndexPrebuildMode.Auto;
    }

    private static HeapIndexPrebuildMode BuildIndexPrebuildModeFromCli(AnalysisCommandRequest request)
    {
        return request.IndexPrebuildMode ?? HeapIndexPrebuildMode.Auto;
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

    private static CollectionAnalysisOptions BuildCollectionFromCli(AnalysisCommandRequest request)
    {
        CollectionAnalysisOptions preset = CollectionAnalysisOptions.Preset(AnalysisProfile.Balanced);
        return CollectionAnalysisOptions.Validate(preset);
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

    private static CrashAnalysisOptions BuildCrashFromCli(AnalysisCommandRequest request)
    {
        CrashAnalysisOptions preset = CrashAnalysisOptions.Preset(AnalysisProfile.Balanced);
        return CrashAnalysisOptions.Validate(preset);
    }

    private static AsyncTaskAnalysisOptions BuildAsyncTaskAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "AsyncTask", out JsonElement section))
        {
            AnalysisProfile profile = ResolveAnalyzerProfile(GetAnalyzerProfile(section), config.Profile);
            AsyncTaskAnalysisOptions preset = AsyncTaskAnalysisOptions.Preset(profile);

            return ApplySectionOverrides(preset, section);
        }

        return config.AsyncTaskAnalysis ?? new AsyncTaskAnalysisOptions();
    }

    private static AsyncTaskAnalysisOptions BuildAsyncTaskAnalysisFromCli(AnalysisCommandRequest request)
        => new AsyncTaskAnalysisOptions();

    private static AsyncStateMachineAnalysisOptions BuildAsyncStateMachineAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "AsyncStateMachine", out JsonElement section))
        {
            AnalysisProfile profile = ResolveAnalyzerProfile(GetAnalyzerProfile(section), config.Profile);
            AsyncStateMachineAnalysisOptions preset = AsyncStateMachineAnalysisOptions.Preset(profile);

            return ApplySectionOverrides(preset, section);
        }

        return config.AsyncStateMachineAnalysis ?? new AsyncStateMachineAnalysisOptions();
    }

    private static AsyncStateMachineAnalysisOptions BuildAsyncStateMachineAnalysisFromCli(AnalysisCommandRequest request)
        => new AsyncStateMachineAnalysisOptions();

    private static ArrayAnalysisOptions BuildArrayAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "Array", out JsonElement section))
        {
            AnalysisProfile profile = ResolveAnalyzerProfile(GetAnalyzerProfile(section), config.Profile);
            ArrayAnalysisOptions preset = ArrayAnalysisOptions.Preset(profile);

            return ApplySectionOverrides(preset, section);
        }

        return config.ArrayAnalysis ?? new ArrayAnalysisOptions();
    }

    private static ArrayAnalysisOptions BuildArrayAnalysisFromCli(AnalysisCommandRequest request)
        => new ArrayAnalysisOptions();

    private static BoxingAnalysisOptions BuildBoxingAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
    {
        if (TryGetAnalyzerSection(config, "Boxing", out JsonElement section))
        {
            AnalysisProfile profile = ResolveAnalyzerProfile(GetAnalyzerProfile(section), config.Profile);
            BoxingAnalysisOptions preset = BoxingAnalysisOptions.Preset(profile);

            return ApplySectionOverrides(preset, section);
        }

        return config.BoxingAnalysis ?? new BoxingAnalysisOptions();
    }

    private static BoxingAnalysisOptions BuildBoxingAnalysisFromCli(AnalysisCommandRequest request)
        => new BoxingAnalysisOptions();

    private static StringAnalysisOptions BuildStringAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "String",
            config.StringAnalysis,
            StringAnalysisOptions.Preset);

    private static StringAnalysisOptions BuildStringAnalysisFromCli(AnalysisCommandRequest request)
        => new StringAnalysisOptions();

    private static SegmentAnalysisOptions BuildSegmentAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "Segment",
            config.SegmentAnalysis,
            SegmentAnalysisOptions.Preset);

    private static SegmentAnalysisOptions BuildSegmentAnalysisFromCli(AnalysisCommandRequest request)
        => new SegmentAnalysisOptions();

    private static AppDomainAnalysisOptions BuildAppDomainAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "AppDomain",
            config.AppDomainAnalysis,
            AppDomainAnalysisOptions.Preset);

    private static AppDomainAnalysisOptions BuildAppDomainAnalysisFromCli(AnalysisCommandRequest request)
        => new AppDomainAnalysisOptions();

    private static AllocationPatternAnalysisOptions BuildAllocationPatternAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "AllocationPattern",
            config.AllocationPatternAnalysis,
            AllocationPatternAnalysisOptions.Preset);

    private static AllocationPatternAnalysisOptions BuildAllocationPatternAnalysisFromCli(AnalysisCommandRequest request)
        => new AllocationPatternAnalysisOptions();

    private static ThreadStackClusterAnalysisOptions BuildThreadStackClusterAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "ThreadStackCluster",
            config.ThreadStackClusterAnalysis,
            ThreadStackClusterAnalysisOptions.Preset);

    private static ThreadStackClusterAnalysisOptions BuildThreadStackClusterAnalysisFromCli(AnalysisCommandRequest request)
        => new ThreadStackClusterAnalysisOptions();

    private static LockGraphAnalysisOptions BuildLockGraphAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "LockGraph",
            config.LockGraphAnalysis,
            LockGraphAnalysisOptions.Preset);

    private static LockGraphAnalysisOptions BuildLockGraphAnalysisFromCli(AnalysisCommandRequest request)
        => new LockGraphAnalysisOptions();

    private static FinalizableObjectAnalysisOptions BuildFinalizableObjectAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "FinalizableObject",
            config.FinalizableObjectAnalysis,
            FinalizableObjectAnalysisOptions.Preset);

    private static FinalizableObjectAnalysisOptions BuildFinalizableObjectAnalysisFromCli(AnalysisCommandRequest request)
        => new FinalizableObjectAnalysisOptions();

    private static GCGenerationAnalysisOptions BuildGCGenerationAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "GCGeneration",
            config.GCGenerationAnalysis,
            GCGenerationAnalysisOptions.Preset);

    private static GCGenerationAnalysisOptions BuildGCGenerationAnalysisFromCli(AnalysisCommandRequest request)
        => new GCGenerationAnalysisOptions();

    private static GCRootAnalysisOptions BuildGCRootAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "GCRoot",
            config.GCRootAnalysis,
            GCRootAnalysisOptions.Preset);

    private static GCRootAnalysisOptions BuildGCRootAnalysisFromCli(AnalysisCommandRequest request)
        => new GCRootAnalysisOptions();

    private static LohFragmentationAnalysisOptions BuildLohFragmentationAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "LohFragmentation",
            config.LohFragmentationAnalysis,
            LohFragmentationAnalysisOptions.Preset);

    private static LohFragmentationAnalysisOptions BuildLohFragmentationAnalysisFromCli(AnalysisCommandRequest request)
        => new LohFragmentationAnalysisOptions();

    private static SegmentReservationAnalysisOptions BuildSegmentReservationAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "SegmentReservation",
            config.SegmentReservationAnalysis,
            SegmentReservationAnalysisOptions.Preset);

    private static SegmentReservationAnalysisOptions BuildSegmentReservationAnalysisFromCli(AnalysisCommandRequest request)
        => new SegmentReservationAnalysisOptions();

    private static ThreadAnalysisOptions BuildThreadAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "Thread",
            config.ThreadAnalysis,
            ThreadAnalysisOptions.Preset);

    private static ThreadAnalysisOptions BuildThreadAnalysisFromCli(AnalysisCommandRequest request)
        => new ThreadAnalysisOptions();

    private static HangAnalysisOptions BuildHangAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "Hang",
            config.HangAnalysis,
            HangAnalysisOptions.Preset);

    private static HangAnalysisOptions BuildHangAnalysisFromCli(AnalysisCommandRequest request)
        => new HangAnalysisOptions();

    private static JitAnalysisOptions BuildJitAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "Jit",
            config.JitAnalysis,
            JitAnalysisOptions.Preset);

    private static JitAnalysisOptions BuildJitAnalysisFromCli(AnalysisCommandRequest request)
        => new JitAnalysisOptions();

    private static WeakReferenceAnalysisOptions BuildWeakReferenceAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "WeakReference",
            config.WeakReferenceAnalysis,
            WeakReferenceAnalysisOptions.Preset);

    private static WeakReferenceAnalysisOptions BuildWeakReferenceAnalysisFromCli(AnalysisCommandRequest request)
        => new WeakReferenceAnalysisOptions();

    private static ObjectShapeAnalysisOptions BuildObjectShapeAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "ObjectShape",
            config.ObjectShapeAnalysis,
            ObjectShapeAnalysisOptions.Preset);

    private static ObjectShapeAnalysisOptions BuildObjectShapeAnalysisFromCli(AnalysisCommandRequest request)
        => new ObjectShapeAnalysisOptions();

    private static ModuleAnalysisOptions BuildModuleAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "Module",
            config.ModuleAnalysis,
            ModuleAnalysisOptions.Preset);

    private static ModuleAnalysisOptions BuildModuleAnalysisFromCli(AnalysisCommandRequest request)
        => new ModuleAnalysisOptions();

    private static DependentHandleAnalysisOptions BuildDependentHandleAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "DependentHandle",
            config.DependentHandleAnalysis,
            DependentHandleAnalysisOptions.Preset);

    private static DependentHandleAnalysisOptions BuildDependentHandleAnalysisFromCli(AnalysisCommandRequest request)
        => new DependentHandleAnalysisOptions();

    private static GCHandleAnalysisOptions BuildGCHandleAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "GCHandle",
            config.GCHandleAnalysis,
            GCHandleAnalysisOptions.Preset);

    private static GCHandleAnalysisOptions BuildGCHandleAnalysisFromCli(AnalysisCommandRequest request)
        => new GCHandleAnalysisOptions();

    private static StaticRootLeakAnalysisOptions BuildStaticRootLeakAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "StaticRootLeak",
            config.StaticRootLeakAnalysis,
            StaticRootLeakAnalysisOptions.Preset);

    private static StaticRootLeakAnalysisOptions BuildStaticRootLeakAnalysisFromCli(AnalysisCommandRequest request)
        => new StaticRootLeakAnalysisOptions();

    private static MemoryAnalysisOptions BuildMemoryAnalysisFromConfig(CliConfigurationFileModel config, AnalysisCommandRequest request)
        => BuildAnalyzerOptionsFromConfig(
            config,
            "Memory",
            config.MemoryAnalysis,
            MemoryAnalysisOptions.Preset);

    private static MemoryAnalysisOptions BuildMemoryAnalysisFromCli(AnalysisCommandRequest request)
        => new MemoryAnalysisOptions();

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

    private static HeapIndexPrebuildMode? ParseHeapIndexMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return null;
        }

        return mode.Trim().ToLowerInvariant() switch
        {
            "auto" => HeapIndexPrebuildMode.Auto,
            "memory" or "mem" => HeapIndexPrebuildMode.Memory,
            "disk" => HeapIndexPrebuildMode.Disk,
            _ => throw new ArgumentException($"Invalid IndexMode value '{mode}' in config.")
        };
    }

    private static ReportAudience? ParseReportAudience(string? audience)
    {
        if (string.IsNullOrWhiteSpace(audience))
        {
            return null;
        }

        return audience.Trim().ToLowerInvariant() switch
        {
            "all" => ReportAudience.All,
            "executive" or "exec" => ReportAudience.Executive,
            "developer" or "dev" => ReportAudience.Developer,
            "deep" or "full" => ReportAudience.Deep,
            _ => throw new ArgumentException($"Invalid ReportAudience value '{audience}' in config.")
        };
    }

    private static ReportFormat? ParseReportFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return null;
        }

        return format.Trim().ToLowerInvariant() switch
        {
            "text" or "txt" => ReportFormat.Text,
            "markdown" or "md" => ReportFormat.Markdown,
            "html" or "htm" => ReportFormat.Html,
            _ => throw new ArgumentException($"Invalid ReportFormat value '{format}' in config.")
        };
    }

    private static int? PositiveOrNull(int? value) => value is > 0 ? value : null;

    private static int? NonNegativeOrNull(int? value) => value is >= 0 ? value : null;

    private static AnalysisProfile ResolveAnalyzerProfile(string? analyzerProfile, string? globalProfile)
        => ParseAnalysisProfile(analyzerProfile)
           ?? ParseAnalysisProfile(globalProfile)
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

        return legacyOptions ?? new T();
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

internal sealed class CliConfigurationFileModel
{
    public string? DumpPath { get; init; }
    public string? BaselineDumpPath { get; init; }
    public List<string>? TrendDumpPaths { get; init; }
    public string? Profile { get; init; }
    public AnalyzerOptionsModel? Analyzers { get; init; }

    public MemoryLeakOptions? MemoryLeak { get; init; }
    public ReferenceChainOptions? ReferenceChain { get; init; }
    public EventLeakOptions? EventLeak { get; init; }
    public DiagnosticsOptions? Diagnostics { get; init; }
    public CrashAnalysisOptionsModel? Crash { get; init; }
    public AsyncTaskAnalysisOptions? AsyncTaskAnalysis { get; init; }
    public AsyncStateMachineAnalysisOptions? AsyncStateMachineAnalysis { get; init; }
    public ArrayAnalysisOptions? ArrayAnalysis { get; init; }
    public BoxingAnalysisOptions? BoxingAnalysis { get; init; }
    public CollectionAnalysisOptionsModel? Collection { get; init; }
    public StringAnalysisOptions? StringAnalysis { get; init; }
    public SegmentAnalysisOptions? SegmentAnalysis { get; init; }
    public AppDomainAnalysisOptions? AppDomainAnalysis { get; init; }
    public AllocationPatternAnalysisOptions? AllocationPatternAnalysis { get; init; }
    public ThreadStackClusterAnalysisOptions? ThreadStackClusterAnalysis { get; init; }
    public LockGraphAnalysisOptions? LockGraphAnalysis { get; init; }
    public FinalizableObjectAnalysisOptions? FinalizableObjectAnalysis { get; init; }
    public GCGenerationAnalysisOptions? GCGenerationAnalysis { get; init; }
    public GCRootAnalysisOptions? GCRootAnalysis { get; init; }
    public LohFragmentationAnalysisOptions? LohFragmentationAnalysis { get; init; }
    public SegmentReservationAnalysisOptions? SegmentReservationAnalysis { get; init; }
    public ThreadAnalysisOptions? ThreadAnalysis { get; init; }
    public HangAnalysisOptions? HangAnalysis { get; init; }
    public JitAnalysisOptions? JitAnalysis { get; init; }
    public WeakReferenceAnalysisOptions? WeakReferenceAnalysis { get; init; }
    public ObjectShapeAnalysisOptions? ObjectShapeAnalysis { get; init; }
    public ModuleAnalysisOptions? ModuleAnalysis { get; init; }
    public DependentHandleAnalysisOptions? DependentHandleAnalysis { get; init; }
    public GCHandleAnalysisOptions? GCHandleAnalysis { get; init; }
    public StaticRootLeakAnalysisOptions? StaticRootLeakAnalysis { get; init; }
    public MemoryAnalysisOptions? MemoryAnalysis { get; init; }
    public ReportOptionsModel? Report { get; init; }

    public int? HighReferenceThreshold { get; init; }
    public int? MaxDuplicateStringLength { get; init; }
    public int? MinDuplicateStringCount { get; init; }
    public int? MaxReferenceAddressesToTrack { get; init; }
    public int? ReferenceChainTopCount { get; init; }
    public int? ReferenceChainMaxPathSearchObjects { get; init; }
    public int? EventLeakMinSubscribers { get; init; }
    public bool? EnableMemoryDiagnostics { get; init; }
    public bool? EnablePerformanceDiagnostics { get; init; }
    public string? ReportFormat { get; init; }
    public string? ReportAudience { get; init; }
    public IndexingOptionsModel? Indexing { get; init; }
    public string? IndexMode { get; init; }
}

internal sealed class AnalyzerOptionsModel
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement> Sections { get; set; } = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
}

internal sealed class ReportOptionsModel
{
    public ReportFormat Format { get; init; } = ReportFormat.Html;
    public ReportAudience Audience { get; init; } = ReportAudience.All;
}

internal sealed class IndexingOptionsModel
{
    public string? Mode { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(CliConfigurationFileModel))]
[JsonSerializable(typeof(AnalyzerOptionsModel))]
[JsonSerializable(typeof(CrashAnalysisOptionsModel))]
[JsonSerializable(typeof(AsyncTaskAnalysisOptions))]
[JsonSerializable(typeof(AsyncStateMachineAnalysisOptions))]
[JsonSerializable(typeof(ArrayAnalysisOptions))]
[JsonSerializable(typeof(BoxingAnalysisOptions))]
[JsonSerializable(typeof(CollectionAnalysisOptions))]
[JsonSerializable(typeof(CollectionAnalysisOptionsModel))]
[JsonSerializable(typeof(StringAnalysisOptions))]
[JsonSerializable(typeof(SegmentAnalysisOptions))]
[JsonSerializable(typeof(AppDomainAnalysisOptions))]
[JsonSerializable(typeof(AllocationPatternAnalysisOptions))]
[JsonSerializable(typeof(ThreadStackClusterAnalysisOptions))]
[JsonSerializable(typeof(LockGraphAnalysisOptions))]
[JsonSerializable(typeof(FinalizableObjectAnalysisOptions))]
[JsonSerializable(typeof(GCGenerationAnalysisOptions))]
[JsonSerializable(typeof(GCRootAnalysisOptions))]
[JsonSerializable(typeof(LohFragmentationAnalysisOptions))]
[JsonSerializable(typeof(SegmentReservationAnalysisOptions))]
[JsonSerializable(typeof(ThreadAnalysisOptions))]
[JsonSerializable(typeof(HangAnalysisOptions))]
[JsonSerializable(typeof(JitAnalysisOptions))]
[JsonSerializable(typeof(WeakReferenceAnalysisOptions))]
[JsonSerializable(typeof(ObjectShapeAnalysisOptions))]
[JsonSerializable(typeof(ModuleAnalysisOptions))]
[JsonSerializable(typeof(DependentHandleAnalysisOptions))]
[JsonSerializable(typeof(GCHandleAnalysisOptions))]
[JsonSerializable(typeof(StaticRootLeakAnalysisOptions))]
[JsonSerializable(typeof(MemoryAnalysisOptions))]
internal partial class CliConfigurationJsonSerializerContext : JsonSerializerContext
{
}
