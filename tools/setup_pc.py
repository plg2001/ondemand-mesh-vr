"""Prepare a PC for ondemand-mesh-vr in one command, on Windows, Linux or macOS. Safe to run again: the steps
already done are skipped, so on a PC that is ready it only checks and runs the tests.

    python tools/setup_pc.py                     # everything: environment, PyTorch for this GPU, models, data, tests
    python tools/setup_pc.py --checkpoints-from /media/usb/checkpoints   # weights copied from a disk, not downloaded
    python tools/setup_pc.py --dry-run           # only print what would be done
    python tools/setup_pc.py --export-checkpoints E:/checkpoints         # on a ready PC: weights for an external disk
Windows: double-click setup_pc.bat. Linux / macOS: ./setup_pc.sh (same options). Any Python 3 can start it: it only
uses the standard library, and builds the project's own environment (.venv) with Python 3.11.

Steps
 1. Python 3.11 in .venv (open3d 0.18, used by TreeMeshGPT's code, has no wheels for Python 3.12+): made with `uv`
    if it is installed (uv downloads Python 3.11 by itself), else with `py -3.11` (Windows) or `python3.11`.
 2. PyTorch 2.7.1 built for this machine, then requirements.txt: CUDA 12.8 for NVIDIA RTX 50 (compute capability
    12.x), CUDA 11.8 for the other NVIDIA GPUs (Pascal such as the GT 1030 up to Ada and Hopper, driver >= 520), CPU
    build without an NVIDIA GPU; --cuda forces one.
 3. The three models' code at the pinned commits (third_party/) and their weights (~11 GB in checkpoints/) at the
    revisions recorded in models/upstream.lock.json: models/setup_models.py downloads them. With --checkpoints-from
    they are copied from that folder (a copy made with --export-checkpoints, or a plain copy of checkpoints/) and
    setup_models.py only checks them against the lock, without downloading.
 4. Test point clouds (data/inputs) and the demo scene with the reference clouds the server needs to measure
    accuracy (data/scenes/demo_room; the copy in the Unity project comes out identical).
 5. The tests (no GPU or weights needed).
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Optional

ROOT = Path(__file__).resolve().parents[1]
TORCH = ["torch==2.7.1", "torchvision==0.22.1"]
TORCH_INDEX = "https://download.pytorch.org/whl/{}"
LOCK = ROOT / "models" / "upstream.lock.json"


# ----------------------------------------------------------------------------- pure helpers (tested)
def venv_python(root: Path = ROOT, platform: str = sys.platform) -> Path:
    return root / ".venv" / ("Scripts/python.exe" if platform == "win32" else "bin/python")


def parse_nvidia_smi(text: str) -> Optional[tuple[str, Optional[float], str]]:
    """(name, compute capability, driver) of the first GPU from
    `nvidia-smi --query-gpu=name,compute_cap,driver_version --format=csv,noheader`, None if there is none."""
    for line in text.splitlines():
        parts = [p.strip() for p in line.split(",")]
        if len(parts) >= 3 and parts[0]:
            try:
                cc = float(parts[1])
            except ValueError:  # old drivers do not know compute_cap
                cc = None
            return parts[0], cc, parts[2]
    return None


def torch_variant(gpu: Optional[tuple], platform: str = sys.platform, forced: str = "auto") -> Optional[str]:
    """PyTorch build for this machine: 'cu128', 'cu118', 'cpu', or None for PyPI's default (macOS)."""
    if forced != "auto":
        return forced
    if platform == "darwin":
        return None
    if gpu is None:
        return "cpu"
    cc = gpu[1]
    return "cu128" if cc is not None and cc >= 12.0 else "cu118"


def weight_files(weights: dict, checkpoints: Path) -> list[tuple[str, Path, Optional[int]]]:
    """(name, path inside a checkpoints folder, expected bytes) of every weight file in the lock: Hugging Face files
    at their pinned revision (cache layout models--org--name/snapshots/<revision>/<file>, the one the wrappers load),
    TreeMeshGPT's file at its path."""
    out = []
    for key, entry in weights.items():
        if entry.get("path"):  # Google Drive (TreeMeshGPT): path relative to the repository
            rel = Path(entry["path"])
            rel = rel.relative_to("checkpoints") if rel.parts[:1] == ("checkpoints",) else rel
            out.append((key, checkpoints / rel, entry.get("bytes")))
        elif entry.get("revision"):
            org, name, fname = key.split("/", 2)
            out.append((key, checkpoints / "huggingface" / f"models--{org}--{name}" / "snapshots" / entry["revision"] /
                        fname, entry.get("bytes")))
    return out


def lock_weights() -> dict:
    return json.loads(LOCK.read_text(encoding="utf-8")).get("weights", {}) if LOCK.exists() else {}


