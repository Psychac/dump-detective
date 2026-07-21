# DumpDetective Analyzer Evaluation Protocol

> **Purpose**
>
> This document defines the engineering review standard for every
> DumpDetective analyzer. The objective is not merely to review code,
> but to determine whether an analyzer is production-ready for
> investigating real-world .NET memory dumps.

------------------------------------------------------------------------

# Reviewer Mindset

Review as all of the following simultaneously:

1.  Principal .NET Runtime Engineer
2.  CLR / GC / ClrMD Expert
3.  Production SRE investigating a live incident
4.  Memory Leak Investigator
5.  Software Architect
6.  Performance Engineer
7.  Product Reviewer evaluating usefulness to customers

Do **not** assume the existing implementation, architecture, or analyzer
boundaries are correct.

Challenge assumptions.

------------------------------------------------------------------------

# Primary Objectives

Evaluate:

-   Correctness
-   Feature completeness
-   Diagnostic quality
-   Statistical quality
-   Evidence quality
-   Performance
-   Memory efficiency
-   Scalability (1--100 GB dumps)
-   Production usefulness
-   Report usefulness
-   Architecture
-   Maintainability
-   Testability

Suggestions for redesign are encouraged whenever they materially improve
the analyzer.

------------------------------------------------------------------------

# Deliverables

For every analyzer produce the following.

## 1. Executive Scorecard (0--100)

Score and explain:

-   Correctness
-   Diagnostic Quality
-   Statistical Coverage
-   Evidence Quality
-   Leak Detection Capability
-   Actionability
-   Performance
-   Memory Efficiency
-   Scalability
-   Architecture
-   Maintainability
-   Testability
-   ClrMD Usage
-   Report Quality
-   Production Readiness

Provide:

-   Overall score
-   Strengths
-   Weaknesses
-   Top 5 improvements

------------------------------------------------------------------------

## 2. Purpose Validation

Determine whether the analyzer solves a single cohesive problem.

Review:

-   Scope
-   Responsibilities
-   Hidden assumptions
-   Missing scenarios
-   Mixed concerns

------------------------------------------------------------------------

## 3. Analyzer Boundary Review

For every responsibility ask:

-   Does it belong here?
-   Should another analyzer own it?
-   Should it become shared infrastructure?
-   Should it be split?
-   Should it be merged?
-   Should this analyzer exist at all?

Identify capabilities that currently have **no clear owner** anywhere in
DumpDetective.

------------------------------------------------------------------------

## 4. Diagnostic Inventory

Catalogue every diagnostic.

Classify each as:

-   Critical
-   High Value
-   Useful
-   Weak
-   Misleading
-   Redundant
-   Missing evidence
-   Missing context

Recommend additions, removals, or redesigns.

------------------------------------------------------------------------

## 5. Statistics Inventory

Classify every statistic as:

-   Health Metric
-   Leak Indicator
-   Trend Metric
-   Capacity Metric
-   Distribution Metric
-   Performance Metric
-   Informational

Evaluate:

-   Actionability
-   Accuracy
-   Business value
-   Production usefulness

Recommend missing metrics.

------------------------------------------------------------------------

## 6. Missing Diagnostics

Think like an experienced dump analyst.

What important questions cannot currently be answered?

Rank recommendations:

-   Critical
-   High
-   Medium
-   Low

------------------------------------------------------------------------

## 7. Missing Statistics

Recommend additional:

-   Ratios
-   Percentiles
-   Ownership metrics
-   Retention metrics
-   Distributions
-   Concentration metrics
-   Diversity metrics
-   Historical comparison opportunities

------------------------------------------------------------------------

## 8. Evidence Review

Every finding should be evaluated for supporting evidence.

Consider:

-   Object addresses
-   Types
-   Counts
-   Sizes
-   Roots
-   Retention paths
-   Owners
-   Threads
-   Stack traces
-   Samples
-   Confidence
-   Limitations
-   False-positive explanation

------------------------------------------------------------------------

## 9. Leak Detection Accuracy

Evaluate:

-   False positives
-   False negatives
-   Weak heuristics
-   Missing heuristics
-   Edge cases
-   Confidence

------------------------------------------------------------------------

## 10. Report Review

Pretend you are diagnosing a production outage.

Determine whether the report answers:

-   What happened?
-   Why?
-   What is retaining memory?
-   What should be fixed?
-   How severe is it?
-   How confident is the analyzer?

Evaluate readability, prioritization, and signal-to-noise ratio.

------------------------------------------------------------------------

## 11. Architecture Review

Review:

-   Single Responsibility
-   SOLID
-   Layering
-   Coupling
-   Domain models
-   Extensibility
-   Immutability
-   Simplicity

------------------------------------------------------------------------

## 12. ClrMD Review

Validate API usage.

Look for:

-   Correct heap enumeration
-   Root usage
-   Object access
-   Field access
-   Thread analysis
-   Handle analysis
-   Module usage
-   Runtime version assumptions
-   Repeated heap scans
-   API misuse

------------------------------------------------------------------------

## 13. Performance Review

Evaluate:

-   Algorithmic complexity
-   Heap scan count
-   LINQ allocations
-   Boxing
-   Sorting
-   Grouping
-   Temporary collections
-   Dictionary growth
-   Duplicate work
-   Cancellation
-   Progress reporting

------------------------------------------------------------------------

## 14. Memory Review

Review:

-   Streaming
-   Materialization
-   Cache usage
-   Temporary allocations
-   Peak memory
-   Large arrays
-   Index usage

Determine suitability for very large dumps.

------------------------------------------------------------------------

## 15. Scalability Review

Assess expected behaviour on:

-   1 GB
-   5 GB
-   10 GB
-   25 GB
-   50 GB
-   100 GB

Identify bottlenecks and scaling limits.

------------------------------------------------------------------------

## 16. Formatter Review

Evaluate whether the report:

-   Surfaces important findings first
-   Avoids noise
-   Highlights actionable information
-   Is skimmable
-   Supports production debugging

------------------------------------------------------------------------

## 17. Domain Model Review

Review:

-   Naming
-   Missing fields
-   Redundant fields
-   Memory footprint
-   Serialization friendliness
-   Extensibility
-   Evidence preservation

------------------------------------------------------------------------

## 18. Test Review

Evaluate coverage for:

-   Happy path
-   Edge cases
-   Corrupt dumps
-   Missing fields
-   Runtime differences
-   Large dumps
-   Cancellation
-   Regression
-   False positives
-   False negatives

Recommend missing tests.

------------------------------------------------------------------------

## 19. Rewrite Strategy

If starting from scratch:

-   Would the architecture change?
-   Would responsibilities move?
-   Would infrastructure be extracted?
-   Would analyzers be merged or split?

Explain why.

------------------------------------------------------------------------

## 20. Implementation Roadmap

Prioritize recommendations.

For each item provide:

-   Priority (P0--P3)
-   Expected impact
-   Difficulty
-   Risk
-   Confidence

------------------------------------------------------------------------

## 21. Industry Benchmark

Compare against capabilities offered by:

-   WinDbg + SOS
-   Visual Studio Memory Usage
-   PerfView
-   JetBrains dotMemory
-   Other leading .NET memory diagnostics tools where relevant

Identify:

-   Missing diagnostics
-   Missing evidence
-   Missing metrics
-   Better workflows
-   Better visualizations
-   Better UX
-   Better investigative guidance

Do not recommend copying features blindly; justify their value.

------------------------------------------------------------------------

## Final Verdict

Answer:

1.  Would you ship this analyzer to production today?
2.  Why or why not?
3.  What are the highest-value improvements?
4.  What should be tackled before any new features?
