"""Paths, upstream import isolation and memory-aware weight loading."""
from __future__ import annotations

import contextlib
import hashlib
import json
import os
import sys
from pathlib import Path
from typing import Iterator, Optional

ROOT = Path(__file__).resolve().parents[2]
THIRD_PARTY = ROOT / "third_party"
CHECKPOINTS = ROOT / "checkpoints"

# Keep every downloaded weight under checkpoints/ (Hugging Face included) instead of ~/.cache/huggingface.
# Must run before huggingface_hub / transformers are imported, since they read these variables at import time.
# An explicit HF_HOME / HF_HUB_CACHE set by the user still wins.
if "HF_HOME" not in os.environ and "HF_HUB_CACHE" not in os.environ:
    os.environ["HF_HUB_CACHE"] = str(CHECKPOINTS / "huggingface")

import torch  # noqa: E402
LOCK_FILE = ROOT / "models" / "upstream.lock.json"
SPECS_DIR = ROOT / "models" / "specs"
DATA_INPUTS = ROOT / "data" / "inputs"
DATA_OUTPUTS = ROOT / "data" / "outputs"


def read_lock() -> dict:
    return json.loads(LOCK_FILE.read_text(encoding="utf-8")) if LOCK_FILE.exists() else {}


@contextlib.contextmanager
def pushd(path: Path) -> Iterator[None]:
    prev = os.getcwd()
    os.chdir(path)
    try:
        yield
    finally:
        os.chdir(prev)


def put_upstream_on_path(repo_dir: Path, top_level_packages: tuple[str, ...] = ()) -> None:
    """Make ``repo_dir`` the first import location.

    Our ``models/`` folder is deliberately *not* a package, but if a namespace module called
    ``models`` was imported by accident it would shadow FastMesh's regular ``models`` package:
    drop such stale entries before importing upstream code.
    """
    repo = str(repo_dir)
    if repo not in sys.path:
        sys.path.insert(0, repo)
    for pkg in top_level_packages:
        mod = sys.modules.get(pkg)
        if mod is not None:
            origin = getattr(mod, "__file__", None) or ""
            if not origin.startswith(repo):
                for name in [n for n in sys.modules if n == pkg or n.startswith(pkg + ".")]:
                    del sys.modules[name]


def sha256_file(path: Path, chunk: int = 1 << 22) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while block := f.read(chunk):
            h.update(block)
    return h.hexdigest()


def resolve_dtype(precision: str) -> torch.dtype:
    return {"fp32": torch.float32, "fp16": torch.float16, "bf16": torch.bfloat16}[precision]


def stream_load_safetensors(model: torch.nn.Module, path: Path, device: str, dtype: Optional[torch.dtype],
                            strict: bool = True, aliases: Optional[dict] = None) -> dict:
    """Load tensors one at a time into a model built under ``accelerate.init_empty_weights``.

    Peak host memory ~ final model size (instead of 2x with load_state_dict(load_file(...))).
    """
    from accelerate.utils import set_module_tensor_to_device
    from safetensors import safe_open

    expected = set(model.state_dict().keys())
    tied = _tied_groups(model)
    seen, unexpected = set(), []
    with safe_open(str(path), framework="pt", device="cpu") as f:
        for key in f.keys():
            if key not in expected:
                unexpected.append(key)
                continue
            t = f.get_tensor(key)
            if dtype is not None and t.is_floating_point():
                t = t.to(dtype)
            set_module_tensor_to_device(model, key, device, value=t, dtype=t.dtype)
            seen.add(key)
            del t
    seen |= _retie(model, tied, seen)
    aliases = dict(aliases or {})
    for k in expected - seen:  # HF tied OPT embeddings: only lm_head.weight is serialised
        if k.endswith("model.decoder.embed_tokens.weight"):
            aliases.setdefault(k, k[: -len("model.decoder.embed_tokens.weight")] + "lm_head.weight")
    for dst, src in aliases.items():  # tensors shared at save time and stored once
        if dst in expected and dst not in seen and src in seen:
            mod_name, _, attr = dst.rpartition(".")
            src_param = model.get_parameter(src)
            if tuple(model.get_submodule(mod_name)._parameters[attr].shape) == tuple(src_param.shape):
                setattr(model.get_submodule(mod_name), attr, src_param)
                seen.add(dst)
    missing = sorted(expected - seen)
    _materialise_leftovers(model, device, dtype)
    if strict and missing:
        raise RuntimeError(f"missing keys in checkpoint: {missing[:10]} (+{max(0, len(missing) - 10)})")
    return {"loaded": len(seen), "missing": missing, "unexpected": unexpected, "tied_groups": len(tied)}


