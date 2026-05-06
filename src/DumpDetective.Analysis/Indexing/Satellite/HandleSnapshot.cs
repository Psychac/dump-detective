using System;

namespace DumpDetective.Analysis.Indexing.Satellite;

internal readonly record struct HandleRecord(ulong Address, ulong MethodTable, byte Kind);

internal interface IHandleSnapshotReader : IDisposable
{
    /// <summary>Enumerate handle snapshot records (streaming).</summary>
    System.Collections.Generic.IEnumerable<HandleRecord> EnumerateRecords(System.Threading.CancellationToken token);

    /// <summary>Optional: estimated/known record count when available.</summary>
    long? RecordCount { get; }
}
