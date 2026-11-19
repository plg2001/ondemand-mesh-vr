"""MeshPad integration: pinned repositories and the architecture on the meta device (no weights loaded, no GPU)."""
import json

import pytest

import setup_models
from mesh_wrappers.loading import LOCK_FILE, count_parameters


def test_meshpad_repositories_are_known_and_pinned():
    needed = setup_models.MODEL_REPOS["meshpad"]
    assert set(needed) == {"meshpad", "meshanything", "fms", "fms_extras", "radio"}
    assert all(k in setup_models.REPOS for keys in setup_models.MODEL_REPOS.values() for k in keys)
    lock = json.loads(LOCK_FILE.read_text(encoding="utf-8"))
    for key in needed:  # a new PC must get the same code, not the repositories' latest commits
        assert len(lock["repos"][key]["commit"]) == 40
    assert set(setup_models.MESHPAD_GDRIVE) == {"addition.pth", "args.yaml", "speculator.pth"}


class _FakeSketchWrapper:
    """Stands in for MeshPadWrapper: the first sampled sequence fails to decode, the second gives a box."""
    name, display_name, input_kind = "meshpad", "MeshPad", "sketch"

    def __init__(self):
        self.seeds = []

    def generate_sketch(self, strokes, max_new_tokens, seed):
        import numpy as np
        import trimesh

        from mesh_wrappers.base import GenerationResult

        self.seeds.append(seed)
        box = trimesh.creation.box(extents=(0.5, 0.3, 0.2))  # MeshPad's [-0.5, 0.5] range, not canonical
        tri = np.asarray(box.vertices)[box.faces]
        return GenerationResult(triangles=tri, n_tokens=120, truncated=False,
                                extra={"detokenize_error": len(self.seeds) == 1})


def _sketch_backend(wrapper):
    import torch

    from benchmarks.profiler import StageTimer
    from edge.reconstruction import ModelBackend

    backend = ModelBackend.__new__(ModelBackend)  # no model loaded: only the sketch path is exercised
    backend.wrapper, backend.input_kind, backend._torch = wrapper, "sketch", torch
    backend.device, backend.time_budget_s, backend.max_new_tokens = "cpu", None, None
    backend.timer = StageTimer("cpu")
    return backend


def test_sketch_backend_retries_and_returns_the_object_frame():
    import numpy as np

    wrapper = _FakeSketchWrapper()
    rec = _sketch_backend(wrapper).reconstruct_sketch(np.ones((224, 224), bool), seed=7)
    assert wrapper.seeds == [7, 8] and rec.info["attempts"] == 2  # "try again with different sampling"
    np.testing.assert_allclose(rec.mesh.extents, [1.0, 0.6, 0.4], atol=1e-6)  # longest side 1
    np.testing.assert_allclose(rec.mesh.bounds.mean(0), 0, atol=1e-6)  # centred


def test_sketch_backend_refuses_points():
    import numpy as np

    from edge.reconstruction import ReconstructionError

    with pytest.raises(ReconstructionError) as e:
        _sketch_backend(_FakeSketchWrapper()).reconstruct(np.zeros((100, 3), np.float32))
    assert e.value.status_code == 400 and "format=sketch" in str(e.value)


def test_meshpad_architecture_on_meta_device():
    from mesh_wrappers.meshpad import RADIO_CHECKPOINT, WEIGHTS, MeshPadWrapper
    from mesh_wrappers.loading import hf_file

    w = MeshPadWrapper()
    if not (w.repo_available() and RADIO_CHECKPOINT.exists() and (WEIGHTS / "args.yaml").exists()
            and hf_file("facebook/opt-350m", "config.json", local_only=True)):
        pytest.skip("MeshPad not installed (python models/setup_models.py --models meshpad)")
    model = w.instantiate_meta()
    assert all(p.device.type == "meta" for p in model.parameters())  # nothing allocated
    assert 640e6 < count_parameters(model.vision_tower) < 660e6  # RADIO v2.5-H (ViT-H/16)
    assert 310e6 < count_parameters(model.transformer) < 325e6  # MeshAnything V2's OPT-350M (vocabulary of 132)
    assert model.cond_length == 199
