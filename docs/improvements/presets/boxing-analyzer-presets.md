# BoxingAnalyzer — Presets

Purpose: find boxed value-type instances and surface struct-padding / oversized value types.

Options observed in code:
- `TypeScanCap` (int) — cap on MethodTable lookups during the type-aggregate scan.
- `TopBoxedTypeLimit` (int) — how many boxed types to include in the Top list.
- `TopPaddingLimit` (int) — how many struct-padding candidates to report.
- `OversizedThresholdBytes` (int) — threshold for considering a value type "oversized".

Fast:
- `TypeScanCap`: 5_000
- `TopBoxedTypeLimit`: 10
- `TopPaddingLimit`: 10
- `OversizedThresholdBytes`: 96

Balanced (default):
- `TypeScanCap`: 10_000
- `TopBoxedTypeLimit`: 20
- `TopPaddingLimit`: 20
- `OversizedThresholdBytes`: 64

Full:
- `TypeScanCap`: 50_000
- `TopBoxedTypeLimit`: 50
- `TopPaddingLimit`: 50
- `OversizedThresholdBytes`: 48

Flow notes:
- `TypeScanCap` bounds metadata lookups (ClrType resolution) — raising it improves coverage but increases runtime.
- `OversizedThresholdBytes` tunes sensitivity for large value types; lower values surface smaller oversized structs.

Rationale — when to pick each preset:
- **Fast:** reduce `TypeScanCap` and Top limits to minimize MT lookups and avoid expensive type-shape reads when triaging large dumps.
- **Balanced:** (default) moderate caps that surface the top boxed types and padding candidates without scanning the entire type set.
- **Full:** increase `TypeScanCap` and top limits to maximize coverage for deep audits of value-type boxing and padding.

Next steps:
- Document recommended `OversizedThresholdBytes` choices for common runtimes (e.g., 64/80/96 bytes) in the presets README.