def copy_weights(src_root: Path, dst_root: Path, dry_run: bool) -> tuple[int, int, list[str]]:
    """Copies the lock's weight files from one checkpoints folder to another, as plain files (links followed, so
    nothing is stored twice); files already there with the right size are skipped. (copied, skipped, missing)."""
    copied = skipped = 0
    missing = []
    for name, src, size in weight_files(lock_weights(), src_root):
        dst = dst_root / src.relative_to(src_root)
        if not src.exists():
            missing.append(name)
            continue
        if dst.exists() and dst.stat().st_size == src.stat().st_size:
            skipped += 1
            continue
        copied += 1
        print(f"  {'would copy' if dry_run else 'copying'} {name} ({src.stat().st_size / 2**30:.2f} GB)", flush=True)
        if not dry_run:
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(src, dst)  # follows the cache's symbolic links
    return copied, skipped, missing


# ----------------------------------------------------------------------------- running things
class Runner:
    def __init__(self, dry_run: bool):
        self.dry_run = dry_run

    def run(self, cmd: list, check: bool = True, **kw) -> int:
        print("  $ " + " ".join(str(c) for c in cmd), flush=True)
        if self.dry_run:
            return 0
        rc = subprocess.call([str(c) for c in cmd], cwd=ROOT, **kw)
        if check and rc != 0:
            sys.exit(f"\n[setup] failed (exit {rc}): " + " ".join(str(c) for c in cmd))
        return rc


def output(cmd: list) -> str:
    try:
        return subprocess.run([str(c) for c in cmd], capture_output=True, text=True, timeout=60).stdout.strip()
    except (OSError, subprocess.TimeoutExpired):
        return ""


def python311() -> Optional[list]:
    """A command that runs Python 3.11 (not uv: handled apart), or None."""
    candidates = []
    if sys.platform == "win32" and shutil.which("py"):
        candidates.append(["py", "-3.11"])
    candidates += [[c] for c in ("python3.11",) if shutil.which(c)]
    candidates.append([sys.executable])
    for cmd in candidates:
        if output(cmd + ["-c", "import sys; print(sys.version_info[:2] == (3, 11))"]) == "True":
            return cmd
    return None


# ----------------------------------------------------------------------------- steps
def step_venv(r: Runner) -> Path:
    py = venv_python()
    print("\n[1/5] Python 3.11 environment (.venv)")
    if py.exists() and output([py, "-c", "import sys; print(sys.version_info[:2] == (3, 11))"]) == "True":
        print(f"  already there: {py}")
        return py
    if py.exists():
        sys.exit(f"[setup] {ROOT / '.venv'} exists but is not Python 3.11: rename or delete it, then run this again.")
    if shutil.which("uv"):
        r.run(["uv", "venv", "--python", "3.11", "--seed", ROOT / ".venv"])  # --seed: with pip, as python -m venv
    else:
        base = python311()
        if base is None:
            sys.exit("[setup] Python 3.11 not found. Install it (or uv, which downloads it by itself):\n"
                     "  Windows: https://www.python.org/downloads/release/python-3119/ (tick 'Add to PATH')\n"
                     "  Ubuntu:  sudo add-apt-repository ppa:deadsnakes/ppa && sudo apt install python3.11 "
                     "python3.11-venv\n  any OS:  https://docs.astral.sh/uv/  then run this again")
        r.run(base + ["-m", "venv", ROOT / ".venv"])
    return py


def step_packages(r: Runner, py: Path, forced_cuda: str) -> None:
    print("\n[2/5] PyTorch and the other packages")
    gpu = parse_nvidia_smi(output(["nvidia-smi", "--query-gpu=name,compute_cap,driver_version",
                                   "--format=csv,noheader"])) if shutil.which("nvidia-smi") else None
    variant = torch_variant(gpu, forced=forced_cuda)
    print(f"  GPU: {gpu[0] + f' (compute capability {gpu[1]}, driver {gpu[2]})' if gpu else 'no NVIDIA GPU'}"
          f" -> PyTorch build: {variant or 'PyPI default'}")
    installed = output([py, "-c", "import torch; print(torch.__version__)"]) if py.exists() else ""
    wanted_suffix = f"+{variant}" if variant else ""
    if installed.startswith("2.7.1") and (not wanted_suffix or installed.endswith(wanted_suffix)):
        print(f"  torch {installed} already installed")
    else:
        r.run([py, "-m", "pip", "install", "--upgrade", "pip"])
        index = ["--index-url", TORCH_INDEX.format(variant)] if variant else []
        r.run([py, "-m", "pip", "install", *TORCH, *index])
    r.run([py, "-m", "pip", "install", "-r", ROOT / "requirements.txt"])


