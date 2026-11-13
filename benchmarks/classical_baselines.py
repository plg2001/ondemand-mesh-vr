"""Classical surface reconstruction on the same inputs, reference and accuracy measure as the generative models.

    python -m benchmarks.classical_baselines                     # the six shapes of the demo scene, 2048 points
    python -m benchmarks.classical_baselines --shapes bunny box --points 500 2048 --target-faces 500 1000
    python -m benchmarks.classical_baselines --no-compare        # without the generative models' rows

Inputs are the files test_inference.py and run_benchmarks use (data/inputs/<shape>/<shape>_n<points>.ply, which carry
the original mesh's normals), the reference is the shape's dense cloud from data/inputs/manifest.json and the accuracy
is mesh_io.quality_metrics: every row is directly comparable with a generative run on the same shape and point count.
Methods that need normals (Ball Pivoting, Poisson) run twice: with the file's true normals and with normals estimated
from the points, as the edge server does for the headset, which sends points only. Every method runs again decimated
to --target-faces triangles, to compare at the weight of the generative models' light meshes. Times: median of
--repeats runs on this PC's CPU (normal estimation reported apart). --compare (default: the committed Phase 1 runs)
adds the generative runs that produced a complete mesh of the same shape and point count.

Output: benchmarks/results/classical_<timestamp>/{results.csv, results.json, summary.md, meshes/*.glb}
"""
from __future__ import annotations

import argparse
import csv
import datetime as dt
import glob
import json
import statistics
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
for _p in (ROOT, ROOT / "models"):
    if str(_p) not in sys.path:
        sys.path.insert(0, str(_p))

import numpy as np  # noqa: E402

from mesh_wrappers import classical  # noqa: E402
from mesh_wrappers.mesh_io import quality_metrics  # noqa: E402
from mesh_wrappers.pointcloud import estimate_normals, load_pointcloud  # noqa: E402

DATA_INPUTS = ROOT / "data" / "inputs"
SCENE_SHAPES = ["box", "bunny", "fastmesh_001", "sphere", "tmg_cat", "mav2_wand"]  # data/scenes/demo_room.yaml
SHAPE_NAMES = {"box": "scatola", "bunny": "coniglio", "fastmesh_001": "pupazzo", "sphere": "sfera",
               "tmg_cat": "gatto", "mav2_wand": "bacchetta"}
DEFAULT_COMPARE = ["benchmarks/results/final_*/runs/*.json", "benchmarks/results/final_full/*.json",
                   "benchmarks/results/normals_*/runs/*.json"]
FIELDS = ["shape", "points", "method", "label", "normals", "target_faces", "triangles", "vertices", "seconds",
          "normals_seconds", "fscore", "precision", "recall", "chamfer_l2", "watertight", "source", "params"]
NORMALS_LABEL = {"file": "vere", "estimate": "stimate", "unused": "non usate", "estimated-open3d": "stimate"}


def shape_inputs(shape: str, points: int) -> tuple[Path, Path]:
    """The .ply cloud (with the mesh's normals) and the dense reference of a shape, as test_inference.py uses them."""
    manifest = json.loads((DATA_INPUTS / "manifest.json").read_text(encoding="utf-8"))["shapes"]
    if shape not in manifest or str(points) not in manifest[shape]["clouds"]:
        raise SystemExit(f"{shape} n={points} not in data/inputs: python data/inputs/generate_pointclouds.py "
                         f"--shapes {shape} --counts {points}")
    entry = manifest[shape]
    return DATA_INPUTS / entry["clouds"][str(points)]["files"]["ply"]["path"], DATA_INPUTS / entry["reference"]


def measure(xyz, normals, method: str, target_faces, repeats: int):
    """(median seconds, last result) of `repeats` reconstructions."""
    times, result = [], None
    for _ in range(max(1, repeats)):
        result = classical.reconstruct(xyz, normals, method, target_faces)
        times.append(result.seconds)
    return statistics.median(times), result


