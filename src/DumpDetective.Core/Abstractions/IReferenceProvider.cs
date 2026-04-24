using System.Collections.Generic;

namespace DumpDetective.Core.Abstractions;

public interface IReferenceProvider
{
    IEnumerable<ulong> GetReferences(ulong obj);
}
