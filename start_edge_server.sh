#!/usr/bin/env sh
# Starts the edge server of ondemand-mesh-vr (Linux / macOS; on Windows: start_edge_server.bat).
# Default: mock backend (convex hull) with 2 s of simulated inference per object. Options after the name replace the
# default, e.g.  ./start_edge_server.sh --host 0.0.0.0 --backend model --model meshanything --save-meshes benchmarks/results/edge_meshes
cd "$(dirname "$0")" || exit 1
PY=.venv/bin/python
if [ ! -x "$PY" ]; then
    echo "[edge] Python environment not found: run ./setup_pc.sh first"
    exit 1
fi
if [ $# -eq 0 ]; then
    # a second launch must not start a second server on the same port
    if command -v curl >/dev/null 2>&1 && curl -s -o /dev/null -m 2 http://127.0.0.1:8000/health; then
        echo "[edge] A server is already running on http://127.0.0.1:8000 - nothing to do."
        exit 0
    fi
    set -- --mock-delay-s 2
    echo "[edge] Starting with: $*"
fi
exec "$PY" -m edge.server "$@"
