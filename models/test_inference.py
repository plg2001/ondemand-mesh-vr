"""Point cloud -> mesh inference & profiling for MeshAnything V2 / FastMesh / TreeMeshGPT.

Examples
    python models/test_inference.py --model meshanything --points 500                 # auto: real if feasible, else mock
    python models/test_inference.py --model treemeshgpt --points 2048 --mode mock --surrogate-layers 2
    python models/test_inference.py --model meshanything --points 2048 --mode real --device cpu --max-new-tokens 64
    python models/test_inference.py --model fastmesh --points 500 --mode real --device cuda --force   # OOM demo on 2 GB GPUs

Modes
    real  official code + pretrained weights, full (or --max-new-tokens truncated) generation, mesh export
    mock  no weights/GPU needed: analytic + FlopCounterMode(meta) FLOPs, theoretical memory footprint,
          optional timing of a k-layer random-weight surrogate on the device (--surrogate-layers k)
    auto  real if repo + weights are present and the estimated memory fits the device, otherwise mock
"""
from __future__ import annotations

import argparse
import csv
import datetime as dt
import gc
import json
import sys
import time
import traceback
import uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
# models/ is NOT a package (see mesh_wrappers/__init__.py); ROOT gives access to the benchmarks package.
for p in (str(HERE), str(ROOT)):
    if p not in sys.path:
        sys.path.insert(0 if p == str(HERE) else 1, p)

import numpy as np  # noqa: E402
import torch  # noqa: E402

from benchmarks import estimator as est  # noqa: E402
from benchmarks.flops import (estimate_workload, human_bytes, human_flops, meta_flops_step,  # noqa: E402
                              decoder_step_flops, time_surrogate)
from benchmarks.profiler import (CudaMemory, GenerationBudgetExceeded, HostMemoryMonitor, NvmlMonitor,  # noqa: E402
                                 RaplMonitor, StageTimer, cuda_sync, system_info)
from mesh_wrappers.base import GenerationResult  # noqa: E402
from mesh_wrappers import flash_shim  # noqa: E402
from mesh_wrappers.loading import DATA_INPUTS, DATA_OUTPUTS, count_parameters, free_memory_bytes, read_lock  # noqa: E402
from mesh_wrappers.mesh_io import cleanup, export_mesh, mesh_stats, quality_metrics, transform_vertices, triangles_to_mesh  # noqa: E402
from mesh_wrappers.pointcloud import estimate_normals, load_pointcloud, resample, storage_bytes, unit  # noqa: E402
from mesh_wrappers.registry import MODEL_NAMES, get_wrapper_class  # noqa: E402

OOM_TYPES = (torch.OutOfMemoryError, MemoryError)


# ============================================================================ helpers
def rel(p) -> str:
    """Repository-relative POSIX path for reports (keeps absolute paths out of shared results)."""
    try:
        return Path(p).resolve().relative_to(ROOT).as_posix()
    except (ValueError, OSError):
        return str(p)


