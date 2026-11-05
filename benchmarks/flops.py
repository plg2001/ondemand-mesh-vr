"""Analytic FLOP / memory model of the three architectures + a shape-faithful surrogate transformer.

The surrogate is used (a) on the ``meta`` device with ``FlopCounterMode`` to *measure* FLOPs of a
prefill / decode step without allocating memory, validating the analytic formulas, and (b) with
random weights and only ``k`` layers on a real device to time decode steps ("partial loading").
Convention: 1 multiply-accumulate = 2 FLOPs.
"""
from __future__ import annotations

import math
import time
from dataclasses import dataclass
from typing import Optional

import torch
import torch.nn as nn
import torch.nn.functional as F

BYTES = {"fp32": 4.0, "fp16": 2.0, "bf16": 2.0, "int8": 1.0, "int4": 0.5}


# ============================================================================ analytic model
def layer_params(d: int, ffn_dim: int, ffn: str) -> int:
    attn = 4 * d * d + 4 * d
    mlp = (3 if ffn == "glu" else 2) * d * ffn_dim + ffn_dim + d
    return attn + mlp + 4 * d  # + 2 layernorms


def component_params(c: dict) -> int:
    k = c["kind"]
    if k in ("decoder_ar", "encoder_bidir"):
        p = c["n_layers"] * layer_params(c["d_model"], c["ffn_dim"], c.get("ffn", "gelu"))
        if k == "decoder_ar":
            p += 2 * c["vocab"] * c["d_model"] + c.get("head_mlp_params", 0)  # embeddings + lm head
        return p
    if k == "perceiver":
        w, f = c["width"], c.get("ffn_mult", 4)
        cross = c["point_dim"] * w + 4 * w * w + 2 * w * f * w + c["n_latents"] * w
        return cross + c["n_layers"] * layer_params(w, f * w, "gelu")
    raise ValueError(k)


def decoder_step_flops(c: dict, context: int) -> float:
    """FLOPs of ONE autoregressive step attending to `context` previous tokens (KV cache)."""
    d, L = c["d_model"], c["n_layers"]
    dense = 2 * L * (layer_params(d, c["ffn_dim"], c.get("ffn", "relu")) - 4 * d)
    attn = 4 * L * d * (context + 1)  # QK^T + AV
    head = 2 * d * c["vocab"] + 2 * c.get("head_mlp_params", 0)
    return float(dense + attn + head)


def sequence_flops(c: dict, n: int, causal: bool) -> float:
    """FLOPs of one full pass over n tokens (prefill or bidirectional encoder)."""
    d, L = c["d_model"], c["n_layers"]
    dense = 2 * n * L * (layer_params(d, c["ffn_dim"], c.get("ffn", "gelu")) - 4 * d)
    attn = 4 * L * d * n * n * (0.5 if causal else 1.0)
    extra = 2 * n * n * d if c.get("pairwise") else 0  # V x V adjacency logits
    return float(dense + attn + extra)


def perceiver_flops(c: dict) -> float:
    N, w, M, f = c["n_points"], c["width"], c["n_latents"], c.get("ffn_mult", 4)
    embed = 2 * N * c["point_dim"] * w
    kv = 2 * 2 * N * w * w
    qo = 2 * 2 * M * w * w
    scores = 4 * M * N * w
    ffn = 2 * 2 * M * w * f * w
    self_layers = c["n_layers"] * (2 * M * (4 * w * w + 2 * w * f * w) + 4 * M * M * w)
    return float(embed + kv + qo + scores + ffn + self_layers)


@dataclass
class WorkloadEstimate:
    params_total: int
    flops_total: float
    flops_by_component: dict
    decode_steps: int
    cond_tokens: int
    kv_bytes_per_token: dict           # per precision
    weights_bytes: dict                # per precision
    peak_kv_bytes: dict                # at the end of generation
    decoder: dict                      # dims of the AR decoder (for the roofline)
    one_shot_flops: float              # encoder + one-shot components
    activation_bytes_fp16: float       # largest transient tensor estimate

    def to_dict(self) -> dict:
        return self.__dict__.copy()


