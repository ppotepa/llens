#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${1:-http://localhost:5009}"
PROJECT="${2:-big-one}"
QUERY="${3:-main loop renderer state}"
ITERATIONS="${4:-20}"

need_bin() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "missing required binary: $1" >&2
    exit 1
  fi
}

need_bin curl
need_bin jq
need_bin awk
need_bin date

run_case() {
  local name="$1"
  local payload="$2"
  local endpoint="$3"
  local mode="$4"

  local latencies=()
  local empties=0

  for ((i=1; i<=ITERATIONS; i++)); do
    local t0 t1 dt resp result_count
    t0=$(date +%s%3N)
    resp="$(curl -fsS -X POST "$BASE_URL$endpoint" -H 'content-type: application/json' -d "$payload")"
    t1=$(date +%s%3N)
    dt=$((t1 - t0))
    latencies+=("$dt")

    if [[ "$mode" == "query" ]]; then
      result_count="$(jq -r '.resultCount // ((.symbols|length)+(.references|length)+(.snippets|length))' <<<"$resp")"
    else
      result_count="$(jq -r '.items|length' <<<"$resp")"
    fi
    if [[ "${result_count:-0}" -eq 0 ]]; then
      empties=$((empties + 1))
    fi
  done

  local stats
  stats="$(printf "%s\n" "${latencies[@]}" | sort -n | awk '
    { arr[NR]=$1; sum+=$1 }
    END {
      if (NR==0) { print "0 0 0"; exit }
      p95i = int((NR*95 + 99) / 100);
      if (p95i < 1) p95i = 1;
      if (p95i > NR) p95i = NR;
      avg = sum/NR;
      print avg, arr[p95i], NR;
    }')"

  local avg p95 n
  read -r avg p95 n <<<"$stats"
  local empty_rate
  empty_rate="$(awk -v e="$empties" -v n="$n" 'BEGIN { if (n==0) print 0; else printf "%.4f", e/n }')"

  echo "$name: avgMs=$(printf '%.2f' "$avg") p95Ms=$p95 n=$n emptyRate=$empty_rate"
}

QUERY_PAYLOAD="$(cat <<JSON
{
  "project":"$PROJECT",
  "mode":"fuzzy",
  "query":"$QUERY",
  "limit":20,
  "snippetRadius":6
}
JSON
)"

CP_PAYLOAD="$(cat <<JSON
{
  "project":"$PROJECT",
  "mode":"implementation",
  "query":"$QUERY",
  "tokenBudget":2200,
  "maxItems":30,
  "snippetRadius":6
}
JSON
)"

echo "benchmark: base=$BASE_URL project=$PROJECT iterations=$ITERATIONS"
run_case "query/fuzzy" "$QUERY_PAYLOAD" "/api/query" "query"
run_case "context-pack/implementation" "$CP_PAYLOAD" "/api/context-pack" "context-pack"

echo "telemetry snapshot:"
curl -fsS "$BASE_URL/api/telemetry" | jq '{totalQueries, emptyResults, fallbackCount, emptyRate, fallbackRate}'