def parse_args(argv=None) -> argparse.Namespace:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--model", required=True, help=f"{' | '.join(MODEL_NAMES)}")
    ap.add_argument("--variant", default=None, help="fastmesh: V1K|V4K, treemeshgpt: 7bit|9bit")
    ap.add_argument("--points", type=int, default=2048, help="sparse cloud size (e.g. 100, 500, 2048)")
    ap.add_argument("--input", type=Path, default=None, help="explicit .ply/.xyz/.npy point cloud")
    ap.add_argument("--shape", default="bunny", help="shape from data/inputs/manifest.json when --input is not given")
    ap.add_argument("--mode", choices=["auto", "real", "mock"], default="auto")
    ap.add_argument("--device", default="auto", help="auto | cpu | cuda | cuda:N")
    ap.add_argument("--precision", default="auto", choices=["auto", "fp32", "fp16", "bf16", "fp16-full", "bf16-full"],
                    help="fp16/bf16 = autocast with fp32 weights (upstream behaviour); *-full also casts weights")
    ap.add_argument("--attn", choices=["auto", "flash", "sdpa"], default="auto")
    ap.add_argument("--normals", choices=["auto", "file", "estimate"], default="auto")
    ap.add_argument("--upsample", choices=["repeat", "jitter"], default="repeat")
    ap.add_argument("--max-new-tokens", type=int, default=None, help="cap AR steps (truncated run, extrapolated)")
    ap.add_argument("--max-seq-len", type=int, default=None, help="TreeMeshGPT max_seq_len (default 30000)")
    ap.add_argument("--time-budget-s", type=float, default=None,
                    help="stop the AR generation after this many seconds (truncated run, timings kept)")
    ap.add_argument("--assume-faces", type=int, default=None, help="mock: output faces used for the token budget")
    ap.add_argument("--surrogate-layers", type=int, default=0, help="mock: time k decoder layers on the device")
    ap.add_argument("--repeats", type=int, default=1)
    ap.add_argument("--warmup", type=int, default=0)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--quality", action="store_true", help="Chamfer / F-score vs the dense reference cloud")
    ap.add_argument("--force", action="store_true", help="run real mode even if memory looks insufficient")
    ap.add_argument("--no-export", action="store_true")
    ap.add_argument("--formats", nargs="+", default=["obj", "glb"])
    ap.add_argument("--out-dir", type=Path, default=DATA_OUTPUTS)
    ap.add_argument("--json-out", type=Path, default=None)
    ap.add_argument("--csv-out", type=Path, default=None)
    ap.add_argument("--quiet", action="store_true", help="no table (used by the benchmark sweep)")
    return ap.parse_args(argv)


def resolve_device(arg: str) -> str:
    if arg == "auto":
        return "cuda" if torch.cuda.is_available() else "cpu"
    if arg.startswith("cuda") and not torch.cuda.is_available():
        raise SystemExit("CUDA requested but torch.cuda.is_available() is False")
    return arg


def resolve_input(args) -> tuple[Path, dict]:
    if args.input:
        return args.input, {"shape": args.input.stem, "reference": None}
    manifest_path = DATA_INPUTS / "manifest.json"
    manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else {"shapes": {}}
    entry = manifest["shapes"].get(args.shape)
    if entry is None or str(args.points) not in entry["clouds"]:
        import subprocess

        print(f"[input] generating {args.shape} n={args.points} ...")
        subprocess.run([sys.executable, str(DATA_INPUTS / "generate_pointclouds.py"), "--shapes", args.shape,
                        "--counts", str(args.points)], check=True)
        manifest = json.loads(manifest_path.read_text())
        entry = manifest["shapes"][args.shape]
    path = DATA_INPUTS / entry["clouds"][str(args.points)]["files"]["ply"]["path"]
    return path, {"shape": args.shape, "reference": DATA_INPUTS / entry["reference"]}


def prepare_cloud(args, wrapper, path: Path) -> dict:
    t0 = time.perf_counter()
    xyz, nrm = load_pointcloud(path)
    src = "file"
    if wrapper.needs_normals and (nrm is None or args.normals == "estimate"):
        if args.normals == "file":
            raise SystemExit(f"{path} has no normals; use --normals estimate")
        nrm, src = estimate_normals(xyz), "estimated-open3d"
    elif nrm is not None:
        nrm = unit(nrm)
    rng = np.random.default_rng(args.seed)
    rx, rn, info = resample(xyz, nrm, wrapper.native_points, args.upsample, rng,
                            downsample=getattr(wrapper, "downsample", "random"))
    norm = wrapper.normalizer(rx)
    out = {"xyz_raw": xyz, "xyz": norm.apply(rx), "normals": rn, "normalizer": norm,
           "info": dict(info, path=rel(path), normals_source=src if wrapper.needs_normals else "unused",
                        storage_bytes_xyz_f32=storage_bytes(len(xyz)),
                        file_bytes=path.stat().st_size, preprocess_s=time.perf_counter() - t0)}
    return out


def precision_for(args, device: str) -> str:
    if args.precision != "auto":
        return args.precision
    return "fp16" if device.startswith("cuda") else "fp32"


def required_bytes(work, precision: str, device: str) -> float:
    wb = work.weights_bytes["fp16" if precision.endswith("-full") else "fp32"]
    kv = work.peak_kv_bytes["fp32" if precision == "fp32" else "fp16"]
    overhead = 0.5e9 if device.startswith("cuda") else 1.0e9  # CUDA context / Python + libs
    return wb + kv + work.activation_bytes_fp16 * 2 + overhead


