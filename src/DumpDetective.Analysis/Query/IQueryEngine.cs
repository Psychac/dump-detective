using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Query;

/// <summary>
/// Structured querying layer that operates on pre-built indices — never on the raw heap.
/// All methods stream results; callers decide how many to materialize.
/// </summary>
internal interface IQueryEngine
{
    /// <summary>
    /// Returns the top <paramref name="topN"/> types ranked by total retained bytes, descending.
    /// Operates on the already-built type statistics cache; O(1) if cache is warm.
    /// </summary>
    IEnumerable<TypeSnapshot> TopTypesBySize(int topN);

    /// <summary>
    /// Streams all indexed heap entries whose CLR type name equals <paramref name="typeName"/>.
    /// Operates on the disk- or memory-backed object index; never enumerates the live heap.
    /// Returns an empty sequence when the type is not found.
    /// </summary>
    IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> ObjectsOfType(string typeName);
}
