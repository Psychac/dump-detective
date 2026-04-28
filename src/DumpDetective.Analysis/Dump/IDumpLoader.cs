using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Dump;

/// <summary>
/// Contract for loading a .NET memory dump and providing access to the ClrMD runtime objects.
/// Implementations are responsible for proper disposal of all underlying resources.
/// </summary>
internal interface IDumpLoader
{
    /// <summary>
    /// Loads the dump at <paramref name="dumpPath"/> and returns a <see cref="DumpLoadContext"/>
    /// that grants access to the <see cref="ClrRuntime"/> and <see cref="ClrHeap"/>.
    /// The caller is responsible for disposing the returned context.
    /// </summary>
    Task<DumpLoadContext> LoadAsync(string dumpPath, CancellationToken cancellationToken);
}
