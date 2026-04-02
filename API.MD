# Llens API

Base URL: `http://localhost:<PORT>` (default from `launch.sh` is `5100`)

See also:
- [`COMPACT_API_GUIDE.md`](COMPACT_API_GUIDE.md) for the recommended low-token workflow with examples.

## Health

### `GET /health`
Returns service status and registered projects/languages.

## Projects

### `GET /api/projects`
List registered projects with configured languages and tool capabilities.

### `GET /api/projects/{name}/summary`
Project-level indexed summary.

Response shape:
- `name`
- `path`
- `totalFiles`
- `byLanguage[]` with `language`, `fileCount`, `totalSymbols`

## Symbols

### `GET /api/symbols/search?q=<text>&project=<name>&kind=<optional>`
Search symbols by name (case-insensitive contains). Optional `kind` filter.

### `GET /api/symbols/resolve?name=<exact>&project=<optional>`
Resolve exact symbol definitions by name.

### `GET /api/symbols/references?symbolId=<id>&project=<optional>`
Returns indexed usages for a symbol.

### `GET /api/symbols/implementors?name=<type>&project=<optional>`
Returns implementor candidates. Currently heuristic/partial.

### `GET /api/symbols/in-file?path=<absolute-path>`
All symbols for a file.
`path` can be absolute or project-relative. When using relative paths in
multi-project setups, pass `project=<name>` to avoid ambiguity.

## Files

### `GET /api/files/?project=<name>`
All indexed files for a project.

### `GET /api/files/node?path=<absolute-or-relative-path>&project=<optional>`
File metadata and imports.
`path` can be absolute or project-relative.

### `GET /api/files/dependents?path=<absolute-or-relative-path>&project=<optional>`
Files that import/reference this file (via file graph).
`path` can be absolute or project-relative.

### `GET /api/files/context?path=<absolute-or-relative-path>&line=<n>&radius=<n>&project=<optional>`
Source window around a line.
`path` can be absolute or project-relative.

## Compact FS

### `POST /api/compact/fs/tree`
Project tree snapshot. Supports optional subpath scoping:

```json
{
  "project": "shell-quest-tests",
  "path": "mods/asteroids",
  "maxDepth": 4,
  "maxEntries": 200
}
```

### `POST /api/compact/fs/read-range`
Read a line range from a single file.

### `POST /api/compact/fs/read-many`
Batch-read many files in one request.

Request:

```json
{
  "project": "shell-quest-tests",
  "paths": ["mods/asteroids/mod.yaml", "mods/asteroids/scenes/game/scene.yml"],
  "from": 1,
  "to": 4000,
  "maxFiles": 200,
  "format": "tuple"
}
```

Supported `format` values:
- omitted/default: object response with `files[]`
- `tuple`: compact tuple envelope
- `raw-lossless`: binary payload (lossless framing with per-file byte length + sha256 + exact file bytes)
- `raw-ordered`: ultra-lean lossless framing in request order (`B <len>` + exact bytes, no path/hash metadata)

Notes:
- `raw-lossless` is self-describing and independently verifiable per file.
- `raw-ordered` is the smallest lossless format for full reads; file identity is preserved by request order (`paths[i]` -> block `i`).

### `POST /api/compact/fs/read-spans`
Read selected line ranges from multiple files (metadata-first follow-up fetch).

Request:

```json
{
  "project": "shell-quest-tests",
  "spans": [
    { "path": "mods/asteroids/scenes/game/game-loop.rhai", "from": 220, "to": 320 },
    { "path": "mods/asteroids/catalogs/input-profiles.yaml", "from": 1, "to": 40 }
  ],
  "maxSpans": 120,
  "maxLinesPerSpan": 220,
  "format": "tuple"
}
```

## Recommended Low-Token Flow

Use this sequence for LLM tasks:

1. `POST /api/compact/resolve` (or `/api/compact/query`) to get candidates.
2. `POST /api/compact/fs/read-spans` for only the required ranges.
3. Use `POST /api/compact/fs/read-many` with:
   - `format: "raw-ordered"` for full-file bulk reads with minimum overhead.
   - `format: "raw-lossless"` when you need per-file integrity/hash metadata.

