using System;

namespace DumpDetective.Analysis.Indexing.Satellite;

/// <summary><paramref name="DependentTarget"/> is the secondary target address for "Dependent"
/// kind handles (P3-3) — 0 when not a Dependent handle, unresolvable, or read from a v1 (pre-P3-3)
/// on-disk snapshot that didn't carry it.</summary>
internal readonly record struct HandleRecord(ulong Address, ulong MethodTable, byte Kind, bool IsAlive = true, ulong DependentTarget = 0);

internal interface IHandleSnapshotReader : IDisposable
{
    /// <summary>Enumerate handle snapshot records (streaming).</summary>
    System.Collections.Generic.IEnumerable<HandleRecord> EnumerateRecords(System.Threading.CancellationToken token);

    /// <summary>Optional: estimated/known record count when available.</summary>
    long? RecordCount { get; }
}