def step_models(r: Runner, py: Path, checkpoints_from: Optional[Path], skip_weights: bool) -> None:
    print("\n[3/5] Models: code at the pinned commits and weights")
    if checkpoints_from:
        if not checkpoints_from.is_dir():
            sys.exit(f"[setup] --checkpoints-from: {checkpoints_from} is not a folder")
        print(f"  weights from {checkpoints_from}")
        copied, skipped, missing = copy_weights(checkpoints_from, ROOT / "checkpoints", r.dry_run)
        print(f"  {copied} copied, {skipped} already there" +
              (f"; not in that folder (will be downloaded): {', '.join(missing)}" if missing else ""))
    # with the weights in place this downloads nothing: it checks them against the lock (sha256), a few minutes
    r.run([py, ROOT / "models" / "setup_models.py", *(["--skip-weights"] if skip_weights else [])])


def step_data(r: Runner, py: Path, force: bool) -> None:
    print("\n[4/5] Test point clouds and demo scene")
    if force or not any((ROOT / "data" / "inputs").glob("*/*.xyz")):
        r.run([py, ROOT / "data" / "inputs" / "generate_pointclouds.py"])
    else:
        print("  point clouds already there (data/inputs)")
    if force or not any((ROOT / "data" / "scenes" / "demo_room").glob("*_ref.npy")):
        r.run([py, ROOT / "data" / "scenes" / "build_scene.py"])
    else:
        print("  demo scene already there (data/scenes/demo_room)")


def report(py: Path) -> None:
    print("\n[setup] done. This PC:")
    code = ("import torch, sys; from edge.netinfo import lan_ipv4_addresses as a; "
            "g = torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'none (CPU only)'; "
            "print(f'  Python {sys.version.split()[0]}, torch {torch.__version__}, GPU {g}'); "
            "print('  addresses on the network: ' + (', '.join(a()) or 'none'))")
    subprocess.call([str(py), "-c", code], cwd=ROOT)
    for name, path, size in weight_files(lock_weights(), ROOT / "checkpoints"):
        state = ("ok" if size is None or path.stat().st_size == size else f"WRONG SIZE ({path.stat().st_size} bytes)") \
            if path.exists() else "MISSING"
        print(f"  weights {name}: {state}")
    run = ".venv\\Scripts\\python.exe" if sys.platform == "win32" else ".venv/bin/python"
    print("\nEdge server for a headset on the network (here or on another PC):")
    print(f"  {run} -m edge.server --host 0.0.0.0 --backend model --model meshanything "
          f"--save-meshes benchmarks/results/edge_meshes")
    print("then, on the PC with the headset:  python tools/quest/quest.py session wifi --server http://<address>:8000")


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--checkpoints-from", type=Path, default=None,
                    help="copy the weights from this folder (e.g. an external disk with a copy of checkpoints/)")
    ap.add_argument("--skip-weights", action="store_true", help="only the models' code, no weights")
    ap.add_argument("--cuda", choices=("auto", "cu118", "cu128", "cpu"), default="auto",
                    help="PyTorch build (default: chosen from the GPU)")
    ap.add_argument("--force-data", action="store_true", help="regenerate point clouds and scene even if present")
    ap.add_argument("--no-tests", action="store_true", help="skip the tests")
    ap.add_argument("--dry-run", action="store_true", help="print the steps without running them")
    ap.add_argument("--export-checkpoints", type=Path, default=None, metavar="FOLDER",
                    help="only copy this PC's weights to FOLDER (e.g. an external disk) for --checkpoints-from")
    args = ap.parse_args(argv)

    if args.export_checkpoints:
        print(f"[setup] weights of {ROOT / 'checkpoints'} -> {args.export_checkpoints}")
        copied, skipped, missing = copy_weights(ROOT / "checkpoints", args.export_checkpoints, args.dry_run)
        print(f"[setup] {copied} copied, {skipped} already there" +
              (f"; MISSING on this PC: {', '.join(missing)}" if missing else "") +
              "\nOn the other PC: setup_pc --checkpoints-from <that folder>")
        return 1 if missing else 0

    r = Runner(args.dry_run)
    print(f"[setup] ondemand-mesh-vr in {ROOT} ({sys.platform})" + ("  -- DRY RUN" if args.dry_run else ""))
    py = step_venv(r)
    step_packages(r, py, args.cuda)
    step_models(r, py, args.checkpoints_from, args.skip_weights)
    step_data(r, py, args.force_data)
    print("\n[5/5] Tests")
    if args.no_tests:
        print("  skipped")
    else:
        r.run([py, "-m", "pytest", "-q", ROOT / "tests"], check=False)
    if not args.dry_run:
        report(py)
    return 0


if __name__ == "__main__":
    sys.exit(main())
