param(
    [string]$OutputDir = "artifacts/reports/phase0",
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
Set-Location $repoRoot

$phase0Dir = Join-Path $repoRoot $OutputDir
New-Item -ItemType Directory -Path $phase0Dir -Force | Out-Null

$utcNow = [DateTime]::UtcNow.ToString("o")

function Get-Matches {
    param(
        [string]$Path,
        [string]$Pattern,
        [int]$Group = 1
    )

    $content = Get-Content $Path -Raw
    $regex = [regex]::new($Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    $results = New-Object System.Collections.Generic.List[string]

    foreach ($m in $regex.Matches($content)) {
        $name = $m.Groups[$Group].Value
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            [void]$results.Add($name)
        }
    }

    return $results
}

function Get-BlockMatches {
    param(
        [string]$Path,
        [string]$BlockStartPattern,
        [string]$ItemPattern,
        [int]$Group = 1
    )

    $content = Get-Content $Path -Raw
    $blockRegex = [regex]::new($BlockStartPattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $blockMatch = $blockRegex.Match($content)
    if (-not $blockMatch.Success) {
        return @()
    }

    $block = $blockMatch.Groups[1].Value
    $itemRegex = [regex]::new($ItemPattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    $items = New-Object System.Collections.Generic.List[string]
    foreach ($m in $itemRegex.Matches($block)) {
        $name = $m.Groups[$Group].Value
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            [void]$items.Add($name)
        }
    }

    return $items
}

function Write-JsonFile {
    param(
        [string]$Path,
        [object]$Data
    )

    $json = $Data | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
}

$moduleCatalogPath = Join-Path $repoRoot "src/DumpDetective.Reporting/Capabilities/DefaultAnalyzerFeatureModuleCatalog.cs"
$serviceRegistrationPath = Join-Path $repoRoot "src/DumpDetective.Cli/Hosting/ServiceRegistration.cs"

$analyzers = @(Get-Matches -Path $moduleCatalogPath -Pattern "typeof\(([A-Za-z0-9_]+Analyzer)\)" | Select-Object -Unique)
$findingGenerators = @(Get-Matches -Path $moduleCatalogPath -Pattern "typeof\(([A-Za-z0-9_]+FindingGenerator)\)" | Select-Object -Unique)
$trendComparers = @(Get-Matches -Path $moduleCatalogPath -Pattern "typeof\(([A-Za-z0-9_]+TrendComparer)\)" | Select-Object -Unique)
$analyzerSectionBuilders = @(Get-Matches -Path $moduleCatalogPath -Pattern "typeof\(([A-Za-z0-9_]+SectionBuilder)\),\s*\d+" | Select-Object -Unique)

$reportSectionBuilders = @(Get-BlockMatches `
    -Path $moduleCatalogPath `
    -BlockStartPattern "GlobalReportSectionBuilderTypes\s*\{\s*get;\s*\}\s*=\s*\[(.*?)\];" `
    -ItemPattern "typeof\(([A-Za-z0-9_]+SectionBuilder)\)" | Select-Object -Unique)

$registrationSnapshot = [ordered]@{
    generatedAtUtc = $utcNow
    analyzers = $analyzers
    analyzerCount = $analyzers.Count
    findingGenerators = $findingGenerators
    findingGeneratorCount = $findingGenerators.Count
    trendComparers = $trendComparers
    trendComparerCount = $trendComparers.Count
    analyzerSectionBuilders = $analyzerSectionBuilders
    analyzerSectionBuilderCount = $analyzerSectionBuilders.Count
    reportSectionBuilders = $reportSectionBuilders
    reportSectionBuilderCount = $reportSectionBuilders.Count
}

$manifest = [ordered]@{
    generatedAtUtc = $utcNow
    singleDump = [ordered]@{
        note = "Populate with real dump paths in local override file before formal capture."
        required = @("baseline-small", "dup-heavy", "rich-evidence")
    }
    trend = [ordered]@{
        requiredOrdered = @("baseline", "comparison", "current")
        note = "Order must be oldest to newest for trend baselines."
    }
    snapshots = @(
        "registration-snapshot.json",
        "single-dump-smoke.json",
        "trend-smoke.json",
        "html-smoke.json",
        "guardrail-tests.json"
    )
}

Write-JsonFile -Path (Join-Path $phase0Dir "registration-snapshot.json") -Data $registrationSnapshot
Write-JsonFile -Path (Join-Path $phase0Dir "golden-dump-set.manifest.json") -Data $manifest

$singleSmoke = [ordered]@{ generatedAtUtc = $utcNow; status = "not-run"; testClass = "DumpDetective.Tests.Integration.P0SmokeTests"; passed = 0; failed = 0; total = 0; tests = @() }
$trendSmoke = [ordered]@{ generatedAtUtc = $utcNow; status = "not-run"; testClass = "DumpDetective.Tests.Integration.P0SmokeTests"; passed = 0; failed = 0; total = 0; tests = @() }
$htmlSmoke = [ordered]@{ generatedAtUtc = $utcNow; status = "not-run"; testClass = "DumpDetective.Tests.Integration.P0SmokeTests"; passed = 0; failed = 0; total = 0; tests = @() }
$guardrails = [ordered]@{ generatedAtUtc = $utcNow; status = "not-run"; testClasses = @("DumpDetective.Tests.Integration.ProgramEntryPointTests", "DumpDetective.Tests.Unit.Analysis.DominatorFindingGeneratorTests"); passed = 0; failed = 0; total = 0; tests = @() }

if (-not $SkipTests) {
    $trxName = "phase0-smoke.trx"
    $trxPath = Join-Path $phase0Dir $trxName

    dotnet test tests/DumpDetective.Tests/DumpDetective.Tests.csproj `
        --filter "FullyQualifiedName~DumpDetective.Tests.Integration.P0SmokeTests|FullyQualifiedName~DumpDetective.Tests.Integration.ProgramEntryPointTests|FullyQualifiedName~DumpDetective.Tests.Unit.Analysis.DominatorFindingGeneratorTests" `
        --logger "trx;LogFileName=$trxName" `
        --results-directory $phase0Dir `
        --nologo

    if (Test-Path $trxPath) {
        [xml]$trx = Get-Content $trxPath
        $namespace = New-Object System.Xml.XmlNamespaceManager($trx.NameTable)
        $namespace.AddNamespace("trx", "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
        $results = @($trx.SelectNodes("//trx:UnitTestResult", $namespace))

        function To-TestRecord {
            param([object]$r)
            [ordered]@{
                name = [string]$r.testName
                outcome = [string]$r.outcome
                duration = [string]$r.duration
            }
        }

        $singleTests = @($results | Where-Object { $_.testName -match "\.P0_1_|\.P0_3_" })
        $trendTests = @($results | Where-Object { $_.testName -match "\.TrendHtml_" })
        $htmlTests = @($results | Where-Object { $_.testName -match "\.P0_1_HtmlReport_|\.P0_3_HtmlReport_|\.TrendHtml_" })
        $guardrailTests = @($results | Where-Object { $_.testName -match "ProgramEntryPointTests\.|DominatorFindingGeneratorTests\." })

        $singleSmoke.tests = @($singleTests | ForEach-Object { To-TestRecord $_ })
        $singleSmoke.total = $singleTests.Count
        $singleSmoke.failed = @($singleTests | Where-Object { $_.outcome -ne "Passed" }).Count
        $singleSmoke.passed = $singleSmoke.total - $singleSmoke.failed
        $singleSmoke.status = if ($singleSmoke.total -eq 0) { "not-run" } elseif ($singleSmoke.failed -eq 0) { "pass" } else { "fail" }

        $trendSmoke.tests = @($trendTests | ForEach-Object { To-TestRecord $_ })
        $trendSmoke.total = $trendTests.Count
        $trendSmoke.failed = @($trendTests | Where-Object { $_.outcome -ne "Passed" }).Count
        $trendSmoke.passed = $trendSmoke.total - $trendSmoke.failed
        $trendSmoke.status = if ($trendSmoke.total -eq 0) { "not-run" } elseif ($trendSmoke.failed -eq 0) { "pass" } else { "fail" }

        $htmlSmoke.tests = @($htmlTests | ForEach-Object { To-TestRecord $_ })
        $htmlSmoke.total = $htmlTests.Count
        $htmlSmoke.failed = @($htmlTests | Where-Object { $_.outcome -ne "Passed" }).Count
        $htmlSmoke.passed = $htmlSmoke.total - $htmlSmoke.failed
        $htmlSmoke.status = if ($htmlSmoke.total -eq 0) { "not-run" } elseif ($htmlSmoke.failed -eq 0) { "pass" } else { "fail" }

        $guardrails.tests = @($guardrailTests | ForEach-Object { To-TestRecord $_ })
        $guardrails.total = $guardrailTests.Count
        $guardrails.failed = @($guardrailTests | Where-Object { $_.outcome -ne "Passed" }).Count
        $guardrails.passed = $guardrails.total - $guardrails.failed
        $guardrails.status = if ($guardrails.total -eq 0) { "not-run" } elseif ($guardrails.failed -eq 0) { "pass" } else { "fail" }
    }
}

Write-JsonFile -Path (Join-Path $phase0Dir "single-dump-smoke.json") -Data $singleSmoke
Write-JsonFile -Path (Join-Path $phase0Dir "trend-smoke.json") -Data $trendSmoke
Write-JsonFile -Path (Join-Path $phase0Dir "html-smoke.json") -Data $htmlSmoke
Write-JsonFile -Path (Join-Path $phase0Dir "guardrail-tests.json") -Data $guardrails

Write-Host "Phase 0 baseline artifacts written to: $phase0Dir"
