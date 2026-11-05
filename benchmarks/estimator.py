"""Standalone-headset feasibility estimator (memory fit, roofline latency, energy) + edge offload comparison."""
from __future__ import annotations

from pathlib import Path
from typing import Optional

import numpy as np
import yaml

from .flops import BYTES, WorkloadEstimate, decoder_step_flops, human_bytes

PROFILES_PATH = Path(__file__).with_name("device_profiles.yaml")


def load_profiles(path: Optional[Path] = None) -> dict:
    return yaml.safe_load(Path(path or PROFILES_PATH).read_text(encoding="utf-8"))


def _v(entry, pick: str = "value"):
    """Read a profile entry: plain number, or {value, range}. pick = value | low | high."""
    if not isinstance(entry, dict):
        return entry
    if pick != "value" and "range" in entry:
        return entry["range"][0 if pick == "low" else 1]
    return entry["value"]


def device_param(dev: dict, defaults: dict, key: str, pick: str = "value"):
    return _v(dev[key], pick) if key in dev else defaults.get(key)


def decode_time_s(w: WorkloadEstimate, dev: dict, defaults: dict, precision: str, case: str) -> float:
    """Sum over AR steps of max(compute, memory) time + dispatch overhead."""
    best = case == "best"
    pick_perf = {"best": "high", "worst": "low"}.get(case, "value")
    flops_peak = device_param(dev, defaults, "gpu_fp16_tflops" if precision != "fp32" else "gpu_fp32_tflops", pick_perf) * 1e12
    bw = device_param(dev, defaults, "mem_bandwidth_gbs", pick_perf) * 1e9
    eta_c = dev.get("efficiency_compute", defaults["efficiency_compute"])
    eta_m = dev.get("efficiency_memory", defaults["efficiency_memory"])
    overhead = device_param(dev, defaults, "per_step_overhead_ms", {"best": "low", "worst": "high"}.get(case, "value")) or 0.0
    dec = dict(w.decoder)
    C, T = w.cond_tokens, w.decode_steps
    ctx = C + np.arange(T, dtype=np.float64)
    f0 = decoder_step_flops(dec, 0)
    flops = f0 + 4 * dec["n_layers"] * dec["d_model"] * ctx
    weight_bytes = dec["params"] * BYTES[precision]
    kv_bytes = w.kv_bytes_per_token[precision if precision in w.kv_bytes_per_token else "fp16"] * (ctx + 1)
    t = np.maximum(flops / (flops_peak * eta_c), (weight_bytes + kv_bytes) / (bw * eta_m)) + overhead / 1e3
    # prefill of the condition tokens is compute bound
    prefill = 2 * dec["params"] * C / (flops_peak * eta_c)
    return float(t.sum() + prefill)


def one_shot_time_s(w: WorkloadEstimate, dev: dict, defaults: dict, precision: str, case: str) -> float:
    pick = {"best": "high", "worst": "low"}.get(case, "value")
    flops_peak = device_param(dev, defaults, "gpu_fp16_tflops" if precision != "fp32" else "gpu_fp32_tflops", pick) * 1e12
    eta_c = dev.get("efficiency_compute", defaults["efficiency_compute"])
    return w.one_shot_flops / (flops_peak * eta_c)


def memory_required(w: WorkloadEstimate, precision: str, defaults: dict, kv_precision: str = "fp16") -> dict:
    weights = w.weights_bytes[precision]
    kv = w.peak_kv_bytes[kv_precision if precision != "fp32" else "fp32"]
    act = w.activation_bytes_fp16 * (2 if precision == "fp32" else 1)
    overhead = defaults["runtime_overhead_bytes"]
    return {"weights": weights, "kv_cache": kv, "activations": act, "runtime": overhead,
            "total": weights + kv + act + overhead}


def estimate_device(w: WorkloadEstimate, dev_key: str, profiles: dict, precision: str = "fp16",
                    case: str = "nominal") -> dict:
    defaults = profiles["defaults"]
    dev = profiles["devices"][dev_key]
    mem = memory_required(w, precision, defaults)
    budget = _v(dev["app_memory_budget_bytes"])
    t_dec = decode_time_s(w, dev, defaults, precision, case)
    t_one = one_shot_time_s(w, dev, defaults, precision, case)
    thermal = device_param(dev, defaults, "thermal_sustained_factor", {"best": "high", "worst": "low"}.get(case, "value")) or 1.0
    t_total = (t_dec + t_one) / thermal
    out = {"device": dev_key, "label": dev["label"], "precision": precision, "case": case,
           "memory": mem, "memory_budget_bytes": budget, "fits_memory": mem["total"] <= budget,
           "latency_s": t_total, "decode_s": t_dec / thermal, "one_shot_s": t_one / thermal}
    if "soc_power_w" in dev:
        p = device_param(dev, defaults, "soc_power_w", {"best": "low", "worst": "high"}.get(case, "value"))
        e = p * t_total
        battery_j = _v(dev["battery_wh"]) * 3600
        frame_ms = 1000.0 / dev.get("display_hz", 90)
        out.update(energy_j=e, battery_pct_per_mesh=100 * e / battery_j, meshes_per_charge=battery_j / e,
                   frames_stalled=t_total * 1000 / frame_ms)
    return out


