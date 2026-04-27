using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Pipeline;

internal static class AnalysisContextExtensions
{
    /// <summary>
    /// Retrieves a strongly-typed option from the context's options registry.
    /// Returns <c>new T()</c> when the type is not registered, ensuring analyzers always
    /// receive a valid options object and never silently lose configuration.
    /// </summary>
    public static T GetOption<T>(this AnalysisContext context) where T : class, new()
        => context.Options.TryGetValue(typeof(T), out object? val) && val is T typed ? typed : new T();
}
