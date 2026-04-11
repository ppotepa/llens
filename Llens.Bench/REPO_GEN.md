# Synthetic Repo Generator

Use the benchmark CLI to generate a deterministic Git-history fixture repository.

## Command

```bash
dotnet run --project Llens.Bench/Llens.Bench.csproj -- --repo-gen --output Llens.Bench/generated/synth-repo --commits 100 --files 100 --seed 42 --force
```

## Flags

- `--repo-gen` enable generator mode
- `--output <path>` output directory
- `--commits <n>` number of commits (default `100`)
- `--files <n>` target number of files to create (default `100`)
- `--seed <n>` deterministic seed (default `42`)
- `--force` overwrite output directory if non-empty

## Output

Generator creates:
- Git repository with synthetic development history
- Mixed file types under `src/`, `docs/`, and `configs/`
- `llens-bench-fixture.json` manifest with metadata (seed, commit count, head, files)

## Running History Task Pack Benchmarks

Default history task pack:
- `Llens.Bench/TaskPacks/synthetic-history.tasks.json`

Run:

```bash
dotnet run --project Llens.Bench/Llens.Bench.csproj -- --filter History --repo Llens.Bench/generated/synth-repo-100 --tasks Llens.Bench/TaskPacks/synthetic-history.tasks.json --temperature both --repeats 2 --out Llens.Bench/out-history
```