# ============================================================================ modes
def run_mock(args, wrapper, spec, device: str, precision: str, result: dict) -> None:
    gen = spec["generation"]
    faces = args.assume_faces or gen.get("default_faces", 800)
    work = estimate_workload(spec, n_faces=faces)
    dec = dict(work.decoder)
    dec_meta = {k: dec[k] for k in ("d_model", "n_layers", "n_heads", "ffn_dim", "vocab", "ffn")}
    t0 = time.perf_counter()
    counted = {}
    if wrapper.repo_available():
        try:
            meta_model = wrapper.instantiate_meta()
            counted = {"total": count_parameters(meta_model)}
            counted.update({n: count_parameters(m) for n, m in meta_model.named_children()})
            del meta_model
        except Exception as e:  # upstream import problems must not break mock mode
            counted = {"error": f"{type(e).__name__}: {e}"}
    meta_step = meta_flops_step(dec_meta, work.cond_tokens)
    result["params"] = {"analytic_total": work.params_total, "official_architecture_meta": counted}
    result["flops"] = {
        "total": work.flops_total, "by_component": work.flops_by_component,
        "decode_step_at_cond_analytic": decoder_step_flops(dec, work.cond_tokens),
        "decode_step_at_cond_flopcounter_meta": meta_step,
    }
    result["memory_theoretical"] = {
        "weights_bytes": work.weights_bytes, "kv_cache_peak_bytes": work.peak_kv_bytes,
        "kv_bytes_per_token": work.kv_bytes_per_token, "activation_bytes_fp16": work.activation_bytes_fp16,
        "required_on_device_bytes": required_bytes(work, precision, device),
    }
    result["mesh"] = {"assumed_faces": faces, "decode_steps_assumed": work.decode_steps}
    timings = {"analysis_s": time.perf_counter() - t0}
    if args.surrogate_layers > 0:
        dtype = torch.float16 if (device.startswith("cuda") and precision != "fp32") else torch.float32
        try:
            sur = time_surrogate(dec_meta, device, dtype, args.surrogate_layers, work.cond_tokens)
            sur["extrapolated_decode_s"] = sur["ms_per_step_full_depth"] * work.decode_steps / 1e3
            timings["surrogate"] = sur
        except OOM_TYPES as e:
            timings["surrogate"] = {"status": "oom", "error": str(e)[:300]}
    result["timings"] = timings
    result["workload"] = work.to_dict()
    result["_work"] = work


