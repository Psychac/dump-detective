namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// Tier 2 — a second dump-only capability alongside <see cref="IDumpObjectFieldQuery"/>, not a
/// separate "third tier": array *element* access (length/rank shape, plus counting null/zero
/// elements) is exactly as dump-only-forever as named-field reads, just addressed by index instead
/// of by field name. Added for <c>ArrayAnalyzer</c>'s retyping
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md).
/// </summary>
/// <remarks>
/// Deliberately not a granular "read element i" primitive — that would force a per-analyzer BFS-style
/// round trip for what's actually always a whole-array aggregate operation in every real consumer
/// today (sparse-density counting). Exposing the aggregate directly as the capability lets the
/// dump-side implementation choose the cheap bulk strategy internally (chunked <c>ClrArray.ReadValues&lt;T&gt;</c>
/// for numeric elements vs. a plain reference-null walk), the same reasoning every other Tier-1/Tier-2
/// surface in this SDK already follows for capability boundaries drawn around a meaningful operation,
/// not every underlying method.
/// </remarks>
public interface IDumpArrayShapeQuery
{
    /// <summary>Returns <c>false</c> for an invalid/unreadable address.</summary>
    bool TryGetArrayShape(ulong address, out int length, out int rank);

    /// <summary>
    /// Exact count of null references (reference-element arrays) or zero-valued elements
    /// (numeric-element arrays) — walks every element, no stride sampling. Returns <c>null</c> when
    /// the array is invalid, rank != 1 (multi-dimensional arrays aren't walked — matches the
    /// pre-retyping analyzer's own single-dimension-only sparse scan), or part of the array was
    /// unreadable (e.g. a truncated dump) — the caller must treat <c>null</c> as "skip this
    /// candidate," not as zero.
    /// </summary>
    int? CountNullOrZeroElements(ulong address);
}
