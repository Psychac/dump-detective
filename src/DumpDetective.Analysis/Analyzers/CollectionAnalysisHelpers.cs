using System;

namespace DumpDetective.Analysis.Analyzers
{
    internal static class CollectionAnalysisHelpers
    {
        /// <summary>
        /// Compute contiguous free segments information for a circular buffer-backed queue.
        /// Returns (freeSegmentCount, largestFreeSlots).
        /// </summary>
        public static (int freeSegmentCount, int largestFreeSlots) ComputeQueueFreeSegments(int capacity, int size, int? head)
        {
            if (capacity <= 0)
                return (0, 0);

            if (size <= 0)
            {
                // entire buffer free
                return (1, capacity);
            }

            if (!head.HasValue || head.Value < 0 || head.Value >= capacity)
            {
                // cannot reason about head; fall back to conservative single segment estimate
                int free = capacity - size;
                return (free > 0 ? 1 : 0, Math.Max(0, free));
            }

            int h = head.Value;
            int endIndex = (h + size - 1) % capacity;

            if (h <= endIndex)
            {
                // used region is contiguous (no wrap)
                int before = h;
                int after = capacity - endIndex - 1;
                int segs = 0;
                if (before > 0) segs++;
                if (after > 0) segs++;
                int largest = Math.Max(before, after);
                return (segs, Math.Max(0, largest));
            }
            else
            {
                // used wraps: free region between endIndex+1 .. h-1
                int freeLen = h - endIndex - 1;
                return (freeLen > 0 ? 1 : 0, Math.Max(0, freeLen));
            }
        }

        public static ulong ComputeWastedMemoryFromSlots(int capacity, int count, ulong elementSize)
        {
            if (capacity <= 0 || count >= capacity)
                return 0UL;
            ulong wastedSlots = (ulong)(capacity - count);
            return wastedSlots * elementSize;
        }

        // Resolve element size using component type when available; otherwise fall back to array size / capacity.
        // This overload is useful for unit tests where ClrType is not available.
        public static ulong ResolveElementSizeFromComponentInfo(bool hasComponentType, bool componentIsValueType, int componentStaticSize, ulong fallbackArraySize, int capacity)
        {
            if (hasComponentType)
            {
                return componentIsValueType ? (ulong)componentStaticSize : (ulong)IntPtr.Size;
            }

            if (capacity <= 0) return 0UL;
            return fallbackArraySize / (ulong)capacity;
        }

        // Resolve element size when a ClrType component type is available.
        public static ulong ResolveElementSizeFromClrType(Microsoft.Diagnostics.Runtime.ClrType? compType, ulong fallbackArraySize, int capacity)
        {
            if (compType != null)
            {
                if (compType.IsValueType)
                    return (ulong)compType.StaticSize;
                return (ulong)IntPtr.Size;
            }

            if (capacity <= 0) return 0UL;
            return fallbackArraySize / (ulong)capacity;
        }
    }
}