def estimate_offload(w: WorkloadEstimate, profiles: dict, edge_key: str = "edge_rtx4090",
                     link_key: str = "wifi6_local_edge", upload_bytes: float = 2048 * 12,
                     download_bytes: Optional[float] = None, edge_compute_s: Optional[float] = None,
                     n_faces: int = 800) -> dict:
    link = profiles["network"][link_key]
    if download_bytes is None:
        download_bytes = n_faces * 0.5 * 12 + n_faces * 12 + 1500  # GLB: positions + indices + header
    if edge_compute_s is None:
        edge_compute_s = estimate_device(w, edge_key, profiles, "fp16")["latency_s"]
    up = upload_bytes * 8 / (_v(link["uplink_mbps"]) * 1e6)
    down = download_bytes * 8 / (_v(link["downlink_mbps"]) * 1e6)
    net = up + down + _v(link["rtt_ms"]) / 1e3
    return {"edge": edge_key, "link": link_key, "edge_compute_s": edge_compute_s, "network_s": net,
            "total_s": edge_compute_s + net, "headset_radio_energy_j": _v(link["radio_power_w"]) * net,
            "upload_bytes": upload_bytes, "download_bytes": download_bytes}


def headset_report(w: WorkloadEstimate, profiles: Optional[dict] = None, measured_edge_s: Optional[float] = None,
                   upload_bytes: float = 2048 * 12, download_bytes: Optional[float] = None, n_faces: int = 800) -> dict:
    profiles = profiles or load_profiles()
    out = {"headsets": {}, "offload": None}
    for key, dev in profiles["devices"].items():
        if dev.get("kind") != "headset":
            continue
        out["headsets"][key] = {
            "nominal_fp16": estimate_device(w, key, profiles, "fp16", "nominal"),
            "best_int4": estimate_device(w, key, profiles, "int4", "best"),
            "worst_fp16": estimate_device(w, key, profiles, "fp16", "worst"),
        }
    out["offload"] = estimate_offload(w, profiles, upload_bytes=upload_bytes, download_bytes=download_bytes,
                                      edge_compute_s=measured_edge_s, n_faces=n_faces)
    return out


def verdict(est: dict) -> str:
    """One-line Italian summary for the console table."""
    m = est["memory"]["total"]
    b = est["memory_budget_bytes"]
    mem = f"mem {human_bytes(m)}/{human_bytes(b)}"
    if not est["fits_memory"]:
        return f"NON FATTIBILE: OOM ({mem}); calcolo ≈{est['latency_s']:.0f} s/mesh"
    s = f"{mem} OK; ≈{est['latency_s']:.0f} s/mesh"
    if "battery_pct_per_mesh" in est:
        s += f", {est['battery_pct_per_mesh']:.2f}% batteria/mesh"
    return s + (" → non interattivo" if est["latency_s"] > 1.0 else "")


def match_local_profile(device_name: str, profiles: Optional[dict] = None) -> Optional[str]:
    profiles = profiles or load_profiles()
    for key, dev in profiles["devices"].items():
        if dev.get("match") and dev["match"].lower() in (device_name or "").lower():
            return key
    return None


def predict_step_ms(w: WorkloadEstimate, dev_key: str, weight_bytes_per_param: float, kv_bytes_per_elem: float,
                    context: float, compute_precision: str = "fp16", profiles: Optional[dict] = None) -> dict:
    """Roofline prediction of one decode step at `context` on `dev_key` (nominal values, no thermal factor).

    Used to validate the estimator against the ms/token measured on the local machine."""
    profiles = profiles or load_profiles()
    defaults, dev = profiles["defaults"], profiles["devices"][dev_key]
    dec = w.decoder
    flops = decoder_step_flops(dec, int(context))
    peak = device_param(dev, defaults, "gpu_fp32_tflops" if compute_precision == "fp32" else "gpu_fp16_tflops") * 1e12
    bw = device_param(dev, defaults, "mem_bandwidth_gbs") * 1e9
    eta_c = dev.get("efficiency_compute", defaults["efficiency_compute"])
    eta_m = dev.get("efficiency_memory", defaults["efficiency_memory"])
    bytes_ = dec["params"] * weight_bytes_per_param + 2 * dec["n_layers"] * dec["d_model"] * kv_bytes_per_elem * (context + 1)
    t_c, t_m = flops / (peak * eta_c), bytes_ / (bw * eta_m)
    ovh = device_param(dev, defaults, "per_step_overhead_ms") or 0.0
    return {"device_profile": dev_key, "compute_ms": 1e3 * t_c, "memory_ms": 1e3 * t_m, "overhead_ms": ovh,
            "predicted_ms": 1e3 * max(t_c, t_m) + ovh, "bound": "memory" if t_m >= t_c else "compute"}