def estimate_workload(spec: dict, n_faces: Optional[int] = None, n_steps: Optional[int] = None) -> WorkloadEstimate:
    """Total cost of generating one mesh with `n_faces` faces (or exactly `n_steps` AR steps)."""
    gen = spec["generation"]
    n_faces = n_faces or gen.get("default_faces", 800)
    T = int(n_steps if n_steps is not None else round(n_faces * gen["tokens_per_face"]))
    T = min(T, gen["max_new_tokens"])
    C = gen["cond_tokens"]
    n_vert = max(3, int(round(n_faces * gen.get("vertices_per_face", 0.5))))
    comps = spec["components"]
    measured = spec.get("params_measured") or {}

    def comp_params(c: dict) -> int:
        keys = c.get("measured_keys") or []
        if keys and all(k in measured for k in keys):  # exact count from the official architecture
            return int(sum(measured[k] for k in keys))
        return component_params(c)

    params = {c["name"]: comp_params(c) for c in comps}
    by_comp, one_shot, act = {}, 0.0, 0.0
    dec = None
    for c in comps:
        if c["kind"] == "perceiver":
            fl = perceiver_flops(c)
            one_shot += fl
            act = max(act, 2.0 * c["n_latents"] * c["n_points"] * 2)  # cross-attn scores fp16
        elif c["kind"] == "decoder_ar":
            dec = c
            prefill = sequence_flops(c, C, causal=True)
            # sum_{t=0}^{T-1} step(C + t): affine in context -> closed form
            steps = T * decoder_step_flops(c, C) + 4 * c["n_layers"] * c["d_model"] * T * (T - 1) / 2
            fl = prefill + steps
        else:  # one-shot bidirectional encoder over generated vertices
            fl = sequence_flops(c, n_vert, causal=False)
            one_shot += fl
            act = max(act, 2.0 * c["n_heads"] * n_vert * n_vert * 2)
        by_comp[c["name"]] = fl
    assert dec is not None, "spec without an autoregressive decoder"
    total_params = int(measured.get("total") or sum(params.values()))
    kv_tok = {p: 2 * dec["n_layers"] * dec["d_model"] * b for p, b in BYTES.items()}
    return WorkloadEstimate(
        params_total=total_params,
        flops_total=float(sum(by_comp.values())),
        flops_by_component=by_comp,
        decode_steps=T,
        cond_tokens=C,
        kv_bytes_per_token=kv_tok,
        weights_bytes={p: total_params * b for p, b in BYTES.items()},
        peak_kv_bytes={p: kv_tok[p] * (C + T) for p in BYTES},
        decoder={k: dec[k] for k in ("d_model", "n_layers", "n_heads", "ffn_dim", "vocab")}
        | {"params": int(params[dec["name"]]), "ffn": dec.get("ffn", "relu"),
           "head_mlp_params": dec.get("head_mlp_params", 0)},
        one_shot_flops=one_shot,
        activation_bytes_fp16=act,
    )


