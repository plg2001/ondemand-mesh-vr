"""Generate the compute-requirement tables used in docs/requisiti_calcolo.md (no weights / GPU needed).

    python -m benchmarks.requirements_tables                    # prints Markdown
    python -m benchmarks.requirements_tables --faces 400 800 1600 --out docs/tabelle_requisiti.md
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from benchmarks.estimator import estimate_device, estimate_offload, load_profiles  # noqa: E402
from benchmarks.flops import estimate_workload  # noqa: E402

SPECS = {"MeshAnything V2": "meshanything_v2.yaml", "TreeMeshGPT 7bit": "treemeshgpt.yaml", "FastMesh V1K": "fastmesh.yaml"}
GIB = 2**30


def load_spec(fname: str) -> dict:
    data = yaml.safe_load((ROOT / "models" / "specs" / fname).read_text(encoding="utf-8"))
    data.pop("variants", None)
    return data


def tables(faces_list: list[int]) -> str:
    prof = load_profiles()
    budgets = {k: d["app_memory_budget_bytes"]["value"] for k, d in prof["devices"].items() if d.get("kind") == "headset"}
    out = ["## Parametri, pesi e KV cache", "",
           "| Modello | Parametri | Pesi fp32 | Pesi fp16 | Pesi int8 | Pesi int4 | KV/token fp16 | Token max | KV a lunghezza max (fp16) |",
           "|---|---|---|---|---|---|---|---|---|"]
    for name, f in SPECS.items():
        s = load_spec(f)
        w = estimate_workload(s)
        tmax = s["generation"]["max_new_tokens"] + s["generation"]["cond_tokens"]
        out.append(f"| {name} | {w.params_total / 1e6:.0f} M | " + " | ".join(
            f"{w.weights_bytes[p] / GIB:.2f} GiB" for p in ("fp32", "fp16", "int8", "int4"))
            + f" | {w.kv_bytes_per_token['fp16'] / 1024:.0f} KiB | {tmax} | {w.kv_bytes_per_token['fp16'] * tmax / GIB:.2f} GiB |")
    out += ["", "## Memoria richiesta vs budget per app (PSS) del visore", "",
            "Pesi + KV cache a fine generazione + attivazioni + runtime (0.37 GiB). Budget: Quest 2 = "
            f"{budgets['quest2'] / GIB:.2f} GiB, Quest 3/3S = {budgets['quest3s'] / GIB:.2f} GiB (documentazione Meta).", "",
            "| Modello | Precisione | 800 facce | Lunghezza massima | Quota budget Quest 2 (800 f.) | Entra in Quest 2 (max) | Entra in Quest 3S (max) |",
            "|---|---|---|---|---|---|---|"]
    for name, f in SPECS.items():
        s = load_spec(f)
        typ = estimate_workload(s, n_faces=800)
        mx = estimate_workload(s, n_steps=s["generation"]["max_new_tokens"])
        for prec in ("fp32", "fp16", "int4"):
            e_typ = estimate_device(typ, "quest2", prof, prec)
            e_mx2 = estimate_device(mx, "quest2", prof, prec)
            e_mx3 = estimate_device(mx, "quest3s", prof, prec)
            out.append(f"| {name} | {prec} | {e_typ['memory']['total'] / GIB:.2f} GiB | {e_mx2['memory']['total'] / GIB:.2f} GiB | "
                       f"{100 * e_typ['memory']['total'] / budgets['quest2']:.0f}% | "
                       f"{'sì' if e_mx2['fits_memory'] else '**no**'} | {'sì' if e_mx3['fits_memory'] else '**no**'} |")
    out += ["", "## Latenza ed energia stimate per mesh (roofline)", "",
            "Nominale fp16; tra parentesi l'intervallo [best case int4, worst case fp16].", "",
            "| Modello | Facce | Token AR | FLOPs | Quest 2 [s] | Quest 3S [s] | Batteria Quest 2 / mesh | Mesh per carica (Q2) | Edge RTX 4090 + Wi-Fi 6 [s] |",
            "|---|---|---|---|---|---|---|---|---|"]
    for name, f in SPECS.items():
        s = load_spec(f)
        for faces in faces_list:
            w = estimate_workload(s, n_faces=faces)
            q2 = {c: estimate_device(w, "quest2", prof, p, c) for c, p in (("nominal", "fp16"), ("best", "int4"), ("worst", "fp16"))}
            q3 = {c: estimate_device(w, "quest3s", prof, p, c) for c, p in (("nominal", "fp16"), ("best", "int4"), ("worst", "fp16"))}
            off = estimate_offload(w, prof, n_faces=faces)
            out.append(
                f"| {name} | {faces} | {w.decode_steps} | {w.flops_total / 1e12:.2f} T | "
                f"{q2['nominal']['latency_s']:.0f} [{q2['best']['latency_s']:.0f}–{q2['worst']['latency_s']:.0f}] | "
                f"{q3['nominal']['latency_s']:.0f} [{q3['best']['latency_s']:.0f}–{q3['worst']['latency_s']:.0f}] | "
                f"{q2['nominal']['battery_pct_per_mesh']:.2f}% | {q2['nominal']['meshes_per_charge']:.0f} | {off['total_s']:.1f} |")
    return "\n".join(out)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--faces", nargs="+", type=int, default=[400, 800, 1500])
    ap.add_argument("--out", type=Path, default=None)
    args = ap.parse_args()
    md = tables(args.faces)
    if args.out:
        args.out.write_text(md + "\n", encoding="utf-8")
    print(md)


if __name__ == "__main__":
    main()