def classical_rows(shapes, points_list, methods, targets, repeats: int, meshes_dir: Path | None) -> list[dict]:
    rows = []
    for shape in shapes:
        for points in points_list:
            ply, ref_path = shape_inputs(shape, points)
            xyz, true_normals = load_pointcloud(ply)
            reference = np.load(ref_path)[:, :3]
            t0 = time.perf_counter()
            estimated = estimate_normals(xyz)
            normals_s = time.perf_counter() - t0
            for method in methods:
                variants = [("file", true_normals), ("estimate", estimated)] if method in classical.NEEDS_NORMALS \
                    else [("unused", None)]
                for normals_name, normals in variants:
                    for target in [None, *targets]:
                        seconds, res = measure(xyz, normals, method, target, repeats)
                        quality = quality_metrics(res.mesh, reference) or {}
                        row = {"shape": shape, "points": points, "method": method,
                               "label": classical.LABELS[method], "normals": normals_name,
                               "target_faces": target or "", "triangles": len(res.mesh.faces),
                               "vertices": len(res.mesh.vertices), "seconds": round(seconds, 5),
                               "normals_seconds": round(normals_s, 5) if normals_name == "estimate" else "",
                               "fscore": quality.get("fscore@0.01"), "precision": quality.get("precision"),
                               "recall": quality.get("recall"), "chamfer_l2": quality.get("chamfer_l2"),
                               "watertight": bool(res.mesh.is_watertight), "source": "classical",
                               "params": json.dumps(res.params)}
                        rows.append(row)
                        print(f"{shape:13s} n{points:<5d} {method:13s} {normals_name:8s} "
                              f"{'' if target is None else f'-> {target} tri':13s} {row['triangles']:6d} tri "
                              f"{seconds * 1e3:8.1f} ms  F {row['fscore'] if row['fscore'] is None else round(row['fscore'], 3)}",
                              flush=True)
                        if meshes_dir is not None and len(res.mesh.faces):
                            meshes_dir.mkdir(parents=True, exist_ok=True)
                            name = f"{shape}_n{points}_{method}_{normals_name}" + (f"_f{target}" if target else "")
                            (meshes_dir / f"{name}.glb").write_bytes(res.mesh.export(file_type="glb"))
    return rows


def generative_rows(patterns, shapes, points_list) -> tuple[list[dict], int]:
    """Complete meshes of the generative runs (test_inference.py reports) on these shapes and point counts, and how
    many runs on them did not produce one (truncated on purpose, time budget, out of memory...)."""
    rows, incomplete, seen = [], 0, set()
    for pattern in patterns:
        for f in sorted(glob.glob(str(ROOT / pattern) if not Path(pattern).is_absolute() else pattern)):
            data = json.loads(Path(f).read_text(encoding="utf-8"))
            for r in data if isinstance(data, list) else [data]:
                inp = r.get("input") or {}
                if r.get("mode") != "real" or inp.get("shape") not in shapes or inp.get("input_points") not in points_list:
                    continue
                q = r.get("quality") or {}
                complete = r.get("status") == "ok" and not (r.get("generation") or {}).get("truncated") and q
                if not complete:
                    incomplete += 1
                    continue
                key = (r.get("model"), r.get("variant"), r.get("device"), r.get("precision"), inp.get("shape"),
                       inp.get("input_points"), inp.get("normals_source"), round(q.get("chamfer_l2", 0), 7))
                if key in seen:  # the same run listed by results.json and runs/*.json
                    continue
                seen.add(key)
                rows.append({"shape": inp["shape"], "points": inp["input_points"], "method": r.get("model"),
                             "label": f"{r.get('model')} {r.get('variant') or ''} ({r.get('device')}, "
                                      f"{r.get('precision')})".replace("  ", " "),
                             "normals": inp.get("normals_source", ""), "target_faces": "",
                             "triangles": (r.get("mesh") or {}).get("triangles"),
                             "vertices": (r.get("mesh") or {}).get("vertices"),
                             "seconds": (r.get("timings") or {}).get("inference_s"), "normals_seconds": "",
                             "fscore": q.get("fscore@0.01"), "precision": q.get("precision"),
                             "recall": q.get("recall"), "chamfer_l2": q.get("chamfer_l2"), "watertight": "",
                             "source": "generative", "params": Path(f).name})
    return rows, incomplete


# ----------------------------------------------------------------------------- report
def _t(seconds) -> str:
    if seconds in (None, ""):
        return "–"
    return f"{seconds * 1e3:.0f} ms" if seconds < 1 else f"{seconds:.1f} s"


def _n(v, fmt) -> str:
    return "–" if v in (None, "") else fmt.format(v)


