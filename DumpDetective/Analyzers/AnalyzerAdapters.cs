using DumpDetective.Utilities;
using DumpDetective.Configuration;
using DumpDetective.Models;

namespace DumpDetective.Analyzers
{
    internal class MemoryAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;
        public string Name => "Memory Analysis";
        public MemoryAnalyzerAdapter(OutputWriter writer) => _writer = writer;
        public AnalyzerExecutionResult Execute(AnalysisContext context)
        {
            var output = new MemoryAnalyzer(_writer).Analyze(context.Heap, context.Cache);
            return new AnalyzerExecutionResult(output.Findings, output.DomainResult);
        }
    }

    internal class GCGenerationAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;
        public string Name => "GC Generation Analysis";
        public GCGenerationAnalyzerAdapter(OutputWriter writer) => _writer = writer;
        public AnalyzerExecutionResult Execute(AnalysisContext context)
        {
            var output = new GCGenerationAnalyzer(_writer).Analyze(context.Heap, context.Cache);
            return new AnalyzerExecutionResult(output.Findings, output.DomainResult);
        }
    }

    internal class ModuleAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;
        public string Name => "Module Analysis";
        public ModuleAnalyzerAdapter(OutputWriter writer) => _writer = writer;
        public AnalyzerExecutionResult Execute(AnalysisContext context)
        {
            var output = new ModuleAnalyzer(_writer).Analyze(context.Runtime);
            return new AnalyzerExecutionResult(output.Findings, output.DomainResult);
        }
    }

    internal class CrashAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;
        public string Name => "Crash Analysis";
        public CrashAnalyzerAdapter(OutputWriter writer) => _writer = writer;
        public AnalyzerExecutionResult Execute(AnalysisContext context)
        {
            var output = new CrashAnalyzer(_writer).Analyze(context.Runtime, context.Heap);
            return new AnalyzerExecutionResult(output.Findings, output.DomainResult);
        }
    }

    internal class HangAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;
        public string Name => "Hang Analysis";
        public HangAnalyzerAdapter(OutputWriter writer) => _writer = writer;
        public AnalyzerExecutionResult Execute(AnalysisContext context)
        {
            var output = new HangAnalyzer(_writer).Analyze(context.Runtime, context.Heap);
            return new AnalyzerExecutionResult(output.Findings, output.DomainResult);
        }
    }

    internal class MemoryLeakAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;
        private readonly AnalysisConfiguration _config;
        public string Name => "Memory Leak Analysis";
        public MemoryLeakAnalyzerAdapter(OutputWriter writer, AnalysisConfiguration config)
        {
            _writer = writer;
            _config = config;
        }
        public AnalyzerExecutionResult Execute(AnalysisContext context)
        {
            var output = new MemoryLeakAnalyzer(_writer, _config).Analyze(context.Heap, context.Runtime);
            return new AnalyzerExecutionResult(output.Findings, output.DomainResult);
        }
    }

    internal class CollectionAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;
        public string Name => "Collection Analysis";
        public CollectionAnalyzerAdapter(OutputWriter writer) => _writer = writer;
        public AnalyzerExecutionResult Execute(AnalysisContext context)
        {
            var output = new CollectionAnalyzer(_writer).Analyze(context.Heap);
            return new AnalyzerExecutionResult(output.Findings, output.DomainResult);
        }
    }

    internal class StaticRootLeakDetectorAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;
        public string Name => "Static Root Leak Detection";
        public StaticRootLeakDetectorAdapter(OutputWriter writer) => _writer = writer;
        public AnalyzerExecutionResult Execute(AnalysisContext context)
        {
            var output = new StaticRootLeakDetector(_writer).Analyze(context.Heap, context.Cache);
            return new AnalyzerExecutionResult(output.Findings, output.DomainResult);
        }
    }

    internal class ReferenceChainAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;
        private readonly AnalysisConfiguration _config;
        public string Name => "Reference Chain Analysis";
        public ReferenceChainAnalyzerAdapter(OutputWriter writer, AnalysisConfiguration config)
        {
            _writer = writer;
            _config = config;
        }
        public AnalyzerExecutionResult Execute(AnalysisContext context)
        {
            var output = new ReferenceChainAnalyzer(_writer, _config).AnalyzeTopTypes(context.Heap, context.Cache);
            return new AnalyzerExecutionResult(output.Findings, output.DomainResult);
        }
    }

    internal class ThreadAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;
        public string Name => "Thread Analysis";
        public ThreadAnalyzerAdapter(OutputWriter writer) => _writer = writer;
        public AnalyzerExecutionResult Execute(AnalysisContext context)
        {
            var output = new ThreadAnalyzer(_writer).Analyze(context.Runtime);
            return new AnalyzerExecutionResult(output.Findings, output.DomainResult);
        }
    }

    internal class EventLeakAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;
        private readonly AnalysisConfiguration _config;
        public string Name => "Event Leak Analysis";
        public EventLeakAnalyzerAdapter(OutputWriter writer, AnalysisConfiguration config)
        {
            _writer = writer;
            _config = config;
        }
        public AnalyzerExecutionResult Execute(AnalysisContext context)
        {
            var output = new EventLeakAnalyzer(_writer, _config).Analyze(context.Heap);
            return new AnalyzerExecutionResult(output.Findings, output.DomainResult);
        }
    }

    internal class GCHandleAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;
        public string Name => "GC Handle Analysis";
        public GCHandleAnalyzerAdapter(OutputWriter writer) => _writer = writer;
        public AnalyzerExecutionResult Execute(AnalysisContext context)
        {
            var output = new GCHandleAnalyzer(_writer).Analyze(context.Runtime);
            return new AnalyzerExecutionResult(output.Findings, output.DomainResult);
        }
    }

    internal class LohFragmentationAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;
        public string Name => "LOH Fragmentation Analysis";
        public LohFragmentationAnalyzerAdapter(OutputWriter writer) => _writer = writer;
        public AnalyzerExecutionResult Execute(AnalysisContext context)
        {
            var output = new LohFragmentationAnalyzer(_writer).Analyze(context.Heap);
            return new AnalyzerExecutionResult(output.Findings, output.DomainResult);
        }
    }

    internal class DependentHandleAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;
        public string Name => "Dependent Handle Analysis";
        public DependentHandleAnalyzerAdapter(OutputWriter writer) => _writer = writer;
        public AnalyzerExecutionResult Execute(AnalysisContext context)
        {
            var output = new DependentHandleAnalyzer(_writer).Analyze(context.Runtime);
            return new AnalyzerExecutionResult(output.Findings, output.DomainResult);
        }
    }

    internal class ThreadStackClusterAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;

        public string Name => "Thread Stack Signature Clustering";

        public ThreadStackClusterAnalyzerAdapter(OutputWriter writer)
        {
            _writer = writer;
        }

        public AnalyzerExecutionResult Execute(AnalysisContext context)
        {
            var output = new ThreadStackClusterAnalyzer(_writer).Analyze(context.Runtime);
            return new AnalyzerExecutionResult(output.Findings, output.DomainResult);
        }
    }
}
