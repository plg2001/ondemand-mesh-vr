"""MeshPad (DerKleineLi/meshpad-release, ICCV 2025) wrapper: sketch image -> artist-like mesh (<= 1600 faces).

MeshPad's addition network: NVIDIA RADIO v2.5-H (a ViT-H, frozen) turns a 224x224 sketch (black strokes on white) into
199 condition tokens; an OPT-350M decoder (MeshAnything V2's, same tokenizer) generates the mesh; a vertex-aligned
speculator predicts the y and z tokens of each vertex together with its x (2.16x fewer decoder passes). Generating
from scratch is an addition to an empty mesh, as in the authors' demo (demo/gradio_demo.py), with its sampling
settings. The mesh comes out in the sketch's +Y-up frame, centred, inside [-0.5, 0.5] (see sketch.py).

Runtime adaptations (upstream code untouched):
* fms/__init__.py reads the version of an installed "ibm-fms" package; only fms.modules.layernorm is needed (by the
  speculator), so ``fms`` is registered as a bare package over the clone in third_party/.
* torch.hub.load("NVlabs/RADIO", ...) would download RADIO's latest main from GitHub at every new PC: it is redirected
  to the clone pinned in upstream.lock.json (source="local"), and its checkpoint is read memory-mapped from
  checkpoints/torch/hub/checkpoints (setup_models.py), never downloaded at run time.
* The whole network is built on the meta device and filled straight from addition.pth (which holds RADIO's weights
  too), part by part: no random initialisation, no second copy in RAM.
* --encoder-device: with little VRAM RADIO (1.3 GB in fp16) stays on the CPU, stored in fp16 and computed in fp32 one
  transformer block at a time; its tokens are moved to the decoder's device. The decoder keeps layer norms and biases
  in fp32 like the demo.
* flash-attn: FA2 code path kept, kernel replaced by our SDPA shim (flash_shim), as for MeshAnything V2.
* args.llm ("facebook/opt-350m") -> the config.json snapshot pinned in upstream.lock.json (no network).
"""
from __future__ import annotations

import contextlib
import os
import sys
import types
import warnings
from argparse import Namespace
from pathlib import Path
from typing import Optional
from urllib.parse import urlparse

import numpy as np
import torch
import yaml

from .base import GenerationResult, MeshModelWrapper
from .loading import CHECKPOINTS, THIRD_PARTY, count_parameters, hf_file, put_upstream_on_path
from .sketch import sketch_tensor

WEIGHTS = CHECKPOINTS / "meshpad" / "addition"
RADIO_CHECKPOINT = CHECKPOINTS / "torch" / "hub" / "checkpoints" / "radio_v2.5-h.pth.tar"
# demo/gradio_demo.py: generation_args, used for every addition in the authors' interface
SAMPLING = {"num_beams": 1, "do_sample": True, "top_k": 10, "top_p": 0.8, "temperature": 0.5}
GREEDY = {"num_beams": 1, "do_sample": False}


class _Encoder(torch.nn.Module):
    """RADIO on its own device and precision; the condition tokens go to the decoder's device."""

    def __init__(self, radio: torch.nn.Module, device: str, out_device: str):
        super().__init__()
        self.radio, self.device, self.out_device = radio, device, out_device

    def forward(self, image: torch.Tensor):
        x = image.to(self.device, torch.float32)
        if self.device.startswith("cuda"):
            with torch.autocast("cuda", dtype=torch.float16):
                out = self.radio(x)
        else:
            out = self.radio(x)
        return out[0].to(self.out_device), out[1].to(self.out_device)


def _upcast_while_running(module: torch.nn.Module) -> None:
    """fp16 storage, fp32 compute: the module is converted for its own forward and back afterwards (the hooks return
    None: a pre-hook's return value would replace the module's inputs)."""

    def to_fp32(m, args):
        m.float()

    def to_fp16(m, args, out):
        m.half()

    module.register_forward_pre_hook(to_fp32)
    module.register_forward_hook(to_fp16)


