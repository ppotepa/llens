# Llens Bench TUI v0.1

## Summary

`v0.1` delivers the first usable benchmark console/TUI for comparing:
- Llens workflow
- Traditional workflow

on deterministic fixture tasks, with repeatable runs and exportable artifacts.

## Included

1. Workflow comparison benchmark scenario
- End-to-end deterministic task set for retrieval-style comparisons.
- Llens and baseline metrics captured per task.

2. Warm/cold run modes
- `--temperature warm|cold|both`

3. Repeat-run protocol
- `--repeats N` averaging in output.

4. Artifact export
- `--out <dir>` writes JSON and CSV.
- Includes timestamp and git commit hash.

5. CI smoke benchmark
- GitHub Actions workflow added:
  - `.github/workflows/bench-smoke.yml`
- Runs benchmark smoke on PR/push/workflow_dispatch.
- Uploads JSON/CSV artifacts.

6. Metric correctness fixes
- C# usage baseline filtering improved (declaration false positives removed).
- Cargo import benchmark scoring fixed to avoid false-pass on empty resolution.

## CLI examples

```bash
dotnet run --project Llens.Bench/Llens.Bench.csproj -- --filter Workflow --temperature both --repeats 3 --out Llens.Bench/out
```

```bash
dotnet run --project Llens.Bench/Llens.Bench.csproj -- --temperature warm
```

## v0.1 Scope Status

All `VERSION_0.1.md` release steps are complete.
