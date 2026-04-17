using DumpDetective.Core.Options;

namespace DumpDetective.Cli.Services;

internal sealed class StartupValidator
{
    public void Validate(ResolvedExecutionOptions options)
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(options.DumpPath))
        {
            errors.Add("DumpPath is required.");
        }
        else if (!File.Exists(options.DumpPath))
        {
            errors.Add($"DumpPath '{options.DumpPath}' does not exist.");
        }

        if (!string.IsNullOrWhiteSpace(options.BaselineDumpPath) && !File.Exists(options.BaselineDumpPath))
        {
            errors.Add($"BaselineDumpPath '{options.BaselineDumpPath}' does not exist.");
        }

        if (!string.IsNullOrWhiteSpace(options.BaselineDumpPath) && options.TrendDumpPaths is { Count: > 0 })
        {
            errors.Add("BaselineDumpPath and TrendDumpPaths are mutually exclusive.");
        }

        if (options.TrendDumpPaths is { Count: > 0 })
        {
            foreach (string trendPath in options.TrendDumpPaths)
            {
                if (!File.Exists(trendPath))
                {
                    errors.Add($"TrendDumpPath '{trendPath}' does not exist.");
                }
            }
        }

        ValidateMemoryLeakOptions(options.MemoryLeak, errors);
        ValidateReferenceChainOptions(options.ReferenceChain, errors);
        ValidateEventLeakOptions(options.EventLeak, errors);

        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(Environment.NewLine, errors));
        }
    }

    private static void ValidateMemoryLeakOptions(MemoryLeakOptions options, List<string> errors)
    {
        if (options.HighReferenceThreshold <= 0)
        {
            errors.Add("MemoryLeak.HighReferenceThreshold must be greater than zero.");
        }

        if (options.MaxDuplicateStringLength <= 0)
        {
            errors.Add("MemoryLeak.MaxDuplicateStringLength must be greater than zero.");
        }

        if (options.MinDuplicateStringCount <= 0)
        {
            errors.Add("MemoryLeak.MinDuplicateStringCount must be greater than zero.");
        }

        if (options.MaxReferenceAddresses <= 0)
        {
            errors.Add("MemoryLeak.MaxReferenceAddresses must be greater than zero.");
        }
    }

    private static void ValidateReferenceChainOptions(ReferenceChainOptions options, List<string> errors)
    {
        if (options.TopCount <= 0)
        {
            errors.Add("ReferenceChain.TopCount must be greater than zero.");
        }

        if (options.MaxPathSearchObjects <= 0)
        {
            errors.Add("ReferenceChain.MaxPathSearchObjects must be greater than zero.");
        }
    }

    private static void ValidateEventLeakOptions(EventLeakOptions options, List<string> errors)
    {
        if (options.MinSubscribers < 0)
        {
            errors.Add("EventLeak.MinSubscribers must be zero or greater.");
        }
    }
}
