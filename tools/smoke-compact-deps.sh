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

echo "[1/5] health"
curl -fsS "$BASE_URL/health" | jq -e '.status == "ok"' >/dev/null

echo "[2/5] wait for llens root Program.cs to be indexed"
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

echo "[3/5] deps with repo-relative path seed"
resp1="$(curl -fsS -X POST "$BASE_URL/api/compact/deps" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"seed\":{\"type\":\"file\",\"path\":\"Program.cs\"}}")"

jq -e --arg p "$ROOT_FILE" '.seed == ("file:" + $p)' >/dev/null <<<"$resp1"
jq -e '.count >= 0 and (.edges | type == "array")' >/dev/null <<<"$resp1"
jq -e '.seed | contains("example-projects") | not' >/dev/null <<<"$resp1"

echo "[4/5] deps with file:id relative seed"
resp2="$(curl -fsS -X POST "$BASE_URL/api/compact/deps" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"seed\":{\"type\":\"file\",\"id\":\"file:Program.cs\"}}")"

jq -e --arg p "$ROOT_FILE" '.seed == ("file:" + $p)' >/dev/null <<<"$resp2"
jq -e '.count >= 0 and (.edges | type == "array")' >/dev/null <<<"$resp2"

echo "[5/5] excluded-path seed should not resolve"
status3="$(curl -sS -o /tmp/compact-deps-excluded.json -w "%{http_code}" \
  -X POST "$BASE_URL/api/compact/deps" \
  -H 'content-type: application/json' \
  -d "{\"project\":\"$PROJECT\",\"seed\":{\"type\":\"file\",\"path\":\"example-projects/shell-quest/mods/shell-quest/os/cognitOS/Program.cs\"}}")"

[[ "$status3" == "404" ]]
rg -q "Seed file not indexed" /tmp/compact-deps-excluded.json

echo "compact deps smoke checks passed"
