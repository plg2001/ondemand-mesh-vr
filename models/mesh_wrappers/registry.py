"""Model name -> wrapper class (imports are lazy: only the selected model's upstream code is loaded)."""
from __future__ import annotations

import importlib

_REGISTRY = {
    "meshanything": ("meshanything_v2", "MeshAnythingV2Wrapper"),
    "fastmesh": ("fastmesh", "FastMeshWrapper"),
    "treemeshgpt": ("treemeshgpt", "TreeMeshGPTWrapper"),
}
ALIASES = {"meshanythingv2": "meshanything", "mav2": "meshanything", "tmg": "treemeshgpt"}
MODEL_NAMES = tuple(_REGISTRY)


def get_wrapper_class(name: str):
    key = ALIASES.get(name.lower(), name.lower())
    if key not in _REGISTRY:
        raise KeyError(f"unknown model {name!r}; available: {', '.join(MODEL_NAMES)}")
    module, cls = _REGISTRY[key]
    return getattr(importlib.import_module(f"mesh_wrappers.{module}"), cls)
