"""Classical surface reconstruction (mesh_wrappers.classical), its edge backend and the comparison table."""
import json

import numpy as np
import pytest

from mesh_wrappers import classical
from mesh_wrappers.mesh_io import quality_metrics


def _sphere(n=1500, seed=0):
    """Points on the unit sphere and their (exact, outward) normals."""
    p = np.random.default_rng(seed).normal(size=(n, 3))
    p /= np.linalg.norm(p, axis=1, keepdims=True)
    return p.astype(np.float32), p.astype(np.float32)


def test_classical_methods_rebuild_a_sphere():
    xyz, normals = _sphere()
    reference, _ = _sphere(20000, seed=1)
    minimum_fscore = {"hull": 0.95, "ball_pivoting": 0.9, "poisson": 0.9}
    for method in classical.METHODS:
        result = classical.reconstruct(xyz, normals if method in classical.NEEDS_NORMALS else None, method)
        assert len(result.mesh.faces) > 0, method
        assert result.seconds >= 0 and result.params["spacing"] > 0
        if method in minimum_fscore:
            assert quality_metrics(result.mesh, reference)["fscore@0.01"] > minimum_fscore[method], method


def test_methods_that_need_normals_say_so():
    xyz, _ = _sphere(300)
    with pytest.raises(ValueError, match="needs normals"):
        classical.reconstruct(xyz, None, "poisson")
    with pytest.raises(ValueError, match="unknown method"):
        classical.reconstruct(xyz, None, "marching_cubes")


def test_target_faces_decimates():
    xyz, normals = _sphere()
    result = classical.reconstruct(xyz, normals, "poisson", target_faces=300)
    assert len(result.mesh.faces) <= 300 and result.params["faces_before_decimation"] > 300


def test_parameters_follow_the_cloud_not_the_reference():
    xyz, _ = _sphere(800)
    small = classical.reconstruct(xyz, None, "alpha").params
    big = classical.reconstruct(xyz * 2, None, "alpha").params
    assert big["spacing"] == pytest.approx(2 * small["spacing"], rel=1e-4)
    assert big["alpha"] == pytest.approx(classical.ALPHA_FACTOR * big["spacing"], rel=1e-4)


def test_classical_backend_through_the_edge_server(tmp_path):
    pytest.importorskip("httpx")
    from fastapi.testclient import TestClient

    from edge.reconstruction import ClassicalBackend, encode_points
    from edge.server import create_app

    xyz, _ = _sphere(2048)
    reference, _ = _sphere(20000, seed=1)
    app = create_app(ClassicalBackend("ball_pivoting"), inputs_dir=tmp_path, log_csv=None, verbose=False,
                     references={"ball_01": reference})
    with TestClient(app) as c:
        assert c.get("/health").json()["label"] == "Ball Pivoting (classical)"
        r = c.post("/reconstruct?object_id=ball_01", content=encode_points(xyz))  # points only, as the headset
        assert r.status_code == 200 and r.headers["X-Backend"] == "classical"
        assert int(r.headers["X-Triangles"]) > 1000 and float(r.headers["X-FScore"]) > 0.9
    with pytest.raises(RuntimeError, match="unknown method"):
        ClassicalBackend("marching_cubes")


def test_comparison_table_takes_complete_generative_meshes_only(tmp_path):
    from benchmarks import classical_baselines as cb

    def run(device, seconds, truncated=False):
        return {"mode": "real", "model": "meshanything", "variant": "default", "device": device, "precision": "fp16",
                "status": "ok", "input": {"shape": "box", "input_points": 2048, "normals_source": "file"},
                "timings": {"inference_s": seconds}, "mesh": {"triangles": 336, "vertices": 314},
                "generation": {"truncated": truncated},
                "quality": {} if truncated else {"fscore@0.01": 0.23, "precision": 0.17, "recall": 0.37,
                                                 "chamfer_l2": 0.0047 if device == "cpu" else 0.00468}}
    (tmp_path / "a.json").write_text(json.dumps([run("cpu", 274.3), run("cuda", 73.7)]))
    (tmp_path / "b.json").write_text(json.dumps(run("cuda", 240.0, truncated=True)))
    rows, incomplete = cb.generative_rows([str(tmp_path / "*.json")], ["box"], [2048])
    assert len(rows) == 2 and incomplete == 1
    classical_row = {"shape": "box", "points": 2048, "method": "ball_pivoting", "label": "Ball Pivoting",
                     "normals": "estimate", "target_faces": "", "triangles": 4012, "vertices": 2000, "seconds": 0.05,
                     "normals_seconds": 0.07, "fscore": 0.968, "precision": 0.98, "recall": 0.96,
                     "chamfer_l2": 0.00004, "watertight": False, "source": "classical", "params": "{}"}
    text = cb.summary([classical_row, *rows], incomplete, 3)
    assert "| scatola | – | – | 0.97 | – | – | – | 0.23 (meshanything, 73.7 s) |" in text  # tie: the fastest
    assert "| Ball Pivoting | stimate | 4012 | 50 ms | 0.968 |" in text
    assert "Non in tabella: 1 run" in text


def test_every_classical_backend_starts():
    # the start-up warm-up must never stop the server (it did with the alpha shape on an exact sphere)
    from edge.reconstruction import ClassicalBackend

    for method in classical.METHODS:
        assert ClassicalBackend(method).describe()["model"] == method