def _tied_groups(model: torch.nn.Module) -> list[list[str]]:
    """Names of parameters that are the same object (e.g. embed_tokens <-> lm_head)."""
    groups: dict[int, list[str]] = {}
    for name, p in model.named_parameters(remove_duplicate=False):
        groups.setdefault(id(p), []).append(name)
    return [g for g in groups.values() if len(g) > 1]


def _retie(model: torch.nn.Module, groups: list[list[str]], loaded: set) -> set:
    """safetensors stores shared tensors once: point the other names at the loaded parameter."""
    fixed = set()
    for names in groups:
        src = next((n for n in names if n in loaded), None)
        if src is None:
            continue
        param = model.get_parameter(src)
        for n in names:
            if n != src:
                mod_name, _, attr = n.rpartition(".")
                setattr(model.get_submodule(mod_name) if mod_name else model, attr, param)
                fixed.add(n)
    return fixed


def load_state_dict_lowmem(model: torch.nn.Module, state_dict: dict, device: str,
                           dtype: Optional[torch.dtype], strict: bool = True) -> dict:
    from accelerate.utils import set_module_tensor_to_device

    expected = set(model.state_dict().keys())
    tied = _tied_groups(model)
    keys = list(state_dict.keys())
    for key in keys:
        t = state_dict.pop(key)
        if key not in expected:
            continue
        if dtype is not None and t.is_floating_point():
            t = t.to(dtype)
        set_module_tensor_to_device(model, key, device, value=t, dtype=t.dtype)
    loaded = (set(keys) & expected)
    loaded |= _retie(model, tied, loaded)
    missing = sorted(expected - loaded)
    _materialise_leftovers(model, device, dtype)
    if strict and missing:
        raise RuntimeError(f"missing keys: {missing[:10]}")
    return {"loaded": len(expected) - len(missing), "missing": missing}


def _materialise_leftovers(model: torch.nn.Module, device: str, dtype: Optional[torch.dtype]) -> None:
    """Move buffers kept on CPU by init_empty_weights to the target device/dtype."""
    for module in model.modules():
        for name, buf in list(module._buffers.items()):
            if buf is None or buf.device.type == "meta":
                continue
            new = buf.to(device)
            if dtype is not None and new.is_floating_point():
                new = new.to(dtype)
            module._buffers[name] = new
    leftovers = [n for n, p in model.named_parameters() if p.device.type == "meta"]
    if leftovers:
        raise RuntimeError(f"parameters not initialised from checkpoint: {leftovers[:10]}")


def count_parameters(module: torch.nn.Module) -> int:
    return int(sum(p.numel() for p in module.parameters()))


def free_memory_bytes(device: str) -> int:
    if device.startswith("cuda"):
        free, _ = torch.cuda.mem_get_info(torch.device(device))
        return int(free)
    import psutil

    return int(psutil.virtual_memory().available)


def hf_file(repo: str, filename: str, local_only: bool = False) -> Optional[str]:
    """Path of an HF Hub file at the revision pinned in upstream.lock.json (None if not cached)."""
    from huggingface_hub import hf_hub_download

    rev = read_lock().get("weights", {}).get(f"{repo}/{filename}", {}).get("revision")
    try:
        return hf_hub_download(repo, filename, revision=rev, local_files_only=local_only)
    except Exception:
        if local_only:
            return None
        raise
