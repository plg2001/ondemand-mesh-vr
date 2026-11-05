"""MeshAnything V2 (buaacyw) wrapper — point cloud with normals -> artist-like mesh (<=1600 faces)."""
from __future__ import annotations

from typing import Optional

import numpy as np
import torch

from . import flash_shim
from .base import GenerationResult, MeshModelWrapper
from .loading import hf_file, pushd, put_upstream_on_path, stream_load_safetensors
from .pointcloud import normalizer_meshanything

HF_REPO = "Yiwen-ntu/meshanythingv2"


class MeshAnythingV2Wrapper(MeshModelWrapper):
    name = "meshanything"
    display_name = "MeshAnything V2"
    repo_dirname = "MeshAnythingV2"
    spec_file = "meshanything_v2.yaml"
    variants = ("default",)
    native_points = 8192
    needs_normals = True
    downsample = "random"
    output_scale = 0.9995 / 0.5  # pc max-abs -> 0.9995 (main.py) while the mesh lives in [-0.5, 0.5]

    def weights_available(self):
        p = hf_file(HF_REPO, "model.safetensors", local_only=True)
        ok = p is not None
        return ok, (p if ok else f"{HF_REPO} not in HF cache (run models/setup_models.py)")

    def normalizer(self, xyz):
        return normalizer_meshanything(xyz)

    def _cls(self):
        flash_shim.neutralize_bettertransformer()
        put_upstream_on_path(self.repo_dir, ("MeshAnything",))
        from MeshAnything.models.meshanything_v2 import MeshAnythingV2  # noqa: E402

        return MeshAnythingV2

    @staticmethod
    def _no_device_move():
        """Upstream ``miche.encode.load_model`` does ``model.to("cuda" if available)``: that breaks
        meta-device construction and would silently put the encoder on the GPU in CPU runs.
        Rebind it (in the module that imported it) to a version that builds the encoder in place."""
        import contextlib
        import sys

        @contextlib.contextmanager
        def ctx():
            enc = sys.modules["MeshAnything.miche.encode"]
            host = sys.modules["MeshAnything.models.meshanything_v2"]

            def load_model(ckpt_path=None):
                cfg = enc.OmegaConf.load("MeshAnything/miche/shapevae-256.yaml")
                cfg = cfg.model if hasattr(cfg, "model") else cfg
                return enc.instantiate_from_config(cfg, ckpt_path=ckpt_path).eval()

            original = host.load_model
            host.load_model = load_model
            try:
                yield
            finally:
                host.load_model = original

        return ctx()

    def instantiate_meta(self):
        from accelerate import init_empty_weights

        cls = self._cls()
        with pushd(self.repo_dir), self._no_device_move(), init_empty_weights(include_buffers=False):
            return cls()

    def load(self) -> dict:
        from accelerate import init_empty_weights

        cls = self._cls()
        with pushd(self.repo_dir), self._no_device_move(), init_empty_weights(include_buffers=False):
            model = cls()  # reads MeshAnything/miche/shapevae-256.yaml (relative path)
        path = hf_file(HF_REPO, "model.safetensors")
        # tied embeddings: the HF checkpoint stores only lm_head.weight
        info = stream_load_safetensors(model, path, self.device, self.weight_dtype(), aliases={
            "transformer.model.decoder.embed_tokens.weight": "transformer.lm_head.weight"})
        model.eval()
        self.model = model
        return info

    def stage_targets(self):
        m = self.model
        return [("encoder", m.point_encoder, "encode_latents"),
                ("decoder_step", m.transformer.model.decoder, "forward"),
                ("detokenize", m, "adjacent_detokenize")]

    def generate(self, xyz, normals, max_new_tokens: Optional[int], seed: int) -> GenerationResult:
        m = self.model
        torch.manual_seed(seed)
        full = int(m.n_max_triangles * m.face_per_token * m.max_seq_ratio + 3 + m.cond_length)
        m.max_length = full if not max_new_tokens else min(full, m.cond_length + int(max_new_tokens))
        pc = np.concatenate([xyz, normals], axis=1).astype(np.float32)
        dtype = self.weight_dtype() or (torch.float16 if self.device.startswith("cuda") and self.precision != "fp32" else torch.float32)
        t = torch.from_numpy(pc).unsqueeze(0).to(self.device, dtype=dtype)
        with self.autocast():
            out = m(t, sampling=False)  # greedy, as main.py default
        tri = out[0].float().cpu().numpy()  # (nf, 3, 3) with NaN padding
        return GenerationResult(triangles=tri, n_tokens=-1, truncated=False,
                                extra={"max_new_tokens": m.max_length - m.cond_length})
