namespace DumpDetective.Sdk.Analysis;

public enum FieldValueKind
{
    Null,
    Bool,
    Int32,
    Int64,
    Double,
    String,
    ObjectReference,
    /// <summary>Added 2026-09-12 for <c>TimerLeakAnalyzer</c>'s retyping: <c>TimerQueueTimer._period</c>/
    /// <c>m_period</c> is a genuine <c>uint</c> field (decompile-verified across .NET Framework and
    /// every modern .NET version checked), whose <c>uint.MaxValue</c> "infinite" sentinel reinterprets
    /// incorrectly if forced through <see cref="Int32"/>'s signed semantics.</summary>
    UInt32,
    /// <summary>Added 2026-09-12 for <c>WeakReferenceAnalyzer</c>'s retyping: <c>WeakReference</c>/
    /// <c>WeakReference&lt;T&gt;</c>'s <c>m_handle</c> field is a genuine pointer-sized <c>IntPtr</c>
    /// (ClrMD's <c>ClrElementType.NativeInt</c>, decompile-verified against the installed ClrMD
    /// package) holding an opaque GC handle value — not a tracked object reference (<see cref="ObjectReference"/>
    /// would imply the GC scans/relocates it, which a raw handle value is not), and not safely
    /// representable as <see cref="Int64"/> without asserting a fixed width a native pointer doesn't
    /// have on a 32-bit dump.</summary>
    NativeInt,
}

/// <summary>
/// One instance field's value, read through <see cref="IDumpObjectFieldQuery"/>. Narrowed to the
/// primitive shapes a real Tier-2 consumer has actually needed so far
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md's Tier-2 pilot,
/// <c>SqlConnectionPoolAnalyzer</c>) — not a general boxed-<see cref="object"/> bag, so a caller's
/// <c>switch</c>/<c>if</c> on <see cref="Kind"/> stays a compiler-checked enum comparison instead of
/// an unchecked cast. Extend with a new <see cref="FieldValueKind"/> case only once a real analyzer
/// needs one — same hard-need-basis convention every other SDK surface in this plan follows.
/// </summary>
/// <remarks>
/// Private constructor + named factory methods, not a positional record — avoids the
/// adjacent-same-typed-parameter transposition risk docs/refactor/modularity/phase-1-sdk-review-findings.md
/// item 5 already found and fixed on three other SDK types (<c>HeapRootRef</c>,
/// <c>HeapSegmentRef</c>, <c>ConfidenceBreakdown</c>) — a <c>FieldValue</c> constructed from several
/// same-typed positional slots (numeric/double/string/address) is exactly that shape again.
/// </remarks>
public readonly struct FieldValue
{
    public FieldValueKind Kind { get; }

    private readonly long _numeric;
    private readonly double _double;
    private readonly string? _string;
    private readonly ulong _address;

    private FieldValue(FieldValueKind kind, long numeric = 0, double @double = 0, string? @string = null, ulong address = 0)
    {
        Kind = kind;
        _numeric = numeric;
        _double = @double;
        _string = @string;
        _address = address;
    }

    public static readonly FieldValue Null = new(FieldValueKind.Null);

    public static FieldValue ForBool(bool value) => new(FieldValueKind.Bool, numeric: value ? 1 : 0);
    public static FieldValue ForInt32(int value) => new(FieldValueKind.Int32, numeric: value);
    public static FieldValue ForInt64(long value) => new(FieldValueKind.Int64, numeric: value);
    public static FieldValue ForUInt32(uint value) => new(FieldValueKind.UInt32, numeric: value);
    public static FieldValue ForDouble(double value) => new(FieldValueKind.Double, @double: value);
    public static FieldValue ForString(string value) => new(FieldValueKind.String, @string: value);
    public static FieldValue ForObjectReference(ulong address) => new(FieldValueKind.ObjectReference, address: address);
    public static FieldValue ForNativeInt(nint value) => new(FieldValueKind.NativeInt, numeric: value);

    public bool AsBool() => Kind == FieldValueKind.Bool
        ? _numeric != 0
        : throw new InvalidOperationException($"FieldValue is {Kind}, not {FieldValueKind.Bool}.");

    public int AsInt32() => Kind == FieldValueKind.Int32
        ? (int)_numeric
        : throw new InvalidOperationException($"FieldValue is {Kind}, not {FieldValueKind.Int32}.");

    /// <summary>Widens an <see cref="FieldValueKind.Int32"/> value too — a caller that only cares
    /// about the numeric value, not the exact declared width, shouldn't have to branch on both.</summary>
    public long AsInt64() => Kind is FieldValueKind.Int64 or FieldValueKind.Int32
        ? _numeric
        : throw new InvalidOperationException($"FieldValue is {Kind}, not {FieldValueKind.Int64}.");

    public uint AsUInt32() => Kind == FieldValueKind.UInt32
        ? (uint)_numeric
        : throw new InvalidOperationException($"FieldValue is {Kind}, not {FieldValueKind.UInt32}.");

    public double AsDouble() => Kind == FieldValueKind.Double
        ? _double
        : throw new InvalidOperationException($"FieldValue is {Kind}, not {FieldValueKind.Double}.");

    public string AsString() => Kind == FieldValueKind.String && _string is not null
        ? _string
        : throw new InvalidOperationException($"FieldValue is {Kind}, not {FieldValueKind.String}.");

    public ulong AsObjectAddress() => Kind == FieldValueKind.ObjectReference
        ? _address
        : throw new InvalidOperationException($"FieldValue is {Kind}, not {FieldValueKind.ObjectReference}.");

    public nint AsNativeInt() => Kind == FieldValueKind.NativeInt
        ? (nint)_numeric
        : throw new InvalidOperationException($"FieldValue is {Kind}, not {FieldValueKind.NativeInt}.");
}
