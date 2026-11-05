"""The decoders emit vertices in [-0.5, 0.5]; their point encoders see clouds normalised differently.

A vertex at +0.5 along the longest axis (the edge of the training frame) must map back onto the
input cloud's bounding box: this is what `output_scale` + `Normalizer.invert` guarantee.
"""
import numpy as np
import pytest

from mesh_wrappers.fastmesh import FastMeshWrapper
from mesh_wrappers.meshanything_v2 import MeshAnythingV2Wrapper


@pytest.mark.parametrize("cls", [MeshAnythingV2Wrapper, FastMeshWrapper])
def test_output_frame_maps_to_input_bbox(cls):
    rng = np.random.default_rng(0)
    xyz = (rng.uniform(-1, 1, size=(4096, 3)) * np.array([3.0, 1.0, 0.5]) + np.array([2.0, -1.0, 4.0])).astype(np.float32)
    w = cls.__new__(cls)  # no model needed: only the normalisation conventions
    norm = w.normalizer(xyz)
    lo, hi = xyz.min(0), xyz.max(0)
    axis = int(np.argmax(hi - lo))
    v = np.zeros((2, 3), np.float32)
    v[0, axis], v[1, axis] = 0.5, -0.5  # extremes of the model output frame along the longest axis
    back = norm.invert(v * w.output_scale)
    np.testing.assert_allclose(back[0, axis], hi[axis], rtol=0, atol=2e-3 * (hi - lo)[axis])
    np.testing.assert_allclose(back[1, axis], lo[axis], rtol=0, atol=2e-3 * (hi - lo)[axis])