## Unified Query API (MVP)

### `POST /api/query`
Single entry point for:
- `snippet` (code portions across project)
- `fuzzy` (fuzzy symbol search)
- `references` (symbol usages)

Request body:

```json
{
  "project": "mixed-basic",
  "mode": "fuzzy",
  "query": "product service get by id",
  "filters": {
    "kind": ["Method", "Trait"],
    "pathPrefix": "src/"
  },
  "limit": 20,
  "snippetRadius": 6
}
```

Fields:
- `project` (required)
- `mode` (required): `snippet | fuzzy | references`
- `query` (required)
- `filters.kind` (optional): array of `SymbolKind` names
- `filters.pathPrefix` (optional): substring/prefix filter on file path
- `limit` (optional, default `20`, clamped `1..50`)
- `snippetRadius` (optional, default `6`, clamped `1..20`)

Response body:

```json
{
  "mode": "fuzzy",
  "project": "mixed-basic",
  "query": "product service",
  "summary": "Found 4 fuzzy symbol match(es).",
  "confidence": "high",
  "selectedSymbol": null,
  "symbols": [],
  "references": [],
  "snippets": [],
  "nextSuggestions": []
}
```

Response fields:
- `summary`: short human-readable result
- `confidence`: `low | medium | high`
- `selectedSymbol`: set in `references` mode when a primary target is resolved
- `symbols[]`: ranked symbol candidates
- `references[]`: usage matches (when available)
- `snippets[]`: source windows
- `nextSuggestions[]`: follow-up query hints

## Query Mode Semantics

### `mode=snippet`
Searches indexed project files for case-insensitive text matches and returns context windows.

### `mode=fuzzy`
Token-based fuzzy lookup over indexed symbols with ranking:
- exact name > prefix > contains
- token boosts for symbol name/signature/path

If no symbol matches are found, it falls back to snippet mode.

### `mode=references`
Resolves best symbol candidate from query, then fetches indexed references for that symbol.

Notes:
- If reference tracking has not been populated yet, `references` may be empty.
- Use `snippet` as a practical fallback for usage-like text searches.

## Graph Query API (UI + LLM)

### `POST /api/graph/query`
Builds a bounded graph around a selected seed (file or symbol).

Request body:

```json
{
  "project": "big-one",
  "seed": {
    "type": "file",
    "path": "/abs/path/to/src/main.rs"
  },
  "expand": {
    "edgeTypes": ["contains", "imports", "references"],
    "direction": "both",
    "depth": 2,
    "maxNodes": 120
  },
  "include": {
    "snippets": true,
    "radius": 6
  }
}
```

Request fields:
- `project` (required)
- `seed` (required)
- `seed.type`: `file | symbol`
- `seed.path`: required for file seed
- `seed.id` or `seed.name`: for symbol seed
- `seed.symbolKind` (optional): disambiguation hint for symbol seed
- `seed.pathHint` (optional): path substring hint for symbol seed
- `expand.edgeTypes`: subset of `contains | imports | references | callers | implements`
- `expand.direction`: `in | out | both`
- `expand.depth`: clamped `1..4`
- `expand.maxNodes`: clamped `10..400`
- `include.snippets`: include context pack snippets
- `include.radius`: snippet radius

Seed ID interoperability:
- File seed accepts either absolute file path (`seed.path`) or graph node id format (`seed.id = "file:/abs/path.rs"`).
- Symbol seed accepts raw symbol id (`repo:/abs/path.rs:Name:line`) or graph node id format (`seed.id = "symbol:repo:/abs/path.rs:Name:line"`).

Response body:

```json
{
  "project": "big-one",
  "seedId": "file:/abs/path/to/src/main.rs",
  "summary": { "nodeCount": 40, "edgeCount": 66, "depth": 2 },
  "nodes": [
    {
      "id": "file:/abs/path/to/src/main.rs",
      "type": "File",
      "label": "main.rs",
      "filePath": "/abs/path/to/src/main.rs",
      "distance": 0
    }
  ],
  "edges": [
    { "from": "file:...", "to": "symbol:...", "type": "contains" }
  ],
  "contextPack": {
    "topFiles": [],
    "topSymbols": [],
    "snippets": []
  }
}
```