@contextlib.contextmanager
def _quiet_build():
    """Hides the notices the upstream code prints while the empty model is built; none of them is a fault:
    - RADIO's hub code copies its checkpoint into the meta-device model, a no-op PyTorch warns about once per tensor
      (~390 lines): every weight, RADIO's included, is then set from addition.pth, and load() fails if one is missing;
    - transformers: "model of type opt to instantiate a model of type shape_opt" (MeshAnything V2's OPT subclass) and
      "use_flash_attention_2 is deprecated" (the upstream flag; flash_shim replaces that attention when it is unusable)."""
    from transformers.utils import logging as hf_logging

    level = hf_logging.get_verbosity()
    hf_logging.set_verbosity_error()
    try:
        with warnings.catch_warnings():
            warnings.filterwarnings("ignore", message=r".*copying from a non-meta parameter in the checkpoint to a meta "
                                                      r"parameter.*")
            yield
    finally:
        hf_logging.set_verbosity(level)


class MeshPadWrapper(MeshModelWrapper):
    name = "meshpad"
    display_name = "MeshPad"
    repo_dirname = "MeshPad"
    spec_file = ""
    variants = ("default",)
    input_kind = "sketch"   # the other wrappers take point clouds
    native_points = 0
    needs_normals = False

    def __init__(self, variant: Optional[str] = None, device: str = "cpu", precision: str = "fp32",
                 attn_backend: str = "sdpa-shim", max_seq_len: Optional[int] = None, encoder_device: str = "auto"):
        super().__init__(variant=variant, device=device, precision=precision, attn_backend=attn_backend,
                         max_seq_len=max_seq_len)
        self.encoder_device = encoder_device
        self.speculator: Optional[torch.nn.Module] = None

    # ------------------------------------------------------------------ metadata
    def weights_available(self):
        files = [WEIGHTS / "addition.pth", WEIGHTS / "args.yaml", WEIGHTS / "speculator.pth", RADIO_CHECKPOINT]
        missing = [str(p) for p in files if not p.exists()]
        if hf_file("facebook/opt-350m", "config.json", local_only=True) is None:
            missing.append("facebook/opt-350m config.json")
        if missing:
            return False, "missing " + ", ".join(missing) + " (run models/setup_models.py --models meshpad)"
        return True, str(WEIGHTS)

    def repo_available(self) -> bool:
        return all((THIRD_PARTY / d).is_dir() for d in ("MeshPad", "MeshAnythingV2", "foundation-model-stack",
                                                        "fms-extras", "RADIO"))

    def normalizer(self, xyz):
        raise NotImplementedError("MeshPad is conditioned on a sketch image, not on a point cloud")

    def generate(self, xyz, normals, max_new_tokens, seed):
        raise NotImplementedError("MeshPad is conditioned on a sketch image: use generate_sketch()")

    def resolved_encoder_device(self) -> str:
        if self.encoder_device != "auto":
            return self.encoder_device
        if self.device.startswith("cuda"):  # RADIO + decoder need ~2.1 GB in fp16 plus the activations
            total = torch.cuda.get_device_properties(torch.device(self.device)).total_memory
            return self.device if total >= 6 * 2**30 else "cpu"
        return self.device

    # ------------------------------------------------------------------ upstream code
    def _import_upstream(self):
        if "fms" not in sys.modules:  # skip fms/__init__.py (it needs an installed ibm-fms package)
            pkg = types.ModuleType("fms")
            pkg.__path__ = [str(THIRD_PARTY / "foundation-model-stack" / "fms")]
            sys.modules["fms"] = pkg
        put_upstream_on_path(THIRD_PARTY / "fms-extras")
        put_upstream_on_path(THIRD_PARTY / "MeshAnythingV2")  # meshanything_train (coordinate discretisation)
        put_upstream_on_path(self.repo_dir, ("meshpad",))
        from fms_extras.models.speculator import MLPSpeculator
        from meshpad.models.meshpad_addition import MeshPadAdd

        return MeshPadAdd, MLPSpeculator

    @staticmethod
    @contextlib.contextmanager
    def _radio_from_pinned_clone():
        real_load, real_url = torch.hub.load, torch.hub.load_state_dict_from_url

        def load_state_dict_from_url(url, model_dir=None, map_location=None, progress=True, check_hash=False,
                                     file_name=None, weights_only=False):
            folder = Path(model_dir) if model_dir else Path(torch.hub.get_dir()) / "checkpoints"
            path = folder / (file_name or os.path.basename(urlparse(url).path))
            if not path.exists():
                raise FileNotFoundError(f"{path} not found: run python models/setup_models.py --models meshpad")
            return torch.load(path, map_location="cpu", weights_only=False, mmap=True)  # official NVIDIA file

        def hub_load(repo_or_dir, model, *args, **kwargs):
            if repo_or_dir == "NVlabs/RADIO":
                for k in ("skip_validation", "source", "trust_repo", "force_reload"):
                    kwargs.pop(k, None)
                return real_load(str(THIRD_PARTY / "RADIO"), model, *args, source="local", **kwargs)
            return real_load(repo_or_dir, model, *args, **kwargs)

        torch.hub.load, torch.hub.load_state_dict_from_url = hub_load, load_state_dict_from_url
        try:
            yield
        finally:
            torch.hub.load, torch.hub.load_state_dict_from_url = real_load, real_url

    def _args(self) -> Namespace:
        args = Namespace(**yaml.safe_load((WEIGHTS / "args.yaml").read_text(encoding="utf-8")))
        args.llm = str(Path(hf_file("facebook/opt-350m", "config.json")).parent)  # pinned snapshot folder
        return args

    def instantiate_meta(self) -> torch.nn.Module:
        from accelerate import init_empty_weights

        from . import flash_shim

        if not flash_shim.is_installed() and not flash_shim.real_flash_usable(self.device):
            flash_shim.install()  # MeshPad hard-wires flash_attention_2: without flash-attn it would refuse to build
        MeshPadAdd, _ = self._import_upstream()
        with self._radio_from_pinned_clone(), init_empty_weights(include_buffers=False), _quiet_build():
            return MeshPadAdd(self._args())

    # ------------------------------------------------------------------ loading
    def load(self) -> dict:
        from accelerate.utils import set_module_tensor_to_device

        MeshPadAdd, MLPSpeculator = self._import_upstream()
        model = self.instantiate_meta()
        args = model.args
        enc_dev = self.resolved_encoder_device()
        half = self.device.startswith("cuda") and self.precision != "fp32"
        enc_half = enc_dev.startswith("cuda") and self.precision != "fp32"

        def target(key: str, t: torch.Tensor) -> tuple[str, torch.dtype]:
            if key.startswith("vision_tower."):
                if not t.is_floating_point():
                    return enc_dev, t.dtype
                if enc_half or (enc_dev == "cpu" and ".model.blocks." in key):
                    return enc_dev, torch.float16  # on the CPU: fp16 storage, fp32 compute (hooks below)
                return enc_dev, torch.float32
            if not t.is_floating_point():
                return self.device, t.dtype
            keep_fp32 = "layer_norm" in key or key.endswith(".bias")  # as the demo does
            return self.device, torch.float16 if half and not keep_fp32 else torch.float32

        expected = set(model.state_dict().keys())
        state = torch.load(WEIGHTS / "addition.pth", map_location="cpu", mmap=True, weights_only=True)
        loaded, unexpected = set(), []
        for key, t in state.items():
            if key not in expected:
                unexpected.append(key)
                continue
            dev, dtype = target(key, t)
            # copy=True on the CPU: a parameter must not keep the memory-mapped file alive (its pages count as RAM)
            value = t.to(dtype=dtype, copy=True) if dev == "cpu" else t.to(dtype)
            set_module_tensor_to_device(model, key, dev, value=value, dtype=dtype)
            loaded.add(key)
        del state
        missing = sorted(expected - loaded)
        for name, module in model.named_modules():  # buffers not in the checkpoint (created for real, on the CPU)
            dev = enc_dev if name.startswith("vision_tower") else self.device
            for bname, buf in list(module._buffers.items()):
                if buf is not None and buf.device.type != "meta":
                    module._buffers[bname] = buf.to(dev)
        leftovers = [n for n, p in model.named_parameters() if p.device.type == "meta"]
        if leftovers:
            raise RuntimeError(f"MeshPad: parameters not in addition.pth: {leftovers[:10]}")

        radio = model.vision_tower
        if enc_dev == "cpu":  # the checkpoint is fp16 anyway: nothing is lost by storing it so
            for block in radio.model.blocks:
                _upcast_while_running(block)
        model.vision_tower = _Encoder(radio, enc_dev, self.device)
        model.eval()

        speculator = MLPSpeculator(emb_dim=1024 * 4, inner_dim=0, vocab_size=args.n_discrete_size + 4, n_predict=2,
                                   tie_weights=False, scale_input=False)
        speculator.load_state_dict(torch.load(WEIGHTS / "speculator.pth", map_location="cpu", weights_only=True))
        self.speculator = speculator.to(self.device, dtype=torch.float16 if half else torch.float32).eval()
        self.model = model
        return {"loaded": len(loaded), "missing": missing, "unexpected": unexpected, "encoder_device": enc_dev,
                "params_encoder": count_parameters(radio), "params_decoder": count_parameters(model.transformer),
                "params_speculator": count_parameters(self.speculator)}

    def stage_targets(self):
        m = self.model
        return [("encoder", m.vision_tower, "forward"),
                ("decoder_step", m.transformer.model.decoder, "forward"),
                ("detokenize", m, "loop_detokenize")]

    # ------------------------------------------------------------------ generation
    def max_new_tokens_full(self) -> int:
        m = self.model
        return int(m.args.gen_n_max_triangles * m.face_per_token * m.max_seq_ratio + 3)

    def generate_sketch(self, strokes: np.ndarray, max_new_tokens: Optional[int] = None, seed: int = 0,
                        sampling: bool = True) -> GenerationResult:
        """strokes: (224, 224) bool, True = black stroke (sketch.load_sketch / sketch.render_sketch)."""
        m = self.model
        full = self.max_new_tokens_full()
        limit = full if not max_new_tokens else min(full, int(max_new_tokens))
        m.max_generation_length = m.cond_length + limit
        torch.manual_seed(seed)
        cond = torch.tensor([[-1]], device=self.device)  # empty mesh: generation from scratch
        data = {"image": sketch_tensor(strokes).to(self.device), "conditional_sequence": cond,
                "conditional_id_sequence": cond.clone()}
        with self.autocast():
            out = m.generate(data, speculator=self.speculator, **(SAMPLING if sampling else GREEDY))
        tri = out[0].float().cpu().numpy()  # (F, 3, 3), NaN rows = padding
        n_tokens = int((data["gen_sequence"][0] != m.pad_id).sum())
        failed = bool(np.isfinite(tri[-1, -1, -1]) and tri[-1, -1, -1] == 0)  # loop_detokenize's error mark
        valid = tri[np.all(np.isfinite(tri.reshape(-1, 9)), axis=1)]
        truncated = n_tokens >= limit - 3
        return GenerationResult(triangles=valid if len(valid) >= 2 else None, n_tokens=n_tokens, truncated=truncated,
                                extra={"max_new_tokens": limit, "sampling": sampling, "detokenize_error": failed,
                                       "faces_raw": int(len(valid))})
