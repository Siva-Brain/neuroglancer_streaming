#!/usr/bin/env bash
# Stop the DGX Zarr streaming server started by run_dgx.sh (app_zarr.py).
# Sends SIGTERM (graceful), then SIGKILL to anything still alive.
#
#   ./stop_dgx.sh                # stop every app_zarr.py on this host
#   ./stop_dgx.sh --port 8010    # stop only the instance launched with --port 8010
#
# The memory rule is one server instance at a time, so the default (stop all)
# is the usual case; --port is there for when you knowingly run more than one.
set -uo pipefail

PORT=""
if [[ "${1:-}" == "--port" && -n "${2:-}" ]]; then PORT="$2"; fi

# match the running server; if a port was given, require that exact --port N in
# its command line so we never touch a different instance.
PATTERN="[p]ython3? .*app_zarr\.py"
[[ -n "$PORT" ]] && PATTERN="$PATTERN.*(--port[ =]$PORT)( |\$)"

mapfile -t PIDS < <(pgrep -f "$PATTERN" | grep -v "^$$\$" || true)

if [[ ${#PIDS[@]} -eq 0 ]]; then
  echo "[stop_dgx] no running app_zarr.py${PORT:+ (--port $PORT)} found"
  exit 0
fi

echo "[stop_dgx] stopping app_zarr.py${PORT:+ (--port $PORT)}: PID(s) ${PIDS[*]}"
kill -TERM "${PIDS[@]}" 2>/dev/null || true

# give it up to ~10s to shut down cleanly
for _ in $(seq 1 20); do
  ALIVE=()
  for p in "${PIDS[@]}"; do kill -0 "$p" 2>/dev/null && ALIVE+=("$p"); done
  [[ ${#ALIVE[@]} -eq 0 ]] && { echo "[stop_dgx] stopped."; exit 0; }
  sleep 0.5
done

echo "[stop_dgx] force-killing: ${ALIVE[*]}"
kill -KILL "${ALIVE[@]}" 2>/dev/null || true
echo "[stop_dgx] stopped (forced)."
