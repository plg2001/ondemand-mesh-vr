#!/usr/bin/env sh
# Starts the edge server of ondemand-mesh-vr with a generative model (Linux / macOS; on Windows:
# tools\edge\start_model_server.bat). First option = model (meshanything | fastmesh | treemeshgpt, default
# meshanything), the rest goes to the server, e.g.
#   tools/edge/start_model_server.sh fastmesh --host 0.0.0.0 --save-meshes benchmarks/results/edge_meshes
# Defaults: --time-budget-s 300 (the server gives up on an object after 5 minutes).
cd "$(dirname "$0")/../.." || exit 1
PY=.venv/bin/python
if [ ! -x "$PY" ]; then
    echo "[edge] Python environment not found: run ./setup_pc.sh in the repository root first"
    exit 1
fi
if command -v curl >/dev/null 2>&1 && curl -s -o /dev/null -m 2 http://127.0.0.1:8000/health; then
    echo "[edge] A server is already running on http://127.0.0.1:8000 - stop it first."
    exit 1
fi
MODEL=meshanything
case "$1" in
    ""|-*) ;;
    *) MODEL=$1; shift ;;
esac
echo "[edge] Starting with: --backend model --model $MODEL --time-budget-s 300 $*"
exec "$PY" -m edge.server --backend model --model "$MODEL" --time-budget-s 300 "$@"
