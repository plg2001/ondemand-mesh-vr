"""MeshPad on sketches: draw (or load) the sketch, generate the mesh, time it and measure its accuracy.

    python models/test_meshpad.py                                   # the six shapes of the demo scene, GPU if any
    python models/test_meshpad.py --shapes box bunny --seeds 0 1 2  # several samples per shape (generation samples)
    python models/test_meshpad.py --images my_sketch.png --device cpu
    python models/test_meshpad.py --azimuth 0 --elevation 20 --greedy --time-budget-s 120

A shape (any key of data/inputs/generate_pointclouds.py SOURCES) is canonicalised as in the scene (bbox centred,
longest side 1, +Y up), drawn as MeshPad draws its sketches (mesh_wrappers/sketch.py) and compared with a dense
reference cloud of the same canonical mesh (20000 points, as build_scene.py). MeshPad decides by itself which way an
object faces, so the accuracy is given both as generated and after the best of 24 rotations about the vertical axis.

Output: benchmarks/results/meshpad_<timestamp>/ with sketches/*.png, meshes/*.glb (as generated, git-ignored),
runs/*.json, results.json and summary.md.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
for i, p in enumerate((str(HERE), str(ROOT), str(ROOT / "data" / "inputs"))):  # models/ is not a package
    if p not in sys.path:
        sys.path.insert(i, p)  # before site-packages: a pip package also ships a top-level "benchmarks"

import mesh_wrappers.loading  # noqa: E402,F401  (checkpoints/ for Hugging Face and torch.hub, before torch loads them)
import numpy as np  # noqa: E402
import torch  # noqa: E402

from benchmarks.profiler import (CudaMemory, GenerationBudgetExceeded, HostMemoryMonitor, StageTimer,  # noqa: E402
                                 cuda_sync, system_info)
from generate_pointclouds import SOURCES, canonicalise, load_source, sample  # noqa: E402
from mesh_wrappers import flash_shim  # noqa: E402
from mesh_wrappers.loading import read_lock  # noqa: E402
from mesh_wrappers.mesh_io import canonical, mesh_stats, quality_best_yaw, quality_metrics, triangles_to_mesh  # noqa: E402
from mesh_wrappers.registry import get_wrapper_class  # noqa: E402
from mesh_wrappers.sketch import DEFAULT_AZIMUTH, DEFAULT_ELEVATION, load_sketch, render_sketch, sketch_to_png  # noqa: E402

DEFAULT_SHAPES = ["box", "bunny", "tmg_cat", "fastmesh_001", "sphere", "mav2_wand"]  # the demo scene's objects
LABELS = {"box": "scatola", "bunny": "coniglio", "tmg_cat": "gatto", "fastmesh_001": "pupazzo", "sphere": "sfera",
          "mav2_wand": "bacchetta", "kenney_chair": "sedia", "kenney_chairRounded": "sedia tonda",
          "kenney_loungeSofa": "divano", "kenney_bench": "panca", "kenney_bedSingle": "letto",
          "kenney_bookcaseOpen": "libreria", "kenney_table": "tavolo", "kenney_tableRound": "tavolo tondo",
          "kenney_desk": "scrivania", "kenney_lampSquareFloor": "lampada", "kenney_computerScreen": "monitor",
          "kenney_laptop": "portatile", "kenney_trashcan": "cestino", "kenney_kitchenMicrowave": "microonde"}
REFERENCE_POINTS = 20000


def build_inputs(args) -> list[dict]:
    items = []
    for shape in args.shapes or []:
        if shape not in SOURCES:
            raise SystemExit(f"unknown shape {shape!r}; available: {', '.join(SOURCES)}")
        mesh, _ = canonicalise(load_source(shape))
        strokes = render_sketch(mesh.vertices, mesh.faces, args.azimuth, args.elevation)
        ref, _ = sample(mesh, REFERENCE_POINTS, seed=0)
        items.append({"name": shape, "label": LABELS.get(shape, shape), "strokes": strokes, "reference": ref,
                      "source": f"shape {shape} ({len(mesh.faces)} faces)"})
    for path in args.images or []:
        items.append({"name": Path(path).stem, "label": Path(path).stem, "strokes": load_sketch(Path(path)),
                      "reference": None, "source": str(path)})
    return items


def run_one(w, timer: StageTimer, cuda_mem: CudaMemory, item: dict, seed: int, args, out: Path) -> dict:
    row = {"name": item["name"], "label": item["label"], "seed": seed, "source": item["source"]}
    timer.reset()
    cuda_mem.reset()
    t0 = time.perf_counter()
    timer.deadline = t0 + args.time_budget_s if args.time_budget_s else None
    gen = None
    try:
        gen = w.generate_sketch(item["strokes"], max_new_tokens=args.max_new_tokens, seed=seed,
                                sampling=not args.greedy)
        row["status"] = "ok"
    except GenerationBudgetExceeded as e:
        row["status"], row["error"] = "timeout", str(e)
    except (torch.OutOfMemoryError, MemoryError) as e:
        row["status"], row["error"] = "oom", str(e).splitlines()[0][:200]
    finally:
        timer.deadline = None
        cuda_sync(w.device)
    row["total_s"] = time.perf_counter() - t0
    row.update(timer.summary())
    row.update({f"cuda_{k}": v for k, v in cuda_mem.snapshot().items()})
    if gen is not None:
        row.update({"tokens": gen.n_tokens, "truncated": gen.truncated, **gen.extra})
    mesh = triangles_to_mesh(gen.triangles) if gen is not None and gen.triangles is not None else None
    if mesh is None or len(mesh.faces) == 0:
        if row["status"] == "ok":
            row["status"] = "empty"
        return row
    row.update(mesh_stats(mesh))
    glb = out / "meshes" / f"{item['name']}_s{seed}.glb"
    glb.parent.mkdir(parents=True, exist_ok=True)
    glb.write_bytes(mesh.export(file_type="glb"))
    row["mesh_file"], row["mesh_bytes"] = glb.relative_to(out).as_posix(), glb.stat().st_size
    if item["reference"] is not None:
        as_generated = quality_metrics(canonical(mesh), item["reference"])
        _, best = quality_best_yaw(mesh, item["reference"])
        row["quality_as_generated"], row["quality_best_yaw"] = as_generated, best
    return row


def fmt(v, spec: str = ".1f", none: str = "–") -> str:
    return none if v is None else format(v, spec)


def write_summary(out: Path, meta: dict, rows: list[dict]) -> None:
    lines = [f"# MeshPad su schizzi ({meta['started'][:16].replace('T', ' ')})", "",
             f"- Dispositivo: decoder **{meta['device']}** ({meta['device_name']}), encoder RADIO **{meta['encoder_device']}**, "
             f"precisione {meta['precision']}, attenzione {meta['attn']}",
             f"- Caricamento del modello: {meta['load_s']:.1f} s (picco RAM {meta['load_peak_ram_gb']:.2f} GB)",
             f"- Vista degli schizzi: azimut {meta['azimuth']:g}°, elevazione {meta['elevation']:g}°; generazione "
             f"{'greedy' if meta['greedy'] else 'campionata come nella demo (top-k 10, top-p 0,8, temperatura 0,5)'}"
             + (f"; limite {meta['time_budget_s']:g} s" if meta["time_budget_s"] else ""),
             "- Accuratezza rispetto alla nuvola densa della mesh originale (F-score all'1% della diagonale, "
             "Chamfer-L2), sia com'è uscita sia ruotata nel modo migliore attorno all'asse verticale "
             "(MeshPad sceglie da solo il davanti di un oggetto).", "",
             "| Forma | Seme | Esito | Triangoli | Token | Passi decoder | Encoder [s] | Generazione [s] | "
             "ms/passo (p50) | Totale [s] | VRAM picco [MiB] | F-score com'è | F-score ruotata | rotazione | "
             "Chamfer-L2 ruotata |",
             "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|"]
    for r in rows:
        q0, qy = r.get("quality_as_generated") or {}, r.get("quality_best_yaw") or {}
        gen_s = (r.get("prefill_s") or 0) + (r.get("decode_s") or 0) if r.get("decode_s") is not None else None
        lines.append(
            f"| {r['label']} | {r['seed']} | {r['status']}{' (troncata)' if r.get('truncated') else ''} | "
            f"{r.get('triangles', '–')} | {r.get('tokens', '–')} | {r.get('decode_steps', '–')} | "
            f"{fmt(r.get('encoder_s'), '.2f')} | {fmt(gen_s)} | {fmt(r.get('ms_per_token_p50'), '.0f')} | "
            f"{fmt(r.get('total_s'))} | {fmt((r.get('cuda_peak_allocated_bytes') or 0) / 2**20, '.0f')} | "
            f"{fmt(q0.get('fscore@0.01'), '.2f')} | {fmt(qy.get('fscore@0.01'), '.2f')} | "
            f"{fmt(qy.get('yaw_deg'), '.0f')}° | {fmt(qy.get('chamfer_l2'), '.5f')} |")
    (out / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--shapes", nargs="*", default=None, help=f"default: {' '.join(DEFAULT_SHAPES)} (if no --images)")
    ap.add_argument("--images", nargs="*", default=None, help="sketch images (any size; strokes dark on light)")
    ap.add_argument("--seeds", nargs="+", type=int, default=[0])
    ap.add_argument("--azimuth", type=float, default=DEFAULT_AZIMUTH)
    ap.add_argument("--elevation", type=float, default=DEFAULT_ELEVATION)
    ap.add_argument("--greedy", action="store_true", help="greedy decoding instead of the demo's sampling")
    ap.add_argument("--device", default="auto", help="decoder device: auto | cuda | cpu")
    ap.add_argument("--encoder-device", default="auto", help="RADIO: auto (CPU when the GPU has < 6 GB) | cuda | cpu")
    ap.add_argument("--precision", default="auto", choices=["auto", "fp32", "fp16-full"])
    ap.add_argument("--attn", default="auto", choices=["auto", "flash", "sdpa"])
    ap.add_argument("--max-new-tokens", type=int, default=None, help="truncate the generation")
    ap.add_argument("--time-budget-s", type=float, default=None, help="stop a generation after this many seconds")
    ap.add_argument("--out", type=Path, default=None)
    args = ap.parse_args(argv)
    if args.shapes is None and not args.images:
        args.shapes = DEFAULT_SHAPES

    device = ("cuda" if torch.cuda.is_available() else "cpu") if args.device == "auto" else args.device
    precision = ("fp16-full" if device.startswith("cuda") else "fp32") if args.precision == "auto" else args.precision
    items = build_inputs(args)
    out = args.out or ROOT / "benchmarks" / "results" / f"meshpad_{dt.datetime.now():%Y%m%d_%H%M%S}"
    (out / "sketches").mkdir(parents=True, exist_ok=True)
    for item in items:
        (out / "sketches" / f"{item['name']}.png").write_bytes(sketch_to_png(item["strokes"]))

    attn = flash_shim.setup_attention(args.attn, device)
    w = get_wrapper_class("meshpad")(device=device, precision=precision, encoder_device=args.encoder_device)
    if not w.repo_available():
        raise SystemExit("MeshPad code not found: run python models/setup_models.py --models meshpad")
    ok, msg = w.weights_available()
    if not ok:
        raise SystemExit(f"MeshPad weights: {msg}")
    print(f"[meshpad] loading on {device} ({precision}), encoder on {w.resolved_encoder_device()}", flush=True)
    with HostMemoryMonitor() as host:
        t0 = time.perf_counter()
        load_info = w.load()
        load_s = time.perf_counter() - t0
    print(f"[meshpad] loaded in {load_s:.1f} s, peak RAM {host.peak / 2**30:.2f} GB", flush=True)
    timer = StageTimer(device)
    for stage, obj, attr in w.stage_targets():
        timer.wrap(stage, obj, attr)
    cuda_mem = CudaMemory(device)

    meta = {"started": dt.datetime.now().isoformat(timespec="seconds"), "device": device,
            "device_name": torch.cuda.get_device_name(device) if device.startswith("cuda") else "cpu",
            "encoder_device": w.resolved_encoder_device(), "precision": precision, "attn": attn,
            "azimuth": args.azimuth, "elevation": args.elevation, "greedy": args.greedy, "seeds": args.seeds,
            "max_new_tokens": args.max_new_tokens, "time_budget_s": args.time_budget_s, "load_s": load_s,
            "load_peak_ram_gb": host.peak / 2**30, "load_info": load_info,
            "lock": {k: v.get("commit") for k, v in read_lock().get("repos", {}).items()
                     if k in ("meshpad", "meshanything", "fms", "fms_extras", "radio")},
            "system": system_info()}
    rows = []
    for item in items:
        for seed in args.seeds:
            row = run_one(w, timer, cuda_mem, item, seed, args, out)
            rows.append(row)
            (out / "runs").mkdir(exist_ok=True)
            (out / "runs" / f"{item['name']}_s{seed}.json").write_text(json.dumps(row, indent=1, default=str),
                                                                      encoding="utf-8")
            qy = (row.get("quality_best_yaw") or {}).get("fscore@0.01")
            print(f"[meshpad] {item['name']:13s} seed {seed}: {row['status']:7s} {row.get('triangles', 0):5} tri "
                  f"{row.get('tokens', 0):5} tok {row['total_s']:6.1f} s  F-score {fmt(qy, '.2f')}", flush=True)
    (out / "results.json").write_text(json.dumps({"meta": meta, "runs": rows}, indent=1, default=str),
                                      encoding="utf-8")
    write_summary(out, meta, rows)
    print(f"[meshpad] {out / 'summary.md'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