Notes:
- Designed for both graph UI rendering and LLM context retrieval.
- `contextPack` gives a compact, ready-to-feed summary for downstream prompts.

### `POST /api/graph/expand`
Incremental expansion endpoint for graph UIs and tool loops.

Request:

```json
{
  "project": "big-one",
  "seed": { "type": "symbol", "id": "symbol:..." },
  "edgeTypes": ["contains", "imports", "references"],
  "direction": "both",
  "depth": 1,
  "maxNodes": 160,
  "excludeNodeIds": ["file:..."],
  "excludeEdgeIds": ["file:...|symbol:...|contains"],
  "page": { "offset": 0, "limit": 120 }
}
```

Response:
- `nodes[]`: newly returned nodes for this page
- `edges[]`: newly returned edges for this page
- `page`: pagination metadata (`hasMore`, `nextOffset`, `totalEdges`)

### `POST /api/graph/collapse`
Contract endpoint for branch collapse planning.

Request:

```json
{
  "project": "big-one",
  "seed": { "type": "file", "path": "/abs/path/to/file.rs" },
  "edgeTypes": ["contains", "imports"],
  "direction": "both",
  "depth": 1,
  "maxNodes": 160
}
```

Response:
- `removeNodeIds[]`
- `removeEdgeIds[]`

Useful for deterministic branch cleanup in client-side graph state.

## Context Pack API (LLM Ready)

### `POST /api/context-pack`
Builds a compact, ranked context payload with token-budget truncation.

Detail-level endpoint presets:
- `POST /api/context-pack/compact` => `format=compact`, `budgetProfile=low`
- `POST /api/context-pack/balanced` => `format=hybrid`, `budgetProfile=medium`
- `POST /api/context-pack/detailed` => `format=full`, `budgetProfile=high`

Request:

```json
{
  "project": "big-one",
  "mode": "implementation",
  "format": "hybrid",
  "budgetProfile": "medium",
  "query": "render terminal frame",
  "seed": { "type": "symbol", "id": "symbol:..." },
  "tokenBudget": 1200,
  "maxItems": 30,
  "snippetRadius": 6,
  "maxSnippetChars": 600,
  "maxSnippetLines": 12,
  "hybridSnippetLimit": 5
}
```

Fields:
- `mode`: `lookup | impact | implementation | bug-trace`
- `format`: `compact | hybrid | full` (default `full`)
- `budgetProfile` (optional): `low | medium | high`
- `query` and/or `seed` (at least one required)
- `tokenBudget`: hard cap for assembled context (estimated tokens)
- `maxItems`: max returned context items
- `maxSnippetChars` / `maxSnippetLines`: snippet truncation guards
- `hybridSnippetLimit`: max full snippets included when `format=hybrid`

Response:

```json
{
  "project": "big-one",
  "mode": "implementation",
  "format": "hybrid",
  "summary": "Assembled 18 context item(s) within token budget.",
  "confidence": "medium",
  "tokenBudget": 2200,
  "tokensUsed": 2134,
  "knownGaps": ["Reference coverage is partial for some languages/repositories."],
  "items": [
    {
      "id": "cp-7",
      "kind": "symbol",
      "symbolId": "big-one:...:render:81",
      "path": "/abs/path/to/file.rs",
      "line": 81,
      "title": "Function: render",
      "snippet": "...",
      "score": 142,
      "referenceCount": 9,
      "tokenEstimate": 96
    }
  ]
}
```

Cost control presets:
- `format=compact` + `budgetProfile=low` => typically smallest payload (~200-600 tokens).
- `format=hybrid` + `budgetProfile=medium` => balanced payload (~700-1400 tokens).
- `format=full` + `budgetProfile=high` => richest payload (~1500-2200+ tokens).

## Telemetry API

### `GET /api/telemetry`
Returns lightweight retrieval telemetry:
- total queries
- empty result count/rate
- fallback count/rate
- recent per-request events (`endpoint`, `mode`, `latencyMs`, `resultCount`, token estimate)

