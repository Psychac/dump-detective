namespace DumpDetective.Platform.Storage.Columns;

/// <summary>
/// Picks the stored width of a narrowed column, and names the escape sentinel that goes with it
/// (docs/cache/cache-format-clean-slate-redesign.md §10.1–§10.2). The chosen width is never stored:
/// readers recover it as <c>Length / RecordCount</c> from the TOC, so a column the writer could not
/// narrow is simply written at <see cref="Full"/> and read exactly as it was before v6.
/// </summary>
internal static class NarrowColumnWidth
{
    public const int Full = sizeof(ulong);

    /// <summary>
    /// Above this share of escaped records, a narrow width stops being worth it — not for the side
    /// table's size, which the cost model below already accounts for, but because the streaming
    /// decoder's escape branch is only free while it predicts. Measured populations are three
    /// orders of magnitude under this.
    /// </summary>
    private const double MaxEscapeRate = 0.01;

    /// <summary>
    /// Chooses between 2, 4 and 8 bytes per record given how many values would escape at each of
    /// the narrow widths, minimising <c>recordCount * width + escapes * EntrySize</c>.
    /// </summary>
    public static int Choose(long recordCount, long escapesAtTwoBytes, long escapesAtFourBytes)
    {
        if (recordCount <= 0)
            return Full;

        long bestCost = recordCount * Full;
        int bestWidth = Full;

        Consider(sizeof(ushort), escapesAtTwoBytes);
        Consider(sizeof(uint), escapesAtFourBytes);

        return bestWidth;

        void Consider(int width, long escapes)
        {
            if (escapes > recordCount * MaxEscapeRate)
                return;

            long cost = recordCount * width + escapes * ColumnOverflowTable.EntrySize;
            if (cost < bestCost)
            {
                bestCost = cost;
                bestWidth = width;
            }
        }
    }

    /// <summary>
    /// The all-ones value at <paramref name="width"/>, reserved to mean "this record's real value is
    /// in the overflow table". <see cref="Full"/> has no sentinel — nothing can escape 8 bytes.
    /// </summary>
    public static ulong Sentinel(int width) => width switch
    {
        sizeof(ushort) => ushort.MaxValue,
        sizeof(uint) => uint.MaxValue,
        _ => ulong.MaxValue,
    };

    public static bool IsSupported(int width) => width is sizeof(ushort) or sizeof(uint) or Full;
}
