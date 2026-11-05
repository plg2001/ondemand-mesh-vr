"""TreeMeshGPT (sail-sg, CVPR 2025) wrapper — feeds point clouds directly to ``TreeMeshGPT.generate``."""
from __future__ import annotations

import contextlib
import io
from typing import Optional

import numpy as np
import torch

from .base import GenerationResult, MeshModelWrapper
from .loading import CHECKPOINTS, load_state_dict_lowmem, put_upstream_on_path, read_lock
from .pointcloud import normalizer_treemeshgpt


class TreeMeshGPTWrapper(MeshModelWrapper):
    name = "treemeshgpt"
    display_name = "TreeMeshGPT"
    repo_dirname = "TreeMeshGPT"
    spec_file = "treemeshgpt.yaml"
    variants = ("7bit", "9bit")
    needs_normals = False
    output_scale = 1.0  # cloud and mesh share the same bbox-diagonal normalisation

    @property
    def native_points(self) -> int:  # inference.py: 8192 uniform (7bit) / FPS to 4096 (9bit)
        return 8192 if self.variant == "7bit" else 4096

    @property
    def downsample(self) -> str:
        return "random" if self.variant == "7bit" else "fps"

    @property
    def quant_bit(self) -> int:
        return 7 if self.variant == "7bit" else 9

    def ckpt_path(self):
        return CHECKPOINTS / "treemeshgpt" / f"treemeshgpt_{self.variant}.pt"

    def weights_available(self):
        p = self.ckpt_path()
        return (p.is_file(), str(p) if p.is_file() else f"missing {p} (run models/setup_models.py)")

    def normalizer(self, xyz):
        return normalizer_treemeshgpt(xyz)

    def _cls(self):
        put_upstream_on_path(self.repo_dir, ("model", "fns"))
        from model.treemeshgpt_inference import TreeMeshGPT  # noqa: E402

        return TreeMeshGPT

    def _kwargs(self):
        return dict(quant_bit=self.quant_bit, max_seq_len=self.max_seq_len or 30000)

    def instantiate_meta(self):
        cls = self._cls()
        with torch.device("meta"):
            return cls(**self._kwargs())

    def load(self) -> dict:
        from accelerate import init_empty_weights

        cls = self._cls()
        with init_empty_weights(include_buffers=False):
            model = cls(**self._kwargs())
        path = self.ckpt_path()
        try:
            pkg = torch.load(path, map_location="cpu", weights_only=True, mmap=True)  # 5 GB file: keep it on disk
        except Exception:
            # the official pickle stores the init args with pickle; allow it only for the hash we pinned
            from .loading import sha256_file

            expected = read_lock().get("weights", {}).get(f"treemeshgpt_{self.variant}", {}).get("sha256")
            if expected and sha256_file(path) != expected:
                raise RuntimeError(f"{path}: sha256 mismatch, refusing unsafe unpickling")
            pkg = torch.load(path, map_location="cpu", weights_only=False, mmap=True)
        state = pkg["model"] if isinstance(pkg, dict) and "model" in pkg else pkg
        info = load_state_dict_lowmem(model, state, self.device, self.weight_dtype())
        model.eval()
        self.model = model
        return info

    def stage_targets(self):
        m = self.model
        return [("encoder", m.pc_encoder, "forward"), ("decoder_step", m.decoder, "forward")]

    def generate(self, xyz, normals, max_new_tokens: Optional[int], seed: int) -> GenerationResult:
        m = self.model
        torch.manual_seed(seed)
        np.random.seed(seed)
        xyz = np.clip(xyz, -0.5, 0.5)
        limit = max_new_tokens or self.max_seq_len or 30000
        m.max_seq_len = limit
        pc = torch.from_numpy(np.ascontiguousarray(xyz)).unsqueeze(0).float().to(self.device)
        sink = io.StringIO()  # upstream prints a progress line per step
        with contextlib.redirect_stdout(sink), self.autocast():
            tri = m.generate(pc, n=0.25)
        progress = sink.getvalue().rsplit("\r", 1)[-1].strip()
        n_seq = None
        if progress.startswith("Sequence length:"):
            try:
                n_seq = int(progress.split(":")[1].split("/")[0])
            except ValueError:
                pass
        tri = tri.float().cpu().numpy()
        return GenerationResult(triangles=tri, n_tokens=-1, truncated=bool(n_seq is not None and n_seq + 1 >= limit),
                                extra={"last_progress": progress, "sequence_length": n_seq})
