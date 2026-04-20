param(
    [Parameter(Mandatory = $true)] [string]$ResultsJson,
    [Parameter(Mandatory = $false)] [string]$BaselineJson = "./perf-baselines.json"
)

if (-not (Test-Path $ResultsJson)) {
    Write-Error "Results path not found: $ResultsJson"
    exit 1
}

if (-not (Test-Path $BaselineJson)) {
    Write-Error "Baseline JSON not found: $BaselineJson"
    exit 1
}

$baseline = Get-Content $BaselineJson -Raw | ConvertFrom-Json
$threshold = [double]$baseline.ThresholdPercent
$failed = $false

$resultFiles = @()
if (Test-Path $ResultsJson -PathType Container) {
    $resultFiles = Get-ChildItem $ResultsJson -Filter "*report-full.json" -File
} else {
    $resultFiles = ,(Get-Item $ResultsJson)
}

if ($resultFiles.Count -eq 0) {
    Write-Error "No benchmark result json files found from: $ResultsJson"
    exit 1
}

function Get-BenchmarkKey($entry) {
    $candidates = @(
        $entry.Descriptor.WorkloadMethod.Name,
        $entry.BenchmarkCase.Descriptor.WorkloadMethod.Name,
        $entry.BenchmarkCase.Descriptor.WorkloadMethodDisplayInfo,
        $entry.Descriptor.WorkloadMethodDisplayInfo,
        $entry.DisplayInfo,
        $entry.FullName
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        foreach ($baselineName in $baseline.Benchmarks.PSObject.Properties.Name) {
            if ($candidate -eq $baselineName -or $candidate -like "*$baselineName*") {
                return $baselineName
            }
        }
    }

    return $null
}

foreach ($file in $resultFiles) {
    $results = Get-Content $file.FullName -Raw | ConvertFrom-Json

    foreach ($entry in $results.Benchmarks) {
        $name = Get-BenchmarkKey $entry
        if ([string]::IsNullOrWhiteSpace($name)) {
            $display = [string]$entry.DisplayInfo
            Write-Host "[WARN] No baseline configured for benchmark '$display'"
            continue
        }

        $target = $baseline.Benchmarks.$name
        $meanNs = [double]$entry.Statistics.Mean
        $allocated = 0.0

        if ($entry.Memory -and $entry.Memory.BytesAllocatedPerOperation) {
            $allocated = [double]$entry.Memory.BytesAllocatedPerOperation
        }

        $meanBudget = [double]$target.MeanNs
        $allocBudget = [double]$target.AllocatedBytes

        $meanLimit = $meanBudget * (1 + $threshold / 100.0)
        $allocLimit = $allocBudget * (1 + $threshold / 100.0)

        if ($meanNs -gt $meanLimit) {
            Write-Host "[FAIL] $name mean exceeded: actual=$meanNs baseline=$meanBudget threshold=$threshold%"
            $failed = $true
        }

        if ($allocated -gt $allocLimit) {
            Write-Host "[FAIL] $name allocation exceeded: actual=$allocated baseline=$allocBudget threshold=$threshold%"
            $failed = $true
        }

        if ($meanNs -le $meanLimit -and $allocated -le $allocLimit) {
            Write-Host "[PASS] $name"
        }
    }
}

if ($failed) {
    exit 2
}

exit 0