def run_real(args, wrapper, spec, device: str, precision: str, cloud: dict, ref_path, result: dict) -> None:
    host = HostMemoryMonitor()
    cmem = CudaMemory(device)
    cmem.reset()
    with host:
        t0 = time.perf_counter()
        load_info = wrapper.load()
        cuda_sync(device)
        t_load = time.perf_counter() - t0
        after_load = cmem.snapshot()
        rss_after_load = host.proc.memory_info().rss
        timer = StageTimer(device)
        for stage, obj, attr in wrapper.stage_targets():
            timer.wrap(stage, obj, attr)
        runs = []
        gen = None
        for i in range(args.warmup + args.repeats):
            timer.reset()
            gc.collect()
            cmem.reset()
            with NvmlMonitor() as nv, RaplMonitor() as rapl:
                cuda_sync(device)
                t1 = time.perf_counter()
                timer.deadline = t1 + args.time_budget_s if args.time_budget_s else None
                try:
                    gen = wrapper.generate(cloud["xyz"], cloud["normals"], args.max_new_tokens, args.seed + i)
                except GenerationBudgetExceeded as e:
                    gen = GenerationResult(triangles=None, n_tokens=-1, truncated=True,
                                           extra={"stopped_by": "time_budget", "detail": str(e)})
                cuda_sync(device)
                t_gen = time.perf_counter() - t1
            if i < args.warmup:
                continue
            runs.append({"generate_s": t_gen, **timer.summary(), **cmem.snapshot(), **nv.result(), **rapl.result()})
        timer.remove()
    t_post0 = time.perf_counter()
    if gen.mesh is not None:
        mesh = cleanup(gen.mesh)
    elif gen.triangles is not None:
        mesh = triangles_to_mesh(gen.triangles)
    else:  # stopped by the time budget: no geometry, only timings
        mesh = triangles_to_mesh(np.zeros((0, 3, 3)))
    k = wrapper.output_scale  # model output frame -> normalised input frame -> input cloud frame
    mesh = transform_vertices(mesh, lambda v: cloud["normalizer"].invert(np.asarray(v) * k))
    t_post = time.perf_counter() - t_post0
    last = runs[-1]
    steps = last.get("decode_steps", 0)
    cap = args.max_new_tokens
    truncated = bool(cap and steps + 1 >= cap) or gen.truncated  # the prefill already emits the 1st token
    gen_times = [r["generate_s"] for r in runs]
    result["timings"] = {
        "preprocess_s": cloud["info"]["preprocess_s"], "load_s": t_load,
        "inference_s": float(np.mean(gen_times)), "inference_s_std": float(np.std(gen_times)),
        "inference_s_runs": gen_times, "postprocess_s": t_post,
        **{k: v for k, v in last.items() if k.endswith("_s") or k.startswith("ms_per") or k.endswith("_calls")},
        "decode_steps": steps,
    }
    if steps:
        result["timings"]["tokens_per_s"] = steps / max(1e-9, last.get("decode_s", 0.0))
    result["memory"] = {
        "host_rss_baseline_bytes": host.baseline, "host_rss_after_load_bytes": rss_after_load,
        "host_rss_peak_bytes": host.peak, "host_os_peak_bytes": HostMemoryMonitor.os_peak_bytes(),
        "host_private_peak_bytes": host.private_peak or None,
        "vram_weights_bytes": after_load.get("allocated_bytes"),
        "vram_peak_allocated_bytes": max((r.get("peak_allocated_bytes") or 0) for r in runs) or None,
        "vram_peak_reserved_bytes": max((r.get("peak_reserved_bytes") or 0) for r in runs) or None,
        "gpu_device_mem_peak_bytes": last.get("gpu_device_mem_peak_bytes"),
    }
    result["energy"] = {k: last[k] for k in last if "energy" in k or "power" in k}
    total_vram = after_load.get("device_total_bytes")
    peak_res = result["memory"]["vram_peak_reserved_bytes"]
    if total_vram and peak_res and peak_res > total_vram:
        # Windows WDDM "CUDA Sysmem Fallback Policy": allocations beyond VRAM spill into shared system RAM
        # over PCIe instead of raising OOM -> the run "works" but at a fraction of the speed.
        result["memory"]["vram_spilled_to_system_ram"] = True
        result["memory"]["vram_spill_bytes"] = peak_res - total_vram
    stats = mesh_stats(mesh)
    if stats["triangles"]:
        stats["tokens_per_face"] = steps / stats["triangles"] if steps else None
    result["mesh"] = stats
    result["generation"] = {"truncated": truncated, "load_info": {k: v for k, v in load_info.items() if k != "config"},
                            **{k: v for k, v in gen.extra.items() if isinstance(v, (int, float, str, bool, type(None)))}}
    work = estimate_workload(spec, n_faces=stats["triangles"] or None, n_steps=steps or None)
    result["flops"] = {"total": work.flops_total, "by_component": work.flops_by_component,
                       "note": "analytic model evaluated at the measured number of AR steps"}
    if truncated:
        exp_work = estimate_workload(spec, n_faces=args.assume_faces)
        per_tok = result["timings"].get("ms_per_token_p50", 0) / 1e3  # median: robust to first-touch page faults
        result["timings"]["extrapolated_full_inference_s"] = (
            result["timings"].get("encoder_s", 0) + result["timings"].get("prefill_s", 0)
            + per_tok * exp_work.decode_steps)
        result["timings"]["extrapolated_steps"] = exp_work.decode_steps
        work = exp_work
    result["workload"] = work.to_dict()
    result["_work"] = work
    # validate the roofline model against what we just measured on this machine
    prof = est.match_local_profile(result.get("device_name", ""))
    p50 = result["timings"].get("ms_per_token_p50")
    if prof and p50 and steps:
        wbytes = 2.0 if precision.endswith("-full") else 4.0
        kvb = 4.0 if (precision == "fp32" or not device.startswith("cuda")) else 2.0
        pred = est.predict_step_ms(work, prof, wbytes, kvb, work.cond_tokens + steps / 2,
                                   "fp32" if precision == "fp32" else "fp16")
        pred["measured_p50_ms"] = p50
        pred["ratio_measured_over_predicted"] = p50 / pred["predicted_ms"]
        result["roofline_validation"] = pred
    if not args.no_export and stats["triangles"] and not truncated:
        stem = f"{Path(cloud['info']['path']).stem}_{wrapper.name}_{wrapper.variant}_{device.split(':')[0]}"
        files = export_mesh(mesh, args.out_dir / wrapper.name / stem, args.formats)
        result["mesh"]["files"] = {k: dict(v, path=rel(v["path"])) for k, v in files.items()}
    if args.quality and ref_path is not None and Path(ref_path).exists():
        ref = np.load(ref_path)[:, :3]
        result["quality"] = quality_metrics(mesh, ref)