## Compact API Namespace

Token-efficient, minimal-shape endpoints for development loops:
- `POST /api/compact/query`
- `POST /api/compact/resolve`
- `POST /api/compact/context-pack`
- `POST /api/compact/graph`
- `POST /api/compact/references-tree`
- `POST /api/compact/regex`
- `POST /api/compact/rg`
- `POST /api/compact/replace-plan`
- `POST /api/compact/fs/tree`
- `POST /api/compact/fs/read-range`
- `POST /api/compact/fs/write-file`
- `POST /api/compact/fs/edit`
- `POST /api/compact/fs/write-patch`
- `POST /api/compact/fs/diff`
- `POST /api/compact/git/history`
- `POST /api/compact/git/commit`
- `POST /api/compact/git/patch`
- `POST /api/compact/git/status`
- `POST /api/compact/deps`
- `POST /api/compact/diagnostics`
- `POST /api/compact/test/map`
- `POST /api/compact/test/run`
- `POST /api/compact/session/plan`
- `GET /api/compact/session/plan?sessionId=...`
- `POST /api/compact/js/check`
- `POST /api/compact/format`
- `POST /api/compact/lint`
- `POST /api/compact/typecheck`
- `POST /api/compact/reindex`
- `POST /api/compact/dev/serve`
- `POST /api/compact/dev/serve/stop`
- `POST /api/compact/quality/guard`
- `POST /api/compact/semantic-search`
- `POST /api/compact/refactor/rename-plan`
- `GET /api/compact/schema`
- `POST /api/compact/workflow/run`

## CLI Mode (Shared Services)

Run compact operations through CLI arguments (no HTTP payload construction):

```bash
dotnet run -- cli --help
dotnet run -- cli js.check --project example-app --path scripts
```

Current command coverage:
- `js.check`
- `fs.tree`
- `fs.read-range`
- `fs.write-file`
- `fs.edit`
- `fs.diff`
- `git.status`
- `reindex`
- `test.run`
- `format`
- `lint`
- `typecheck`

Design notes:
- API endpoints and CLI commands call shared application services.
- CLI uses attribute metadata for command/arg discovery.
- Required vs optional follows CLR nullability:
  - non-nullable value type (for example `int`) => required
  - nullable value type (for example `int?`) => optional
  - non-nullable reference type (for example `string`) => required
  - nullable reference type (for example `string?`) => optional

Common compact output style:
- short keys
- stable ids
- no large snippets by default

Example (`/api/compact/query`):

```json
{
  "project": "big-one",
  "mode": "fuzzy",
  "q": "renderer loop state",
  "count": 12,
  "tokens": 380,
  "items": [
    { "id":"symbol:...", "t":"s", "n":"render_frame", "p":"/abs/path.rs", "l":91, "k":"Function", "sc":128 }
  ]
}
```

Tuple mode (token-optimized) is available on:
- `POST /api/compact/query` with `"format":"tuple"`
- `POST /api/compact/context-pack` with `"format":"tuple"`
- `POST /api/compact/git/status` with `"format":"tuple"`
- `POST /api/compact/rg` with `"format":"tuple"`

Resolver endpoint:
- `/api/compact/resolve` runs deterministic fallback chain:
  - `exact -> fuzzy -> snippet`
- Response includes `stage` so orchestrators know which retrieval stage produced results.

Delta compact context-pack:
- `/api/compact/context-pack` accepts `previousIds[]`
- Returns only items not already present in previous compact result sets.

## Compact Ops Quick Reference

### `POST /api/compact/regex`
Regex scan over indexed project files (bounded by `maxFiles` / `maxMatches`).

```json
{
  "project": "llens",
  "pattern": "MapCompact",
  "pathPrefix": "/home/ppotepa/git/llens/Api",
  "ignoreCase": true,
  "maxFiles": 300,
  "maxMatches": 250
}
```

### `POST /api/compact/rg`
Searches indexed files in `regex` or `literal` mode with compact, line-oriented hits.
`pathPrefix` accepts full path or project-relative prefix.

