using DumpDetective.Cli.Services.Capabilities;
using DumpDetective.Reporting.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace DumpDetective.Cli.Services;

internal sealed class DefaultSectionBuilderFactory : ISectionBuilderFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IAnalyzerFeatureModuleCatalog _moduleCatalog;

    public DefaultSectionBuilderFactory()
        : this(new ServiceCollection().BuildServiceProvider(), new DefaultAnalyzerFeatureModuleCatalog())
    {
    }

    public DefaultSectionBuilderFactory(IServiceProvider serviceProvider, IAnalyzerFeatureModuleCatalog moduleCatalog)
    {
        _serviceProvider = serviceProvider;
        _moduleCatalog = moduleCatalog;
    }

    public IReadOnlyList<IAnalyzerSectionBuilder> CreateAnalyzerBuilders() =>
        _moduleCatalog.Modules
            .OrderBy(m => m.Order)
            .Select(m => (IAnalyzerSectionBuilder)ActivatorUtilities.CreateInstance(_serviceProvider, m.AnalyzerSectionBuilderType))
            .ToList();

    public IReadOnlyList<IReportSectionBuilder> CreateReportBuilders() =>
        _moduleCatalog.GlobalReportSectionBuilderTypes
            .Select(t => (IReportSectionBuilder)ActivatorUtilities.CreateInstance(_serviceProvider, t))
            .ToList();
}
