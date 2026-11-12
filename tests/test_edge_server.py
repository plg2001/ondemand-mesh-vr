"""Edge server: wire format, mock backend and HTTP endpoints (no weights, no GPU needed)."""
import io
import json

import numpy as np
import pytest
import trimesh

from edge.reconstruction import MockBackend, decode_points, encode_points, export_glb, hull_mesh


def sample_box(n=512, seed=0):
    mesh = trimesh.creation.box(extents=(1.0, 0.6, 0.4))
    pts, face_idx = trimesh.sample.sample_surface(mesh, n, seed=seed)
    return np.asarray(pts, dtype=np.float32), np.asarray(mesh.face_normals[face_idx], dtype=np.float32)


def load_glb(data: bytes) -> trimesh.Trimesh:
    return trimesh.load(io.BytesIO(data), file_type="glb", force="mesh")


# ----------------------------------------------------------------------------- wire format
def test_points_roundtrip_xyz_and_xyzn():
    xyz, nrm = sample_box(100)
    got_xyz, got_nrm = decode_points(encode_points(xyz), "xyz")
    assert got_nrm is None and np.array_equal(got_xyz, xyz)
    got_xyz, got_nrm = decode_points(encode_points(xyz, nrm), "xyzn")
    assert np.array_equal(got_xyz, xyz) and np.array_equal(got_nrm, nrm)


def test_wire_size_is_12_bytes_per_point():
    xyz, _ = sample_box(2048)
    assert len(encode_points(xyz)) == 2048 * 12  # the 24 KB "sparse storage" of the thesis


@pytest.mark.parametrize("body,fmt,msg", [
    (b"", "xyz", "empty"),
    (b"\x00" * 13, "xyz", "multiple of 12"),
    (np.zeros((4, 3), "<f4").tobytes(), "xyz", "between"),
    (np.full((32, 3), np.nan, "<f4").tobytes(), "xyz", "NaN"),
    (np.zeros((32, 3), "<f4").tobytes(), "ply", "unknown format"),
])
def test_decode_rejects_bad_bodies(body, fmt, msg):
    with pytest.raises(ValueError, match=msg):
        decode_points(body, fmt)


# ----------------------------------------------------------------------------- mock backend
def test_mock_backend_returns_mesh_in_input_frame():
    xyz, _ = sample_box(512)
    xyz += np.array([3.0, -1.0, 2.0], np.float32)  # far from the origin: the mesh must follow the points
    rec = MockBackend().reconstruct(xyz)
    mesh = load_glb(export_glb(rec.mesh))
    assert len(mesh.faces) > 0
    np.testing.assert_allclose(mesh.bounds, np.stack([xyz.min(0), xyz.max(0)]), atol=1e-4)


def test_hull_handles_flat_input():
    flat = np.random.default_rng(0).random((64, 3)).astype(np.float32)
    flat[:, 2] = 0.5  # coplanar: qhull joggles it into a thin hull (or we fall back to a thin box)
    mesh = hull_mesh(flat)
    assert len(mesh.faces) > 0
    np.testing.assert_allclose(mesh.bounds[:, :2], np.stack([flat.min(0), flat.max(0)])[:, :2], atol=1e-3)


def test_hull_fallback_box(monkeypatch):
    def boom(self):
        raise RuntimeError("qhull failed")

    monkeypatch.setattr(trimesh.PointCloud, "convex_hull", property(boom))
    xyz, _ = sample_box(64)
    mesh = hull_mesh(xyz)
    assert len(mesh.faces) == 12  # axis-aligned bounding box
    np.testing.assert_allclose(mesh.bounds, np.stack([xyz.min(0), xyz.max(0)]), atol=1e-5)


# ----------------------------------------------------------------------------- HTTP
@pytest.fixture
def client(tmp_path):
    pytest.importorskip("httpx")
    from fastapi.testclient import TestClient

    from edge.server import create_app

    xyz, nrm = sample_box(256)
    (tmp_path / "box").mkdir()
    np.savetxt(tmp_path / "box" / "box_n256.xyz", xyz, fmt="%.6f")
    manifest = {"shapes": {"box": {"clouds": {"256": {"files": {"xyz": {"path": "box/box_n256.xyz"}}}}}}}
    (tmp_path / "manifest.json").write_text(json.dumps(manifest))
    app = create_app(MockBackend(), inputs_dir=tmp_path, log_csv=tmp_path / "log.csv", verbose=False)
    with TestClient(app) as c:
        c.tmp_path = tmp_path
        yield c


