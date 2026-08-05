namespace DumpDetective.Analysis.Analyzers.EventLeak;

/// <summary>
/// One delegate-typed field a <see cref="IPublisherShape"/> recognized on a type, produced
/// once per unique MethodTable during <see cref="PublisherRegistry.Build"/> (design §3.3).
/// </summary>
internal readonly struct EventFieldDescriptor
{
    /// <summary>
    /// Absolute byte offset from the start of the object (same frame as
    /// <c>ClrInstanceField.Offset + pointerSize</c>). Meaningless for static descriptors
    /// (<see cref="IsStatic"/> == true), which are read via <c>ClrStaticField</c> instead.
    /// </summary>
    public readonly int Offset;
    public readonly string FieldName;
    public readonly string PublisherTypeName;
    public readonly bool IsStatic;
    public readonly byte ShapeId;

    public EventFieldDescriptor(int offset, string fieldName, string publisherTypeName, bool isStatic, byte shapeId)
    {
        Offset = offset;
        FieldName = fieldName;
        PublisherTypeName = publisherTypeName;
        IsStatic = isStatic;
        ShapeId = shapeId;
    }
}
