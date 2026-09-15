namespace DumpDetective.Orchestration.Graph;

/// <summary>
/// Keyed data bag threaded through a pipeline run. Nodes read the keys they declare in
/// <see cref="IPipelineNode.Consumes"/> and write the keys they declare in
/// <see cref="IPipelineNode.Produces"/>; the keys themselves are opaque strings to the executor.
/// </summary>
public sealed class PipelineNodeContext
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

    public void Set<T>(string key, T value) where T : notnull => _values[key] = value;

    public T Get<T>(string key)
    {
        if (!_values.TryGetValue(key, out object? value))
            throw new KeyNotFoundException($"Pipeline context has no value for key '{key}'.");

        return (T)value;
    }

    public bool TryGet<T>(string key, out T? value)
    {
        if (_values.TryGetValue(key, out object? raw) && raw is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }
}
