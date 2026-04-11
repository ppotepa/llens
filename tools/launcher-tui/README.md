# Llens Launcher TUI

Simple Rust terminal launcher for common project commands.

## Run

From repo root:

```bash
cargo run --manifest-path tools/launcher-tui/Cargo.toml
```

## Keys

- `Up` / `Down`: select task
- `Enter`: launch selected task
- `k`: stop running task
- `c`: clear output pane
- `q`: quit

## Task Config

Tasks are loaded from:

`tools/launcher-tui/launcher.tasks.json`

Each task supports:

- `name`
- `description`
- `program`
- `args` (array)
- `cwd` (optional, relative to repo root)
- `env` (optional object)
