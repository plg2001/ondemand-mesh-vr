"""Benchmark sweep: models x point counts x modes x shapes x normals, one subprocess per configuration.

Each configuration runs `models/test_inference.py` in a fresh Python process, which gives clean peak-memory
measurements, isolates the upstream imports (FastMesh's top-level `models` package) and keeps the sweep alive
if one run OOMs or crashes.

    python -m benchmarks.run_benchmarks --modes mock                                   # all models, 100/500/2048 pts
    python -m benchmarks.run_benchmarks --models meshanything treemeshgpt --modes real --device cpu \
        --points 500 2048 --max-new-tokens 64 --force
    python -m benchmarks.run_benchmarks --modes real --device cuda --repeats 3 --warmup 1       # edge node GPU

    # the thesis table on the lab GPU: the six shapes of the demo scene, the three models, accuracy, both normals
    python -m benchmarks.run_benchmarks --modes real --device cuda --points 2048 --quality --time-budget-s 300 \
        --repeats 3 --warmup 1 --shapes box bunny fastmesh_001 sphere tmg_cat mav2_wand --normals file estimate

--normals: "file" gives the models the true normals of the original mesh (stored in the .ply), "estimate" computes
them from the points with Open3D, as the edge server does for the headset, which sends points only; "auto" (default)
uses the file's normals when there are any. Models that do not use normals report "unused".

Outputs: benchmarks/results/<timestamp>/{results.csv, results.json, system.json, summary.md, runs/*.json}
"""
from __future__ import annotations

import argparse
import datetime as dt
import itertools
import json
import os
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "models"))

from benchmarks.profiler import system_info  # noqa: E402
from benchmarks.summarize import write_summary  # noqa: E402

NORMALS = ("auto", "file", "estimate")


def config_tag(mode: str, model: str, shape: str, points: int, normals: str = "auto") -> str:
    """Name of a configuration's report; unchanged for the default normals, so older sweeps keep their names."""
    return f"{mode}_{model}_{shape}_n{points}" + ("" if normals == "auto" else f"_normals-{normals}")


def inference_cmd(args, python: str, mode: str, model: str, shape: str, points: int, normals: str,
                  json_out: Path, csv_out: Path, variant: str | None = None) -> list[str]:
    """The test_inference.py command of one configuration."""
    cmd = [python, str(ROOT / "models" / "test_inference.py"), "--model", model,
           "--shape", shape, "--points", str(points), "--mode", mode, "--device", args.device,
           "--precision", args.precision, "--repeats", str(args.repeats), "--warmup", str(args.warmup),
           "--normals", normals, "--json-out", str(json_out), "--csv-out", str(csv_out), "--quiet"]
    if variant:
        cmd += ["--variant", variant]
    if args.max_new_tokens:
        cmd += ["--max-new-tokens", str(args.max_new_tokens)]
    if args.time_budget_s:
        cmd += ["--time-budget-s", str(args.time_budget_s)]
    if mode == "mock" and args.surrogate_layers:
        cmd += ["--surrogate-layers", str(args.surrogate_layers)]
    if args.quality:
        cmd += ["--quality"]
    if args.force:
        cmd += ["--force"]
    return cmd


def parse_args(argv=None) -> argparse.Namespace:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--models", nargs="+", default=["meshanything", "fastmesh", "treemeshgpt"])
    ap.add_argument("--variants", nargs="*", default=[], help="model=variant, e.g. fastmesh=V4K treemeshgpt=9bit")
    ap.add_argument("--points", nargs="+", type=int, default=[100, 500, 2048])
    ap.add_argument("--shapes", nargs="+", default=["bunny"])
    ap.add_argument("--modes", nargs="+", default=["mock"], choices=["mock", "real", "auto"])
    ap.add_argument("--normals", nargs="+", default=["auto"], choices=NORMALS,
                    help="file = the mesh's true normals, estimate = computed from the points as the edge server "
                         "does for the headset; several values = one configuration each")
    ap.add_argument("--device", default="auto")
    ap.add_argument("--precision", default="auto")
    ap.add_argument("--repeats", type=int, default=1)
    ap.add_argument("--warmup", type=int, default=0)
    ap.add_argument("--max-new-tokens", type=int, default=None)
    ap.add_argument("--time-budget-s", type=float, default=None, help="per-run cap on AR generation time")
    ap.add_argument("--surrogate-layers", type=int, default=2)
    ap.add_argument("--quality", action="store_true")
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--timeout", type=int, default=3600, help="seconds per configuration")
    ap.add_argument("--python", nargs="*", default=[],
                    help="per-model interpreter, e.g. meshanything=C:/venvs/mav2/Scripts/python.exe")
    ap.add_argument("--out", type=Path, default=None)
    return ap.parse_args(argv)


def main(argv=None) -> None:
    args = parse_args(argv)
    variants = dict(v.split("=", 1) for v in args.variants)
    pythons = dict(v.split("=", 1) for v in args.python)
    out = args.out or ROOT / "benchmarks" / "results" / dt.datetime.now().strftime("%Y%m%d_%H%M%S")
    (out / "runs").mkdir(parents=True, exist_ok=True)
    (out / "system.json").write_text(json.dumps(system_info(), indent=2, default=str), encoding="utf-8")
    csv_path = out / "results.csv"
    results = []
    configs = list(itertools.product(args.modes, args.models, args.shapes, args.points, args.normals))
    print(f"{len(configs)} configurations -> {out}")
    for i, (mode, model, shape, pts, normals) in enumerate(configs, 1):
        tag = config_tag(mode, model, shape, pts, normals)
        js = out / "runs" / f"{tag}.json"
        cmd = inference_cmd(args, pythons.get(model, sys.executable), mode, model, shape, pts, normals, js, csv_path,
                            variants.get(model))
        t0 = time.perf_counter()
        print(f"[{i}/{len(configs)}] {tag} ...", end=" ", flush=True)
        try:
            proc = subprocess.run(cmd, capture_output=True, text=True, timeout=args.timeout,
                                  env={**os.environ, "PYTHONIOENCODING": "utf-8"})
            status = {0: "ok", 3: "oom"}.get(proc.returncode, f"exit {proc.returncode}")
            reason = ""
            if proc.returncode != 0:  # keep why it stopped (e.g. "estimated requirement ... use --force")
                text = (proc.stdout or "") + (proc.stderr or "")
                (out / "runs" / f"{tag}.log.txt").write_text(text[-20000:], encoding="utf-8")
                lines = [line for line in text.splitlines() if line.strip()]
                reason = f": {lines[-1].strip()[:160]}" if lines else ""
        except subprocess.TimeoutExpired:
            status, reason = "timeout", ""
        dur = time.perf_counter() - t0
        print(f"{status} ({dur:.0f} s){reason}")
        if js.exists():
            r = json.loads(js.read_text(encoding="utf-8"))
        else:  # crashed before writing its report (e.g. killed by the OS for lack of memory)
            r = {"model": model, "mode": mode, "status": status,
                 "input": {"shape": shape, "input_points": pts, "normals_requested": normals}}
        r["sweep_wall_s"] = dur
        results.append(r)
    (out / "results.json").write_text(json.dumps(results, indent=2, default=str), encoding="utf-8")
    md = write_summary(results, out / "summary.md")
    print(md)
    print(f"\nCSV: {csv_path}\nJSON: {out / 'results.json'}\nSummary: {out / 'summary.md'}")


if __name__ == "__main__":
    main()
