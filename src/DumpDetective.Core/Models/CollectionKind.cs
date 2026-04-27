namespace DumpDetective.Core.Models;

/// <summary>
/// Shared enum describing kinds of collections recognized by the analyzers.
/// Placed in Core so it can be referenced by analyzers, reporting and printers.
/// </summary>
public enum CollectionKind
{
    None,
    Dictionary,
    List,
    ArrayList,
    Stack,
    HashSet,
    SortedList,
    SortedSet,
    Queue
}
