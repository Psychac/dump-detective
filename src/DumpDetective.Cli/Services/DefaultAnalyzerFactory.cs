using DumpDetective.Cli.Services.Capabilities;
using DumpDetective.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace DumpDetective.Cli.Services;

internal sealed class DefaultAnalyzerFactory : IAnalyzerFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IAnalyzerFeatureModuleCatalog _moduleCatalog;

    public DefaultAnalyzerFactory()
        : this(BuildCompatibilityServiceProvider(NullLoggerFactory.Instance), new DefaultAnalyzerFeatureModuleCatalog())
    {
    }

    public DefaultAnalyzerFactory(IServiceProvider serviceProvider, IAnalyzerFeatureModuleCatalog moduleCatalog)
    {
        _serviceProvider = serviceProvider;
        _moduleCatalog = moduleCatalog;
    }

    public IReadOnlyList<IAnalyzer> CreateAnalyzers()
    {
        return _moduleCatalog.Modules
            .OrderBy(m => m.Order)
            .Select(m => (IAnalyzer)ActivatorUtilities.CreateInstance(_serviceProvider, m.AnalyzerType))
            .ToList();
    }

    private static IServiceProvider BuildCompatibilityServiceProvider(ILoggerFactory loggerFactory)
    {
        ServiceCollection services = new();
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        return services.BuildServiceProvider();
    }
}