# ============================================================================ reporting
def fmt_s(x):
    if x is None:
        return "n/d"
    return f"{x * 1e3:.1f} ms" if x < 1 else (f"{x:.2f} s" if x < 120 else f"{x / 60:.1f} min")


def table_rows(r: dict) -> list[tuple[str, str]]:
    rows = [("Modello", f"{r['model_display']} ({r['variant']})"),
            ("Modalità / device", f"{r['mode']} / {r['device']} ({r.get('device_name', '')})"),
            ("Precisione / attenzione", f"{r['precision']} / {r['attn_backend']}"),
            ("Stato", r["status"] + (f" — {r['error'][:120]}" if r.get("error") else ""))]
    inp = r.get("input", {})
    if inp:
        rows.append(("Punti input → punti modello",
                     f"{inp.get('input_points')} → {inp.get('model_points')} ({inp.get('resample')}, normali: {inp.get('normals_source')})"))
        rows.append(("Footprint nuvola sparsa (xyz f32)", human_bytes(inp.get("storage_bytes_xyz_f32", 0))))
    t = r.get("timings", {})
    if r["mode"] == "real" and r["status"] == "ok":
        rows.append(("Tempo inferenza (per point cloud)", f"{fmt_s(t.get('inference_s'))} ± {fmt_s(t.get('inference_s_std'))}"))
        parts = [f"encoder {fmt_s(t.get('encoder_s'))}", f"prefill {fmt_s(t.get('prefill_s'))}",
                 f"decoding {fmt_s(t.get('decode_s'))}"]
        for k in ("vertex_refine_s", "face_gen_s", "detokenize_s"):
            if k in t:
                parts.append(f"{k[:-2]} {fmt_s(t[k])}")
        rows.append(("  di cui", ", ".join(parts)))
        rows.append(("Caricamento pesi / post-processing", f"{fmt_s(t.get('load_s'))} / {fmt_s(t.get('postprocess_s'))}"))
        if t.get("decode_steps"):
            rows.append(("Token generati / latenza per token",
                         f"{t['decode_steps']} / {t.get('ms_per_token_mean', 0):.1f} ms (p95 {t.get('ms_per_token_p95', 0):.1f} ms, "
                         f"{t.get('ms_per_token_first10pct', 0):.1f}→{t.get('ms_per_token_last10pct', 0):.1f} ms)"))
        if "extrapolated_full_inference_s" in t:
            rows.append(("Stima generazione completa", f"{fmt_s(t['extrapolated_full_inference_s'])} ({t['extrapolated_steps']} token)"))
        m = r.get("memory", {})
        rows.append(("Picco VRAM (allocata / riservata)",
                     f"{human_bytes(m.get('vram_peak_allocated_bytes'))} / {human_bytes(m.get('vram_peak_reserved_bytes'))}"
                     + (f" ⚠ oltre la VRAM fisica: {human_bytes(m['vram_spill_bytes'])} in RAM condivisa (WDDM sysmem fallback)"
                        if m.get("vram_spilled_to_system_ram") else "")
                     if m.get("vram_peak_allocated_bytes") else "n/d (CPU)"))
        rows.append(("Picco RAM host (working set / privata)",
                     f"{human_bytes(m.get('host_rss_peak_bytes'))} / {human_bytes(m.get('host_private_peak_bytes'))}"))
        e = r.get("energy", {})
        rows.append(("Energia GPU", f"{e['gpu_energy_j']:.1f} J ({e['gpu_energy_source']})" if "gpu_energy_j" in e
                     else e.get("gpu_energy_source", "n/d")))
        mesh = r.get("mesh", {})
        rows.append(("Vertici / Triangoli", f"{mesh.get('vertices')} / {mesh.get('triangles')}"
                     + (f" ({mesh['tokens_per_face']:.2f} token/faccia)" if mesh.get("tokens_per_face") else "")))
        files = mesh.get("files", {})
        if files:
            rows.append(("File mesh", ", ".join(f"{v['path']} ({human_bytes(v['bytes'])})" for v in files.values())))
        if r.get("quality"):
            q = r["quality"]
            rows.append(("Qualità vs riferimento denso", f"Chamfer-L2 {q['chamfer_l2']:.5f}, F-score@1% {q['fscore@0.01']:.3f}"))
    elif r["mode"] == "mock":
        p = r.get("params", {})
        meta = p.get("official_architecture_meta", {})
        rows.append(("Parametri", f"{p.get('analytic_total', 0) / 1e6:.0f} M analitici"
                     + (f", {meta['total'] / 1e6:.1f} M dall'architettura ufficiale (meta)" if meta.get("total") else "")))
        f = r.get("flops", {})
        rows.append(("FLOPs step di decoding (analitico / FlopCounter)",
                     f"{human_flops(f.get('decode_step_at_cond_analytic', 0))} / {human_flops(f.get('decode_step_at_cond_flopcounter_meta', 0))}"))
        mt = r.get("memory_theoretical", {})
        w = mt.get("weights_bytes", {})
        rows.append(("Pesi fp32 / fp16 / int8 / int4", " / ".join(human_bytes(w.get(k, 0)) for k in ("fp32", "fp16", "int8", "int4"))))
        rows.append(("KV cache a fine generazione (fp16)", human_bytes(mt.get("kv_cache_peak_bytes", {}).get("fp16", 0))))
        rows.append(("Memoria richiesta su questo device", human_bytes(mt.get("required_on_device_bytes", 0))))
        mesh = r.get("mesh", {})
        rows.append(("Ipotesi di output", f"{mesh.get('assumed_faces')} facce → {mesh.get('decode_steps_assumed')} token AR"))
        sur = t.get("surrogate")
        if sur:
            rows.append(("Surrogate misurato su device", f"{sur.get('ms_per_step_full_depth', 0):.1f} ms/token (da {sur.get('layers_run')} layer) → "
                         f"decoding ≈ {fmt_s(sur.get('extrapolated_decode_s'))}" if "ms_per_step_full_depth" in sur else str(sur)))
    if "flops" in r:
        rows.append(("FLOPs stimati per mesh", human_flops(r["flops"].get("total", 0))))
    rv = r.get("roofline_validation")
    if rv:
        rows.append(("Validazione roofline (questo device)",
                     f"previsto {rv['predicted_ms']:.1f} ms/token ({rv['bound']}-bound) vs misurato {rv['measured_p50_ms']:.1f} ms "
                     f"(×{rv['ratio_measured_over_predicted']:.2f})"))
    for key, h in (r.get("headset") or {}).get("headsets", {}).items():
        rows.append((f"Stima {h['nominal_fp16']['label'].split('(')[0].strip()}",
                     est.verdict(h["nominal_fp16"]) + f" | best case int4: ≈{h['best_int4']['latency_s']:.0f} s"))
    off = (r.get("headset") or {}).get("offload")
    if off:
        rows.append(("Offload su edge (RTX 4090, Wi-Fi 6)",
                     f"≈{fmt_s(off['total_s'])} (calcolo {fmt_s(off['edge_compute_s'])} + rete {fmt_s(off['network_s'])})"))
    return rows


