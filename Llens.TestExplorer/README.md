# Llens Test Explorer (Windows GUI)

Windows GUI for running `Llens.Bench` and exploring token-compression results.

## Run

```powershell
dotnet run --project Llens.TestExplorer/Llens.TestExplorer.csproj
```

## What it does

- Runs benchmark commands (`dotnet run --project Llens.Bench -- ...`)
- Always writes SQL run data (`--sqlite`) for traceability
- Single-screen explorer based on the maquette in [`docs/maquette.window.xml`](/D:/Git/llens/docs/maquette.window.xml):
  - left: catalog tree (`goal`, `tooling`, `language`, `smart views`)
  - center: dominant scenario list for scan/sort/multi-select
  - right: selected-test summary with always-visible `classic`, `compact`, `hybrid` cards
  - bottom: local tabs for the selected testcase only: `Compare`, `Trace`, `Raw IO`, `SQL History`, `Run Log`
- Run actions:
  - `Run All`
  - `Run Checked` (uses checked rows to create a temporary subset task-pack)
  - `Load Latest` (from SQLite)
- Search and category filtering for fast triage
- Sanitized run log with ANSI stripping and repeated-line collapsing
- Human-readable scenario names:
  - optional `title` attribute in the task pack
  - fallback derived names when `title` is missing
- Interaction model:
  - click a row to inspect one testcase
  - check rows for batch execution
- Smart views:
  - `Savings Winners`
  - `Regressions`
  - `Flaky` (computed from SQL history)

## SQL Reporting

`Llens.Bench` now supports:

```powershell
--sqlite <path-to-db>
```

Example:

```powershell
dotnet run --project Llens.Bench -- --history-only --tasks Llens.Bench/TaskPacks/agent-rust-csharp-history-100.tasks.json --out Llens.Bench/out-gui --sqlite Llens.Bench/out-gui/bench-results.db
```

Tables created:

- `benchmark_runs`
- `benchmark_results`
- `benchmark_mode_results`
- `benchmark_steps`
