namespace DumpDetective.Sdk.Synthesis;

/// <summary>
/// The closed set of things a claim can say. Closed on purpose: it makes a rule author state what kind of
/// statement they are making, makes claims groupable across domains, and makes prose generation mechanical.
/// See docs/ReportStructure/ReportSystemVision.md § 6.8 and docs/ReportStructure/m1-claims-plan.md (step 3).
/// </summary>
public enum AssertionPredicate
{
    Retains,

    /// <summary>Needs a reference snapshot, so only trend rules can assert it.</summary>
    Grows,

    Blocks,
    Leaks,
    Exceeds,
    Fragments,
    Faults,
    Stalls,
    Duplicates,
    Conflicts,

    /// <summary>Also the stand-in for tool-limitation findings until coverage (M3) takes them over.</summary>
    Missing,

    /// <summary>Positive statement of absence: looked, and found nothing abnormal.</summary>
    Normal,

    /// <summary>Descriptive snapshot of composition; makes no judgment.</summary>
    Composes,

    /// <summary>Concentration; the share is the measure.</summary>
    Dominates,

    /// <summary>A pattern is present, with no threshold to exceed.</summary>
    Exhibits,
}
