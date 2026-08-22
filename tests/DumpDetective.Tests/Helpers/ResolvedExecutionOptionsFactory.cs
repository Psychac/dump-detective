using DumpDetective.Cli.Models;
using DumpDetective.Core.Options;
using DumpDetective.Analysis.Indexing;

internal static class ResolvedExecutionOptionsFactory
{
    public static ResolvedExecutionOptions Create(string outputPath)
    {
        string dumpPath = Path.ChangeExtension(outputPath, ".dmp");
        return new ResolvedExecutionOptions(
            DumpPath: dumpPath,
            OutputPath: outputPath,
            BaselineDumpPath: null,
            TrendDumpPaths: null,
            MemoryLeak: new RetentionOptions(),
            ReferenceChain: new DumpDetective.Core.Options.ReferenceChainOptions(),
            EventLeak: new DumpDetective.Core.Options.EventLeakOptions(),
            Diagnostics: new DumpDetective.Core.Options.DiagnosticsOptions(),
            Report: new DumpDetective.Core.Options.ReportOptions(),
            Crash: new DumpDetective.Core.Options.CrashAnalysisOptions(),
            AsyncTaskAnalysis: new DumpDetective.Core.Options.AsyncTaskAnalysisOptions(),
            AsyncStateMachineAnalysis: new DumpDetective.Core.Options.AsyncStateMachineAnalysisOptions(),
            ArrayAnalysis: new DumpDetective.Core.Options.ArrayAnalysisOptions(),
            BoxingAnalysis: new DumpDetective.Core.Options.BoxingAnalysisOptions(),
            Collection: new DumpDetective.Core.Options.CollectionAnalysisOptions(),
            StringAnalysis: new DumpDetective.Core.Options.StringAnalysisOptions(),
            HeapTopology: new DumpDetective.Core.Options.HeapTopologyAnalysisOptions(),
            AllocationPatternAnalysis: new DumpDetective.Core.Options.AllocationPatternAnalysisOptions(),
            ThreadStackClusterAnalysis: new DumpDetective.Core.Options.ThreadStackClusterAnalysisOptions(),
            LockGraphAnalysis: new DumpDetective.Core.Options.LockGraphAnalysisOptions(),
            FinalizableObjectAnalysis: new DumpDetective.Core.Options.FinalizableObjectAnalysisOptions(),
            GCGenerationAnalysis: new DumpDetective.Core.Options.GCGenerationAnalysisOptions(),
            GCRootAnalysis: new DumpDetective.Core.Options.GCRootAnalysisOptions(),
            LohFragmentationAnalysis: new DumpDetective.Core.Options.LohFragmentationAnalysisOptions(),
            SegmentReservationAnalysis: new DumpDetective.Core.Options.SegmentReservationAnalysisOptions(),
            ThreadAnalysis: new DumpDetective.Core.Options.ThreadAnalysisOptions(),
            HangAnalysis: new DumpDetective.Core.Options.HangAnalysisOptions(),
            JitAnalysis: new DumpDetective.Core.Options.JitAnalysisOptions(),
            WeakReferenceAnalysis: new DumpDetective.Core.Options.WeakReferenceAnalysisOptions(),
            ModuleAnalysis: new DumpDetective.Core.Options.ModuleAnalysisOptions(),
            GCHandleAnalysis: new DumpDetective.Core.Options.GCHandleAnalysisOptions(),
            StaticRootLeakAnalysis: new DumpDetective.Core.Options.StaticRootLeakAnalysisOptions(),
            MemoryAnalysis: new DumpDetective.Core.Options.MemoryAnalysisOptions(),
            ConfigPath: null,
            UsedConfigFile: false,
            IncludeAnalyzers: System.Array.Empty<string>(),
            ExcludeAnalyzers: System.Array.Empty<string>(),
            DiagnosticMode: false);
    }
}
