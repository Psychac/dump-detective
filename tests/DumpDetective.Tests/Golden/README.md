# Golden/Snapshot Tests for DumpDetective.Reporting

This directory contains snapshot/golden tests for the DumpDetective.Reporting module. These tests capture the expected output shapes and validate that changes to the reporting pipeline don't inadvertently break serialization, document composition, or payload generation.

## Overview

Three categories of snapshot tests are implemented:

### 1. ReportSerializer Snapshots (`ReportSerializerSnapshotTests.cs`)

Tests the canonical document serialization pipeline, verifying:
- Document shape consistency (all expected fields present)
- Section assembly ordering and categorization  
- Finding projection completeness (all domain results captured)
- Metadata preservation across serialization

**Baseline files**: `Baselines/ReportSerializer/`

### 2. TrendReportComposer Snapshots (`TrendReportComposerSnapshotTests.cs`)

Tests trend document composition, verifying:
- Trend-specific sections (health scorecard, snapshot strip, timeline, regression dashboard, appendix)
- Per-dump projection shape (full single-dump structure embedded in trend context)
- Cross-dump aggregation (comparison sections, story narratives)
- Intentional per-dump rebuild cost (O(N) compositions for N dumps)

**Baseline files**: `Baselines/TrendReportComposer/`

### 3. HtmlReportRenderer Snapshots (`HtmlReportRendererSnapshotTests.cs`)

Tests HTML payload generation, verifying:
- Embedded resource bundling (CSS, JS inlined for offline capability)
- CSS/JS validity and completeness
- Single-dump and trend-specific payload structures
- Graceful degradation without JavaScript
- Payload size within expectations

**Baseline files**: `Baselines/HtmlReportRenderer/`

## Usage

### Running the Tests

```bash
dotnet test tests/DumpDetective.Tests/DumpDetective.Tests.csproj -k "SnapshotTests"
```

### Creating / Updating Baselines

When a snapshot test runs for the first time:
1. The test creates the baseline file in `Baselines/`
2. Review the generated baseline carefully
3. Commit the baseline to version control
4. Future test runs compare against the committed baseline

If an intentional change affects the output shape:
1. Delete the baseline file
2. Re-run the test to regenerate it
3. Review and commit the new baseline

### Diff Output

If a baseline mismatch is detected:
1. A `.diff` file is written to `Baselines/` showing expected vs actual
2. Review the diff to understand what changed
3. If the change is intentional, delete the baseline and regenerate
4. If unintentional, fix the code and re-run

## Integration with CI/CD

These tests should be part of the standard CI pipeline:
- Runs on every PR
- Fails if baseline mismatches detected (unless baseline is intentionally updated)
- Ensures serialization changes are conscious and documented

## Adding New Snapshots

To add a new snapshot test:

1. Create a test method in the appropriate test class
2. Call `ApproveGoldenOutput(actualOutput, goldenFileName)` with:
   - `actualOutput`: the JSON or string representation to snapshot
   - `goldenFileName`: descriptive name for the baseline file
3. Run the test to generate the baseline
4. Review and commit the baseline file

Example:
```csharp
[Fact]
public void MyNewSnapshot_DescribesFeature()
{
    var testName = nameof(MyNewSnapshot_DescribesFeature);
    var output = SerializeToJson(myObject);
    ApproveGoldenOutput(output, testName);
}
```

## Notes

- Baseline files use `.golden` extension for easy identification
- All baselines are stored in version control for traceability
- Snapshot tests serve as both validation and living documentation of output format contracts
- Use `SerializeToJson()` helper for consistent JSON formatting (indented, camelCase)