```json
{
  "project": "llens",
  "pattern": "MapPost\\(\"/rg\"",
  "mode": "regex",
  "pathPrefix": "Api",
  "ignoreCase": true,
  "maxFiles": 300,
  "maxMatches": 300,
  "maxLineLength": 220,
  "format": "tuple"
}
```

### `POST /api/compact/replace-plan`
Plans literal/regex replacements without editing files.

```json
{
  "project": "llens",
  "find": "OldName",
  "replace": "NewName",
  "mode": "literal",
  "pathPrefix": "/home/ppotepa/git/llens",
  "maxMatches": 400
}
```

### `POST /api/compact/fs/tree`
Directory/file tree snapshot for a project subtree.

### `POST /api/compact/fs/read-range`
Reads a bounded line range from a file in project root.

### `POST /api/compact/fs/write-file`
Creates or overwrites a file in project root (language-agnostic).

### `POST /api/compact/fs/edit`
Applies targeted partial edits (line/snippet operations) to avoid full-file rewrites.

Supported op `type` values:
- `replace_range` (`startLine`, `endLine`, `content`)
- `insert_before` (`startLine`, `content`)
- `insert_after` (`startLine`, `content`)
- `delete_range` (`startLine`, `endLine`)
- `replace_snippet` (`find`, `content`, optional `occurrence`; `<=0` means all)

### `POST /api/compact/fs/write-patch`
Applies or dry-runs patch operations (`literal` or `regex`) on files in project root.

### `POST /api/compact/fs/diff`
Returns git diff text for project root or a specific path.

### `POST /api/compact/git/history`
Efficient commit history:
- `mode=meta`: commit metadata + changed files (cheapest)
- `mode=compact`: adds hunk headers without full patch bodies

### `POST /api/compact/git/commit`
Single-commit detail in `meta|compact` mode.

### `POST /api/compact/git/patch`
Full patch text on demand (`maxChars` bounded).

### `POST /api/compact/git/status`
Working tree status with token-efficient mode:
- `mode=compact` (default): returns preformatted compact lines
- `mode=full`: returns structured entries + compact lines

### `POST /api/compact/deps`
Dependency edges for seed:
- file seed: imports + reverse dependents
- symbol seed: reference edges

### `POST /api/compact/diagnostics`
Runs project checks (`dotnet build` or `cargo check`) and returns parsed diagnostics.
Supported parser formats:
- C#: `path(line,col): error CODE: message`
- TS/JS: `path:line:col - error TSxxxx: message`
- Rust: `error...` with `--> path:line:col`

### `POST /api/compact/test/map`
Finds tests and related test files for a query or seed.

### `POST/GET /api/compact/session/plan`
Lightweight in-memory session plan store (`sessionId`, `goal`, `state`, `steps[]`).

### `POST /api/compact/js/check`
Syntax-checks JavaScript files via `node --check` (single file, directory, or whole project).

### `POST /api/compact/dev/serve`
Starts a lightweight static server for a project path and returns URL/process metadata.

### `POST /api/compact/dev/serve/stop`
Stops a running dev server by `serverId`.

### `POST /api/compact/quality/guard`
Returns quick guardrails/warnings for retrieval workflow setup.

### `POST /api/compact/semantic-search`
Token-weighted semantic-like search over symbol names/signatures/paths.

### `POST /api/compact/refactor/rename-plan`
Definition + reference candidate list for safe rename planning.

### `GET /api/compact/schema`
Lists compact namespace endpoints and expected request shapes.

### `POST /api/compact/workflow/run`
Single-call retrieval pipeline:
- resolve (`exact -> fuzzy -> snippet`)
- context-pack
- optional graph expansion

## LLM Usage Playbook

Recommended deterministic retrieval sequence:
1. `POST /api/query` in `mode=fuzzy` for initial symbol candidates.
2. `POST /api/graph/query` from best seed (`file` or `symbol`) with small depth (`1-2`) and bounded `maxNodes`.
3. `POST /api/graph/expand` iteratively using `excludeNodeIds`/`excludeEdgeIds` and pagination while more context is needed.
4. `POST /api/graph/collapse` when branch cleanup is needed in graph state.
5. `GET /api/files/context` for final line-window snippets before prompt assembly.

