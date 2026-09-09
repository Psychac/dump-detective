using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sdk.Analysis;

/// <summary>The <c>runtime.modules</c> capability.</summary>
public interface IRuntimeModuleQuery
{
    IEnumerable<ModuleRef> EnumerateModules();
}