# ============================================================================ surrogate network
class _Block(nn.Module):
    def __init__(self, d: int, h: int, ffn_dim: int, glu: bool):
        super().__init__()
        self.h = h
        self.ln1, self.ln2 = nn.LayerNorm(d), nn.LayerNorm(d)
        self.qkv, self.o = nn.Linear(d, 3 * d), nn.Linear(d, d)
        self.glu = glu
        self.fc1 = nn.Linear(d, ffn_dim * (2 if glu else 1))
        self.fc2 = nn.Linear(ffn_dim, d)

    def forward(self, x, cache: Optional[tuple] = None):
        B, n, d = x.shape
        q, k, v = self.qkv(self.ln1(x)).view(B, n, 3, self.h, d // self.h).permute(2, 0, 3, 1, 4)
        if cache is not None:
            k, v = torch.cat([cache[0], k], 2), torch.cat([cache[1], v], 2)
        a = F.scaled_dot_product_attention(q, k, v, is_causal=cache is None and n > 1)
        x = x + self.o(a.transpose(1, 2).reshape(B, n, d))
        hdn = self.fc1(self.ln2(x))
        if self.glu:
            a1, a2 = hdn.chunk(2, -1)
            hdn = a1 * F.gelu(a2)
        else:
            hdn = F.relu(hdn)
        return x + self.fc2(hdn), (k, v)


class SurrogateDecoder(nn.Module):
    """Decoder-only transformer with the dimensions of the real AR decoder (embeddings omitted)."""

    def __init__(self, d_model: int, n_layers: int, n_heads: int, ffn_dim: int, vocab: int,
                 ffn: str = "relu", **_):
        super().__init__()
        self.blocks = nn.ModuleList(_Block(d_model, n_heads, ffn_dim, ffn == "glu") for _ in range(n_layers))
        self.head = nn.Linear(d_model, vocab, bias=False)
        self.d, self.h = d_model, n_heads

    def forward(self, x, caches: Optional[list] = None):
        new = []
        for i, b in enumerate(self.blocks):
            x, kv = b(x, None if caches is None else caches[i])
            new.append(kv)
        return self.head(x[:, -1:]), new


def meta_flops_step(dec: dict, context: int) -> float:
    """FLOPs of one decode step at `context`, counted by FlopCounterMode on the meta device."""
    from torch.utils.flop_counter import FlopCounterMode

    with torch.device("meta"):
        net = SurrogateDecoder(**dec)
        d, h = dec["d_model"], dec["n_heads"]
        x = torch.empty(1, 1, d)
        caches = [(torch.empty(1, h, context, d // h), torch.empty(1, h, context, d // h)) for _ in net.blocks]
        counter = FlopCounterMode(display=False)
        with counter:
            net(x, caches)
    return float(counter.get_total_flops())


def _time_steps(dec: dict, n_layers: int, device: str, dtype: torch.dtype, cond_tokens: int,
                steps: int, warmup: int) -> float:
    """Median wall time of one decode step of an n_layers random-weight surrogate."""
    net = SurrogateDecoder(**dict(dec, n_layers=n_layers)).to(device=device, dtype=dtype).eval()

    def sync():
        if device.startswith("cuda"):
            torch.cuda.synchronize()

    with torch.no_grad():
        x = torch.randn(1, cond_tokens, dec["d_model"], device=device, dtype=dtype)
        _, caches = net(x)
        tok = torch.randn(1, 1, dec["d_model"], device=device, dtype=dtype)
        times = []
        for i in range(warmup + steps):
            sync()
            t0 = time.perf_counter()
            _, caches = net(tok, caches)
            sync()
            if i >= warmup:
                times.append(time.perf_counter() - t0)
    del net, caches
    times.sort()
    return times[len(times) // 2]


def time_surrogate(dec: dict, device: str, dtype: torch.dtype, layers: int, cond_tokens: int,
                   steps: int = 32, warmup: int = 4) -> dict:
    """Time decode steps of a k-layer random-weight surrogate on a real device and scale to full depth.

    Linear model t(L) = t_fixed + L * t_layer: with k >= 2 a 1-layer run separates the fixed part
    (LM head, dispatch) from the per-layer cost, so only the latter is multiplied by the full depth.
    """
    layers = max(1, min(layers, dec["n_layers"]))
    if device.startswith("cuda"):
        torch.cuda.reset_peak_memory_stats()
    t_k = _time_steps(dec, layers, device, dtype, cond_tokens, steps, warmup)
    if layers >= 2:
        t_1 = _time_steps(dec, 1, device, dtype, cond_tokens, steps, warmup)
        per_layer = max((t_k - t_1) / (layers - 1), 1e-9)
        fixed = max(t_1 - per_layer, 0.0)
    else:
        per_layer, fixed = t_k, 0.0
    full = fixed + per_layer * dec["n_layers"]
    out = {"layers_run": layers, "ms_per_step_measured": t_k * 1e3, "ms_per_layer": per_layer * 1e3,
           "ms_fixed_per_step": fixed * 1e3, "ms_per_step_full_depth": full * 1e3,
           "context_at_measure": cond_tokens + warmup + steps, "device": device, "dtype": str(dtype)}
    if device.startswith("cuda"):
        out["peak_vram_mb"] = torch.cuda.max_memory_allocated() / 2**20
    return out


def human_flops(x: float) -> str:
    for unit, s in (("PFLOP", 1e15), ("TFLOP", 1e12), ("GFLOP", 1e9), ("MFLOP", 1e6)):
        if x >= s:
            return f"{x / s:.2f} {unit}"
    return f"{x:.0f} FLOP"


def human_bytes(x: float) -> str:
    if x is None or (isinstance(x, float) and math.isnan(x)):
        return "n/d"
    for unit, s in (("GB", 2**30), ("MB", 2**20), ("KB", 2**10)):
        if abs(x) >= s:
            return f"{x / s:.2f} {unit}"
    return f"{x:.0f} B"
