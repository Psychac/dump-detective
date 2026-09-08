# DumpDetective Analyzer Audit Protocol

> **Purpose**
>
> Audit a single analyzer to determine how effectively it solves its
> problem domain, how useful its diagnostics are, and how it can evolve
> into the best possible implementation.
>
> Use **Phase 0 as architectural context rather than architectural
> constraint**. Phase 0 describes the current platform architecture; it
> does not limit recommendations. Reviewers are encouraged to propose
> improvements to the analyzer and, where justified, evolution of the
> overall platform.

------------------------------------------------------------------------

# Reviewer Mindset

Review as:

-   Principal .NET Runtime Engineer
-   ClrMD Expert
-   CLR & GC Specialist
-   Memory Diagnostics Engineer
-   Production SRE investigating a live incident
-   Performance Engineer
-   Software Architect

Every conclusion must be supported by implementation evidence.

Challenge assumptions where justified, including analyzer boundaries or
platform architecture when compelling evidence exists. Avoid speculative conclusions.

------------------------------------------------------------------------

# Inputs

Review all components contributing to the analyzer:

-   Analyzer implementation
-   Domain models
-   Options
-   Formatters
-   Helpers
-   Shared infrastructure consumed
-   Tests
-   Phase 0 outputs (context only)

------------------------------------------------------------------------

# Audit Area 1 --- Role & Opportunity Assessment

## Objective

Understand the analyzer's current role, evaluate how well it performs
that role, and identify opportunities to naturally expand or improve its
capabilities.

## Evaluate

-   Current role and cohesion
-   Coverage of the problem domain
-   Missing functionality
-   Unexpected functionality
-   Adjacent capabilities
-   Shared infrastructure opportunities
-   Platform evolution opportunities

## Questions

-   What problem does this analyzer solve?
-   How well does it solve it?
-   What valuable capabilities naturally belong here?
-   Should its scope evolve?
-   Does the implementation reveal opportunities to improve the overall
    platform?

## Deliverables

-   Current role assessment
-   Coverage gaps
-   Expansion opportunities
-   Architectural observations

------------------------------------------------------------------------

# Audit Area 2 --- Diagnostic & Report Quality

## Objective

Determine whether the analyzer produces reports that enable engineers to
confidently diagnose problems.

## Evaluate

-   Diagnostic clarity
-   Actionability
-   Evidence quality
-   Statistical usefulness
-   Signal-to-noise ratio
-   Report structure
-   Prioritization
-   Readability
-   Missing context
-   Missing evidence
-   Missing summaries

## Questions

-   Can an engineer determine what happened?
-   Can they determine why it happened?
-   Are findings actionable?
-   Are statistics supporting the diagnostics rather than replacing
    them?
-   What information is still missing?

## Deliverables

-   Strengths
-   Weaknesses
-   Missing diagnostics
-   Missing statistics
-   Report improvements

------------------------------------------------------------------------

# Audit Area 3 --- ClrMD & Platform Utilization

## Objective

Determine whether the analyzer makes optimal use of ClrMD and existing
DumpDetective infrastructure.

## Evaluate

-   ClrMD APIs
-   Runtime semantics
-   GC semantics
-   Existing indexes
-   Shared caches
-   Shared helpers
-   Traversal utilities
-   Evidence builders

## Questions

-   Is ClrMD being used optimally?
-   Are existing indexes fully utilized?
-   Is functionality unnecessarily duplicated?
-   What runtime information is ignored?
-   Would existing or new indexes materially improve the analyzer?

## Deliverables

-   ClrMD recommendations
-   Infrastructure recommendations
-   Index recommendations
-   Missing runtime information

------------------------------------------------------------------------

# Audit Area 4 --- Diagnostic Opportunity Analysis

## Objective

Identify valuable information that exists in the dump but is not
currently extracted.

Think beyond the current implementation.

## Evaluate

-   Diagnostics
-   Statistics
-   Correlations
-   Rankings
-   Ownership
-   Retention evidence
-   Aggregations
-   Summaries
-   Heuristics
-   Confidence improvements
-   Investigation workflows
-   Visualizations

## Questions

-   What additional information could be extracted?
-   What would significantly improve investigations?
-   What would increase confidence?
-   If engineering time were unlimited, what would the ideal analyzer
    provide?

## Deliverables

-   High-value diagnostics
-   High-value statistics
-   Evidence recommendations
-   Priority-ranked opportunities

------------------------------------------------------------------------

# Audit Area 5 --- Performance, Memory & Scalability

## Objective

Determine whether the analyzer scales efficiently to production-sized
dumps.

## Evaluate

-   Heap scans
-   Root traversals
-   Streaming
-   Materialization
-   Temporary allocations
-   Existing index usage
-   Candidate indexes
-   Duplicate work
-   Complexity
-   Progress reporting
-   Cancellation

Assess expected behavior on dumps from **1 GB to 100 GB**.

## Questions

-   Can existing indexes improve performance?
-   Should new indexes exist?
-   What is the scalability bottleneck?

## Deliverables

-   Performance assessment
-   Memory assessment
-   Scalability assessment
-   Optimization roadmap
-   Index recommendations

------------------------------------------------------------------------

# Audit Area 6 --- Correctness & Confidence

## Objective

Determine whether every conclusion produced by the analyzer is
technically defensible.

## Evaluate

-   Assumptions
-   Evidence quality
-   False positives
-   False negatives
-   Edge cases
-   Confidence

## Questions

-   Can every finding be defended?
-   Where could incorrect conclusions occur?
-   Which edge cases are unsupported?

## Deliverables

-   Confidence assessment
-   Risks
-   Correctness improvements

------------------------------------------------------------------------

# Audit Area 7 --- Industry Benchmark

## Objective

Compare the analyzer against leading .NET memory diagnostics tools.

## Evaluate

-   WinDbg + SOS
-   PerfView
-   Visual Studio Memory Usage
-   JetBrains dotMemory

Focus on engineering value rather than feature parity.

## Questions

-   What capabilities are missing?
-   What workflows are stronger elsewhere?
-   Which ideas would materially improve DumpDetective?

## Deliverables

-   Benchmark observations
-   Competitive opportunities
-   High-value feature recommendations

------------------------------------------------------------------------

# Recommendation Classification

Every recommendation should be classified as one of the following.

## Improvement

Enhances the existing analyzer.

Examples:

-   Better diagnostics
-   Better evidence
-   Better statistics
-   Better ClrMD usage
-   Better performance
-   Better reports

## Evolution

Improves the overall platform.

Examples:

-   New shared infrastructure
-   New index
-   New analyzer
-   Analyzer merge or split
-   Platform capability expansion
-   New investigation workflow

------------------------------------------------------------------------

# Final Executive Summary

Provide:

## Overall Assessment

-   Overall score (0--100)
-   Production readiness
-   Major strengths
-   Major weaknesses

## Priority Roadmap

Categorize recommendations:

-   P0 -- Critical
-   P1 -- High
-   P2 -- Medium
-   P3 -- Low

For every recommendation include:

-   Expected impact
-   Difficulty
-   Confidence
-   Classification (Improvement or Evolution)

## Final Verdict

Answer:

1.  Is the analyzer production-ready?
2.  What are its highest-impact improvements?
3.  What opportunities exist to evolve the platform?
4.  Which recommendations provide the highest engineering return?

Support every conclusion with concrete implementation evidence.
