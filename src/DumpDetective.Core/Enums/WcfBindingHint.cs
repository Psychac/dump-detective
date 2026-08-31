namespace DumpDetective.Core.Enums;

/// <summary>
/// Heuristic classification of a WCF channel's binding, inferred from its runtime type name
/// (see <c>WcfChannelAnalyzer.ClassifyBindingHint</c>). Not authoritative — a dump has no
/// binding configuration, only the channel object graph — but the type-name conventions used by
/// System.ServiceModel's channel factories are stable enough to distinguish the common bindings.
/// </summary>
public enum WcfBindingHint
{
    Unknown,
    Basic,
    NetTcp,
    WsHttp,
    NamedPipe
}