def test_health(client):
    r = client.get("/health")
    assert r.status_code == 200 and r.json()["backend"] == "mock"


def test_reconstruct_returns_glb_with_timing_headers(client):
    xyz, _ = sample_box(2048)
    r = client.post("/reconstruct?object_id=box", content=encode_points(xyz),
                    headers={"Content-Type": "application/octet-stream"})
    assert r.status_code == 200
    assert r.headers["content-type"] == "model/gltf-binary"
    assert r.content[:4] == b"glTF"  # glb magic
    for h in ("X-Server-Ms", "X-Queue-Ms", "X-Inference-Ms", "X-Export-Ms", "X-Triangles"):
        assert float(r.headers[h]) >= 0
    assert int(r.headers["X-Points-In"]) == 2048
    assert len(load_glb(r.content).faces) == int(r.headers["X-Triangles"])
    log = (client.tmp_path / "log.csv").read_text().splitlines()
    assert len(log) == 2 and log[1].split(",")[3] == "box"  # header + one row; 4th column = object_id


def test_reconstruct_bad_body_is_400(client):
    r = client.post("/reconstruct", content=b"\x00" * 13)
    assert r.status_code == 400 and "multiple of 12" in r.json()["detail"]


def test_stored_shapes(client):
    assert client.get("/shapes").json() == {"shapes": {"box": [256]}}
    pts = client.get("/shapes/box/points?points=256")
    assert pts.status_code == 200 and len(pts.content) == 256 * 12
    mesh = client.get("/shapes/box/mesh?points=256")
    assert mesh.status_code == 200 and mesh.content[:4] == b"glTF"
    assert client.get("/shapes/sphere/mesh").status_code == 404
    assert client.get("/shapes/box/mesh?points=999").status_code == 404


def test_log_records_the_client_address(client):
    xyz, _ = sample_box(256)
    client.post("/reconstruct?object_id=box", content=encode_points(xyz), headers={"User-Agent": "UnityPlayer/6000"})
    import csv as _csv

    rows = list(_csv.DictReader((client.tmp_path / "log.csv").open(encoding="utf-8")))
    assert rows[-1]["client"] == "testclient" and rows[-1]["user_agent"] == "UnityPlayer/6000"


def test_log_with_old_columns_is_renamed_aside(tmp_path):
    from edge.server import LOG_FIELDS, RequestLog

    path = tmp_path / "edge_requests.csv"
    path.write_text("timestamp,request_id\n2026-11-10T16:45:39,abc\n", encoding="utf-8")
    RequestLog(path).write({"timestamp": "2026-11-11T11:00:00", "request_id": "new"})
    assert path.read_text(encoding="utf-8").splitlines()[0] == ",".join(LOG_FIELDS)
    old = list(tmp_path.glob("edge_requests_old_*.csv"))
    assert len(old) == 1 and "abc" in old[0].read_text(encoding="utf-8")


def test_objects_option_refuses_other_objects(tmp_path):
    pytest.importorskip("httpx")
    from fastapi.testclient import TestClient

    from edge.server import create_app

    app = create_app(MockBackend(), inputs_dir=tmp_path, log_csv=tmp_path / "log.csv", verbose=False,
                     objects={"box_01"})
    xyz, _ = sample_box(256)
    with TestClient(app) as c:
        assert c.get("/health").json()["objects"] == ["box_01"]
        refused = c.post("/reconstruct?object_id=bunny_01", content=encode_points(xyz))
        assert refused.status_code == 409 and "not served" in refused.json()["detail"]
        assert c.post("/reconstruct?object_id=box_01", content=encode_points(xyz)).status_code == 200
    rows = (tmp_path / "log.csv").read_text().splitlines()
    assert len(rows) == 2 and ",box_01," in rows[1]  # refused requests are not logged


