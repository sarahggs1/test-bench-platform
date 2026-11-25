#!/usr/bin/env bash
# Build everything and start the simulator and the portal: then open http://localhost:5080
#   scripts/run_demo.sh [--fail-oil-pump-at 20]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cmake -S "$ROOT/bench-sim" -B "$ROOT/bench-sim/build" -DCMAKE_BUILD_TYPE=Release > /dev/null
cmake --build "$ROOT/bench-sim/build" -j > /dev/null
(cd "$ROOT/portal" && npm ci --silent && npx ng build > /dev/null)

"$ROOT/bench-sim/build/bench_sim" "$@" &
SIM_PID=$!
trap 'kill $SIM_PID 2>/dev/null || true' EXIT
echo "Portal: http://localhost:5080"
cd "$ROOT/acquisition/src/BenchPortal.Api" && dotnet run --no-launch-profile
