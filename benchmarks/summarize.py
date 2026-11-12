"""Aggregate run reports (JSON) into Markdown tables (mean ± std over repeated configurations).

    python -m benchmarks.summarize benchmarks/results/<timestamp>/results.json
    python -m benchmarks.summarize "benchmarks/results/runs/*.json" --out docs/risultati_benchmark.md
"""
from __future__ import annotations

import argparse
import glob
import json
import statistics
from collections import defaultdict
from pathlib import Path


def _get(d, path, default=None):
    for k in path.split("."):
        if not isinstance(d, dict) or k not in d:
            return default
        d = d[k]
    return d


def _ms(vals, fmt: str = "{:.2f}"):
    vals = [v for v in vals if isinstance(v, (int, float)) and not isinstance(v, bool)]
    if not vals:
        return "—"
    m = statistics.mean(vals)
    s = statistics.stdev(vals) if len(vals) > 1 else 0.0
    return f"{fmt.format(m)} ± {fmt.format(s)}" if s else fmt.format(m)


def _mb(v):
    return f"{v / 2**20:.0f}" if isinstance(v, (int, float)) and v else "—"


NORMALS_LABEL = {"file": "dalla mesh", "estimated-open3d": "stimate", "unused": "non usate", "estimate": "stimate",
                 None: "—"}


def _normals(r: dict):
    """Which normals the model got: recorded by test_inference.py, or the one requested if the run crashed."""
    return _get(r, "input.normals_source") or {"file": "file", "estimate": "estimated-open3d"}.get(
        _get(r, "input.normals_requested"))


