#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${1:-http://localhost:5009}"
PROJECT="${2:-big-one}"
SEED_SYMBOL_ID="${3:-symbol:big-one:/home/ppotepa/git/llens/example-projects/shell-quest/editor/src/main.rs:main:14}"
SEED_FILE_ID="${4:-file:/home/ppotepa/git/llens/example-projects/shell-quest/editor/src/main.rs}"

need_bin() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "missing required binary: $1" >&2
    exit 1
  fi
}

need_bin curl
need_bin jq

echo "[1/4] health"
curl -fsS "$BASE_URL/health" | jq -e '.status == "ok"' >/dev/null

echo "[2/4] graph query with file seed id"
curl -fsS -X POST "$BASE_URL/api/graph/query" \
  -H 'content-type: application/json' \
  -d "{
    \"project\": \"$PROJECT\",
    \"seed\": { \"type\": \"file\", \"id\": \"$SEED_FILE_ID\" },
    \"expand\": {
      \"edgeTypes\": [\"contains\", \"imports\"],
      \"direction\": \"both\",
      \"depth\": 1,
      \"maxNodes\": 60
    },
    \"include\": { \"snippets\": false }
  }" \
  | jq -e '.seedId | startswith("file:")' >/dev/null

echo "[3/4] graph expand with symbol seed id"
curl -fsS -X POST "$BASE_URL/api/graph/expand" \
  -H 'content-type: application/json' \
  -d "{
    \"project\": \"$PROJECT\",
    \"seed\": { \"type\": \"symbol\", \"id\": \"$SEED_SYMBOL_ID\" },
    \"edgeTypes\": [\"contains\", \"references\", \"callers\"],
    \"direction\": \"both\",
    \"depth\": 1,
    \"maxNodes\": 80,
    \"excludeNodeIds\": [],
    \"excludeEdgeIds\": [],
    \"page\": { \"offset\": 0, \"limit\": 50 }
  }" \
  | jq -e '.seedId | startswith("symbol:")' >/dev/null

echo "[4/4] graph collapse contract"
curl -fsS -X POST "$BASE_URL/api/graph/collapse" \
  -H 'content-type: application/json' \
  -d "{
    \"project\": \"$PROJECT\",
    \"seed\": { \"type\": \"symbol\", \"id\": \"$SEED_SYMBOL_ID\" },
    \"edgeTypes\": [\"contains\", \"imports\", \"references\"],
    \"direction\": \"both\",
    \"depth\": 1,
    \"maxNodes\": 80
  }" \
  | jq -e '(.removeNodeIds | type == "array") and (.removeEdgeIds | type == "array")' >/dev/null

echo "smoke checks passed"
