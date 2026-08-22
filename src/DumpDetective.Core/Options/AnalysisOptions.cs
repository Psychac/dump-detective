namespace DumpDetective.Core.Options;

public sealed record AnalysisOptions
{
    public RetentionOptions MemoryLeak { get; init; } = new();
    public ReferenceChainOptions ReferenceChain { get; init; } = new();
    public EventLeakOptions EventLeak { get; init; } = new();
    public DiagnosticsOptions Diagnostics { get; init; } = new();
    public ExecutionPolicy ExecutionPolicy { get; init; } = ExecutionPolicy.Default;
    public CrashAnalysisOptions Crash { get; init; } = new();
    public AsyncTaskAnalysisOptions AsyncTaskAnalysis { get; init; } = new();
    public AsyncStateMachineAnalysisOptions AsyncStateMachineAnalysis { get; init; } = new();
    public ArrayAnalysisOptions ArrayAnalysis { get; init; } = new();
    public BoxingAnalysisOptions BoxingAnalysis { get; init; } = new();
    public CollectionAnalysisOptions Collection { get; init; } = new();
    public StringAnalysisOptions StringAnalysis { get; init; } = new();
    public HeapTopologyAnalysisOptions HeapTopology { get; init; } = new();
    public AllocationPatternAnalysisOptions AllocationPatternAnalysis { get; init; } = new();
    public ThreadStackClusterAnalysisOptions ThreadStackClusterAnalysis { get; init; } = new();
    public FinalizableObjectAnalysisOptions FinalizableObjectAnalysis { get; init; } = new();
    public GCGenerationAnalysisOptions GCGenerationAnalysis { get; init; } = new();
    public GCRootAnalysisOptions GCRootAnalysis { get; init; } = new();
    public LohFragmentationAnalysisOptions LohFragmentationAnalysis { get; init; } = new();
    public SegmentReservationAnalysisOptions SegmentReservationAnalysis { get; init; } = new();
    public ThreadAnalysisOptions ThreadAnalysis { get; init; } = new();
    public HangAnalysisOptions HangAnalysis { get; init; } = new();
    public JitAnalysisOptions JitAnalysis { get; init; } = new();
    public WeakReferenceAnalysisOptions WeakReferenceAnalysis { get; init; } = new();
    public ModuleAnalysisOptions ModuleAnalysis { get; init; } = new();
    public GCHandleAnalysisOptions GCHandleAnalysis { get; init; } = new();
    public StaticRootLeakAnalysisOptions StaticRootLeakAnalysis { get; init; } = new();
    public MemoryAnalysisOptions MemoryAnalysis { get; init; } = new();

    public bool TryGet<T>(out T? option) where T : class
    {
        System.Reflection.PropertyInfo[] properties = GetType().GetProperties();
        for (int i = 0; i < properties.Length; i++)
        {
            System.Reflection.PropertyInfo property = properties[i];
            if (!typeof(T).IsAssignableFrom(property.PropertyType))
                continue;

            if (property.GetValue(this) is T typed)
            {
                option = typed;
                return true;
            }
        }

        option = null;
        return false;
    }
}