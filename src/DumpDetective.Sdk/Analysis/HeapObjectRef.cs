using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// A live heap object, source-neutral. The SDK-typed equivalent of today's
/// <c>Core.Models.HeapEntry</c> — <see cref="Type"/> carries a <see cref="TypeRef"/> for identity
/// instead of a raw dump-local <c>MethodTable</c>. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md's Tier-1 surface table.
/// </summary>
public readonly record struct HeapObjectRef(ulong Address, TypeRef Type, ulong Size);