## Common Operations (Copy/Paste)

### 1) Check service and projects

```bash
curl -sS http://localhost:5100/health | jq
```

### 2) Fast symbol lookup (compact object mode)

```bash
curl -sS -X POST http://localhost:5100/api/compact/query \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"llens",
    "mode":"fuzzy",
    "q":"CompactFsService",
    "limit":10
  }' | jq
```

### 3) Fast symbol lookup (tuple mode, lower token payload)

```bash
curl -sS -X POST http://localhost:5100/api/compact/query \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"llens",
    "mode":"fuzzy",
    "q":"CompactFsService",
    "limit":10,
    "format":"tuple"
  }' | jq
```

### 4) Deterministic resolve chain (`exact -> fuzzy -> snippet`)

```bash
curl -sS -X POST http://localhost:5100/api/compact/resolve \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"llens",
    "q":"MapCompactRoutes",
    "limit":8
  }' | jq
```

### 5) Build compact context for LLM (with dedupe via `previousIds`)

```bash
curl -sS -X POST http://localhost:5100/api/compact/context-pack \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"llens",
    "mode":"fuzzy",
    "q":"graph expand pagination",
    "tokenBudget":700,
    "maxItems":15,
    "previousIds":[]
  }' | jq
```

Tuple variant:

```bash
curl -sS -X POST http://localhost:5100/api/compact/context-pack \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"llens",
    "mode":"fuzzy",
    "q":"graph expand pagination",
    "tokenBudget":700,
    "maxItems":15,
    "format":"tuple"
  }' | jq
```

### 6) Read a file range

```bash
curl -sS -X POST http://localhost:5100/api/compact/fs/read-range \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"example-app",
    "path":"scripts/nav.js",
    "from":1,
    "to":80
  }' | jq
```

### 7) Edit a file portion (no full-file rewrite)

Dry-run:

```bash
curl -sS -X POST http://localhost:5100/api/compact/fs/edit \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"example-app",
    "dryRun":true,
    "operations":[
      {
        "path":"index.html",
        "type":"replace_snippet",
        "find":"Start Here",
        "content":"Start Here (Updated)",
        "occurrence":1
      },
      {
        "path":"index.html",
        "type":"insert_after",
        "startLine":24,
        "content":"      <p class=\"muted\">Edited via fs.edit</p>"
      }
    ]
  }' | jq
```

Apply:

```bash
curl -sS -X POST http://localhost:5100/api/compact/fs/edit \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"example-app",
    "dryRun":false,
    "operations":[
      {
        "path":"index.html",
        "type":"replace_snippet",
        "find":"Start Here",
        "content":"Start Here (Updated)",
        "occurrence":1
      }
    ]
  }' | jq
```

### 8) Get git status (compact, object mode)

```bash
curl -sS -X POST http://localhost:5100/api/compact/git/status \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"example-app",
    "mode":"compact",
    "maxEntries":50
  }' | jq
```

Tuple variant:

```bash
curl -sS -X POST http://localhost:5100/api/compact/git/status \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"example-app",
    "mode":"compact",
    "maxEntries":50,
    "format":"tuple"
  }' | jq
```

### 9) JavaScript syntax check for a project directory

```bash
curl -sS -X POST http://localhost:5100/api/compact/js/check \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"example-app",
    "path":"scripts"
  }' | jq
```

### 10) Search project text with compact `rg` (tuple mode)

```bash
curl -sS -X POST http://localhost:5100/api/compact/rg \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"llens",
    "pattern":"CompactFsService",
    "mode":"literal",
    "pathPrefix":"Api",
    "maxMatches":80,
    "format":"tuple"
  }' | jq
```

### 11) Reindex a project, file, or directory

Project-wide refresh with stale-cache pruning:

```bash
curl -sS -X POST http://localhost:5100/api/compact/reindex \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"llens",
    "pruneStale":true
  }' | jq
```

Directory-scoped refresh:

```bash
curl -sS -X POST http://localhost:5100/api/compact/reindex \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"example-app",
    "path":"scripts",
    "pruneStale":true,
    "maxFiles":200
  }' | jq
```