def write_summary(results: list[dict], path: Path | None = None) -> str:
    groups = defaultdict(list)
    for r in results:
        key = (r.get("mode"), r.get("model"), r.get("variant", ""), r.get("device", ""), r.get("precision", ""),
               _get(r, "input.shape"), _get(r, "input.input_points"),
               _normals(r) if r.get("mode") == "real" else None)  # mock runs do not use the normals
        groups[key].append(r)
    lines = ["# Riepilogo benchmark", ""]
    real = [(k, v) for k, v in groups.items() if k[0] == "real"]
    mock = [(k, v) for k, v in groups.items() if k[0] == "mock"]
    if real:
        lines += ["## Inferenza reale", "",
                  "Run *troncati*: 64 token AR per misurare i ms/token; la colonna *Stima completa* li estrapola "
                  "a una mesh da 800 facce. Vertici, triangoli e qualità solo per generazioni complete.", "",
                  "*Normali*: quelle vere della mesh originale (*dalla mesh*) oppure stimate dai punti come fa il "
                  "server per il visore, che manda solo i punti (*stimate*).", "",
                  "| Modello | Device | Prec. | Shape | Punti | Normali | Stato | Inferenza [s] | ms/token (p50) | Token | "
                  "Stima completa [s] | Picco VRAM [MB] | Picco RAM privata [MB] | Vertici | Triangoli | Chamfer-L2 | F-score@1% |",
                  "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|"]
        for (mode, model, var, dev, prec, shape, pts, nrm), rs in sorted(real, key=lambda x: [str(i) for i in x[0]]):
            ok = [r for r in rs if r.get("status") == "ok"]
            full = [r for r in ok if not _get(r, "generation.truncated")]  # complete meshes only
            st = ",".join(sorted({r.get("status", "?") for r in rs}))
            if ok and not full:
                st += " (troncato)"
            lines.append("| " + " | ".join([
                f"{model} {var}", str(dev), str(prec), str(shape), str(pts), NORMALS_LABEL.get(nrm, str(nrm)), st,
                _ms([_get(r, "timings.inference_s") for r in ok]),
                _ms([_get(r, "timings.ms_per_token_p50") for r in ok]),
                _ms([_get(r, "timings.decode_steps") for r in ok], "{:.0f}"),
                _ms([_get(r, "timings.extrapolated_full_inference_s") for r in ok]),
                _mb(max([_get(r, "memory.vram_peak_allocated_bytes") or 0 for r in rs], default=0)),
                _mb(max([_get(r, "memory.host_private_peak_bytes") or _get(r, "memory.host_rss_peak_bytes") or 0
                         for r in rs], default=0)),
                _ms([_get(r, "mesh.vertices") for r in full], "{:.0f}"),
                _ms([_get(r, "mesh.triangles") for r in full], "{:.0f}"),
                _ms([_get(r, "quality.chamfer_l2") for r in full], "{:.5f}"),
                _ms([(_get(r, "quality") or {}).get("fscore@0.01") for r in full], "{:.3f}"),  # key contains a dot
            ]) + " |")
        lines.append("")
    if mock:
        lines += ["## Mock / profiling (senza pesi)", "",
                  "| Modello | Device | Punti | Parametri [M] | FLOPs/mesh [TFLOP] | Pesi fp16 [MB] | KV fp16 [MB] | "
                  "Surrogato ms/token | Decoding stimato [s] |",
                  "|---|---|---|---|---|---|---|---|---|"]
        for (mode, model, var, dev, prec, shape, pts, _nrm), rs in sorted(mock, key=lambda x: [str(i) for i in x[0]]):
            r = rs[0]
            meta = _get(r, "params.official_architecture_meta", {}) or {}
            params = meta.get("total") or _get(r, "params.analytic_total")
            lines.append("| " + " | ".join([
                f"{model} {var}", str(dev), str(pts), f"{params / 1e6:.1f}" if params else "—",
                f"{(_get(r, 'flops.total') or 0) / 1e12:.2f}",
                _mb(_get(r, "memory_theoretical.weights_bytes.fp16")),
                _mb(_get(r, "memory_theoretical.kv_cache_peak_bytes.fp16")),
                _ms([_get(r, "timings.surrogate.ms_per_step_full_depth") for r in rs]),
                _ms([_get(r, "timings.surrogate.extrapolated_decode_s") for r in rs]),
            ]) + " |")
        lines.append("")
    # headset feasibility: one row per model (estimates do not depend on the input point count)
    seen = {}
    for r in results:
        if _get(r, "headset.headsets") and r.get("model") not in seen:
            seen[r.get("model")] = r
    if seen:
        lines += ["## Stima su visore standalone (roofline, fp16 nominale | int4 best case)", "",
                  "| Modello | Visore | Memoria richiesta [MB] | Budget app [MB] | Entra in memoria | Latenza [s] | "
                  "Latenza int4 best [s] | Batteria/mesh [%] | Offload edge [s] |",
                  "|---|---|---|---|---|---|---|---|---|"]
        for model, r in seen.items():
            for dev, h in r["headset"]["headsets"].items():
                n = h["nominal_fp16"]
                lines.append("| " + " | ".join([
                    model, dev, _mb(n["memory"]["total"]), _mb(n["memory_budget_bytes"]),
                    "sì" if n["fits_memory"] else "**no (OOM)**", f"{n['latency_s']:.1f}",
                    f"{h['best_int4']['latency_s']:.1f}", f"{n.get('battery_pct_per_mesh', 0):.2f}",
                    f"{r['headset']['offload']['total_s']:.2f}"]) + " |")
        lines.append("")
    md = "\n".join(lines)
    if path:
        Path(path).write_text(md + "\n", encoding="utf-8")
    return md


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("inputs", nargs="+", help="results.json files or globs of single-run JSON reports")
    ap.add_argument("--out", type=Path, default=None)
    args = ap.parse_args()
    results = []
    for pattern in args.inputs:
        for f in sorted(glob.glob(pattern)):
            data = json.loads(Path(f).read_text(encoding="utf-8"))
            results.extend(data if isinstance(data, list) else [data])
    print(write_summary(results, args.out))


if __name__ == "__main__":
    main()
