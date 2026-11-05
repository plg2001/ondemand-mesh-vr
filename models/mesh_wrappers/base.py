"""Common interface of the three model wrappers."""
from __future__ import annotations

import abc
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

import numpy as np
import torch
import yaml

from .loading import SPECS_DIR, THIRD_PARTY
from .pointcloud import Normalizer


@dataclass
class GenerationResult:
    triangles: Optional[np.ndarray]  # (F,3,3) in *model* space, None if not produced
    n_tokens: int                    # autoregressive steps actually executed
    truncated: bool = False          # stopped by --max-new-tokens before EOS
    extra: dict = field(default_factory=dict)
    mesh: object = None              # optional trimesh.Trimesh already built by upstream code (model space)


def deep_merge(base: dict, override: dict) -> dict:
    out = dict(base)
    for k, v in override.items():
        out[k] = deep_merge(out[k], v) if isinstance(v, dict) and isinstance(out.get(k), dict) else v
    return out


class MeshModelWrapper(abc.ABC):
    name: str = ""            # CLI key
    display_name: str = ""
    repo_dirname: str = ""    # folder under third_party/
    spec_file: str = ""       # models/specs/<file>.yaml
    variants: tuple[str, ...] = ("default",)
    native_points: int = 8192
    needs_normals: bool = True
    # The decoders emit vertices in [-0.5, 0.5]; the point encoders were trained on clouds normalised to a
    # different range. output_scale maps model-output coordinates into the *normalised input* frame.
    output_scale: float = 1.0

    def __init__(self, variant: Optional[str] = None, device: str = "cpu", precision: str = "fp32",
                 attn_backend: str = "sdpa-shim", max_seq_len: Optional[int] = None):
        self.variant = variant or self.variants[0]
        if self.variant not in self.variants:
            raise ValueError(f"{self.name}: unknown variant {self.variant!r}, choose from {self.variants}")
        self.device = device
        self.precision = precision
        self.attn_backend = attn_backend
        self.max_seq_len = max_seq_len
        self.model: Optional[torch.nn.Module] = None

    # ------------------------------------------------------------------ metadata
    @property
    def repo_dir(self) -> Path:
        return THIRD_PARTY / self.repo_dirname

    def spec(self) -> dict:
        data = yaml.safe_load((SPECS_DIR / self.spec_file).read_text(encoding="utf-8"))
        var = (data.get("variants") or {}).get(self.variant, {})
        return deep_merge({k: v for k, v in data.items() if k != "variants"}, var)

    def repo_available(self) -> bool:
        return self.repo_dir.is_dir()

    @abc.abstractmethod
    def weights_available(self) -> tuple[bool, str]:
        """(ok, human readable path or reason)."""

    # ------------------------------------------------------------------ pipeline
    @abc.abstractmethod
    def normalizer(self, xyz: np.ndarray) -> Normalizer: ...

    @abc.abstractmethod
    def load(self) -> dict:
        """Build the official network on self.device with real weights. Returns load info."""

    @abc.abstractmethod
    def stage_targets(self) -> list[tuple[str, object, str]]:
        """(stage, object, attribute) callables to time. Stage 'decoder_step' = one AR step per call
        (its first call is the prefill of the condition tokens)."""

    @abc.abstractmethod
    def generate(self, xyz: np.ndarray, normals: Optional[np.ndarray], max_new_tokens: Optional[int],
                 seed: int) -> GenerationResult:
        """xyz/normals already resampled to native_points and normalised to model space."""

    def instantiate_meta(self) -> torch.nn.Module:
        """Official architecture with parameters on the meta device (no memory, no weights)."""
        raise NotImplementedError

    def autocast(self):
        if self.device.startswith("cuda") and self.precision != "fp32":
            dt = torch.bfloat16 if self.precision.startswith("bf16") else torch.float16
            return torch.autocast("cuda", dtype=dt)
        import contextlib

        return contextlib.nullcontext()

    def weight_dtype(self) -> Optional[torch.dtype]:
        """fp16/bf16 = upstream behaviour (fp32 weights + autocast); *-full also casts the weights."""
        return {"fp16-full": torch.float16, "bf16-full": torch.bfloat16}.get(self.precision)
