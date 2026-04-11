# Llens Bench TUI - Version 0.11 Plan

## Goal

Add benchmark mode focused on consumed-token comparison:
- `X = Llens workflow`
- `Y = Traditional workflow`

using deterministic repo-history fixtures that are large enough to stress retrieval behavior.

## Scope

1. Synthetic repo fixture generation
- Deterministic generator for:
  - ~100 commits
  - ~100 files
  - realistic mixed-language file set
  - evolving commit history (add/modify/rename)

2. History-aware benchmark tasks
- Add tasks that require commit/file-history context.
- Include fixed expected answers for deterministic scoring.

3. Token-focused output
- Promote consumed tokens as first-class metric in TUI and exported artifacts.
- Report X vs Y deltas by task and aggregate.

4. CI-ready fixture flow
- Fixture generated from seed in CI.
- No network dependency for core smoke runs.

## Delivery Steps

1. Add fixture generator command to `Llens.Bench`.
2. Define fixture schema and generation manifest.
3. Add benchmark task pack format for history-aware tasks.
4. Add history-aware scenarios to TUI output.
5. Add CI smoke variant that generates fixture then runs comparison.
6. Validate determinism and release `0.11`.

## Acceptance Criteria

1. Running generator twice with same seed produces equivalent history shape.
2. Bench runs on generated fixture without manual setup.
3. TUI shows X vs Y token deltas per task.
4. JSON/CSV artifacts include fixture seed and generation metadata.

## Current Status (2026-04-11)

- Completed:
  - Step 1: fixture generator command added (`--repo-gen`)
  - Step 2: fixture manifest emitted (`llens-bench-fixture.json`)
- In progress:
  - Step 3+: task-pack format + history-aware scenarios against generated fixture
