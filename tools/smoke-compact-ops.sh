#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${1:-http://localhost:5100}"
PROJECT="${2:-llens}"

need_bin() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "missing required binary: $1" >&2
    exit 1
  fi
}

need_bin curl
need_bin jq
need_bin rg

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"

echo "[1/16] health"
curl -fsS "$BASE_URL/health" | jq -e '.status == "ok"' >/dev/null

echo "[2/16] wait for project indexing baseline"
ROOT_FILE="$REPO_ROOT/Program.cs"
for _ in $(seq 1 90); do
  files="$(curl -fsS "$BASE_URL/api/files/?project=$PROJECT")" || true
  if jq -e --arg p "$ROOT_FILE" '.[] | select(.filePath == $p)' >/dev/null <<<"$files"; then
    break
  fi
  sleep 1
done
files="$(curl -fsS "$BASE_URL/api/files/?project=$PROJECT")"
jq -e --arg p "$ROOT_FILE" '.[] | select(.filePath == $p)' >/dev/null <<<"$files"

echo "[3/16] schema"
curl -fsS "$BASE_URL/api/compact/schema" \
  | jq -e '.endpoints | type == "array" and (index("/api/compact/workflow/run") != null)' >/dev/null

echo "[4/16] regex"
curl -fsS -X POST "$BASE_URL/api/compact/regex" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"pattern\":\"MapCompact\",\"pathPrefix\":\"$REPO_ROOT/Api\",\"maxMatches\":5}" \
  | jq -e '.project and (.count >= 0) and (.matches | type == "array")' >/dev/null

echo "[5/16] replace-plan"
curl -fsS -X POST "$BASE_URL/api/compact/replace-plan" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"find\":\"MapCompact\",\"replace\":\"MapCompact\",\"mode\":\"literal\",\"pathPrefix\":\"$REPO_ROOT/Api\",\"maxMatches\":5}" \
  | jq -e '.project and (.total >= 0) and (.files | type == "array")' >/dev/null

echo "[6/16] fs tree + read-range"
curl -fsS -X POST "$BASE_URL/api/compact/fs/tree" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"maxDepth\":1,\"maxEntries\":40}" \
  | jq -e '.project and (.count >= 1) and (.entries | type == "array")' >/dev/null

curl -fsS -X POST "$BASE_URL/api/compact/fs/read-range" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"path\":\"Program.cs\",\"from\":1,\"to\":20}" \
  | jq -e --arg p "$ROOT_FILE" '.path == $p and (.from == 1) and (.lines | type == "array") and (.lines | length > 0)' >/dev/null

echo "[7/16] deps"
curl -fsS -X POST "$BASE_URL/api/compact/deps" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"seed\":{\"type\":\"file\",\"path\":\"Program.cs\"}}" \
  | jq -e --arg p "$ROOT_FILE" '.seed == ("file:" + $p) and (.count >= 0) and (.edges | type == "array")' >/dev/null

echo "[8/16] diagnostics"
curl -fsS -X POST "$BASE_URL/api/compact/diagnostics" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"target\":\"dotnet\",\"timeoutMs\":30000}" \
  | jq -e '.project and (.target | type == "string") and (.exitCode | type == "number") and (.diagnostics | type == "array")' >/dev/null

echo "[9/16] test-map"
curl -fsS -X POST "$BASE_URL/api/compact/test/map" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"q\":\"compact routes\",\"limit\":5}" \
  | jq -e '.project and (.count >= 0) and (.candidates | type == "array")' >/dev/null

echo "[10/16] session plan"
curl -fsS -X POST "$BASE_URL/api/compact/session/plan" \
  -H 'content-type: application/json' \
  -d '{"sessionId":"smoke-compact","setGoal":"verify","appendStep":"one","setState":"active"}' \
  | jq -e '.sessionId == "smoke-compact" and .goal == "verify" and .state == "active" and (.steps | length >= 1)' >/dev/null

curl -fsS "$BASE_URL/api/compact/session/plan?sessionId=smoke-compact" \
  | jq -e '.sessionId == "smoke-compact" and .goal == "verify"' >/dev/null

echo "[11/16] quality + semantic + rename"
curl -fsS -X POST "$BASE_URL/api/compact/quality/guard" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"tokenBudget\":1200}" \
  | jq -e '.project and (.warnings | type == "array") and (.infos | type == "array")' >/dev/null

curl -fsS -X POST "$BASE_URL/api/compact/semantic-search" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"q\":\"compact graph endpoint\",\"limit\":5}" \
  | jq -e '.project and .mode == "semantic" and (.count >= 0) and (.items | type == "array")' >/dev/null

curl -fsS -X POST "$BASE_URL/api/compact/refactor/rename-plan" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"symbolName\":\"MapCompactRoutes\",\"newName\":\"MapCompactRoutesX\"}" \
  | jq -e '.project and (.count >= 0) and (.candidates | type == "array")' >/dev/null

echo "[12/16] workflow run"
curl -fsS -X POST "$BASE_URL/api/compact/workflow/run" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"q\":\"compact graph endpoint\",\"limit\":8,\"tokenBudget\":800,\"maxItems\":20}" \
  | jq -e '.project and (.resolve | type == "object") and (.contextPack | type == "object")' >/dev/null

echo "[13/16] fs.edit dry-run"
EDIT_OPS_FILE="$(mktemp)"
cat > "$EDIT_OPS_FILE" <<JSON
[
  {
    "path": "Program.cs",
    "type": "replace_snippet",
    "find": "using Llens.Api;",
    "content": "using Llens.Api;",
    "occurrence": 1
  }
]
JSON

curl -fsS -X POST "$BASE_URL/api/compact/fs/edit" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"dryRun\":true,\"operations\":$(cat "$EDIT_OPS_FILE")}" \
  | jq -e '.project and (.dryRun == true) and (.applied >= 1) and (.failed == 0) and (.results | type == "array")' >/dev/null
rm -f "$EDIT_OPS_FILE"

echo "[14/16] git status object + tuple"
curl -fsS -X POST "$BASE_URL/api/compact/git/status" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"mode\":\"compact\",\"maxEntries\":20}" \
  | jq -e '.project and (.count >= 0) and (.compact | type == "array")' >/dev/null

curl -fsS -X POST "$BASE_URL/api/compact/git/status" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"mode\":\"compact\",\"maxEntries\":20,\"format\":\"tuple\"}" \
  | jq -e '.v == "2" and .format == "tuple" and .schema == "compact.git-status" and (.columns | type == "array") and (.items | type == "array")' >/dev/null

echo "[15/16] query + context-pack tuple"
curl -fsS -X POST "$BASE_URL/api/compact/query" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"mode\":\"fuzzy\",\"q\":\"Compact\",\"limit\":10,\"format\":\"tuple\"}" \
  | jq -e '.v == "2" and .format == "tuple" and .schema == "compact.query" and (.columns | type == "array") and (.meta | type == "object") and (.items | type == "array")' >/dev/null

curl -fsS -X POST "$BASE_URL/api/compact/context-pack" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"mode\":\"fuzzy\",\"q\":\"Compact\",\"tokenBudget\":500,\"maxItems\":10,\"format\":\"tuple\"}" \
  | jq -e '.v == "2" and .format == "tuple" and .schema == "compact.context-pack" and (.columns | type == "array") and (.meta | type == "object") and (.items | type == "array")' >/dev/null

echo "[16/16] compact deps focused smoke"
"$SCRIPT_DIR/smoke-compact-deps.sh" "$BASE_URL" "$PROJECT" >/dev/null

echo "compact ops smoke checks passed"