def summary(rows: list[dict], incomplete: int, repeats: int) -> str:
    shapes = list(dict.fromkeys(r["shape"] for r in rows))
    lines = ["# Metodi classici contro modelli generativi", "",
             "Stessi ingressi (`data/inputs/<forma>_n<punti>.ply`), stesso riferimento (nuvola densa della mesh "
             "originale) e stessa misura (`quality_metrics`) dello sweep dei modelli generativi. Accuratezza: F-score "
             "all'1% della diagonale (più alto è meglio), precisione (la mesh non inventa), richiamo (non dimentica), "
             "Chamfer-L2 (più basso è meglio). Normali *vere*: della mesh originale; *stimate*: dai punti, come fa il "
             "server per il visore. Tempi dei metodi classici: mediana di "
             f"{repeats} prove sulla CPU di questo PC, stima delle normali esclusa; dei generativi: generazione.", ""]
    # overview: one F-score per shape and method, at full resolution
    columns = [("hull", "unused", "Involucro"), ("alpha", "unused", "Alpha"),
               ("ball_pivoting", "estimate", "BPA (stimate)"), ("poisson", "estimate", "Poisson (stimate)"),
               ("ball_pivoting", "file", "BPA (vere)"), ("poisson", "file", "Poisson (vere)")]
    lines += ["## F-score per forma (mesh a piena risoluzione)", "",
              "| Forma | " + " | ".join(c[2] for c in columns) + " | Miglior generativo |",
              "|---|" + "---|" * (len(columns) + 1)]
    for shape in shapes:
        cells = []
        for method, normals, _ in columns:
            match = [r for r in rows if r["shape"] == shape and r["method"] == method and r["normals"] == normals
                     and r["target_faces"] == "" and r["source"] == "classical"]
            cells.append(_n(match[0]["fscore"] if match else None, "{:.2f}"))
        gen = [r for r in rows if r["shape"] == shape and r["source"] == "generative" and r["fscore"] is not None]
        best = max(gen, key=lambda r: (r["fscore"], -(r["seconds"] or 0))) if gen else None  # ties: the fastest
        cells.append(f"{best['fscore']:.2f} ({best['method']}, {_t(best['seconds'])})" if best else "nessuna mesh completa")
        lines.append(f"| {SHAPE_NAMES.get(shape, shape)} | " + " | ".join(cells) + " |")
    lines += ["", "## Dettaglio", ""]
    for shape in shapes:
        lines += [f"### {SHAPE_NAMES.get(shape, shape)} (`{shape}`)", "",
                  "| Metodo | Normali | Triangoli | Tempo | F-score | Precisione | Richiamo | Chamfer-L2 | Chiusa |",
                  "|---|---|---|---|---|---|---|---|---|"]
        for r in [r for r in rows if r["shape"] == shape]:
            name = r["label"] + (f" ridotta a {r['target_faces']}" if r["target_faces"] else "")
            lines.append(f"| {name} | {NORMALS_LABEL.get(r['normals'], r['normals'] or '–')} | "
                         f"{_n(r['triangles'], '{}')} | {_t(r['seconds'])} | {_n(r['fscore'], '{:.3f}')} | "
                         f"{_n(r['precision'], '{:.2f}')} | {_n(r['recall'], '{:.2f}')} | "
                         f"{_n(r['chamfer_l2'], '{:.5f}')} | "
                         f"{'' if r['watertight'] == '' else ('sì' if r['watertight'] else 'no')} |")
        lines.append("")
    if incomplete:
        lines.append(f"Non in tabella: {incomplete} run dei modelli generativi su queste forme senza una mesh completa "
                     "(troncati di proposito per misurare i ms/token, oppure fermati dal limite di tempo).")
    return "\n".join(lines) + "\n"


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--shapes", nargs="+", default=SCENE_SHAPES)
    ap.add_argument("--points", nargs="+", type=int, default=[2048])
    ap.add_argument("--methods", nargs="+", default=list(classical.METHODS), choices=classical.METHODS)
    ap.add_argument("--target-faces", nargs="*", type=int, default=[1000],
                    help="also decimate every mesh to these triangle counts (none: full resolution only)")
    ap.add_argument("--repeats", type=int, default=3, help="runs per configuration, the median time is kept")
    ap.add_argument("--compare", nargs="*", default=DEFAULT_COMPARE,
                    help="globs of generative run reports to put in the same table")
    ap.add_argument("--no-compare", action="store_true")
    ap.add_argument("--no-meshes", action="store_true", help="do not save the meshes")
    ap.add_argument("--out", type=Path, default=None)
    args = ap.parse_args(argv)

    out = args.out or ROOT / "benchmarks" / "results" / f"classical_{dt.datetime.now():%Y%m%d_%H%M%S}"
    out.mkdir(parents=True, exist_ok=True)
    rows = classical_rows(args.shapes, args.points, args.methods, args.target_faces, args.repeats,
                          None if args.no_meshes else out / "meshes")
    incomplete = 0
    if not args.no_compare:
        gen, incomplete = generative_rows(args.compare, args.shapes, args.points)
        rows += gen
    with (out / "results.csv").open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=FIELDS)
        w.writeheader()
        w.writerows(rows)
    (out / "results.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
    md = summary(rows, incomplete, args.repeats)
    (out / "summary.md").write_text(md, encoding="utf-8")
    print(f"\nsaved in {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
