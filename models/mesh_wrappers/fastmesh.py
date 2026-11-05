"""FastMesh (jhkim0759) wrapper — AR vertex generation + one-shot adjacency (faces) prediction."""
from __future__ import annotations

import json
from typing import Optional

import numpy as np
import torch

from . import flash_shim
from .base import GenerationResult, MeshModelWrapper
from .loading import hf_file, pushd, put_upstream_on_path, stream_load_safetensors
from .pointcloud import normalizer_fastmesh

HF_REPOS = {"V1K": "WopperSet/FastMesh-V1K", "V4K": "WopperSet/FastMesh-V4K"}


class FastMeshWrapper(MeshModelWrapper):
    name = "fastmesh"
    display_name = "FastMesh"
    repo_dirname = "FastMesh"
    spec_file = "fastmesh.yaml"
    variants = ("V1K", "V4K")
    native_points = 8192
    needs_normals = True
    downsample = "random"
    output_scale = 2 * 0.95  # pc longest side -> 1.9 (apply_normalize) while vertices live in [-0.5, 0.5]

    @property
    def hf_repo(self) -> str:
        return HF_REPOS[self.variant]

    def weights_available(self):
        p = hf_file(self.hf_repo, "model.safetensors", local_only=True)
        ok = p is not None
        return ok, (p if ok else f"{self.hf_repo} not in HF cache (run models/setup_models.py --fastmesh-variant {self.variant})")

    def normalizer(self, xyz):
        return normalizer_fastmesh(xyz)

    def _config(self) -> dict:

        return json.loads(open(hf_file(self.hf_repo, "config.json"), encoding="utf-8").read())

    def _cls(self):
        if self.attn_backend == "sdpa-shim":
            flash_shim.install()  # before import: models/modules/transformers.py binds the symbol at import
        put_upstream_on_path(self.repo_dir, ("models", "utils"))
        import models.mesh_gen  # noqa: F401,E402  (FastMesh's package, not ours)
        from models.models import MODELS  # noqa: E402

        if flash_shim.is_installed():
            flash_shim.patch_loaded_modules(("models.",))
        return MODELS["MeshGen"]

    def instantiate_meta(self):
        from accelerate import init_empty_weights

        cls, cfg = self._cls(), self._config()
        with pushd(self.repo_dir), init_empty_weights(include_buffers=False):
            return cls(**cfg)

    def load(self) -> dict:
        from accelerate import init_empty_weights

        cls, cfg = self._cls(), self._config()
        with pushd(self.repo_dir), init_empty_weights(include_buffers=False):
            model = cls(**cfg)
        path = hf_file(self.hf_repo, "model.safetensors")
        info = stream_load_safetensors(model, path, self.device, self.weight_dtype())
        model.eval()
        self.model = model
        info["config"] = cfg
        return info

    def stage_targets(self):
        vg, fg = self.model.vert_gen, self.model.face_gen
        targets = [("encoder", vg.point_encoder, "encode_latents"),
                   ("decoder_step", vg.transformer.model.decoder, "forward")]
        if getattr(vg, "use_refine", False) and hasattr(vg, "refine_net"):
            targets.append(("vertex_refine", vg.refine_net, "refine_vertices"))
        targets.append(("face_gen", fg, "forward"))
        return targets

    def generate(self, xyz, normals, max_new_tokens: Optional[int], seed: int) -> GenerationResult:
        vg = self.model.vert_gen
        torch.manual_seed(seed)
        np.random.seed(seed)
        if not hasattr(self, "_full_len"):
            self._full_len = vg.gen_max_length
        vg.gen_max_length = self._full_len if not max_new_tokens else min(self._full_len, vg.cond_length + int(max_new_tokens))
        pc = np.concatenate([xyz, normals], axis=1).astype(np.float32)
        dtype = self.weight_dtype() or (torch.float16 if self.device.startswith("cuda") and self.precision != "fp32" else torch.float32)
        t = torch.from_numpy(pc).unsqueeze(0).to(self.device, dtype=dtype)
        with self.autocast(), torch.no_grad():
            meshes = self.model({"pc_normal": t}, is_eval=True)
        mesh = meshes[0] if isinstance(meshes, (list, tuple)) else meshes
        return GenerationResult(triangles=None, n_tokens=-1, mesh=mesh,
                                extra={"max_new_tokens": vg.gen_max_length - vg.cond_length})
