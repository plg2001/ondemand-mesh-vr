"""FlashAttention-2 -> PyTorch SDPA compatibility shim (transformers 4.43.x).

MeshAnythingV2 and FastMesh hard-wire ``attn_implementation="flash_attention_2"``:
their custom OPT decoder raises ``ValueError`` for any other backend and FastMesh's
FaceGen calls ``transformers.modeling_flash_attention_utils._flash_attention_forward``
directly. The ``flash_attn`` package only supports NVIDIA Ampere+ GPUs on Linux, so on
CPU, Pascal/Turing GPUs or Windows these models cannot run unmodified.

Instead of editing upstream sources we keep their control flow intact (2-D padding masks,
FA2 code paths) and replace only the kernel:

* ``PreTrainedModel._check_and_enable_flash_attn_2`` no longer raises when flash_attn is missing;
* ``_flash_attention_forward`` (in transformers and in any upstream module that imported it)
  is replaced by :func:`sdpa_flash_attention_forward`, a numerically equivalent implementation
  based on ``torch.nn.functional.scaled_dot_product_attention``;
* ``PreTrainedModel.to_bettertransformer`` becomes a no-op when optimum is not installed
  (BetterTransformer matches modules by class name and never replaces ``OptFlashAttention2``,
  so for FA2 models it is already a no-op).

Semantics reproduced (flash_attn >= 2.1 as used by transformers 4.43):
  inputs/outputs in (batch, seq, heads, head_dim) layout; ``attention_mask`` is a 2-D
  (batch, kv_len) padding mask (1 = keep) or None; causal masking is *bottom-right* aligned;
  with ``use_top_left_mask`` causal is disabled for ``query_length == 1`` (transformers rule);
  padded query rows return zeros (as ``pad_input`` does).
"""
from __future__ import annotations

import importlib.util
import logging
import sys
from typing import Optional

import torch
import torch.nn.functional as F

log = logging.getLogger(__name__)

_INSTALLED = False
_ORIGINALS: dict = {}


def flash_attn_available() -> bool:
    return importlib.util.find_spec("flash_attn") is not None


def real_flash_usable(device: str | torch.device | None = None) -> bool:
    """True when the genuine flash_attn kernels can be used (package + CUDA sm80+)."""
    if not flash_attn_available() or not torch.cuda.is_available():
        return False
    major, _ = torch.cuda.get_device_capability(device)
    return major >= 8


def sdpa_flash_attention_forward(
    query_states: torch.Tensor,
    key_states: torch.Tensor,
    value_states: torch.Tensor,
    attention_mask: Optional[torch.Tensor],
    query_length: int,
    is_causal: bool,
    dropout: float = 0.0,
    position_ids: Optional[torch.Tensor] = None,
    softmax_scale: Optional[float] = None,
    sliding_window: Optional[int] = None,
    use_top_left_mask: bool = False,
    softcap: Optional[float] = None,
    deterministic: Optional[bool] = None,
    **_: object,
) -> torch.Tensor:
    if sliding_window is not None or softcap is not None:
        raise NotImplementedError("SDPA shim: sliding_window/softcap are not supported")

    q = query_states.transpose(1, 2)  # (B, H, Lq, D)
    k = key_states.transpose(1, 2)
    v = value_states.transpose(1, 2)
    if k.dtype != q.dtype:
        k, v = k.to(q.dtype), v.to(q.dtype)
    bsz, _, lq, _ = q.shape
    lk = k.shape[2]

    causal = is_causal and not (use_top_left_mask and query_length == 1)

    mask = None  # boolean, True = attend
    if attention_mask is not None:
        key_keep = attention_mask[:, -lk:].to(torch.bool)  # (B, Lk)
        mask = key_keep[:, None, None, :].expand(bsz, 1, lq, lk)
    if causal and lq > 1:
        # bottom-right aligned causal mask: query i may see keys j <= i + (lk - lq)
        offset = lk - lq
        idx_q = torch.arange(lq, device=q.device)[:, None]
        idx_k = torch.arange(lk, device=q.device)[None, :]
        cmask = (idx_k <= idx_q + offset)[None, None]
        mask = cmask if mask is None else (mask & cmask)

    if mask is None:
        out = F.scaled_dot_product_attention(q, k, v, dropout_p=dropout, scale=softmax_scale)
    else:
        # rows with no visible key would produce NaN in SDPA: let them see everything, zero them later
        empty_rows = ~mask.any(dim=-1, keepdim=True)
        safe_mask = mask | empty_rows
        out = F.scaled_dot_product_attention(q, k, v, attn_mask=safe_mask, dropout_p=dropout, scale=softmax_scale)
        out = out.masked_fill(empty_rows, 0.0)

    out = out.transpose(1, 2)  # (B, Lq, H, D)
    if attention_mask is not None:
        q_keep = attention_mask[:, -lq:].to(torch.bool)
        if lq == query_length and not bool(q_keep.all()):
            out = out * q_keep[:, :, None, None].to(out.dtype)
    return out.contiguous()