def test_accuracy_is_measured_and_meshes_saved(tmp_path):
    pytest.importorskip("httpx")
    import csv as _csv

    from fastapi.testclient import TestClient

    from edge.server import create_app

    xyz, _ = sample_box(2048)
    dense, _ = sample_box(20000, seed=1)  # the "original mesh" of box_01
    app = create_app(MockBackend(), inputs_dir=tmp_path, log_csv=tmp_path / "log.csv", verbose=False,
                     references={"box_01": dense}, save_meshes=tmp_path / "meshes")
    with TestClient(app) as c:
        r = c.post("/reconstruct?object_id=box_01", content=encode_points(xyz))
        assert r.status_code == 200
        assert float(r.headers["X-FScore"]) > 0.9  # the convex hull of a box is the box itself
        assert 0 <= float(r.headers["X-Chamfer-L2"]) < 1e-3
        assert float(r.headers["X-Quality-Ms"]) <= float(r.headers["X-Server-Ms"])
        assert r.headers["X-Model-Label"] == "convex-hull"
        other = c.post("/reconstruct?object_id=not_in_scene", content=encode_points(xyz))
        assert other.status_code == 200 and "X-FScore" not in other.headers  # no reference: no accuracy
    saved = sorted(f.name for f in (tmp_path / "meshes").glob("*.glb"))
    assert len(saved) == 2 and any(name.endswith("_box_01.glb") for name in saved)
    rows = list(_csv.DictReader((tmp_path / "log.csv").open(encoding="utf-8")))
    assert float(rows[0]["fscore"]) > 0.9 and rows[0]["mesh_file"].endswith("_box_01.glb")
    assert rows[1]["fscore"] == "" and rows[1]["quality_ms"] == ""


def test_health_gives_the_inputs_of_the_time_estimate(tmp_path):
    pytest.importorskip("httpx")
    from fastapi.testclient import TestClient

    from edge.server import create_app

    app = create_app(MockBackend(0.5), inputs_dir=tmp_path, log_csv=None, verbose=False)
    with TestClient(app) as c:
        h = c.get("/health").json()
        assert h["typical_s"] == 0.5 and h["budget_s"] == 0.0  # mock: its delay, until meshes are served
        xyz, _ = sample_box(256)
        c.post("/reconstruct?object_id=box", content=encode_points(xyz))
        assert 0.5 <= c.get("/health").json()["typical_s"] < 5  # now the median of the meshes served


def test_load_references_keeps_xyz(tmp_path):
    from edge.server import load_references

    np.save(tmp_path / "box_01_ref.npy", np.zeros((10, 6), dtype=np.float32))
    (tmp_path / "box_01.xyz").write_text("0 0 0\n")
    refs = load_references(tmp_path)
    assert list(refs) == ["box_01"] and refs["box_01"].shape == (10, 3)
    assert load_references(tmp_path / "missing") == {}


def test_requests_and_saved_meshes_can_be_fetched_from_another_pc(tmp_path):
    pytest.importorskip("httpx")
    import csv as _csv
    import time as _time

    from fastapi.testclient import TestClient

    from edge.server import create_app

    app = create_app(MockBackend(), inputs_dir=tmp_path, log_csv=None, verbose=False, save_meshes=tmp_path / "m")
    xyz, _ = sample_box(256)
    with TestClient(app) as c:
        before = c.get("/health").json()["server_time"]
        assert abs(before - _time.time()) < 5
        c.post("/reconstruct?object_id=box_01", content=encode_points(xyz))
        c.post("/reconstruct?object_id=sphere_01", content=encode_points(xyz))
        rows = list(_csv.DictReader(io.StringIO(c.get(f"/requests?since={before - 1}").text)))
        assert [r["object_id"] for r in rows] == ["box_01", "sphere_01"]
        assert all(float(r["t_epoch"]) >= before - 1 for r in rows)
        assert c.get(f"/requests?since={_time.time() + 60}").text.count("\n") == 1  # header only
        mesh = c.get(f"/meshes/{rows[0]['mesh_file']}")
        assert mesh.status_code == 200 and mesh.content[:4] == b"glTF"
        assert c.get("/meshes/missing.glb").status_code == 404
        assert c.get("/meshes/..%2F..%2Fsecret.glb").status_code == 404
    nothing_saved = create_app(MockBackend(), inputs_dir=tmp_path, log_csv=None, verbose=False)
    with TestClient(nothing_saved) as c:
        assert c.get("/meshes/a.glb").status_code == 404