def print_table(r: dict) -> None:
    rows = table_rows(r)
    try:
        from rich.console import Console
        from rich.table import Table

        tab = Table(title=f"{r['model_display']} — {r['mode']} — {r.get('input', {}).get('input_points', '?')} punti")
        tab.add_column("Metrica", style="bold")
        tab.add_column("Valore")
        for k, v in rows:
            tab.add_row(k, v)
        Console().print(tab)
    except ImportError:
        w = max(len(k) for k, _ in rows)
        for k, v in rows:
            print(f"{k:<{w}} | {v}")


def flatten(d: dict, prefix: str = "") -> dict:
    out = {}
    for k, v in d.items():
        key = f"{prefix}{k}"
        if isinstance(v, dict):
            out.update(flatten(v, key + "."))
        elif isinstance(v, (list, tuple)):
            out[key] = json.dumps(v)
        else:
            out[key] = v
    return out


CSV_KEYS = ["run_id", "timestamp", "model", "variant", "mode", "status", "device", "device_name", "precision",
            "attn_backend", "input.shape", "input.input_points", "input.model_points", "input.resample",
            "timings.inference_s", "timings.inference_s_std", "timings.encoder_s", "timings.prefill_s",
            "timings.decode_s", "timings.decode_steps", "timings.ms_per_token_mean", "timings.ms_per_token_p95",
            "timings.face_gen_s", "timings.load_s", "timings.extrapolated_full_inference_s",
            "timings.surrogate.ms_per_step_full_depth", "timings.surrogate.extrapolated_decode_s",
            "memory.vram_peak_allocated_bytes", "memory.vram_peak_reserved_bytes", "memory.vram_spilled_to_system_ram", "memory.host_rss_peak_bytes", "memory.host_private_peak_bytes",
            "memory_theoretical.required_on_device_bytes", "energy.gpu_energy_j", "flops.total",
            "mesh.vertices", "mesh.triangles", "mesh.tokens_per_face", "mesh.assumed_faces",
            "quality.chamfer_l2", "quality.fscore@0.01",
            "headset.headsets.quest2.nominal_fp16.latency_s", "headset.headsets.quest2.nominal_fp16.fits_memory",
            "headset.headsets.quest2.nominal_fp16.battery_pct_per_mesh",
            "headset.headsets.quest3s.nominal_fp16.latency_s", "headset.headsets.quest3s.nominal_fp16.fits_memory",
            "headset.headsets.quest3s.nominal_fp16.battery_pct_per_mesh", "headset.offload.total_s",
            "roofline_validation.predicted_ms", "roofline_validation.ratio_measured_over_predicted", "error"]


