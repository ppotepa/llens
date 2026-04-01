# Llens API

Base URL: `http://localhost:<PORT>` (default from `launch.sh` is `5100`)

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

## Files

### `GET /api/files/?project=<name>`
All indexed files for a project.

### `GET /api/files/node?path=<absolute-path>`
File metadata and imports.

### `GET /api/files/dependents?path=<absolute-path>&project=<optional>`
Files that import/reference this file (via file graph).

### `GET /api/files/context?path=<absolute-path>&line=<n>&radius=<n>`
Source window around a line.

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
- `POST /api/compact/replace-plan`
- `POST /api/compact/fs/tree`
- `POST /api/compact/fs/read-range`
- `POST /api/compact/fs/write-file`
- `POST /api/compact/fs/write-patch`
- `POST /api/compact/fs/diff`
- `POST /api/compact/deps`
- `POST /api/compact/diagnostics`
- `POST /api/compact/test/map`
- `POST /api/compact/session/plan`
- `GET /api/compact/session/plan?sessionId=...`
- `POST /api/compact/quality/guard`
- `POST /api/compact/semantic-search`
- `POST /api/compact/refactor/rename-plan`
- `GET /api/compact/schema`
- `POST /api/compact/workflow/run`

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

### `POST /api/compact/fs/write-patch`
Applies or dry-runs patch operations (`literal` or `regex`) on files in project root.

### `POST /api/compact/fs/diff`
Returns git diff text for project root or a specific path.

### `POST /api/compact/deps`
Dependency edges for seed:
- file seed: imports + reverse dependents
- symbol seed: reference edges

### `POST /api/compact/diagnostics`
Runs project checks (`dotnet build` or `cargo check`) and returns parsed diagnostics.

### `POST /api/compact/test/map`
Finds tests and related test files for a query or seed.

### `POST/GET /api/compact/session/plan`
Lightweight in-memory session plan store (`sessionId`, `goal`, `state`, `steps[]`).

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
