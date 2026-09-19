using System.Linq;

using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>ModuleFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Every per-module observation type here is module-scale (typically tens to low hundreds of
/// loaded assemblies in a real process), never heap-object-scale, so unlike several prior playbook
/// entries, no bounded-selection-in-the-analyzer or single-identity-via-evidence tricks were
/// needed — every list the old generator reads is fully observed per-entry, trusting the
/// analyzer's own emission order (already sorted where the old generator's own logic assumes
/// order, e.g. <c>runtime.module-type-density</c>) the same way <c>ArraySynthesisRule</c> trusts
/// its own pre-sorted lists.
/// </remarks>
internal sealed class ModuleSynthesisRule : ISynthesisRule
{
    private const int DynamicModuleWarningCount = 20;
    private const int DynamicModuleCriticalCount = 100;
    private const int AnonymousModuleWarningCount = 5;

    private const string AnalyzerName = "Module Analysis";

    public string RuleId => "module.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes =
        [
            "runtime.module-population", "runtime.module-heap-footprint", "runtime.module-type-density",
            "runtime.module-conflict-group", "runtime.module-cross-domain-load",
            "runtime.module-assemblyref-mismatch", "runtime.module-unknown-identity",
        ],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        var conflictGroups = new List<Observation>();
        var heapFootprints = new List<Observation>();
        var typeDensities = new List<Observation>();
        var crossDomainLoads = new List<Observation>();
        var assemblyRefMismatches = new List<Observation>();
        var unknownIdentityModules = new List<Observation>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "runtime.module-population": population = observation; break;
                case "runtime.module-conflict-group": conflictGroups.Add(observation); break;
                case "runtime.module-heap-footprint": heapFootprints.Add(observation); break;
                case "runtime.module-type-density": typeDensities.Add(observation); break;
                case "runtime.module-cross-domain-load": crossDomainLoads.Add(observation); break;
                case "runtime.module-assemblyref-mismatch": assemblyRefMismatches.Add(observation); break;
                case "runtime.module-unknown-identity": unknownIdentityModules.Add(observation); break;
            }
        }

        if (population is null)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        long totalModules = (long)population.Measures["total.count"].Value;
        long dynamicModules = (long)population.Measures["dynamic.count"].Value;
        long conflicts = (long)population.Measures["version-conflict.count"].Value;
        ulong heavyModuleWarningThresholdBytes = (ulong)population.Measures["heavy-module-warning.threshold-bytes"].Value;
        long totalDynamicModules = (long)population.Measures["total-dynamic.count"].Value;
        long totalDomains = (long)population.Measures["total-domains.count"].Value;
        long anonymousModuleCount = (long)population.Measures["anonymous-module.count"].Value;
        long unknownIdentityDuplicateCount = (long)population.Measures["unknown-identity-duplicate.count"].Value;

        var findings = new List<Finding>();

        // ── Version conflicts ────────────────────────────────────────────────────
        Severity conflictSeverity = conflicts >= 3 ? Severity.Critical
            : conflicts > 0 ? Severity.Warning
            : Severity.Info;

        string conflictEvidence = $"{totalModules:N0} modules loaded, {dynamicModules:N0} dynamic, {conflicts:N0} version conflict group(s).";
        var conflictDerivedFrom = new List<ObservationId> { population.Id };
        if (conflicts > 0 && conflictGroups.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            int detailCount = Math.Min(3, conflictGroups.Count);
            for (int i = 0; i < detailCount; i++)
            {
                Observation group = conflictGroups[i];
                string moduleName = ((ModuleRef)group.Subjects[0]).SimpleName;
                string versions = group.Evidence.Count > 0 ? group.Evidence[0].Description ?? "" : "";
                sb.Append($" {moduleName}: {versions}");
                if (i < detailCount - 1) sb.Append(';');
                conflictDerivedFrom.Add(group.Id);
            }
            if (conflictGroups.Count > 3)
                sb.Append($" (+ {conflictGroups.Count - 3} more)");
            conflictEvidence += $" Conflicts: {sb}";
        }

        findings.Add(new Finding
        {
            Fingerprint = Fingerprint("version-conflicts"),
            Analyzer = AnalyzerName,
            Category = "Dependency",
            Severity = conflictSeverity,
            Title = conflicts > 0 ? "Module identity conflicts detected" : "Module dependency snapshot",
            Narrative = conflictEvidence,
            Recommendation = conflicts > 0
                ? "Align dependency versions and verify binding redirects/deployment consistency."
                : "No immediate module-version conflict action required.",
            Tags = ["modules", "assemblies", "dependency"],
            MetricValue = conflicts,
            MetricUnit = "conflict-groups",
            DerivedFrom = conflictDerivedFrom,
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        });

        // ── Heavy modules (heap memory) ──────────────────────────────────────────
        if (heapFootprints.Count > 0)
        {
            Observation heaviest = heapFootprints[0];
            string moduleName = ((ModuleRef)heaviest.Subjects[0]).SimpleName;
            ulong totalBytes = (ulong)heaviest.Measures["shallow.bytes"].Value;
            long objectCount = (long)heaviest.Measures["object.count"].Value;
            long uniqueTypeCount = (long)heaviest.Measures["unique-type.count"].Value;

            Severity heavySeverity = totalBytes >= heavyModuleWarningThresholdBytes ? Severity.Warning : Severity.Info;

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("heavy-modules"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = heavySeverity,
                Title = "Module heap memory distribution",
                Narrative = $"Heaviest module on heap: {moduleName} consuming {FormatHelper.FormatBytes(totalBytes)} across {objectCount:N0} objects ({uniqueTypeCount} type(s)).",
                Recommendation = heavySeverity == Severity.Warning
                    ? $"Investigate why {moduleName} dominates heap memory. Check for unbounded caches or collections."
                    : "Module heap distribution appears normal.",
                Tags = ["modules", "memory", "heap"],
                MetricValue = totalBytes,
                MetricUnit = "bytes",
                DerivedFrom = [heaviest.Id, population.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Type density anomalies ───────────────────────────────────────────────
        if (typeDensities.Count > 0)
        {
            Observation worst = typeDensities[0];
            string moduleName = ((ModuleRef)worst.Subjects[0]).SimpleName;
            long uniqueTypeCount = (long)worst.Measures["unique-type.count"].Value;
            ulong totalBytes = (ulong)worst.Measures["shallow.bytes"].Value;
            ulong bytesPerType = (ulong)worst.Measures["bytes-per-type.bytes"].Value;

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("type-density"),
                Analyzer = AnalyzerName,
                Category = "Memory",
                Severity = Severity.Warning,
                Title = "Abnormal type density — memory concentrated in very few types",
                Narrative = $"{typeDensities.Count} module(s) have ≤5 types yet consume significant heap memory. Worst: {moduleName} — {uniqueTypeCount} type(s), {FormatHelper.FormatBytes(totalBytes)}, {FormatHelper.FormatBytes(bytesPerType)}/type.",
                Recommendation = "Few-type modules with large heap footprints often indicate singleton buffers, large arrays, or byte array pools. Verify these are intentional and bounded.",
                Tags = ["modules", "memory", "type-density", "concentration"],
                MetricValue = typeDensities.Count,
                MetricUnit = "anomalous-modules",
                DerivedFrom = [.. typeDensities.Select(t => t.Id)],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Dynamic module accumulation (AppDomain-scoped) ───────────────────────
        if (totalDynamicModules >= DynamicModuleWarningCount)
        {
            Severity sev = totalDynamicModules >= DynamicModuleCriticalCount ? Severity.Critical : Severity.Warning;

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("dynamic-module-accumulation"),
                Analyzer = AnalyzerName,
                Category = "Modules",
                Severity = sev,
                Title = $"Dynamic module accumulation: {totalDynamicModules:N0} dynamic modules loaded",
                Narrative = $"{totalDynamicModules:N0} dynamic (in-memory) modules are present across " +
                            $"{totalDomains:N0} AppDomain(s). Dynamic modules are generated at runtime " +
                            "(e.g. by Reflection.Emit, expression trees, or code-generation frameworks) " +
                            "and are never unloaded while the AppDomain is alive.",
                Recommendation = "Audit usage of Reflection.Emit, LCG (DynamicMethod), and code-generation " +
                                  "libraries. Re-use generated assemblies/methods instead of creating new ones " +
                                  "per request. Consider AssemblyLoadContext for isolation and unloadability.",
                Tags = ["dynamic-module", "reflection", "code-gen", "memory"],
                MetricValue = totalDynamicModules,
                MetricUnit = "modules",
                DerivedFrom = [population.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Anonymous module accumulation ────────────────────────────────────────
        if (anonymousModuleCount >= AnonymousModuleWarningCount)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("anonymous-module-accumulation"),
                Analyzer = AnalyzerName,
                Category = "Modules",
                Severity = Severity.Warning,
                Title = $"Anonymous module accumulation: {anonymousModuleCount:N0} modules without a file name",
                Narrative = $"{anonymousModuleCount:N0} modules have no file-system path (anonymous/in-memory). " +
                            "This is typical of LCG delegates, serializer proxies, or IL-woven code.",
                Recommendation = "Investigate the source of anonymous modules. They cannot be unloaded " +
                                  "independently; use a Collectible AssemblyLoadContext if module lifetime control is needed.",
                Tags = ["anonymous-module", "dynamic", "memory"],
                MetricValue = anonymousModuleCount,
                MetricUnit = "modules",
                DerivedFrom = [population.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Cross-domain module loads ─────────────────────────────────────────────
        if (crossDomainLoads.Count > 0)
        {
            bool anyHeavyOrWidelyLoaded = false;
            foreach (Observation load in crossDomainLoads)
            {
                long domainCount = (long)load.Measures["domain.count"].Value;
                ulong size = (ulong)load.Measures["shallow.bytes"].Value;
                if (domainCount >= 3 || size >= heavyModuleWarningThresholdBytes)
                {
                    anyHeavyOrWidelyLoaded = true;
                    break;
                }
            }

            var sb = new System.Text.StringBuilder();
            int detailCount = Math.Min(3, crossDomainLoads.Count);
            for (int i = 0; i < detailCount; i++)
            {
                Observation load = crossDomainLoads[i];
                string moduleName = ((ModuleRef)load.Subjects[0]).SimpleName;
                long domainCount = (long)load.Measures["domain.count"].Value;
                ulong size = (ulong)load.Measures["shallow.bytes"].Value;
                sb.Append($" {moduleName} ({domainCount} domains, {FormatHelper.FormatBytes(size)})");
                if (i < detailCount - 1) sb.Append(';');
            }
            if (crossDomainLoads.Count > 3)
                sb.Append($" (+ {crossDomainLoads.Count - 3} more)");

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("cross-domain-loads"),
                Analyzer = AnalyzerName,
                Category = "Modules",
                Severity = anyHeavyOrWidelyLoaded ? Severity.Warning : Severity.Info,
                Title = $"Cross-domain module loads: {crossDomainLoads.Count:N0} non-framework module(s) loaded into multiple AppDomains",
                Narrative = $"{crossDomainLoads.Count:N0} non-framework module(s) are each loaded into more than one AppDomain, meaning a separate managed-memory copy per domain. Modules:{sb}",
                Recommendation = "Verify these loads are intentional (e.g. plugin/isolation architecture) rather than accidental re-loading of the same assembly. " +
                                  "Each additional domain load multiplies the module's static/type footprint.",
                Tags = ["modules", "appdomain", "cross-domain", "memory"],
                MetricValue = crossDomainLoads.Count,
                MetricUnit = "cross-domain-modules",
                DerivedFrom = [.. crossDomainLoads.Select(l => l.Id), population.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── AssemblyRef version mismatches ───────────────────────────────────────
        if (assemblyRefMismatches.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            int detailCount = Math.Min(3, assemblyRefMismatches.Count);
            for (int i = 0; i < detailCount; i++)
            {
                Observation mismatch = assemblyRefMismatches[i];
                string requiringModule = ((ModuleRef)mismatch.Subjects[0]).SimpleName;
                string detail = mismatch.Evidence.Count > 0 ? mismatch.Evidence[0].Description ?? "" : "";
                sb.Append($" {requiringModule} requires {detail}");
                if (i < detailCount - 1) sb.Append(';');
            }
            if (assemblyRefMismatches.Count > 3)
                sb.Append($" (+ {assemblyRefMismatches.Count - 3} more)");

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("assemblyref-mismatches"),
                Analyzer = AnalyzerName,
                Category = "Dependency",
                Severity = Severity.Warning,
                Title = $"AssemblyRef version mismatches: {assemblyRefMismatches.Count:N0} module(s) require a version different from what's loaded",
                Narrative = $"{assemblyRefMismatches.Count:N0} AssemblyRef entries request a version of another assembly that differs from every loaded version of it (implicit binding/unification).{sb}",
                Recommendation = "Verify binding redirects or the runtime's assembly unification are intentional. An implicit version substitution can mask a missing dependency " +
                                  "or introduce subtle behavior changes (e.g. MissingMethodException at a rarely-hit code path).",
                Tags = ["modules", "dependency", "assembly-ref", "version-mismatch"],
                MetricValue = assemblyRefMismatches.Count,
                MetricUnit = "assemblyref-mismatches",
                DerivedFrom = [.. assemblyRefMismatches.Select(m => m.Id)],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Unknown-identity duplicate modules ────────────────────────────────────
        if (unknownIdentityDuplicateCount > 0)
        {
            var names = new List<string>(unknownIdentityModules.Count);
            foreach (Observation module in unknownIdentityModules)
                names.Add(((ModuleRef)module.Subjects[0]).SimpleName);

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("unknown-identity-duplicates"),
                Analyzer = AnalyzerName,
                Category = "Dependency",
                Severity = Severity.Warning,
                Title = $"Unknown-identity duplicate modules: {unknownIdentityDuplicateCount:N0} module(s) with unidentifiable versions",
                Narrative = $"{unknownIdentityDuplicateCount:N0} module(s) appear multiple times with no version, public-key-token, or file-hash. " +
                            "This makes it impossible to distinguish whether these are intentional loads of the same assembly or accidental duplicates. " +
                            $"Modules: {string.Join(", ", names.Take(5))}." +
                            (unknownIdentityDuplicateCount > 5 ? $" (+ {unknownIdentityDuplicateCount - 5} more)" : ""),
                Recommendation = "Verify that duplicate loads of these assemblies are intentional. If possible, ensure assemblies have strong-named identity (version, public-key-token) to enable proper conflict detection.",
                Tags = ["modules", "identity", "duplicate", "dependency"],
                MetricValue = unknownIdentityDuplicateCount,
                MetricUnit = "unknown-identity-modules",
                DerivedFrom = unknownIdentityModules.Count > 0 ? [.. unknownIdentityModules.Select(m => m.Id)] : [population.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<Finding>>(findings);
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
