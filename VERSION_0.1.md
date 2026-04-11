# Llens Bench TUI - Version 0.1 Release Plan

## Purpose

Ship a usable, reproducible benchmark tool that compares:
- Llens workflow
- Traditional workflow

for the same tasks under the same constraints.

## In Scope (Must Have)

1. Dual benchmark mode
- Run both Llens and Traditional pipelines on identical scenarios.

2. Fixed scenario pack
- At least 10 deterministic tasks across:
  - symbol lookup
  - reference tracing
  - context assembly
  - small change/patch guidance

3. Fairness controls
- Same model configuration (when model is involved)
- Same token/time/tool-call limits
- Same repo snapshot
- Explicit warm vs cold run mode

4. Core metrics
- Success rate
- End-to-end latency (mean, p95)
- Estimated tokens
- Tool/API call count
- Optional cost estimate (if pricing config provided)

5. TUI minimum UX
- Live run progress view
- Results table with Llens vs Traditional and deltas
- Per-scenario drilldown with failure reason and key stats

6. Export and reproducibility
- Save JSON and CSV for each run
- Include timestamp and git commit hash
- Support config file-driven runs

7. Quality gate
- Benchmark project tests pass
- Smoke benchmark runnable in CI
- No known metric correctness bugs in pass/fail logic

## Nice to Have (If Time Allows)

1. Historical comparison vs last run
2. Regression threshold checks with non-zero exit on degradation
3. Markdown summary report generation

## Out of Scope (v0.1)

1. Perfect simulation of full agentic coding behavior
2. Multi-model comparison matrix
3. Advanced UI polish beyond readability and reliability

## Release Exit Criteria

v0.1 is releasable when all are true:

1. One-command local run produces deterministic, exportable results.
2. CI can run a smoke suite and publish artifacts.
3. Scenario-level outcomes are explainable (not only aggregate score).
4. At least 5 repeated runs show acceptable variance.
5. Team can clearly answer:
   - where Llens wins
   - where it loses
   - by how much

## Step-by-Step Execution Plan

1. Freeze scope and metrics in this document.
2. Fix current benchmark correctness issues.
3. Introduce workflow scenarios (Llens vs Traditional).
4. Add warm/cold mode and repeat-run protocol.
5. Add exports (JSON/CSV) and commit metadata.
6. Add CI smoke benchmark.
7. Run release candidate validation and publish v0.1.

## Implementation Status (2026-04-11)

- Completed:
  - Scope freeze document (step 1)
  - Benchmark correctness fixes (step 2)
  - Workflow comparison scenario in TUI (step 3)
  - Repeat-run support via `--repeats` (step 4)
  - Warm/cold execution modes via `--temperature warm|cold|both` (step 4)
  - JSON/CSV export with commit hash via `--out` (step 5)
  - CI smoke benchmark workflow with artifact upload (step 6)
  - Release candidate validation pass (step 7)
- Remaining:
  - None for v0.1 scope
