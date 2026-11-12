#!/usr/bin/env sh
# Prepares this PC for ondemand-mesh-vr (Linux / macOS): Python 3.11 environment, PyTorch for this GPU, models,
# data, tests. Safe to run again. Options: see tools/setup_pc.py, e.g.  ./setup_pc.sh --checkpoints-from /media/usb/checkpoints
cd "$(dirname "$0")" || exit 1
exec python3 tools/setup_pc.py "$@"
