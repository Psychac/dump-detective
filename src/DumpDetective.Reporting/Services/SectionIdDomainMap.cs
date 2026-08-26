namespace DumpDetective.Reporting.Services;

/// <summary>
/// Static map from analyzer name to its report domain string and stable section-ID prefix.
/// Used by <see cref="ReportSerializer"/> to annotate sections after they are built and by
/// <see cref="HealthScorecardBuilder"/> to group findings by domain.
/// </summary>
internal static class SectionIdDomainMap
{
    // Key: AnalyzerName (case-insensitive match via caller). Value: (Domain, SectionId)
    private static readonly Dictionary<string, (string Domain, string SectionId)> _map = new(StringComparer.OrdinalIgnoreCase)
    {
        // Domain A — Memory & Leaks
        ["LeakCandidateAnalyzer"]      = ("Leaks",      "A1"),
        ["Leak Candidate Analysis"]    = ("Leaks",      "A1"),
        ["MemoryAnalyzer"]             = ("Memory",     "A2"),
        ["Memory Analysis"]            = ("Memory",     "A2"),
        ["DominatorAnalyzer"]          = ("Memory",     "A3"),
        ["Dominator Analysis"]         = ("Memory",     "A3"),
        ["GCRootAnalyzer"]             = ("Memory",     "A5"),
        ["GC Root Analysis"]           = ("Memory",     "A5"),
        ["StaticRootLeakDetector"]     = ("Memory",     "A6"),
        ["Static Root Leak Detection"] = ("Memory",     "A6"),
        ["StringAnalyzer"]             = ("Memory",     "A7"),
        ["String Analysis"]            = ("Memory",     "A7"),

        // Domain B — GC Health
        ["GCGenerationAnalyzer"]       = ("GC",         "B1"),
        ["GC Generation Analysis"]     = ("GC",         "B1"),
        ["AllocationPatternAnalyzer"]  = ("GC",         "B2"),
        ["Allocation Pattern Analysis"] = ("GC",        "B2"),
        ["HeapTopologyAnalyzer"]        = ("GC",         "B3"),
        ["Heap Topology"]              = ("GC",         "B3"),
        ["LohFragmentationAnalyzer"]   = ("GC",         "B4"),
        ["LOH Fragmentation Analysis"]  = ("GC",         "B4"),
        ["LOH & POH Fragmentation Analysis"]  = ("GC",   "B4"),
        ["SegmentReservationAnalyzer"] = ("GC",         "B5"),
        ["Segment Reservation Analysis"] = ("GC",       "B5"),
        ["FinalizableObjectAnalyzer"]  = ("GC",         "B6"),
        ["Finalizable Object Analysis"] = ("GC",        "B6"),
        ["GCHandleAnalyzer"]           = ("GC",         "B7"),
        ["GC Handle Analysis"]         = ("GC",         "B7"),
        ["WeakReferenceAnalyzer"]      = ("GC",         "B7"),
        ["Weak Reference Analysis"]    = ("GC",         "B7"),

        // Domain C — Type System
        ["ObjectShapeAnalyzer"]        = ("TypeSystem", "C2"),
        ["Object Shape Analysis"]      = ("TypeSystem", "C2"),
        ["CollectionAnalyzer"]         = ("TypeSystem", "C3"),
        ["Collection Analysis"]        = ("TypeSystem", "C3"),
        ["ArrayAnalyzer"]              = ("TypeSystem", "C4"),
        ["Array Analysis"]             = ("TypeSystem", "C4"),
        ["BoxingAnalyzer"]             = ("TypeSystem", "C5"),
        ["Boxing Analysis"]            = ("TypeSystem", "C5"),

        // Domain D — Threads & Concurrency
        ["ThreadAnalyzer"]             = ("Threads",    "D1"),
        ["Thread Analysis"]            = ("Threads",    "D1"),
        ["HangAnalyzer"]               = ("Threads",    "D2"),
        ["Hang Analysis"]              = ("Threads",    "D2"),
        ["LockGraphAnalyzer"]          = ("Threads",    "D3"),
        ["Lock Graph Analysis"]        = ("Threads",    "D3"),
        ["ThreadStackClusterAnalyzer"] = ("Threads",    "D3"),
        ["Thread Stack Signature Clustering"] = ("Threads", "D3"),
        ["EventLeakAnalyzer"]          = ("Threads",    "D4"),
        ["Event Leak Analysis"]        = ("Threads",    "D4"),

        // Domain E — Async & Tasks
        ["AsyncTaskAnalyzer"]          = ("Async",      "E1"),
        ["Async Task Analysis"]        = ("Async",      "E1"),
        ["AsyncStateMachineAnalyzer"]  = ("Async",      "E2"),
        ["Async State Machine Analysis"] = ("Async",    "E2"),

        // Domain F — Exceptions
        ["CrashAnalyzer"]              = ("Exceptions", "F1"),
        ["Crash Analysis"]             = ("Exceptions", "F1"),
        ["Exception Analysis"]         = ("Exceptions", "F1"),  // ExceptionAnalysisSectionBuilder display name

        // Domain G — Runtime Infrastructure
        ["ModuleAnalyzer"]             = ("Runtime",    "G1"),
        ["Module Analysis"]            = ("Runtime",    "G1"),
        ["Modules & Assemblies"]       = ("Runtime",    "G1"),   // ModuleSectionBuilder display title
        ["JitAnalyzer"]                = ("Runtime",    "G3"),
        ["JIT Analysis"]               = ("Runtime",    "G3"),

        // IReportSectionBuilder display-title aliases (for sections where DisplayTitle is used as AnalyzerName in the AnalyzerDetailSection)
        ["GC Root Intelligence"]       = ("Memory",     "A5"),   // GCRootIntelligenceSectionBuilder display title
        ["Memory Overview"]            = ("Memory",     "A2"),   // MemoryAnalysisSectionBuilder display title
        ["Generation Pressure"]        = ("GC",         "B1"),   // GCPressureSectionBuilder display title
        ["Heap Topology"]              = ("GC",         "B3"),   // HeapTopologySectionBuilder display title
        ["Task Overview"]              = ("Async",      "E1"),   // AsyncAnalysisSectionBuilder display title

        // Individual B7/B8 — split builders
        ["GCHandleAnalyzer"]            = ("GC",      "B7"),
        ["GC Handle Analysis"]          = ("GC",      "B7"),
        ["GC Handles"]                  = ("GC",      "B7"),
        ["WeakReferenceAnalyzer"]       = ("GC",      "B8"),
        ["Weak Reference Analysis"]     = ("GC",      "B8"),

        // Supplementary section — ReferenceChainSectionBuilder (supporting analyzer, no fixed spec section)
        ["Reference Chain Analysis"]   = ("Memory",     ""),

        // Domain H — Infrastructure / Network
        ["DbConnectionAnalyzer"]       = ("Infrastructure", "H1"),
        ["DB Connection Analysis"]     = ("Infrastructure", "H1"),
        ["WcfChannelAnalyzer"]         = ("Infrastructure", "H2"),
        ["WCF Channel Analysis"]       = ("Infrastructure", "H2"),
        ["HttpObjectAnalyzer"]         = ("Infrastructure", "H3"),
        ["HTTP Object Analysis"]       = ("Infrastructure", "H3"),
        ["TimerLeakAnalyzer"]         = ("Infrastructure", "H4"),
        ["Timer Leak Analysis"]       = ("Infrastructure", "H4"),

        // C1 (TypeTable) is built by TypeSystemSectionBuilder as an IReportSectionBuilder — no analyzerName entry needed.
    };

    /// <summary>
    /// Tries to resolve a domain and section-ID for the given analyzer name.
    /// Returns false when the analyzer is not in the map (e.g. pipeline or cross-cutting builders).
    /// </summary>
    public static bool TryGet(string analyzerName, out string domain, out string sectionId)
    {
        if (_map.TryGetValue(analyzerName, out var entry))
        {
            domain    = entry.Domain;
            sectionId = entry.SectionId;
            return true;
        }
        domain    = "";
        sectionId = "";
        return false;
    }

    /// <summary>Returns the domain string for an analyzer name, or empty string when not mapped.</summary>
    public static string GetDomain(string analyzerName)
        => _map.TryGetValue(analyzerName, out var entry) ? entry.Domain : "";

    /// <summary>Returns all distinct domain strings in priority render order.</summary>
    public static IReadOnlyList<string> DomainsInOrder { get; } =
    [
        "Leaks", "Memory", "GC", "TypeSystem", "Threads", "Async", "Exceptions", "Runtime", "Infrastructure"
    ];
}
