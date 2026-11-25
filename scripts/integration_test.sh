#!/usr/bin/env bash
# End-to-end non-regression test: simulator (C++) + acquisition API (C#), driven over HTTP.
#   1. nominal run          -> run "completed", verdict "pass", measurements stored
#   2. oil pump failure     -> bench trips, run "fault", verdict "fail", alarm reported
# Usage: scripts/integration_test.sh   (after building bench-sim and the API)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
API="http://localhost:5080/api"
WORK="$(mktemp -d)"
SIM_PID=""; API_PID=""

cleanup() {
  [[ -n "$SIM_PID" ]] && kill "$SIM_PID" 2>/dev/null || true
  [[ -n "$API_PID" ]] && kill "$API_PID" 2>/dev/null || true
  rm -rf "$WORK"
}
trap cleanup EXIT

json() { python3 -c "import json,sys; d=json.load(sys.stdin); print(eval(sys.argv[1], {'d': d}))" "$1"; }
fail() { echo "FAIL: $*"; echo "--- api log"; tail -20 "$WORK/api.log"; exit 1; }

start_sim() {
  [[ -n "$SIM_PID" ]] && kill "$SIM_PID" 2>/dev/null && wait "$SIM_PID" 2>/dev/null || true
  "$ROOT/bench-sim/build/bench_sim" "$@" > "$WORK/sim.log" 2>&1 &
  SIM_PID=$!
  sleep 0.5
}

run_test() {  # name duration -> prints run id once finished
  local id run
  id=$(curl -sf -X POST "$API/tests" -H 'Content-Type: application/json' \
    -d "{\"name\":\"$1\",\"benchId\":1,\"configurationId\":1,\"plannedDate\":\"2026-12-01\",\"speedRpm\":3000,\"loadNm\":150,\"durationS\":$2}" | json "d['id']")
  run=$(curl -sf -X POST "$API/tests/$id/start" | json "d['id']")
  for _ in $(seq 1 $(( $2 * 2 + 20 ))); do
    [[ "$(curl -sf "$API/runs/$run" | json "d['status']")" != "running" ]] && break
    sleep 0.5
  done
  echo "$run"
}

start_sim
BIN="$ROOT/acquisition/src/BenchPortal.Api/bin/Release/net8.0"
(cd "$BIN" && exec dotnet BenchPortal.Api.dll --urls http://localhost:5080 \
  --Bench:Database="Data Source=$WORK/it.db") > "$WORK/api.log" 2>&1 &
API_PID=$!
for _ in $(seq 1 60); do curl -sf "$API/benches" > /dev/null 2>&1 && break; sleep 0.5; done
curl -sf "$API/benches" > /dev/null || fail "API did not start"

echo "== 1. nominal run"
RUN=$(run_test "Nominal 3000 rpm" 8)
STATUS=$(curl -sf "$API/runs/$RUN")
[[ "$(echo "$STATUS" | json "d['status']")" == "completed" ]] || fail "nominal run status: $STATUS"
[[ "$(echo "$STATUS" | json "d['verdict']")" == "pass" ]] || fail "nominal run verdict: $STATUS"
POINTS=$(curl -sf "$API/runs/$RUN/measurements?signal=speed_rpm" | json "len(d)")
LAST=$(curl -sf "$API/runs/$RUN/measurements?signal=speed_rpm" | json "round(d[-1]['value'])")
(( POINTS > 50 )) || fail "only $POINTS speed samples"
(( LAST > 2950 && LAST < 3050 )) || fail "speed did not reach the 3000 rpm setpoint ($LAST)"
echo "   ok: completed, pass, $POINTS samples, final speed $LAST rpm"

echo "== 2. oil pump failure at t = 4 s"
start_sim --fail-oil-pump-at 4
RUN=$(run_test "Oil pump failure" 15)
STATUS=$(curl -sf "$API/runs/$RUN")
[[ "$(echo "$STATUS" | json "d['status']")" == "fault" ]] || fail "failure run status: $STATUS"
[[ "$(echo "$STATUS" | json "d['verdict']")" == "fail" ]] || fail "failure run verdict: $STATUS"
echo "$STATUS" | json "d['summaryJson']" | grep -q "Oil pressure low" || fail "alarm not reported"
curl -sf -X POST "$API/bench/reset" > /dev/null
echo "   ok: fault detected, fail verdict, alarm 'Oil pressure low' reported"

echo "All integration tests passed"
