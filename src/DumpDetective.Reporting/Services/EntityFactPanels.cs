namespace DumpDetective.Reporting.Services;

/// <summary>
/// Which dossier panel each kind of observation feeds, and which measures may be added across several facts of one kind
/// (docs/ReportStructure/m2-entities-plan.md, step 5). A kind missing from <see cref="PanelByFactKind"/> lands in
/// <see cref="Other"/> under its own name, so a new analyzer cannot make facts disappear.
/// </summary>
internal static class EntityFactPanels
{
    public const string SizeAndPopulation = "Size and population";
    public const string GenerationsAndHeaps = "Generations and heaps";
    public const string Retention = "Retention";
    public const string Lifecycle = "Lifecycle";
    public const string ShapeAndCost = "Shape and cost";
    public const string ExceptionsAndAsync = "Exceptions and async";
    public const string State = "State";
    public const string Other = "Other";

    private static readonly Dictionary<string, string> PanelByFactKind = new(StringComparer.Ordinal)
    {
        ["leak.candidate-type"] = SizeAndPopulation,
        ["type.finalizable-population"] = SizeAndPopulation,
        ["type.array-composition"] = SizeAndPopulation,
        ["type.known-pattern-population"] = SizeAndPopulation,
        ["infra.timer"] = SizeAndPopulation,
        ["infra.http-object"] = SizeAndPopulation,
        ["infra.sql-command"] = SizeAndPopulation,
        ["infra.sql-transaction"] = SizeAndPopulation,
        ["infra.db-connection-type-population"] = SizeAndPopulation,
        ["infra.wcf-channel-type-population"] = SizeAndPopulation,

        ["gc.generation-type-profile"] = GenerationsAndHeaps,
        ["gc.generation-top-accumulator"] = GenerationsAndHeaps,
        ["gc.generation-top-allocator"] = GenerationsAndHeaps,
        ["heap.loh-type-composition"] = GenerationsAndHeaps,
        ["heap.large-array"] = GenerationsAndHeaps,
        ["heap.pinned-array"] = GenerationsAndHeaps,
        ["heap.sparse-array"] = GenerationsAndHeaps,
        ["heap.long-string"] = GenerationsAndHeaps,

        ["type.retention"] = Retention,
        ["type.retention-hotspot"] = Retention,
        ["type.root-reachability"] = Retention,
        ["reference-chain.type-sample"] = Retention,
        ["heap.root"] = Retention,
        ["event.publisher-leak"] = Retention,
        ["event.publisher-leak-group"] = Retention,
        ["thread.retained-bytes"] = Retention,

        ["finalizer.queue-entry"] = Lifecycle,
        ["weakhandle.target"] = Lifecycle,
        ["handle.pinned"] = Lifecycle,
        ["handle.pinned-target-bytes"] = Lifecycle,
        ["handle.pinned-target-type"] = Lifecycle,
        ["handle.refcounted-target"] = Lifecycle,
        ["type.orphaned-task"] = Lifecycle,

        ["type.boxing"] = ShapeAndCost,
        ["type.struct-padding-waste"] = ShapeAndCost,
        ["type.oversized-value"] = ShapeAndCost,
        ["type.reference-heavy"] = ShapeAndCost,
        ["type.value-heavy"] = ShapeAndCost,
        ["collection.wasteful"] = ShapeAndCost,
        ["collection.element-type-waste"] = ShapeAndCost,

        ["runtime.exception-type"] = ExceptionsAndAsync,
        ["runtime.active-exception"] = ExceptionsAndAsync,
        ["runtime.crash-thread-candidate"] = ExceptionsAndAsync,
        ["type.async-state-machine"] = ExceptionsAndAsync,
        ["type.async-captured-closure"] = ExceptionsAndAsync,

        ["thread.state"] = State,
        ["runtime.finalizer-thread"] = State,
        ["thread.stack-cluster-member"] = State,
    };

    // An allow-list, so a measure nobody has reviewed can never be summed wrongly: counts and shallow-style byte totals add up
    // across facts; retained bytes overlap, and averages, ratios, percents and ordinals do not add.
    private static readonly HashSet<string> SummableMeasures = new(StringComparer.Ordinal)
    {
        "instance.count", "shallow.bytes", "gen0.count", "gen1.count", "gen2.count", "gen2.bytes", "loh.count", "loh.bytes",
        "total.bytes", "total.count", "object.count", "count", "subscriber.count", "pinned.count", "pinned.bytes",
        "captured.bytes", "wasted.bytes", "wasted-padding.bytes", "waste.count", "waste.bytes",
    };

    public static string PanelFor(string factKind) =>
        PanelByFactKind.TryGetValue(factKind, out string? panel) ? panel : Other;

    public static bool IsSummable(string measureName) => SummableMeasures.Contains(measureName);
}
