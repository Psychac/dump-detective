# Schema Versioning

DumpDetective report and snapshot formats use a simple semantic contract.

## Current schema version

- Report schema: `2.1`

## Compatibility rules

- Increment the **major** version when a change breaks backward compatibility for saved reports or trend snapshots.
- Increment the **minor** version when adding fields or sections that are backward compatible.
- Patch-level changes are not tracked in the schema string.

## Read policy

- Readers must reject saved data when the major version does not match the version they understand.
- Readers may accept older minor versions when missing fields have safe defaults.
- When a reader loads a report or snapshot, it should treat the schema version as a contract check before attempting trend comparison.

## Practical guidance

- Do not remove or rename persisted fields without a major version bump.
- Prefer additive changes for new report sections, trend fields, or metadata.
- If snapshot serialization is introduced later, persist the schema version alongside the payload and validate it before merging historical runs.