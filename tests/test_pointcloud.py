import numpy as np
import pytest

from mesh_wrappers import pointcloud as pc


def sphere(n, seed=0):
    rng = np.random.default_rng(seed)
    x = rng.normal(size=(n, 3)).astype(np.float32)
    x /= np.linalg.norm(x, axis=1, keepdims=True)
    return x * 2.0 + np.array([1.0, -3.0, 0.5], np.float32), x


@pytest.mark.parametrize("n,target,method", [(100, 8192, "repeat"), (500, 8192, "jitter"), (9000, 8192, "repeat")])
def test_resample_counts(n, target, method):
    xyz, nrm = sphere(n)
    out, onrm, info = pc.resample(xyz, nrm, target, method)
    assert out.shape == (target, 3) and onrm.shape == (target, 3)
    assert info["input_points"] == n


def test_repeat_does_not_invent_points():
    xyz, nrm = sphere(100)
    out, _, _ = pc.resample(xyz, nrm, 4096, "repeat")
    assert len(np.unique(out, axis=0)) == 100


@pytest.mark.parametrize("factory,bound", [(pc.normalizer_meshanything, 0.9995), (pc.normalizer_fastmesh, 0.95),
                                           (pc.normalizer_treemeshgpt, 0.5)])
def test_normalizers_roundtrip(factory, bound):
    xyz, _ = sphere(2048)
    n = factory(xyz)
    y = n.apply(xyz)
    assert np.abs(y).max() <= bound + 1e-5
    np.testing.assert_allclose(n.invert(y), xyz, atol=1e-5)


def test_io_roundtrip(tmp_path):
    xyz, nrm = sphere(300)
    for ext in (".npy", ".ply", ".xyzn"):
        p = pc.save_pointcloud(tmp_path / f"c{ext}", xyz, nrm)
        a, b = pc.load_pointcloud(p)
        np.testing.assert_allclose(a, xyz, atol=1e-5)
        np.testing.assert_allclose(b, nrm, atol=1e-5)
    a, b = pc.load_pointcloud(pc.save_pointcloud(tmp_path / "c.xyz", xyz))
    assert b is None and a.shape == (300, 3)


def test_estimated_normals_are_unit_and_outward():
    xyz, true_n = sphere(2000)
    n = pc.estimate_normals(xyz)
    np.testing.assert_allclose(np.linalg.norm(n, axis=1), 1.0, atol=1e-5)
    assert np.mean(np.sum(n * true_n, 1) > 0.9) > 0.95
