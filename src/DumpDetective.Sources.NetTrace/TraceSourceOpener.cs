using Microsoft.Diagnostics.Tracing;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// Opens the right <see cref="TraceEventDispatcher"/> subclass for a trace file by extension.
/// <see cref="EventPipeEventSource"/> (<c>.nettrace</c>) and <see cref="ETWTraceEventSource"/>
/// (<c>.etl</c>) share the same callback-dispatch base API, so every indexer in this project is
/// built against that shared base and opens its source through this one place.
/// </summary>
internal static class TraceSourceOpener
{
    public static TraceEventDispatcher Open(string tracePath)
    {
        string extension = Path.GetExtension(tracePath);
        return extension.Equals(".etl", StringComparison.OrdinalIgnoreCase)
            ? new ETWTraceEventSource(tracePath)
            : extension.Equals(".nettrace", StringComparison.OrdinalIgnoreCase)
                ? new EventPipeEventSource(tracePath)
                : throw new NotSupportedException($"Unsupported trace file extension: {extension}");
    }
}
