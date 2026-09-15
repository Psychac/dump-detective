using DumpDetective.Core.Enums;

namespace DumpDetective.Analysis.Models;

/// <summary>
/// Per-type count/state summary for a WCF channel type found on the heap.
/// </summary>
internal sealed record WcfChannelTypeSummary(
    string TypeName,
    int TotalCount,
    int OpeningCount,
    int OpenedCount,
    int FaultedCount,
    int ClosingCount,
    int ClosedCount,
    int OtherCount,
    ulong TotalBytes,
    WcfBindingHint BindingHint = WcfBindingHint.Unknown,
    /// <summary>Count of channels whose read state value fell outside the valid
    /// CommunicationState range (0-5) — a field-probe mismatch or memory corruption signal,
    /// kept separate from <see cref="OtherCount"/> rather than silently folded into it.</summary>
    int InvalidStateCount = 0);

/// <summary>
/// Lightweight snapshot of a single WCF channel object.
/// </summary>
internal sealed record WcfChannelSnapshot(
    string TypeName,
    ulong Address,
    string StateLabel,
    int StateValue,
    string? RemoteAddress = null,
    /// <summary>Exact dominator-tree retained bytes for this channel (§9,
    /// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md). Null when the exact
    /// tree wasn't available.</summary>
    ulong? RetainedBytes = null);

/// <summary>
/// Domain result produced by <c>WcfChannelAnalyzer</c>.
/// </summary>
internal sealed record WcfChannelDomainResult(
    bool WcfPresent,
    int TotalChannels,
    int OpeningChannels,
    int OpenedChannels,
    int FaultedChannels,
    int ClosingChannels,
    int ClosedChannels,
    int OtherChannels,
    IReadOnlyList<WcfChannelTypeSummary> ByType,
    IReadOnlyList<WcfChannelSnapshot> TopFaultedChannels,
    int FactoryCount = 0,
    ulong TotalBytes = 0,
    /// <summary>Aggregate of <see cref="WcfChannelTypeSummary.InvalidStateCount"/> across
    /// <see cref="ByType"/>.</summary>
    int InvalidStateCount = 0,
    /// <summary>Total channel instances whose type name identifies a duplex-shaped channel
    /// (e.g. ClientFramingDuplexSessionChannel). A channel can be both duplex and session-based;
    /// this and <see cref="SessionChannelCount"/> are independent, overlapping classifications
    /// of <see cref="TotalChannels"/>, not a separate partition.</summary>
    int DuplexChannelCount = 0,
    /// <summary>Total channel instances whose type name identifies a session-based channel.</summary>
    int SessionChannelCount = 0) : DumpDetective.Core.Models.AnalyzerDomainResult;

