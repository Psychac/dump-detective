using DumpDetective.Utilities;
using DumpDetective.Configuration;

namespace DumpDetective.Analyzers
{
    internal class MemoryAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;

        public string Name => "Memory Analysis";

        public MemoryAnalyzerAdapter(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Execute(AnalysisContext context)
        {
            new MemoryAnalyzer(_writer).Analyze(context.Heap);
        }
    }

    internal class GCGenerationAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;

        public string Name => "GC Generation Analysis";

        public GCGenerationAnalyzerAdapter(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Execute(AnalysisContext context)
        {
            new GCGenerationAnalyzer(_writer).Analyze(context.Heap);
        }
    }

    internal class ModuleAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;

        public string Name => "Module Analysis";

        public ModuleAnalyzerAdapter(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Execute(AnalysisContext context)
        {
            new ModuleAnalyzer(_writer).Analyze(context.Runtime);
        }
    }

    internal class CrashAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;

        public string Name => "Crash Analysis";

        public CrashAnalyzerAdapter(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Execute(AnalysisContext context)
        {
            new CrashAnalyzer(_writer).Analyze(context.Runtime, context.Heap);
        }
    }

    internal class HangAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;

        public string Name => "Hang Analysis";

        public HangAnalyzerAdapter(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Execute(AnalysisContext context)
        {
            new HangAnalyzer(_writer).Analyze(context.Runtime, context.Heap);
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

        public void Execute(AnalysisContext context)
        {
            new MemoryLeakAnalyzer(_writer, _config).Analyze(context.Heap, context.Runtime);
        }
    }

    internal class CollectionAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;

        public string Name => "Collection Analysis";

        public CollectionAnalyzerAdapter(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Execute(AnalysisContext context)
        {
            new CollectionAnalyzer(_writer).Analyze(context.Heap);
        }
    }

    internal class StaticRootLeakDetectorAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;

        public string Name => "Static Root Leak Detection";

        public StaticRootLeakDetectorAdapter(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Execute(AnalysisContext context)
        {
            new StaticRootLeakDetector(_writer).Analyze(context.Heap);
        }
    }

    internal class EventHandlerLeakDetectorAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;

        public string Name => "Event Handler Leak Detection";

        public EventHandlerLeakDetectorAdapter(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Execute(AnalysisContext context)
        {
            new EventHandlerLeakDetector(_writer).Analyze(context.Heap);
        }
    }

    internal class ReferenceChainAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;
        private readonly int _topCount;

        public string Name => "Reference Chain Analysis";

        public ReferenceChainAnalyzerAdapter(OutputWriter writer, int topCount = 5)
        {
            _writer = writer;
            _topCount = topCount;
        }

        public void Execute(AnalysisContext context)
        {
            new ReferenceChainAnalyzer(_writer).AnalyzeTopTypes(context.Heap, context.Cache, _topCount);
        }
    }

    internal class ThreadAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;

        public string Name => "Thread Analysis";

        public ThreadAnalyzerAdapter(OutputWriter writer)
        {
            _writer = writer;
        }

        public void Execute(AnalysisContext context)
        {
            new ThreadAnalyzer(_writer).Analyze(context.Runtime);
        }
    }

    internal class EventLeakAnalyzerAdapter : IAnalyzer
    {
        private readonly OutputWriter _writer;
        private readonly int _minSubscribers;

        public string Name => "Event Leak Analysis";

        public EventLeakAnalyzerAdapter(OutputWriter writer, int minSubscribers = 0)
        {
            _writer = writer;
            _minSubscribers = minSubscribers;
        }

        public void Execute(AnalysisContext context)
        {
            new EventLeakAnalyzer(_writer).Analyze(context.Heap, _minSubscribers);
        }
    }
}
