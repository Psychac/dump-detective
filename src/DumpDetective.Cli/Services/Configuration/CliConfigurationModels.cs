using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using DumpDetective.Core.Options;
using DumpDetective.Core.Configuration;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Enums;

namespace DumpDetective.Cli.Services.Configuration;

internal sealed class CliConfigurationFileModel
{
    public string? DumpPath { get; init; }
    public string? BaselineDumpPath { get; init; }
    public List<string>? TrendDumpPaths { get; init; }
    public string? Profile { get; init; }
    public AnalyzerOptionsModel? Analyzers { get; init; }
    public ExecutionPolicyModel? ExecutionPolicy { get; init; }

    public RetentionOptions? MemoryLeak { get; init; }
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
    public HeapTopologyAnalysisOptions? HeapTopology { get; init; }
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

    public bool? EnableMemoryDiagnostics { get; init; }
    public bool? EnablePerformanceDiagnostics { get; init; }
    public string? ReportFormat { get; init; }
    public string? ReportStyleVersion { get; init; }
    public IndexingOptionsModel? Indexing { get; init; }
    public string? IndexMode { get; init; }
    public List<string>? IncludeAnalyzers { get; init; }
    public List<string>? ExcludeAnalyzers { get; init; }
    public string? CacheDirectory { get; init; }
}

internal sealed class AnalyzerOptionsModel
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement> Sections { get; set; } = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
}

internal sealed class ReportOptionsModel
{
    public ReportFormat Format { get; init; } = ReportFormat.Html;
    public ReportStyleVersion StyleVersion { get; init; } = ReportStyleVersion.V1;
    public bool PreRender { get; init; } = false;
    public bool SeparateJson { get; init; } = false;
}

internal sealed class IndexingOptionsModel
{
    public string? Mode { get; init; }
}

internal sealed class ExecutionPolicyModel
{
    public int? MaxLeakScanObjects { get; init; }
    public int? MaxReferenceAddresses { get; init; }
    public int? ReferenceChainMaxPathDepth { get; init; }
    public int? ReferenceChainFastModeMaxDepth { get; init; }
    public int? ReferenceChainMaxPathSearchObjects { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(CliConfigurationFileModel))]
[JsonSerializable(typeof(AnalyzerOptionsModel))]
[JsonSerializable(typeof(ExecutionPolicyModel))]
[JsonSerializable(typeof(CrashAnalysisOptionsModel))]
[JsonSerializable(typeof(AsyncTaskAnalysisOptions))]
[JsonSerializable(typeof(AsyncStateMachineAnalysisOptions))]
[JsonSerializable(typeof(ArrayAnalysisOptions))]
[JsonSerializable(typeof(BoxingAnalysisOptions))]
[JsonSerializable(typeof(CollectionAnalysisOptions))]
[JsonSerializable(typeof(CollectionAnalysisOptionsModel))]
[JsonSerializable(typeof(StringAnalysisOptions))]
[JsonSerializable(typeof(HeapTopologyAnalysisOptions))]
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
[JsonSerializable(typeof(DiagnosticsOptions))]
internal partial class CliConfigurationJsonSerializerContext : JsonSerializerContext
{
}