### 11) Run tests with auto-detected backend

```bash
curl -sS -X POST http://localhost:5100/api/compact/test/run \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"rust-basic",
    "target":"cargo",
    "timeoutMs":120000
  }' | jq
```

### 12) Check formatting without writing files

```bash
curl -sS -X POST http://localhost:5100/api/compact/format \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"rust-basic",
    "target":"cargo",
    "checkOnly":true
  }' | jq
```

### 13) Run lint/analyzer checks

```bash
curl -sS -X POST http://localhost:5100/api/compact/lint \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"rust-basic",
    "target":"cargo",
    "timeoutMs":120000
  }' | jq
```

### 14) Run typecheck/build-check

```bash
curl -sS -X POST http://localhost:5100/api/compact/typecheck \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"llens",
    "target":"dotnet",
    "timeoutMs":120000
  }' | jq
```

### 15) Start and stop static preview server

Start:

```bash
curl -sS -X POST http://localhost:5100/api/compact/dev/serve \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"example-app",
    "path":".",
    "port":5132,
    "serverId":"example-app-dev"
  }' | jq
```

Stop:

```bash
curl -sS -X POST http://localhost:5100/api/compact/dev/serve/stop \
  -H 'Content-Type: application/json' \
  -d '{"serverId":"example-app-dev"}' | jq
```

### 16) CLI equivalents (shared services, no curl payloads)

```bash
dotnet run --no-build -- cli js.check --project example-app --path scripts
dotnet run --no-build -- cli reindex --project llens --prune-stale true
dotnet run --no-build -- cli test.run --project rust-basic --target cargo
dotnet run --no-build -- cli format --project rust-basic --target cargo --check-only true
dotnet run --no-build -- cli lint --project rust-basic --target cargo
dotnet run --no-build -- cli typecheck --project llens --target dotnet
dotnet run --no-build -- cli fs.read-range --project example-app --path scripts/nav.js --from 1 --to 60
dotnet run --no-build -- cli git.status --project example-app --mode compact --format tuple --max-entries 20
dotnet run --no-build -- cli fs.diff --project llens --path Program.cs --max-chars 5000
```

## Curl Examples

### Snippet search

```bash
curl -sS -X POST http://localhost:5100/api/query \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"csharp-basic",
    "mode":"snippet",
    "query":"ApplyDiscount",
    "limit":10,
    "snippetRadius":8
  }'
```

### Fuzzy search

```bash
curl -sS -X POST http://localhost:5100/api/query \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"mixed-basic",
    "mode":"fuzzy",
    "query":"product service get by id",
    "filters":{"kind":["Method","Trait"]},
    "limit":20
  }'
```

### References lookup

```bash
curl -sS -X POST http://localhost:5100/api/query \
  -H 'Content-Type: application/json' \
  -d '{
    "project":"csharp-basic",
    "mode":"references",
    "query":"GetDiscountedTotal",
    "limit":20
  }'
```

### API smoke checks

```bash
chmod +x tools/smoke-api.sh
./tools/smoke-api.sh http://localhost:5009 big-one
```

### Compact deps smoke checks

```bash
chmod +x tools/smoke-compact-deps.sh
./tools/smoke-compact-deps.sh http://localhost:5100 llens
```

### Compact ops full smoke checks

```bash
chmod +x tools/smoke-compact-ops.sh
./tools/smoke-compact-ops.sh http://localhost:5100 llens
```

### Integration tests (core compact flow)

```bash
dotnet test Llens.Tests/Llens.Tests.csproj -v minimal
```

### Retrieval benchmark

```bash
chmod +x tools/benchmark-retrieval.sh
./tools/benchmark-retrieval.sh http://localhost:5009 big-one "main loop renderer state" 20
```

## Important Current Limitations

- `/api/codemap/*` endpoints exist in code but are not currently mapped in `Program.cs`.
- Reference indexing is modeled, but cross-file reference population is partial/incomplete.
- Default cache backend is in-memory (`InMemoryCodeMapCache`); restart clears indexed state.