def install() -> None:
    """Patch transformers so FA2 models run without flash_attn. Idempotent."""
    global _INSTALLED
    if _INSTALLED:
        return
    import transformers.modeling_flash_attention_utils as fau
    from transformers import modeling_utils
    from transformers.models.opt import modeling_opt

    _ORIGINALS["fau"] = getattr(fau, "_flash_attention_forward", None)
    fau._flash_attention_forward = sdpa_flash_attention_forward
    # modeling_opt imports the symbol only `if is_flash_attn_2_available()`: define it anyway
    modeling_opt._flash_attention_forward = sdpa_flash_attention_forward

    pm = modeling_utils.PreTrainedModel
    _ORIGINALS["check"] = pm.__dict__["_check_and_enable_flash_attn_2"]

    def _check_and_enable_flash_attn_2(cls, config, torch_dtype=None, device_map=None,
                                       check_device_map=True, hard_check_only=False):
        if not hard_check_only:
            config._attn_implementation = "flash_attention_2"
        return config

    pm._check_and_enable_flash_attn_2 = classmethod(_check_and_enable_flash_attn_2)

    neutralize_bettertransformer()
    _INSTALLED = True
    patch_loaded_modules()
    log.info("FA2->SDPA shim installed")


def neutralize_bettertransformer() -> None:
    """MeshAnythingV2 calls ``to_bettertransformer()`` on an FA2 OPT model: optimum matches modules by
    class name ("OPTAttention") and never replaces ``OptFlashAttention2``, so the call is a no-op.
    Without optimum installed transformers would raise ImportError instead: make it a real no-op."""
    if importlib.util.find_spec("optimum") is not None:
        return
    from transformers import modeling_utils

    pm = modeling_utils.PreTrainedModel
    if "bt" not in _ORIGINALS:
        _ORIGINALS["bt"] = pm.to_bettertransformer
        pm.to_bettertransformer = lambda self: self


def patch_loaded_modules(prefixes: tuple[str, ...] = ("models.", "MeshAnything.")) -> list[str]:
    """Rebind `_flash_attention_forward` in upstream modules that did `from ... import` it."""
    patched = []
    for name, mod in list(sys.modules.items()):
        if mod is None or not name.startswith(prefixes):
            continue
        if getattr(mod, "_flash_attention_forward", None) not in (None, sdpa_flash_attention_forward):
            mod._flash_attention_forward = sdpa_flash_attention_forward
            patched.append(name)
    return patched


def is_installed() -> bool:
    return _INSTALLED


def setup_attention(mode: str, device: str) -> str:
    """Resolve --attn {auto,flash,sdpa}; returns the backend actually used."""
    if mode == "flash":
        if not real_flash_usable(device):
            raise RuntimeError("--attn flash requires the flash_attn package and an NVIDIA sm80+ GPU")
        return "flash_attention_2"
    if mode == "auto" and device.startswith("cuda") and real_flash_usable(device):
        return "flash_attention_2"
    install()
    return "sdpa-shim"