def append_csv(path: Path, r: dict) -> None:
    flat = flatten(r)
    path.parent.mkdir(parents=True, exist_ok=True)
    new = not path.exists()
    with open(path, "a", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=CSV_KEYS, extrasaction="ignore")
        if new:
            w.writeheader()
        w.writerow({k: flat.get(k) for k in CSV_KEYS})


def json_default(o):
    if isinstance(o, (np.integer,)):
        return int(o)
    if isinstance(o, (np.floating,)):
        return float(o)
    if isinstance(o, np.ndarray):
        return o.tolist()
    if isinstance(o, Path):
        return str(o)
    return str(o)


# ============================================================================ main
def main(argv=None) -> int:
    args = parse_args(argv)
    device = resolve_device(args.device)
    precision = precision_for(args, device)
    cls = get_wrapper_class(args.model)
    max_seq = args.max_seq_len
    wrapper = cls(variant=args.variant, device=device, precision=precision, max_seq_len=max_seq)
    spec = wrapper.spec()
    result = {"run_id": uuid.uuid4().hex[:10], "timestamp": dt.datetime.now().isoformat(timespec="seconds"),
              "model": wrapper.name, "model_display": wrapper.display_name, "variant": wrapper.variant,
              "device": device, "device_name": torch.cuda.get_device_name(device) if device.startswith("cuda") else
              system_info().get("cpu"), "precision": precision, "status": "ok",
              "args": {k: (rel(v) if isinstance(v, Path) else v) for k, v in vars(args).items()},
              "upstream": read_lock().get("repos", {}).get(wrapper.name)}

    path, meta = resolve_input(args)
    cloud = prepare_cloud(args, wrapper, path)
    result["input"] = dict(cloud["info"], shape=meta["shape"], normalizer=cloud["normalizer"].to_dict())

    # ---- mode resolution
    mode = args.mode
    repo_ok = wrapper.repo_available()
    weights_ok, weights_msg = wrapper.weights_available() if repo_ok else (False, "repository not cloned")
    need = required_bytes(estimate_workload(spec), precision, device)
    free = free_memory_bytes(device)
    result["preflight"] = {"repo_available": repo_ok, "weights": str(weights_msg).replace(str(Path.home()), "~"),
                           "required_bytes_estimate": need,
                           "free_bytes": free}
    if mode == "auto":
        mode = "real" if (repo_ok and weights_ok and need <= free) else "mock"
        reason = ("weights missing" if not weights_ok else
                  f"needs ~{human_bytes(need)} but only {human_bytes(free)} free on {device}" if need > free else "")
        result["preflight"]["auto_reason"] = reason
    if mode == "real":
        if not (repo_ok and weights_ok):
            raise SystemExit(f"real mode unavailable: {weights_msg}")
        if need > free and not args.force:
            raise SystemExit(f"estimated requirement {human_bytes(need)} > free {human_bytes(free)} on {device}; "
                             f"use --force to try anyway (OOM demonstration) or --mode mock")
    result["mode"] = mode

    attn = "n/a (native SDPA)" if wrapper.name == "treemeshgpt" else flash_shim.setup_attention(args.attn, device)
    wrapper.attn_backend = attn
    result["attn_backend"] = attn
    try:
        if mode == "mock":
            run_mock(args, wrapper, spec, device, precision, result)
        else:
            run_real(args, wrapper, spec, device, precision, cloud, meta.get("reference"), result)
    except OOM_TYPES as e:
        result["status"], result["error"] = "oom", f"{type(e).__name__}: {str(e).splitlines()[0][:300]}"
    except RuntimeError as e:
        msg = str(e)
        result["status"] = "oom" if "out of memory" in msg.lower() or "not enough memory" in msg.lower() else "error"
        result["error"] = f"{type(e).__name__}: {msg.splitlines()[0][:300]}"
        result["traceback"] = traceback.format_exc()[-3000:]
    except Exception as e:
        result["status"], result["error"] = "error", f"{type(e).__name__}: {str(e)[:300]}"
        result["traceback"] = traceback.format_exc()[-3000:]
    if result["status"] == "oom" and device.startswith("cuda"):
        result.setdefault("memory", {})["vram_peak_allocated_bytes"] = torch.cuda.max_memory_allocated()

    work = result.pop("_work", None) or estimate_workload(spec, n_faces=args.assume_faces)
    glb = (result.get("mesh", {}).get("files", {}).get("glb") or {}).get("bytes")
    tm = result.get("timings", {})
    edge_s = (tm.get("extrapolated_full_inference_s") or tm.get("inference_s")) if (
        mode == "real" and result["status"] == "ok" and device.startswith("cuda")
        and torch.cuda.get_device_capability()[0] >= 8) else None  # only a modern GPU counts as "edge node"
    result["headset"] = est.headset_report(work, measured_edge_s=edge_s, upload_bytes=storage_bytes(cloud["info"]["input_points"]),
                                           download_bytes=glb, n_faces=result.get("mesh", {}).get("triangles")
                                           or args.assume_faces or spec["generation"]["default_faces"])
    result["system"] = system_info()

    if not args.quiet:
        print_table(result)
    out_json = args.json_out or (ROOT / "benchmarks" / "results" / "runs" / f"{result['timestamp'][:10]}_{wrapper.name}_{mode}_{result['run_id']}.json")
    out_json.parent.mkdir(parents=True, exist_ok=True)
    out_json.write_text(json.dumps(result, indent=2, default=json_default), encoding="utf-8")
    if args.csv_out:
        append_csv(args.csv_out, result)
    if not args.quiet:
        print(f"report JSON: {out_json}")
    return 0 if result["status"] == "ok" else (3 if result["status"] == "oom" else 1)


if __name__ == "__main__":
    sys.exit(main())
