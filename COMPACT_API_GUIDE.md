# Compact API Guide

This guide focuses on the newer compact endpoints intended for low-token LLM workflows.

## Goals

- Read only what you need (spans before full files).
- Prefer tuple payloads for JSON responses.
- Use raw binary modes when you need lossless full-file transfer.
- Keep file paths unambiguous across projects.

## Path Resolution Rules

Applies to:
- `GET /api/symbols/in-file`
- `GET /api/files/node`
- `GET /api/files/dependents`
- `GET /api/files/context`

Rules:
- `path` may be absolute or project-relative.
- If you use relative paths and multiple projects are indexed, pass `project`.
- If `project` is omitted and the relative suffix matches multiple files, the API returns an ambiguity error.

Example:

```bash
curl -sS "http://localhost:5100/api/files/node?path=engine/src/lib.rs&project=shell-quest-tests" | jq
```

## Compact FS Endpoints

## 1) Tree Snapshot

Endpoint:
- `POST /api/compact/fs/tree`

Use this for shallow discovery before reads.

```json
{
  "project": "shell-quest-tests",
  "path": "mods/shell-quest-tests",
  "maxDepth": 3,
  "maxEntries": 300
}
```

## 2) Read One File Range

Endpoint:
- `POST /api/compact/fs/read-range`

Use this when you already know the target file.

## 3) Read Many Files

Endpoint:
- `POST /api/compact/fs/read-many`

Formats:
- default/object: `{"project","count","files":[...]}`
- `tuple`: token-optimized JSON envelope
- `raw-lossless`: binary blocks with metadata (path/hash/length + exact bytes)
- `raw-ordered`: minimal binary blocks in request order (`paths[i] -> block i`)

Example tuple request:

```json
{
  "project": "shell-quest-tests",
  "paths": [
    "mods/shell-quest-tests/mod.yaml",
    "mods/shell-quest-tests/scenes/stress/scene.yml"
  ],
  "from": 1,
  "to": 2000,
  "format": "tuple"
}
```

## 4) Read Sparse Spans

Endpoint:
- `POST /api/compact/fs/read-spans`

Use this after symbol/query/search steps to fetch only relevant windows.

```json
{
  "project": "shell-quest-tests",
  "spans": [
    { "path": "engine/src/behavior.rs", "from": 180, "to": 320 },
    { "path": "engine-core/src/scene/sprite.rs", "from": 1, "to": 120 }
  ],
  "maxSpans": 120,
  "maxLinesPerSpan": 220,
  "format": "tuple"
}
```

## Compact Search Endpoint

Endpoint:
- `POST /api/compact/rg`

Modes:
- `regex`
- `literal`

Useful fields:
- `pattern`
- `pathPrefix` (full or project-relative prefix)
- `ignoreCase`
- `maxFiles`, `maxMatches`, `maxLineLength`
- `format: "tuple"` for compact results

Example:

```json
{
  "project": "shell-quest-tests",
  "pattern": "BehaviorContext",
  "mode": "literal",
  "pathPrefix": "engine",
  "maxMatches": 80,
  "format": "tuple"
}
```

## Recommended Workflow

For low-token coding loops:

1. Use `POST /api/compact/resolve` or `POST /api/compact/query` to find likely files/symbols.
2. Use `POST /api/compact/rg` to pin exact candidate lines.
3. Use `POST /api/compact/fs/read-spans` to fetch only required ranges.
4. Only if needed, use `POST /api/compact/fs/read-many` for full-file retrieval.
5. Pick format:
   - `tuple` for compact JSON consumption.
   - `raw-ordered` when full-file transfer size matters most.
   - `raw-lossless` when per-file integrity metadata is needed.

## Reindexing Notes

`POST /api/compact/reindex` now supports project/file/directory scopes through shared reindex service logic.

Use scoped reindex after targeted file operations:

```json
{
  "project": "shell-quest-tests",
  "path": "engine/src",
  "pruneStale": true,
  "maxFiles": 5000
}
```
