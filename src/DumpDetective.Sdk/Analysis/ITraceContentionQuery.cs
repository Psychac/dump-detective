using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sdk.Analysis;

/// <summary>One lock-contention episode — see <see cref="ITraceContentionQuery"/>.</summary>
public readonly record struct ContentionEpisode(
    ThreadRef Thread,
    TimeSpan StartProcessUptime,
    TimeSpan Duration);

/// <summary>The <c>trace.contention-events</c> capability.</summary>
public interface ITraceContentionQuery
{
    IEnumerable<ContentionEpisode> EnumerateEpisodes();
}
